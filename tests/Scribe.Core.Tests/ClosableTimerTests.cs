using Scribe.Core.Lifecycle;
using Scribe.Core.Tests.Concurrency;

namespace Scribe.Core.Tests;

/// <summary>
/// ClosableTimer is what lets the dictation controller retire its timers while other threads still try to re-arm
/// them, and what keeps a tick queued for one schedule from acting on the next. The fake timer throws from Change once
/// disposed, exactly as <see cref="Timer"/> does, and the fake clock only moves when a test advances it, so every
/// interleaving below is replayed exactly rather than hoped for.
/// </summary>
public sealed class ClosableTimerTests
{
    [Fact]
    public void Schedule_arms_a_one_shot_and_cancel_disarms_it()
    {
        var provider = new ManualTimeProvider();
        using var timer = new ClosableTimer(() => { }, provider);
        var inner = Assert.Single(provider.Timers);

        Assert.True(timer.Schedule(TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(5), inner.DueTime);
        Assert.True(timer.Cancel());
        Assert.Equal(Timeout.InfiniteTimeSpan, inner.DueTime);
    }

    [Fact]
    public void A_tick_that_is_due_runs_the_callback_exactly_once()
    {
        var provider = new ManualTimeProvider();
        var ticks = 0;
        using var timer = new ClosableTimer(() => ticks++, provider);
        var inner = provider.Timers[0];

        timer.Schedule(TimeSpan.FromMinutes(1));
        provider.Advance(TimeSpan.FromMinutes(1));
        inner.Fire();
        inner.Fire(); // a duplicate delivery of the same schedule

        Assert.Equal(1, ticks);
    }

    /// <summary>
    /// The reviewed defect, at the timer level: recording A's ceiling tick is already queued when A stops and B arms
    /// its own ceiling. Delivered late, that tick must neither run the callback (which would end B) nor cost B its own.
    /// </summary>
    [Fact]
    public void A_stale_tick_from_a_replaced_schedule_is_dropped_and_the_new_schedule_still_fires()
    {
        var provider = new ManualTimeProvider();
        var ticks = 0;
        using var timer = new ClosableTimer(() => ticks++, provider);
        var inner = provider.Timers[0];

        timer.Schedule(TimeSpan.FromMinutes(5)); // A arms its ceiling
        provider.Advance(TimeSpan.FromMinutes(5)); // A's tick is due and queued, but not yet delivered
        timer.Cancel(); // A stops
        timer.Schedule(TimeSpan.FromMinutes(5)); // B starts and arms its ceiling
        provider.Advance(TimeSpan.FromMinutes(1));

        inner.Fire(); // A's stale tick finally runs

        Assert.Equal(0, ticks);
        Assert.Equal("change:00:04:00", inner.Events[^1]); // re-armed for what remains of B's schedule

        provider.Advance(TimeSpan.FromMinutes(4));
        inner.Fire();

        Assert.Equal(1, ticks);
    }

    [Fact]
    public void A_tick_for_a_canceled_schedule_is_dropped()
    {
        var provider = new ManualTimeProvider();
        var ticks = 0;
        using var timer = new ClosableTimer(() => ticks++, provider);
        var inner = provider.Timers[0];

        timer.Schedule(TimeSpan.FromMinutes(1));
        provider.Advance(TimeSpan.FromMinutes(1));
        timer.Cancel();
        inner.Fire();

        Assert.Equal(0, ticks);
    }

    [Fact]
    public void A_tick_that_arrives_early_re_arms_for_the_remaining_time_instead_of_being_lost()
    {
        var provider = new ManualTimeProvider();
        var ticks = 0;
        using var timer = new ClosableTimer(() => ticks++, provider);
        var inner = provider.Timers[0];

        timer.Schedule(TimeSpan.FromMinutes(1));
        provider.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromMilliseconds(10)); // coarse timer resolution
        inner.Fire();

        Assert.Equal(0, ticks);
        Assert.Equal("change:00:00:00.0100000", inner.Events[^1]);

        provider.Advance(TimeSpan.FromMilliseconds(10));
        inner.Fire();

        Assert.Equal(1, ticks);
    }

    [Fact]
    public void After_close_every_schedule_is_a_no_op_that_never_touches_the_disposed_timer()
    {
        var provider = new ManualTimeProvider();
        var timer = new ClosableTimer(() => { }, provider);
        var inner = Assert.Single(provider.Timers);

        timer.Close();

        Assert.True(timer.IsClosed);
        Assert.False(timer.Schedule(TimeSpan.FromMinutes(10)));
        Assert.False(timer.Cancel());
        Assert.Equal(new[] { "dispose" }, inner.Events);
    }

    [Fact]
    public void Close_is_idempotent()
    {
        var provider = new ManualTimeProvider();
        var timer = new ClosableTimer(() => { }, provider);

        timer.Close();
        timer.Close();
        timer.Dispose();

        Assert.Equal(1, provider.Timers[0].DisposeCount);
    }

    [Fact]
    public void A_tick_that_arrives_after_close_does_not_run_the_callback()
    {
        var provider = new ManualTimeProvider();
        var ticks = 0;
        var timer = new ClosableTimer(() => ticks++, provider);
        var inner = provider.Timers[0];

        timer.Schedule(TimeSpan.FromMinutes(1));
        provider.Advance(TimeSpan.FromMinutes(1));
        timer.Close();
        inner.Fire(); // the pool can still deliver a tick queued before the close

        Assert.Equal(0, ticks);
        Assert.Equal("dispose", inner.Events[^1]); // and it never touched the disposed timer
    }

    [Fact]
    public void Close_waits_for_a_schedule_already_in_progress_and_disposes_only_after_it()
    {
        using var inChange = new ManualResetEventSlim();
        using var releaseChange = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(releaseChange);
        var provider = new ManualTimeProvider();
        var timer = new ClosableTimer(() => { }, provider);
        var inner = provider.Timers[0];
        timer.Cancel(); // compiles the call path before the race
        inner.DuringChange = () =>
        {
            inChange.Set();
            releaseChange.Wait();
        };

        var scheduled = false;
        var scheduler = BlockedThreads.Start(() => scheduled = timer.Schedule(TimeSpan.FromMinutes(1)));
        Assert.True(inChange.Wait(BlockedThreads.SafetyTimeout));

        var closer = BlockedThreads.Start(timer.Close);
        BlockedThreads.WaitUntilBlocked(closer);
        Assert.False(inner.IsDisposed);

        inner.DuringChange = null;
        releaseChange.Set();
        BlockedThreads.Join(scheduler);
        BlockedThreads.Join(closer);

        Assert.True(scheduled);
        Assert.Equal("dispose", inner.Events[^1]);
        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public void Close_never_waits_for_a_running_callback_and_the_callback_cannot_re_arm_afterwards()
    {
        using var inCallback = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var releaseAtExit = new ReleaseAtExit(releaseCallback);
        var provider = new ManualTimeProvider();
        ClosableTimer? timer = null;
        bool? rearmed = null;
        timer = new ClosableTimer(
            () =>
            {
                inCallback.Set();
                releaseCallback.Wait();
                rearmed = timer!.Schedule(TimeSpan.FromMinutes(1)); // what an owner re-arming on its way out does
            },
            provider);
        var inner = provider.Timers[0];
        timer.Schedule(TimeSpan.FromMinutes(1));
        provider.Advance(TimeSpan.FromMinutes(1));

        var tick = BlockedThreads.Start(inner.Fire);
        Assert.True(inCallback.Wait(BlockedThreads.SafetyTimeout));

        timer.Close(); // returns while the callback is still running: the owner's teardown is never held hostage

        releaseCallback.Set();
        BlockedThreads.Join(tick);
        Assert.False(rearmed);
        Assert.Equal("dispose", inner.Events[^1]);
    }

    [Fact]
    public void Due_times_beyond_the_platform_limit_are_clamped_instead_of_throwing()
    {
        var provider = new ManualTimeProvider();
        using var timer = new ClosableTimer(() => { }, provider);

        Assert.True(timer.Schedule(TimeSpan.FromDays(400)));

        Assert.Equal(ClosableTimer.MaxDueTime, provider.Timers[0].DueTime);
    }
}
