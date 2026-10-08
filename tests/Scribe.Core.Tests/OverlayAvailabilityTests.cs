using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

public sealed class OverlayAvailabilityTests
{
    [Fact]
    public void A_failed_before_pipe_warmup_opens_one_episode_without_an_idle_retry_loop()
    {
        var lifetime = new OverlayHelperLifetime();
        Assert.Equal(OverlayAvailability.Initial, lifetime.Availability);
        lifetime.OnLaunchCompleted(0, OverlayLaunchResult.Failed, OverlayDemand.None);

        Assert.Equal(new OverlayAvailability(false, true, 1), lifetime.Availability);
        Assert.Null(lifetime.NextWakeAtMs());
        Assert.Equal(OverlayCommandAction.Drop, lifetime.OnHelperChanged(500, OverlayDemand.None, OverlayHelperObservation.Absent));
        Assert.Equal(OverlayCommandAction.Hold, Request(lifetime, 500, OverlayDemand.Sustained));
        Assert.Equal(1_000, lifetime.NextWakeAtMs());
        Assert.Equal(OverlayDueWork.Retry, lifetime.TakeDueWork(1_000, OverlayDemand.Sustained, OverlayHelperObservation.Absent));
    }

    [Fact]
    public void A_first_successful_launch_is_available_but_a_connect_during_recovery_is_not()
    {
        var lifetime = new OverlayHelperLifetime();
        lifetime.OnLaunchCompleted(0, OverlayLaunchResult.Launched, OverlayDemand.Sustained);
        Assert.Equal(new OverlayAvailability(true, false, 0), lifetime.Availability);
        Assert.Null(lifetime.NextWakeAtMs());

        Assert.Equal(OverlayCommandAction.Hold,
            lifetime.OnHelperChanged(200, OverlayDemand.Sustained, OverlayHelperObservation.Lost(200)));
        var episode = lifetime.Availability;
        lifetime.OnLaunchCompleted(1_200, OverlayLaunchResult.Launched, OverlayDemand.Sustained);
        Assert.Equal(episode, lifetime.Availability);
        Assert.Equal(11_200, lifetime.StableDueAtMs);
    }

    [Fact]
    public void Connected_then_early_crashes_keep_the_episode_open_and_resume_the_backoff()
    {
        var lifetime = new OverlayHelperLifetime();
        lifetime.OnLaunchCompleted(0, OverlayLaunchResult.Failed, OverlayDemand.Sustained);
        lifetime.OnLaunchCompleted(1_000, OverlayLaunchResult.Launched, OverlayDemand.Sustained);
        Assert.False(lifetime.Availability.IsAvailable);

        Assert.Equal(OverlayCommandAction.Hold,
            lifetime.OnHelperChanged(2_000, OverlayDemand.Sustained, OverlayHelperObservation.Lost(2_000)));
        Assert.Equal(2, lifetime.ConsecutiveFailures);
        Assert.Equal(new OverlayAvailability(false, true, 1), lifetime.Availability);
        Assert.Null(lifetime.StableDueAtMs);
        Assert.Equal(4_000, lifetime.RetryDueAtMs);

        lifetime.OnLaunchCompleted(4_000, OverlayLaunchResult.Launched, OverlayDemand.Sustained);
        lifetime.TakeDueWork(11_000, OverlayDemand.Sustained, OverlayHelperObservation.Alive);
        Assert.False(lifetime.Availability.IsAvailable);
        Assert.Equal(14_000, lifetime.NextWakeAtMs());
        lifetime.TakeDueWork(14_000, OverlayDemand.Sustained, OverlayHelperObservation.Alive);
        Assert.Equal(new OverlayAvailability(true, false, 1), lifetime.Availability);
        Assert.Null(lifetime.NextWakeAtMs());
    }

    [Fact]
    public void Stability_has_its_own_consumer_wake_even_when_processing_sends_nothing()
    {
        var lifetime = new OverlayHelperLifetime();
        lifetime.OnLaunchCompleted(100, OverlayLaunchResult.Failed, OverlayDemand.Sustained);
        lifetime.OnLaunchCompleted(1_100, OverlayLaunchResult.Launched, OverlayDemand.Sustained);

        Assert.Equal(11_100, lifetime.NextWakeAtMs());
        Assert.Equal(OverlayDueWork.None, lifetime.TakeDueWork(11_099, OverlayDemand.Sustained, OverlayHelperObservation.Alive));
        Assert.False(lifetime.Availability.IsAvailable);
        Assert.Equal(OverlayDueWork.None, lifetime.TakeDueWork(11_100, OverlayDemand.Sustained, OverlayHelperObservation.Alive));
        Assert.True(lifetime.Availability.IsAvailable);
        Assert.False(lifetime.Availability.IsFaulted);
        Assert.Null(lifetime.NextWakeAtMs());

        lifetime.OnHelperChanged(12_000, OverlayDemand.Sustained, OverlayHelperObservation.Lost(12_000));
        Assert.Equal(2, lifetime.Availability.FailureEpisode);
    }

    [Fact]
    public void A_late_observation_of_an_early_exit_cannot_mistake_it_for_stable_recovery()
    {
        var lifetime = new OverlayHelperLifetime();
        lifetime.OnLaunchCompleted(0, OverlayLaunchResult.Failed, OverlayDemand.Sustained);
        lifetime.OnLaunchCompleted(1_000, OverlayLaunchResult.Launched, OverlayDemand.Sustained);

        lifetime.TakeDueWork(15_000, OverlayDemand.Sustained, OverlayHelperObservation.Lost(2_000));

        Assert.Equal(new OverlayAvailability(false, true, 1), lifetime.Availability);
        Assert.Equal(new OverlayHelperLoss(1_000, 2_000), lifetime.LastLoss);
        Assert.Null(lifetime.StableDueAtMs);
    }

    [Theory]
    [InlineData(OverlayDemand.None)]
    [InlineData(OverlayDemand.Transient)]
    public void An_exit_wake_for_an_idle_helper_or_an_outcome_launches_nothing(OverlayDemand demand)
    {
        var lifetime = new OverlayHelperLifetime();
        lifetime.OnLaunchCompleted(0, OverlayLaunchResult.Launched, demand);

        Assert.Equal(OverlayCommandAction.Drop, lifetime.OnHelperChanged(500, demand, OverlayHelperObservation.Lost(500)));
        Assert.Equal(new OverlayAvailability(false, true, 1), lifetime.Availability);
        Assert.Null(lifetime.NextWakeAtMs());
    }

    [Fact]
    public void Intentional_suspend_pause_release_and_exit_never_open_a_fault_episode()
    {
        var suspend = new OverlayHelperLifetime();
        suspend.SetIdlePeriodMs(100);
        Request(suspend, 0, OverlayDemand.None);
        suspend.OnLaunchCompleted(0, OverlayLaunchResult.Launched, OverlayDemand.None);
        Assert.Equal(OverlayDueWork.Suspend, suspend.TakeDueWork(100, OverlayDemand.None, OverlayHelperObservation.Alive));
        Assert.Equal(OverlayAvailability.Initial, suspend.Availability);
        Assert.Equal(OverlayCommandAction.Drop, suspend.OnHelperChanged(101, OverlayDemand.None, OverlayHelperObservation.Absent));

        var pause = new OverlayHelperLifetime();
        pause.OnLaunchCompleted(0, OverlayLaunchResult.Launched, OverlayDemand.None);
        Assert.Equal(OverlayDueWork.Release,
            pause.OnReleaseWhenIdle(100, pause.IssueStamp(), OverlayDemand.None, OverlayHelperObservation.Alive));
        Assert.Equal(OverlayAvailability.Initial, pause.Availability);
        pause.OnLaunchCompleted(200, OverlayLaunchResult.Launched, OverlayDemand.Sustained);
        Assert.Equal(OverlayCommandAction.Write,
            pause.OnHelperChanged(201, OverlayDemand.Sustained, OverlayHelperObservation.Alive));
        Assert.Equal(new OverlayAvailability(true, false, 0), pause.Availability); // A queued old exit reads the current helper.

        var close = new OverlayHelperLifetime();
        close.OnLaunchCompleted(0, OverlayLaunchResult.Launched, OverlayDemand.Sustained);
        close.OnExit();
        Assert.Equal(OverlayAvailability.Initial, close.Availability);
        Assert.Equal(OverlayCommandAction.Drop,
            close.OnHelperChanged(100, OverlayDemand.Sustained, OverlayHelperObservation.Lost(100)));
        Assert.Null(close.NextWakeAtMs());
    }

    [Fact]
    public void Intentional_stop_during_recovery_cancels_the_stability_wake_without_creating_a_new_fault()
    {
        var lifetime = new OverlayHelperLifetime();
        lifetime.OnLaunchCompleted(0, OverlayLaunchResult.Failed, OverlayDemand.Sustained);
        lifetime.OnLaunchCompleted(1_000, OverlayLaunchResult.Launched, OverlayDemand.None);
        lifetime.OnReleaseWhenIdle(2_000, lifetime.IssueStamp(), OverlayDemand.None, OverlayHelperObservation.Alive);

        Assert.Equal(new OverlayAvailability(false, true, 1), lifetime.Availability);
        Assert.Null(lifetime.StableDueAtMs);
        Assert.Null(lifetime.NextWakeAtMs());
        lifetime.OnHelperChanged(3_000, OverlayDemand.None, OverlayHelperObservation.Absent);
        Assert.Equal(1, lifetime.Availability.FailureEpisode);
    }

    [Fact]
    public void A_close_that_abandons_startup_is_not_a_failed_start()
    {
        var lifetime = new OverlayHelperLifetime();
        lifetime.OnLaunchCompleted(500, OverlayLaunchResult.Abandoned, OverlayDemand.Sustained);
        lifetime.OnExit();

        Assert.Equal(OverlayAvailability.Initial, lifetime.Availability);
        Assert.Equal(0, lifetime.ConsecutiveFailures);
        Assert.Null(lifetime.NextWakeAtMs());
    }

    [Theory]
    [InlineData(null, "unset")]
    [InlineData(0, "0x00000000")]
    [InlineData(unchecked((int)0x8898008D), "0x8898008D")]
    [InlineData(unchecked((int)0xC0000005), "0xC0000005")]
    public void Exit_codes_keep_their_unsigned_hex_shape(int? code, string expected) =>
        Assert.Equal(expected, OverlayExitCode.Format(code));

    private static OverlayCommandAction Request(OverlayHelperLifetime lifetime, long nowMs, OverlayDemand demand) =>
        lifetime.OnStateCommand(nowMs, lifetime.IssueStamp(), true, false, demand, OverlayHelperObservation.Absent);
}
