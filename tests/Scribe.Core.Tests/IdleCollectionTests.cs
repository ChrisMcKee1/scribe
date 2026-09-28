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
        release.RunRequested();
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
        release.RunRequested();

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
        release.RunRequested();

        Assert.Equal(IdleReleaseOutcome.NotIdle, release.LastOutcome);
        Assert.Empty(release.Gc.Collections);
        Assert.Empty(release.Steps);
        Assert.NotNull(stop.Admission);
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
            Lifecycle = new DictationLifecycle<Capture>(RunRequested, () => { }, Clock);
        }

        public ManualTimeProvider Clock { get; } = new();

        public DictationLifecycle<Capture> Lifecycle { get; }

        public ManualTimer IdleTimer => Clock.Timers[0];

        public FakeCollector Gc { get; } = new();

        public List<string> Steps { get; } = [];

        public bool Resident { get; init; } = true;

        public Action? DuringUnload { get; set; }

        public Action? BeforeCollector { get; set; }

        public IdleReleaseOutcome? LastOutcome { get; private set; }

        public void RunRequested() => LastOutcome = RunNested();

        public IdleReleaseOutcome RunNested() =>
            Lifecycle.RunIdleRelease(
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
                releaseRetained: () => Steps.Add("release buffers"));
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
