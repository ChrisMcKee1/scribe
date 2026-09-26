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
/// A refresh is requested by everything that stores vocabulary, after it stored it: a settings save or a stored-settings
/// reapply, and quick add and learning from history (the dictation controller); a new library vocabulary
/// (<see cref="ILibraryVocabularySource.Changed"/>); and a reload of the post-processor
/// (<see cref="ITextPostProcessor.Reloaded"/>). One builder runs at a time, off the caller's thread, the first generation
/// included (<see cref="StartAsync"/>). Requests that arrive while a build runs coalesce: one more build follows, reading
/// its inputs after the newest request, and every request a caller awaits is answered by the first build that began after
/// it (or by its deadline, below), with <see cref="VocabularyRefreshOutcome.Applied"/> once that build's generation is
/// published, so a caller that awaits the answer before saying a change is in effect says it only when the next dictation
/// is admitted with it. A build that fails leaves the previous generation in use and answers
/// <see cref="VocabularyRefreshOutcome.NotApplied"/>, so a generation is never half-built. The two events ask for a build
/// and await nothing.
/// </para>
/// <para>
/// A build reads the library snapshot and then the personal dictionary, and compiles exactly those two. A commit of both
/// that lands between the two reads (a Settings save of libraries and dictionary together) can give that one build the
/// old libraries and the new dictionary. The mix is transient and harmless: every change of either source asks for a
/// build after it committed, so the build after it reads both new; each generation is still immutable and whole, and each
/// dictation uses the one it was admitted with; and its glossary and the scope that judges it come from the same library
/// snapshot, so AI cleanup stays bound to the content that snapshot permitted (the library's new content refuses it).
/// </para>
/// <para>
/// Every wait is bounded, and shutdown ends it. Startup waits at most <see cref="StartupDeadline"/> for the first
/// generation (<see cref="StartAsync"/> then faults, and the app ends startup with its failure notice), and every later
/// request is answered by <see cref="RefreshDeadline"/> at the latest (<see cref="VocabularyRefreshOutcome.TimedOut"/>), so
/// no caller is held by a library or dictionary read that does not return. That read is synchronous I/O on the build's
/// thread and cannot be abandoned, so nothing waits for it: a request its deadline answered is retired, and the build is
/// left to finish. If it returns it publishes in order (one builder at a time, and tickets), so a later dictation gets
/// what it read, and it answers only the requests still waiting. Disposal answers every waiting request at once
/// (<see cref="VocabularyRefreshOutcome.Stopped"/>) without waiting for a build, and a build that returns after it
/// publishes nothing.
/// </para>
/// <para>
/// Each build takes a ticket, the number of requests made when it began, before it reads anything. Logs carry counts and
/// generation numbers only, never a term, a library or its name.
/// </para>
/// </remarks>
public sealed class VocabularyPublisher : IDisposable
{
    /// <summary>
    /// How long startup waits for the first generation before giving up on it: thirty seconds, after which
    /// <see cref="StartAsync"/> faults with a <see cref="TimeoutException"/>. A healthy first build takes well under a second;
    /// the slowest it gets is the library source's cold first read of a large vocabulary on a slow or busy disk (after the
    /// integration a catalog load, with any recovery it runs), then the dictionary read, which waits at most SQLite's
    /// ten-second busy timeout for a lock, then the compile. Thirty seconds covers all three with room to spare, and still
    /// ends a read that never returns, which would otherwise hold the single-instance mutex with no tray, no hotkey and no
    /// way to quit.
    /// </summary>
    public static TimeSpan StartupDeadline { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a later request waits before it is answered as not in use yet
    /// (<see cref="VocabularyRefreshOutcome.TimedOut"/>): fifteen seconds. The library source is warm by then, so a healthy
    /// build is the dictionary read, which waits at most SQLite's ten-second busy timeout for a lock, and the compile.
    /// Fifteen seconds covers both and still releases a Settings save, the Usage page's Add, quick add and learning from
    /// history from a build that never returns.
    /// </summary>
    public static TimeSpan RefreshDeadline { get; } = TimeSpan.FromSeconds(15);

    private readonly ILibraryVocabularySource _libraries;
    private readonly IDictionaryRepository _dictionary;
    private readonly ITextPostProcessor _postProcessor;
    private readonly ILogger<VocabularyPublisher> _log;
    private readonly Action<Action> _schedule;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly List<Waiter> _waiting = [];

    private VocabularyGeneration _current = VocabularyGeneration.Empty;

    // Requests made, the newest request a build has answered (published or not), the request the published generation
    // was built for, and generation numbers. Guarded by _sync.
    private long _requested;
    private long _answered;
    private long _appliedTicket;
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
    /// <param name="time">The clock behind the deadlines; the system clock by default, a test's manual clock in tests.</param>
    internal VocabularyPublisher(
        ILibraryVocabularySource libraries,
        IDictionaryRepository dictionary,
        ITextPostProcessor postProcessor,
        ILogger<VocabularyPublisher> log,
        Action<Action> schedule,
        TimeProvider? time = null)
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
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The newest complete generation: a lock-free read, safe on the dictation path. <see cref="VocabularyGeneration.Empty"/>
    /// until the build <see cref="StartAsync"/> asks for has published the first.
    /// </summary>
    public VocabularyGeneration Current => Volatile.Read(ref _current);

    /// <summary>
    /// Raised after a new generation is published, on the thread that built it (usually the thread pool), outside any
    /// lock, through <see cref="ResilientEvent.InvokeAll{T}"/>. Subscribers must be quick and must never wait on the
    /// dispatcher.
    /// </summary>
    public event Action<VocabularyGeneration>? Published;

    /// <summary>
    /// Subscribes to the library vocabulary and to dictionary reloads, and asks for the first generation, built off the
    /// caller's thread like every other: the library source's first read can load a cold catalog, which must never run on
    /// the dispatcher. The app awaits the answer before the hotkey is installed, so the first dictation after startup never
    /// runs without its vocabulary, and nothing waits for it synchronously. The task completes with the first build's
    /// answer (<see cref="VocabularyRefreshOutcome.NotApplied"/> when it could not read the dictionary, which is no reason
    /// to stop startup; <see cref="VocabularyRefreshOutcome.Stopped"/> when shutdown began first), and faults with a
    /// <see cref="TimeoutException"/> when that build has not returned by <see cref="StartupDeadline"/>. Idempotent in its
    /// subscriptions; each call asks for a build. Throws <see cref="ObjectDisposedException"/> once disposed.
    /// </summary>
    public Task<VocabularyRefresh> StartAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                _started = true;
                _libraries.Changed += OnLibrariesChanged;
                _postProcessor.Reloaded += OnDictionaryReloaded;
            }
        }

        return FirstGenerationAsync(Request(StartupDeadline, first: true));
    }

    /// <summary>
    /// Asks for a new generation, built off the caller's thread from inputs read after this call. The task completes once a
    /// build answers the request: <see cref="VocabularyRefreshOutcome.Applied"/> with the generation it published, which is
    /// what a dictation admitted from then on is given; <see cref="VocabularyRefreshOutcome.NotApplied"/> with the
    /// generation kept, when that build could not read the dictionary; <see cref="VocabularyRefreshOutcome.TimedOut"/> with
    /// the generation kept, when no build has answered it by <see cref="RefreshDeadline"/>; or
    /// <see cref="VocabularyRefreshOutcome.Stopped"/> once the publisher is disposed. It never faults. Whoever reports a
    /// stored change as in effect awaits it first, and never waits on it synchronously.
    /// </summary>
    public Task<VocabularyRefresh> RefreshAsync() => Request(RefreshDeadline, first: false);

    // The first generation's answer, or a TimeoutException once its deadline answered it: startup then ends with its failure
    // notice rather than hold the single-instance mutex with nothing on screen and no way to quit.
    private static async Task<VocabularyRefresh> FirstGenerationAsync(Task<VocabularyRefresh> answer)
    {
        var first = await answer.ConfigureAwait(false);
        return first.Outcome == VocabularyRefreshOutcome.TimedOut
            ? throw new TimeoutException(
                $"The first vocabulary generation was not built within {StartupDeadline.TotalSeconds:0} seconds.")
            : first;
    }

    private Task<VocabularyRefresh> Request(TimeSpan deadline, bool first)
    {
        var done = new TaskCompletionSource<VocabularyRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool startBuilding;
        lock (_sync)
        {
            if (_disposed)
            {
                done.SetResult(new VocabularyRefresh(VocabularyRefreshOutcome.Stopped, Current));
                return done.Task;
            }

            var waiter = new Waiter(++_requested, first, done);
            _waiting.Add(waiter);

            // Armed under the lock, so a build that answers the request first finds the timer to dispose, and a deadline that
            // fires first finds the request still waiting.
            waiter.Deadline = _time.CreateTimer(_ => OnDeadline(waiter, deadline), null, deadline, Timeout.InfiniteTimeSpan);
            startBuilding = ClaimBuilderLocked();
        }

        if (startBuilding)
        {
            StartBuilder();
        }

        return done.Task;
    }

    // A request nobody awaits (a new library vocabulary, a post-processor reload): one more build, with no answer to give
    // and so no deadline to keep.
    private void RequestBuild()
    {
        bool startBuilding;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ++_requested;
            startBuilding = ClaimBuilderLocked();
        }

        if (startBuilding)
        {
            StartBuilder();
        }
    }

    // Must be called under _sync. True when the caller is to start the one builder.
    private bool ClaimBuilderLocked()
    {
        if (_building)
        {
            return false;
        }

        _building = true;
        return true;
    }

    private void StartBuilder()
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
                CompleteAllLocked(VocabularyRefreshOutcome.NotApplied);
            }
        }
    }

    // A request no build answered by its deadline is answered as not in use yet and retired, so its caller goes on
    // without the read that has not returned. The build is left to finish: if it returns it publishes in order, and
    // answers only the requests still waiting then.
    private void OnDeadline(Waiter waiter, TimeSpan deadline)
    {
        bool retired;
        long kept;
        lock (_sync)
        {
            retired = _waiting.Remove(waiter);
            if (retired)
            {
                waiter.Answer(new VocabularyRefresh(VocabularyRefreshOutcome.TimedOut, Current));
            }

            kept = Current.Number;
        }

        if (!retired)
        {
            return;
        }

        if (waiter.First)
        {
            TryLog(log => log.LogWarning(
                "The first vocabulary generation was not built within {DeadlineMs} ms; startup gives up on it.",
                (long)deadline.TotalMilliseconds));
            return;
        }

        TryLog(log => log.LogWarning(
            "A vocabulary generation was not built within {DeadlineMs} ms; its request is answered as not in use yet, " +
            "dictation keeps generation {Number}, and the build goes on.",
            (long)deadline.TotalMilliseconds,
            kept));
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

            CompleteAllLocked(VocabularyRefreshOutcome.Stopped);
        }
    }

    // A library save or recovery and a post-processor reload each ask for a new generation; the handlers only schedule,
    // so the thread raising the event is never held up by a build.
    private void OnLibrariesChanged(long libraryGeneration) => RequestBuild();

    private void OnDictionaryReloaded(long reload) => RequestBuild();

    // One builder at a time: it keeps building while requests arrive faster than it builds, and each build answers every
    // request still waiting that was made before it began (a deadline may have answered some already).
    private void RunBuilds()
    {
        try
        {
            while (true)
            {
                long ticket;
                lock (_sync)
                {
                    if (_disposed || _answered >= _requested)
                    {
                        _building = false;
                        CompleteAllLocked(_disposed ? VocabularyRefreshOutcome.Stopped : VocabularyRefreshOutcome.NotApplied);
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
                CompleteAllLocked(VocabularyRefreshOutcome.NotApplied);
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
            // One builder at a time, so a build's ticket is always newer than the newest request answered; the check keeps
            // an older build from ever publishing over a newer generation should that change.
            if (inputs is { } read && ticket > _answered && !_disposed)
            {
                published = new VocabularyGeneration(++_number, read.Dictionary, read.Libraries, read.Rules);
                Volatile.Write(ref _current, published);
                _appliedTicket = ticket;
            }

            // A failed build still answers the requests it was for, as not applied: retrying here could spin on a dictionary
            // that cannot be read, and the next request builds again anyway.
            _answered = Math.Max(_answered, ticket);
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

    // The library snapshot first, then the dictionary, then the rules compiled from exactly those two (a commit of both
    // between the two reads makes this one build a transient mix, see the class remarks). Null when the dictionary could
    // not be read; a library vocabulary that cannot be read counts as none (fail closed for AI cleanup, the personal
    // dictionary alone for local rules, as the post-processor has always fallen back).
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

    // Must be called under _sync. A request is answered by the first build that took its ticket after it, and applied when
    // the generation in use was built for it or a later request, so from inputs read after it was made. A request its
    // deadline already answered is no longer here, so a build that finishes late answers only the requests still waiting.
    private void CompleteAnsweredLocked()
    {
        var current = Current;
        for (var i = _waiting.Count - 1; i >= 0; i--)
        {
            var waiter = _waiting[i];
            if (waiter.Ticket <= _answered)
            {
                waiter.Answer(new VocabularyRefresh(
                    _appliedTicket >= waiter.Ticket ? VocabularyRefreshOutcome.Applied : VocabularyRefreshOutcome.NotApplied,
                    current));
                _waiting.RemoveAt(i);
            }
        }
    }

    // Must be called under _sync. Every request still waiting is newer than the generation in use, so none is applied.
    private void CompleteAllLocked(VocabularyRefreshOutcome outcome)
    {
        var answer = new VocabularyRefresh(outcome, Current);
        foreach (var waiter in _waiting)
        {
            waiter.Answer(answer);
        }

        _waiting.Clear();
    }

    // One request: its ticket, whether it is the first generation's, its answer, and the timer that answers it at its
    // deadline if no build has by then.
    private sealed class Waiter(long ticket, bool first, TaskCompletionSource<VocabularyRefresh> done)
    {
        public long Ticket { get; } = ticket;

        public bool First { get; } = first;

        // Set under _sync right after the waiter is listed, before anything can answer it.
        public ITimer? Deadline { get; set; }

        // Must be called under _sync, once, as the waiter leaves the list. Stops the deadline (a tick already running then
        // finds the waiter gone), and completes the answer, whose continuations never run under the lock.
        public void Answer(VocabularyRefresh answer)
        {
            Deadline?.Dispose();
            done.TrySetResult(answer);
        }
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
