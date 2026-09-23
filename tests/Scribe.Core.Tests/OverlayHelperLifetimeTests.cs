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
        consumer.Show("WARMUP", OverlayDemand.None);
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
        // The flash is stamped and queued while the consumer runs the due suspend. It does not keep the pill
        // on screen, so only its stamp can veto the suspend and let it reach the helper that is still warm.
        var consumer = new Consumer();
        consumer.Show("WARMUP", OverlayDemand.None);
        consumer.Drain();

        consumer.AdvanceTo(Idle - 1);
        consumer.Show("FAILED reason", OverlayDemand.Transient); // queued, not taken yet
        consumer.AdvanceTo(Idle);
        consumer.Drain();

        Assert.Empty(consumer.At("Suspend"));
        Assert.Equal([0L], consumer.At("Launch"));
        Assert.Equal([Idle], consumer.At("Write FAILED reason"));
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
            consumer.Show("WARMUP", OverlayDemand.None);
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
    public void A_failure_flash_never_earns_a_retry()
    {
        var consumer = new Consumer();
        consumer.FailNextLaunches(1);
        consumer.Show("FAILED reason", OverlayDemand.Transient);
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
        consumer.Show("FAILED reason", OverlayDemand.Transient); // held back: the cooldown is still running
        consumer.Drain();

        consumer.AdvanceTo(5_000);

        Assert.Empty(consumer.At("Retry"));
        Assert.Equal([0L], consumer.At("Launch"));
    }

    [Fact]
    public void Keep_warm_zero_never_suspends_and_a_changed_period_applies_to_an_armed_deadline()
    {
        var consumer = new Consumer(idleMs: 0);
        consumer.Show("WARMUP", OverlayDemand.None);
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
        consumer.Show("WARMUP", OverlayDemand.None);
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
        consumer.Show("WARMUP", OverlayDemand.None);
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
        consumer.Show("WARMUP", OverlayDemand.None);
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
        consumer.Show("WARMUP", OverlayDemand.None);
        consumer.Drain();
        consumer.RequestRelease();
        consumer.Show("POSITION TopCenter", OverlayDemand.None, ensureAlive: false); // a settings save raced the pause
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
        consumer.Show("WARMUP", OverlayDemand.None);
        consumer.Drain();
        consumer.CrashHelperAt(60_000);

        consumer.AdvanceTo(70_000);
        consumer.Show("POSITION TopCenter", OverlayDemand.None, ensureAlive: false);
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
        hidden.Show("WARMUP", OverlayDemand.None);
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
        consumer.Show("WARMUP", OverlayDemand.None);
        consumer.Drain();
        consumer.DropStamped(); // a superseded preview step: taken, not carried out

        consumer.AdvanceTo(Idle);
        Assert.Equal([Idle], consumer.At("Suspend"));
    }

    /// <summary>
    /// Plays the overlay client's consumer and its producers against a scripted clock and a simulated
    /// helper, recording each decision as "<c>What</c> at <c>time</c>".
    /// </summary>
    private sealed class Consumer
    {
        private readonly Queue<Pending> _queue = new();
        private readonly Queue<(OverlayLaunchResult Result, long TakesMs)> _launches = new();
        private readonly List<(long AtMs, string What)> _events = [];
        private OverlayHelperStatus _helper = OverlayHelperStatus.Absent;
        private long? _crashedAtMs;

        public Consumer(long idleMs = Idle)
        {
            Lifetime.SetIdlePeriodMs(idleMs);
        }

        public OverlayHelperLifetime Lifetime { get; } = new();

        public long NowMs { get; private set; }

        public int Wakes { get; private set; }

        public OverlayHelperStatus HelperStatus => _helper;

        /// <summary>The latest demand a producer published; the consumer reads it at each decision.</summary>
        public OverlayDemand Demand { get; private set; }

        public long[] At(string what) => [.. _events.Where(e => e.What == what).Select(e => e.AtMs)];

        public void FailNextLaunches(int count)
        {
            for (var i = 0; i < count; i++)
            {
                NextLaunch(OverlayLaunchResult.Failed);
            }
        }

        public void NextLaunch(OverlayLaunchResult result, long takesMs = 0) => _launches.Enqueue((result, takesMs));

        // ---- Producers ----------------------------------------------------------------------------

        /// <summary>ShowRecording and friends: publish the demand, stamp, enqueue.</summary>
        public void Show(string line, OverlayDemand demand, bool ensureAlive = true, bool cancelsRetry = false)
        {
            PublishOnly(demand);
            StampAndEnqueue(line, ensureAlive, cancelsRetry);
        }

        public void PublishOnly(OverlayDemand demand) => Demand = demand;

        public void StampAndEnqueue(string line, bool ensureAlive = true, bool cancelsRetry = false) =>
            _queue.Enqueue(new Pending(PendingKind.State, line, Lifetime.IssueStamp(), ensureAlive, cancelsRetry));

        public void RequestRelease() =>
            _queue.Enqueue(new Pending(PendingKind.Release, "RELEASE", Lifetime.IssueStamp(), false, false));

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
            while (_queue.TryDequeue(out var pending))
            {
                if (pending.Kind == PendingKind.Release)
                {
                    var observed = Observe();
                    var work = Lifetime.OnReleaseWhenIdle(pending.Stamp, Demand, observed);
                    DiscardIfLost(observed);
                    if (work == OverlayDueWork.Suspend)
                    {
                        Record("Release");
                        _helper = OverlayHelperStatus.Absent;
                    }

                    continue;
                }

                var helper = Observe();
                var action = Lifetime.OnStateCommand(NowMs, pending.Stamp, pending.EnsureAlive, pending.CancelsRetry, Demand, helper);
                Carry(action, helper, pending.Line);
            }
        }

        /// <summary>A live level meter taken at the current time.</summary>
        public void Meter()
        {
            var helper = Observe();
            Carry(Lifetime.OnMeter(NowMs, Demand, helper), helper, "METER");
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
                case OverlayDueWork.Retry:
                    Record("Retry");
                    Launch();
                    break;
            }
        }

        private void Carry(OverlayCommandAction action, OverlayHelperObservation helper, string line)
        {
            DiscardIfLost(helper);
            switch (action)
            {
                case OverlayCommandAction.Write:
                    Record("Write " + line);
                    break;
                case OverlayCommandAction.Launch:
                    if (Launch())
                    {
                        Record("Write " + line);
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

        private bool Launch()
        {
            Record("Launch");
            var (result, takesMs) = _launches.Count > 0 ? _launches.Dequeue() : (OverlayLaunchResult.Launched, 0L);
            NowMs += takesMs; // the consumer is blocked in the launch meanwhile
            _helper = result == OverlayLaunchResult.Launched ? OverlayHelperStatus.Alive : OverlayHelperStatus.Absent;
            Lifetime.OnLaunchCompleted(NowMs, result, Demand);
            return result == OverlayLaunchResult.Launched;
        }

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

        private readonly record struct Pending(PendingKind Kind, string Line, long Stamp, bool EnsureAlive, bool CancelsRetry);
    }
}
