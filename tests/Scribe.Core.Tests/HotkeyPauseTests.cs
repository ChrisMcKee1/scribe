using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// R20: while dictation is paused the push-to-talk binding passes through to other apps. Each test
/// is one row of the pause state table: which phase of a hold, a toggle, a chord or a Settings
/// capture session the pause or resume lands in, and what every following key event must do.
///
/// The rules the rows pin:
/// <list type="bullet">
/// <item>A press made while paused reaches other apps and never activates dictation.</item>
/// <item>A key swallowed before the pause stays swallowed through its autorepeat and release.</item>
/// <item>A key pressed during the pause keeps passing through even if resume comes first.</item>
/// <item>A chord held across a resume activates only when pressed again.</item>
/// <item>Pausing cancels hold and toggle latches without raising Deactivated.</item>
/// <item>Binding capture keeps working while paused, and the pause outlives it.</item>
/// </list>
/// </summary>
public class HotkeyPauseTests
{
    private const uint RightCtrl = 0xA3;
    private const uint RightShift = 0xA1;
    private const uint LeftCtrl = 0xA2;
    private const uint LeftWin = 0x5B;
    private const uint RightWin = 0x5C;
    private const uint KeyA = 0x41;
    private const uint KeyH = 0x48;
    private const uint Space = 0x20;
    private const uint F9 = 0x78;

    private static readonly HotkeyBinding Hold = HotkeyBinding.Legacy; // Right Ctrl, swallowed

    private static readonly HotkeyBinding Toggle = HotkeyBinding.Legacy with { Mode = HotkeyMode.Toggle };

    private static readonly HotkeyBinding CtrlShiftChord = new(
        RightCtrl, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Right Ctrl+Right Shift",
        SecondaryVirtualKey: RightShift, SuppressChordMembers: true);

    private static readonly HotkeyBinding WinHChord = new(
        LeftWin, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Left Win+H",
        SecondaryVirtualKey: KeyH, SuppressChordMembers: true);

    private static readonly HotkeyBinding CtrlRightWinChord = new(
        RightCtrl, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "Right Ctrl+Right Win",
        SecondaryVirtualKey: RightWin, SuppressChordMembers: true);

    // The modifier-flag spelling: the Win flag makes either Windows key a binding key, so both are
    // pre-empted.
    private static readonly HotkeyBinding WinSpaceFlagChord = new(
        Space, KeyModifiers.Win, HotkeyMode.Hold, Suppress: true, "Win+Space", SuppressChordMembers: true);

    private static readonly HotkeyBinding CtrlSpace =
        new(Space, KeyModifiers.Control, HotkeyMode.Hold, Suppress: true, "Ctrl+Space");

    private static readonly HotkeyBinding F9Hold =
        new(F9, KeyModifiers.None, HotkeyMode.Hold, Suppress: true, "F9");

    // ---- Hold mode -------------------------------------------------------------------------

    [Fact]
    public void Hold_press_made_while_paused_passes_through_and_never_activates()
    {
        var state = new ChordStateMachine(Hold);
        state.SetPaused(true);

        Down(state, RightCtrl, swallowed: false);
        Down(state, RightCtrl, swallowed: false); // autorepeat
        Up(state, RightCtrl, swallowed: false);
    }

    [Fact]
    public void Hold_key_swallowed_before_the_pause_stays_swallowed_through_repeat_and_release()
    {
        var state = new ChordStateMachine(Hold);
        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);

        state.SetPaused(true);

        Down(state, RightCtrl, swallowed: true); // other apps never saw it go down
        Up(state, RightCtrl, swallowed: true);   // so no orphaned key-up, and no Deactivated either
    }

    [Fact]
    public void Hold_key_pressed_during_the_pause_passes_through_even_when_resume_comes_first()
    {
        var state = new ChordStateMachine(Hold);
        state.SetPaused(true);
        Down(state, RightCtrl, swallowed: false);

        state.SetPaused(false);

        Down(state, RightCtrl, swallowed: false); // autorepeat of the paused press
        Up(state, RightCtrl, swallowed: false);   // other apps saw the down, so they get the up
        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);
        Up(state, RightCtrl, swallowed: true, HotkeyTransition.Deactivated);
    }

    [Fact]
    public void Hold_key_swallowed_before_the_pause_and_held_across_resume_needs_a_fresh_press()
    {
        var state = new ChordStateMachine(Hold);
        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);

        state.SetPaused(true);
        state.SetPaused(false);

        Down(state, RightCtrl, swallowed: true); // still swallowed, and no second activation
        Up(state, RightCtrl, swallowed: true);   // the latch died with the pause
        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);
    }

    [Fact]
    public void Pause_and_resume_between_presses_leave_the_binding_working()
    {
        var state = new ChordStateMachine(Hold);

        state.SetPaused(true);
        state.SetPaused(false);

        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);
        Up(state, RightCtrl, swallowed: true, HotkeyTransition.Deactivated);
    }

    [Fact]
    public void Paused_state_keeps_the_physical_key_view_the_leak_reconciler_relies_on()
    {
        var state = new ChordStateMachine(Hold);
        state.SetPaused(true);

        state.Process(RightCtrl, isDown: true);
        Assert.True(state.IsPressed(RightCtrl));

        state.Process(RightCtrl, isDown: false);
        Assert.False(state.IsPressed(RightCtrl));
    }

    // ---- Toggle mode -----------------------------------------------------------------------

    [Fact]
    public void Pausing_cancels_a_latched_toggle_so_the_first_press_after_resume_starts_a_new_dictation()
    {
        var state = new ChordStateMachine(Toggle);
        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);
        Up(state, RightCtrl, swallowed: true);

        state.SetPaused(true);
        Down(state, RightCtrl, swallowed: false); // would have been the toggle-off; paused, it types
        Up(state, RightCtrl, swallowed: false);
        state.SetPaused(false);

        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);
    }

    [Fact]
    public void Pausing_mid_toggle_press_keeps_that_press_swallowed_through_its_release()
    {
        var state = new ChordStateMachine(Toggle);
        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);

        state.SetPaused(true);
        Up(state, RightCtrl, swallowed: true);
        state.SetPaused(false);

        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);
    }

    [Fact]
    public void Toggle_pressed_during_the_pause_and_released_after_resume_does_not_toggle()
    {
        var state = new ChordStateMachine(Toggle);
        state.SetPaused(true);
        Down(state, RightCtrl, swallowed: false);

        state.SetPaused(false);

        Up(state, RightCtrl, swallowed: false);
        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);
    }

    // ---- Chords ----------------------------------------------------------------------------

    [Fact]
    public void Chord_completed_during_the_pause_types_normally_and_does_not_fire_on_resume()
    {
        var state = new ChordStateMachine(CtrlShiftChord);
        Down(state, RightCtrl, swallowed: false); // a lone member always reaches other apps
        state.SetPaused(true);
        Down(state, RightShift, swallowed: false); // completes the chord while paused

        state.SetPaused(false);

        Down(state, RightShift, swallowed: false); // autorepeat: the chord is already held
        Up(state, RightShift, swallowed: false);
        Up(state, RightCtrl, swallowed: false);
        Down(state, RightCtrl, swallowed: false);
        Down(state, RightShift, swallowed: true, HotkeyTransition.Activated);
    }

    [Fact]
    public void Chord_member_held_across_resume_completes_with_a_fresh_partner_press()
    {
        var state = new ChordStateMachine(CtrlShiftChord);
        state.SetPaused(true);
        Down(state, RightCtrl, swallowed: false);

        state.SetPaused(false);

        Down(state, RightShift, swallowed: true, HotkeyTransition.Activated); // a real press after resume
        Up(state, RightShift, swallowed: true, HotkeyTransition.Deactivated);
        Up(state, RightCtrl, swallowed: false); // its down reached other apps, so its up must too
    }

    [Fact]
    public void Chord_active_at_the_pause_keeps_only_its_swallowed_member_swallowed()
    {
        var state = new ChordStateMachine(CtrlShiftChord);
        Down(state, RightCtrl, swallowed: false);
        Down(state, RightShift, swallowed: true, HotkeyTransition.Activated);

        state.SetPaused(true);

        Up(state, RightShift, swallowed: true); // no orphaned key-up for the member apps never saw
        Up(state, RightCtrl, swallowed: false); // and no stuck Ctrl for the member they did see
    }

    [Fact]
    public void Paused_reserved_chord_lets_the_Windows_key_reach_the_shell()
    {
        var state = new ChordStateMachine(WinHChord);
        state.SetPaused(true);

        Down(state, LeftWin, swallowed: false); // pre-emptive suppression stands down as well
        Down(state, KeyH, swallowed: false);
        Up(state, KeyH, swallowed: false);
        Up(state, LeftWin, swallowed: false);
    }

    [Fact]
    public void Windows_key_pre_empted_before_the_pause_stays_swallowed_through_its_release()
    {
        var state = new ChordStateMachine(WinHChord);
        Down(state, LeftWin, swallowed: true);

        state.SetPaused(true);

        Up(state, LeftWin, swallowed: true);
    }

    // Pre-emption used to ignore autorepeat, so the first repeat after resume was swallowed and
    // recorded, which swallowed the release too: the shell saw Win go down and never come up.
    [Theory]
    [InlineData("Left Win+H", LeftWin, HotkeyMode.Hold)]
    [InlineData("Left Win+H", LeftWin, HotkeyMode.Toggle)]
    [InlineData("Right Ctrl+Right Win", RightWin, HotkeyMode.Hold)]
    [InlineData("Win+Space", LeftWin, HotkeyMode.Hold)]
    [InlineData("Win+Space", RightWin, HotkeyMode.Hold)]
    public void Windows_key_pressed_during_the_pause_passes_through_whole_even_when_resume_comes_first(
        string chord, uint windowsKey, HotkeyMode mode)
    {
        using var h = new HotkeyEngineHarness(Binding(chord) with { Mode = mode });
        h.Router.SetPaused(true);
        PassesThrough(h.Down(windowsKey)); // the shell sees Win go down

        h.Router.SetPaused(false);
        h.Engine.OnWake();

        PassesThrough(h.Down(windowsKey)); // its autorepeats are not pre-empted after the resume
        PassesThrough(h.Down(windowsKey));
        PassesThrough(h.Up(windowsKey));   // so the shell gets the release, with no leak check needed
        Assert.Empty(h.TakeTransitions());

        Assert.True(h.Down(windowsKey).Suppress); // a fresh press after the resume is pre-empted again
        Assert.True(h.Up(windowsKey).Suppress);
    }

    [Fact]
    public void Windows_key_held_across_resume_completes_the_chord_with_a_fresh_partner_and_still_passes_its_release()
    {
        using var h = new HotkeyEngineHarness(WinHChord);
        h.Router.SetPaused(true);
        PassesThrough(h.Down(LeftWin));
        h.Router.SetPaused(false);
        h.Engine.OnWake();

        Assert.True(h.Down(KeyH).Suppress); // a real press after the resume completes the chord
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
        PassesThrough(h.Down(LeftWin));     // Win keeps passing: the shell already saw it go down
        PassesThrough(h.Up(LeftWin));       // and its release still reaches the shell, ending the hold
        Assert.Equal(HotkeyTransition.Deactivated, Assert.Single(h.TakeTransitions()).Transition);
        Assert.True(h.Up(KeyH).Suppress);   // H's down was swallowed, so its up is as well
    }

    [Fact]
    public void Windows_chord_completed_during_the_pause_and_held_across_resume_passes_through_whole()
    {
        var state = new ChordStateMachine(WinHChord);
        state.SetPaused(true);
        Down(state, LeftWin, swallowed: false);
        Down(state, KeyH, swallowed: false); // the shell handles Win+H itself while paused

        state.SetPaused(false);

        Down(state, LeftWin, swallowed: false); // autorepeats after the resume neither fire nor swallow
        Down(state, KeyH, swallowed: false);
        Up(state, KeyH, swallowed: false);
        Up(state, LeftWin, swallowed: false);
        Down(state, LeftWin, swallowed: true);  // the next real press is the binding again
        Down(state, KeyH, swallowed: true, HotkeyTransition.Activated);
    }

    [Fact]
    public void Dictation_only_Windows_chord_passes_a_paused_press_through_whole_after_resume()
    {
        using var h = new HotkeyEngineHarness(Hold, WinHChord);
        h.Router.SetPaused(true);
        PassesThrough(h.Down(LeftWin));
        h.Router.SetPaused(false);
        h.Engine.OnWake();

        PassesThrough(h.Down(LeftWin));
        PassesThrough(h.Up(LeftWin));
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void Paused_modifier_binding_lets_the_primary_key_through()
    {
        var state = new ChordStateMachine(CtrlSpace);
        state.SetPaused(true);

        Down(state, LeftCtrl, swallowed: false);
        Down(state, Space, swallowed: false);
        Up(state, Space, swallowed: false);
        Up(state, LeftCtrl, swallowed: false);
    }

    // ---- Binding capture overlap -----------------------------------------------------------

    [Fact]
    public void Capture_mode_works_while_paused_and_the_pause_outlives_it()
    {
        var state = new ChordStateMachine(Hold);
        state.SetPaused(true);
        Assert.Equal(HotkeyTransition.None, state.SetCaptureMode(true).Transition);

        Down(state, RightCtrl, swallowed: false); // the capture box sees the key
        Up(state, RightCtrl, swallowed: false);
        Assert.Equal(HotkeyTransition.None, state.SetCaptureMode(false).Transition);

        Down(state, RightCtrl, swallowed: false); // still paused once capture ends
        Up(state, RightCtrl, swallowed: false);
        state.SetPaused(false);
        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);
    }

    [Fact]
    public void Capture_started_before_the_pause_and_ended_after_resume_keeps_capture_semantics()
    {
        var state = new ChordStateMachine(Hold);
        state.SetCaptureMode(true);
        state.SetPaused(true);
        Down(state, RightCtrl, swallowed: false);
        Up(state, RightCtrl, swallowed: false);

        state.SetPaused(false);

        Down(state, RightCtrl, swallowed: false); // resuming does not end capture
        Up(state, RightCtrl, swallowed: false);
        state.SetCaptureMode(false);
        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);
    }

    [Fact]
    public void Pausing_during_capture_keeps_the_binding_down_after_capture_ends()
    {
        var state = new ChordStateMachine(Hold);
        state.SetCaptureMode(true);
        state.SetPaused(true);

        state.SetCaptureMode(false);

        Down(state, RightCtrl, swallowed: false);
        Up(state, RightCtrl, swallowed: false);
        state.SetPaused(false);
        Down(state, RightCtrl, swallowed: true, HotkeyTransition.Activated);
    }

    // ---- Through the engine: requests from other threads, both triggers --------------------

    [Fact]
    public async Task Pause_request_cancels_the_latch_silently_and_frees_the_other_trigger()
    {
        using var h = new HotkeyEngineHarness(Hold, F9Hold);
        h.Down(RightCtrl);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);

        await Task.Run(() => h.Router.SetPaused(true));
        h.Engine.OnWake();

        // The controller stops the recording itself; a Deactivated here would race that stop.
        Assert.Empty(h.TakeTransitions());
        Assert.True(h.Up(RightCtrl).Suppress);
        Assert.Empty(h.TakeTransitions());

        await Task.Run(() => h.Router.SetPaused(false));
        h.Engine.OnWake();

        // The pause released the arbiter, so the dictation-only binding is not blocked.
        Assert.True(h.Down(F9).Suppress);
        var activation = Assert.Single(h.TakeTransitions());
        Assert.Equal(HotkeyTrigger.DictationOnly, activation.Trigger);
        Assert.True(h.WouldDispatch(activation));
    }

    [Fact]
    public void Pausing_makes_an_activation_computed_before_it_stale_and_resuming_invalidates_nothing()
    {
        using var h = new HotkeyEngineHarness(Hold);
        h.Down(RightCtrl);
        var beforePause = Assert.Single(h.TakeTransitions());

        h.Router.SetPaused(true); // not yet applied by the hook thread

        Assert.False(h.WouldDispatch(beforePause));

        h.Engine.OnWake();
        h.Up(RightCtrl);
        var resumedAt = h.Router.CurrentGeneration;
        h.Router.SetPaused(false);
        Assert.Equal(resumedAt, h.Router.CurrentGeneration);

        h.Down(RightCtrl);
        Assert.True(h.WouldDispatch(Assert.Single(h.TakeTransitions())));
    }

    [Fact]
    public void Paused_dictation_only_binding_passes_through_too()
    {
        using var h = new HotkeyEngineHarness(Hold, F9Hold);
        h.Router.SetPaused(true);

        Assert.False(h.Down(F9).Suppress);
        Assert.False(h.Up(F9).Suppress);
        Assert.False(h.Down(RightCtrl).Suppress);
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void Dictation_only_binding_added_while_paused_starts_paused()
    {
        using var h = new HotkeyEngineHarness(Hold);
        h.Router.SetPaused(true);
        h.Engine.OnWake();

        h.Router.UpdateBindings(Hold, F9Hold);

        Assert.False(h.Down(F9).Suppress);
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void Repeated_pause_requests_change_nothing()
    {
        using var h = new HotkeyEngineHarness(Hold);

        Assert.True(h.Router.SetPaused(true).Changed);
        var pausedAt = h.Router.CurrentGeneration;
        Assert.Equal((false, (HotkeyEngine?)null), h.Router.SetPaused(true));
        Assert.Equal(pausedAt, h.Router.CurrentGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Numbered_pause_requests_leave_the_latest_state_whichever_arrives_first(bool resumeArrivesFirst)
    {
        using var h = new HotkeyEngineHarness(Hold);

        // The caller paused (request 1) and then resumed (request 2) on two threads, and each call
        // left the caller's lock before reaching the hook, so either one can arrive first.
        if (resumeArrivesFirst)
        {
            Assert.NotNull(h.Router.SetPaused(false, requestSequence: 2));
            Assert.Null(h.Router.SetPaused(true, requestSequence: 1)); // superseded: changes nothing
        }
        else
        {
            Assert.True(h.Router.SetPaused(true, requestSequence: 1)?.Changed);
            Assert.True(h.Router.SetPaused(false, requestSequence: 2)?.Changed);
        }

        Assert.True(h.Down(RightCtrl).Suppress); // resumed, as the latest request said
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Fact]
    public void A_numbered_pause_request_is_applied_at_most_once()
    {
        using var h = new HotkeyEngineHarness(Hold);
        Assert.True(h.Router.SetPaused(true, requestSequence: 5)?.Changed);

        Assert.Null(h.Router.SetPaused(false, requestSequence: 5)); // the same number is not newer
        Assert.False(h.Down(RightCtrl).Suppress);                    // so the binding stays paused

        Assert.True(h.Router.SetPaused(false, requestSequence: 6)?.Changed);
        h.Up(RightCtrl);
        Assert.True(h.Down(RightCtrl).Suppress);

        // An unnumbered request is never judged against the numbered ones.
        Assert.True(h.Router.SetPaused(true).Changed);
    }

    [Fact]
    public void Release_of_a_key_swallowed_before_the_pause_still_requests_a_leak_check()
    {
        using var h = new HotkeyEngineHarness(Hold);
        h.Down(RightCtrl);
        h.Router.SetPaused(true);

        var release = h.Up(RightCtrl);

        Assert.True(release.Suppress);
        Assert.True(release.RequestReconcile);
    }

    [Fact]
    public void Resume_applied_between_a_paused_press_and_its_release_still_passes_the_release()
    {
        using var h = new HotkeyEngineHarness(Hold);
        h.Router.SetPaused(true);
        Assert.False(h.Down(RightCtrl).Suppress);

        h.Router.SetPaused(false);
        h.Engine.OnWake();

        var release = h.Up(RightCtrl);
        Assert.False(release.Suppress);
        Assert.False(release.RequestReconcile);
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void Pause_survives_a_hook_reinstall()
    {
        using var h = new HotkeyEngineHarness(Hold);
        h.Router.SetPaused(true);

        var (replacement, _) = h.Router.BeginEngine(h.Transitions);

        Assert.False(replacement.OnKeyEvent(RightCtrl, isDown: true).Suppress);
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void Capture_request_while_paused_still_passes_keys_and_the_pause_outlives_it()
    {
        using var h = new HotkeyEngineHarness(Hold);
        h.Router.SetPaused(true);
        h.Router.SetCaptureMode(true);

        Assert.False(h.Down(RightCtrl).Suppress);
        Assert.False(h.Up(RightCtrl).Suppress);
        h.Router.SetCaptureMode(false);
        Assert.False(h.Down(RightCtrl).Suppress);
        Assert.False(h.Up(RightCtrl).Suppress);
        Assert.Empty(h.TakeTransitions());

        h.Router.SetPaused(false);
        Assert.True(h.Down(RightCtrl).Suppress);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    // ---- Whole keystrokes through any pause and resume --------------------------------------

    /// <summary>
    /// A seeded walk (every run replays the same sequence) over presses, autorepeats, releases,
    /// pauses and resumes. Whatever the order, each keystroke is swallowed or passed whole, and
    /// while paused nothing fires and no new press is swallowed.
    /// </summary>
    [Theory]
    [InlineData("Right Ctrl")]
    [InlineData("Right Ctrl toggle")]
    [InlineData("Right Ctrl+Right Shift")]
    [InlineData("Left Win+H")]
    [InlineData("Left Win+H toggle")]
    [InlineData("Right Ctrl+Right Win")]
    [InlineData("Ctrl+Space")]
    [InlineData("Win+Space")]
    public void Every_keystroke_is_swallowed_or_passed_whole_through_any_pause_and_resume(string chord)
    {
        uint[] keys = [LeftWin, RightWin, KeyH, RightCtrl, LeftCtrl, RightShift, Space, KeyA];
        var state = new ChordStateMachine(Binding(chord));
        var random = new Random(20260922);
        var held = new Dictionary<uint, (bool Swallowed, bool WhilePaused)>();
        var trace = new List<string>();
        var paused = false;
        var repeatsAcrossResume = 0;

        for (var step = 0; step < 4000; step++)
        {
            if (random.Next(100) < 8)
            {
                paused = !paused;
                state.SetPaused(paused);
                trace.Add(paused ? "pause" : "resume");
                continue;
            }

            var key = keys[random.Next(keys.Length)];
            var release = held.ContainsKey(key) && random.Next(100) < 45;
            var update = state.Process(key, isDown: !release);
            trace.Add($"{(release ? "up" : "down")} 0x{key:X2} swallowed={update.ShouldSuppress}");

            if (held.TryGetValue(key, out var press))
            {
                if (update.ShouldSuppress != press.Swallowed)
                {
                    Assert.Fail($"{chord}: the {(release ? "release" : "autorepeat")} of 0x{key:X2} split its " +
                        $"keystroke at step {step}. Last events: {string.Join(", ", trace.TakeLast(12))}");
                }

                if (!release && !paused && press.WhilePaused)
                {
                    repeatsAcrossResume++;
                }

                if (release)
                {
                    held.Remove(key);
                }
            }
            else
            {
                Assert.False(paused && update.ShouldSuppress, $"{chord}: a press made while paused was swallowed.");
                held[key] = (update.ShouldSuppress, paused);
            }

            Assert.False(paused && update.Transition != HotkeyTransition.None, $"{chord}: a transition fired while paused.");
            Assert.Equal(held.ContainsKey(key), state.IsPressed(key));
        }

        // The case the walk exists for has to actually occur, or the test proves nothing.
        Assert.True(repeatsAcrossResume > 0);
    }

    private static HotkeyBinding Binding(string chord) => chord switch
    {
        "Right Ctrl" => Hold,
        "Right Ctrl toggle" => Toggle,
        "Right Ctrl+Right Shift" => CtrlShiftChord,
        "Left Win+H" => WinHChord,
        "Left Win+H toggle" => WinHChord with { Mode = HotkeyMode.Toggle },
        "Right Ctrl+Right Win" => CtrlRightWinChord,
        "Ctrl+Space" => CtrlSpace,
        "Win+Space" => WinSpaceFlagChord,
        _ => throw new ArgumentOutOfRangeException(nameof(chord), chord, null),
    };

    private static void PassesThrough(HookDecision decision)
    {
        Assert.False(decision.Suppress);
        Assert.False(decision.RequestReconcile);
    }

    private static void Down(
        ChordStateMachine state, uint key, bool swallowed, HotkeyTransition transition = HotkeyTransition.None) =>
        Expect(state.Process(key, isDown: true), swallowed, transition);

    private static void Up(
        ChordStateMachine state, uint key, bool swallowed, HotkeyTransition transition = HotkeyTransition.None) =>
        Expect(state.Process(key, isDown: false), swallowed, transition);

    private static void Expect(ChordUpdate update, bool swallowed, HotkeyTransition transition)
    {
        Assert.Equal(transition, update.Transition);
        Assert.Equal(swallowed, update.ShouldSuppress);
    }
}
