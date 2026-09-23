using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

public sealed class OverlayLaunchBackoffTests
{
    [Fact]
    public void Defaults_are_one_second_doubling_to_sixty_with_a_ten_second_stable_window()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), OverlayLaunchBackoff.DefaultInitialCooldown);
        Assert.Equal(TimeSpan.FromSeconds(60), OverlayLaunchBackoff.DefaultMaxCooldown);
        Assert.Equal(TimeSpan.FromSeconds(10), OverlayLaunchBackoff.DefaultStableAfter);
    }

    [Fact]
    public void A_fresh_policy_allows_a_launch()
    {
        var backoff = new OverlayLaunchBackoff();

        Assert.Equal(OverlayLaunchDecision.Attempt, backoff.OnLaunchWanted(0, OverlayDemand.Sustained));
        Assert.Equal(0, backoff.ConsecutiveFailures);
        Assert.Equal(0, backoff.CooldownRemainingMs(0));
        Assert.Null(backoff.RetryDueAtMs);
    }

    [Fact]
    public void Consecutive_failures_double_the_cooldown_up_to_the_cap()
    {
        var backoff = new OverlayLaunchBackoff();
        long[] expected = [1_000, 2_000, 4_000, 8_000, 16_000, 32_000, 60_000, 60_000, 60_000];

        var nowMs = 0L;
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], backoff.OnLaunchFailed(nowMs, OverlayDemand.None));
            Assert.Equal(i + 1, backoff.ConsecutiveFailures);
            Assert.Equal(expected[i], backoff.LastCooldownMs);
            nowMs += expected[i];
        }
    }

    [Fact]
    public void The_schedule_is_a_pure_function_of_the_failure_count()
    {
        var backoff = new OverlayLaunchBackoff();

        Assert.Equal(0, backoff.CooldownAfterFailures(0));
        Assert.Equal(1_000, backoff.CooldownAfterFailures(1));
        Assert.Equal(32_000, backoff.CooldownAfterFailures(6));
        Assert.Equal(60_000, backoff.CooldownAfterFailures(7));
        Assert.Equal(60_000, backoff.CooldownAfterFailures(int.MaxValue));
    }

    [Fact]
    public void Doubling_saturates_at_a_huge_cap_instead_of_overflowing()
    {
        var backoff = new OverlayLaunchBackoff(TimeSpan.FromMilliseconds(3), TimeSpan.MaxValue, TimeSpan.Zero);

        Assert.Equal((long)TimeSpan.MaxValue.TotalMilliseconds, backoff.CooldownAfterFailures(int.MaxValue));
    }

    [Fact]
    public void Commands_during_the_cooldown_do_not_relaunch()
    {
        var backoff = new OverlayLaunchBackoff();
        backoff.OnLaunchFailed(0, OverlayDemand.None);

        Assert.Equal(OverlayLaunchDecision.HeldBack, backoff.OnLaunchWanted(1, OverlayDemand.None));
        Assert.Equal(OverlayLaunchDecision.HeldBack, backoff.OnLaunchWanted(999, OverlayDemand.Transient));
        Assert.Equal(1, backoff.CooldownRemainingMs(999));
        Assert.Null(backoff.RetryDueAtMs);

        Assert.Equal(OverlayLaunchDecision.Attempt, backoff.OnLaunchWanted(1_000, OverlayDemand.None));
    }

    [Fact]
    public void Success_resets_the_backoff()
    {
        var backoff = new OverlayLaunchBackoff();
        backoff.OnLaunchFailed(0, OverlayDemand.Sustained);
        backoff.OnLaunchFailed(1_000, OverlayDemand.Sustained);
        backoff.OnLaunchFailed(3_000, OverlayDemand.Sustained);

        backoff.OnLaunchSucceeded(7_000);

        Assert.Equal(0, backoff.ConsecutiveFailures);
        Assert.Equal(0, backoff.CooldownRemainingMs(7_000));
        Assert.Null(backoff.RetryDueAtMs);
        Assert.Equal(OverlayLaunchDecision.Attempt, backoff.OnLaunchWanted(7_000, OverlayDemand.Sustained));

        // A later, unrelated failure starts again from one second rather than resuming at eight.
        Assert.Equal(1_000, backoff.OnLaunchFailed(600_000, OverlayDemand.None));
    }

    [Fact]
    public void A_failure_while_the_pill_must_show_schedules_one_retry_at_the_end_of_the_cooldown()
    {
        var backoff = new OverlayLaunchBackoff();

        backoff.OnLaunchFailed(500, OverlayDemand.Sustained);

        Assert.Equal(1_500, backoff.RetryDueAtMs);
        Assert.False(backoff.TryTakeDueRetry(1_499, OverlayDemand.Sustained));
        Assert.True(backoff.TryTakeDueRetry(1_500, OverlayDemand.Sustained));
        Assert.Equal(OverlayLaunchDecision.Attempt, backoff.OnLaunchWanted(1_500, OverlayDemand.Sustained));
    }

    [Theory]
    [InlineData(OverlayDemand.None)]
    [InlineData(OverlayDemand.Transient)]
    public void A_failure_for_a_hidden_or_self_hiding_state_schedules_no_retry(OverlayDemand demand)
    {
        var backoff = new OverlayLaunchBackoff();

        backoff.OnLaunchFailed(0, demand);

        Assert.Null(backoff.RetryDueAtMs);
        Assert.False(backoff.TryTakeDueRetry(100_000, OverlayDemand.Sustained));
    }

    [Fact]
    public void Commands_during_the_cooldown_coalesce_into_exactly_one_retry()
    {
        var backoff = new OverlayLaunchBackoff();
        backoff.OnLaunchFailed(0, OverlayDemand.None); // the startup warmup failed while hidden

        // A recording starts inside the cooldown: RECORDING, a warning, then PROCESSING.
        Assert.Equal(OverlayLaunchDecision.RetryPending, backoff.OnLaunchWanted(100, OverlayDemand.Sustained));
        Assert.Equal(OverlayLaunchDecision.RetryPending, backoff.OnLaunchWanted(400, OverlayDemand.Sustained));
        Assert.Equal(OverlayLaunchDecision.RetryPending, backoff.OnLaunchWanted(900, OverlayDemand.Sustained));

        Assert.Equal(1_000, backoff.RetryDueAtMs);
        Assert.True(backoff.TryTakeDueRetry(1_000, OverlayDemand.Sustained));
        Assert.False(backoff.TryTakeDueRetry(1_000, OverlayDemand.Sustained));
        Assert.Null(backoff.RetryDueAtMs);
    }

    [Fact]
    public void The_retry_is_dropped_when_the_latest_state_no_longer_needs_the_pill()
    {
        // RECORDING could not launch. By the end of the cooldown the dictation has moved on to a failure
        // flash, so relaunching to show a recording would replay a stale state.
        var backoff = new OverlayLaunchBackoff();
        backoff.OnLaunchFailed(0, OverlayDemand.Sustained);

        Assert.False(backoff.TryTakeDueRetry(1_000, OverlayDemand.Transient));
        Assert.Null(backoff.RetryDueAtMs);
        Assert.False(backoff.TryTakeDueRetry(2_000, OverlayDemand.Sustained));
    }

    [Fact]
    public void The_retry_fires_for_whichever_sustained_state_is_latest()
    {
        // RECORDING failed; PROCESSING arrived inside the cooldown. One retry fires, and the client
        // replays the latest line (PROCESSING), never the superseded RECORDING.
        var backoff = new OverlayLaunchBackoff();
        backoff.OnLaunchFailed(0, OverlayDemand.Sustained);
        Assert.Equal(OverlayLaunchDecision.RetryPending, backoff.OnLaunchWanted(600, OverlayDemand.Sustained));

        Assert.True(backoff.TryTakeDueRetry(1_000, OverlayDemand.Sustained));
    }

    [Fact]
    public void Cancelling_drops_the_pending_retry_but_not_the_cooldown()
    {
        var backoff = new OverlayLaunchBackoff();
        backoff.OnLaunchFailed(0, OverlayDemand.Sustained);

        backoff.CancelRetry(); // HIDE, idle suspend, close and exit

        Assert.Null(backoff.RetryDueAtMs);
        Assert.False(backoff.TryTakeDueRetry(100_000, OverlayDemand.Sustained));

        // Cancelling is not a success: the cooldown still holds, and a new recording inside it leaves
        // a fresh retry pending rather than launching at once.
        Assert.Equal(500, backoff.CooldownRemainingMs(500));
        Assert.Equal(OverlayLaunchDecision.RetryPending, backoff.OnLaunchWanted(500, OverlayDemand.Sustained));
        Assert.Equal(1_000, backoff.RetryDueAtMs);
    }

    [Fact]
    public void A_retry_that_fails_again_escalates_and_leaves_one_new_retry()
    {
        var backoff = new OverlayLaunchBackoff();
        backoff.OnLaunchFailed(0, OverlayDemand.Sustained);
        Assert.True(backoff.TryTakeDueRetry(1_000, OverlayDemand.Sustained));
        Assert.Equal(OverlayLaunchDecision.Attempt, backoff.OnLaunchWanted(1_000, OverlayDemand.Sustained));

        Assert.Equal(2_000, backoff.OnLaunchFailed(1_010, OverlayDemand.Sustained));

        Assert.Equal(2, backoff.ConsecutiveFailures);
        Assert.Equal(3_010, backoff.RetryDueAtMs);
    }

    [Fact]
    public void A_helper_lost_soon_after_its_launch_counts_as_a_failed_launch()
    {
        // It connected, then died on the state it had just been shown. Treating that as a success would
        // let every later pipe write relaunch it with no cooldown at all.
        var backoff = new OverlayLaunchBackoff();
        backoff.OnLaunchSucceeded(0);

        Assert.Equal(1_000, backoff.OnHelperLost(2_000, OverlayDemand.Sustained));
        Assert.Equal(1, backoff.ConsecutiveFailures);
        Assert.Equal(3_000, backoff.RetryDueAtMs);
        Assert.Equal(OverlayLaunchDecision.RetryPending, backoff.OnLaunchWanted(2_001, OverlayDemand.Sustained));
    }

    [Fact]
    public void A_helper_that_keeps_dying_after_connecting_backs_off_like_any_failure()
    {
        var backoff = new OverlayLaunchBackoff();
        long[] expected = [1_000, 2_000, 4_000, 8_000, 16_000, 32_000, 60_000, 60_000];

        var nowMs = 0L;
        foreach (var cooldownMs in expected)
        {
            backoff.OnLaunchSucceeded(nowMs);
            Assert.Equal(cooldownMs, backoff.OnHelperLost(nowMs + 500, OverlayDemand.Sustained));

            nowMs += 500 + cooldownMs;
            Assert.True(backoff.TryTakeDueRetry(nowMs, OverlayDemand.Sustained));
            Assert.Equal(OverlayLaunchDecision.Attempt, backoff.OnLaunchWanted(nowMs, OverlayDemand.Sustained));
        }
    }

    [Fact]
    public void A_helper_lost_after_proving_stable_relaunches_at_once()
    {
        var backoff = new OverlayLaunchBackoff();
        backoff.OnLaunchFailed(0, OverlayDemand.None);
        backoff.OnLaunchSucceeded(5_000);

        Assert.Null(backoff.OnHelperLost(15_000, OverlayDemand.Sustained));

        Assert.Equal(0, backoff.ConsecutiveFailures);
        Assert.Null(backoff.RetryDueAtMs);
        Assert.Equal(OverlayLaunchDecision.Attempt, backoff.OnLaunchWanted(15_000, OverlayDemand.Sustained));
    }

    [Fact]
    public void A_loss_is_judged_and_cooled_down_from_when_it_happened_not_when_it_was_noticed()
    {
        // The helper died 3 s after launching; nothing noticed until a command 30 s later.
        var backoff = new OverlayLaunchBackoff();
        backoff.OnLaunchSucceeded(0);

        Assert.Equal(1_000, backoff.OnHelperLost(3_000, OverlayDemand.Sustained));

        Assert.Equal(1, backoff.ConsecutiveFailures);
        Assert.Equal(0, backoff.CooldownRemainingMs(30_000)); // that cooldown ended long ago
        Assert.Equal(4_000, backoff.RetryDueAtMs);
        Assert.Equal(OverlayLaunchDecision.Attempt, backoff.OnLaunchWanted(30_000, OverlayDemand.Sustained));
    }

    [Fact]
    public void Ending_the_helper_on_purpose_is_never_a_failure()
    {
        var backoff = new OverlayLaunchBackoff();
        backoff.OnLaunchSucceeded(0);

        backoff.OnHelperStopped(); // idle suspend or exit

        Assert.Null(backoff.OnHelperLost(100, OverlayDemand.Sustained));
        Assert.Equal(0, backoff.ConsecutiveFailures);
        Assert.Equal(OverlayLaunchDecision.Attempt, backoff.OnLaunchWanted(100, OverlayDemand.Sustained));
    }

    [Fact]
    public void Losing_a_helper_that_never_launched_changes_nothing()
    {
        var backoff = new OverlayLaunchBackoff();

        Assert.Null(backoff.OnHelperLost(0, OverlayDemand.Sustained));
        Assert.Equal(0, backoff.ConsecutiveFailures);
        Assert.Equal(OverlayLaunchDecision.Attempt, backoff.OnLaunchWanted(0, OverlayDemand.Sustained));
    }

    [Fact]
    public void Rejects_a_schedule_that_cannot_back_off()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OverlayLaunchBackoff(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OverlayLaunchBackoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OverlayLaunchBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1)));
    }
}
