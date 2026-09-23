using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

public sealed class OverlayIdleDeadlineTests
{
    // The shipped keep-warm default (ReleaseModelsAfterIdleMinutes = 10), in milliseconds.
    private const long Idle = 10 * 60_000;

    [Fact]
    public void Nothing_is_due_before_any_command_was_processed()
    {
        var deadline = new OverlayIdleDeadline();

        Assert.Null(deadline.DueAtMs(Idle));
        Assert.False(deadline.TryCommitSuspend(100 * Idle, Idle, OverlayDemand.None));
    }

    [Fact]
    public void A_processed_command_arms_the_deadline_one_idle_period_later()
    {
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(1_000, deadline.NoteCommandIssued());

        Assert.Equal(1_000 + Idle, deadline.DueAtMs(Idle));
        Assert.False(deadline.TryCommitSuspend(1_000 + Idle - 1, Idle, OverlayDemand.None));
        Assert.True(deadline.TryCommitSuspend(1_000 + Idle, Idle, OverlayDemand.None));
    }

    [Fact]
    public void Every_processed_command_resets_the_deadline()
    {
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued());          // WARMUP at startup
        deadline.OnCommandProcessed(5 * 60_000, deadline.NoteCommandIssued()); // a position preview

        Assert.Equal(5 * 60_000 + Idle, deadline.DueAtMs(Idle));
        Assert.False(deadline.TryCommitSuspend(Idle, Idle, OverlayDemand.None));
        Assert.True(deadline.TryCommitSuspend(5 * 60_000 + Idle, Idle, OverlayDemand.None));
    }

    [Fact]
    public void A_preview_is_followed_by_the_ordinary_deadline()
    {
        // The preview sends POSITION, RECORDING, a meter sweep, HIDE and the POSITION restore. Its last
        // command starts the idle period, so the helper it launched no longer stays up forever.
        var deadline = new OverlayIdleDeadline();
        for (var step = 0; step < 28; step++)
        {
            deadline.OnCommandProcessed(step * 70, deadline.NoteCommandIssued());
        }

        const long lastCommandMs = 27 * 70;
        Assert.Equal(lastCommandMs + Idle, deadline.DueAtMs(Idle));
        Assert.True(deadline.TryCommitSuspend(lastCommandMs + Idle, Idle, OverlayDemand.None));
    }

    [Fact]
    public void A_committed_suspend_disarms_until_the_next_command()
    {
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued());
        Assert.True(deadline.TryCommitSuspend(Idle, Idle, OverlayDemand.None));

        Assert.Null(deadline.DueAtMs(Idle));
        Assert.False(deadline.TryCommitSuspend(10 * Idle, Idle, OverlayDemand.None));

        deadline.OnCommandProcessed(20 * Idle, deadline.NoteCommandIssued());
        Assert.Equal(21 * Idle, deadline.DueAtMs(Idle));
    }

    [Fact]
    public void A_command_stamped_before_the_commit_vetoes_the_suspend()
    {
        // Race, command first: the idle period ends while a new command is stamped but not yet taken
        // from the queue.
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued());
        var racing = deadline.NoteCommandIssued();

        Assert.False(deadline.TryCommitSuspend(Idle, Idle, OverlayDemand.None));

        // No spinning while that command is still on its way: the next look is a full period away.
        Assert.Equal(2 * Idle, deadline.DueAtMs(Idle));

        deadline.OnCommandProcessed(Idle + 5, racing);
        Assert.Equal(2 * Idle + 5, deadline.DueAtMs(Idle));
        Assert.True(deadline.TryCommitSuspend(2 * Idle + 5, Idle, OverlayDemand.None));
    }

    [Fact]
    public void A_command_stamped_after_the_commit_starts_a_new_idle_period()
    {
        // Race, commit first: the suspend is decided before the command exists. The consumer takes the
        // command after the teardown and relaunches the helper for it; here the period simply restarts.
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued());

        Assert.True(deadline.TryCommitSuspend(Idle, Idle, OverlayDemand.None));

        var late = deadline.NoteCommandIssued();
        Assert.Null(deadline.DueAtMs(Idle));

        deadline.OnCommandProcessed(Idle + 5, late);
        Assert.Equal(2 * Idle + 5, deadline.DueAtMs(Idle));
    }

    [Fact]
    public void A_recording_that_starts_as_the_idle_period_ends_keeps_its_pill_in_every_interleaving()
    {
        // ShowRecording publishes the sustained state, then stamps, then enqueues. The consumer's commit
        // reads the stamp, then the state. Each way the two can overlap either vetoes the suspend or
        // commits before the recording exists, in which case its RECORDING command relaunches the pill.
        var stampSeen = ArmedAndDue();
        stampSeen.NoteCommandIssued();
        Assert.False(stampSeen.TryCommitSuspend(Idle, Idle, OverlayDemand.None));

        var stateSeen = ArmedAndDue();
        Assert.False(stateSeen.TryCommitSuspend(Idle, Idle, OverlayDemand.Sustained));

        var neitherSeen = ArmedAndDue();
        Assert.True(neitherSeen.TryCommitSuspend(Idle, Idle, OverlayDemand.None));
        neitherSeen.OnCommandProcessed(Idle + 1, neitherSeen.NoteCommandIssued());
        Assert.Equal(2 * Idle + 1, neitherSeen.DueAtMs(Idle));
        Assert.False(neitherSeen.TryCommitSuspend(2 * Idle + 1, Idle, OverlayDemand.Sustained));

        static OverlayIdleDeadline ArmedAndDue()
        {
            var deadline = new OverlayIdleDeadline();
            deadline.OnCommandProcessed(0, deadline.NoteCommandIssued());
            return deadline;
        }
    }

    [Fact]
    public void A_sustained_state_is_never_suspended_and_is_checked_again_a_period_later()
    {
        // A recording longer than the idle period sends no state commands; meters are not stamped.
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued()); // RECORDING

        Assert.False(deadline.TryCommitSuspend(Idle, Idle, OverlayDemand.Sustained));
        Assert.Equal(2 * Idle, deadline.DueAtMs(Idle));
        Assert.False(deadline.TryCommitSuspend(2 * Idle, Idle, OverlayDemand.Sustained));

        deadline.OnCommandProcessed(2 * Idle + 100, deadline.NoteCommandIssued()); // HIDE
        Assert.True(deadline.TryCommitSuspend(3 * Idle + 100, Idle, OverlayDemand.None));
    }

    [Fact]
    public void A_self_hiding_failure_flash_does_not_keep_the_helper_resident()
    {
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued()); // FAILED, hidden by the helper itself

        Assert.True(deadline.TryCommitSuspend(Idle, Idle, OverlayDemand.Transient));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-60_000L)]
    public void Keep_warm_zero_or_less_never_suspends(long idleMs)
    {
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued());

        Assert.Null(deadline.DueAtMs(idleMs));
        Assert.False(deadline.TryCommitSuspend(100 * Idle, idleMs, OverlayDemand.None));
    }

    [Fact]
    public void A_changed_idle_period_applies_to_a_deadline_that_is_already_armed()
    {
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(1_000, deadline.NoteCommandIssued());

        Assert.Equal(1_000 + Idle, deadline.DueAtMs(Idle));
        Assert.Equal(1_000 + 60_000, deadline.DueAtMs(60_000));
        Assert.True(deadline.TryCommitSuspend(1_000 + 60_000, 60_000, OverlayDemand.None));
    }

    [Fact]
    public void Commands_enqueued_out_of_stamp_order_still_validate()
    {
        // Two producers can stamp in one order and win the enqueue in the other.
        var deadline = new OverlayIdleDeadline();
        var first = deadline.NoteCommandIssued();
        var second = deadline.NoteCommandIssued();

        deadline.OnCommandProcessed(0, second);
        deadline.OnCommandProcessed(10, first);

        Assert.True(deadline.TryCommitSuspend(10 + Idle, Idle, OverlayDemand.None));
    }

    [Fact]
    public void A_stamp_whose_command_never_arrives_costs_one_period_only()
    {
        // An enqueue that loses the race with shutdown leaves its stamp behind. It vetoes one commit,
        // and the deadline then recovers instead of refusing forever.
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued());
        _ = deadline.NoteCommandIssued();

        Assert.False(deadline.TryCommitSuspend(Idle, Idle, OverlayDemand.None));
        Assert.True(deadline.TryCommitSuspend(2 * Idle, Idle, OverlayDemand.None));
    }

    [Fact]
    public void A_stamp_from_another_thread_before_the_commit_point_vetoes_it()
    {
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued());
        Assert.Equal(Idle, deadline.DueAtMs(Idle)); // the consumer has found the deadline due

        using var stamped = new ManualResetEventSlim();
        var producer = new Thread(() =>
        {
            deadline.NoteCommandIssued(); // ShowRecording on the UI thread
            stamped.Set();
        });
        producer.Start();

        Assert.True(stamped.Wait(TimeSpan.FromSeconds(30)));
        Assert.False(deadline.TryCommitSuspend(Idle, Idle, OverlayDemand.None));
        producer.Join();
    }

    [Fact]
    public void A_stamp_from_another_thread_after_the_commit_point_starts_a_new_period()
    {
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued());
        Assert.True(deadline.TryCommitSuspend(Idle, Idle, OverlayDemand.None));

        using var committed = new ManualResetEventSlim();
        long stamp = 0;
        var producer = new Thread(() =>
        {
            committed.Wait();
            stamp = deadline.NoteCommandIssued();
        });
        producer.Start();
        committed.Set();
        producer.Join();

        Assert.Null(deadline.DueAtMs(Idle));
        deadline.OnCommandProcessed(Idle + 1, stamp);
        Assert.Equal(2 * Idle + 1, deadline.DueAtMs(Idle));
    }

    [Fact]
    public void A_release_commits_at_once_when_nothing_was_stamped_after_it()
    {
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued());      // HIDE for the Paused state
        var release = deadline.NoteCommandIssued();

        Assert.True(deadline.TryCommitRelease(release, OverlayDemand.None));
        Assert.Null(deadline.DueAtMs(Idle)); // disarmed until the next command
    }

    [Fact]
    public void A_release_is_vetoed_by_a_newer_stamp_or_a_state_that_keeps_the_pill_on_screen()
    {
        var newer = new OverlayIdleDeadline();
        newer.OnCommandProcessed(0, newer.NoteCommandIssued());
        var release = newer.NoteCommandIssued();
        _ = newer.NoteCommandIssued(); // resumed: a newer command is on its way
        Assert.False(newer.TryCommitRelease(release, OverlayDemand.None));
        Assert.Equal(Idle, newer.DueAtMs(Idle)); // the idle deadline is untouched

        var sustained = new OverlayIdleDeadline();
        sustained.OnCommandProcessed(0, sustained.NoteCommandIssued());
        Assert.False(sustained.TryCommitRelease(sustained.NoteCommandIssued(), OverlayDemand.Sustained));
    }

    [Fact]
    public void A_settled_stamp_does_not_veto_the_next_commit()
    {
        // A superseded preview step or a vetoed release is taken but never processed as activity.
        var deadline = new OverlayIdleDeadline();
        deadline.OnCommandProcessed(0, deadline.NoteCommandIssued());
        deadline.NoteStampSettled(deadline.NoteCommandIssued());

        Assert.Equal(Idle, deadline.DueAtMs(Idle)); // settling is not activity
        Assert.True(deadline.TryCommitSuspend(Idle, Idle, OverlayDemand.None));
    }
}
