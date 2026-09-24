using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// Page Up or Page Down bound on its own, as the new defaults are, starts only when no modifier is held: a bare press
/// dictates, while Ctrl with them (switching tabs in browsers, editors and Excel), Shift with them (selecting a page),
/// Alt, Win, Ctrl+Alt (Narrator's table commands) and a Narrator key (Caps Lock, Insert or NonConvert: changing views)
/// reach the app whole and start nothing. A modifier pressed during a dictation neither ends it nor lets the bound key
/// through; a modifier whose release the hook never saw blocks nothing once Windows reports it up, and a Narrator key
/// released on another desktop blocks nothing once the desktop switch is seen. Every other binding, custom ones an
/// existing install already has included, matches exactly as it did before, whatever else is held.
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
    private const uint KeyH = 0x48;
    private const uint KeyX = 0x58;
    private const uint F9 = 0x78;

    // Every combination a bare Page Up or Page Down must let through, by name.
    private static readonly Dictionary<string, uint[]> Combinations = new()
    {
        ["Left Ctrl"] = [LeftCtrl],
        ["Right Ctrl"] = [RightCtrl],
        ["Left Shift"] = [LeftShift],
        ["Right Shift"] = [RightShift],
        ["Left Alt"] = [LeftAlt],
        ["Right Alt"] = [RightAlt],
        ["Left Win"] = [LeftWin],
        ["Right Win"] = [RightWin],
        ["Ctrl+Alt"] = [LeftCtrl, LeftAlt],
        ["Ctrl+Shift"] = [LeftCtrl, LeftShift],
        ["AltGr (the Left Ctrl Windows adds, then Right Alt)"] = [LeftCtrl, RightAlt],
        ["Narrator key Caps Lock"] = [CapsLock],
        ["Narrator key Insert"] = [Insert],
        ["Narrator key NonConvert (Japanese 106 keyboard)"] = [NonConvert],
        ["injected generic Ctrl"] = [Ctrl],
        ["injected generic Shift"] = [Shift],
        ["injected generic Alt"] = [Alt],
    };

    // Bindings an existing install may already have, none a bare Page Up or Page Down, with the keys that complete each
    // in the order they are pressed.
    private static readonly Dictionary<string, (HotkeyBinding Binding, uint[] Keys)> OtherBindings = new()
    {
        ["F9"] = (new HotkeyBinding(F9, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F9"), [F9]),
        ["Ctrl+Shift+X"] = (
            new HotkeyBinding(KeyX, KeyModifiers.Control | KeyModifiers.Shift, HotkeyMode.Hold, Suppress: true),
            [LeftCtrl, LeftShift, KeyX]),
        ["Right Ctrl"] = (HotkeyBinding.Legacy, [RightCtrl]),
        ["Right Alt"] = (new HotkeyBinding(RightAlt, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Right Alt"), [RightAlt]),
        ["Caps Lock"] = (new HotkeyBinding(CapsLock, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Caps Lock"), [CapsLock]),
        ["Insert"] = (new HotkeyBinding(Insert, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Insert"), [Insert]),
        ["Ctrl+Page Down"] = (
            new HotkeyBinding(PageDown, KeyModifiers.Control, HotkeyMode.Hold, Suppress: true),
            [LeftCtrl, PageDown]),
        ["Page Down+Page Up"] = (
            new HotkeyBinding(
                PageDown, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Page Down+Page Up",
                SecondaryVirtualKey: PageUp, SuppressChordMembers: true),
            [PageDown, PageUp]),
        ["Page Up+X"] = (
            new HotkeyBinding(
                PageUp, KeyModifiers.None, HotkeyMode.Toggle, Suppress: true, "Page Up+X",
                SecondaryVirtualKey: KeyX, SuppressChordMembers: true),
            [PageUp, KeyX]),
        ["Left Win+H"] = (
            new HotkeyBinding(
                LeftWin, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Left Win+H",
                SecondaryVirtualKey: KeyH, SuppressChordMembers: true),
            [LeftWin, KeyH]),
    };

    public static TheoryData<string, uint[]> Modifiers
    {
        get
        {
            var data = new TheoryData<string, uint[]>();
            foreach (var (name, keys) in Combinations)
            {
                data.Add(name, keys);
            }

            return data;
        }
    }

    public static TheoryData<string, string> OtherBindingsWithEachCombination
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var binding in OtherBindings.Keys)
            {
                foreach (var combination in Combinations.Keys)
                {
                    data.Add(binding, combination);
                }
            }

            return data;
        }
    }

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
        // Only a bare Page Up or Page Down is judged, so a Right Ctrl binding, the one every earlier release shipped,
        // does not regress.
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
    public void Shift_f9_activates_as_it_did_before()
    {
        // F9 bound on its own fired whatever else was held, and an existing install that relies on that keeps it.
        var binding = new HotkeyBinding(F9, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F9");

        foreach (var modifiers in new uint[][] { [], [RightShift], [LeftShift], [LeftCtrl], [LeftAlt], [CapsLock] })
        {
            var press = Chord(binding, modifiers, F9);
            Assert.Equal(HotkeyTransition.Activated, press.Transition);
            Assert.True(press.ShouldSuppress);
        }
    }

    [Fact]
    public void Ctrl_shift_x_with_alt_held_activates_as_it_did_before()
    {
        var binding = new HotkeyBinding(KeyX, KeyModifiers.Control | KeyModifiers.Shift, HotkeyMode.Hold, Suppress: true);

        Assert.Equal(HotkeyTransition.Activated, Chord(binding, [LeftCtrl, LeftShift], KeyX).Transition);
        Assert.Equal(HotkeyTransition.Activated, Chord(binding, [RightCtrl, RightShift], KeyX).Transition);

        var extraAlt = Chord(binding, [LeftCtrl, LeftShift, LeftAlt], KeyX);
        Assert.Equal(HotkeyTransition.Activated, extraAlt.Transition);
        Assert.True(extraAlt.ShouldSuppress);

        // Completed by its last modifier, with Alt held, as before too.
        Assert.Equal(HotkeyTransition.Activated, Chord(binding, [LeftAlt, LeftShift, KeyX], LeftCtrl).Transition);

        // A named modifier missing is still not the binding.
        var ctrlOnly = Chord(binding, [LeftCtrl], KeyX);
        Assert.Equal(HotkeyTransition.None, ctrlOnly.Transition);
        Assert.False(ctrlOnly.ShouldSuppress);
    }

    [Theory]
    [MemberData(nameof(OtherBindingsWithEachCombination))]
    public void Any_binding_but_a_bare_page_key_fires_as_before_whatever_else_is_held(string bindingName, string combination)
    {
        // The keys of the combination that belong to the binding are pressed as part of it instead.
        var (binding, keys) = OtherBindings[bindingName];
        var windows = new WindowsKeyboard();
        var state = new ChordStateMachine(binding, windows.IsDown);
        foreach (var key in Combinations[combination].Where(key => !ChordStateMachine.IsBindingKey(binding, key)))
        {
            windows.Press(key);
            state.Process(key, isDown: true);
        }

        var completing = default(ChordUpdate);
        foreach (var key in keys)
        {
            windows.Press(key);
            completing = state.Process(key, isDown: true);
        }

        Assert.Equal(HotkeyTransition.Activated, completing.Transition);
        Assert.True(completing.ShouldSuppress);
        Assert.Empty(windows.Asked); // Windows is asked about nothing for any of these bindings
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
    [InlineData(CapsLock)]
    [InlineData(Insert)]
    [InlineData(NonConvert)]
    public void A_narrator_key_released_on_the_lock_screen_stops_blocking_once_the_desktop_switch_is_seen(uint narratorKey)
    {
        // Held when Win+L locked the PC and released on the lock screen, where the hook is not called. Windows reports
        // the key up, as it would while Narrator keeps it, so that does not settle it: the hook's view holds the key and
        // the bare press is refused until the desktop switch reaches the engine, which forgets it.
        using var h = new HotkeyEngineHarness(
            HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly, isLogicallyDown: new WindowsKeyboard().IsDown);
        h.Down(narratorKey);
        Assert.False(h.Down(PageDown).Suppress);
        Assert.False(h.Up(PageDown).Suppress);
        Assert.Empty(h.TakeTransitions());

        h.Engine.OnDesktopSwitch();

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

        // Pressed again after the switch, the Narrator key counts again.
        h.Down(narratorKey);
        Assert.False(h.Down(PageDown).Suppress);
        Assert.Empty(h.TakeTransitions());
        Assert.Equal(1, h.Engine.DesktopSwitches);
    }

    [Fact]
    public void A_desktop_switch_leaves_a_narrator_key_that_is_itself_the_binding()
    {
        // Caps Lock bound as push-to-talk and held through the switch: its dictation is ended by its own release, as
        // before, and the switch starts or stops nothing.
        var capsLock = new HotkeyBinding(CapsLock, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Caps Lock");
        using var h = new HotkeyEngineHarness(capsLock);

        Assert.True(h.Down(CapsLock).Suppress);
        h.Engine.OnDesktopSwitch();
        Assert.True(h.Down(CapsLock).Suppress); // an autorepeat, still swallowed
        Assert.True(h.Up(CapsLock).Suppress);

        Assert.Equal(
            new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
            h.TakeTransitions().Select(t => t.Transition).ToArray());
    }

    [Fact]
    public void A_retired_engine_ignores_a_desktop_switch()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation);
        h.Down(CapsLock);
        h.Router.EndEngine();

        h.Engine.OnDesktopSwitch();

        Assert.Equal(0, h.Engine.DesktopSwitches);
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
    public void Windows_is_asked_only_about_a_modifier_the_hook_sees_on_the_press_that_completes_a_bare_page_key()
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

        // Any other binding is never judged, so nothing is asked: Ctrl+Shift+X with Alt held as well.
        var other = new WindowsKeyboard();
        var ctrlShiftX = new ChordStateMachine(
            new HotkeyBinding(KeyX, KeyModifiers.Control | KeyModifiers.Shift, HotkeyMode.Hold, Suppress: true), other.IsDown);
        foreach (var key in new[] { RightCtrl, LeftShift, LeftAlt })
        {
            other.Press(key);
            ctrlShiftX.Process(key, isDown: true);
        }

        Assert.Equal(HotkeyTransition.Activated, ctrlShiftX.Process(KeyX, isDown: true).Transition);
        Assert.Empty(other.Asked);

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
