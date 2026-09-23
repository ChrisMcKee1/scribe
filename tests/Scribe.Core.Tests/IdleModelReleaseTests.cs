using Scribe.Core.Lifecycle;

namespace Scribe.Core.Tests;

/// <summary>
/// The ordering contract of an idle model release: claimed while idle, unloaded outside the owner's lock, and the
/// blocking collection and the announcement each re-validated first, so a dictation that starts mid-release is never
/// held up by a compaction or reported as released. Idle-only buffers go on every claimed release, resident models or
/// not.
/// </summary>
public sealed class IdleModelReleaseTests
{
    [Fact]
    public void An_owner_that_is_not_idle_runs_nothing()
    {
        var owner = new Owner { Idle = false };

        Assert.Equal(IdleReleaseOutcome.NotIdle, owner.Release());
        Assert.Empty(owner.Steps);
    }

    [Fact]
    public void Nothing_resident_skips_the_unload_but_still_releases_idle_buffers()
    {
        var owner = new Owner { Resident = false };

        Assert.Equal(IdleReleaseOutcome.NothingResident, owner.Release());
        Assert.Equal(new[] { "release buffers" }, owner.Steps);
    }

    [Fact]
    public void A_release_that_stays_idle_unloads_releases_buffers_compacts_then_announces()
    {
        var owner = new Owner();

        Assert.Equal(IdleReleaseOutcome.Released, owner.Release());
        Assert.Equal(new[] { "unload", "release buffers", "compact", "announce" }, owner.Steps);
    }

    [Fact]
    public void The_buffer_step_is_optional()
    {
        var owner = new Owner { ReleaseBuffers = false };

        Assert.Equal(IdleReleaseOutcome.Released, owner.Release());
        Assert.Equal(new[] { "unload", "compact", "announce" }, owner.Steps);
    }

    [Fact]
    public void A_dictation_starting_during_the_unload_skips_the_compaction_and_the_announcement()
    {
        var owner = new Owner();
        owner.DuringUnload = owner.StartDictation;

        Assert.Equal(IdleReleaseOutcome.UnloadedThenActivityResumed, owner.Release());
        Assert.Equal(new[] { "unload", "release buffers" }, owner.Steps);
    }

    [Fact]
    public void A_dictation_starting_during_the_compaction_skips_only_the_announcement()
    {
        var owner = new Owner();
        owner.DuringCompact = owner.StartDictation;

        Assert.Equal(IdleReleaseOutcome.CompactedThenActivityResumed, owner.Release());
        Assert.Equal(new[] { "unload", "release buffers", "compact" }, owner.Steps);
    }

    [Fact]
    public void A_dictation_that_starts_and_finishes_during_the_unload_still_invalidates_the_claim()
    {
        // Idle again by the time the release checks, but a dictation happened in between: the epoch, not the state,
        // is what tells the release its claim is stale.
        var owner = new Owner();
        owner.DuringUnload = () =>
        {
            owner.StartDictation();
            owner.Idle = true;
        };

        Assert.Equal(IdleReleaseOutcome.UnloadedThenActivityResumed, owner.Release());
    }

    [Fact]
    public void Closing_during_the_unload_skips_everything_after_it()
    {
        var owner = new Owner();
        owner.DuringUnload = () => owner.Closing = true;

        Assert.Equal(IdleReleaseOutcome.UnloadedThenActivityResumed, owner.Release());
        Assert.Equal(new[] { "unload", "release buffers" }, owner.Steps);
    }

    // Stands in for the dictation controller: its gate guards the state and the activity epoch.
    private sealed class Owner
    {
        private readonly object _gate = new();
        private long _epoch;

        public bool Idle { get; set; } = true;

        public bool Resident { get; init; } = true;

        public bool ReleaseBuffers { get; init; } = true;

        public bool Closing { get; set; }

        public List<string> Steps { get; } = [];

        public Action? DuringUnload { get; set; }

        public Action? DuringCompact { get; set; }

        public void StartDictation()
        {
            lock (_gate)
            {
                Idle = false;
                _epoch++;
            }
        }

        public IdleReleaseOutcome Release() => IdleModelRelease.Run(
            tryClaim: () =>
            {
                lock (_gate)
                {
                    return Idle && !Closing ? _epoch : null;
                }
            },
            isStillIdle: claim =>
            {
                lock (_gate)
                {
                    return !Closing && _epoch == claim;
                }
            },
            anythingResident: () => Resident,
            unload: () =>
            {
                Assert.False(Monitor.IsEntered(_gate)); // native work never runs under the owner's lock
                Steps.Add("unload");
                DuringUnload?.Invoke();
            },
            compact: () =>
            {
                Assert.False(Monitor.IsEntered(_gate));
                Steps.Add("compact");
                DuringCompact?.Invoke();
            },
            announce: () => Steps.Add("announce"),
            releaseRetained: ReleaseBuffers
                ? () =>
                {
                    Assert.False(Monitor.IsEntered(_gate));
                    Steps.Add("release buffers");
                }
                : null);
    }
}
