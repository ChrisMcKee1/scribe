using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Libraries;
using Scribe.Core.Lifecycle;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;
using PublisherLog = Scribe.Core.Tests.CleanupLogging.CapturingLogger<Scribe.Core.Vocabulary.VocabularyPublisher>;
using ManualClock = Scribe.Core.Tests.Concurrency.ManualTimeProvider;
using ScriptedDictionary = Scribe.Core.Tests.Vocabulary.VocabularyPublisherTests.ScriptedDictionary;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// Every wait for a vocabulary generation is bounded, and shutdown ends it (round 3, A5). A build whose library or
/// dictionary read never returned used to hold its callers until the publisher was disposed: startup, before the tray and
/// the hotkey existed, with the single-instance mutex held and no way to quit; and after it a Settings save, the Usage
/// page's Add, quick add and learning from history. Each case holds a build's read on its own thread until the test
/// releases it, and fires the deadline on a manual clock, so nothing sleeps.
/// </summary>
public sealed class VocabularyPublicationDeadlineTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly LibraryVocabulary Libraries = Of(3, new Library("team", H1, true, Entry("kes trel", "Kestrel")));

    [Fact]
    public async Task A_refresh_whose_build_never_returns_is_answered_timed_out_at_its_deadline_while_the_read_stays_blocked()
    {
        var clock = new ManualClock();
        using var rig = new Rig(clock);
        var before = await rig.StartAsync();

        // The build for this request reads its libraries and does not return (a file another program holds, a stalled
        // disk): the save that asked for it would wait for as long as the read does.
        var hang = rig.HoldNextLibraryRead();
        rig.Dictionary.Entries = [Entry("quill moor", "Quillmoor")];
        var saving = rig.Publisher.RefreshAsync();
        await hang.Reading.WaitAsync(Bound);
        Assert.False(saving.IsCompleted);

        // Its deadline answers it at once, on the clock's tick, with the read still blocked: the caller goes on, told the
        // change is not in use yet, and dictation keeps the generation from before the change.
        var deadline = Assert.Single(clock.Timers, timer => !timer.IsDisposed);
        Assert.Equal(VocabularyPublisher.RefreshDeadline, deadline.DueTime);
        deadline.Fire();
        Assert.True(saving.IsCompleted, "The caller was not released by its deadline.");
        var answer = await saving;
        Assert.Equal(VocabularyRefreshOutcome.TimedOut, answer.Outcome);
        Assert.False(answer.Applied);
        Assert.Same(before, answer.Generation);
        Assert.Same(before, rig.Publisher.Current);
        Assert.Same(before, Admit(rig.Publisher));
        Assert.True(hang.StillHeld, "The read returned, so this did not show the deadline releasing the caller.");
        Assert.True(deadline.IsDisposed);

        // The log line is a shape: the deadline and the generation kept.
        var warning = Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(
            $"A vocabulary generation was not built within 15000 ms; its request is answered as not in use yet, dictation " +
            $"keeps generation {before.Number}, and the build goes on.",
            warning.Message);
        Assert.DoesNotContain("quill", rig.Log.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("team", rig.Log.AllText, StringComparison.OrdinalIgnoreCase);

        // Retired: a tick delivered again while the read is still blocked finds nothing to answer and logs nothing more.
        deadline.Fire();
        Assert.Same(answer, await saving);
        Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_build_that_returns_after_its_request_timed_out_still_publishes_for_later_dictations_and_that_answer_stands()
    {
        var clock = new ManualClock();
        using var rig = new Rig(clock);
        var before = await rig.StartAsync();

        var hang = rig.HoldNextLibraryRead();
        rig.Dictionary.Entries = [Entry("quill moor", "Quillmoor")];
        var abandoned = rig.Publisher.RefreshAsync();
        await hang.Reading.WaitAsync(Bound);
        var deadline = Assert.Single(clock.Timers, timer => !timer.IsDisposed);
        deadline.Fire();
        var timedOut = await abandoned;
        Assert.Equal(VocabularyRefreshOutcome.TimedOut, timedOut.Outcome);

        // The read returns long after the deadline. The build publishes what it read, in order, and a dictation admitted
        // from then on gets it: the change the save stored is in use after all.
        var published = new TaskCompletionSource<VocabularyGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Publisher.Published += generation => published.TrySetResult(generation);
        hang.Release();
        var late = await published.Task.WaitAsync(Bound);
        rig.JoinBuilders();

        Assert.Equal(before.Number + 1, late.Number);
        Assert.Equal(["quill moor"], late.Dictionary.Select(entry => entry.Pattern));
        Assert.Same(late, rig.Publisher.Current);
        Assert.Same(late, Admit(rig.Publisher));

        // The answer the caller was given stands: nothing turns the abandoned request into an acknowledgement, and a stale
        // tick of its deadline changes nothing either.
        Assert.Same(timedOut, await abandoned);
        Assert.Same(before, timedOut.Generation);
        deadline.Fire();
        Assert.Same(timedOut, await abandoned);
        Assert.Same(late, rig.Publisher.Current);
        Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.All(clock.Timers, timer => Assert.True(timer.IsDisposed));
    }

    [Fact]
    public async Task A_build_that_returns_late_never_answers_a_request_made_after_it_began_even_if_it_read_that_change()
    {
        // The late build took its ticket before the newer request existed, so it is not an answer to that request, whatever
        // it happened to read: only a build that began after a request may say the change is in use.
        var clock = new ManualClock();
        using var rig = new Rig(clock);
        var before = await rig.StartAsync();

        var hang = rig.HoldNextDictionaryRead();
        var abandoned = rig.Publisher.RefreshAsync();
        await hang.Reading.WaitAsync(Bound);
        Assert.Single(clock.Timers, timer => !timer.IsDisposed).Fire();
        Assert.Equal(VocabularyRefreshOutcome.TimedOut, (await abandoned).Outcome);

        // A newer request while the late build is still blocked, and the build after the late one held too, so the late
        // build's publication happens while the newer request still waits.
        var next = rig.HoldNextDictionaryRead();
        rig.Dictionary.Entries = [Entry("lan tern", "Lantern")];
        var newer = rig.Publisher.RefreshAsync();
        hang.Release();
        await next.Reading.WaitAsync(Bound);

        Assert.Equal(before.Number + 1, rig.Publisher.Current.Number);
        Assert.Equal(["lan tern"], rig.Publisher.Current.Dictionary.Select(entry => entry.Pattern));
        Assert.False(newer.IsCompleted, "A build that began before the request answered it.");

        next.Release();
        var answer = await newer.WaitAsync(Bound);
        Assert.Equal(VocabularyRefreshOutcome.Applied, answer.Outcome);
        Assert.Equal(before.Number + 2, answer.Generation.Number);
        Assert.Equal(["lan tern"], answer.Generation.Dictionary.Select(entry => entry.Pattern));
        Assert.Same(answer.Generation, rig.Publisher.Current);
    }

    [Fact]
    public async Task Disposing_while_a_build_hangs_answers_every_waiting_request_stopped_at_once_and_the_late_build_publishes_nothing()
    {
        var clock = new ManualClock();
        using var rig = new Rig(clock);
        var before = await rig.StartAsync();

        var hang = rig.HoldNextLibraryRead();
        var first = rig.Publisher.RefreshAsync();
        await hang.Reading.WaitAsync(Bound);
        var second = rig.Publisher.RefreshAsync();

        // Shutdown: both waiting requests are answered at once, before any deadline and with the read still blocked.
        rig.Publisher.Dispose();
        Assert.True(first.IsCompleted && second.IsCompleted, "Disposal waited for the build.");
        var firstAnswer = await first;
        Assert.Equal(VocabularyRefreshOutcome.Stopped, firstAnswer.Outcome);
        Assert.Equal(VocabularyRefreshOutcome.Stopped, (await second).Outcome);
        Assert.Same(before, firstAnswer.Generation);
        Assert.True(hang.StillHeld);
        Assert.All(clock.Timers, timer => Assert.True(timer.IsDisposed));

        // The build that returns afterwards publishes nothing and raises nothing.
        var published = 0;
        rig.Publisher.Published += _ => Interlocked.Increment(ref published);
        hang.Release();
        rig.JoinBuilders();
        Assert.Equal(0, Volatile.Read(ref published));
        Assert.Same(before, rig.Publisher.Current);
        Assert.DoesNotContain(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Startup_gives_up_with_a_timeout_when_the_first_generation_is_not_built_by_the_startup_deadline()
    {
        var clock = new ManualClock();
        using var rig = new Rig(clock);

        var hang = rig.HoldNextLibraryRead();
        var starting = rig.Publisher.StartAsync();
        await hang.Reading.WaitAsync(Bound);
        Assert.False(starting.IsCompleted);

        // The startup deadline, not a refresh's, and the start faults, which is what ends the app's start with its failure
        // notice and releases the single-instance mutex (App.OnStartup, AbandonStartup).
        var deadline = Assert.Single(clock.Timers);
        Assert.Equal(VocabularyPublisher.StartupDeadline, deadline.DueTime);
        deadline.Fire();
        var failure = await Assert.ThrowsAsync<TimeoutException>(() => starting.WaitAsync(Bound));
        Assert.Equal("The first vocabulary generation was not built within 30 seconds.", failure.Message);
        Assert.Same(VocabularyGeneration.Empty, rig.Publisher.Current);
        var warning = Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal("The first vocabulary generation was not built within 30000 ms; startup gives up on it.", warning.Message);
        Assert.True(hang.StillHeld);
    }

    [Fact]
    public async Task A_start_that_shutdown_interrupts_ends_stopped_and_an_unreadable_dictionary_is_no_startup_failure()
    {
        // Shutdown while the first build hangs: the start ends at once, with no deadline reached and no fault.
        var clock = new ManualClock();
        using (var interrupted = new Rig(clock))
        {
            var hang = interrupted.HoldNextLibraryRead();
            var starting = interrupted.Publisher.StartAsync();
            await hang.Reading.WaitAsync(Bound);
            interrupted.Publisher.Dispose();

            // Answered while the read is still blocked and before any deadline: disposal does not wait for the build.
            var stopped = await starting.WaitAsync(Bound);
            Assert.True(hang.StillHeld);
            Assert.Equal(VocabularyRefreshOutcome.Stopped, stopped.Outcome);
            Assert.Same(VocabularyGeneration.Empty, stopped.Generation);
            Assert.All(clock.Timers, timer => Assert.True(timer.IsDisposed));
            Assert.Empty(interrupted.Log.Entries);
        }

        // A first build that cannot read the dictionary does not fault the start: it answers not applied, and dictation runs
        // without vocabulary until the next change builds one.
        var unreadableClock = new ManualClock();
        using var unreadable = new Rig(unreadableClock);
        unreadable.Dictionary.Failure = new InvalidOperationException("database is locked");
        var answer = await unreadable.Publisher.StartAsync().WaitAsync(Bound);
        Assert.Equal(VocabularyRefreshOutcome.NotApplied, answer.Outcome);
        Assert.Same(VocabularyGeneration.Empty, unreadable.Publisher.Current);
        Assert.All(unreadableClock.Timers, timer => Assert.True(timer.IsDisposed));
    }

    [Fact]
    public async Task A_library_change_or_a_dictionary_reload_during_a_hang_arms_no_deadline_and_is_built_once_the_read_returns()
    {
        // Nobody awaits the two events' requests, so they carry no answer and no deadline: no timer, no warning.
        var clock = new ManualClock();
        using var rig = new Rig(clock);
        await rig.StartAsync();

        var hang = rig.HoldNextLibraryRead();
        var saving = rig.Publisher.RefreshAsync();
        await hang.Reading.WaitAsync(Bound);
        var timers = clock.Timers.Count;

        var newer = Of(4, new Library("team", H2, true, Entry("kes trel", "KESTREL")));
        rig.Source.Publish(newer);
        rig.Processor.Reload();
        Assert.Equal(timers, clock.Timers.Count);

        // The one deadline is the save's, and the one warning is for it.
        Assert.Single(clock.Timers, timer => !timer.IsDisposed).Fire();
        Assert.Equal(VocabularyRefreshOutcome.TimedOut, (await saving).Outcome);
        Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);

        // Once the read returns, the builder goes on to a build that began after both events, so the new library vocabulary
        // reaches dictation.
        hang.Release();
        rig.JoinBuilders();
        Assert.Same(newer, rig.Publisher.Current.Libraries);
        Assert.Single(rig.Log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.All(clock.Timers, timer => Assert.True(timer.IsDisposed));
    }

    [Fact]
    public void The_deadlines_leave_room_for_a_healthy_build_and_the_production_publisher_keeps_them_on_the_system_clock()
    {
        // Thirty seconds for the first generation: a cold library read of a large vocabulary on a slow disk, the dictionary
        // read, which waits at most SQLite's busy timeout for a lock, and the compile. Fifteen for a later one, the library
        // source warm by then.
        Assert.Equal(TimeSpan.FromSeconds(30), VocabularyPublisher.StartupDeadline);
        Assert.Equal(TimeSpan.FromSeconds(15), VocabularyPublisher.RefreshDeadline);
        Assert.True(VocabularyPublisher.RefreshDeadline > TimeSpan.FromMilliseconds(ScribeDatabase.BusyTimeoutMs));
        Assert.True(VocabularyPublisher.StartupDeadline > VocabularyPublisher.RefreshDeadline);

        var source = Read("src", "Scribe.Core", "Vocabulary", "VocabularyPublisher.cs");
        Assert.Contains(": this(libraries, dictionary, postProcessor, log, static work => _ = Task.Run(work))", source, StringComparison.Ordinal);
        Assert.Contains("_time = time ?? TimeProvider.System;", source, StringComparison.Ordinal);
        Assert.Contains("return FirstGenerationAsync(Request(StartupDeadline, first: true));", source, StringComparison.Ordinal);
        Assert.Contains("public Task<VocabularyRefresh> RefreshAsync() => Request(RefreshDeadline, first: false);", source, StringComparison.Ordinal);
    }

    // A recording admitted the way the dictation controller admits one: its capture context takes the publisher's current
    // generation inside the lifecycle's admission.
    private static VocabularyGeneration Admit(VocabularyPublisher publisher)
    {
        var lifecycle = new DictationLifecycle<VocabularyGeneration>(() => { }, () => { });
        var activation = lifecycle.TryBeginRecording(() => publisher.Current);
        Assert.Equal(ActivationDecision.Started, activation.Decision);
        return activation.Capture!;
    }

    private static string Read(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root.FullName, .. parts]));
    }

    // A publisher over a test source and dictionary whose builds each run on a thread of their own, as a pool thread would,
    // so a build held on its read blocks nothing else, with a manual clock behind the deadlines.
    private sealed class Rig : IDisposable
    {
        private readonly ConcurrentQueue<Thread> _builders = new();
        private readonly ConcurrentQueue<Hang> _hangs = new();

        public Rig(ManualClock clock)
        {
            Source = new TestVocabularySource(Libraries);
            Dictionary = new ScriptedDictionary([]);
            Processor = new TextPostProcessor(Dictionary, NullLogger<TextPostProcessor>.Instance);
            Publisher = new VocabularyPublisher(
                Source,
                Dictionary,
                Processor,
                Log,
                work =>
                {
                    var builder = new Thread(() => work()) { IsBackground = true };
                    _builders.Enqueue(builder);
                    builder.Start();
                },
                clock);
        }

        public TestVocabularySource Source { get; }

        public ScriptedDictionary Dictionary { get; }

        public TextPostProcessor Processor { get; }

        public PublisherLog Log { get; } = new();

        public VocabularyPublisher Publisher { get; }

        public async Task<VocabularyGeneration> StartAsync()
        {
            var answer = await Publisher.StartAsync().WaitAsync(Bound);
            Assert.Equal(VocabularyRefreshOutcome.Applied, answer.Outcome);
            return answer.Generation;
        }

        public Hang HoldNextLibraryRead()
        {
            var hang = Track(new Hang());
            Source.ReadingCurrent = () =>
            {
                Source.ReadingCurrent = null;
                hang.Hold();
            };
            return hang;
        }

        public Hang HoldNextDictionaryRead()
        {
            var hang = Track(new Hang());
            Dictionary.Reading = () =>
            {
                Dictionary.Reading = null;
                hang.Hold();
            };
            return hang;
        }

        // Waits, with the safety bound, until every builder started so far has returned.
        public void JoinBuilders()
        {
            foreach (var builder in _builders)
            {
                Assert.True(builder.Join(Bound), "A build did not return.");
            }
        }

        public void Dispose()
        {
            Publisher.Dispose();
            foreach (var hang in _hangs)
            {
                hang.Release();
            }
        }

        private Hang Track(Hang hang)
        {
            _hangs.Enqueue(hang);
            return hang;
        }
    }

    // A read held on the build's thread until the test releases it, and only then (review round 4 of stream TR, A5): tests
    // assert it is still held (StillHeld), which a hold that ended by itself would leave saying so after the read returned.
    // The rig releases every hold it made when it is disposed, so a test that fails first still ends.
    private sealed class Hang
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();

        public Task Reading => _reading.Task;

        public bool StillHeld => !_release.IsSet;

        public void Hold()
        {
            _reading.TrySetResult();
            _release.Wait();
        }

        public void Release() => _release.Set();
    }
}
