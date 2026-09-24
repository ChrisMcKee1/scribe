using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// A binding made only of ordinary keys starts only when the modifiers held are exactly the ones it names. With the new
/// defaults a bare Page Down or Page Up dictates, while Ctrl with them (switching tabs in browsers, editors and Excel),
/// Shift with them (selecting a page), Alt, Win, Ctrl+Alt (Narrator's table commands) and a Narrator key (Caps Lock,
/// Insert or NonConvert: changing views) reach the app whole and start nothing. A modifier pressed during a dictation
/// neither ends it nor lets the bound key through, a modifier whose release the hook never saw blocks nothing once
/// Windows reports it up, and a binding that includes a modifier key keeps matching as it always has.
/// </summary>
public sealed class HotkeyModifierTests
{
    private const uint PageDown = 0x22;
    private const uint PageUp = 0x21;
    private const uint Shift = 0x10;
    private const uint Ctrl = 0x11;
    private const uint Alt = 0x12;
    private const uint CapsLock = 0x14;
    private const uint NonConvert = 0x1D;
    private const uint Insert = 0x2D;
    private const uint LeftWin = 0x5B;
    private const uint RightWin = 0x5C;
    private const uint LeftShift = 0xA0;
    private const uint RightShift = 0xA1;
    private const uint LeftCtrl = 0xA2;
    private const uint RightCtrl = 0xA3;
    private const uint LeftAlt = 0xA4;
    private const uint RightAlt = 0xA5;
    private const uint KeyX = 0x58;
    private const uint F9 = 0x78;

    public static TheoryData<string, uint[]> Modifiers => new()
    {
        { "Left Ctrl", [LeftCtrl] },
        { "Right Ctrl", [RightCtrl] },
        { "Left Shift", [LeftShift] },
        { "Right Shift", [RightShift] },
        { "Left Alt", [LeftAlt] },
        { "Right Alt", [RightAlt] },
        { "Left Win", [LeftWin] },
        { "Right Win", [RightWin] },
        { "Ctrl+Alt", [LeftCtrl, LeftAlt] },
        { "Ctrl+Shift", [LeftCtrl, LeftShift] },
        { "AltGr (the Left Ctrl Windows adds, then Right Alt)", [LeftCtrl, RightAlt] },
        { "Narrator key Caps Lock", [CapsLock] },
        { "Narrator key Insert", [Insert] },
        { "Narrator key NonConvert (Japanese 106 keyboard)", [NonConvert] },
        { "injected generic Ctrl", [Ctrl] },
        { "injected generic Shift", [Shift] },
        { "injected generic Alt", [Alt] },
    };

    // Modifiers the hook saw go down whose release it never saw: Win+L locks before the keys come up, and Ctrl+Alt+Del
    // shows the secure desktop, where a hook on the user's desktop is not called.
    public static TheoryData<string, uint[]> MissedReleases => new()
    {
        { "Win+L", [LeftWin] },
        { "Ctrl+Alt+Del", [LeftCtrl, LeftAlt] },
        { "Ctrl+Shift+Enter into a UAC prompt", [LeftCtrl, LeftShift] },
        { "Right Win", [RightWin] },
        { "Right Alt", [RightAlt] },
    };

    [Theory]
    [MemberData(nameof(Modifiers))]
    public void A_page_key_pressed_with_a_modifier_reaches_the_app_and_starts_nothing(string combination, uint[] modifiers)
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);

        foreach (var key in new[] { PageDown, PageUp })
        {
            foreach (var modifier in modifiers)
            {
                Assert.False(h.Down(modifier).Suppress, combination);
            }

            Assert.False(h.Down(key).Suppress, combination);
            Assert.False(h.Down(key).Suppress, combination); // autorepeat
            Assert.False(h.Up(key).Suppress, combination);
            foreach (var modifier in modifiers.Reverse())
            {
                Assert.False(h.Up(modifier).Suppress, combination);
            }

            Assert.Empty(h.TakeTransitions());

            // Nothing was left behind: the bare key dictates straight after.
            Assert.True(h.Down(key).Suppress, combination);
            Assert.True(h.Up(key).Suppress, combination);
            Assert.Equal(
                new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
                h.TakeTransitions().Select(t => t.Transition).ToArray());
        }
    }

    [Fact]
    public void A_bare_page_key_still_dictates_through_each_trigger()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);

        Assert.True(h.Down(PageDown).Suppress);
        Assert.True(h.Up(PageDown).Suppress);
        Assert.True(h.Down(PageUp).Suppress);
        Assert.True(h.Up(PageUp).Suppress);

        Assert.Equal(
            new[]
            {
                (HotkeyTransition.Activated, HotkeyTrigger.Standard),
                (HotkeyTransition.Deactivated, HotkeyTrigger.Standard),
                (HotkeyTransition.Activated, HotkeyTrigger.DictationOnly),
                (HotkeyTransition.Deactivated, HotkeyTrigger.DictationOnly),
            },
            h.TakeTransitions().Select(t => (t.Transition, t.Trigger)).ToArray());
    }

    [Fact]
    public void Releasing_the_modifier_first_still_leaves_that_whole_keystroke_to_the_app()
    {
        // Ctrl+Page Down, then Ctrl let go with Page Down still held: its autorepeats and its release get the decision
        // its press got, so the app never sees half a keystroke and nothing starts part way through.
        var state = new ChordStateMachine(HotkeyBinding.DefaultDictation);

        state.Process(LeftCtrl, isDown: true);
        var press = state.Process(PageDown, isDown: true);
        state.Process(LeftCtrl, isDown: false);
        var repeat = state.Process(PageDown, isDown: true);
        var release = state.Process(PageDown, isDown: false);

        Assert.All(new[] { press, repeat, release }, update =>
        {
            Assert.Equal(HotkeyTransition.None, update.Transition);
            Assert.False(update.ShouldSuppress);
        });
    }

    [Theory]
    [MemberData(nameof(Modifiers))]
    public void A_modifier_pressed_during_a_dictation_neither_ends_it_nor_lets_the_key_through(
        string combination, uint[] modifiers)
    {
        var state = new ChordStateMachine(HotkeyBinding.DefaultDictation);

        var press = state.Process(PageDown, isDown: true);
        foreach (var modifier in modifiers)
        {
            var update = state.Process(modifier, isDown: true);
            Assert.Equal(HotkeyTransition.None, update.Transition);
            Assert.False(update.ShouldSuppress, combination); // the modifier itself reaches the app
        }

        var repeat = state.Process(PageDown, isDown: true);
        foreach (var modifier in modifiers.Reverse())
        {
            Assert.Equal(HotkeyTransition.None, state.Process(modifier, isDown: false).Transition);
        }

        var release = state.Process(PageDown, isDown: false);

        Assert.Equal(HotkeyTransition.Activated, press.Transition);
        Assert.True(press.ShouldSuppress);
        Assert.Equal(HotkeyTransition.None, repeat.Transition);
        Assert.True(repeat.ShouldSuppress, combination);
        Assert.Equal(HotkeyTransition.Deactivated, release.Transition);
        Assert.True(release.ShouldSuppress, combination);
    }

    [Fact]
    public void A_modified_press_neither_starts_nor_stops_a_toggle()
    {
        var state = new ChordStateMachine(HotkeyBinding.DefaultDictation with { Mode = HotkeyMode.Toggle });

        Assert.Equal(HotkeyTransition.None, PressWith(state, LeftCtrl, PageDown).Transition);
        Assert.Equal(HotkeyTransition.Activated, Press(state, PageDown).Transition);

        // Switching tabs during a toggled dictation: the tab switch happens and the dictation goes on.
        var switchTabs = PressWith(state, LeftCtrl, PageDown);
        Assert.Equal(HotkeyTransition.None, switchTabs.Transition);
        Assert.False(switchTabs.ShouldSuppress);

        Assert.Equal(HotkeyTransition.Deactivated, Press(state, PageDown).Transition);
    }

    [Fact]
    public void Right_ctrl_keeps_firing_with_another_modifier_held()
    {
        // A binding that includes a modifier key keeps its old match, so a Right Ctrl binding does not regress.
        var state = new ChordStateMachine(HotkeyBinding.Legacy);

        var bare = Press(state, RightCtrl);
        var withShift = PressWith(state, LeftShift, RightCtrl);

        Assert.Equal(HotkeyTransition.Activated, bare.Transition);
        Assert.True(bare.ShouldSuppress);
        Assert.Equal(HotkeyTransition.Activated, withShift.Transition);
        Assert.True(withShift.ShouldSuppress);
    }

    [Fact]
    public void Right_alt_keeps_firing_on_a_layout_with_altgr()
    {
        // Windows reports a Left Ctrl press with every AltGr press; an exact check would never let this binding fire.
        var state = new ChordStateMachine(
            new HotkeyBinding(RightAlt, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Right Alt"));

        var altGr = PressWith(state, LeftCtrl, RightAlt);

        Assert.Equal(HotkeyTransition.Activated, altGr.Transition);
        Assert.True(altGr.ShouldSuppress);
    }

    [Fact]
    public void A_chord_of_modifier_keys_keeps_its_match()
    {
        var chord = new HotkeyBinding(
            RightCtrl, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Right Ctrl+Right Shift",
            SecondaryVirtualKey: RightShift);
        var state = new ChordStateMachine(chord);

        state.Process(LeftAlt, isDown: true);
        state.Process(RightCtrl, isDown: true);
        var complete = state.Process(RightShift, isDown: true);

        Assert.Equal(HotkeyTransition.Activated, complete.Transition);
        Assert.True(complete.ShouldSuppress);
    }

    [Fact]
    public void Ctrl_shift_x_fires_with_exactly_ctrl_and_shift_from_either_side()
    {
        var binding = new HotkeyBinding(KeyX, KeyModifiers.Control | KeyModifiers.Shift, HotkeyMode.Hold, Suppress: true);

        Assert.Equal(HotkeyTransition.Activated, Chord(binding, [LeftCtrl, LeftShift], KeyX).Transition);
        Assert.Equal(HotkeyTransition.Activated, Chord(binding, [RightCtrl, RightShift], KeyX).Transition);
        Assert.Equal(HotkeyTransition.Activated, Chord(binding, [LeftShift, LeftCtrl], KeyX).Transition);

        // Another modifier makes it another shortcut (Ctrl+Alt+Shift+X), which reaches the app.
        var extraAlt = Chord(binding, [LeftCtrl, LeftShift, LeftAlt], KeyX);
        Assert.Equal(HotkeyTransition.None, extraAlt.Transition);
        Assert.False(extraAlt.ShouldSuppress);

        // A named modifier missing is not the binding either, as before.
        var ctrlOnly = Chord(binding, [LeftCtrl], KeyX);
        Assert.Equal(HotkeyTransition.None, ctrlOnly.Transition);
        Assert.False(ctrlOnly.ShouldSuppress);
    }

    [Fact]
    public void Ctrl_shift_x_completed_by_its_last_modifier_is_judged_the_same_way()
    {
        // Pressing the key first and a named modifier last completes the binding on the modifier's press.
        var binding = new HotkeyBinding(KeyX, KeyModifiers.Control | KeyModifiers.Shift, HotkeyMode.Hold, Suppress: true);

        Assert.Equal(HotkeyTransition.Activated, Chord(binding, [LeftShift, KeyX], LeftCtrl).Transition);
        Assert.Equal(HotkeyTransition.None, Chord(binding, [LeftAlt, LeftShift, KeyX], LeftCtrl).Transition);
    }

    [Fact]
    public void A_function_key_fires_bare_and_passes_with_a_modifier()
    {
        var binding = new HotkeyBinding(F9, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F9");

        var bare = Chord(binding, [], F9);
        var ctrlF9 = Chord(binding, [LeftCtrl], F9);
        var shiftF9 = Chord(binding, [RightShift], F9);

        Assert.Equal(HotkeyTransition.Activated, bare.Transition);
        Assert.True(bare.ShouldSuppress);
        Assert.Equal(HotkeyTransition.None, ctrlF9.Transition);
        Assert.False(ctrlF9.ShouldSuppress);
        Assert.Equal(HotkeyTransition.None, shiftF9.Transition);
        Assert.False(shiftF9.ShouldSuppress);
    }

    [Theory]
    [InlineData(Insert, CapsLock)]
    [InlineData(CapsLock, Insert)]
    [InlineData(CapsLock, NonConvert)]
    [InlineData(NonConvert, Insert)]
    public void A_bound_narrator_key_is_not_counted_against_itself(uint bound, uint otherNarratorKey)
    {
        // A Narrator key bound as push-to-talk: its own press fires it, while another Narrator key held still makes the
        // press a Narrator command.
        var binding = new HotkeyBinding(bound, KeyModifiers.None, HotkeyMode.Hold, Suppress: true);

        Assert.Equal(HotkeyTransition.Activated, Chord(binding, [], bound).Transition);
        Assert.Equal(HotkeyTransition.None, Chord(binding, [otherNarratorKey], bound).Transition);
    }

    [Theory]
    [MemberData(nameof(MissedReleases))]
    public void A_modifier_released_where_the_hook_could_not_see_it_does_not_block_the_bare_key(
        string combination, uint[] modifiers)
    {
        // Windows saw the release, so it reports the modifier up: the bare press dictates, through each trigger.
        var windows = new WindowsKeyboard();
        using var h = new HotkeyEngineHarness(
            HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly, isLogicallyDown: windows.IsDown);
        foreach (var modifier in modifiers)
        {
            h.Down(modifier);
        }

        Assert.True(h.Down(PageDown).Suppress, combination);
        Assert.True(h.Up(PageDown).Suppress, combination);
        Assert.True(h.Down(PageUp).Suppress, combination);
        Assert.True(h.Up(PageUp).Suppress, combination);
        Assert.Equal(
            new[]
            {
                (HotkeyTransition.Activated, HotkeyTrigger.Standard),
                (HotkeyTransition.Deactivated, HotkeyTrigger.Standard),
                (HotkeyTransition.Activated, HotkeyTrigger.DictationOnly),
                (HotkeyTransition.Deactivated, HotkeyTrigger.DictationOnly),
            },
            h.TakeTransitions().Select(t => (t.Transition, t.Trigger)).ToArray());
    }

    [Theory]
    [MemberData(nameof(Modifiers))]
    public void A_modifier_windows_also_reports_down_still_makes_it_another_command(string combination, uint[] modifiers)
    {
        // The Narrator keys are held for the hook only, as when Narrator keeps them from Windows.
        var windows = new WindowsKeyboard(narratorKeysHidden: true);
        using var h = new HotkeyEngineHarness(
            HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly, isLogicallyDown: windows.IsDown);

        foreach (var key in new[] { PageDown, PageUp })
        {
            foreach (var modifier in modifiers)
            {
                windows.Press(modifier);
                Assert.False(h.Down(modifier).Suppress, combination);
            }

            Assert.False(h.Down(key).Suppress, combination);
            Assert.False(h.Up(key).Suppress, combination);
            foreach (var modifier in modifiers.Reverse())
            {
                windows.Release(modifier);
                h.Up(modifier);
            }
        }

        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void Windows_is_asked_only_about_a_modifier_the_hook_sees_on_the_press_that_completes_the_binding()
    {
        var windows = new WindowsKeyboard();
        var state = new ChordStateMachine(HotkeyBinding.DefaultDictation, windows.IsDown);

        // A bare press, its repeat and its release ask nothing.
        state.Process(PageDown, isDown: true);
        state.Process(PageDown, isDown: true);
        state.Process(PageDown, isDown: false);
        Assert.Empty(windows.Asked);

        // Ctrl held: the one question is about Ctrl, on either side at once, and never about Page Down itself.
        windows.Press(LeftCtrl);
        state.Process(LeftCtrl, isDown: true);
        state.Process(PageDown, isDown: true);
        state.Process(PageDown, isDown: true);
        state.Process(PageDown, isDown: false);
        Assert.Equal(new[] { Ctrl }, windows.Asked);

        // A named modifier is not asked about either: Ctrl+Shift+X with exactly Ctrl and Shift held asks nothing.
        var named = new WindowsKeyboard();
        var ctrlShiftX = new ChordStateMachine(
            new HotkeyBinding(KeyX, KeyModifiers.Control | KeyModifiers.Shift, HotkeyMode.Hold, Suppress: true), named.IsDown);
        foreach (var key in new[] { RightCtrl, LeftShift })
        {
            named.Press(key);
            ctrlShiftX.Process(key, isDown: true);
        }

        Assert.Equal(HotkeyTransition.Activated, ctrlShiftX.Process(KeyX, isDown: true).Transition);
        Assert.Empty(named.Asked);

        // Paused, nothing is refused, so nothing is asked either.
        var paused = new WindowsKeyboard();
        var pausedState = new ChordStateMachine(HotkeyBinding.DefaultDictation, paused.IsDown);
        pausedState.SetPaused(true);
        paused.Press(LeftCtrl);
        pausedState.Process(LeftCtrl, isDown: true);
        Assert.Equal(HotkeyTransition.None, pausedState.Process(PageDown, isDown: true).Transition);
        Assert.Empty(paused.Asked);
    }

    [Fact]
    public void A_modifier_only_windows_reports_down_does_not_refuse_the_press()
    {
        // The hook's view decides that a modifier is held; Windows' view can only take one away. So a key Windows
        // believes is stuck down (left so by another app) does not stop push-to-talk.
        var windows = new WindowsKeyboard();
        windows.Press(LeftCtrl);
        var state = new ChordStateMachine(HotkeyBinding.DefaultDictation, windows.IsDown);

        var press = state.Process(PageDown, isDown: true);

        Assert.Equal(HotkeyTransition.Activated, press.Transition);
        Assert.True(press.ShouldSuppress);
    }

    [Fact]
    public void A_dictation_only_binding_added_later_gets_windows_view_too()
    {
        var windows = new WindowsKeyboard();
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, isLogicallyDown: windows.IsDown);
        h.Router.UpdateBindings(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);

        h.Down(LeftWin); // Win+L: its release never reached the hook
        Assert.True(h.Down(PageUp).Suppress);
        Assert.Equal(
            new[] { (HotkeyTransition.Activated, HotkeyTrigger.DictationOnly) },
            h.TakeTransitions().Select(t => (t.Transition, t.Trigger)).ToArray());
    }

    [Fact]
    public void The_hook_service_gives_its_engines_get_async_key_state()
    {
        // The service builds the router every engine comes from; without Windows' view there, a modifier released on
        // the lock screen would block the bare key again. Constructing the service installs no hook.
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);

        Assert.Equal((Func<uint, bool>)NativeMethods.IsKeyLogicallyDown, service.WindowsKeyState);
    }

    // A fresh machine, the given keys pressed in order, and the update for the last key's press.
    private static ChordUpdate Chord(HotkeyBinding binding, uint[] heldFirst, uint last)
    {
        var state = new ChordStateMachine(binding);
        foreach (var key in heldFirst)
        {
            state.Process(key, isDown: true);
        }

        return state.Process(last, isDown: true);
    }

    // Presses and releases the key, returning the press's update.
    private static ChordUpdate Press(ChordStateMachine state, uint key)
    {
        var press = state.Process(key, isDown: true);
        state.Process(key, isDown: false);
        return press;
    }

    // Holds the modifier, presses and releases the key, lets the modifier go, and returns the key press's update.
    private static ChordUpdate PressWith(ChordStateMachine state, uint modifier, uint key)
    {
        state.Process(modifier, isDown: true);
        var press = Press(state, key);
        state.Process(modifier, isDown: false);
        return press;
    }

    // Windows' view of the keyboard as GetAsyncKeyState reports it, where a generic Ctrl, Alt or Shift is down while
    // either side is. It records every key it is asked about. With narratorKeysHidden, a Narrator key never goes down
    // here, as when Narrator keeps it from the rest of Windows.
    private sealed class WindowsKeyboard(bool narratorKeysHidden = false)
    {
        private readonly HashSet<uint> _down = [];

        public List<uint> Asked { get; } = [];

        public void Press(uint key)
        {
            if (!(narratorKeysHidden && key is CapsLock or Insert or NonConvert))
            {
                _down.Add(key);
            }
        }

        public void Release(uint key) => _down.Remove(key);

        public bool IsDown(uint key)
        {
            Asked.Add(key);
            return key switch
            {
                Ctrl => _down.Overlaps([Ctrl, LeftCtrl, RightCtrl]),
                Alt => _down.Overlaps([Alt, LeftAlt, RightAlt]),
                Shift => _down.Overlaps([Shift, LeftShift, RightShift]),
                _ => _down.Contains(key),
            };
        }
    }
}
