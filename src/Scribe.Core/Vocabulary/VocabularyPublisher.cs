using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;

namespace Scribe.Core.Vocabulary;

/// <summary>
/// Builds and publishes the <see cref="VocabularyGeneration"/> every dictation is admitted with (plan 3.8, R6): one
/// committed library snapshot (<see cref="ILibraryVocabularySource.Current"/>) plus the enabled personal dictionary,
/// compiled off the dispatcher and published in one write.
/// </summary>
/// <remarks>
/// <para>
/// A refresh is requested by a settings save or a stored-settings reapply (the dictation controller), by a new library
/// vocabulary (<see cref="ILibraryVocabularySource.Changed"/>), and by a dictionary change stored outside a save
/// (<see cref="ITextPostProcessor.Reloaded"/>: quick add, learning from history). Requests that arrive while a build
/// runs coalesce: one more build follows, reading its inputs after the newest request, and every request is answered by
/// the first generation built after it. A build that fails leaves the previous generation in use, so a generation is
/// never half-built, and nothing is ever mixed from two builds.
/// </para>
/// <para>
/// Each build takes a ticket, the number of requests made when it began, and publishes only when its ticket is newer
/// than the published generation's, so however two builds interleave the published generation reflects every request
/// up to its ticket. Logs carry counts and generation numbers only, never a term, a library or its name.
/// </para>
/// </remarks>
public sealed class VocabularyPublisher : IDisposable
{
    private readonly ILibraryVocabularySource _libraries;
    private readonly IDictionaryRepository _dictionary;
    private readonly ITextPostProcessor _postProcessor;
    private readonly ILogger<VocabularyPublisher> _log;
    private readonly Action<Action> _schedule;
    private readonly object _sync = new();
    private readonly List<(long Ticket, TaskCompletionSource<VocabularyGeneration> Done)> _waiting = [];

    private VocabularyGeneration _current = VocabularyGeneration.Empty;

    // Requests made, the newest request the published generation answers, and generation numbers. Guarded by _sync.
    private long _requested;
    private long _published;
    private long _number;
    private bool _building;
    private bool _started;
    private bool _disposed;

    public VocabularyPublisher(
        ILibraryVocabularySource libraries,
        IDictionaryRepository dictionary,
        ITextPostProcessor postProcessor,
        ILogger<VocabularyPublisher> log)
        : this(libraries, dictionary, postProcessor, log, static work => _ = Task.Run(work))
    {
    }

    /// <param name="schedule">Runs a build off the caller's thread; the thread pool in production, a test's queue in tests.</param>
    internal VocabularyPublisher(
        ILibraryVocabularySource libraries,
        IDictionaryRepository dictionary,
        ITextPostProcessor postProcessor,
        ILogger<VocabularyPublisher> log,
        Action<Action> schedule)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(postProcessor);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(schedule);
        _libraries = libraries;
        _dictionary = dictionary;
        _postProcessor = postProcessor;
        _log = log;
        _schedule = schedule;
    }

    /// <summary>
    /// The newest complete generation: a lock-free read, safe on the dictation path. <see cref="VocabularyGeneration.Empty"/>
    /// until <see cref="Start"/> has built the first.
    /// </summary>
    public VocabularyGeneration Current => Volatile.Read(ref _current);

    /// <summary>
    /// Raised after a new generation is published, on the thread that built it (usually the thread pool), outside any
    /// lock, through <see cref="ResilientEvent.InvokeAll{T}"/>. Subscribers must be quick and must never wait on the
    /// dispatcher.
    /// </summary>
    public event Action<VocabularyGeneration>? Published;

    /// <summary>
    /// Subscribes to the library vocabulary and to dictionary reloads, and builds and publishes the first generation on
    /// the calling thread, so the first dictation after startup never runs without its vocabulary. Idempotent in its
    /// subscriptions; each call builds.
    /// </summary>
    public VocabularyGeneration Start()
    {
        long ticket;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                _started = true;
                _libraries.Changed += OnLibrariesChanged;
                _postProcessor.Reloaded += OnDictionaryReloaded;
            }

            ticket = ++_requested;
        }

        BuildAndPublish(ticket);
        return Current;
    }

    /// <summary>
    /// Requests a new generation, built off the caller's thread. The task completes with the first generation that
    /// answers this request (or the one still in use, if that build failed or the publisher was disposed); it never
    /// faults.
    /// </summary>
    public Task<VocabularyGeneration> RefreshAsync()
    {
        var done = new TaskCompletionSource<VocabularyGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startBuilding = false;
        lock (_sync)
        {
            if (_disposed)
            {
                done.SetResult(Current);
                return done.Task;
            }

            _waiting.Add((++_requested, done));
            if (!_building)
            {
                _building = true;
                startBuilding = true;
            }
        }

        if (startBuilding)
        {
            try
            {
                _schedule(RunBuilds);
            }
            catch (Exception ex)
            {
                // A scheduler that refuses the work must not leave the requests waiting for a build that never runs.
                TryLog(log => log.LogWarning(
                    "A vocabulary generation build could not be scheduled ({Failure}).", FailureShape.Describe(ex)));
                lock (_sync)
                {
                    _building = false;
                    CompleteAllLocked();
                }
            }
        }

        return done.Task;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_started)
            {
                _libraries.Changed -= OnLibrariesChanged;
                _postProcessor.Reloaded -= OnDictionaryReloaded;
            }

            CompleteAllLocked();
        }
    }

    // A library save or recovery and a dictionary change stored outside a save each ask for a new generation; the
    // handlers only schedule, so the thread raising the event is never held up by a build.
    private void OnLibrariesChanged(long libraryGeneration) => _ = RefreshAsync();

    private void OnDictionaryReloaded(long reload) => _ = RefreshAsync();

    // One builder at a time for refreshes: it keeps building while requests arrive faster than it builds, and each build
    // answers every request made before it began.
    private void RunBuilds()
    {
        try
        {
            while (true)
            {
                long ticket;
                lock (_sync)
                {
                    if (_disposed || _published >= _requested)
                    {
                        _building = false;
                        CompleteAllLocked();
                        return;
                    }

                    ticket = _requested;
                }

                BuildAndPublish(ticket);
            }
        }
        catch (Exception ex)
        {
            // BuildAndPublish handles every failure it expects; anything here is a defect, and the requests waiting on
            // this builder are answered with the generation in use rather than left hanging.
            TryLog(log => log.LogError(
                "The vocabulary generation builder stopped unexpectedly: {Failure}", FailureShape.DescribeWithStack(ex)));
            lock (_sync)
            {
                _building = false;
                CompleteAllLocked();
            }
        }
    }

    private void BuildAndPublish(long ticket)
    {
        var started = Stopwatch.GetTimestamp();
        var inputs = TryRead();
        VocabularyGeneration? published = null;
        long keptNumber;
        lock (_sync)
        {
            if (inputs is { } read && ticket > _published && !_disposed)
            {
                published = new VocabularyGeneration(++_number, read.Dictionary, read.Libraries, read.Rules);
                Volatile.Write(ref _current, published);
            }

            // A failed build still answers the requests it was for, with the generation in use: retrying here could spin
            // on a dictionary that cannot be read, and the next request builds again anyway.
            _published = Math.Max(_published, ticket);
            CompleteAnsweredLocked();
            keptNumber = Current.Number;
        }

        if (published is null)
        {
            if (inputs is null)
            {
                TryLog(log => log.LogWarning(
                    "A vocabulary generation could not be built; dictation keeps generation {Number}.", keptNumber));
            }

            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        TryLog(log => log.LogInformation(
            "Vocabulary generation {Number} published: {DictionaryEntries} dictionary entr(ies) and {LibraryEntries} " +
            "library entr(ies) compiled into {Rules} rule(s); {AiEntries} library entr(ies) from {AiLibraries} " +
            "librar(ies) may go to AI cleanup (library generation {LibraryGeneration}); built in {ElapsedMs} ms.",
            published.Number,
            published.Dictionary.Count,
            published.Libraries.Entries.Count,
            published.Rules.Count,
            published.Libraries.AiEntries.Count,
            published.AiScope.PermittedLibraryIds.Count,
            published.Libraries.Generation,
            (long)elapsed.TotalMilliseconds));

        ResilientEvent.InvokeAll(Published, published, ex => TryLog(log => log.LogWarning(
            "A vocabulary generation subscriber failed ({Failure}).", FailureShape.DescribeWithStack(ex))));
    }

    // The library snapshot first, then the dictionary, then the rules compiled from exactly those two. Null when the
    // dictionary could not be read; a library vocabulary that cannot be read counts as none (fail closed for AI cleanup,
    // the personal dictionary alone for local rules, as the post-processor has always fallen back).
    private (IReadOnlyList<DictionaryEntry> Dictionary, LibraryVocabulary Libraries, CompiledDictionaryRules Rules)? TryRead()
    {
        LibraryVocabulary libraries;
        try
        {
            libraries = _libraries.Current ?? LibraryVocabulary.Empty;
        }
        catch (Exception ex)
        {
            TryLog(log => log.LogWarning(
                "The library vocabulary could not be read; this generation carries the personal dictionary only ({Failure}).",
                FailureShape.Describe(ex)));
            libraries = LibraryVocabulary.Empty;
        }

        try
        {
            var dictionary = _dictionary.GetEnabled();
            var rules = _postProcessor.Compile(dictionary, libraries.Entries);
            return (dictionary, libraries, rules);
        }
        catch (Exception ex)
        {
            TryLog(log => log.LogWarning(
                "The personal dictionary could not be read for a vocabulary generation ({Failure}).",
                FailureShape.Describe(ex)));
            return null;
        }
    }

    // Must be called under _sync.
    private void CompleteAnsweredLocked()
    {
        var current = Current;
        for (var i = _waiting.Count - 1; i >= 0; i--)
        {
            if (_waiting[i].Ticket <= _published)
            {
                _waiting[i].Done.TrySetResult(current);
                _waiting.RemoveAt(i);
            }
        }
    }

    // Must be called under _sync.
    private void CompleteAllLocked()
    {
        var current = Current;
        foreach (var (_, done) in _waiting)
        {
            done.TrySetResult(current);
        }

        _waiting.Clear();
    }

    // Logging must never be why a generation is lost or a request left waiting.
    private void TryLog(Action<ILogger> write)
    {
        try
        {
            write(_log);
        }
        catch
        {
            // Best effort, like every diagnostic here.
        }
    }
}
