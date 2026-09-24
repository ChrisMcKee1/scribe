using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// The hook is not called for input on the lock screen or a secure desktop, so a key held as the desktop switches is
/// released where the hook cannot see it. A desktop switch therefore ends a recording the way its binding would have
/// ended it (a hold as if released, a toggle as if toggled off), once, reported as a desktop switch through the same
/// queue every stop takes, and leaves the hook ready for a fresh press. A switch with nothing recording does nothing.
/// </summary>
public sealed class HotkeyDesktopSwitchTests
{
    private const uint PageDown = 0x22;
    private const uint PageUp = 0x21;
    private const uint CapsLock = 0x14;
    private const uint LeftCtrl = 0xA2;

    [Fact]
    public void A_hold_through_a_desktop_switch_ends_once_as_if_released()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);
        Assert.True(h.Down(PageDown).Suppress);
        Assert.True(h.Down(PageDown).Suppress); // autorepeat while dictating

        h.Engine.OnDesktopSwitch(); // Win+L: Page Down is released on the lock screen
        h.Engine.OnDesktopSwitch(); // and the desktop switches back after unlocking

        var transitions = h.TakeTransitions();
        Assert.Equal(
            new[]
            {
                (HotkeyTransition.Activated, HotkeyTrigger.Standard, HotkeyDeactivation.Released),
                (HotkeyTransition.Deactivated, HotkeyTrigger.Standard, HotkeyDeactivation.DesktopSwitch),
            },
            transitions.Select(t => (t.Transition, t.Trigger, t.Deactivation)).ToArray());
        Assert.False(transitions[1].AllowReconcile); // a state clear, as for a hook reinstall
        Assert.True(h.WouldDispatch(transitions[1]));

        // A release that does arrive, the key having been held all the way back, ends nothing a second time.
        h.Up(PageDown);
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void A_toggle_through_a_desktop_switch_ends_once_as_if_toggled_off()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation with { Mode = HotkeyMode.Toggle });
        h.Down(PageDown);
        h.Up(PageDown); // toggled on

        h.Engine.OnDesktopSwitch();
        h.Engine.OnDesktopSwitch();

        // The next press starts a new dictation; it is not taken as the toggle-off of the one the switch ended.
        h.Down(PageDown);
        h.Up(PageDown);
        Assert.Equal(
            new[]
            {
                (HotkeyTransition.Activated, HotkeyDeactivation.Released),
                (HotkeyTransition.Deactivated, HotkeyDeactivation.DesktopSwitch),
                (HotkeyTransition.Activated, HotkeyDeactivation.Released),
            },
            h.TakeTransitions().Select(t => (t.Transition, t.Deactivation)).ToArray());
    }

    [Fact]
    public void A_desktop_switch_with_nothing_recording_does_nothing()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);
        h.Engine.OnDesktopSwitch();
        h.Down(LeftCtrl);
        h.Up(LeftCtrl);
        h.Engine.OnDesktopSwitch();
        Assert.Empty(h.TakeTransitions());

        // Paused, a held key records nothing, so a switch has nothing to end either.
        h.Router.SetPaused(true);
        h.Down(PageDown);
        h.Engine.OnDesktopSwitch();
        Assert.Empty(h.TakeTransitions());
        Assert.Equal(3, h.Engine.DesktopSwitches);
    }

    [Fact]
    public void A_fresh_press_after_a_desktop_switch_starts_normally()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation);
        h.Down(PageDown);
        h.Engine.OnDesktopSwitch(); // locked; the release happens on the lock screen, unseen
        h.Engine.OnDesktopSwitch(); // unlocked
        h.TakeTransitions();

        // Without the reset, this press would look like an autorepeat of the key the hook still believed held, and it
        // would be swallowed without starting anything.
        Assert.True(h.Down(PageDown).Suppress);
        Assert.True(h.Up(PageDown).Suppress);
        Assert.Equal(
            new[]
            {
                (HotkeyTransition.Activated, HotkeyDeactivation.Released),
                (HotkeyTransition.Deactivated, HotkeyDeactivation.Released),
            },
            h.TakeTransitions().Select(t => (t.Transition, t.Deactivation)).ToArray());
    }

    [Fact]
    public void A_dictation_only_hold_through_a_desktop_switch_ends_as_its_own_trigger()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);
        h.Down(PageUp);
        h.Engine.OnDesktopSwitch();

        Assert.Equal(
            new[]
            {
                (HotkeyTransition.Activated, HotkeyTrigger.DictationOnly, HotkeyDeactivation.Released),
                (HotkeyTransition.Deactivated, HotkeyTrigger.DictationOnly, HotkeyDeactivation.DesktopSwitch),
            },
            h.TakeTransitions().Select(t => (t.Transition, t.Trigger, t.Deactivation)).ToArray());
    }

    [Fact]
    public void Both_keys_held_through_a_desktop_switch_end_the_one_dictation_once()
    {
        // Page Up pressed while Page Down dictates is refused by the arbiter; the switch ends the one dictation there is.
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);
        h.Down(PageDown);
        h.Down(PageUp);
        h.Engine.OnDesktopSwitch();

        Assert.Equal(
            new[]
            {
                (HotkeyTransition.Activated, HotkeyTrigger.Standard),
                (HotkeyTransition.Deactivated, HotkeyTrigger.Standard),
            },
            h.TakeTransitions().Select(t => (t.Transition, t.Trigger)).ToArray());
    }

    [Fact]
    public void A_narrator_key_bound_on_its_own_and_held_through_a_desktop_switch_ends_like_any_hold()
    {
        var capsLock = new HotkeyBinding(CapsLock, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Caps Lock");
        using var h = new HotkeyEngineHarness(capsLock);
        Assert.True(h.Down(CapsLock).Suppress);

        h.Engine.OnDesktopSwitch();

        Assert.Equal(
            new[]
            {
                (HotkeyTransition.Activated, HotkeyDeactivation.Released),
                (HotkeyTransition.Deactivated, HotkeyDeactivation.DesktopSwitch),
            },
            h.TakeTransitions().Select(t => (t.Transition, t.Deactivation)).ToArray());
    }

    [Fact]
    public void An_activation_still_queued_when_the_desktop_switches_never_starts_a_recording()
    {
        // The consumer thread is behind: Page Down's Activated is still in the queue when Win+L switches the desktop.
        // Raised now it would open the microphone after the lock, only for the switch's stop to close it again, so the
        // consumer's own rule must discard it; the stop stays dispatchable.
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation);
        h.Down(PageDown);
        h.Engine.OnDesktopSwitch();
        h.Engine.OnDesktopSwitch();

        var queued = h.TakeTransitions();
        Assert.Equal(
            new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
            queued.Select(t => t.Transition).ToArray());
        Assert.False(h.WouldDispatch(queued[0]));
        Assert.True(h.WouldDispatch(queued[1]));

        // A genuinely new press after the switch still starts normally.
        Assert.True(h.Down(PageDown).Suppress);
        var fresh = Assert.Single(h.TakeTransitions());
        Assert.Equal(HotkeyTransition.Activated, fresh.Transition);
        Assert.True(h.WouldDispatch(fresh));
    }

    [Theory]
    [InlineData(PageDown, PageUp)] // Page Down owns the dictation, Page Up is refused, Page Down is released
    [InlineData(PageUp, PageDown)] // the other way round
    public void A_switch_after_the_owner_was_released_stops_nothing(uint owner, uint refused)
    {
        // The refused key's machine still latches its press; the arbiter owns nothing once the owner is released, so
        // there is no dictation for the switch to end, whichever machine reports it was active.
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);
        h.Down(owner);
        h.Down(refused);
        h.Up(owner);
        Assert.Equal(
            new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
            h.TakeTransitions().Select(t => t.Transition).ToArray());

        h.Engine.OnDesktopSwitch();

        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void An_ignored_toggle_on_the_other_key_is_not_stopped_by_a_switch_and_toggles_on_afresh()
    {
        var toggledOnly = HotkeyBinding.DefaultDictationOnly with { Mode = HotkeyMode.Toggle };
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, toggledOnly);
        h.Down(PageDown); // Page Down owns the dictation
        h.Down(PageUp);
        h.Up(PageUp); // the toggle latches on in its machine, but the arbiter refuses it
        h.Up(PageDown);
        h.TakeTransitions();

        h.Engine.OnDesktopSwitch();
        Assert.Empty(h.TakeTransitions());

        // The reset dropped the ignored latch, so the next tap turns the toggle on instead of off.
        h.Down(PageUp);
        h.Up(PageUp);
        Assert.Equal(
            new[] { (HotkeyTransition.Activated, HotkeyTrigger.DictationOnly) },
            h.TakeTransitions().Select(t => (t.Transition, t.Trigger)).ToArray());
    }

    [Fact]
    public void The_consumer_raises_no_activation_queued_before_a_desktop_switch()
    {
        // The real consumer step: an Activated whose activation epoch is older than the queue's is dropped, one that is
        // current is raised, and stops are raised whatever their epoch.
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);
        using var queue = new HotkeyTransitionQueue();
        var raised = new List<string>();
        service.Activated += (_, e) => raised.Add("start " + e.Trigger);
        service.Deactivated += (_, e) => raised.Add("stop " + e.Trigger);
        queue.AdvanceActivationEpoch(); // the switch

        service.DispatchTransition(new HotkeyService.QueuedTransition(
            HotkeyTransition.Activated, HotkeyTrigger.Standard, 0, AllowReconcile: true, ActivationEpoch: 0), queue);
        service.DispatchTransition(new HotkeyService.QueuedTransition(
            HotkeyTransition.Deactivated, HotkeyTrigger.Standard, 0, AllowReconcile: false, HotkeyDeactivation.DesktopSwitch), queue);
        service.DispatchTransition(new HotkeyService.QueuedTransition(
            HotkeyTransition.Activated, HotkeyTrigger.DictationOnly, 0, AllowReconcile: true, ActivationEpoch: 1), queue);

        Assert.Equal(new[] { "stop Standard", "start DictationOnly" }, raised.ToArray());
    }

    [Fact]
    public void A_desktop_switch_stop_reaches_subscribers_with_its_reason()
    {
        // The consumer thread's step: a desktop-switch stop is raised like any Deactivated, with its reason, so the
        // controller ends the recording on its usual stop path and logs why.
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);
        var seen = new List<(HotkeyTrigger, HotkeyDeactivation)>();
        service.Deactivated += (_, e) => seen.Add((e.Trigger, e.Deactivation));

        service.DispatchTransition(new HotkeyService.QueuedTransition(
            HotkeyTransition.Deactivated, HotkeyTrigger.DictationOnly, 1, AllowReconcile: false, HotkeyDeactivation.DesktopSwitch),
            new HotkeyTransitionQueue());
        service.DispatchTransition(new HotkeyService.QueuedTransition(
            HotkeyTransition.Deactivated, HotkeyTrigger.Standard, 1, AllowReconcile: false),
            new HotkeyTransitionQueue());

        Assert.Equal(
            new[]
            {
                (HotkeyTrigger.DictationOnly, HotkeyDeactivation.DesktopSwitch),
                (HotkeyTrigger.Standard, HotkeyDeactivation.Released),
            },
            seen.ToArray());
    }
}
