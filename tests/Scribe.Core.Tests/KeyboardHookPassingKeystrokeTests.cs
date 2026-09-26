using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 3, found while checking item 1 (Astra's A6) against every command that clears the key view. A keystroke
/// whose press a hook ahead of Scribe's may have seen (judged uncertain after the hook became the newest registration, or
/// reaching only a registration a move replaced) was kept unswallowed only by the machines' view of it: its key was held
/// and never swallowed, so its repeats and its release passed. Anything that clears that view (a capture start or end, new
/// bindings, a desktop switch) made its next repeat a fresh press once the uncertainty window had closed, swallowed with its
/// release, which the hook that forwarded the press then never saw. So the engine keeps those keys itself, until each one's
/// release is seen, whatever the machines forget meanwhile (<see cref="HotkeyEngine.OnKeyEvent"/>).
/// </summary>
public sealed class KeyboardHookPassingKeystrokeTests
{
    private const uint RightCtrl = 0xA3;
    private const uint Moved = 10_000;
    private const int Window = 875;

    [Fact]
    public void An_uncertain_keystroke_stays_unswallowed_through_a_command_that_clears_the_view_after_the_window_closed()
    {
        using var h = Armed(HotkeyBinding.Legacy);
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 100).Suppress);
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + Window + 10).Suppress); // closes it

        h.Router.UpdateBindings(HotkeyBinding.Legacy, HotkeyBinding.DefaultDictationOnly);

        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + Window + 40).Suppress);
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + Window + 70).Suppress);
        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + Window + 100).Suppress);
        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + Window + 130).Suppress);
    }

    [Fact]
    public void A_keystroke_that_reached_only_a_replaced_registration_stays_unswallowed_through_a_command_that_clears_the_view()
    {
        // Its press entered the chain before the move and passed every hook registered between the two registrations, so
        // those hooks have to get the rest of it too, whatever the machines forget meanwhile. No window is open here.
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        var passOn = new KeyEventPassOn();
        Assert.False(Route(h, passOn, current: false, RightCtrl, time: 5, up: false).Swallow);

        h.Router.SetCaptureMode(true);
        h.Router.SetCaptureMode(false);

        Assert.False(Route(h, passOn, current: true, RightCtrl, time: 35, up: false).Swallow);
        Assert.False(Route(h, passOn, current: true, RightCtrl, time: 65, up: true).Swallow);
        Assert.True(Route(h, passOn, current: true, RightCtrl, time: 95, up: false).Swallow);
        Assert.True(Route(h, passOn, current: true, RightCtrl, time: 125, up: true).Swallow);
    }

    [Fact]
    public void An_uncertain_keystroke_held_through_a_desktop_switch_stays_unswallowed_after_the_window_closed()
    {
        using var h = Armed(HotkeyBinding.Legacy);
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 100).Suppress);

        h.Engine.OnDesktopSwitch(); // the lock screen: both machines forget every key, and the dictation is ended

        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + Window + 500).Suppress);
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + Window + 530).Suppress);
    }

    [Fact]
    public void A_passing_keystroke_whose_release_went_unseen_lets_the_next_press_of_that_key_through_once()
    {
        // The cost of failing open, pinned: released on the lock screen, where the hook is not called, the key stays among the
        // keystrokes passing, so its next press goes through whole, once (the dictation still starts and ends), and the press
        // after that is swallowed as ever. Swallowing instead could strand a key in the session that saw the press.
        using var h = Armed(HotkeyBinding.Legacy);
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 100).Suppress);
        h.Engine.OnDesktopSwitch();
        h.TakeTransitions();

        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 5_000).Suppress);
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + 5_030).Suppress);
        Assert.Equal(
            [HotkeyTransition.Activated, HotkeyTransition.Deactivated], h.TakeTransitions().Select(t => t.Transition));
        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 5_060).Suppress);
        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + 5_090).Suppress);
    }

    private static KeyboardHookRoute Route(HotkeyEngineHarness h, KeyEventPassOn passOn, bool current, uint key, uint time, bool up) =>
        KeyboardHookFilter.Route(h.Engine, passOn, current, new KeyEventIdentity(key, 0x1D, up ? 0x81u : 0x01u, time), isDown: !up, 0);

    private static HotkeyEngineHarness Armed(HotkeyBinding binding)
    {
        var h = new HotkeyEngineHarness(binding);
        h.Engine.SetUncertaintyWindow(Window);
        h.Engine.OnRegisteredAhead(Moved);
        return h;
    }
}