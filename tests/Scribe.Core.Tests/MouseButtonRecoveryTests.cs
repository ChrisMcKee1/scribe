using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// What becomes of a mouse button's dictation, its swallowed release and the leak check when the hook's view of the
/// button can no longer be trusted: Windows removed the mouse hook and its renewal found it gone, the input desktop
/// switched while the button was held, or the leak check meets a button Windows holds that the engine never saw go down.
/// Every test drives the real engine, router and leak check the way the hook thread and the service do, with Windows'
/// view of the buttons scripted.
/// </summary>
public sealed class MouseButtonRecoveryTests
{
    private const uint Middle = MouseButtons.Middle;
    private const uint Back = MouseButtons.Back;
    private const uint Forward = MouseButtons.Forward;
    private const uint PageDown = 0x22;
    private const uint LeftCtrl = 0xA2;
    private const uint RightCtrl = 0xA3;

    private static HotkeyBinding Bare(uint button, HotkeyMode mode = HotkeyMode.Hold) =>
        HotkeyCaptureSession.Build([button], mode);

    private static HotkeyBinding Chord(uint first, uint second) => HotkeyCaptureSession.Build([first, second], HotkeyMode.Hold);

    private static (HotkeyTransition, HotkeyDeactivation)[] Transitions(HotkeyEngineHarness h) =>
        h.TakeTransitions().Select(t => (t.Transition, t.Deactivation)).ToArray();

    // A1: the renewal found the mouse hook gone.

    [Fact]
    public void A_lost_mouse_hook_ends_a_button_hold_without_another_click()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        Assert.True(h.ButtonDown(Back).Suppress);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);

        // Windows removed the mouse hook, the user let go of Back while it was gone, and the renewal found it gone.
        h.Engine.OnMouseHookLost();

        var stop = Assert.Single(h.TakeTransitions());
        Assert.Equal(
            (HotkeyTransition.Deactivated, HotkeyTrigger.Standard, HotkeyDeactivation.MouseHookLost),
            (stop.Transition, stop.Trigger, stop.Deactivation));
        Assert.False(stop.AllowReconcile); // born from a state clear, like a desktop switch's
        Assert.False(h.Engine.HoldsAnyLatch);
        Assert.False(h.Engine.IsPressed(Back));

        // The next click is a dictation of its own, not the release of the one that ended.
        var (down, up) = h.Click(Back);
        Assert.True(down.Suppress);
        Assert.True(up.Suppress);
        Assert.Equal(
            new[] { (HotkeyTransition.Activated, HotkeyDeactivation.Released), (HotkeyTransition.Deactivated, HotkeyDeactivation.Released) },
            Transitions(h));
    }

    [Fact]
    public void An_activation_queued_before_the_mouse_hook_was_found_lost_never_starts_a_recording()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        var started = 0;
        var stopped = new List<HotkeyDeactivation>();
        h.Service.Activated += (_, _) => started++;
        h.Service.Deactivated += (_, e) => stopped.Add(e.Deactivation);
        h.ButtonDown(Back); // its Activated waits for a consumer that has not run yet

        h.Engine.OnMouseHookLost();
        var dispatched = h.DispatchAll();

        Assert.Equal(
            new[] { HotkeyTransition.Activated, HotkeyTransition.Deactivated },
            dispatched.Select(t => t.Transition).ToArray());
        Assert.Equal(0, started);
        Assert.Equal(new[] { HotkeyDeactivation.MouseHookLost }, stopped.ToArray());
    }

    [Fact]
    public void A_lost_mouse_hook_keeps_the_release_of_a_button_still_held_from_the_app()
    {
        // Still held when the renewal found the hook gone: the release reaches the renewed hook, and since the press was
        // swallowed, the app under the pointer must not get the release either.
        using var h = new HotkeyEngineHarness(Bare(Back));
        h.ButtonDown(Back);
        h.Engine.OnMouseHookLost();
        h.TakeTransitions();

        Assert.True(h.ButtonUp(Back).Suppress);
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void A_lost_mouse_hook_ends_a_toggled_button_dictation_and_the_next_click_starts_one()
    {
        using var h = new HotkeyEngineHarness(Bare(Middle, HotkeyMode.Toggle));
        h.Click(Middle);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);

        h.Engine.OnMouseHookLost(); // the click that would have ended it may be the one no hook saw

        Assert.Equal(
            new[] { (HotkeyTransition.Deactivated, HotkeyDeactivation.MouseHookLost) },
            Transitions(h));
        h.Click(Middle);
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Fact]
    public void A_lost_mouse_hook_ends_a_key_and_button_chord_and_keeps_the_key_the_keyboard_hook_saw()
    {
        using var h = new HotkeyEngineHarness(Chord(LeftCtrl, Back));
        h.Down(LeftCtrl);
        Assert.True(h.ButtonDown(Back).Suppress);
        h.TakeTransitions();

        h.Engine.OnMouseHookLost();

        Assert.Equal(new[] { (HotkeyTransition.Deactivated, HotkeyDeactivation.MouseHookLost) }, Transitions(h));
        Assert.True(h.Engine.IsPressed(LeftCtrl));
        Assert.True(h.ButtonUp(Back).Suppress);
        Assert.True(h.ButtonDown(Back).Suppress); // Ctrl is still held, so this is the chord again
        Assert.Equal(HotkeyTransition.Activated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Fact]
    public void A_lost_mouse_hook_leaves_a_keyboard_dictation_and_its_queued_start_alone()
    {
        using var h = new HotkeyEngineHarness(HotkeyBinding.DefaultDictation, Bare(Forward));
        Assert.True(h.Down(PageDown).Suppress);
        var start = Assert.Single(h.TakeTransitions());

        h.Engine.OnMouseHookLost();

        Assert.Empty(h.TakeTransitions());
        Assert.True(h.WouldDispatch(start));
        Assert.True(h.Up(PageDown).Suppress);
        Assert.Equal(HotkeyTransition.Deactivated, Assert.Single(h.TakeTransitions()).Transition);
    }

    [Fact]
    public void A_retired_engine_does_nothing_when_its_mouse_hook_is_found_lost()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        h.ButtonDown(Back);
        h.TakeTransitions();
        var (replacement, interrupted) = h.Router.BeginEngine(h.Transitions);
        Assert.Equal(HotkeyTrigger.Standard, interrupted); // the reinstall stops it, once

        h.Engine.OnMouseHookLost();

        Assert.Empty(h.TakeTransitions());
        Assert.Equal(0, h.Engine.MouseHookLossesHandled);
        Assert.Equal(0, replacement.ActivationEpoch);
    }

    // A2: a desktop reset ends the recording but keeps the swallowed press's release from the app.

    [Fact]
    public void A_desktop_switch_ends_a_button_hold_but_keeps_its_release_from_the_app()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        Assert.True(h.ButtonDown(Back).Suppress);
        h.Engine.OnDesktopSwitch(); // a UAC prompt comes up while Back is held
        h.Engine.OnDesktopSwitchNotice(() => true); // and the original desktop is back, Back still held
        Assert.Equal(
            new[] { (HotkeyTransition.Activated, HotkeyDeactivation.Released), (HotkeyTransition.Deactivated, HotkeyDeactivation.DesktopSwitch) },
            Transitions(h));

        Assert.True(h.ButtonUp(Back).Suppress); // passed on, DefWindowProc would make it a Back command
        Assert.Empty(h.TakeTransitions());

        var (down, up) = h.Click(Back);
        Assert.True(down.Suppress);
        Assert.True(up.Suppress);
        Assert.Equal(
            new[] { (HotkeyTransition.Activated, HotkeyDeactivation.Released), (HotkeyTransition.Deactivated, HotkeyDeactivation.Released) },
            Transitions(h));
    }

    [Fact]
    public void A_press_after_a_release_missed_on_another_desktop_is_judged_afresh()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        h.ButtonDown(Back);
        h.Engine.OnDesktopSwitch(); // and Back goes up on the secure desktop, where the hook is not called
        h.TakeTransitions();

        // With Ctrl held a bare Back is the app's, press and release alike: the release still owed to the press before the
        // switch is not this press's.
        h.Down(LeftCtrl);
        Assert.False(h.ButtonDown(Back).Suppress);
        Assert.False(h.ButtonUp(Back).Suppress);
        h.Up(LeftCtrl);
        Assert.Empty(h.TakeTransitions());
    }

    [Fact]
    public void A_swallowed_button_release_stays_swallowed_through_capture_and_a_rebinding()
    {
        // Set chosen while Middle is held: the capture never saw the press, and the app must not see the release.
        using (var capture = new HotkeyEngineHarness(Bare(Middle)))
        {
            capture.ButtonDown(Middle);
            capture.Router.SetCaptureMode(true);
            Assert.True(capture.ButtonUp(Middle).Suppress);

            var (down, up) = capture.Click(Middle); // a press during the capture is the capture's
            Assert.False(down.Suppress);
            Assert.False(up.Suppress);
        }

        // Saved while Back is held, with Forward bound instead.
        using (var rebound = new HotkeyEngineHarness(Bare(Back)))
        {
            rebound.ButtonDown(Back);
            rebound.Router.UpdateBindings(Bare(Forward), null);
            rebound.Engine.OnWake();
            Assert.True(rebound.ButtonUp(Back).Suppress);
            Assert.False(rebound.Click(Back).Up.Suppress);
        }

        // The dictation-only trigger removed while its button is held; Back keeps the mouse hook.
        using var removed = new HotkeyEngineHarness(Bare(Back), Bare(Forward));
        removed.ButtonDown(Forward);
        removed.Router.UpdateBindings(Bare(Back), null);
        removed.Engine.OnWake();
        Assert.True(removed.ButtonUp(Forward).Suppress);
    }

    // A3: the leak check repairs a button only on the evidence of a release the hook swallowed.

    [Fact]
    public void The_leak_check_never_releases_a_button_the_engine_never_saw_go_down()
    {
        // Middle held for another app's drag since before the mouse hook existed: Windows holds it, and the engine has
        // no record of it at all.
        using var h = new HotkeyEngineHarness(Bare(Middle));
        var windows = new HashSet<uint> { Middle };
        var injected = new List<uint>();
        var reconciler = HotkeyService.CreateReconciler(h.Router, windows.Contains, Record(injected));

        var result = reconciler.ReleaseLeakedKeys(Bare(Middle));

        Assert.Empty(result.Released);
        Assert.Empty(result.Failed);
        Assert.Empty(injected);
    }

    [Fact]
    public void The_leak_check_repairs_a_button_whose_swallowed_release_left_windows_holding_it_once()
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        var windows = new HashSet<uint>();
        var injected = new List<uint>();
        var reconciler = HotkeyService.CreateReconciler(h.Router, windows.Contains, Record(injected));

        Assert.True(h.ButtonDown(Back).Suppress);
        windows.Add(Back); // a missed deadline let the press through to Windows
        Assert.True(h.ButtonUp(Back).Suppress); // and the release was swallowed, so Windows still holds Back

        Assert.Equal(new[] { Back }, reconciler.ReleaseLeakedKeys(Bare(Back)).Released);
        Assert.Equal(new[] { Back }, injected.ToArray());

        // That evidence answers one check: a later one touches nothing, whatever Windows holds by then.
        Assert.Empty(reconciler.ReleaseLeakedKeys(Bare(Back)).Released);
        Assert.Single(injected);
    }

    public static TheoryData<string> WaysTheEvidenceEnds => new()
    {
        "a later press", "a desktop switch", "capture", "new bindings", "a lost mouse hook", "a reinstall",
    };

    [Theory]
    [MemberData(nameof(WaysTheEvidenceEnds))]
    public void The_evidence_of_a_swallowed_release_ends_with_the_next_press_and_with_every_reset(string what)
    {
        using var h = new HotkeyEngineHarness(Bare(Back));
        var windows = new HashSet<uint> { Back }; // from here on the user really holds Back, in another app
        var injected = new List<uint>();
        var reconciler = HotkeyService.CreateReconciler(h.Router, windows.Contains, Record(injected));
        h.Click(Back); // swallowed, press and release

        switch (what)
        {
            case "a later press":
                h.Router.SetPaused(true);
                Assert.False(h.ButtonDown(Back).Suppress); // paused: this press reaches the app, and it is still held
                break;
            case "a desktop switch":
                h.Engine.OnDesktopSwitch();
                break;
            case "capture":
                h.Router.SetCaptureMode(true);
                h.Engine.OnWake();
                break;
            case "new bindings":
                h.Router.UpdateBindings(Bare(Back, HotkeyMode.Toggle), null);
                h.Engine.OnWake();
                break;
            case "a lost mouse hook":
                h.Engine.OnMouseHookLost();
                break;
            case "a reinstall":
                h.Router.BeginEngine(h.Transitions);
                break;
        }

        Assert.Empty(reconciler.ReleaseLeakedKeys(Bare(Back)).Released);
        Assert.Empty(injected);
    }

    [Fact]
    public void A_press_during_capture_ends_the_evidence_of_the_release_swallowed_just_before_it()
    {
        // Set chosen while Middle is held; its owed release is swallowed during the capture, which is evidence; then the
        // user presses Middle for the capture itself. The machines track nothing during a capture, so only that press
        // ending the evidence keeps the leak check from releasing the button the user is holding.
        using var h = new HotkeyEngineHarness(Bare(Middle));
        var windows = new HashSet<uint>();
        var injected = new List<uint>();
        var reconciler = HotkeyService.CreateReconciler(h.Router, windows.Contains, Record(injected));
        h.ButtonDown(Middle);
        h.Router.SetCaptureMode(true);
        Assert.True(h.ButtonUp(Middle).Suppress);

        Assert.False(h.ButtonDown(Middle).Suppress);
        windows.Add(Middle);

        Assert.Empty(reconciler.ReleaseLeakedKeys(Bare(Middle)).Released);
        Assert.Empty(injected);
    }

    [Fact]
    public void The_leak_check_still_repairs_a_key_as_before()
    {
        // Keys keep the rule they had: Windows holds it and the hook saw it released.
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        var windows = new HashSet<uint> { RightCtrl };
        var injected = new List<uint>();
        var reconciler = HotkeyService.CreateReconciler(h.Router, windows.Contains, Record(injected));

        Assert.Equal(new[] { RightCtrl }, reconciler.ReleaseLeakedKeys(HotkeyBinding.Legacy).Released);
    }

    private static Func<uint, bool> Record(List<uint> injected) => key =>
    {
        injected.Add(key);
        return true;
    };
}
