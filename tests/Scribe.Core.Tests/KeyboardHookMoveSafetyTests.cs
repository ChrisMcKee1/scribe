using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// What keeps a move ahead of a Remote Desktop client's keyboard hook from leaving a key held in the remote session. A
/// hook that is ahead of Scribe's when a key goes down may forward that press into the remote session; if Scribe's hook
/// were ahead when the key came up, and swallowed the release, the remote session would never see it and would keep the
/// key down (for a push-to-talk Right Ctrl, pressing it again does not help: Scribe swallows that key). Two rules close
/// that: a move waits while a key whose press the engine swallowed is held (<see cref="HotkeyEngine.HoldsSwallowedKey"/>),
/// and an event that reaches only a registration the move replaced, because it entered the chain before the move, is
/// judged but never swallowed (<see cref="KeyboardHookFilter.Route"/>, <see cref="HotkeyEngine.OnKeyEvent"/> with
/// mayBeSwallowed false). Also Scribe's own input, whose marker Windows may hand the hook whole or as its low half.
/// </summary>
public sealed class KeyboardHookMoveSafetyTests
{
    private const uint RightCtrl = 0xA3;
    private const uint LeftCtrl = 0xA2;
    private const uint LeftWin = 0x5B;
    private const uint KeyA = 0x41;
    private const uint KeyH = 0x48;
    private const uint F13 = 0x7C;
    private const uint F20 = 0x83;
    private const uint VkPacket = 0xE7;
    private static readonly nuint TestMarker = unchecked((nuint)0x5343524954455354UL);

    [Fact]
    public void The_engine_holds_a_swallowed_key_from_its_swallowed_press_to_its_release()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        Assert.False(h.Engine.HoldsSwallowedKey);

        Assert.True(h.Down(RightCtrl).Suppress);
        Assert.True(h.Engine.HoldsSwallowedKey);
        Assert.True(h.Down(RightCtrl).Suppress); // an autorepeat
        Assert.True(h.Engine.HoldsSwallowedKey);

        Assert.True(h.Up(RightCtrl).Suppress);
        Assert.False(h.Engine.HoldsSwallowedKey);
    }

    [Fact]
    public void A_key_the_engine_lets_through_is_no_swallowed_key_however_long_it_is_held()
    {
        // Ctrl+F20: Ctrl, the chord's first input, is let through; F20 completes it and is swallowed.
        using var h = new HotkeyEngineHarness(new HotkeyBinding(F20, KeyModifiers.Control, HotkeyMode.Hold, Suppress: true));

        Assert.False(h.Down(LeftCtrl).Suppress);
        Assert.False(h.Down(KeyA).Suppress);
        Assert.False(h.Engine.HoldsSwallowedKey);

        Assert.True(h.Down(F20).Suppress);
        Assert.True(h.Engine.HoldsSwallowedKey);
        Assert.True(h.Up(F20).Suppress);

        // Ctrl and A are still held, and were never swallowed.
        Assert.False(h.Engine.HoldsSwallowedKey);
    }

    [Fact]
    public void The_dictation_only_binding_s_swallowed_key_counts_too()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);

        Assert.True(h.Down(0x21).Suppress); // Page Up, the dictation-only binding
        Assert.True(h.Engine.HoldsSwallowedKey);
        Assert.True(h.Up(0x21).Suppress);
        Assert.False(h.Engine.HoldsSwallowedKey);
    }

    [Fact]
    public void A_swallowed_mouse_button_is_no_key_the_keyboard_hook_s_order_can_strand()
    {
        using var h = new HotkeyEngineHarness(
            HotkeyCaptureSession.Build([MouseButtons.Back], HotkeyMode.Hold), buttonDownInWindows: _ => false);

        Assert.True(h.ButtonDown(MouseButtons.Back).Suppress);
        Assert.False(h.Engine.HoldsSwallowedKey);
        Assert.True(h.ButtonUp(MouseButtons.Back).Suppress);
    }

    [Fact]
    public void An_event_that_may_not_be_swallowed_starts_the_dictation_and_leaves_nothing_swallowed_behind_it()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);

        var press = h.Engine.OnKeyEvent(RightCtrl, isDown: true, mayBeSwallowed: false);
        Assert.False(press.Suppress);
        Assert.False(press.RequestReconcile);
        Assert.Equal([HotkeyTransition.Activated], h.TakeTransitions().Select(t => t.Transition));
        Assert.False(h.Engine.HoldsSwallowedKey);

        // Its repeats and its release come through the current registration, which may swallow, and does not: the hooks
        // ahead of the replaced registration saw the press, so they get the rest of the keystroke too.
        Assert.False(h.Down(RightCtrl).Suppress);
        var release = h.Up(RightCtrl);
        Assert.False(release.Suppress);
        Assert.False(release.RequestReconcile);
        Assert.Equal([HotkeyTransition.Deactivated], h.TakeTransitions().Select(t => t.Transition));

        // And the next press, a new keystroke, is swallowed as ever.
        Assert.True(h.Down(RightCtrl).Suppress);
    }

    [Fact]
    public void An_event_that_may_not_be_swallowed_lets_even_a_windows_key_chord_member_through()
    {
        // Win+H with its members swallowed on their own press: the one member that is (NeedsPreemptiveSuppression).
        var binding = new HotkeyBinding(KeyH, KeyModifiers.Win, HotkeyMode.Hold, Suppress: true, SuppressChordMembers: true);
        using var control = new HotkeyEngineHarness(binding);
        Assert.True(control.Down(LeftWin).Suppress);

        using var h = new HotkeyEngineHarness(binding);
        Assert.False(h.Engine.OnKeyEvent(LeftWin, isDown: true, mayBeSwallowed: false).Suppress);
        Assert.False(h.Engine.HoldsSwallowedKey);
        Assert.False(h.Up(LeftWin).Suppress);
    }

    [Fact]
    public void A_swallowed_key_s_release_that_may_not_be_swallowed_is_let_through_and_asks_for_no_repair()
    {
        // A move waits while a swallowed key is held, so no such release can be on its way to a replaced registration;
        // if one were, it goes on to the hooks behind Scribe's rather than stopping there.
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        Assert.True(h.Down(RightCtrl).Suppress);

        var release = h.Engine.OnKeyEvent(RightCtrl, isDown: false, mayBeSwallowed: false);

        Assert.False(release.Suppress);
        Assert.False(release.RequestReconcile);
        Assert.False(h.Engine.HoldsSwallowedKey);
    }

    [Fact]
    public void Through_the_current_registration_a_bound_press_is_swallowed_and_any_other_key_is_judged_and_passed_on()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        var passOn = new KeyEventPassOn();

        Assert.Equal(Swallowed(), Route(h, passOn, current: true, Key(RightCtrl, 1), isDown: true));
        Assert.Equal(Judged(), Route(h, passOn, current: true, Key(KeyA, 2), isDown: true));

        // A release the bindings swallowed asks for the leaked-key repair, in the view it was judged in.
        var release = Route(h, passOn, current: true, Key(RightCtrl, 3, up: true), isDown: false);
        Assert.True(release.Swallow);
        Assert.Equal(h.Engine.KeyViewEpoch, release.RepairAt);
    }

    [Fact]
    public void An_echo_through_a_replaced_registration_passes_untouched_and_never_reaches_the_engine()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        var passOn = new KeyEventPassOn();
        var press = Key(KeyA, 10);
        Assert.Equal(Judged(), Route(h, passOn, current: true, press, isDown: true));
        Assert.True(h.Engine.IsPressed(KeyA));

        // While the current registration is passing A's press on, A is released (made up, so that judging the echo would
        // show), and the press comes back through the replaced registration: an echo, which the engine never sees.
        passOn.Enter(press);
        Assert.Equal(Judged(), Route(h, passOn, current: true, Key(KeyA, 11, up: true), isDown: false));
        var echo = Route(h, passOn, current: false, press, isDown: true);
        passOn.Leave();

        Assert.Equal(new KeyboardHookRoute(Swallow: false, TrackPass: false, Echo: true, RepairAt: 0), echo);
        Assert.False(h.Engine.IsPressed(KeyA));
    }

    [Fact]
    public void An_event_entering_the_current_registration_is_judged_even_when_it_matches_an_event_being_passed_on()
    {
        // Review round 2 (A2). The four fields do not name one event: KEYBDINPUT.time is the caller's. A synthesized F13-up
        // passes through Scribe and stays on the pass stack while its CallNextHookEx runs; a key remapper behind Scribe's hook
        // synthesizes F13-down and F13-up with the same time, scan code and flags; injected input enters the chain at its
        // head, the current registration, so both are new events there, never echoes.
        using var h = new HotkeyEngineHarness(HotkeyCaptureSession.Build([F13], HotkeyMode.Hold));
        var passOn = new KeyEventPassOn();
        var outerUp = new KeyEventIdentity(F13, 0x64, 0x90, 777);
        var down = new KeyEventIdentity(F13, 0x64, 0x10, 777);
        var up = new KeyEventIdentity(F13, 0x64, 0x90, 777);

        Assert.Equal(Judged(), Route(h, passOn, current: true, outerUp, isDown: false));
        passOn.Enter(outerUp);
        var press = Route(h, passOn, current: true, down, isDown: true);
        var release = Route(h, passOn, current: true, up, isDown: false);
        passOn.Leave();

        Assert.True(press.Swallow);
        Assert.False(release.Echo);
        Assert.True(release.Swallow);
        Assert.Equal(
            [HotkeyTransition.Activated, HotkeyTransition.Deactivated], h.TakeTransitions().Select(t => t.Transition));
        Assert.False(h.Engine.IsPressed(F13));

        // The same identity reaching a replaced registration inside that pass is the echo, as before.
        passOn.Enter(up);
        Assert.True(Route(h, passOn, current: false, up, isDown: false).Echo);
        passOn.Leave();
    }

    [Fact]
    public void An_event_that_reaches_only_a_replaced_registration_is_judged_and_passed_on_never_swallowed()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        var passOn = new KeyEventPassOn();

        // Right Ctrl's press entered the chain before the move, and a hook ahead of the old registration has seen it.
        Assert.Equal(Judged(), Route(h, passOn, current: false, Key(RightCtrl, 20), isDown: true));
        Assert.Equal([HotkeyTransition.Activated], h.TakeTransitions().Select(t => t.Transition));
        Assert.False(h.Engine.HoldsSwallowedKey);

        // Its repeat and release enter the chain at the new registration after the move, and go on too.
        Assert.Equal(Judged(), Route(h, passOn, current: true, Key(RightCtrl, 21), isDown: true));
        Assert.Equal(Judged(), Route(h, passOn, current: true, Key(RightCtrl, 22, up: true), isDown: false));
        Assert.Equal([HotkeyTransition.Deactivated], h.TakeTransitions().Select(t => t.Transition));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scribe_s_probe_stops_at_the_first_registration_it_reaches_and_its_other_input_passes_unjudged(bool current)
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        var passOn = new KeyEventPassOn();
        var unjudged = new KeyboardHookRoute(Swallow: false, TrackPass: false, Echo: false, RepairAt: 0);
        foreach (var marker in new[] { SyntheticInputMarker.Value, (nuint)(uint)SyntheticInputMarker.Value })
        {
            Assert.Equal(
                new KeyboardHookRoute(Swallow: true, TrackPass: false, Echo: false, RepairAt: 0),
                Route(h, passOn, current, Key(NativeMethods.VK_PROBE, 1, up: true), isDown: false, marker));
            Assert.Equal(unjudged, Route(h, passOn, current, Key(VkPacket, 2), isDown: true, marker));
            Assert.Equal(unjudged, Route(h, passOn, current, Key(RightCtrl, 3), isDown: true, marker));
            Assert.Equal(unjudged, Route(h, passOn, current, Key(RightCtrl, 4, up: true), isDown: false, marker));
        }

        Assert.Empty(h.TakeTransitions());
        Assert.False(h.Engine.IsPressed(RightCtrl));

        // The probe key from anyone else is a key like any other.
        Assert.Equal(Judged(), Route(h, passOn, current, Key(NativeMethods.VK_PROBE, 5, up: true), isDown: false, TestMarker));
    }

    [Fact]
    public void Scribe_s_own_input_is_its_marker_whole_or_the_low_half_windows_may_keep_of_it()
    {
        Assert.True(KeyboardHookFilter.IsScribesOwn(SyntheticInputMarker.Value));
        Assert.True(KeyboardHookFilter.IsScribesOwn((nuint)(uint)SyntheticInputMarker.Value));

        Assert.False(KeyboardHookFilter.IsScribesOwn(0));
        Assert.False(KeyboardHookFilter.IsScribesOwn(TestMarker));
        Assert.False(KeyboardHookFilter.IsScribesOwn((nuint)(uint)TestMarker));
        Assert.False(KeyboardHookFilter.IsScribesOwn(SyntheticInputMarker.Value >> 32));
        Assert.False(KeyboardHookFilter.IsScribesOwn(SyntheticInputMarker.Value ^ 1));
        Assert.False(KeyboardHookFilter.IsScribesOwn(((nuint)(uint)SyntheticInputMarker.Value) | ((nuint)1 << 40)));
    }

    private static KeyboardHookRoute Swallowed() => new(Swallow: true, TrackPass: false, Echo: false, RepairAt: 0);

    private static KeyboardHookRoute Judged() => new(Swallow: false, TrackPass: true, Echo: false, RepairAt: 0);

    private static KeyEventIdentity Key(uint virtualKey, uint time, bool up = false) => new(virtualKey, 0, up ? 0x80u : 0u, time);

    private static KeyboardHookRoute Route(
        HotkeyEngineHarness h, KeyEventPassOn passOn, bool current, KeyEventIdentity key, bool isDown, nuint extraInfo = 0) =>
        KeyboardHookFilter.Route(h.Engine, passOn, current, key, isDown, extraInfo);
}
