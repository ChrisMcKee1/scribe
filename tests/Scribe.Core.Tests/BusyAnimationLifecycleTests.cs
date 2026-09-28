using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// StopInactiveProgress: a Settings busy indicator animates only while it is shown and never after its window closed. The
/// cases are the ones the round 3 sign-off (UI-S-01) asked for; "shown" is the control's effective visibility, so a page
/// change, a collapsed ancestor and an unloaded tree all arrive as the same report.
/// </summary>
public sealed class BusyAnimationLifecycleTests
{
    [Fact]
    public void A_new_indicator_that_is_not_shown_does_nothing()
    {
        var lifecycle = new BusyAnimationLifecycle();

        Assert.Equal(BusyAnimationChange.None, lifecycle.Update(shown: false));
        Assert.False(lifecycle.IsRunning);
        Assert.False(lifecycle.IsClosed);
    }

    [Fact]
    public void Busy_then_idle_then_busy_starts_stops_and_starts_again()
    {
        var lifecycle = new BusyAnimationLifecycle();

        Assert.Equal(BusyAnimationChange.Start, lifecycle.Update(shown: true));
        Assert.True(lifecycle.IsRunning);
        Assert.Equal(BusyAnimationChange.Stop, lifecycle.Update(shown: false));
        Assert.False(lifecycle.IsRunning);
        Assert.Equal(BusyAnimationChange.Start, lifecycle.Update(shown: true));
        Assert.True(lifecycle.IsRunning);
    }

    [Fact]
    public void Busy_then_the_row_hidden_then_busy_again_restarts_without_any_other_input()
    {
        // Show(null) collapses the whole row, which hides its spinner: the same report as the busy state ending.
        var lifecycle = new BusyAnimationLifecycle();
        lifecycle.Update(shown: true);

        Assert.Equal(BusyAnimationChange.Stop, lifecycle.Update(shown: false));
        Assert.Equal(BusyAnimationChange.Start, lifecycle.Update(shown: true));
    }

    [Fact]
    public void Busy_on_a_page_the_user_leaves_stops_and_starts_again_when_the_page_returns_with_no_status_update()
    {
        var lifecycle = new BusyAnimationLifecycle();
        Assert.Equal(BusyAnimationChange.Start, lifecycle.Update(shown: true));

        // Another page: the spinner's own visibility (the busy state) is unchanged, its page is collapsed.
        Assert.Equal(BusyAnimationChange.Stop, lifecycle.Update(shown: false));
        Assert.False(lifecycle.IsRunning);

        // The same page again: nothing but the visibility report arrives.
        Assert.Equal(BusyAnimationChange.Start, lifecycle.Update(shown: true));
        Assert.True(lifecycle.IsRunning);
    }

    [Fact]
    public void Busy_under_an_ancestor_that_hides_or_unloads_stops_and_restarts_when_it_is_shown_or_loaded_again()
    {
        var lifecycle = new BusyAnimationLifecycle();
        lifecycle.Update(shown: true);

        Assert.Equal(BusyAnimationChange.Stop, lifecycle.Update(shown: false)); // ancestor collapsed
        Assert.Equal(BusyAnimationChange.Start, lifecycle.Update(shown: true)); // shown again
        Assert.Equal(BusyAnimationChange.Stop, lifecycle.Update(shown: false)); // tree unloaded
        Assert.Equal(BusyAnimationChange.Start, lifecycle.Update(shown: true)); // reloaded
    }

    [Fact]
    public void Busy_when_the_window_closes_stops_and_nothing_that_arrives_later_starts_it_again()
    {
        var lifecycle = new BusyAnimationLifecycle();
        lifecycle.Update(shown: true);

        Assert.Equal(BusyAnimationChange.Stop, lifecycle.Close());
        Assert.True(lifecycle.IsClosed);
        Assert.False(lifecycle.IsRunning);

        // A posted status update, or a late visibility change, after the close.
        Assert.Equal(BusyAnimationChange.None, lifecycle.Update(shown: true));
        Assert.Equal(BusyAnimationChange.None, lifecycle.Update(shown: false));
        Assert.Equal(BusyAnimationChange.None, lifecycle.Update(shown: true));
        Assert.Equal(BusyAnimationChange.None, lifecycle.Close());
        Assert.False(lifecycle.IsRunning);
    }

    [Fact]
    public void Closing_an_indicator_that_is_not_running_changes_nothing_and_keeps_it_stopped()
    {
        var lifecycle = new BusyAnimationLifecycle();

        Assert.Equal(BusyAnimationChange.None, lifecycle.Close());
        Assert.Equal(BusyAnimationChange.None, lifecycle.Update(shown: true));
        Assert.False(lifecycle.IsRunning);
    }

    [Fact]
    public void Busy_requested_while_already_hidden_starts_only_once_the_indicator_is_shown()
    {
        var lifecycle = new BusyAnimationLifecycle();

        // The busy state is set while its page is hidden: the spinner is still not shown.
        Assert.Equal(BusyAnimationChange.None, lifecycle.Update(shown: false));
        Assert.False(lifecycle.IsRunning);
        Assert.Equal(BusyAnimationChange.Start, lifecycle.Update(shown: true));
    }

    [Fact]
    public void Repeated_reports_of_the_same_state_are_answered_with_nothing()
    {
        var lifecycle = new BusyAnimationLifecycle();

        Assert.Equal(BusyAnimationChange.Start, lifecycle.Update(shown: true));
        Assert.Equal(BusyAnimationChange.None, lifecycle.Update(shown: true));
        Assert.Equal(BusyAnimationChange.Stop, lifecycle.Update(shown: false));
        Assert.Equal(BusyAnimationChange.None, lifecycle.Update(shown: false));
    }

    [Fact]
    public void Rapid_transitions_before_another_layout_pass_leave_exactly_the_last_state()
    {
        var lifecycle = new BusyAnimationLifecycle();
        bool[] reports = [true, false, true, false, true, true, false, true];
        var changes = reports.Select(lifecycle.Update).ToArray();

        Assert.Equal(
            [
                BusyAnimationChange.Start, BusyAnimationChange.Stop, BusyAnimationChange.Start, BusyAnimationChange.Stop,
                BusyAnimationChange.Start, BusyAnimationChange.None, BusyAnimationChange.Stop, BusyAnimationChange.Start,
            ],
            changes);
        Assert.True(lifecycle.IsRunning);
    }

    [Fact]
    public void Any_sequence_keeps_starts_and_stops_paired_and_ends_in_the_state_last_reported()
    {
        // Seeded, so a failure reproduces: 2,000 sequences of shown reports with a close somewhere in some of them.
        var random = new Random(51_2026);
        for (var sequence = 0; sequence < 2_000; sequence++)
        {
            var lifecycle = new BusyAnimationLifecycle();
            var running = false;
            var closed = false;
            var lastShown = false;
            var length = random.Next(1, 40);
            var closeAt = random.Next(0, 3) == 0 ? random.Next(0, length) : -1;
            for (var step = 0; step < length; step++)
            {
                BusyAnimationChange change;
                if (step == closeAt)
                {
                    change = lifecycle.Close();
                    closed = true;
                }
                else
                {
                    lastShown = random.Next(2) == 1;
                    change = lifecycle.Update(lastShown);
                }

                // A start only from stopped, a stop only from running, never a start once closed.
                switch (change)
                {
                    case BusyAnimationChange.Start:
                        Assert.False(running, $"sequence {sequence} step {step}: started twice");
                        Assert.False(closed, $"sequence {sequence} step {step}: started after the close");
                        running = true;
                        break;
                    case BusyAnimationChange.Stop:
                        Assert.True(running, $"sequence {sequence} step {step}: stopped twice");
                        running = false;
                        break;
                }

                Assert.Equal(!closed && lastShown, lifecycle.IsRunning);
                Assert.Equal(running, lifecycle.IsRunning);
            }
        }
    }
}
