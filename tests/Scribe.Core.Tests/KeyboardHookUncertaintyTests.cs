using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 2, item 1 (Astra's A1, Grok's G1). A Remote Desktop client's hook, called before Scribe's, can forward a
/// key-down into the remote session and keep it (never calling CallNextHookEx), so Scribe never sees that press and
/// nothing in its view says the key is held; GetAsyncKeyState cannot say it either, since a kept low-level event never
/// updates the asynchronous state. Once Scribe's hook moves ahead of the client's, that key's next autorepeat reaches
/// Scribe first. Taken for a fresh press, it was swallowed, and so was its release, which the remote session then never
/// received (a push-to-talk Right Ctrl stayed down there). So after the hook becomes the newest registration, a key-down of
/// a key the engine neither holds nor has seen go up since is uncertain: judged (a dictation still starts or ends) but
/// never swallowed, with the rest of its keystroke, for as long as an autorepeat of a key held across the move can take
/// to arrive (<see cref="KeyRepeatTiming"/>). Event times are the hook's (KBDLLHOOKSTRUCT.time, the tick-count clock).
/// </summary>
public sealed class KeyboardHookUncertaintyTests
{
    private const uint RightCtrl = 0xA3;
    private const uint LeftCtrl = 0xA2;
    private const uint LeftWin = 0x5B;
    private const uint KeyH = 0x48;
    private const uint F20 = 0x83;
    private const uint Moved = 10_000;
    private const int Window = 875;

    [Fact]
    public void A_key_down_nobody_saw_go_down_right_after_the_move_passes_with_its_whole_keystroke_and_still_dictates()
    {
        using var h = Armed(HotkeyBinding.Legacy);

        var press = h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 100);
        Assert.False(press.Suppress);
        Assert.Equal([HotkeyTransition.Activated], h.TakeTransitions().Select(t => t.Transition));

        // Its repeats and its release pass too, so the hook that forwarded the press sees the release.
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 130).Suppress);
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 160).Suppress);
        var release = h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + 190);
        Assert.False(release.Suppress);
        Assert.False(release.RequestReconcile);
        Assert.Equal([HotkeyTransition.Deactivated], h.TakeTransitions().Select(t => t.Transition));
        Assert.Equal(1, h.Engine.UncertainPresses);
        Assert.False(h.Engine.HoldsSwallowedKey);
    }

    [Fact]
    public void The_next_press_after_a_release_seen_since_the_move_is_swallowed_as_ever()
    {
        using var h = Armed(HotkeyBinding.Legacy);
        h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 100);
        h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + 150);

        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 200).Suppress);
        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + 250).Suppress);
        Assert.Equal(1, h.Engine.UncertainPresses);
    }

    [Fact]
    public void A_key_down_of_an_unseen_key_once_the_window_is_over_is_an_ordinary_press()
    {
        using var inside = Armed(HotkeyBinding.Legacy);
        Assert.False(inside.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + Window - 1).Suppress);

        using var after = Armed(HotkeyBinding.Legacy);
        Assert.True(after.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + Window).Suppress);
        Assert.True(after.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + Window + 40).Suppress);
        Assert.Equal(0, after.Engine.UncertainPresses);
    }

    [Fact]
    public void A_key_down_stamped_before_the_registration_counts_as_inside_the_window()
    {
        // An autorepeat generated just before the move can be delivered to the new registration after it.
        using var h = Armed(HotkeyBinding.Legacy);

        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved - 20).Suppress);
        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + 10).Suppress);
    }

    [Fact]
    public void The_window_closes_at_the_first_event_past_it_and_stays_closed()
    {
        using var h = Armed(HotkeyBinding.Legacy);
        Assert.False(h.Engine.OnKeyEvent(F20, isDown: true, eventTime: Moved + Window + 5).Suppress);
        h.Engine.OnKeyEvent(F20, isDown: false, eventTime: Moved + Window + 6);

        // Past the window, closed: an injected event stamped before the move (its time is the caller's) is an ordinary press.
        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved - 5).Suppress);
    }

    [Fact]
    public void The_window_is_measured_across_the_tick_count_s_wrap()
    {
        // GetMessageTime: "subtract the time of the first message from the time of the second message (ignoring overflow)".
        const uint beforeWrap = uint.MaxValue - 99;

        using var inside = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        inside.Engine.SetUncertaintyWindow(Window);
        inside.Engine.OnRegisteredAhead(beforeWrap);
        Assert.False(inside.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: unchecked(beforeWrap + 300)).Suppress);

        using var after = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        after.Engine.SetUncertaintyWindow(Window);
        after.Engine.OnRegisteredAhead(beforeWrap);
        Assert.True(after.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: unchecked(beforeWrap + Window + 10)).Suppress);
    }

    [Fact]
    public void Before_the_hook_first_becomes_the_newest_registration_nothing_is_uncertain()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        h.Engine.SetUncertaintyWindow(Window);

        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: 5).Suppress);
    }

    [Fact]
    public void With_no_window_nothing_is_uncertain()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        h.Engine.SetUncertaintyWindow(0);
        h.Engine.OnRegisteredAhead(Moved);

        Assert.True(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 1).Suppress);
    }

    [Fact]
    public void Moving_again_opens_a_new_window_and_forgets_the_releases_seen_since_the_last_move()
    {
        using var h = Armed(HotkeyBinding.Legacy);
        h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 100);
        h.Engine.OnKeyEvent(RightCtrl, isDown: false, eventTime: Moved + 150);

        h.Engine.OnRegisteredAhead(Moved + 5_000);

        Assert.False(h.Engine.OnKeyEvent(RightCtrl, isDown: true, eventTime: Moved + 5_010).Suppress);
        Assert.Equal(2, h.Engine.UncertainPresses);
    }

    [Fact]
    public void A_chord_member_the_engine_saw_go_down_before_the_move_is_not_uncertain_but_an_unseen_completing_key_is()
    {
        // Ctrl+F20: Left Ctrl was pressed before the move (seen, let through); F20, pressed after it, completes the chord.
        using var h = new HotkeyEngineHarness(new HotkeyBinding(F20, KeyModifiers.Control, HotkeyMode.Hold, Suppress: true));
        h.Engine.SetUncertaintyWindow(Window);
        Assert.False(h.Engine.OnKeyEvent(LeftCtrl, isDown: true, eventTime: Moved - 500).Suppress);
        h.Engine.OnRegisteredAhead(Moved);

        Assert.False(h.Engine.OnKeyEvent(LeftCtrl, isDown: true, eventTime: Moved + 50).Suppress); // a repeat
        Assert.Equal(0, h.Engine.UncertainPresses);

        var completing = h.Engine.OnKeyEvent(F20, isDown: true, eventTime: Moved + 80);
        Assert.False(completing.Suppress);
        Assert.Equal([HotkeyTransition.Activated], h.TakeTransitions().Select(t => t.Transition));
        Assert.False(h.Engine.OnKeyEvent(F20, isDown: false, eventTime: Moved + 120).Suppress);
    }

    [Fact]
    public void A_windows_key_chord_member_inside_the_window_is_not_swallowed_on_its_own_press()
    {
        var binding = new HotkeyBinding(KeyH, KeyModifiers.Win, HotkeyMode.Hold, Suppress: true, SuppressChordMembers: true);
        using var h = new HotkeyEngineHarness(binding);
        h.Engine.SetUncertaintyWindow(Window);
        h.Engine.OnRegisteredAhead(Moved);

        Assert.False(h.Engine.OnKeyEvent(LeftWin, isDown: true, eventTime: Moved + 10).Suppress);
        Assert.False(h.Engine.OnKeyEvent(LeftWin, isDown: false, eventTime: Moved + 60).Suppress);
    }

    [Fact]
    public void The_route_hands_the_engine_the_event_s_time()
    {
        using var h = Armed(HotkeyBinding.Legacy);
        var passOn = new KeyEventPassOn();

        var inside = KeyboardHookFilter.Route(
            h.Engine, passOn, throughCurrentRegistration: true, new KeyEventIdentity(RightCtrl, 0x1D, 0x01, Moved + 10), isDown: true, 0);
        KeyboardHookFilter.Route(
            h.Engine, passOn, throughCurrentRegistration: true, new KeyEventIdentity(RightCtrl, 0x1D, 0x81, Moved + 40), isDown: false, 0);
        var ordinary = KeyboardHookFilter.Route(
            h.Engine, passOn, throughCurrentRegistration: true, new KeyEventIdentity(RightCtrl, 0x1D, 0x01, Moved + 70), isDown: true, 0);

        Assert.False(inside.Swallow);
        Assert.True(inside.TrackPass);
        Assert.True(ordinary.Swallow);
    }

    [Theory]
    [InlineData(0, 31, 563)]
    [InlineData(1, 31, 875)] // Windows' defaults
    [InlineData(2, 31, 1188)]
    [InlineData(3, 31, 1500)]
    [InlineData(0, 0, 750)] // the slowest repeat outlasts the shortest delay
    [InlineData(3, 0, 1500)]
    [InlineData(-1, 31, 563)] // out of range: clamped to what Windows documents
    [InlineData(9, 40, 1500)]
    public void The_window_is_the_longer_of_the_repeat_delay_and_period_with_a_quarter_to_spare_plus_the_margin(
        int delay, int speed, int expected)
    {
        Assert.Equal(expected, KeyRepeatTiming.UncertaintyWindowMs(delay, speed));
    }

    [Fact]
    public void The_worst_case_is_the_slowest_setting_and_this_machine_s_window_lies_between_the_extremes()
    {
        Assert.Equal(KeyRepeatTiming.WorstCaseWindowMs, KeyRepeatTiming.UncertaintyWindowMs(3, 0));
        Assert.Equal(250, KeyRepeatTiming.MarginMs);

        var read = KeyRepeatTiming.ReadUncertaintyWindowMs();
        Assert.InRange(read, KeyRepeatTiming.UncertaintyWindowMs(0, 31), KeyRepeatTiming.WorstCaseWindowMs);
    }

    private static HotkeyEngineHarness Armed(HotkeyBinding binding)
    {
        var h = new HotkeyEngineHarness(binding);
        h.Engine.SetUncertaintyWindow(Window);
        h.Engine.OnRegisteredAhead(Moved);
        return h;
    }
}
