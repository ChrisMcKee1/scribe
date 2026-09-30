using System.Runtime;
using Scribe.Core.Diagnostics;
using Scribe.Core.Lifecycle;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// The idle release's one collection: Aggressive by default from 0.5.1, and 0.5.0's Forced collection under
/// PerfFlags.ForcedIdleGc, which selects the mode and nothing else. Every collection here goes through a recording fake,
/// so no test changes the test host's GC (the large object heap mode and the collection are both process-wide). The
/// release is driven through the real lifecycle and IdleModelRelease, in both flag states, for each trigger (the idle
/// countdown, and a pause made while nothing records) and for each ordering the release documents, including the race it
/// does not seal: a recording that starts after the last check still meets the collection.
/// </summary>
public sealed class IdleCollectionTests
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(10);

    [Fact]
    public void The_forced_mode_is_exactly_the_collection_0_5_0_made()
    {
        var gc = new FakeCollector();

        var result = IdleCollection.Compact(aggressive: false, gc);

        Assert.Equal(["loh:CompactOnce", "collect:2:Forced:blocking:compacting"], gc.Calls);
        Assert.Equal(IdleCollectionMode.Forced, result.Mode);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void The_default_is_one_aggressive_blocking_compacting_collection_of_the_oldest_generation(int maxGeneration)
    {
        var gc = new FakeCollector { MaxGeneration = maxGeneration };

        var result = IdleCollection.Compact(aggressive: true, gc);

        Assert.Equal(["loh:CompactOnce", $"collect:{maxGeneration}:Aggressive:blocking:compacting"], gc.Calls);
        Assert.Equal(IdleCollectionMode.Aggressive, result.Mode);
    }

    [Theory]
    [InlineData(null, IdleCollectionMode.Aggressive)]
    [InlineData("", IdleCollectionMode.Aggressive)]
    [InlineData("MatcherSpans", IdleCollectionMode.Aggressive)]
    [InlineData("ForcedIdleGc", IdleCollectionMode.Forced)]
    [InlineData("forcedidlegc,MatcherSpans", IdleCollectionMode.Forced)]
    // The name the flag had while it was being tested is unknown now, so it reaches the new default like no flag at all.
    [InlineData("AggressiveIdleGc", IdleCollectionMode.Aggressive)]
    public void The_flag_alone_selects_the_mode(string? variable, IdleCollectionMode expected)
    {
        var flags = PerfFlags.Parse(variable);
        var gc = new FakeCollector();

        var result = IdleCollection.Compact(flags, gc);

        Assert.Equal(expected, result.Mode);
        Assert.Equal(expected == IdleCollectionMode.Forced, flags.IsOn(PerfFlags.ForcedIdleGc));
        Assert.Single(gc.Calls, call => call.StartsWith("collect:", StringComparison.Ordinal));
        Assert.Equal("loh:CompactOnce", gc.Calls[0]);
    }

    [Fact]
    public void No_flag_is_the_new_default_and_only_the_forced_name_is_known()
    {
        var gc = new FakeCollector();

        Assert.Equal(IdleCollectionMode.Aggressive, IdleCollection.Compact(PerfFlags.None, gc).Mode);
        Assert.False(PerfFlags.None.IsOn(PerfFlags.ForcedIdleGc));
        Assert.Contains(PerfFlags.ForcedIdleGc, PerfFlags.Known);
        Assert.DoesNotContain("AggressiveIdleGc", PerfFlags.Known);
        Assert.Equal(1, PerfFlags.Parse("AggressiveIdleGc").UnknownCount);
    }

    [Fact]
    public void The_result_carries_the_collections_numbers_and_nothing_else()
    {
        var gc = new FakeCollector
        {
            CommittedBefore = 263_900_000,
            CommittedAfter = 47_000_000,
            HeapAfter = 44_600_000,
            PauseBefore = TimeSpan.FromMilliseconds(100),
            PauseAfter = TimeSpan.FromMilliseconds(134.5),
            ElapsedValue = TimeSpan.FromMilliseconds(35),
        };

        var result = IdleCollection.Compact(aggressive: true, gc);

        Assert.Equal(263_900_000, result.CommittedBeforeBytes);
        Assert.Equal(47_000_000, result.CommittedAfterBytes);
        Assert.Equal(44_600_000, result.HeapAfterBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(34.5), result.Pause);
        Assert.Equal(TimeSpan.FromMilliseconds(35), result.Elapsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_idle_deadline_collects_once_in_the_selected_mode(bool forced)
    {
        var release = new Release(forced);
        release.Lifecycle.Start(IdleDelay);

        release.Clock.Advance(IdleDelay);
        release.IdleTimer.Fire();

        Assert.Equal(IdleReleaseOutcome.Released, release.LastOutcome);
        Assert.Equal([Mode(forced)], release.Gc.Collections);
        Assert.Equal(["unload", "release buffers", "compact", "announce"], release.Steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pause_while_nothing_records_asks_for_the_release_and_collects_once(bool forced)
    {
        var release = new Release(forced);
        release.Lifecycle.Start(IdleDelay);

        var change = release.Lifecycle.SetPaused(true);

        Assert.True(IdleReleaseRequests.OnPauseChange(paused: true, change));
        release.RunPauseRequested();
        Assert.Equal(IdleReleaseOutcome.Released, release.LastOutcome);
        Assert.Equal([Mode(forced)], release.Gc.Collections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pause_still_asks_for_the_release_when_the_idle_countdown_is_off(bool forced)
    {
        var release = new Release(forced);
        release.Lifecycle.Start(Timeout.InfiniteTimeSpan);

        // The countdown is off: however long the loop idles, its timer releases nothing.
        release.Clock.Advance(TimeSpan.FromHours(8));
        release.IdleTimer.Fire();
        Assert.Empty(release.Gc.Collections);

        var change = release.Lifecycle.SetPaused(true);
        Assert.True(IdleReleaseRequests.OnPauseChange(paused: true, change));
        release.RunPauseRequested();

        Assert.Equal([Mode(forced)], release.Gc.Collections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pause_that_stops_a_recording_asks_for_no_release(bool forced)
    {
        var release = new Release(forced);
        release.Lifecycle.Start(IdleDelay);
        release.Lifecycle.TryBeginRecording(() => new Capture("live"));

        var change = release.Lifecycle.SetPaused(true);

        Assert.True(change.WasRecording);
        Assert.False(IdleReleaseRequests.OnPauseChange(paused: true, change));
        Assert.Empty(release.Gc.Collections);
    }

    [Fact]
    public void A_resume_or_an_unchanged_pause_asks_for_no_release()
    {
        var release = new Release(forced: false);
        release.Lifecycle.Start(IdleDelay);
        release.Lifecycle.SetPaused(true);

        Assert.False(IdleReleaseRequests.OnPauseChange(paused: true, release.Lifecycle.SetPaused(true))); // no change
        Assert.False(IdleReleaseRequests.OnPauseChange(paused: false, release.Lifecycle.SetPaused(false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pause_during_processing_is_refused_and_collects_nothing(bool forced)
    {
        var release = new Release(forced);
        release.Lifecycle.Start(IdleDelay);
        release.Lifecycle.TryBeginRecording(() => new Capture("a"));
        var stop = release.Lifecycle.TryBeginProcessing();

        var change = release.Lifecycle.SetPaused(true);
        Assert.True(IdleReleaseRequests.OnPauseChange(paused: true, change)); // asked for; the lifecycle decides
        release.RunPauseRequested();

        Assert.Equal(IdleReleaseOutcome.NotIdle, release.LastOutcome);
        Assert.Empty(release.Gc.Collections);
        Assert.Empty(release.Steps);
        Assert.NotNull(stop.Admission);
    }

    /// <summary>
    /// A pause made while a dictation records or processes owes the release: the lifecycle hands it back when that
    /// dictation has ended, and it runs then, once, whether or not the idle countdown is on. Before 0.5.2 it was refused and
    /// forgotten, so with the countdown off ("Never") a pause mid-dictation kept every model loaded.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_pause_made_mid_dictation_releases_once_that_dictation_has_ended(bool whileRecording, bool countdownOn)
    {
        var release = new Release(forced: false);
        release.Lifecycle.Start(countdownOn ? IdleDelay : Timeout.InfiniteTimeSpan);
        release.Lifecycle.TryBeginRecording(() => new Capture("a"));
        ProcessingAdmission<Capture>? admission = null;
        if (!whileRecording)
        {
            admission = release.Lifecycle.TryBeginProcessing().Admission;
        }

        var change = release.Lifecycle.SetPaused(true);
        Assert.Equal(!whileRecording, IdleReleaseRequests.OnPauseChange(paused: true, change));
        if (!whileRecording)
        {
            release.RunPauseRequested(); // the immediate request, refused while processing
            Assert.Equal(IdleReleaseOutcome.NotIdle, release.LastOutcome);
        }

        admission ??= release.Lifecycle.TryBeginProcessing().Admission; // the pause's own stop
        release.Lifecycle.ReturnToIdle(countdownOn ? IdleDelay : Timeout.InfiniteTimeSpan);
        release.RunPauseRequested(); // still finishing
        Assert.Equal(IdleReleaseOutcome.NotIdle, release.LastOutcome);

        Assert.Equal(IdleReleaseTrigger.PauseRequest, release.Lifecycle.EndProcessing(admission!));
        release.RunPauseRequested();

        Assert.Equal(IdleReleaseOutcome.Released, release.LastOutcome);
        Assert.Equal(["Aggressive"], release.Gc.Collections);

        // Owed once: the countdown (when on) was spent with it, so its deadline releases nothing more.
        Assert.Null(release.Lifecycle.EndProcessing(admission!));
        release.RunRequested();
        Assert.Equal(IdleReleaseOutcome.NotIdle, release.LastOutcome);
        Assert.Single(release.Gc.Collections);
    }

    [Fact]
    public void A_resume_withdraws_the_release_a_pause_owed_and_a_pause_request_that_runs_after_it_releases_nothing()
    {
        var release = new Release(forced: false);
        release.Lifecycle.Start(IdleDelay);
        release.Lifecycle.TryBeginRecording(() => new Capture("a"));
        release.Lifecycle.SetPaused(true);
        var stop = release.Lifecycle.TryBeginProcessing();
        release.Lifecycle.SetPaused(false);
        release.Lifecycle.ReturnToIdle(IdleDelay);

        Assert.Null(release.Lifecycle.EndProcessing(stop.Admission!));

        // Paused and resumed again before the request it made ran.
        release.Lifecycle.SetPaused(true);
        release.Lifecycle.SetPaused(false);
        release.RunPauseRequested();

        Assert.Equal(IdleReleaseOutcome.NotIdle, release.LastOutcome);
        Assert.Empty(release.Gc.Collections);
    }

    [Fact]
    public void A_second_request_for_the_same_pause_releases_nothing()
    {
        var release = new Release(forced: false);
        release.Lifecycle.Start(IdleDelay);
        release.Lifecycle.SetPaused(true);

        release.RunPauseRequested();
        Assert.Equal(IdleReleaseOutcome.Released, release.LastOutcome);

        // The immediate request and a dictation's hint can both be queued for one pause: the pause's release is paid once.
        release.RunPauseRequested();
        Assert.Equal(IdleReleaseOutcome.NotIdle, release.LastOutcome);
        Assert.Single(release.Gc.Collections);
    }

    [Fact]
    public void A_pause_made_while_a_release_runs_is_due_once_it_ends()
    {
        var release = new Release(forced: false);
        release.Lifecycle.Start(IdleDelay);
        IdleReleaseOutcome? nested = null;
        release.DuringUnload = () =>
        {
            release.Lifecycle.SetPaused(true);
            nested = release.RunNested(IdleReleaseTrigger.PauseRequest);
        };

        release.RunRequested(); // the idle deadline's release, with the pause's request turned away inside it

        Assert.Equal(IdleReleaseOutcome.NotIdle, nested);
        Assert.Equal(IdleReleaseTrigger.PauseRequest, release.Lifecycle.ReleaseDue());
        release.DuringUnload = null;
        release.RunPauseRequested();
        Assert.Equal(IdleReleaseOutcome.Released, release.LastOutcome);
        Assert.Null(release.Lifecycle.ReleaseDue());
    }

    /// <summary>
    /// AI cleanup's release goes out in the background with the authority of the claim, decided first, before anything is
    /// unloaded, so a recording that starts while the speech models unload cancels it rather than becoming its baseline.
    /// </summary>
    [Fact]
    public void The_release_that_goes_out_in_the_background_is_decided_first_after_the_claim()
    {
        var resident = new Release(forced: false) { RecordAfterClaim = true };
        resident.Lifecycle.Start(IdleDelay);
        resident.RunRequested();
        Assert.Equal(["after claim", "unload", "release buffers", "compact", "announce"], resident.Steps);

        var nothing = new Release(forced: false) { RecordAfterClaim = true, Resident = false };
        nothing.Lifecycle.Start(IdleDelay);
        nothing.RunRequested();
        Assert.Equal(["after claim", "release buffers"], nothing.Steps);

        var refused = new Release(forced: false) { RecordAfterClaim = true };
        refused.Lifecycle.Start(IdleDelay);
        refused.Lifecycle.TryBeginRecording(() => new Capture("a"));
        refused.RunRequested();
        Assert.Empty(refused.Steps);
    }

    [Fact]
    public void A_release_the_claim_started_in_the_background_is_withdrawn_by_anything_after_the_claim()
    {
        var model = new Release(forced: false) { RecordAfterClaim = true };
        model.Lifecycle.Start(IdleDelay);
        model.RunRequested();
        var ticket = model.Ticket!.Value;
        Assert.True(model.Lifecycle.IsCurrent(ticket));

        // A one-off request that was already running answered while the release waited for it: a new idle period.
        model.Lifecycle.NoteActivity(IdleDelay);
        Assert.False(model.Lifecycle.IsCurrent(ticket));

        var recording = new Release(forced: false) { RecordAfterClaim = true };
        recording.Lifecycle.Start(IdleDelay);
        recording.RunRequested();
        var claimed = recording.Ticket!.Value;
        recording.Lifecycle.TryBeginRecording(() => new Capture("a"));
        Assert.False(recording.Lifecycle.IsCurrent(claimed));

        var closing = new Release(forced: false) { RecordAfterClaim = true };
        closing.Lifecycle.Start(IdleDelay);
        closing.RunRequested();
        closing.Lifecycle.BeginShutdown();
        Assert.False(closing.Lifecycle.IsCurrent(closing.Ticket!.Value));
    }

    [Fact]
    public void A_pause_while_a_recording_s_microphone_opens_releases_when_the_recording_is_abandoned()
    {
        var release = new Release(forced: false);
        release.Lifecycle.Start(IdleDelay);
        var recording = release.Lifecycle.TryBeginRecording(() => new Capture("a"));
        release.Lifecycle.SetPaused(true);

        var idle = release.Lifecycle.TryAbandonRecording(recording.DictationId, IdleDelay);

        Assert.True(idle!.Value.PauseReleaseDue);
        release.RunPauseRequested();
        Assert.Equal(IdleReleaseOutcome.Released, release.LastOutcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_release_is_refused_while_recording_or_processing(bool forced)
    {
        var release = new Release(forced);
        release.Lifecycle.Start(IdleDelay);

        release.Lifecycle.TryBeginRecording(() => new Capture("a"));
        release.RunRequested();
        Assert.Equal(IdleReleaseOutcome.NotIdle, release.LastOutcome);

        var stop = release.Lifecycle.TryBeginProcessing();
        release.RunRequested();
        Assert.Equal(IdleReleaseOutcome.NotIdle, release.LastOutcome);

        release.Lifecycle.ReturnToIdle(IdleDelay);
        release.RunRequested(); // returned to idle, still finishing
        Assert.Equal(IdleReleaseOutcome.NotIdle, release.LastOutcome);

        Assert.Empty(release.Gc.Collections);
        release.Lifecycle.EndProcessing(stop.Admission!);
        release.RunRequested();
        Assert.Equal([Mode(forced)], release.Gc.Collections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nothing_resident_still_releases_the_buffers_and_collects_nothing(bool forced)
    {
        var release = new Release(forced) { Resident = false };
        release.Lifecycle.Start(IdleDelay);

        release.RunRequested();

        Assert.Equal(IdleReleaseOutcome.NothingResident, release.LastOutcome);
        Assert.Equal(["release buffers"], release.Steps);
        Assert.Empty(release.Gc.Collections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Closing_before_the_claim_or_during_the_unload_collects_nothing(bool forced)
    {
        var early = new Release(forced);
        early.Lifecycle.Start(IdleDelay);
        early.Lifecycle.BeginShutdown();
        early.RunRequested();
        Assert.Equal(IdleReleaseOutcome.NotIdle, early.LastOutcome);
        Assert.Empty(early.Gc.Collections);

        var late = new Release(forced);
        late.Lifecycle.Start(IdleDelay);
        late.DuringUnload = () => late.Lifecycle.BeginShutdown();
        late.RunRequested();
        Assert.Equal(IdleReleaseOutcome.UnloadedThenActivityResumed, late.LastOutcome);
        Assert.Empty(late.Gc.Collections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_second_release_while_one_runs_is_refused_and_only_one_collection_happens(bool forced)
    {
        var release = new Release(forced);
        release.Lifecycle.Start(IdleDelay);
        IdleReleaseOutcome? nested = null;
        release.DuringUnload = () => nested = release.RunNested();

        release.RunRequested();

        Assert.Equal(IdleReleaseOutcome.NotIdle, nested);
        Assert.Equal(IdleReleaseOutcome.Released, release.LastOutcome);
        Assert.Equal([Mode(forced)], release.Gc.Collections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_recording_that_starts_during_the_unload_skips_the_collection(bool forced)
    {
        var release = new Release(forced);
        release.Lifecycle.Start(IdleDelay);
        release.DuringUnload = () => release.Lifecycle.TryBeginRecording(() => new Capture("mid-unload"));

        release.RunRequested();

        Assert.Equal(IdleReleaseOutcome.UnloadedThenActivityResumed, release.LastOutcome);
        Assert.Empty(release.Gc.Collections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_recording_that_starts_after_the_last_check_still_meets_the_collection(bool forced)
    {
        // The documented race, pinned rather than sealed: the check before the collection passed, then a recording began
        // before the collector was called. Holding the lifecycle's gate across the collection is not the fix.
        var release = new Release(forced);
        release.Lifecycle.Start(IdleDelay);
        release.BeforeCollector = () => release.Lifecycle.TryBeginRecording(() => new Capture("after the check"));

        release.RunRequested();

        Assert.Equal([Mode(forced)], release.Gc.Collections);
        Assert.Equal(IdleReleaseOutcome.CompactedThenActivityResumed, release.LastOutcome);
        Assert.DoesNotContain("announce", release.Steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_recording_that_starts_during_the_collection_meets_it_and_skips_only_the_announcement(bool forced)
    {
        var release = new Release(forced);
        release.Lifecycle.Start(IdleDelay);
        release.Gc.DuringCollect = () => release.Lifecycle.TryBeginRecording(() => new Capture("during the collection"));

        release.RunRequested();

        Assert.Equal([Mode(forced)], release.Gc.Collections);
        Assert.Equal(IdleReleaseOutcome.CompactedThenActivityResumed, release.LastOutcome);
        Assert.Equal(["unload", "release buffers", "compact"], release.Steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shutdown_during_the_collection_lets_it_finish_and_skips_the_announcement(bool forced)
    {
        var release = new Release(forced);
        release.Lifecycle.Start(IdleDelay);
        release.Gc.DuringCollect = () => release.Lifecycle.BeginShutdown();

        release.RunRequested();

        Assert.Equal([Mode(forced)], release.Gc.Collections);
        Assert.Equal(IdleReleaseOutcome.CompactedThenActivityResumed, release.LastOutcome);
    }

    [Fact]
    public void The_controller_collects_only_through_the_seam_at_its_release_and_with_both_triggers()
    {
        var controller = ReadSource("src", "Scribe.App", "Dictation", "DictationController.cs");

        Assert.DoesNotContain("GC.Collect(", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("GCSettings.", controller, StringComparison.Ordinal);
        Assert.Single(Occurrences(controller, "IdleCollection.Compact("));
        Assert.Contains("compact: () => collected = IdleCollection.Compact(_perfFlags)", controller, StringComparison.Ordinal);
        Assert.Contains("() => ReleaseIdleModels(IdleReleaseTrigger.IdleDeadline)", controller, StringComparison.Ordinal);
        Assert.Contains("IdleReleaseRequests.OnPauseChange(paused, change)", controller, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => ReleaseIdleModels(IdleReleaseTrigger.PauseRequest))", controller, StringComparison.Ordinal);

        // A release the lifecycle kept while a dictation ran (a pause's, or a deadline that fell due as it finished) runs
        // when that dictation ends; AI cleanup's model follows the trigger; a model used outside a dictation restarts the
        // countdown.
        Assert.Contains("if (_lifecycle.EndProcessing(admission, faulted) is { } releaseDue)", controller, StringComparison.Ordinal);
        Assert.Contains("_ = Task.Run(() => ReleaseIdleModels(releaseDue));", controller, StringComparison.Ordinal);
        Assert.Contains(
            "trigger == IdleReleaseTrigger.PauseRequest ? ModelMemoryRelease.Pause : ModelMemoryRelease.Idle",
            controller,
            StringComparison.Ordinal);
        Assert.Contains("_cleanup.LocalModelUsed += OnLocalModelUsed;", controller, StringComparison.Ordinal);
        Assert.Contains("_lifecycle.NoteActivity(IdleReleaseDelay(CurrentSettings));", controller, StringComparison.Ordinal);
        Assert.Contains("afterClaim: ticket => _cleanup.ReleaseModelMemory(", controller, StringComparison.Ordinal);
        Assert.Contains(
            "trigger == IdleReleaseTrigger.PauseRequest ? () => _lifecycle.IsPaused : () => _lifecycle.IsCurrent(ticket)",
            controller,
            StringComparison.Ordinal);
        Assert.Contains("if (_lifecycle.ReleaseDue() is { } followUp)", controller, StringComparison.Ordinal);

        // What the release gives back before the collection (the order is IdleModelRelease's, pinned above): the capture
        // service's retained buffers and, since AU-3, its scratch pool, so the collection returns them too.
        Assert.Contains("var bytes = _audio.ReleaseRetainedBuffers();", controller, StringComparison.Ordinal);
        var capture = ReadSource("src", "Scribe.Core", "Audio", "AudioCaptureService.cs");
        Assert.Contains(
            "public long ReleaseRetainedBuffers() => _buffers.ReleaseRetained() + _scratch.ReleaseRetained();",
            capture,
            StringComparison.Ordinal);

        // No other production code collects: the idle release's collection is the only one.
        var core = Path.Combine(RepositoryRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
        {
            if (file.EndsWith("IdleCollection.cs", StringComparison.Ordinal) || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain("GC.Collect(", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    private static string Mode(bool forced) => forced ? "Forced" : "Aggressive";

    private static IEnumerable<int> Occurrences(string text, string value)
    {
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            yield return index;
        }
    }

    private static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    private sealed record Capture(string Name);

    /// <summary>
    /// The controller's release wiring (DictationController.ReleaseIdleModels), with the models, buffers and the GC replaced by
    /// recorders: the lifecycle's idle timer and a pause request both run the same release, whose compact step is
    /// IdleCollection over the fake collector, with no flag (Aggressive) or with ForcedIdleGc.
    /// </summary>
    private sealed class Release
    {
        private readonly PerfFlags _flags;

        public Release(bool forced)
        {
            _flags = forced ? PerfFlags.Parse(PerfFlags.ForcedIdleGc) : PerfFlags.None;
            Lifecycle = new DictationLifecycle<Capture>(
                () => LastOutcome = RunNested(IdleReleaseTrigger.IdleDeadline), () => { }, Clock);
        }

        public ManualTimeProvider Clock { get; } = new();

        public DictationLifecycle<Capture> Lifecycle { get; }

        public ManualTimer IdleTimer => Clock.Timers[0];

        public FakeCollector Gc { get; } = new();

        public List<string> Steps { get; } = [];

        public bool Resident { get; init; } = true;

        // Records the step that runs first after a claim (the controller's AI cleanup release) when set.
        public bool RecordAfterClaim { get; init; }

        // The ticket the last recorded claim handed that step.
        public IdleReleaseTicket? Ticket { get; private set; }

        public Action? DuringUnload { get; set; }

        public Action? BeforeCollector { get; set; }

        public IdleReleaseOutcome? LastOutcome { get; private set; }

        // The idle deadline falls due (the countdown armed with IdleDelay has run out) and its tick runs the release.
        public void RunRequested()
        {
            Clock.Advance(IdleDelay);
            LastOutcome = RunNested(IdleReleaseTrigger.IdleDeadline);
        }

        // What the controller runs when a pause asks for the release.
        public void RunPauseRequested() => LastOutcome = RunNested(IdleReleaseTrigger.PauseRequest);

        public IdleReleaseOutcome RunNested(IdleReleaseTrigger trigger = IdleReleaseTrigger.IdleDeadline) =>
            Lifecycle.RunIdleRelease(
                trigger,
                anythingResident: () => Resident,
                unload: () =>
                {
                    Steps.Add("unload");
                    DuringUnload?.Invoke();
                },
                compact: () =>
                {
                    Steps.Add("compact");
                    BeforeCollector?.Invoke();
                    IdleCollection.Compact(_flags, Gc);
                },
                announce: () => Steps.Add("announce"),
                releaseRetained: () => Steps.Add("release buffers"),
                afterClaim: RecordAfterClaim ? ticket => { Steps.Add("after claim"); Ticket = ticket; } : null);
    }

    internal sealed class FakeCollector : IIdleCollector
    {
        public List<string> Calls { get; } = [];

        public IEnumerable<string> Collections =>
            Calls.Where(call => call.StartsWith("collect:", StringComparison.Ordinal)).Select(call => call.Split(':')[2]);

        public int MaxGeneration { get; init; } = 2;

        public long CommittedBefore { get; init; }

        public long CommittedAfter { get; init; }

        public long HeapAfter { get; init; }

        public TimeSpan PauseBefore { get; init; }

        public TimeSpan PauseAfter { get; init; }

        public TimeSpan ElapsedValue { get; init; }

        public Action? DuringCollect { get; set; }

        private bool Collected => Calls.Any(call => call.StartsWith("collect:", StringComparison.Ordinal));

        public void CompactLargeObjectHeapOnce() => Calls.Add($"loh:{GCLargeObjectHeapCompactionMode.CompactOnce}");

        public void Collect(int generation, GCCollectionMode mode, bool blocking, bool compacting)
        {
            Calls.Add($"collect:{generation}:{mode}:{(blocking ? "blocking" : "background")}:{(compacting ? "compacting" : "sweeping")}");
            DuringCollect?.Invoke();
        }

        public (long CommittedBytes, long HeapBytes) Memory() =>
            Collected ? (CommittedAfter, HeapAfter) : (CommittedBefore, 0);

        public TimeSpan TotalPause() => Collected ? PauseAfter : PauseBefore;

        public long Timestamp() => 0;

        public TimeSpan ElapsedSince(long timestamp) => ElapsedValue;
    }
}
