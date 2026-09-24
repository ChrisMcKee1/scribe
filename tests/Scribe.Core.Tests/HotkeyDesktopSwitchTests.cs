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
    public void A_desktop_switch_stop_reaches_subscribers_with_its_reason()
    {
        // The consumer thread's step: a desktop-switch stop is raised like any Deactivated, with its reason, so the
        // controller ends the recording on its usual stop path and logs why.
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);
        var seen = new List<(HotkeyTrigger, HotkeyDeactivation)>();
        service.Deactivated += (_, e) => seen.Add((e.Trigger, e.Deactivation));

        service.DispatchTransition(new HotkeyService.QueuedTransition(
            HotkeyTransition.Deactivated, HotkeyTrigger.DictationOnly, 1, AllowReconcile: false, HotkeyDeactivation.DesktopSwitch));
        service.DispatchTransition(new HotkeyService.QueuedTransition(
            HotkeyTransition.Deactivated, HotkeyTrigger.Standard, 1, AllowReconcile: false));

        Assert.Equal(
            new[]
            {
                (HotkeyTrigger.DictationOnly, HotkeyDeactivation.DesktopSwitch),
                (HotkeyTrigger.Standard, HotkeyDeactivation.Released),
            },
            seen.ToArray());
    }
}
