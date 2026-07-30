using Scribe.Core.Hotkeys;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// The watchdog's dead-hook decision. These cases encode the production failure that motivated
/// extracting it: the previous implementation compared two <c>Environment.TickCount64</c> stamps
/// and armed the probe AFTER <c>SendInput</c> returned, so the callback the send itself raised
/// always looked older than the probe, and the next tick declared a healthy hook dead. That fired
/// on 13.3% of watchdog ticks in production, each one tearing down the hook thread and resetting
/// chord state.
/// </summary>
public sealed class HookLivenessProbeTests
{
    [Fact]
    public void Not_dead_before_any_probe_is_armed()
    {
        var probe = new HookLivenessProbe();

        Assert.False(probe.IsArmed);
        Assert.False(probe.IsHookDead(0));
    }

    /// <summary>
    /// The exact race that produced the false positives: the hook callback runs while
    /// <c>SendInput</c> is still executing. Baselining first means that callback still counts.
    /// </summary>
    [Fact]
    public void Callback_raised_during_the_send_answers_the_probe()
    {
        var probe = new HookLivenessProbe();
        var callbacks = 7L;

        probe.Baseline(callbacks);
        callbacks++;            // the hook callback fires inside SendInput
        probe.Arm(sendSucceeded: true);

        Assert.False(probe.IsHookDead(callbacks));
    }

    [Fact]
    public void Callback_raised_after_the_send_returns_answers_the_probe()
    {
        var probe = new HookLivenessProbe();
        var callbacks = 7L;

        probe.Baseline(callbacks);
        probe.Arm(sendSucceeded: true);
        callbacks++;            // delivery was asynchronous

        Assert.False(probe.IsHookDead(callbacks));
    }

    [Fact]
    public void No_callback_at_all_reports_a_dead_hook()
    {
        var probe = new HookLivenessProbe();

        probe.Baseline(12);
        probe.Arm(sendSucceeded: true);

        Assert.True(probe.IsArmed);
        Assert.True(probe.IsHookDead(12));
    }

    /// <summary>
    /// A rejected SendInput (UIPI, a desktop switch mid-tick) proves nothing either way, so the
    /// absence of a callback must not be read as a dead hook.
    /// </summary>
    [Fact]
    public void Failed_send_never_reports_a_dead_hook()
    {
        var probe = new HookLivenessProbe();

        probe.Baseline(3);
        probe.Arm(sendSucceeded: false);

        Assert.False(probe.IsArmed);
        Assert.False(probe.IsHookDead(3));
    }

    /// <summary>
    /// Real typing proves the hook is installed just as well as the probe does, so a burst of user
    /// input between ticks must not be mistaken for a missing probe answer.
    /// </summary>
    [Fact]
    public void Unrelated_keyboard_activity_also_answers_the_probe()
    {
        var probe = new HookLivenessProbe();
        var callbacks = 100L;

        probe.Baseline(callbacks);
        probe.Arm(sendSucceeded: true);
        callbacks += 42;        // the user typed

        Assert.False(probe.IsHookDead(callbacks));
    }

    [Fact]
    public void Disarm_forgets_an_outstanding_probe()
    {
        var probe = new HookLivenessProbe();

        probe.Baseline(5);
        probe.Arm(sendSucceeded: true);
        probe.Disarm();

        Assert.False(probe.IsArmed);
        Assert.False(probe.IsHookDead(5));
    }

    /// <summary>
    /// After a reinstall the fresh hook has raised no callbacks yet. Judging the old probe against
    /// it would report dead immediately and reinstall in a loop.
    /// </summary>
    [Fact]
    public void Disarming_on_reinstall_prevents_an_immediate_second_reinstall()
    {
        var probe = new HookLivenessProbe();
        var callbacks = 20L;

        probe.Baseline(callbacks);
        probe.Arm(sendSucceeded: true);
        Assert.True(probe.IsHookDead(callbacks));   // genuinely dead, triggers a reinstall

        probe.Disarm();                             // what ReinstallHookLocked does
        Assert.False(probe.IsHookDead(callbacks));  // the new hook is not judged by the old probe
    }

    /// <summary>
    /// A healthy hook answering every probe must never report dead, however many periods pass.
    /// The old tick-based comparison failed this on roughly one cycle in eight.
    /// </summary>
    [Fact]
    public void Healthy_hook_never_reports_dead_across_many_cycles()
    {
        var probe = new HookLivenessProbe();
        var callbacks = 0L;

        for (var cycle = 0; cycle < 5_000; cycle++)
        {
            Assert.False(probe.IsHookDead(callbacks));
            probe.Baseline(callbacks);
            callbacks++;                // the probe reaches the hook
            probe.Arm(sendSucceeded: true);
        }
    }
}
