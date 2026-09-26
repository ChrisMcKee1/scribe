using System.Runtime.InteropServices;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The middle, Back and Forward mouse buttons as hotkeys. A button is stored as its virtual-key code and reaches the
/// same engine as the keyboard, so every rule the keys have holds for it: hold and toggle, both triggers, a chord with a
/// key, swallowed while it drives a dictation and passed through otherwise, the bare-binding modifier rule the Page keys
/// have, the desktop-switch reset, pause, capture mode, a reinstall and a retired engine. The mouse hook's filter hands
/// every other mouse message on without reading it, and a keyboard event carrying a button's code is not the button.
/// </summary>
public sealed class MouseButtonHotkeyTests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;
    private const uint Forward = MouseButtons.Forward;
    private const uint PageDown = 0x22;
    private const uint PageUp = 0x21;
    private const uint Ctrl = 0x11;
    private const uint Shift = 0x10;
    private const uint CapsLock = 0x14;
    private const uint Insert = 0x2D;
    private const uint LeftWin = 0x5B;
    private const uint LeftShift = 0xA0;
    private const uint RightShift = 0xA1;
    private const uint LeftCtrl = 0xA2;
    private const uint RightCtrl = 0xA3;
    private const uint LeftAlt = 0xA4;
    private const uint RightAlt = 0xA5;
    private const uint F9 = 0x78;

    public static TheoryData<uint> Buttons => new() { Middle, Back, Forward };

    // A bare button pressed with any of these is meant for the app under the pointer, as a bare Page key is.
    public static TheoryData<uint, string, uint[]> ButtonsWithModifiers
    {
        get
        {
            var combinations = new Dictionary<string, uint[]>
            {
                ["Left Ctrl"] = [LeftCtrl],
                ["Right Ctrl"] = [RightCtrl],
                ["Left Shift"] = [LeftShift],
                ["Right Shift"] = [RightShift],
                ["Left Alt"] = [LeftAlt],
                ["Right Alt"] = [RightAlt],
                ["Left Win"] = [LeftWin],
                ["Ctrl+Shift"] = [LeftCtrl, LeftShift],
                ["AltGr"] = [LeftCtrl, RightAlt],
                ["Narrator key Caps Lock"] = [CapsLock],
                ["Narrator key Insert"] = [Insert],
                ["injected generic Ctrl"] = [Ctrl],
                ["injected generic Shift"] = [Shift],
            };
            var data = new TheoryData<uint, string, uint[]>();
            foreach (var button in new[] { Middle, Back, Forward })
            {
                foreach (var (name, keys) in combinations)
                {
                    data.Add(button, name, keys);
                }
            }

            return data;
        }
    }

    private static HotkeyBinding Bare(uint button, HotkeyMode mode = HotkeyMode.Hold) =>
        HotkeyCaptureSession.Build([button], mode);

    private static HotkeyBinding Chord(uint first, uint second, HotkeyMode mode = HotkeyMode.Hold) =>
        HotkeyCaptureSession.Build([first, second], mode);

    private static (HotkeyTransition, HotkeyTrigger)[] Transitions(HotkeyEngineHarness h) =>
        h.TakeTransitions().Select(t => (t.Transition, t.Trigger)).ToArray();

    [Theory]
    [MemberData(nameof(Buttons))]
    public void A_held_button_dictates_and_its_press_and_release_never_reach_the_app(uint button)
    {
        using var h = new HotkeyEngineHarness(Bare(button));

        var down = h.ButtonDown(button);
        var up = h.ButtonUp(button);

        Assert.True(down.Suppress);
        Assert.True(up.Suppress);
        Assert.True(up.RequestReconcile); // a swallowed release is the moment the leak check runs, as for a key
        var transitions = h.TakeTransitions();
        Assert.Equal(
            new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
            transitions.Select(t => t.Transition).ToArray());
        Assert.True(h.WouldDispatch(transitions[0]));
        Assert.True(transitions[0].Activation > 0);
        Assert.Equal(2, h.Engine.MouseButtonEvents);
    }

    [Theory]
    [MemberData(nameof(Buttons))]
    public void A_toggled_button_starts_on_one_click_and_stops_on_the_next(uint button)
    {
        using var h = new HotkeyEngineHarness(Bare(button, HotkeyMode.Toggle));

        var first = h.Click(button);
        Assert.Equal(new[] { (HotkeyTransition.Activated, HotkeyTrigger.Standard) }, Transitions(h));
        var second = h.Click(button);
        Assert.Equal(new[] { (HotkeyTransition.Deactivated, HotkeyTrigger.Standard) }, Transitions(h));

        Assert.All(new[] { first.Down, first.Up, second.Down, second.Up }, decision => Assert.True(decision.Suppress));
    }

    [Fact]
    public void A_button_on_the_dictation_only_trigger_starts_its_own_dictation()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, Bare(Forward));

        Assert.True(h.ButtonDown(Forward).Suppress);
        Assert.True(h.ButtonUp(Forward).Suppress);
        Assert.True(h.Down(PageDown).Suppress);
        Assert.True(h.Up(PageDown).Suppress);

        Assert.Equal(
            new[]
            {
                (HotkeyTransition.Activated, HotkeyTrigger.DictationOnly),
                (HotkeyTransition.Deactivated, HotkeyTrigger.DictationOnly),
                (HotkeyTransition.Activated, HotkeyTrigger.Standard),
                (HotkeyTransition.Deactivated, HotkeyTrigger.Standard),
            },
            Transitions(h));
    }

    [Fact]
    public void A_button_no_binding_presses_reaches_the_app_and_starts_nothing()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));

        foreach (var other in new[] { Middle, Forward })
        {
            var (down, up) = h.Click(other);
            Assert.False(down.Suppress);
            Assert.False(up.Suppress);
            Assert.False(up.RequestReconcile);
        }

        Assert.Empty(h.TakeTransitions());
        Assert.True(h.ButtonDown(Back).Suppress); // and the bound one still works
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Fact]
    public void Keyboard_bindings_leave_every_mouse_button_alone()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);

        foreach (var button in new[] { Middle, Back, Forward })
        {
            var (down, up) = h.Click(button);
            Assert.False(down.Suppress);
            Assert.False(up.Suppress);
        }

        Assert.Empty(h.TakeTransitions());
        Assert.False(h.Engine.UsesMouseButtons);
    }

    [Theory]
    [InlineData(Back)]
    [InlineData(Middle)]
    [InlineData(MouseButtons.Left)]
    [InlineData(MouseButtons.Right)]
    public void A_keyboard_event_with_a_mouse_button_code_is_not_the_button(uint code)
    {
        // Only injected keyboard input can carry these codes. Taken as the button, a keyboard "down" with no matching
        // "up" from the mouse hook would leave the button held in the engine's view.
        using var h = new HotkeyEngineHarness(Bare(Back));

        Assert.Equal(default, h.Down(code));
        Assert.Equal(default, h.Up(code));
        Assert.False(h.Engine.IsPressed(code));
        Assert.Empty(h.TakeTransitions());
        Assert.Equal(0, h.Engine.MouseButtonEvents);

        Assert.True(h.ButtonDown(Back).Suppress);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Theory]
    [InlineData(MouseButtons.Left)]
    [InlineData(MouseButtons.Right)]
    [InlineData(0x03u)] // VK_CANCEL, a key
    [InlineData(0x07u)]
    public void The_engine_takes_only_the_middle_and_side_buttons_from_the_mouse_hook(uint code)
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation);

        Assert.Equal(default, h.Engine.OnMouseButtonEvent(code, isDown: true));
        Assert.False(h.Engine.IsPressed(code));
        Assert.Equal(0, h.Engine.MouseButtonEvents);
    }

    [Fact]
    public void Ctrl_then_back_dictates_swallowing_the_button_and_letting_ctrl_through()
    {
        using var h = new HotkeyEngineHarness(Chord(LeftCtrl, Back));

        Assert.False(h.Down(LeftCtrl).Suppress); // a chord member waits for the chord, so Ctrl reaches the app
        Assert.True(h.ButtonDown(Back).Suppress); // the input that completes the chord is swallowed
        Assert.Equal(new[] { (HotkeyTransition.Activated, HotkeyTrigger.Standard) }, Transitions(h));
        Assert.True(h.ButtonUp(Back).Suppress);
        Assert.Equal(new[] { (HotkeyTransition.Deactivated, HotkeyTrigger.Standard) }, Transitions(h));
        Assert.False(h.Up(LeftCtrl).Suppress);

        // Releasing the key first ends it too, and the button's release still pairs with its swallowed press.
        h.Down(LeftCtrl);
        h.ButtonDown(Back);
        Assert.False(h.Up(LeftCtrl).Suppress);
        Assert.True(h.ButtonUp(Back).Suppress);
        Assert.Equal(
            new[]
            {
                (HotkeyTransition.Activated, HotkeyTrigger.Standard),
                (HotkeyTransition.Deactivated, HotkeyTrigger.Standard),
            },
            Transitions(h));
    }

    [Fact]
    public void A_button_pressed_before_its_chord_key_reaches_the_app_whole_and_the_key_completes_the_chord()
    {
        // The chord rule keys always had: its first input is not pre-empted (only a Windows key is), so a button pressed
        // first does its job in the app, and its release pairs with that press. The capture warns about this order.
        using var h = new HotkeyEngineHarness(Chord(LeftCtrl, Back));

        Assert.False(h.ButtonDown(Back).Suppress);
        Assert.True(h.Down(LeftCtrl).Suppress);
        Assert.Equal(new[] { (HotkeyTransition.Activated, HotkeyTrigger.Standard) }, Transitions(h));
        Assert.False(h.ButtonUp(Back).Suppress);
        Assert.Equal(new[] { (HotkeyTransition.Deactivated, HotkeyTrigger.Standard) }, Transitions(h));
        Assert.True(h.Up(LeftCtrl).Suppress);
    }

    [Fact]
    public void A_chord_of_two_buttons_and_a_toggled_key_and_button_chord_work_like_key_chords()
    {
        using (var buttons = new HotkeyEngineHarness(Chord(Back, Forward)))
        {
            Assert.False(buttons.ButtonDown(Back).Suppress);
            Assert.True(buttons.ButtonDown(Forward).Suppress);
            Assert.True(buttons.ButtonUp(Forward).Suppress);
            Assert.False(buttons.ButtonUp(Back).Suppress);
            Assert.Equal(
                new[]
                {
                    (HotkeyTransition.Activated, HotkeyTrigger.Standard),
                    (HotkeyTransition.Deactivated, HotkeyTrigger.Standard),
                },
                Transitions(buttons));
        }

        using var toggle = new HotkeyEngineHarness(Chord(RightCtrl, Middle, HotkeyMode.Toggle));
        toggle.Down(RightCtrl);
        toggle.Click(Middle);
        toggle.Up(RightCtrl);
        Assert.Equal(new[] { (HotkeyTransition.Activated, HotkeyTrigger.Standard) }, Transitions(toggle));
        toggle.Down(RightCtrl);
        toggle.Click(Middle);
        toggle.Up(RightCtrl);
        Assert.Equal(new[] { (HotkeyTransition.Deactivated, HotkeyTrigger.Standard) }, Transitions(toggle));
    }

    [Fact]
    public void A_chord_with_a_button_matches_whatever_else_is_held()
    {
        // Only a bare binding is judged by the modifier rule, so a chord still fires with Shift held.
        using var h = new HotkeyEngineHarness(Chord(LeftCtrl, Back));

        h.Down(LeftShift);
        h.Down(LeftCtrl);
        Assert.True(h.ButtonDown(Back).Suppress);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Theory]
    [MemberData(nameof(ButtonsWithModifiers))]
    public void A_bare_button_pressed_with_a_modifier_reaches_the_app_whole_and_starts_nothing(
        uint button, string combination, uint[] modifiers)
    {
        using var h = new HotkeyEngineHarness(Bare(button));

        foreach (var modifier in modifiers)
        {
            Assert.False(h.Down(modifier).Suppress, combination);
        }

        var (down, up) = h.Click(button);
        Assert.False(down.Suppress, combination);
        Assert.False(up.Suppress, combination);
        foreach (var modifier in modifiers.Reverse())
        {
            Assert.False(h.Up(modifier).Suppress, combination);
        }

        Assert.Empty(h.TakeTransitions());

        // Nothing was left behind: the bare button dictates straight after.
        Assert.True(h.ButtonDown(button).Suppress, combination);
        Assert.True(h.ButtonUp(button).Suppress, combination);
        Assert.Equal(
            new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
            h.TakeTransitions().Select(t => t.Transition).ToArray());
    }

    [Fact]
    public void A_modifier_pressed_during_a_button_dictation_neither_ends_it_nor_lets_the_button_through()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));

        Assert.True(h.ButtonDown(Back).Suppress);
        Assert.False(h.Down(LeftCtrl).Suppress);
        Assert.False(h.Up(LeftCtrl).Suppress);
        Assert.True(h.ButtonUp(Back).Suppress);
        Assert.Equal(
            new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
            h.TakeTransitions().Select(t => t.Transition).ToArray());
    }

    [Fact]
    public void A_modifier_windows_reports_up_blocks_no_bare_button()
    {
        // Win+L: the hook saw Win go down and never saw it come up, but Windows says it is up, so the bare button
        // dictates; with Windows saying it is down, the click reaches the app.
        var windowsDown = new HashSet<uint>();
        using var h = new HotkeyEngineHarness(Bare(Middle), isLogicallyDown: windowsDown.Contains);

        h.Down(LeftWin);
        Assert.True(h.ButtonDown(Middle).Suppress);
        h.ButtonUp(Middle);
        Assert.Equal(2, h.TakeTransitions().Count);

        windowsDown.Add(LeftWin);
        Assert.False(h.ButtonDown(Middle).Suppress);
        Assert.False(h.ButtonUp(Middle).Suppress);
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void Ctrl_with_a_page_key_still_reaches_the_app_beside_a_bare_button_binding()
    {
        // One rule for both kinds of bare binding, each judged on its own trigger.
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, Bare(Back));

        h.Down(LeftCtrl);
        Assert.False(h.Down(PageDown).Suppress);
        Assert.False(h.Up(PageDown).Suppress);
        Assert.False(h.ButtonDown(Back).Suppress);
        Assert.False(h.ButtonUp(Back).Suppress);
        h.Up(LeftCtrl);
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void A_desktop_switch_ends_a_button_hold_once_and_the_next_press_starts_afresh()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        h.ButtonDown(Back);

        h.Engine.OnDesktopSwitch(); // Win+L: the button is released on the lock screen, unseen
        h.Engine.OnDesktopSwitchNotice(() => true); // unlocked
        var transitions = h.TakeTransitions();
        Assert.Equal(
            new[]
            {
                (HotkeyTransition.Activated, HotkeyDeactivation.Released),
                (HotkeyTransition.Deactivated, HotkeyDeactivation.DesktopSwitch),
            },
            transitions.Select(t => (t.Transition, t.Deactivation)).ToArray());
        Assert.False(h.Engine.IsPressed(Back));

        // Without the reset this press would read as the button still held, and start nothing.
        Assert.True(h.ButtonDown(Back).Suppress);
        Assert.True(h.ButtonUp(Back).Suppress);
        Assert.Equal(
            new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
            h.TakeTransitions().Select(t => t.Transition).ToArray());
    }

    [Fact]
    public void An_activation_queued_before_a_desktop_switch_never_starts_a_button_recording()
    {
        using var h = new HotkeyEngineHarness(Bare(Forward, HotkeyMode.Toggle));
        h.Click(Forward);
        h.Engine.OnDesktopSwitch();

        var transitions = h.TakeTransitions();
        Assert.Equal(HotkeyTransition.Activated, transitions[0].Transition);
        Assert.False(h.WouldDispatch(transitions[0]));
        Assert.True(h.WouldDispatch(transitions[1]));
    }

    [Fact]
    public void Paused_a_click_reaches_the_app_and_a_button_swallowed_before_the_pause_stays_swallowed()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        Assert.True(h.ButtonDown(Back).Suppress);
        h.TakeTransitions();

        h.Router.SetPaused(true);
        Assert.True(h.ButtonUp(Back).Suppress); // its press was swallowed, so its release is too
        var (down, up) = h.Click(Back);
        Assert.False(down.Suppress);
        Assert.False(up.Suppress);
        Assert.Empty(h.TakeTransitions());

        // Held across the resume, the button needs a fresh press.
        h.ButtonDown(Back);
        h.Router.SetPaused(false);
        h.Engine.OnWake();
        Assert.False(h.ButtonUp(Back).Suppress);
        Assert.Empty(h.TakeTransitions());
        Assert.True(h.ButtonDown(Back).Suppress);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Fact]
    public void Capture_mode_passes_every_button_through_and_ends_a_button_dictation()
    {
        using var h = new HotkeyEngineHarness(Bare(Middle));
        h.ButtonDown(Middle);
        var activation = Assert.Single(h.TakeTransitions());

        h.Router.SetCaptureMode(true);
        Assert.False(h.WouldDispatch(activation));
        var up = h.ButtonUp(Middle); // the release of the button held when capture began: its press was swallowed
        Assert.True(up.Suppress);
        var stop = Assert.Single(h.TakeTransitions());
        Assert.Equal(HotkeyTransition.Deactivated, stop.Transition);
        Assert.False(stop.AllowReconcile);

        var (down, release) = h.Click(Middle);
        Assert.False(down.Suppress);
        Assert.False(release.Suppress);
        Assert.Empty(h.TakeTransitions());

        h.Router.SetCaptureMode(false);
        Assert.True(h.ButtonDown(Middle).Suppress);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Fact]
    public void A_reinstall_stops_a_button_dictation_and_the_retired_engine_passes_buttons_through()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        h.ButtonDown(Back);
        h.TakeTransitions();

        var (replacement, interrupted) = h.Router.BeginEngine(h.Transitions);

        Assert.Equal(HotkeyTrigger.Standard, interrupted);
        Assert.True(h.Engine.IsRetired);
        Assert.Equal(default, h.Engine.OnMouseButtonEvent(Back, isDown: false)); // a late release on the old thread
        Assert.Equal(default, h.Engine.OnMouseButtonEvent(Back, isDown: true));
        Assert.Empty(h.TakeTransitions());
        Assert.True(replacement.UsesMouseButtons);

        // The replacement starts from a clean state: a fresh press dictates on it.
        Assert.True(replacement.OnMouseButtonEvent(Back, isDown: true).Suppress);
        var start = Assert.Single(h.TakeTransitions());
        Assert.Equal(HotkeyTransition.Activated, start.Transition);
        Assert.Same(replacement, start.Engine);
        Assert.True(h.WouldDispatch(start));
    }

    [Fact]
    public void The_engine_says_whether_its_bindings_need_a_mouse_hook()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation);
        Assert.False(h.Engine.UsesMouseButtons);

        h.Router.UpdateBindings(Bare(Back), null);
        Assert.False(h.Engine.UsesMouseButtons); // requested; the hook thread has not applied it yet
        h.Engine.OnWake();
        Assert.True(h.Engine.UsesMouseButtons);

        h.Router.UpdateBindings(HotkeyBinding.DefaultDictation, Chord(LeftCtrl, Forward));
        h.Engine.OnWake();
        Assert.True(h.Engine.UsesMouseButtons);

        h.Router.UpdateBindings(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);
        h.Engine.OnWake();
        Assert.False(h.Engine.UsesMouseButtons);

        // A new engine takes it from the published configuration.
        h.Router.UpdateBindings(Bare(Middle), null);
        var (next, _) = h.Router.BeginEngine(h.Transitions);
        Assert.True(next.UsesMouseButtons);
    }

    [Theory]
    [InlineData(0x04u, true)]
    [InlineData(0x05u, true)]
    [InlineData(0x06u, true)]
    [InlineData(0x01u, false)]
    [InlineData(0x02u, false)]
    [InlineData(0x03u, false)]
    [InlineData(0x22u, false)]
    public void Only_the_middle_and_side_buttons_are_bindable(uint code, bool bindable)
    {
        Assert.Equal(bindable, MouseButtons.IsBindable(code));
        Assert.Equal(bindable, MouseButtons.Uses(new HotkeyBinding(code, KeyModifiers.None, HotkeyMode.Hold, true)));
        Assert.Equal(
            bindable, MouseButtons.Uses(new HotkeyBinding(F9, KeyModifiers.None, HotkeyMode.Hold, true, SecondaryVirtualKey: code)));
    }

    [Fact]
    public void The_log_names_what_kind_of_input_a_binding_presses()
    {
        Assert.Equal("key", MouseButtons.InputKind(HotkeyBinding.DefaultDictation));
        Assert.Equal("key", MouseButtons.InputKind(Chord(LeftCtrl, F9)));
        Assert.Equal("mouse", MouseButtons.InputKind(Bare(Back)));
        Assert.Equal("mouse", MouseButtons.InputKind(Chord(Back, Forward)));
        Assert.Equal("key+mouse", MouseButtons.InputKind(Chord(LeftCtrl, Back)));
        Assert.Equal("key+mouse", MouseButtons.InputKind(Chord(Middle, F9)));
        Assert.Equal(
            "key+mouse", MouseButtons.InputKind(new HotkeyBinding(Back, KeyModifiers.Control, HotkeyMode.Hold, true)));
        Assert.False(MouseButtons.Uses(null));
    }

    [Fact]
    public void Exactly_the_four_button_messages_pass_the_fast_path_comparison()
    {
        var passing = new List<int>();
        for (var message = -0x1000; message <= 0x10000; message++)
        {
            if (MouseHookFilter.IsButtonMessage(message))
            {
                passing.Add(message);
            }
        }

        Assert.Equal(
            new[]
            {
                MouseHookFilter.WM_MBUTTONDOWN, MouseHookFilter.WM_MBUTTONUP,
                MouseHookFilter.WM_XBUTTONDOWN, MouseHookFilter.WM_XBUTTONUP,
            },
            passing);
        Assert.False(MouseHookFilter.IsButtonMessage(int.MinValue));
        Assert.False(MouseHookFilter.IsButtonMessage(int.MaxValue));
        Assert.False(MouseHookFilter.IsButtonMessage(MouseHookFilter.WM_MOUSEMOVE));
        Assert.False(MouseHookFilter.IsButtonMessage(MouseHookFilter.WM_MOUSEWHEEL));
        Assert.False(MouseHookFilter.IsButtonMessage(MouseHookFilter.WM_MOUSEHWHEEL));
    }

    [Fact]
    public void Moves_wheels_and_the_left_and_right_buttons_are_passed_on_without_reading_the_message()
    {
        // lParam is zero, so a read of the message's data would throw: these are handed on untouched, and the engine,
        // bound to every button a hotkey can use, never hears of them.
        using var h = new HotkeyEngineHarness(Bare(Back), Chord(Middle, Forward));
        for (var message = 0x0200; message <= 0x020E; message++)
        {
            if (!MouseHookFilter.IsButtonMessage(message))
            {
                Assert.False(MouseHookFilter.Swallows(0, message, lParam: 0, h.Engine, reconcileSignal: null));
            }
        }

        Assert.Equal(0, h.Engine.MouseButtonEvents);
        Assert.Empty(h.TakeTransitions());
    }

    // In the collection that runs alone (stream TR, item 1): nothing else in the process runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void The_fast_path_and_an_unbound_button_allocate_nothing()
        {
            using var h = new HotkeyEngineHarness(Bare(Back));
            using var message = new HookMessage();
            message.Set(mouseData: 0);

            // Warm up every path first, so a first-call cost cannot be mistaken for a per-event one, and the readings
            // around the window.
            MouseHookFilter.Swallows(0, MouseHookFilter.WM_MOUSEMOVE, 0, h.Engine, null);
            MouseHookFilter.Swallows(0, MouseHookFilter.WM_MBUTTONDOWN, message.Pointer, h.Engine, null);
            MouseHookFilter.Swallows(0, MouseHookFilter.WM_MBUTTONUP, message.Pointer, h.Engine, null);
            h.Engine.OnKeyEvent(0x41, isDown: true);
            h.Engine.OnKeyEvent(0x41, isDown: false);
            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            // Then the same loop once, unmeasured (review round 4, item 5): what the runtime does once on this thread as the
            // loop runs hot, tier promotion or on-stack replacement, lands here and not in the measurement (a one-time 7,672
            // bytes did, once, on x64 CI). It hides nothing the measurement is for: a per-event allocation allocates in the
            // measured loop too, and so does any branch the loop takes that the warm-up did not.
            RunEvents();

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            RunEvents();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            AllocationMeasurement.AssertZero(allocated, during, "10,000 rounds of the fast path, an unbound button and an unused key", RunEvents);
            Assert.Empty(h.TakeTransitions());

            void RunEvents()
            {
                for (var i = 0; i < 10_000; i++)
                {
                    MouseHookFilter.Swallows(0, MouseHookFilter.WM_MOUSEMOVE, 0, h.Engine, null);
                    MouseHookFilter.Swallows(0, MouseHookFilter.WM_MOUSEWHEEL, 0, h.Engine, null);
                    MouseHookFilter.Swallows(0, MouseHookFilter.WM_MBUTTONDOWN, message.Pointer, h.Engine, null);
                    MouseHookFilter.Swallows(0, MouseHookFilter.WM_MBUTTONUP, message.Pointer, h.Engine, null);
                    h.Engine.OnKeyEvent(0x41, isDown: true); // and a key no binding uses, on the keyboard hook's path
                    h.Engine.OnKeyEvent(0x41, isDown: false);
                }
            }
        }
    }

    [Theory]
    [InlineData(MouseHookFilter.WM_MBUTTONDOWN, 0u, Middle, true)]
    [InlineData(MouseHookFilter.WM_MBUTTONUP, 0u, Middle, false)]
    [InlineData(MouseHookFilter.WM_XBUTTONDOWN, 0x0001_0000u, Back, true)]
    [InlineData(MouseHookFilter.WM_XBUTTONUP, 0x0001_0000u, Back, false)]
    [InlineData(MouseHookFilter.WM_XBUTTONDOWN, 0x0002_0000u, Forward, true)]
    [InlineData(MouseHookFilter.WM_XBUTTONUP, 0x0002_0000u, Forward, false)]
    public void A_button_message_reaches_the_engine_as_its_button(int message, uint mouseData, uint button, bool isDown)
    {
        Assert.Equal(button, MouseHookFilter.ButtonOf(message, mouseData));
        Assert.Equal(isDown, MouseHookFilter.IsDown(message));

        using var h = new HotkeyEngineHarness(Bare(button));
        using var hook = new HookMessage();
        hook.Set(mouseData);
        if (!isDown)
        {
            // Each release message is its press message plus one.
            Assert.True(MouseHookFilter.Swallows(0, message - 1, hook.Pointer, h.Engine, null));
        }

        Assert.True(MouseHookFilter.Swallows(0, message, hook.Pointer, h.Engine, null));
        Assert.Equal(isDown ? 1 : 2, h.TakeTransitions().Count);
        Assert.Equal(isDown ? 1 : 2, h.Engine.MouseButtonEvents);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x0003_0000u)] // both X buttons at once, which only injected input sends
    [InlineData(0x0004_0000u)]
    public void An_x_button_message_that_names_neither_button_is_passed_on(uint mouseData)
    {
        using var h = new HotkeyEngineHarness(Bare(Back), Bare(Forward));
        using var hook = new HookMessage();
        hook.Set(mouseData);

        Assert.False(MouseHookFilter.Swallows(0, MouseHookFilter.WM_XBUTTONDOWN, hook.Pointer, h.Engine, null));
        Assert.Equal(0, h.Engine.MouseButtonEvents);
    }

    [Fact]
    public void Scribe_s_own_injected_release_and_a_negative_code_are_passed_on_untouched()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        using var hook = new HookMessage();

        // The whole marker, and the low half of it, which is all Windows hands a low-level mouse hook (measured on CI).
        hook.Set(0x0001_0000u, SyntheticInputMarker.Value);
        Assert.False(MouseHookFilter.Swallows(0, MouseHookFilter.WM_XBUTTONUP, hook.Pointer, h.Engine, null));
        hook.Set(0x0001_0000u, MouseHookFilter.Marker);
        Assert.False(MouseHookFilter.Swallows(0, MouseHookFilter.WM_XBUTTONDOWN, hook.Pointer, h.Engine, null));
        hook.Set(0x0001_0000u);
        Assert.False(MouseHookFilter.Swallows(-1, MouseHookFilter.WM_XBUTTONDOWN, hook.Pointer, h.Engine, null));
        Assert.Equal(0, h.Engine.MouseButtonEvents);

        // Another app's extra information is the user's input as far as the hook is concerned.
        hook.Set(0x0001_0000u, unchecked((nuint)0x5343524954455354UL));
        Assert.True(MouseHookFilter.Swallows(0, MouseHookFilter.WM_XBUTTONDOWN, hook.Pointer, h.Engine, null));
        Assert.Equal(1, h.Engine.MouseButtonEvents);
    }

    [Fact]
    public void A_swallowed_release_asks_for_the_leak_check()
    {
        // Observed where the request is made, on this thread (review round 3, item 6): the signal counts the repair it is
        // asked for, and the hold keeps the epoch it asks it for, so the test waits for no pass on the pool.
        using var h = new HotkeyEngineHarness(Bare(Middle));
        using var hook = new HookMessage();
        using var hold = new ManualResetEventSlim(false);
        using var signal = new HotkeyReconcileSignal(_ => { }) { HoldBeforeTakingForTests = hold };
        try
        {
            hook.Set(0);

            Assert.True(MouseHookFilter.Swallows(0, MouseHookFilter.WM_MBUTTONDOWN, hook.Pointer, h.Engine, signal));
            Assert.Equal((0L, 0L), (signal.RepairRequests, signal.SyncRequestsForTests));
            Assert.True(MouseHookFilter.Swallows(0, MouseHookFilter.WM_MBUTTONUP, hook.Pointer, h.Engine, signal));

            Assert.Equal((1L, 0L), (signal.RepairRequests, signal.SyncRequestsForTests));
            Assert.Equal(h.Engine.KeyViewEpoch, signal.PendingRepairAtForTests);
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public void No_production_code_injects_mouse_input()
    {
        // The leak check covers keys only, and nothing else sends mouse input: Scribe injects no mouse button event.
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Scribe.slnx")))
        {
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))!;
        }

        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (path, text: File.ReadAllText(path)));
        foreach (var (path, text) in sources)
        {
            Assert.DoesNotContain("type = INPUT_MOUSE", text);
            Assert.DoesNotContain("type = NativeMethods.INPUT_MOUSE", text);
            Assert.DoesNotContain("MOUSEEVENTF_", text);
        }
    }

    [Fact]
    public void The_leak_check_never_lists_or_releases_a_mouse_button()
    {
        Assert.Empty(SuppressedKeyReconciler.CandidateKeys(Bare(Back)));
        Assert.Equal(new[] { LeftCtrl }, SuppressedKeyReconciler.CandidateKeys(Chord(LeftCtrl, Back)).ToArray());

        // Windows holding a button the hook sees released is never reason enough: the button may be held for another app
        // since before the hook saw it, and a new press can overtake any check (MouseButtonRound3Tests).
        var windowsDown = new HashSet<uint> { Back, Middle, LeftCtrl };
        var released = new List<uint>();
        var reconciler = new SuppressedKeyReconciler(windowsDown.Contains, _ => false, key =>
        {
            released.Add(key);
            return true;
        });

        Assert.Empty(reconciler.ReleaseLeakedKeys(Bare(Back)).Released);
        Assert.Empty(reconciler.ReleaseLeakedKeys(Bare(Middle)).Released);
        Assert.Equal(new[] { LeftCtrl }, reconciler.ReleaseLeakedKeys(Chord(LeftCtrl, Back)).Released); // keys as before
        Assert.Equal(new[] { LeftCtrl }, released);
    }

    // A mouse's buttons beyond the fifth reach Scribe only as the keys its software or firmware sends for them (Windows
    // delivers five mouse buttons to apps): those bind, match and are swallowed like any key, whatever modifier codes the
    // software uses, and need no mouse hook.
    [Theory]
    [InlineData(0x7Cu)] // F13
    [InlineData(0x87u)] // F24
    [InlineData(0xA6u)] // Browser Back
    [InlineData(0xAFu)] // Volume Up
    [InlineData(0xB3u)] // Play/Pause
    public void A_key_a_mouse_s_software_sends_dictates_and_never_reaches_the_app(uint key)
    {
        using var h = new HotkeyEngineHarness(HotkeyCaptureSession.Build([key], HotkeyMode.Hold));

        Assert.True(h.Down(key).Suppress);
        Assert.True(h.Down(key).Suppress); // a repeat, which some software sends while the button is held
        Assert.True(h.Up(key).Suppress);

        Assert.Equal(
            new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
            h.TakeTransitions().Select(t => t.Transition).ToArray());
        Assert.False(h.Engine.UsesMouseButtons);
        Assert.Equal(0, h.Engine.MouseButtonEvents);
    }

    [Theory]
    [InlineData(0x11u, 0x10u)] // the generic codes injected input can carry
    [InlineData(LeftCtrl, LeftShift)]
    [InlineData(RightCtrl, RightShift)]
    public void A_shortcut_a_mouse_s_software_sends_dictates_and_only_its_key_is_swallowed(uint ctrl, uint shift)
    {
        // Captured as Ctrl+Shift+F13 (a key held with two modifiers becomes the key with modifier flags), it matches
        // whichever side's, or the generic, modifier codes the software injects.
        using var h = new HotkeyEngineHarness(HotkeyCaptureSession.Build([LeftCtrl, LeftShift, 0x7C], HotkeyMode.Hold));

        Assert.False(h.Down(ctrl).Suppress);
        Assert.False(h.Down(shift).Suppress);
        Assert.True(h.Down(0x7C).Suppress);
        Assert.True(h.Up(0x7C).Suppress);
        Assert.False(h.Up(shift).Suppress);
        Assert.False(h.Up(ctrl).Suppress);
        Assert.Equal(
            new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
            h.TakeTransitions().Select(t => t.Transition).ToArray());

        // F13 alone, or with only one of the modifiers, is not the shortcut.
        Assert.False(h.Down(0x7C).Suppress);
        Assert.False(h.Up(0x7C).Suppress);
        h.Down(ctrl);
        Assert.False(h.Down(0x7C).Suppress);
        Assert.False(h.Up(0x7C).Suppress);
        h.Up(ctrl);
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void A_shortcut_whose_key_goes_down_first_keeps_its_last_modifier_from_the_app_instead()
    {
        // What is kept from the app is the input that completes the shortcut: the key when the modifiers go down first,
        // as a mouse's software and a person send one, and otherwise the last modifier, while the key reaches the app
        // whole. Set warns when it records that order (HotkeyCaptureSessionTests).
        using var h = new HotkeyEngineHarness(HotkeyCaptureSession.Build([LeftCtrl, LeftShift, 0x7C], HotkeyMode.Hold));

        Assert.False(h.Down(0x7C).Suppress);
        Assert.False(h.Down(LeftShift).Suppress);
        Assert.True(h.Down(LeftCtrl).Suppress);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
        Assert.False(h.Up(0x7C).Suppress);
        Assert.Equal(HotkeyTransition.Deactivated, Assert.Single(h.TakeTransitions()).Transition);
        Assert.True(h.Up(LeftCtrl).Suppress);
        Assert.False(h.Up(LeftShift).Suppress);
    }

    [Fact]
    public void A_bare_key_a_mouse_s_software_sends_keeps_dictating_with_a_modifier_held()
    {
        // The modifier pass-through is for a bare Page key or native mouse button only, so a bare F13 binding matches
        // with Ctrl held, as it always has: the key the software sends, not the modifier, says which button it is.
        using var h = new HotkeyEngineHarness(HotkeyCaptureSession.Build([0x7C], HotkeyMode.Hold));

        h.Down(LeftCtrl);
        Assert.True(h.Down(0x7C).Suppress);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Fact]
    public void A_native_button_held_with_two_modifiers_dictates_only_with_them_and_only_the_button_is_swallowed()
    {
        using var h = new HotkeyEngineHarness(HotkeyCaptureSession.Build([LeftCtrl, LeftShift, Middle], HotkeyMode.Hold));
        Assert.True(h.Engine.UsesMouseButtons);

        Assert.False(h.Down(RightCtrl).Suppress);
        Assert.False(h.Down(LeftShift).Suppress);
        Assert.True(h.ButtonDown(Middle).Suppress);
        Assert.True(h.ButtonUp(Middle).Suppress);
        Assert.False(h.Up(LeftShift).Suppress);
        Assert.False(h.Up(RightCtrl).Suppress);
        Assert.Equal(
            new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
            h.TakeTransitions().Select(t => t.Transition).ToArray());

        // The button alone, or with one of the two, is a click for the app under the pointer.
        var (down, up) = h.Click(Middle);
        Assert.False(down.Suppress);
        Assert.False(up.Suppress);
        h.Down(LeftShift);
        (down, up) = h.Click(Middle);
        Assert.False(down.Suppress);
        Assert.False(up.Suppress);
        h.Up(LeftShift);
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void The_hook_message_struct_matches_the_windows_layout()
    {
        // MSLLHOOKSTRUCT: POINT pt; DWORD mouseData; DWORD flags; DWORD time; ULONG_PTR dwExtraInfo. The callback reads the
        // two fields at the offsets MouseHookFilter writes out, which must be the struct's.
        Assert.Equal(8, (int)Marshal.OffsetOf<NativeMethods.MSLLHOOKSTRUCT>(nameof(NativeMethods.MSLLHOOKSTRUCT.mouseData)));
        Assert.Equal(
            IntPtr.Size == 8 ? 24 : 20,
            (int)Marshal.OffsetOf<NativeMethods.MSLLHOOKSTRUCT>(nameof(NativeMethods.MSLLHOOKSTRUCT.dwExtraInfo)));
        Assert.Equal(
            MouseHookFilter.MouseDataOffset,
            (int)Marshal.OffsetOf<NativeMethods.MSLLHOOKSTRUCT>(nameof(NativeMethods.MSLLHOOKSTRUCT.mouseData)));
        Assert.Equal(
            MouseHookFilter.ExtraInfoOffset,
            (int)Marshal.OffsetOf<NativeMethods.MSLLHOOKSTRUCT>(nameof(NativeMethods.MSLLHOOKSTRUCT.dwExtraInfo)));
    }

    // An MSLLHOOKSTRUCT in native memory, as the mouse hook receives one.
    private sealed class HookMessage : IDisposable
    {
        public HookMessage() => Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MSLLHOOKSTRUCT>());

        public nint Pointer { get; }

        public void Set(uint mouseData, nuint extraInfo = 0) =>
            Marshal.StructureToPtr(
                new NativeMethods.MSLLHOOKSTRUCT { mouseData = mouseData, dwExtraInfo = extraInfo }, Pointer, fDeleteOld: false);

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}
