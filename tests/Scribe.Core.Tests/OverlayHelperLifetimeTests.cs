using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// Scripted, fake-clock tests of every lifetime decision the overlay client's command consumer carries out.
/// The <see cref="Consumer"/> driver plays both sides exactly the way <c>OverlayProcessClient</c> does:
/// a producer publishes the latest demand, then stamps, then enqueues; the consumer wakes only at
/// <see cref="OverlayHelperLifetime.NextWakeAtMs"/> or when a command arrives, reads the live demand at the
/// moment of each decision, and carries each decision out against a simulated helper whose launch outcomes
/// and crashes the test scripts.
/// </summary>
public sealed class OverlayHelperLifetimeTests
{
    // The shipped keep-warm default (ReleaseModelsAfterIdleMinutes = 10), in milliseconds.
    private const long Idle = 10 * 60_000;

    [Fact]
    public void A_sustained_recording_across_several_idle_periods_is_never_suspended()
    {
        var consumer = new Consumer();
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        Assert.Equal(OverlayHelperStatus.Alive, consumer.HelperStatus);

        // A long toggle-mode recording sends nothing but unstamped level meters.
        for (var t = 1_000L; t <= 5 * Idle; t += 1_000)
        {
            consumer.AdvanceTo(t);
            consumer.Meter();
        }

        Assert.Empty(consumer.At("Suspend"));
        Assert.Equal(OverlayHelperStatus.Alive, consumer.HelperStatus);

        consumer.Show("HIDE", OverlayDemand.None, ensureAlive: false, cancelsRetry: true);
        consumer.Drain();
        var hiddenAt = consumer.NowMs;
        consumer.AdvanceTo(hiddenAt + 2 * Idle);

        Assert.Equal([hiddenAt + Idle], consumer.At("Suspend"));
        Assert.Equal(OverlayHelperStatus.Absent, consumer.HelperStatus);
    }

    [Fact]
    public void A_failed_recording_launch_then_processing_inside_the_cooldown_retries_exactly_once_at_its_end()
    {
        var consumer = new Consumer();
        consumer.FailNextLaunches(1);

        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain(); // launch fails at t=0: a 1 s cooldown with one retry pending at its end
        consumer.AdvanceTo(400);
        consumer.Show("PROCESSING 0", OverlayDemand.Sustained);
        consumer.Drain();

        Assert.Equal([400L], consumer.At("Hold"));

        // Processing sends no meters, so nothing but the retry itself can wake the consumer.
        consumer.AdvanceTo(20_000);

        Assert.Equal([1_000L], consumer.At("Retry"));
        Assert.Equal([0L, 1_000L], consumer.At("Launch"));
        Assert.Equal(OverlayHelperStatus.Alive, consumer.HelperStatus);
        Assert.Equal(0, consumer.Lifetime.ConsecutiveFailures);
    }

    [Fact]
    public void An_engine_hide_inside_the_cooldown_cancels_the_pending_retry()
    {
        var consumer = new Consumer();
        consumer.FailNextLaunches(1);
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        consumer.AdvanceTo(400);
        consumer.Show("PROCESSING 0", OverlayDemand.Sustained);
        consumer.Drain();

        consumer.AdvanceTo(500);
        consumer.Show("HIDE", OverlayDemand.None, ensureAlive: false, cancelsRetry: true);
        consumer.Drain();
        consumer.AdvanceTo(20_000);

        Assert.Empty(consumer.At("Retry"));
        Assert.Null(consumer.Lifetime.RetryDueAtMs);
        Assert.Equal([0L], consumer.At("Launch"));
    }

    [Fact]
    public void Exit_with_a_retry_pending_drops_it_and_leaves_nothing_due()
    {
        var consumer = new Consumer();
        consumer.FailNextLaunches(1);
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        Assert.Equal(1_000, consumer.Lifetime.RetryDueAtMs);

        consumer.AdvanceTo(500);
        consumer.Exit();

        Assert.Null(consumer.Lifetime.RetryDueAtMs);
        Assert.Null(consumer.Lifetime.NextWakeAtMs());
        consumer.AdvanceTo(10 * Idle);
        Assert.Empty(consumer.At("Retry"));
        Assert.Empty(consumer.At("Suspend"));

        // Commands still queued behind EXIT are dropped, never relaunching a helper.
        consumer.Show("PROCESSING 0", OverlayDemand.Sustained);
        consumer.Drain();
        Assert.Equal([0L], consumer.At("Launch"));
        Assert.Equal([10 * Idle], consumer.At("Drop"));
    }

    [Fact]
    public void Exit_during_a_connect_is_not_a_failure_and_leaves_nothing_due()
    {
        // CloseOverlay cancels the connect; the launch comes back abandoned, then EXIT is taken.
        var consumer = new Consumer();
        consumer.NextLaunch(OverlayLaunchResult.Abandoned, takesMs: 50);

        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();

        Assert.Equal(0, consumer.Lifetime.ConsecutiveFailures);
        Assert.Equal(0, consumer.Lifetime.CooldownRemainingMs(consumer.NowMs));
        Assert.Null(consumer.Lifetime.RetryDueAtMs);

        consumer.Exit();
        consumer.AdvanceTo(10 * Idle);

        Assert.Null(consumer.Lifetime.NextWakeAtMs());
        Assert.Equal([0L], consumer.At("Launch"));
        Assert.Empty(consumer.At("Retry"));
    }

    [Fact]
    public void Exit_after_a_connect_that_failed_as_the_close_landed_still_leaves_nothing_due()
    {
        // The close raced a connect that was failing anyway: the failure schedules a retry, EXIT drops it.
        var consumer = new Consumer();
        consumer.NextLaunch(OverlayLaunchResult.Failed, takesMs: 8_000);

        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        Assert.Equal(9_000, consumer.Lifetime.RetryDueAtMs);

        consumer.Exit();
        consumer.AdvanceTo(10 * Idle);

        Assert.Empty(consumer.At("Retry"));
        Assert.Null(consumer.Lifetime.NextWakeAtMs());
    }

    [Fact]
    public void A_retry_whose_launch_is_abandoned_by_the_close_leaves_nothing_due()
    {
        var consumer = new Consumer();
        consumer.FailNextLaunches(1);
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();

        consumer.NextLaunch(OverlayLaunchResult.Abandoned, takesMs: 30);
        consumer.AdvanceTo(1_000);
        Assert.Equal([1_000L], consumer.At("Retry"));

        consumer.Exit();
        consumer.AdvanceTo(10 * Idle);
        Assert.Single(consumer.At("Retry"));
        Assert.Null(consumer.Lifetime.NextWakeAtMs());
    }

    [Fact]
    public void A_recording_after_an_idle_suspend_launches_the_helper_again()
    {
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();

        consumer.AdvanceTo(Idle);
        Assert.Equal([Idle], consumer.At("Suspend"));
        Assert.Equal(OverlayHelperStatus.Absent, consumer.HelperStatus);

        consumer.AdvanceTo(Idle + 5);
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();

        Assert.Equal([0L, Idle + 5], consumer.At("Launch"));
        Assert.Equal([Idle + 5], consumer.At("Write RECORDING"));
    }

    [Fact]
    public void A_command_stamped_before_the_commit_point_keeps_the_warm_helper()
    {
        // The outcome is stamped and queued while the consumer runs the due suspend. It does not keep the pill
        // on screen before it is shown, so only its stamp can veto the suspend and let it reach the helper that is still warm.
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();

        consumer.AdvanceTo(Idle - 1);
        consumer.Show("NOTHINGTYPED Try again", OverlayDemand.Transient); // queued, not taken yet
        consumer.AdvanceTo(Idle);
        consumer.Drain();

        Assert.Empty(consumer.At("Suspend"));
        Assert.Equal([0L], consumer.At("Launch"));
        Assert.Equal([Idle], consumer.At("Write NOTHINGTYPED Try again"));
    }

    [Fact]
    public void A_recording_that_starts_as_the_idle_period_ends_keeps_its_pill_in_every_interleaving()
    {
        // Producer order: publish the demand, stamp, enqueue. Consumer order at the commit: stamp, then demand.

        // Stamped before the commit point: the stamp vetoes it and the warm helper shows the recording.
        var stamped = ArmedAt0();
        stamped.AdvanceTo(Idle - 1);
        stamped.Show("RECORDING", OverlayDemand.Sustained);
        stamped.AdvanceTo(Idle);
        stamped.Drain();
        Assert.Empty(stamped.At("Suspend"));
        Assert.Equal([Idle], stamped.At("Write RECORDING"));

        // Published but not stamped yet: the demand vetoes it.
        var published = ArmedAt0();
        published.AdvanceTo(Idle - 1);
        published.PublishOnly(OverlayDemand.Sustained);
        published.AdvanceTo(Idle);
        published.StampAndEnqueue("RECORDING");
        published.Drain();
        Assert.Empty(published.At("Suspend"));
        Assert.Equal([Idle], published.At("Write RECORDING"));

        // Neither yet: the suspend commits before the recording exists, and its RECORDING relaunches the pill.
        var neither = ArmedAt0();
        neither.AdvanceTo(Idle);
        neither.Show("RECORDING", OverlayDemand.Sustained);
        neither.Drain();
        Assert.Equal([Idle], neither.At("Suspend"));
        Assert.Equal([0L, Idle], neither.At("Launch"));
        Assert.Equal(OverlayHelperStatus.Alive, neither.HelperStatus);

        static Consumer ArmedAt0()
        {
            var consumer = new Consumer();
            consumer.Warmup();
            consumer.Drain();
            return consumer;
        }
    }

    [Fact]
    public void The_wake_time_is_the_earlier_of_the_retry_and_the_idle_deadline()
    {
        // Retry first: a failed launch at t=0 under the default ten minute idle period.
        var retryFirst = new Consumer();
        retryFirst.FailNextLaunches(1);
        retryFirst.Show("RECORDING", OverlayDemand.Sustained);
        retryFirst.Drain();
        Assert.Equal(1_000, retryFirst.Lifetime.NextWakeAtMs());

        // Idle deadline first: a half second idle period, the same failure.
        var idleFirst = new Consumer(idleMs: 500);
        idleFirst.FailNextLaunches(1);
        idleFirst.Show("RECORDING", OverlayDemand.Sustained);
        idleFirst.Drain();
        Assert.Equal(500, idleFirst.Lifetime.NextWakeAtMs());

        // The recording vetoes the suspend and looks again a period later; the retry still fires on time.
        idleFirst.AdvanceTo(2_000);
        Assert.Empty(idleFirst.At("Suspend"));
        Assert.Equal([1_000L], idleFirst.At("Retry"));
    }

    [Fact]
    public void Due_work_always_moves_the_next_wake_past_now()
    {
        // The driver asserts this after every wake; this script visits every kind of due work.
        var consumer = new Consumer(idleMs: 2_000);
        consumer.FailNextLaunches(2);
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();                 // fails: retry at 1000
        consumer.AdvanceTo(1_500);        // the retry fails again: next retry at 3000
        consumer.AdvanceTo(4_000);        // idle veto at 2000 under the recording, then the retry at 3000 succeeds
        consumer.Show("HIDE", OverlayDemand.None, ensureAlive: false, cancelsRetry: true);
        consumer.Drain();
        consumer.AdvanceTo(10_000);       // idle suspend

        Assert.Equal([1_000L, 3_000L], consumer.At("Retry"));
        Assert.Equal([6_000L], consumer.At("Suspend"));
        Assert.True(consumer.Wakes > 0);
    }

    [Fact]
    public void An_outcome_never_earns_a_retry()
    {
        var consumer = new Consumer();
        consumer.FailNextLaunches(1);
        consumer.Show("NOTHINGTYPED Try again", OverlayDemand.Transient);
        consumer.Drain();

        Assert.Null(consumer.Lifetime.RetryDueAtMs);
        consumer.AdvanceTo(Idle / 2);
        Assert.Empty(consumer.At("Retry"));
    }

    [Fact]
    public void A_retry_is_dropped_when_the_latest_state_no_longer_keeps_the_pill_on_screen()
    {
        var consumer = new Consumer();
        consumer.FailNextLaunches(1);
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        consumer.AdvanceTo(300);
        consumer.Show("NOTHINGTYPED Try again", OverlayDemand.Transient); // held back: the cooldown is still running
        consumer.Drain();

        consumer.AdvanceTo(5_000);

        Assert.Empty(consumer.At("Retry"));
        Assert.Equal([0L], consumer.At("Launch"));
    }

    [Fact]
    public void Keep_warm_zero_never_suspends_and_a_changed_period_applies_to_an_armed_deadline()
    {
        var consumer = new Consumer(idleMs: 0);
        consumer.Warmup();
        consumer.Drain();
        consumer.AdvanceTo(100 * Idle);
        Assert.Empty(consumer.At("Suspend"));
        Assert.Null(consumer.Lifetime.NextWakeAtMs());

        consumer.Lifetime.SetIdlePeriodMs(60_000); // armed at the warmup, long ago: due at once
        Assert.Equal(60_000, consumer.Lifetime.NextWakeAtMs());
        consumer.AdvanceTo(consumer.NowMs);
        Assert.Equal([100 * Idle], consumer.At("Suspend"));
    }

    [Fact]
    public void A_pause_release_ends_an_idle_helper_at_once()
    {
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();

        consumer.AdvanceTo(30_000);
        consumer.Show("HIDE", OverlayDemand.None, ensureAlive: false, cancelsRetry: true); // the Paused state
        consumer.RequestRelease();
        consumer.Drain();

        Assert.Equal([30_000L], consumer.At("Release"));
        Assert.Equal(OverlayHelperStatus.Absent, consumer.HelperStatus);
        Assert.Null(consumer.Lifetime.NextWakeAtMs());

        // Resuming and recording brings the pill straight back.
        consumer.AdvanceTo(40_000);
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        Assert.Equal([0L, 40_000L], consumer.At("Launch"));
    }

    [Fact]
    public void A_pause_release_is_vetoed_by_a_command_stamped_after_it()
    {
        // Resuming raises Idle, whose HIDE is stamped after the release. It keeps nothing on screen, so only
        // its stamp can veto the release, and the next recording finds the helper still warm.
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();

        consumer.RequestRelease();
        consumer.Show("HIDE", OverlayDemand.None, ensureAlive: false, cancelsRetry: true);
        consumer.Drain();

        Assert.Empty(consumer.At("Release"));
        Assert.Equal(OverlayHelperStatus.Alive, consumer.HelperStatus);

        consumer.AdvanceTo(5_000);
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        Assert.Equal([0L], consumer.At("Launch"));
        Assert.Equal([5_000L], consumer.At("Write RECORDING"));
    }

    [Fact]
    public void A_pause_release_is_vetoed_by_a_state_that_keeps_the_pill_on_screen()
    {
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();

        consumer.RequestRelease();
        consumer.PublishOnly(OverlayDemand.Sustained); // a recording published its state but has not stamped yet
        consumer.Drain();
        consumer.StampAndEnqueue("RECORDING");
        consumer.Drain();

        Assert.Empty(consumer.At("Release"));
        Assert.Equal([0L], consumer.At("Write RECORDING"));
    }

    [Fact]
    public void A_vetoed_pause_release_leaves_the_idle_deadline_working()
    {
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();
        consumer.RequestRelease();
        consumer.StampAndEnqueue("POSITION TopCenter", ensureAlive: false, carriesState: false); // a settings save raced the pause
        consumer.Drain();
        Assert.Empty(consumer.At("Release"));

        consumer.AdvanceTo(2 * Idle);
        Assert.Equal([Idle], consumer.At("Suspend"));
    }

    [Fact]
    public void A_helper_lost_early_but_noticed_late_is_judged_by_its_exit_time()
    {
        var consumer = new Consumer();
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();

        consumer.CrashHelperAt(3_000);   // the process exits; nothing notices while it is not written to
        consumer.AdvanceTo(30_000);
        consumer.Meter();                // the first meter written after the crash finds it lost

        Assert.Equal(new OverlayHelperLoss(3_000, 1_000), consumer.Lifetime.LastLoss);

        // Its one second cooldown ran from the crash and has long ended, so the pill comes back now.
        Assert.Equal([0L, 30_000L], consumer.At("Launch"));
        Assert.Equal([30_000L], consumer.At("Write METER"));
        Assert.Equal(OverlayHelperStatus.Alive, consumer.HelperStatus);
    }

    [Fact]
    public void A_helper_lost_early_and_noticed_at_once_comes_back_at_the_end_of_its_cooldown()
    {
        var consumer = new Consumer();
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();

        consumer.CrashHelperAt(3_000);
        consumer.AdvanceTo(3_500);
        consumer.Meter();

        Assert.Equal([3_500L], consumer.At("Hold"));
        Assert.Equal(4_000, consumer.Lifetime.RetryDueAtMs);
        consumer.AdvanceTo(10_000);
        Assert.Equal([4_000L], consumer.At("Retry"));
        Assert.Equal(OverlayHelperStatus.Alive, consumer.HelperStatus);
    }

    [Fact]
    public void A_helper_that_proved_stable_is_brought_back_at_once_by_whichever_command_notices()
    {
        var consumer = new Consumer();
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();

        consumer.CrashHelperAt(20_000);
        consumer.AdvanceTo(20_100);
        consumer.Meter();

        Assert.Equal(new OverlayHelperLoss(20_000, null), consumer.Lifetime.LastLoss);
        Assert.Equal([0L, 20_100L], consumer.At("Launch"));
        Assert.Equal(0, consumer.Lifetime.ConsecutiveFailures);
    }

    [Fact]
    public void A_helper_lost_while_hidden_is_discarded_and_not_relaunched()
    {
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();
        consumer.CrashHelperAt(60_000);

        consumer.AdvanceTo(70_000);
        consumer.StampAndEnqueue("POSITION TopCenter", ensureAlive: false, carriesState: false);
        consumer.Drain();

        Assert.Equal([70_000L], consumer.At("Drop"));
        Assert.Equal([0L], consumer.At("Launch"));
        Assert.Equal(OverlayHelperStatus.Absent, consumer.HelperStatus);
    }

    [Fact]
    public void A_meter_never_launches_a_helper_that_was_not_lost()
    {
        // The recording's launch failed and its retry is pending: meters in the cooldown do not add launches.
        var consumer = new Consumer();
        consumer.FailNextLaunches(1);
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();

        for (var t = 25L; t < 1_000; t += 25)
        {
            consumer.AdvanceTo(t);
            consumer.Meter();
        }

        Assert.Equal([0L], consumer.At("Launch"));
        consumer.AdvanceTo(1_000);
        Assert.Equal([0L, 1_000L], consumer.At("Launch"));
    }

    [Fact]
    public void A_failed_write_under_an_on_screen_state_relaunches_through_the_cooldown_gate()
    {
        var stable = new Consumer();
        stable.Show("RECORDING", OverlayDemand.Sustained);
        stable.Drain();
        stable.AdvanceTo(15_000);
        stable.FailWrite();
        Assert.Equal([0L, 15_000L], stable.At("Launch"));

        var early = new Consumer();
        early.Show("RECORDING", OverlayDemand.Sustained);
        early.Drain();
        early.AdvanceTo(2_000);
        early.FailWrite();
        Assert.Equal([2_000L], early.At("Hold"));
        early.AdvanceTo(5_000);
        Assert.Equal([3_000L], early.At("Retry"));

        var hidden = new Consumer();
        hidden.Warmup();
        hidden.Drain();
        hidden.AdvanceTo(15_000);
        hidden.FailWrite();
        Assert.Equal([0L], hidden.At("Launch"));
        Assert.Equal(OverlayHelperStatus.Absent, hidden.HelperStatus);
    }

    [Fact]
    public void A_dropped_stamped_command_does_not_hold_the_idle_suspend_back()
    {
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();
        consumer.DropStamped(); // a superseded preview step: taken, not carried out

        consumer.AdvanceTo(Idle);
        Assert.Equal([Idle], consumer.At("Suspend"));
    }

    // ---- Outcomes: a pill that hides itself after its hold ------------------------------------------

    // How long each outcome keeps the pill on screen once shown, its fade out included (PillOutcome.OnScreen).
    private static readonly long TypedOnScreen = (long)(PillTiming.TypedHold + PillTiming.FadeOut).TotalMilliseconds;
    private static readonly long NoticeOnScreen = (long)(PillTiming.NoticeHold + PillTiming.FadeOut).TotalMilliseconds;

    [Fact]
    public void A_pause_release_waits_until_a_typed_pill_has_faded()
    {
        var consumer = new Consumer();
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        consumer.AdvanceTo(5_000);

        // The dictation that the pause stopped returns to idle, paused: its outcome, then the release the pause asks for.
        consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
        consumer.RequestRelease();
        consumer.Drain();

        Assert.Empty(consumer.At("Release"));
        Assert.Equal(OverlayHelperStatus.Alive, consumer.HelperStatus);
        Assert.Equal(5_000 + TypedOnScreen, consumer.Lifetime.ReleaseDueAtMs);
        Assert.Equal(5_000 + TypedOnScreen, consumer.Lifetime.NextWakeAtMs());

        consumer.AdvanceTo(20_000);

        Assert.Equal([5_000 + TypedOnScreen], consumer.At("Release"));
        Assert.Equal(OverlayHelperStatus.Absent, consumer.HelperStatus);
        Assert.Null(consumer.Lifetime.ReleaseDueAtMs);
        Assert.Null(consumer.Lifetime.NextWakeAtMs());
    }

    [Fact]
    public void A_pause_release_waits_for_an_error_notice_s_whole_hold()
    {
        var consumer = new Consumer();
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        consumer.AdvanceTo(3_000);

        consumer.Show("NOTHINGTYPED Copy it from the tray menu", OverlayDemand.Transient, showsForMs: NoticeOnScreen);
        consumer.RequestRelease();
        consumer.Drain();

        consumer.AdvanceTo(3_000 + NoticeOnScreen - 1);
        Assert.Empty(consumer.At("Release"));
        Assert.Equal(OverlayHelperStatus.Alive, consumer.HelperStatus);

        consumer.AdvanceTo(3_000 + NoticeOnScreen);
        Assert.Equal([3_000 + NoticeOnScreen], consumer.At("Release"));
    }

    [Fact]
    public void A_pause_release_after_the_outcome_has_gone_ends_the_helper_at_once()
    {
        var justBefore = WithTypedPillAt(1_000);
        justBefore.AdvanceTo(1_000 + TypedOnScreen - 1);
        justBefore.RequestRelease();
        justBefore.Drain();
        Assert.Empty(justBefore.At("Release"));
        Assert.Equal(1_000 + TypedOnScreen, justBefore.Lifetime.ReleaseDueAtMs);

        var atTheEnd = WithTypedPillAt(1_000);
        atTheEnd.AdvanceTo(1_000 + TypedOnScreen);
        atTheEnd.RequestRelease();
        atTheEnd.Drain();
        Assert.Equal([1_000 + TypedOnScreen], atTheEnd.At("Release"));
        Assert.Null(atTheEnd.Lifetime.ReleaseDueAtMs);

        static Consumer WithTypedPillAt(long atMs)
        {
            var consumer = new Consumer();
            consumer.Warmup();
            consumer.Drain();
            consumer.AdvanceTo(atMs);
            consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
            consumer.Drain();
            return consumer;
        }
    }

    [Fact]
    public void A_waiting_pause_release_is_vetoed_by_whatever_would_veto_it_at_once()
    {
        // A command stamped after the request: resuming raises Idle, then the next recording starts.
        var stamped = WaitingRelease();
        stamped.AdvanceTo(300);
        stamped.Show("HIDE", OverlayDemand.None, ensureAlive: false, cancelsRetry: true);
        stamped.Show("RECORDING", OverlayDemand.Sustained);
        stamped.Drain();
        stamped.AdvanceTo(10 * NoticeOnScreen);
        Assert.Empty(stamped.At("Release"));
        Assert.Equal(OverlayHelperStatus.Alive, stamped.HelperStatus);

        // A recording published but not stamped yet when the wait ends: its demand vetoes it.
        var published = WaitingRelease();
        published.AdvanceTo(NoticeOnScreen - 1);
        published.PublishOnly(OverlayDemand.Sustained);
        published.AdvanceTo(NoticeOnScreen);
        published.StampAndEnqueue("RECORDING");
        published.Drain();
        Assert.Empty(published.At("Release"));
        Assert.Equal([NoticeOnScreen], published.At("Write RECORDING"));

        static Consumer WaitingRelease()
        {
            var consumer = new Consumer();
            consumer.Warmup();
            consumer.Drain();
            consumer.Show("NOTHINGTYPED Try again", OverlayDemand.Transient, showsForMs: NoticeOnScreen);
            consumer.RequestRelease();
            consumer.Drain();
            Assert.Equal(NoticeOnScreen, consumer.Lifetime.ReleaseDueAtMs);
            return consumer;
        }
    }

    [Fact]
    public void A_newer_pause_release_takes_the_place_of_the_waiting_one()
    {
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();
        consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
        consumer.RequestRelease();
        consumer.Drain();
        consumer.AdvanceTo(100);
        consumer.RequestRelease(); // paused, resumed and paused again inside the hold, with nothing shown between
        consumer.Drain();

        consumer.AdvanceTo(10_000);

        Assert.Equal([TypedOnScreen], consumer.At("Release"));
    }

    [Fact]
    public void Exit_drops_a_waiting_pause_release()
    {
        var consumer = new Consumer();
        consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
        consumer.RequestRelease();
        consumer.Drain();
        Assert.NotNull(consumer.Lifetime.ReleaseDueAtMs);

        consumer.Exit();

        Assert.Null(consumer.Lifetime.ReleaseDueAtMs);
        Assert.Null(consumer.Lifetime.NextWakeAtMs());
        consumer.AdvanceTo(10_000);
        Assert.Empty(consumer.At("Release"));
    }

    [Fact]
    public void The_idle_suspend_waits_for_an_outcome_still_on_screen()
    {
        // An idle period shorter than the notice (the setting is in minutes; the rule must not depend on that).
        var consumer = new Consumer(idleMs: 300);
        consumer.Show("NOTHINGTYPED Copy it from the tray menu", OverlayDemand.Transient, showsForMs: NoticeOnScreen);
        consumer.Drain();

        consumer.AdvanceTo(NoticeOnScreen - 1);
        Assert.Empty(consumer.At("Suspend"));
        Assert.Equal(OverlayHelperStatus.Alive, consumer.HelperStatus);

        consumer.AdvanceTo(10_000);
        Assert.Equal([NoticeOnScreen], consumer.At("Suspend"));
    }

    [Fact]
    public void An_outcome_that_launched_the_helper_is_on_screen_from_its_write()
    {
        // The launch takes 2 s and the write that follows it 300 ms: the overlay has the line, and starts its hold, only then.
        var consumer = new Consumer();
        consumer.NextLaunch(OverlayLaunchResult.Launched, takesMs: 2_000);
        consumer.NextWrite(takesMs: 300);

        consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
        consumer.RequestRelease();
        consumer.Drain();

        Assert.Equal([2_300L], consumer.At("Write TYPED"));
        Assert.Equal(2_300 + TypedOnScreen, consumer.Lifetime.ReleaseDueAtMs);
        consumer.AdvanceTo(10_000);
        Assert.Equal([2_300 + TypedOnScreen], consumer.At("Release"));
    }

    [Fact]
    public void A_slow_write_starts_the_outcome_s_hold_when_it_returns()
    {
        // Astra's sequence: pausing a recording queues its TYPED, then the release. The write succeeds in 1 s, inside the
        // client's 1.5 s timeout, so the overlay starts its 400 ms hold at 6 s; a hold counted from 5 s would have let the
        // release end the helper at 6 s, before the check was ever seen.
        var consumer = new Consumer();
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        consumer.AdvanceTo(5_000);

        consumer.NextWrite(takesMs: 1_000);
        consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
        consumer.RequestRelease();
        consumer.Drain();

        Assert.Equal([6_000L], consumer.At("Write TYPED"));
        Assert.Empty(consumer.At("Release"));
        Assert.Equal(6_000 + TypedOnScreen, consumer.Lifetime.ReleaseDueAtMs);

        consumer.AdvanceTo(20_000);
        Assert.Equal([6_000 + TypedOnScreen], consumer.At("Release"));
    }

    [Fact]
    public void A_slow_write_starts_a_notice_s_hold_when_it_returns()
    {
        var consumer = new Consumer();
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        consumer.AdvanceTo(3_000);

        consumer.NextWrite(takesMs: 1_400);
        consumer.Show("NOTHINGTYPED Copy it from the tray menu", OverlayDemand.Transient, showsForMs: NoticeOnScreen);
        consumer.RequestRelease();
        consumer.Drain();

        consumer.AdvanceTo(4_400 + NoticeOnScreen - 1);
        Assert.Empty(consumer.At("Release"));
        consumer.AdvanceTo(4_400 + NoticeOnScreen);
        Assert.Equal([4_400 + NoticeOnScreen], consumer.At("Release"));
    }

    [Fact]
    public void The_idle_suspend_counts_an_outcome_s_hold_from_its_write()
    {
        var consumer = new Consumer(idleMs: 300);
        consumer.Warmup();
        consumer.Drain();

        consumer.NextWrite(takesMs: 1_000);
        consumer.Show("NOTHINGTYPED Copy it from the tray menu", OverlayDemand.Transient, showsForMs: NoticeOnScreen);
        consumer.Drain();

        consumer.AdvanceTo(1_000 + NoticeOnScreen - 1);
        Assert.Empty(consumer.At("Suspend"));
        consumer.AdvanceTo(10_000);
        Assert.Equal([1_000 + NoticeOnScreen], consumer.At("Suspend"));
    }

    [Fact]
    public void An_outcome_whose_write_failed_keeps_nothing_on_screen()
    {
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();
        consumer.AdvanceTo(20_000); // stable, so the loss starts no cooldown

        consumer.NextWrite(takesMs: 200, fails: true);
        consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
        consumer.RequestRelease();
        consumer.Drain();

        Assert.Equal([20_200L], consumer.At("Write failed TYPED"));
        Assert.Null(consumer.Lifetime.ReleaseDueAtMs);
        Assert.Equal(OverlayHelperStatus.Absent, consumer.HelperStatus);
        Assert.Equal([0L], consumer.At("Launch")); // an outcome never brings a lost helper back
    }

    // ---- A state command a newer state replaced ------------------------------------------------------

    [Fact]
    public void An_outcome_a_newer_recording_replaced_during_its_launch_is_not_written_over_it()
    {
        // Astra's sequence: the helper is lost during cleanup, dictation A's outcome launches a replacement, and dictation B
        // starts while that launch connects. The launch gives the new helper B's recording; A's outcome, taken before B
        // started, must not be written after it, or "Typed" covers B's live recording until B's own command arrives.
        var consumer = new Consumer();
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        consumer.AdvanceTo(1_000);
        consumer.Show("PROCESSING 1", OverlayDemand.Sustained);
        consumer.Drain();
        consumer.CrashHelperAt(2_000);

        consumer.AdvanceTo(15_000);
        consumer.NextLaunch(
            OverlayLaunchResult.Launched, takesMs: 500, during: () => consumer.Show("RECORDING", OverlayDemand.Sustained));
        consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
        consumer.Drain();

        Assert.Equal(
            ["Launch", "Replay POSITION BottomCenter", "Replay RECORDING", "Skip TYPED", "Write RECORDING"],
            consumer.WhatSince(15_000));

        // Nothing of A's outcome is on screen, so a pause that ends B does not wait for it.
        consumer.Show("HIDE", OverlayDemand.None, ensureAlive: false, cancelsRetry: true);
        consumer.RequestRelease();
        consumer.Drain();
        Assert.Equal([15_500L], consumer.At("Release"));
    }

    [Fact]
    public void A_state_command_a_newer_state_replaced_before_it_was_taken_launches_nothing()
    {
        // The helper is absent, and the outcome is replaced (the pill was switched off) before the consumer takes it.
        var consumer = new Consumer();
        consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
        consumer.Show("HIDE", OverlayDemand.None, ensureAlive: false, cancelsRetry: true);
        consumer.Drain();

        Assert.Empty(consumer.At("Launch"));
        Assert.Equal(["Drop", "Drop"], consumer.WhatSince(0));
    }

    [Fact]
    public void A_recording_and_its_warning_replaced_during_their_launch_are_not_written_over_the_processing()
    {
        // Every state command has the outcome's shape: a short recording's RECORDING launches the helper, a warning on it is
        // queued behind, and the processing that replaced both is published while the launch connects.
        var consumer = new Consumer();
        consumer.NextLaunch(
            OverlayLaunchResult.Launched, takesMs: 300, during: () => consumer.Show("PROCESSING 0", OverlayDemand.Sustained));
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Warn("Microphone muted");
        consumer.Drain();

        Assert.Equal(
            [
                "Launch", "Replay POSITION BottomCenter", "Replay PROCESSING 0", "Skip RECORDING", "Skip WARNING Microphone muted",
                "Write PROCESSING 0",
            ],
            consumer.WhatSince(0));
    }

    [Fact]
    public void A_warning_belongs_to_the_live_recording_and_is_written_after_it()
    {
        var consumer = new Consumer();
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Warn("Microphone muted");
        consumer.Drain();

        Assert.Equal(
            ["Launch", "Replay POSITION BottomCenter", "Replay RECORDING", "Write RECORDING", "Write WARNING Microphone muted"],
            consumer.WhatSince(0));
    }

    [Fact]
    public void A_moved_anchor_s_state_line_that_a_newer_state_replaced_is_not_written()
    {
        // A settings save moves the anchor while an outcome shows; the next recording starts before the consumer takes
        // the save's commands. The anchor is written; the outcome's replay line is not, so the recording follows at once.
        var consumer = new Consumer();
        consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
        consumer.Drain();
        consumer.AdvanceTo(100);

        consumer.MoveAnchor("TopCenter");
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();

        Assert.Equal(["Write POSITION TopCenter", "Skip HIDE", "Write RECORDING"], consumer.WhatSince(100));
    }

    // ---- A preview command superseded during its launch (Astra's A4) --------------------------------------------------

    [Fact]
    public void A_preview_step_superseded_during_its_launch_is_not_written_over_the_state_the_launch_replayed()
    {
        // Astra's A4. A preview's anchor is written, then the helper dies. The preview's recording look passes the gate and
        // launches a replacement; meanwhile a dictation starts and reaches processing, which supersedes the preview. The
        // launch gives the new helper that processing, and the look, taken before, must not follow it, or the pill goes
        // back to Listening over Processing until the engine's own command arrives.
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();
        consumer.AdvanceTo(20_000);
        consumer.Preview("TopLeft");
        consumer.TakeNext();
        consumer.CrashHelperAt(20_000);

        consumer.NextLaunch(OverlayLaunchResult.Launched, takesMs: 500, during: () =>
        {
            consumer.Show("RECORDING", OverlayDemand.Sustained);
            consumer.Show("PROCESSING 0", OverlayDemand.Sustained);
        });
        consumer.Drain();

        Assert.Equal(
            [
                "Write preview POSITION TopLeft", "Launch", "Replay POSITION BottomCenter", "Replay PROCESSING 0",
                "Skip preview RECORDING", "Write POSITION BottomCenter", "Skip RECORDING", "Write PROCESSING 0",
            ],
            consumer.WhatSince(20_000));
    }

    [Fact]
    public void A_preview_anchor_superseded_during_its_own_launch_is_not_written_over_the_applied_anchor()
    {
        // Astra's simpler variant: the candidate anchor's own command launches the helper, the launch gives it the applied
        // anchor, and a recording starts meanwhile. The candidate must not follow, or the recording shows at the previewed
        // position until the engine's command restores the anchor.
        var consumer = new Consumer();
        consumer.NextLaunch(
            OverlayLaunchResult.Launched, takesMs: 300, during: () => consumer.Show("RECORDING", OverlayDemand.Sustained));
        consumer.Preview("TopLeft");
        consumer.Drain();

        Assert.Equal(
            [
                "Launch", "Replay POSITION BottomCenter", "Replay RECORDING", "Skip preview POSITION TopLeft",
                "Gate drop preview RECORDING", "Write POSITION BottomCenter", "Write RECORDING",
            ],
            consumer.WhatSince(0));
    }

    [Fact]
    public void A_newer_preview_begun_during_an_older_one_s_launch_is_the_only_one_shown()
    {
        var consumer = new Consumer();
        long second = 0;
        consumer.NextLaunch(OverlayLaunchResult.Launched, takesMs: 300, during: () => second = consumer.Preview("TopRight"));
        var first = consumer.Preview("TopLeft");
        consumer.Drain();
        consumer.PreviewEnd(first); // the older sweep queues nothing once superseded
        consumer.PreviewEnd(second);
        consumer.Drain();

        Assert.Equal(
            [
                "Launch", "Replay POSITION BottomCenter", "Replay HIDE", "Skip preview POSITION TopLeft",
                "Gate drop preview RECORDING", "Write preview POSITION TopRight", "Write preview RECORDING",
                "Write preview end HIDE", "Write preview end POSITION BottomCenter",
            ],
            consumer.WhatSince(0));
    }

    [Fact]
    public void A_preview_end_superseded_during_its_launch_leaves_the_anchor_to_the_engine_command()
    {
        // A preview over a live recording ends while the helper is lost; its end brings the helper back for the recording,
        // and the recording stops meanwhile. The end is turned away and the processing's own command puts the applied
        // anchor back, as after any superseded preview.
        var consumer = new Consumer();
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        consumer.AdvanceTo(20_000);
        var preview = consumer.Preview("TopLeft");
        consumer.Drain();
        consumer.PreviewEnd(preview);
        consumer.CrashHelperAt(20_000);

        consumer.NextLaunch(
            OverlayLaunchResult.Launched, takesMs: 500, during: () => consumer.Show("PROCESSING 0", OverlayDemand.Sustained));
        consumer.Drain();

        Assert.Equal(
            [
                "Write preview POSITION TopLeft", "Write preview RECORDING", "Launch", "Replay POSITION BottomCenter",
                "Replay PROCESSING 0", "Skip preview end", "Write POSITION BottomCenter", "Write PROCESSING 0",
            ],
            consumer.WhatSince(20_000));
    }

    [Fact]
    public void A_preview_still_current_after_its_launch_is_written_whole()
    {
        var consumer = new Consumer();
        consumer.NextLaunch(OverlayLaunchResult.Launched, takesMs: 300);
        var preview = consumer.Preview("TopLeft");
        consumer.Drain();
        consumer.PreviewStep(preview, "METER 500");
        consumer.PreviewEnd(preview);
        consumer.Drain();

        Assert.Equal(
            [
                "Launch", "Replay POSITION BottomCenter", "Replay HIDE", "Write preview POSITION TopLeft", "Write preview RECORDING",
                "Write preview METER 500", "Write preview end HIDE", "Write preview end POSITION BottomCenter",
            ],
            consumer.WhatSince(0));
    }

    [Fact]
    public void An_outcome_that_was_never_shown_keeps_nothing_on_screen()
    {
        var consumer = new Consumer();
        consumer.FailNextLaunches(1);
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain(); // fails at 0: a 1 s cooldown
        consumer.AdvanceTo(100);

        consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen); // held back by the cooldown
        consumer.RequestRelease();
        consumer.Drain();

        Assert.Equal([100L], consumer.At("Hold"));
        Assert.Null(consumer.Lifetime.ReleaseDueAtMs);

        // Nor does one whose launch failed, even for the next launch that succeeds.
        var failed = new Consumer();
        failed.FailNextLaunches(1);
        failed.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
        failed.RequestRelease();
        failed.Drain();
        Assert.Null(failed.Lifetime.ReleaseDueAtMs);
        Assert.Null(failed.Lifetime.RetryDueAtMs);

        failed.AdvanceTo(1_000); // the cooldown is over
        failed.Show("RECORDING", OverlayDemand.Sustained);
        failed.Drain();
        failed.Show("HIDE", OverlayDemand.None, ensureAlive: false, cancelsRetry: true);
        failed.RequestRelease();
        failed.Drain();
        Assert.Equal([1_000L], failed.At("Release"));
        Assert.Null(failed.Lifetime.ReleaseDueAtMs);
    }

    [Fact]
    public void A_helper_that_is_gone_shows_no_outcome_for_a_release_to_wait_for()
    {
        var consumer = new Consumer();
        consumer.Show("NOTHINGTYPED Copy it from the tray menu", OverlayDemand.Transient, showsForMs: NoticeOnScreen);
        consumer.Drain();
        consumer.CrashHelperAt(100);

        consumer.AdvanceTo(200);
        consumer.RequestRelease();
        consumer.Drain();

        Assert.Null(consumer.Lifetime.ReleaseDueAtMs);
        Assert.Equal(OverlayHelperStatus.Absent, consumer.HelperStatus);
    }

    [Fact]
    public void A_helper_lost_under_an_outcome_is_not_brought_back_for_it()
    {
        // An outcome is shown once, by its own command, and never replayed: a relaunch for it would show nothing.
        var consumer = new Consumer();
        consumer.Show("TYPED", OverlayDemand.Transient, showsForMs: TypedOnScreen);
        consumer.Drain();
        consumer.CrashHelperAt(100);

        consumer.AdvanceTo(200);
        consumer.StampAndEnqueue("POSITION TopCenter", ensureAlive: false, carriesState: false); // a settings save; the latest state is unchanged
        consumer.Drain();

        Assert.Equal([0L], consumer.At("Launch"));
        Assert.Equal([200L], consumer.At("Drop"));
        Assert.Equal(OverlayHelperStatus.Absent, consumer.HelperStatus);
    }

    [Fact]
    public void A_failed_write_under_an_outcome_does_not_relaunch()
    {
        var consumer = new Consumer();
        consumer.Show("NOTHINGTYPED Copy it from the tray menu", OverlayDemand.Transient, showsForMs: NoticeOnScreen);
        consumer.Drain();
        consumer.AdvanceTo(500);

        consumer.FailWrite();

        Assert.Equal([0L], consumer.At("Launch"));
        Assert.Empty(consumer.At("Hold"));
        Assert.Null(consumer.Lifetime.RetryDueAtMs);
        Assert.Equal(OverlayHelperStatus.Absent, consumer.HelperStatus);

        // The next recording brings the pill back at once: the loss inside the stable window cooled down from 500.
        consumer.AdvanceTo(2_000);
        consumer.Show("RECORDING", OverlayDemand.Sustained);
        consumer.Drain();
        Assert.Equal([0L, 2_000L], consumer.At("Launch"));
    }

    [Fact]
    public void An_outcome_s_own_command_brings_back_a_helper_that_is_gone()
    {
        // Worth showing when the helper can be reached at once: the command that shows it may launch it.
        var consumer = new Consumer();
        consumer.Warmup();
        consumer.Drain();
        consumer.AdvanceTo(Idle); // suspended
        Assert.Equal(OverlayHelperStatus.Absent, consumer.HelperStatus);

        consumer.AdvanceTo(Idle + 10);
        consumer.Show("PARTLYTYPED Copy it from the tray menu", OverlayDemand.Transient, showsForMs: NoticeOnScreen);
        consumer.Drain();

        Assert.Equal([0L, Idle + 10], consumer.At("Launch"));
        Assert.Equal([Idle + 10], consumer.At("Write PARTLYTYPED Copy it from the tray menu"));
    }

    /// <summary>
    /// Plays the overlay client's consumer and its producers against a scripted clock and a simulated
    /// helper, recording each decision as "<c>What</c> at <c>time</c>".
    /// </summary>
    private sealed class Consumer
    {
        private readonly Queue<Pending> _queue = new();
        private readonly Queue<(OverlayLaunchResult Result, long TakesMs, Action? During)> _launches = new();
        private readonly Queue<(long TakesMs, bool Fails)> _writes = new();
        private readonly List<(long AtMs, string What)> _events = [];
        private OverlayHelperStatus _helper = OverlayHelperStatus.Absent;
        private long? _crashedAtMs;

        // The latest state a producer asked for, as OverlayProcessClient's _desired: every request is a state of its own,
        // and a state command carries the one it was made for, compared by reference.
        private RequestedState _latest = new("HIDE", OverlayDemand.None);

        // The applied anchor, as the client's _position: a relaunch replays it, and an anchor move writes it as it stands.
        private string _applied = "BottomCenter";

        public Consumer(long idleMs = Idle)
        {
            Lifetime.SetIdlePeriodMs(idleMs);
        }

        public OverlayHelperLifetime Lifetime { get; } = new();

        /// <summary>The client's real position-preview gate: engine requests supersede a preview, which a newer one also does.</summary>
        public OverlayPreviewGate Gate { get; } = new();

        public long NowMs { get; private set; }

        public int Wakes { get; private set; }

        public OverlayHelperStatus HelperStatus => _helper;

        /// <summary>The demand of the latest state a producer published; the consumer reads it at each decision.</summary>
        public OverlayDemand Demand => _latest.Demand;

        public long[] At(string what) => [.. _events.Where(e => e.What == what).Select(e => e.AtMs)];

        /// <summary>What happened from <paramref name="fromMs"/> on, in order.</summary>
        public string[] WhatSince(long fromMs) => [.. _events.Where(e => e.AtMs >= fromMs).Select(e => e.What)];

        public void FailNextLaunches(int count)
        {
            for (var i = 0; i < count; i++)
            {
                NextLaunch(OverlayLaunchResult.Failed);
            }
        }

        /// <summary>
        /// Scripts the next launch: its outcome, how long the consumer is blocked in it, and what producers do meanwhile
        /// (they keep publishing states and queuing commands while the new helper connects).
        /// </summary>
        public void NextLaunch(OverlayLaunchResult result, long takesMs = 0, Action? during = null) =>
            _launches.Enqueue((result, takesMs, during));

        /// <summary>
        /// Scripts the next write of a command to the helper: a pipe write can take up to the client's 1.5 s timeout and
        /// still succeed, or fail, which loses the helper. Unscripted writes take no time and succeed.
        /// </summary>
        public void NextWrite(long takesMs = 0, bool fails = false) => _writes.Enqueue((takesMs, fails));

        // ---- Producers ----------------------------------------------------------------------------

        /// <summary>
        /// ShowRecording and friends: publish a new state, stamp, enqueue its command. An outcome passes how long it stays
        /// on screen once shown, its fade out included (<see cref="PillOutcome.OnScreen"/>).
        /// </summary>
        public void Show(string line, OverlayDemand demand, bool ensureAlive = true, bool cancelsRetry = false, long showsForMs = 0)
        {
            PublishOnly(demand, line);
            StampAndEnqueue(line, ensureAlive, cancelsRetry, showsForMs);
        }

        /// <summary>
        /// A producer published a new state and has not stamped its command yet. As in the client, it supersedes any
        /// running preview first (CancelPreview).
        /// </summary>
        public void PublishOnly(OverlayDemand demand, string line = "RECORDING")
        {
            Gate.Supersede();
            _latest = new RequestedState(line, demand);
        }

        /// <summary>
        /// Stamps and queues a command for the state published last, or, with <paramref name="carriesState"/> false, one
        /// that shows no state of its own (a warmup, an anchor). <paramref name="appliedAnchor"/> marks the engine's anchor
        /// move, which writes the applied anchor as it stands when written.
        /// </summary>
        public void StampAndEnqueue(
            string line,
            bool ensureAlive = true,
            bool cancelsRetry = false,
            long showsForMs = 0,
            bool carriesState = true,
            bool appliedAnchor = false) =>
            _queue.Enqueue(new Pending(
                PendingKind.State,
                line,
                Lifetime.IssueStamp(),
                ensureAlive,
                cancelsRetry,
                showsForMs,
                carriesState ? _latest : null,
                AppliedAnchor: appliedAnchor));

        /// <summary>As OverlayProcessClient.Warmup: it launches the helper and asks for no state.</summary>
        public void Warmup() => StampAndEnqueue("WARMUP", carriesState: false);

        /// <summary>
        /// As OverlayProcessClient.ShowRecordingWarning: a warning belongs to the live recording, so it carries that
        /// recording's state; with no recording state published, it publishes one.
        /// </summary>
        public void Warn(string text)
        {
            Gate.Supersede();
            if (_latest.Line != "RECORDING")
            {
                PublishOnly(OverlayDemand.Sustained);
            }

            StampAndEnqueue("WARNING " + text);
        }

        /// <summary>
        /// As OverlayProcessClient.SetPosition: the applied anchor moves, then its anchor command (written as the applied
        /// anchor stands at the write) and the latest state's replay line, for that state.
        /// </summary>
        public void MoveAnchor(string anchor)
        {
            Gate.Supersede();
            _applied = anchor;
            StampAndEnqueue("POSITION " + anchor, ensureAlive: false, carriesState: false, appliedAnchor: true);
            StampAndEnqueue(_latest.ReplayLine, ensureAlive: false);
        }

        /// <summary>
        /// As OverlayProcessClient.Preview: a new preview supersedes any earlier one and queues its candidate anchor and its
        /// recording look, both needing the helper. It publishes no state: the engine's latest stays what it was.
        /// </summary>
        public long Preview(string candidate)
        {
            var generation = Gate.BeginPreview();
            EnqueuePreview(generation, OverlayPreviewRole.Anchor, "POSITION " + candidate, ensureAlive: true);
            EnqueuePreview(generation, OverlayPreviewRole.Step, "RECORDING", ensureAlive: true);
            return generation;
        }

        /// <summary>As the preview's sweep: one level step, queued only while its preview is current.</summary>
        public void PreviewStep(long generation, string line)
        {
            if (Gate.IsCurrent(generation))
            {
                EnqueuePreview(generation, OverlayPreviewRole.Step, line, ensureAlive: false);
            }
        }

        /// <summary>As the end of the preview's sweep: queued only while its preview is current.</summary>
        public void PreviewEnd(long generation)
        {
            if (Gate.IsCurrent(generation))
            {
                EnqueuePreview(generation, OverlayPreviewRole.End, string.Empty, ensureAlive: false);
            }
        }

        public void RequestRelease() =>
            _queue.Enqueue(new Pending(PendingKind.Release, "RELEASE", Lifetime.IssueStamp(), false, false, 0, null));

        public void CrashHelperAt(long atMs)
        {
            Assert.Equal(OverlayHelperStatus.Alive, _helper);
            _helper = OverlayHelperStatus.Lost;
            _crashedAtMs = atMs;
        }

        // ---- Consumer -----------------------------------------------------------------------------

        /// <summary>Takes everything queued, at the current time, as the consumer's queue wait would.</summary>
        public void Drain()
        {
            while (TakeNext())
            {
            }
        }

        /// <summary>
        /// Takes the next queued command, as OverlayProcessClient.HandleCommand and HandleState do: a state command passes
        /// the preview gate first (a superseded preview's step is dropped there, and the first engine command after one
        /// restores the applied anchor), then the lifetime decides. Returns false when nothing was queued.
        /// </summary>
        public bool TakeNext()
        {
            if (!_queue.TryDequeue(out var pending))
            {
                return false;
            }

            if (pending.Kind == PendingKind.Release)
            {
                var observed = Observe();
                var work = Lifetime.OnReleaseWhenIdle(NowMs, pending.Stamp, Demand, observed);
                DiscardIfLost(observed);
                if (work == OverlayDueWork.Release)
                {
                    Record("Release");
                    _helper = OverlayHelperStatus.Absent;
                }

                return true;
            }

            var verdict = Gate.OnCommand(pending.Role, pending.Generation);
            if (verdict == OverlayPreviewVerdict.Drop)
            {
                Lifetime.OnCommandDropped(pending.Stamp);
                Record("Gate drop " + Label(pending));
                return true;
            }

            var helper = Observe();
            var action = Lifetime.OnStateCommand(
                NowMs, pending.Stamp, pending.EnsureAlive, pending.CancelsRetry, Demand, helper, Superseded(pending));
            Carry(action, helper, pending, verdict);
            return true;
        }

        /// <summary>A live level meter taken at the current time.</summary>
        public void Meter()
        {
            var helper = Observe();
            Carry(Lifetime.OnMeter(NowMs, Demand, helper), helper, new Pending(PendingKind.State, "METER", 0, false, false, 0, null));
        }

        /// <summary>A stamped command taken and dropped without being carried out.</summary>
        public void DropStamped() => Lifetime.OnCommandDropped(Lifetime.IssueStamp());

        /// <summary>The next write to the running helper fails at the current time.</summary>
        public void FailWrite()
        {
            Assert.Equal(OverlayHelperStatus.Alive, _helper);
            var action = Lifetime.OnWriteFailed(NowMs, NowMs, Demand);
            _helper = OverlayHelperStatus.Absent;
            if (action == OverlayCommandAction.Launch)
            {
                Launch();
            }
            else if (action == OverlayCommandAction.Hold)
            {
                Record("Hold");
            }
        }

        public void Exit()
        {
            Lifetime.OnExit();
            _helper = OverlayHelperStatus.Absent;
            Record("Exit");
        }

        /// <summary>Lets time pass with no command arriving: the consumer wakes only when the lifetime says so.</summary>
        public void AdvanceTo(long untilMs)
        {
            while (Lifetime.NextWakeAtMs() is { } wakeMs && wakeMs <= untilMs)
            {
                NowMs = Math.Max(NowMs, wakeMs);
                Wakes++;
                RunDueWork();

                // The consumer re-enters its wait after due work; a wake still at or before now would spin it.
                Assert.True(Lifetime.NextWakeAtMs() is not { } nextMs || nextMs > NowMs, $"the consumer would spin at {NowMs} ms");
            }

            NowMs = Math.Max(NowMs, untilMs);
        }

        private void RunDueWork()
        {
            var helper = Observe();
            var work = Lifetime.TakeDueWork(NowMs, Demand, helper);
            DiscardIfLost(helper);
            switch (work)
            {
                case OverlayDueWork.Suspend:
                    Record("Suspend");
                    _helper = OverlayHelperStatus.Absent;
                    break;
                case OverlayDueWork.Release:
                    Record("Release");
                    _helper = OverlayHelperStatus.Absent;
                    break;
                case OverlayDueWork.Retry:
                    Record("Retry");
                    Launch();
                    break;
            }
        }

        private void Carry(
            OverlayCommandAction action,
            OverlayHelperObservation helper,
            Pending pending,
            OverlayPreviewVerdict verdict = OverlayPreviewVerdict.Deliver)
        {
            DiscardIfLost(helper);
            switch (action)
            {
                case OverlayCommandAction.Write:
                    Deliver(pending, verdict);
                    break;
                case OverlayCommandAction.Launch:
                    if (Launch())
                    {
                        Deliver(pending, verdict);
                    }

                    break;
                case OverlayCommandAction.Hold:
                    Record("Hold");
                    break;
                default:
                    Record("Drop");
                    break;
            }
        }

        // As OverlayProcessClient.HandleState after Prepare: a preview command superseded while it waited or while its launch
        // blocked is not written (ConfirmWrite), then come the anchor restore a superseded preview owes, a preview's end (the
        // applied anchor and the latest state, read at the write), and a command whose state a newer one replaced (while it
        // waited, or while its launch blocked, when the launch replayed the newer state) is not written; the newer state's
        // command is queued behind it. Otherwise the write takes the time the script gives it, an outcome is on screen from
        // when its write returns, and a write that fails loses the helper (RecoverFromFailedWrite), and the rest of the
        // command with it.
        private void Deliver(Pending pending, OverlayPreviewVerdict verdict)
        {
            if (!Gate.ConfirmWrite(pending.Role, pending.Generation))
            {
                Record("Skip " + Label(pending));
                return;
            }

            if (verdict == OverlayPreviewVerdict.RestoreAnchorThenDeliver && !Write("POSITION " + _applied))
            {
                return;
            }

            if (pending.Role == OverlayPreviewRole.End)
            {
                WritePreviewEnd();
                return;
            }

            if (Superseded(pending))
            {
                Record("Skip " + Label(pending));
                return;
            }

            var line = pending.AppliedAnchor ? "POSITION " + _applied : pending.Line;
            if (Write(line, pending.Role == OverlayPreviewRole.None ? line : "preview " + line) && pending.ShowsForMs > 0)
            {
                Lifetime.OnShown(NowMs, pending.ShowsForMs);
            }
        }

        // As OverlayProcessClient.WritePreviewEnd: a sustained state the preview covered comes back at the applied anchor;
        // otherwise the pill hides first and moves back while hidden.
        private void WritePreviewEnd()
        {
            var latest = _latest;
            if (latest.Demand == OverlayDemand.Sustained)
            {
                _ = Write("POSITION " + _applied, "preview end POSITION " + _applied) && Write(latest.Line, "preview end " + latest.Line);
            }
            else
            {
                _ = Write("HIDE", "preview end HIDE") && Write("POSITION " + _applied, "preview end POSITION " + _applied);
            }
        }

        // One WriteWithTimeout: it takes the time the script gives it, and a failure loses the helper.
        private bool Write(string line, string? label = null)
        {
            label ??= line;
            var (takesMs, fails) = _writes.Count > 0 ? _writes.Dequeue() : (0L, false);
            NowMs += takesMs; // the consumer is blocked in the write meanwhile
            if (!fails)
            {
                Record("Write " + label);
                return true;
            }

            Record("Write failed " + label);
            var action = Lifetime.OnWriteFailed(NowMs, NowMs, Demand);
            _helper = OverlayHelperStatus.Absent;
            if (action == OverlayCommandAction.Launch)
            {
                Launch();
            }
            else if (action == OverlayCommandAction.Hold)
            {
                Record("Hold");
            }

            return false;
        }

        private bool Launch()
        {
            Record("Launch");
            var (result, takesMs, during) = _launches.Count > 0
                ? _launches.Dequeue()
                : (OverlayLaunchResult.Launched, 0L, (Action?)null);
            during?.Invoke(); // producers keep asking for states while the consumer is blocked in the launch
            NowMs += takesMs;
            _helper = result == OverlayLaunchResult.Launched ? OverlayHelperStatus.Alive : OverlayHelperStatus.Absent;
            Lifetime.OnLaunchCompleted(NowMs, result, Demand);
            if (result == OverlayLaunchResult.Launched)
            {
                // TryLaunch gives the new helper the applied anchor and the latest state, as they stand once it connects.
                Record("Replay POSITION " + _applied);
                Record("Replay " + _latest.ReplayLine);
            }

            return result == OverlayLaunchResult.Launched;
        }

        private bool Superseded(Pending pending) => pending.State is { } state && !ReferenceEquals(state, _latest);

        private static string Label(Pending pending) => pending.Role switch
        {
            OverlayPreviewRole.None => pending.Line,
            OverlayPreviewRole.End => "preview end",
            _ => "preview " + pending.Line,
        };

        private void EnqueuePreview(long generation, OverlayPreviewRole role, string line, bool ensureAlive) =>
            _queue.Enqueue(new Pending(
                PendingKind.State, line, Lifetime.IssueStamp(), ensureAlive, false, 0, null, role, generation));

        private OverlayHelperObservation Observe() => _helper switch
        {
            OverlayHelperStatus.Alive => OverlayHelperObservation.Alive,
            OverlayHelperStatus.Lost => OverlayHelperObservation.Lost(_crashedAtMs ?? NowMs),
            _ => OverlayHelperObservation.Absent,
        };

        private void DiscardIfLost(OverlayHelperObservation helper)
        {
            if (helper.Status == OverlayHelperStatus.Lost)
            {
                _helper = OverlayHelperStatus.Absent;
                _crashedAtMs = null;
            }
        }

        private void Record(string what) => _events.Add((NowMs, what));

        private enum PendingKind
        {
            State,
            Release,
        }

        private readonly record struct Pending(
            PendingKind Kind,
            string Line,
            long Stamp,
            bool EnsureAlive,
            bool CancelsRetry,
            long ShowsForMs,
            RequestedState? State,
            OverlayPreviewRole Role = OverlayPreviewRole.None,
            long Generation = 0,
            bool AppliedAnchor = false);

        // A class, not a record: two requests for the same line are still two states.
        private sealed class RequestedState(string line, OverlayDemand demand)
        {
            public string Line { get; } = line;

            public OverlayDemand Demand { get; } = demand;

            /// <summary>What a relaunch replays for it: the state, unless it hides itself (DesiredState.ReplayLine).</summary>
            public string ReplayLine => Demand == OverlayDemand.Transient ? "HIDE" : Line;
        }
    }
}
