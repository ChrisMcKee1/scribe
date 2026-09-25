using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The real <c>WH_MOUSE_LL</c> hook. Like the other <c>Start_</c> tests these install a global hook, so the local
/// desktop filter leaves them to CI and to a private desktop nobody switches to (a hook there never sees the user's
/// input). The ones that inject mouse input go further and run only where the input desktop is a throwaway machine's:
/// on CI, or in a run that sets SCRIBE_INPUT_INJECTION_TESTS=1. Everything they inject carries a test marker, and a
/// guard hook installed before the service's, so called after it, swallows whatever the service lets through, so no
/// injected click reaches a window even there; what the guard receives is what the service passed on.
/// </summary>
public partial class HotkeyServiceTests
{
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(10);

    // Not Scribe's own marker: the service must take these as real input.
    private static readonly nuint TestInputMarker = unchecked((nuint)0x5343524954455354UL);

    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventXDown = 0x0080;
    private const uint MouseEventXUp = 0x0100;
    private const uint MouseEventWheel = 0x0800;
    private const uint MouseEventHWheel = 0x1000;
    private const uint LeftCtrlKey = 0xA2;
    private const uint PageDownKey = 0x22;

    private static HotkeyBinding BareButton(uint button, HotkeyMode mode = HotkeyMode.Hold) =>
        HotkeyCaptureSession.Build([button], mode);

    [Fact]
    public void Start_installs_the_mouse_hook_only_while_a_binding_presses_a_mouse_button()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.DefaultDictation, () => true);
        service.Start();

        // Keys alone: no system-wide mouse hook, so no pointer move ever waits for Scribe.
        Assert.False(service.MouseHookInstalled);

        service.UpdateBindings(BareButton(MouseButtons.Back), null);
        Assert.True(SpinWait.SpinUntil(() => service.MouseHookInstalled, HookTimeout), "The mouse hook was never installed.");

        // A button on the dictation-only trigger keeps it. A renewal proves the hook thread applied the change first.
        service.UpdateBindings(HotkeyBinding.DefaultDictation, BareButton(MouseButtons.Forward));
        AwaitRenewal(service);
        Assert.True(service.MouseHookInstalled);

        service.UpdateBindings(HotkeyBinding.DefaultDictation, HotkeyBinding.DefaultDictationOnly);
        Assert.True(SpinWait.SpinUntil(() => !service.MouseHookInstalled, HookTimeout), "The mouse hook was never removed.");

        // The watchdog's upkeep does not bring back a hook no binding needs.
        service.MaintainMouseHookNow();
        service.UpdateBindings(HotkeyBinding.DefaultDictation, BareButton(MouseButtons.Middle));
        Assert.True(SpinWait.SpinUntil(() => service.MouseHookInstalled, HookTimeout));
        service.UpdateBindings(HotkeyBinding.DefaultDictation, null);
        Assert.True(SpinWait.SpinUntil(() => !service.MouseHookInstalled, HookTimeout));

        service.Stop();
        Assert.False(service.MouseHookInstalled);
    }

    [Fact]
    public void Start_with_a_mouse_binding_has_the_mouse_hook_before_it_returns()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Middle), () => true);
        service.Start();

        Assert.True(service.MouseHookInstalled);
        Assert.Equal(1, service.MouseHookRegistrations);
        Assert.Equal(0, service.MouseHookLosses);
    }

    [Fact]
    public void Start_renews_the_mouse_hook_and_counts_a_registration_already_gone()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Back), () => true);
        service.Start();
        var first = service.MouseHookHandle;

        AwaitRenewal(service);
        var second = service.MouseHookHandle;
        Assert.NotEqual(first, second); // the new registration is made while the old one still exists
        Assert.Equal(0, service.MouseHookLosses);

        // What a hook Windows removed for missing its deadline is to the renewal: a registration that is already gone.
        Assert.True(UnhookWindowsHookEx(second), $"Could not remove the registration (Win32 error {Marshal.GetLastWin32Error()}).");
        AwaitRenewal(service);

        Assert.Equal(1, service.MouseHookLosses);
        Assert.True(service.MouseHookInstalled);
        Assert.NotEqual(second, service.MouseHookHandle);
    }

    [Fact]
    public void Start_keeps_a_swallowed_button_press_out_of_windows_own_view()
    {
        // The measurement the owed-release decision rests on (HotkeyEngine.OnMouseButtonEvent, MouseButtonRound6Tests): a
        // press the mouse hook swallows never reaches Windows' own view of the button, so that release reads up and stays
        // swallowed. Back's press, which the service swallows, then an unbound F20, which
        // reaches Windows, in one SendInput, which Windows processes in order: once it reports F20 down it has processed
        // Back's press too, and would report Back down had the swallow reached its view.
        if (!InputInjectionAllowed())
        {
            return;
        }

        const uint F20 = 0x83;
        using var guard = new InjectedMouseGuard();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Back), () => true);
        service.Start();

        Inject(ButtonDown(MouseButtons.Back), Key(F20, up: false));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => NativeMethods.IsKeyLogicallyDown(F20), HookTimeout), "F20 never reached Windows.");
            Assert.False(NativeMethods.IsKeyLogicallyDown(MouseButtons.Back), "Windows shows the swallowed Back press as down.");
        }
        finally
        {
            Inject(Key(F20, up: true), ButtonUp(MouseButtons.Back));
        }
    }

    [Fact]
    public void Start_hands_a_mouse_hook_found_gone_to_the_engine_once()
    {
        // The renewal that finds the registration gone tells the engine, on the hook thread and before the renewal counts,
        // so a dictation a button was driving is ended there (MouseButtonRecoveryTests); a healthy renewal tells it nothing.
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Back), () => true);
        service.Start();

        AwaitRenewal(service);
        Assert.Equal(0, service.MouseHookLossesHandled);

        Assert.True(UnhookWindowsHookEx(service.MouseHookHandle), $"Could not remove the registration (Win32 error {Marshal.GetLastWin32Error()}).");
        AwaitRenewal(service);
        Assert.Equal(1, service.MouseHookLossesHandled);

        AwaitRenewal(service);
        Assert.Equal(1, service.MouseHookLossesHandled);
    }

    // A real reinstall, with Windows' view scripted (round 7). The replacement starts owing nothing: between the old thread's
    // exit and the new registration no hook saw the mouse, and no reading in the callback can be trusted to tell the
    // swallowed press from one Windows got in that time. So the release of Middle, held through the reinstall, reaches the
    // app whatever Windows shows: at worst one stray release. The test plays the hook thread between its messages.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Start_lets_a_release_held_across_a_reinstall_through(bool windowsHoldsMiddle)
    {
        if (NativeMethods.ThreadDesktopReceivesInput() == true && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            return; // someone's own desktop: the test's events are real engine input, so it runs on CI or a private desktop
        }

        var held = new ConcurrentDictionary<uint, bool>();
        var router = new HotkeyCommandRouter(BareButton(MouseButtons.Middle), new object(), isLogicallyDown: null, held.ContainsKey);
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, router, () => true);
        service.Start();
        Assert.True(service.CurrentEngineForTests!.OnMouseButtonEvent(MouseButtons.Middle, isDown: true).Suppress);

        service.ReinstallHookNow();
        Assert.True(service.MouseHookInstalled);
        if (windowsHoldsMiddle)
        {
            held[MouseButtons.Middle] = true; // a press inside an older hook goes on to Windows after the registration
        }

        Assert.Equal(0, service.CurrentEngineForTests!.OwedButtonReleases);
        Assert.False(service.CurrentEngineForTests!.OnMouseButtonEvent(MouseButtons.Middle, isDown: false).Suppress);
    }

    // Which gaps drop what the engine owes, through the real hooks: a healthy renewal overlaps the old registration and
    // changes nothing; a renewal that finds the registration gone drops it; a reinstall's new engine owes nothing. The test
    // plays the hook thread between its messages.
    [Fact]
    public void Start_drops_owed_releases_only_across_a_time_no_hook_saw_the_mouse()
    {
        if (NativeMethods.ThreadDesktopReceivesInput() == true && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            return; // someone's own desktop: the test's events are real engine input, so it runs on CI or a private desktop
        }

        const int BackBit = 1 << (int)MouseButtons.Back;
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Back), () => true);
        service.Start();
        Assert.True(service.CurrentEngineForTests!.OnMouseButtonEvent(MouseButtons.Back, isDown: true).Suppress);

        AwaitRenewal(service);
        Assert.Equal(BackBit, service.CurrentEngineForTests!.OwedButtonReleases);

        Assert.True(UnhookWindowsHookEx(service.MouseHookHandle), $"Could not remove the registration (Win32 error {Marshal.GetLastWin32Error()}).");
        AwaitRenewal(service);
        Assert.Equal(0, service.CurrentEngineForTests!.OwedButtonReleases);

        Assert.True(service.CurrentEngineForTests!.OnMouseButtonEvent(MouseButtons.Back, isDown: true).Suppress);
        Assert.Equal(BackBit, service.CurrentEngineForTests!.OwedButtonReleases);
        service.ReinstallHookNow();
        Assert.Equal(0, service.CurrentEngineForTests!.OwedButtonReleases);
    }

    // G1: the last mouse binding replaced by a key while Back is still held after a swallowed press. The service keeps a
    // drain-only mouse hook until the owed release arrives, and removes it once the debt is gone, whether the release came
    // or Back was pressed again. The test plays the hook thread between its messages; nothing else feeds this engine on
    // a desktop that receives no input (on CI the injected version below covers the real callback).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Start_keeps_a_drain_only_mouse_hook_until_the_owed_release_is_paid(bool pressedAgain)
    {
        if (NativeMethods.ThreadDesktopReceivesInput() == true && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            return; // someone's own desktop: the test's events are real engine input, so it runs on CI or a private desktop
        }

        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Back), () => true);
        service.Start();
        var engine = service.CurrentEngineForTests!;
        Assert.True(engine.OnMouseButtonEvent(MouseButtons.Back, isDown: true).Suppress);

        service.UpdateBindings(HotkeyBinding.DefaultDictation, null);
        AwaitRenewal(service);
        Assert.False(engine.UsesMouseButtons);
        Assert.True(service.MouseHookInstalled, "The mouse hook was removed while a swallowed press still owed its release.");

        if (pressedAgain)
        {
            Assert.False(engine.OnMouseButtonEvent(MouseButtons.Back, isDown: true).Suppress); // no binding: the user's own
        }
        else
        {
            Assert.True(engine.OnMouseButtonEvent(MouseButtons.Back, isDown: false).Suppress);
        }

        AwaitMouseHookRemoved(service);
    }

    // G5 (Grok, round 6): after the last mouse binding went, the drain-only mouse hook is removed as soon as the release it
    // was kept for settles, whichever way: swallowed (Windows up), let through (Windows holds the button), or forgiven by a
    // new press of the button, with no watchdog upkeep. Until then every pointer move waits for the hook thread. The test
    // plays the hook callback through MouseHookFilter, with the service's own reconcile signal, between the hook thread's
    // messages.
    [Theory]
    [InlineData("swallowed")]
    [InlineData("let through")]
    [InlineData("forgiven by a new press")]
    public void Start_removes_the_drain_only_mouse_hook_as_soon_as_its_owed_release_settles(string settled)
    {
        if (NativeMethods.ThreadDesktopReceivesInput() == true && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            return; // someone's own desktop: the test's events are real engine input, so it runs on CI or a private desktop
        }

        var windowsHoldsBack = settled == "let through";
        var router = new HotkeyCommandRouter(BareButton(MouseButtons.Back), new object(), isLogicallyDown: null, _ => windowsHoldsBack);
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, router, () => true);
        service.Start();
        using var message = new XButtonMessage(0x0001);
        Assert.True(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONDOWN, message));
        service.UpdateBindings(HotkeyBinding.DefaultDictation, null);
        AwaitRenewal(service);
        Assert.True(service.MouseHookInstalled, "The mouse hook was removed while a swallowed press still owed its release.");

        switch (settled)
        {
            case "swallowed":
                Assert.True(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONUP, message));
                break;
            case "let through":
                Assert.False(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONUP, message));
                break;
            default:
                Assert.False(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONDOWN, message)); // no binding: the user's own
                break;
        }

        AwaitMouseHookRemoved(service, allowUpkeep: false);
    }

    // The same when the debt goes with a lost hook: the renewal that finds the registration gone drops what the engine owed,
    // and with no binding pressing a mouse button the hook it just registered goes too, in that same renewal.
    [Fact]
    public void Start_removes_the_drain_only_mouse_hook_in_the_renewal_that_drops_its_debt()
    {
        if (NativeMethods.ThreadDesktopReceivesInput() == true && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            return; // someone's own desktop: the test's events are real engine input, so it runs on CI or a private desktop
        }

        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Back), () => true);
        service.Start();
        Assert.True(service.CurrentEngineForTests!.OnMouseButtonEvent(MouseButtons.Back, isDown: true).Suppress);
        service.UpdateBindings(HotkeyBinding.DefaultDictation, null);
        AwaitRenewal(service);
        Assert.True(service.MouseHookInstalled);

        Assert.True(UnhookWindowsHookEx(service.MouseHookHandle), $"Could not remove the registration (Win32 error {Marshal.GetLastWin32Error()}).");
        AwaitRenewal(service);

        Assert.Equal(1, service.MouseHookLossesHandled);
        Assert.Equal(0, service.CurrentEngineForTests!.OwedButtonReleases);
        Assert.False(service.MouseHookInstalled, "The drain-only mouse hook outlived the debt the lost hook dropped.");
    }

    // A9 through the service (review round 8): Astra's capture sequence, played between the hook thread's messages with the
    // service's own reconcile signal and pass, Windows' key state scripted to show the Left Ctrl the user holds, and every
    // key-up the leak repair would send recorded instead of sent. The press that forgives the debt the lock screen left
    // asks for the mouse hook's sync alone, and the pass that runs for it releases nothing.
    [Fact]
    public void Start_releases_no_key_for_the_capture_sequence_that_forgives_a_debt()
    {
        if (NativeMethods.ThreadDesktopReceivesInput() == true && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            return; // someone's own desktop: the test's events are real engine input, so it runs on CI or a private desktop
        }

        var injected = new ConcurrentQueue<uint>();
        using var service = ScriptedKeysService(
            ChordOf(LeftCtrlKey, MouseButtons.Back), windowsHoldsBack: false, desktopReceivesInput: false, injected);
        service.Start();
        using var message = new XButtonMessage(0x0001);
        var engine = service.CurrentEngineForTests!;
        Assert.False(engine.OnKeyEvent(LeftCtrlKey, isDown: true).Suppress);
        Assert.True(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONDOWN, message));

        NotifyWinEvent(EventSystemDesktopSwitch, GetDesktopWindow(), ObjectIdWindow, ChildIdSelf); // the lock screen
        Assert.True(
            SpinWait.SpinUntil(() => service.DesktopSwitchesSeen == 1, HookTimeout), "The desktop switch was never applied.");
        Assert.Equal(1 << (int)MouseButtons.Back, engine.OwedButtonReleases);

        service.SetCaptureMode(true);
        AwaitRenewal(service); // the hook thread has applied capture
        var passes = service.ReconcilePassesRun;
        Assert.False(engine.OnKeyEvent(LeftCtrlKey, isDown: true).Suppress);
        Assert.False(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONDOWN, message));

        Assert.True(
            SpinWait.SpinUntil(() => service.ReconcilePassesRun > passes, HookTimeout), "The forgiving press started no pass.");
        Assert.Empty(injected);
        Assert.Equal(0, engine.OwedButtonReleases);
    }

    // The round-6 trigger in capture: Set chosen with the chord still held, then Back released. The release is swallowed,
    // still owed, and asks for no repair, whose view capture had emptied.
    [Fact]
    public void Start_releases_no_key_when_a_debt_from_before_capture_is_released_during_it()
    {
        if (NativeMethods.ThreadDesktopReceivesInput() == true && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            return; // someone's own desktop: the test's events are real engine input, so it runs on CI or a private desktop
        }

        var injected = new ConcurrentQueue<uint>();
        using var service = ScriptedKeysService(
            ChordOf(LeftCtrlKey, MouseButtons.Back), windowsHoldsBack: false, desktopReceivesInput: true, injected);
        service.Start();
        using var message = new XButtonMessage(0x0001);
        var engine = service.CurrentEngineForTests!;
        Assert.False(engine.OnKeyEvent(LeftCtrlKey, isDown: true).Suppress);
        Assert.True(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONDOWN, message));

        service.SetCaptureMode(true);
        AwaitRenewal(service);
        var passes = service.ReconcilePassesRun;
        Assert.True(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONUP, message));

        Assert.True(SpinWait.SpinUntil(() => service.ReconcilePassesRun > passes, HookTimeout), "The release started no pass.");
        Assert.Empty(injected);
        Assert.Equal(0, engine.OwedButtonReleases);
    }

    // G5's drain-only removal, with a key held: the chord is held while new bindings with no mouse button are saved, so the
    // engine's view of Left Ctrl is cleared while Windows still holds it. However the owed release settles, the drain-only
    // hook goes at once and the leak repair is not asked for, so no Ctrl-up is sent while the user holds Ctrl.
    [Theory]
    [InlineData("swallowed")]
    [InlineData("let through")]
    [InlineData("forgiven by a new press")]
    public void Start_removes_the_drain_only_mouse_hook_without_releasing_a_held_key(string settled)
    {
        if (NativeMethods.ThreadDesktopReceivesInput() == true && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            return; // someone's own desktop: the test's events are real engine input, so it runs on CI or a private desktop
        }

        var injected = new ConcurrentQueue<uint>();
        using var service = ScriptedKeysService(
            ChordOf(LeftCtrlKey, MouseButtons.Back), windowsHoldsBack: settled == "let through", desktopReceivesInput: true, injected);
        service.Start();
        using var message = new XButtonMessage(0x0001);
        Assert.False(service.CurrentEngineForTests!.OnKeyEvent(LeftCtrlKey, isDown: true).Suppress);
        Assert.True(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONDOWN, message));
        service.UpdateBindings(ChordOf(LeftCtrlKey, PageDownKey), null);
        AwaitRenewal(service);
        Assert.True(service.MouseHookInstalled, "The mouse hook was removed while a swallowed press still owed its release.");
        var passes = service.ReconcilePassesRun;

        switch (settled)
        {
            case "swallowed":
                Assert.True(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONUP, message));
                break;
            case "let through":
                Assert.False(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONUP, message));
                break;
            default:
                Assert.False(PlayMouseCallback(service, MouseHookFilter.WM_XBUTTONDOWN, message)); // no binding: the user's own
                break;
        }

        AwaitMouseHookRemoved(service, allowUpkeep: false);
        Assert.True(SpinWait.SpinUntil(() => service.ReconcilePassesRun > passes, HookTimeout), "The settle started no pass.");
        Assert.Empty(injected);
    }

    // Round 9 (A11): a repair that capture stopped is not run again when capture ends, through the real hook thread and
    // signal. The Left Ctrl the user holds from the capture gesture is not in the engine's view then, so a replay would
    // send its key-up while it is held; a real leak is repaired at the next trigger instead.
    [Fact]
    public void Start_runs_no_repair_again_when_capture_ends()
    {
        if (NativeMethods.ThreadDesktopReceivesInput() == true && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            return; // someone's own desktop: the test's events are real engine input, so it runs on CI or a private desktop
        }

        var injected = new ConcurrentQueue<uint>();
        using var service = ScriptedKeysService(
            ChordOf(LeftCtrlKey, MouseButtons.Back), windowsHoldsBack: false, desktopReceivesInput: true, injected);
        service.Start();
        service.SetCaptureMode(true);
        AwaitRenewal(service); // the hook thread has applied capture
        var passes = service.ReconcilePassesRun;
        service.ReconcileSignalForTests!.Signal(); // a repair asked for during the capture
        Assert.True(SpinWait.SpinUntil(() => service.ReconcilePassesRun > passes, HookTimeout), "The repair never ran.");

        service.SetCaptureMode(false);
        AwaitRenewal(service); // the hook thread has applied capture's end
        Thread.Sleep(250); // ten times the pass's settling delay: a replay would have run by now

        Assert.Equal(passes + 1, service.ReconcilePassesRun);
        Assert.Empty(injected);
    }

    // A service whose leak repair sees a scripted Windows holding Left Ctrl and records the key-ups it would send, over a
    // router whose view of a mouse button in Windows is scripted too.
    private static HotkeyService ScriptedKeysService(
        HotkeyBinding binding, bool windowsHoldsBack, bool desktopReceivesInput, ConcurrentQueue<uint> injected)
    {
        var router = new HotkeyCommandRouter(binding, new object(), isLogicallyDown: null, _ => windowsHoldsBack);
        return new HotkeyService(
            NullLogger<HotkeyService>.Instance,
            router,
            () => desktopReceivesInput,
            key => key == LeftCtrlKey,
            key =>
            {
                injected.Enqueue(key);
                return true;
            });
    }

    private static HotkeyBinding ChordOf(uint first, uint second) => HotkeyCaptureSession.Build([first, second], HotkeyMode.Hold);

    // A button message as the mouse hook callback gets it, and the callback's own decision on it.
    private static bool PlayMouseCallback(HotkeyService service, int message, XButtonMessage data) =>
        MouseHookFilter.Swallows(0, message, data.Pointer, service.CurrentEngineForTests!, service.ReconcileSignalForTests);

    // An MSLLHOOKSTRUCT in native memory for an X button (1 for Back, 2 for Forward), as the mouse hook receives one.
    private sealed class XButtonMessage : IDisposable
    {
        public XButtonMessage(uint xButton)
        {
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MSLLHOOKSTRUCT>());
            Marshal.StructureToPtr(new NativeMethods.MSLLHOOKSTRUCT { mouseData = xButton << 16 }, Pointer, fDeleteOld: false);
        }

        public nint Pointer { get; }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    [Fact]
    public void Start_swallows_an_owed_button_release_after_the_last_mouse_binding_is_gone()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        using var guard = new InjectedMouseGuard();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Back), () => true);
        var events = new ConcurrentQueue<string>();
        service.Activated += (_, e) => events.Enqueue("start " + e.Trigger);
        service.Deactivated += (_, e) => events.Enqueue("stop " + e.Trigger);
        service.Start();

        Inject(ButtonDown(MouseButtons.Back));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 1, HookTimeout), "The bound button started nothing.");
        service.UpdateBindings(HotkeyBinding.DefaultDictation, null); // Save with a key while Back is still held
        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout), "The new bindings left the dictation running.");
        AwaitRenewal(service);
        Assert.True(service.MouseHookInstalled);

        // Back's release, then an unbound Forward click: once the guard has the click, Back's release, sent before it,
        // would be there too had the drain-only hook passed it on.
        Inject(ButtonUp(MouseButtons.Back), ButtonDown(MouseButtons.Forward), ButtonUp(MouseButtons.Forward));
        Assert.True(guard.WaitFor(2), "The unbound click never came through.");
        Assert.Equal(
            new[] { Message(ButtonDown(MouseButtons.Forward)), Message(ButtonUp(MouseButtons.Forward)) },
            guard.SeenWithoutMoves);

        // The swallowed release asks for the leak check, which asks the hook thread to drop the hook nobody needs now:
        // no watchdog upkeep is needed for that.
        AwaitMouseHookRemoved(service, allowUpkeep: false);
    }

    // Removed at the hook thread's next sync once nothing needs it: the swallowed release's own request, or, with
    // allowUpkeep, the watchdog's upkeep, which is the only sync when the debt went with a new press.
    private static void AwaitMouseHookRemoved(HotkeyService service, bool allowUpkeep = true)
    {
        if (allowUpkeep)
        {
            service.MaintainMouseHookNow();
        }

        Assert.True(
            SpinWait.SpinUntil(() => !service.MouseHookInstalled, HookTimeout),
            "The drain-only mouse hook outlived the release it was kept for.");
    }

    [Fact]
    public void Start_ends_a_button_hold_whose_release_came_while_the_mouse_hook_was_gone()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        using var guard = new InjectedMouseGuard();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Back), () => true);
        var events = new ConcurrentQueue<string>();
        service.Activated += (_, e) => events.Enqueue("start " + e.Trigger);
        service.Deactivated += (_, e) => events.Enqueue("stop " + e.Trigger + " " + e.Deactivation);
        service.Start();

        Inject(ButtonDown(MouseButtons.Back));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 1, HookTimeout), "The bound button started nothing.");

        // Windows removes the hook (a missed deadline), the user lets go of Back meanwhile, and only the guard sees it.
        Assert.True(UnhookWindowsHookEx(service.MouseHookHandle), $"Could not remove the registration (Win32 error {Marshal.GetLastWin32Error()}).");
        Inject(ButtonUp(MouseButtons.Back));
        Assert.True(guard.WaitFor(1), "The release never reached the guard.");
        Assert.Single(events);

        // The watchdog's renewal finds the hook gone and ends the dictation, with no further click.
        AwaitRenewal(service);
        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout), "The renewal left the dictation running.");
        Assert.Equal(new[] { "start Standard", "stop Standard MouseHookLost" }, events.ToArray());
    }

    [Fact]
    public void Start_keeps_a_swallowed_button_release_from_the_app_across_a_desktop_switch()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        // The input-desktop query answers "lost input" for the notice, as it would under a UAC prompt, so the switch is
        // applied; the button is then released back on this desktop.
        using var guard = new InjectedMouseGuard();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Back), () => false);
        var events = new ConcurrentQueue<string>();
        service.Activated += (_, e) => events.Enqueue("start " + e.Trigger);
        service.Deactivated += (_, e) => events.Enqueue("stop " + e.Trigger + " " + e.Deactivation);
        service.Start();

        Inject(ButtonDown(MouseButtons.Back));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 1, HookTimeout), "The bound button started nothing.");
        NotifyWinEvent(EventSystemDesktopSwitch, GetDesktopWindow(), ObjectIdWindow, ChildIdSelf);
        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout), "The desktop switch left the dictation running.");

        // Back's release, then an unbound Forward click: once the guard has the click, Back's release, sent before it,
        // would be there too had the service passed it on.
        Inject(ButtonUp(MouseButtons.Back), ButtonDown(MouseButtons.Forward), ButtonUp(MouseButtons.Forward));
        Assert.True(guard.WaitFor(2), "The unbound click never came through the service's hook.");
        Assert.Equal(
            new[] { Message(ButtonDown(MouseButtons.Forward)), Message(ButtonUp(MouseButtons.Forward)) },
            guard.SeenWithoutMoves);
        Assert.Equal(new[] { "start Standard", "stop Standard DesktopSwitch" }, events.ToArray());
    }

    [Theory]
    [InlineData(MouseButtons.Middle)]
    [InlineData(MouseButtons.Back)]
    [InlineData(MouseButtons.Forward)]
    public void Start_swallows_a_bound_mouse_button_while_it_dictates(uint button)
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        using var guard = new InjectedMouseGuard(); // installed first, so the service's hook runs before it
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(button), () => true);
        var events = new ConcurrentQueue<string>();
        service.Activated += (_, e) => events.Enqueue("start " + e.Trigger);
        service.Deactivated += (_, e) => events.Enqueue("stop " + e.Trigger);
        service.Start();

        Inject(ButtonDown(button));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 1, HookTimeout), "The bound button started nothing.");

        // A renewal while the button is held keeps its state: its release still ends the dictation and is swallowed.
        AwaitRenewal(service);
        Inject(ButtonUp(button));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout), "The bound button's release ended nothing.");
        Assert.Equal(new[] { "start Standard", "stop Standard" }, events.ToArray());

        // An unbound button sent last: once the guard has it, the bound button's events, sent before it, would have
        // reached the guard too, had the service let them through.
        var sentinel = button == MouseButtons.Back ? MouseButtons.Forward : MouseButtons.Back;
        Inject(ButtonDown(sentinel), ButtonUp(sentinel));
        Assert.True(guard.WaitFor(2), "The unbound button never came through the service's hook.");
        Assert.Equal(new[] { Message(ButtonDown(sentinel)), Message(ButtonUp(sentinel)) }, guard.SeenWithoutMoves);
        Assert.Equal(4, service.MouseButtonEventsSeen);
    }

    [Fact]
    public void Start_hands_moves_wheels_and_unbound_buttons_straight_on()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        using var guard = new InjectedMouseGuard();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Back), () => true);
        var events = 0;
        service.Activated += (_, _) => Interlocked.Increment(ref events);
        service.Start();

        // The guard swallows each of these, so the pointer never moves and nothing scrolls. The last one, a second
        // horizontal wheel turn of its own size, says when the guard has everything.
        NativeMethods.INPUT[] inputs =
        [
            Mouse(MouseEventMove, dx: 1), Mouse(MouseEventMove, dx: -1),
            Mouse(MouseEventWheel, data: 120), Mouse(MouseEventHWheel, data: 120),
            ButtonDown(MouseButtons.Middle), ButtonUp(MouseButtons.Middle),
            ButtonDown(MouseButtons.Forward), ButtonUp(MouseButtons.Forward),
            Mouse(MouseEventMove, dx: 1), Mouse(MouseEventMove, dx: -1),
            Mouse(MouseEventHWheel, data: 240),
        ];
        Inject(inputs);

        var expected = inputs.Select(Message).Where(seen => seen.Message != MouseHookFilter.WM_MOUSEMOVE).ToArray();
        Assert.True(guard.WaitFor(expected.Length), $"The guard received {guard.SeenWithoutMoves.Length} of {expected.Length} events.");
        Assert.Equal(expected, guard.SeenWithoutMoves);
        Assert.True(guard.MovesSeen >= 4, $"The guard received {guard.MovesSeen} of the 4 moves.");
        Assert.Equal(4, service.MouseButtonEventsSeen); // the buttons only: no move or wheel reached the engine
        Assert.Equal(0, Volatile.Read(ref events));
    }

    [Fact]
    public void Start_lets_a_bare_bound_button_through_while_ctrl_is_held_and_takes_it_once_ctrl_is_up()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        using var guard = new InjectedMouseGuard();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Back), () => true);
        var events = new ConcurrentQueue<string>();
        service.Activated += (_, _) => events.Enqueue("start");
        service.Deactivated += (_, _) => events.Enqueue("stop");
        service.Start();

        // The modifier rule asks Windows whether Ctrl is down, so the Ctrl press goes through to Windows (it is not
        // bound, so neither hook takes it) and is released whatever happens.
        Inject(Key(LeftCtrlKey, up: false));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => NativeMethods.IsKeyLogicallyDown(NativeMethods.VK_CONTROL), HookTimeout));
            Inject(ButtonDown(MouseButtons.Back), ButtonUp(MouseButtons.Back));
            Assert.True(guard.WaitFor(2), "Ctrl+Back never reached the app.");
        }
        finally
        {
            Inject(Key(LeftCtrlKey, up: true));
        }

        Assert.True(SpinWait.SpinUntil(() => !NativeMethods.IsKeyLogicallyDown(NativeMethods.VK_CONTROL), HookTimeout));
        Assert.Empty(events);

        Inject(ButtonDown(MouseButtons.Back), ButtonUp(MouseButtons.Back), ButtonDown(MouseButtons.Middle), ButtonUp(MouseButtons.Middle));
        Assert.True(guard.WaitFor(4));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout));
        Assert.Equal(new[] { "start", "stop" }, events.ToArray());
        Assert.Equal(
            new[]
            {
                Message(ButtonDown(MouseButtons.Back)), Message(ButtonUp(MouseButtons.Back)),
                Message(ButtonDown(MouseButtons.Middle)), Message(ButtonUp(MouseButtons.Middle)),
            },
            guard.SeenWithoutMoves);
    }

    [Fact]
    public void Start_toggles_a_button_dictation_on_one_click_and_off_on_the_next()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        using var guard = new InjectedMouseGuard();
        using var service = new HotkeyService(
            NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Forward, HotkeyMode.Toggle), () => true);
        var events = new ConcurrentQueue<string>();
        service.Activated += (_, _) => events.Enqueue("start");
        service.Deactivated += (_, _) => events.Enqueue("stop");
        service.Start();

        Inject(ButtonDown(MouseButtons.Forward), ButtonUp(MouseButtons.Forward));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 1, HookTimeout));
        Inject(ButtonDown(MouseButtons.Forward), ButtonUp(MouseButtons.Forward));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout));
        Assert.Equal(new[] { "start", "stop" }, events.ToArray());

        Inject(ButtonDown(MouseButtons.Middle), ButtonUp(MouseButtons.Middle));
        Assert.True(guard.WaitFor(2));
        Assert.Equal(
            new[] { Message(ButtonDown(MouseButtons.Middle)), Message(ButtonUp(MouseButtons.Middle)) }, guard.SeenWithoutMoves);
    }

    [Fact]
    public void Start_passes_scribes_own_injected_button_release_on_untouched()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        using var guard = new InjectedMouseGuard();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, BareButton(MouseButtons.Forward), () => true);
        service.Start();

        // A button release carrying Scribe's own marker (Scribe injects no mouse input today, so only a test sends one):
        // Windows hands the hook only the low half of the marker, which the hook still recognizes, so the engine never
        // takes such a release as the user's.
        var marked = Mouse(MouseEventXUp, data: 2);
        marked.U.mi.dwExtraInfo = SyntheticInputMarker.Value;
        Inject(marked);

        Assert.True(guard.WaitFor(1), "Scribe's own release never came through the service's hook.");
        Assert.Equal(new[] { (MouseHookFilter.WM_XBUTTONUP, 0x0002_0000u) }, guard.SeenWithoutMoves);
        Assert.Equal(0, service.MouseButtonEventsSeen);
    }

    [Theory]
    [InlineData(0x7Cu)] // F13, which most keyboards cannot type and a mouse's software commonly sends
    [InlineData(0xB3u)] // Play/Pause, a media key some mice send from firmware
    [InlineData(0xA6u)] // Browser Back
    public void Start_swallows_a_key_a_mouse_s_software_sends_while_it_dictates(uint key)
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        using var service = new HotkeyService(
            NullLogger<HotkeyService>.Instance, HotkeyCaptureSession.Build([key], HotkeyMode.Hold), () => true);
        var events = new ConcurrentQueue<string>();
        service.Activated += (_, _) => events.Enqueue("start");
        service.Deactivated += (_, _) => events.Enqueue("stop");
        service.Start();
        Assert.False(service.MouseHookInstalled); // a key needs no mouse hook

        Inject(Key(key, up: false));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => events.Count == 1, HookTimeout), "The bound key started nothing.");
            AssertSwallowedWhileHeld(key);
        }
        finally
        {
            Inject(Key(key, up: true));
        }

        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout), "The bound key's release ended nothing.");
        Assert.Equal(new[] { "start", "stop" }, events.ToArray());
    }

    [Fact]
    public void Start_matches_a_shortcut_a_mouse_s_software_sends_with_generic_modifier_codes()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        // Ctrl+Shift+F13 as a remapper that injects the generic VK_CONTROL and VK_SHIFT sends it.
        using var service = new HotkeyService(
            NullLogger<HotkeyService>.Instance,
            HotkeyCaptureSession.Build([0xA2, 0xA0, 0x7C], HotkeyMode.Hold),
            () => true);
        var events = new ConcurrentQueue<string>();
        service.Activated += (_, _) => events.Enqueue("start");
        service.Deactivated += (_, _) => events.Enqueue("stop");
        service.Start();

        Inject(Key(0x11, up: false), Key(0x10, up: false), Key(0x7C, up: false));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => events.Count == 1, HookTimeout), "The shortcut started nothing.");
            AssertSwallowedWhileHeld(0x7C);
            Assert.True(NativeMethods.IsKeyLogicallyDown(NativeMethods.VK_CONTROL), "The modifiers were not left to Windows.");
        }
        finally
        {
            Inject(Key(0x7C, up: true), Key(0x10, up: true), Key(0x11, up: true));
        }

        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout), "The shortcut's release ended nothing.");
    }

    // A key the hook swallowed never reaches Windows, so Windows' own key state leaves it up. An unbound key sent after it
    // does reach Windows: once Windows reports that one down, the bound key, sent first, would be down too had the hook
    // let it through.
    private static void AssertSwallowedWhileHeld(uint key)
    {
        const uint F20 = 0x83;
        Inject(Key(F20, up: false));
        try
        {
            Assert.True(
                SpinWait.SpinUntil(() => NativeMethods.IsKeyLogicallyDown(F20), HookTimeout),
                "The unbound key never reached Windows.");
            Assert.False(NativeMethods.IsKeyLogicallyDown(key), $"Key 0x{key:X2} reached Windows although it is bound.");
        }
        finally
        {
            Inject(Key(F20, up: true));
        }
    }

    // Injected input lands on the input desktop, under whatever the pointer is over, so only a throwaway machine's:
    // CI, or a run that asks for it. CI must have an input desktop, or these would pass without testing anything.
    private static bool InputInjectionAllowed()
    {
        var onCi = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        var asked = Environment.GetEnvironmentVariable("SCRIBE_INPUT_INJECTION_TESTS") == "1";
        if (!onCi && !asked)
        {
            return false;
        }

        var receivesInput = NativeMethods.ThreadDesktopReceivesInput();
        Assert.True(
            receivesInput == true,
            $"The mouse hook injection tests need the input desktop, and this thread's desktop reports {receivesInput}.");
        return true;
    }

    // One renewal, the watchdog's upkeep, and the wait for the hook thread to finish it: everything queued before it has
    // been applied by then.
    private static void AwaitRenewal(HotkeyService service)
    {
        var registrations = service.MouseHookRegistrations;
        service.MaintainMouseHookNow();
        Assert.True(
            SpinWait.SpinUntil(() => service.MouseHookRegistrations > registrations, HookTimeout),
            "The hook thread never renewed the mouse hook.");
    }

    private static NativeMethods.INPUT Mouse(uint flags, uint data = 0, int dx = 0) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        U = new NativeMethods.InputUnion
        {
            mi = new NativeMethods.MOUSEINPUT { dx = dx, mouseData = data, dwFlags = flags, dwExtraInfo = TestInputMarker },
        },
    };

    private static NativeMethods.INPUT Key(uint virtualKey, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = (ushort)virtualKey,
                dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = TestInputMarker,
            },
        },
    };

    private static NativeMethods.INPUT ButtonDown(uint button) => button switch
    {
        MouseButtons.Middle => Mouse(MouseEventMiddleDown),
        MouseButtons.Back => Mouse(MouseEventXDown, data: 1),
        _ => Mouse(MouseEventXDown, data: 2),
    };

    private static NativeMethods.INPUT ButtonUp(uint button) => button switch
    {
        MouseButtons.Middle => Mouse(MouseEventMiddleUp),
        MouseButtons.Back => Mouse(MouseEventXUp, data: 1),
        _ => Mouse(MouseEventXUp, data: 2),
    };

    // The message and mouseData the low-level hook receives for an injected mouse input.
    private static (int Message, uint MouseData) Message(NativeMethods.INPUT input)
    {
        var mi = input.U.mi;
        return mi.dwFlags switch
        {
            MouseEventMove => (MouseHookFilter.WM_MOUSEMOVE, 0u),
            MouseEventWheel => (MouseHookFilter.WM_MOUSEWHEEL, mi.mouseData << 16),
            MouseEventHWheel => (MouseHookFilter.WM_MOUSEHWHEEL, mi.mouseData << 16),
            MouseEventMiddleDown => (MouseHookFilter.WM_MBUTTONDOWN, 0u),
            MouseEventMiddleUp => (MouseHookFilter.WM_MBUTTONUP, 0u),
            MouseEventXDown => (MouseHookFilter.WM_XBUTTONDOWN, mi.mouseData << 16),
            MouseEventXUp => (MouseHookFilter.WM_XBUTTONUP, mi.mouseData << 16),
            _ => throw new ArgumentOutOfRangeException(nameof(input)),
        };
    }

    private static void Inject(params NativeMethods.INPUT[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        Assert.True(
            sent == inputs.Length, $"SendInput sent {sent} of {inputs.Length} inputs (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    /// <summary>
    /// A low-level mouse hook of the test's own, on its own thread, that swallows every event carrying the test marker
    /// (or Scribe's own) and records it: installed before the service's hook, it is called after it, so it sees exactly
    /// what the service passed on, and no injected event goes further. Real input carries no marker and passes untouched.
    /// </summary>
    private sealed class InjectedMouseGuard : IDisposable
    {
        private static readonly int MouseDataOffset =
            (int)Marshal.OffsetOf<NativeMethods.MSLLHOOKSTRUCT>(nameof(NativeMethods.MSLLHOOKSTRUCT.mouseData));

        private static readonly int ExtraInfoOffset =
            (int)Marshal.OffsetOf<NativeMethods.MSLLHOOKSTRUCT>(nameof(NativeMethods.MSLLHOOKSTRUCT.dwExtraInfo));

        private readonly NativeMethods.LowLevelMouseProc _proc;
        private readonly ConcurrentQueue<(int Message, uint MouseData)> _seen = new();
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private uint _threadId;
        private nint _hook;
        private int _error;

        public InjectedMouseGuard()
        {
            _proc = Callback;
            _thread = new Thread(Run) { IsBackground = true, Name = "test-mouse-guard" };
            _thread.Start();
            Assert.True(_ready.Wait(HookTimeout), "The guard hook thread never started.");
            Assert.True(_hook != 0, $"The guard hook could not be installed (Win32 error {_error}); nothing is injected without it.");
        }

        public (int Message, uint MouseData)[] Seen => _seen.ToArray();

        /// <summary>
        /// Everything but moves, in order. Windows adds a move of its own before an injected button event now and then
        /// (measured on CI: a WM_MOUSEMOVE carrying the same extra information appeared between an injected X button's
        /// press and release), so the tests judge the order of the button and wheel events alone.
        /// </summary>
        public (int Message, uint MouseData)[] SeenWithoutMoves =>
            _seen.Where(seen => seen.Message != MouseHookFilter.WM_MOUSEMOVE).ToArray();

        public int MovesSeen => _seen.Count(seen => seen.Message == MouseHookFilter.WM_MOUSEMOVE);

        public bool WaitFor(int buttonAndWheelEvents) =>
            SpinWait.SpinUntil(() => SeenWithoutMoves.Length >= buttonAndWheelEvents, HookTimeout);

        public void Dispose()
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, 0, 0);
            _thread.Join(HookTimeout);
            _ready.Dispose();
        }

        private void Run()
        {
            NativeMethods.EnsureMessageQueue();
            _threadId = NativeMethods.GetCurrentThreadId();
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            _error = _hook == 0 ? Marshal.GetLastWin32Error() : 0;
            _ready.Set();
            if (_hook == 0)
            {
                return;
            }

            // Low-level hook callbacks are delivered inside GetMessage.
            while (NativeMethods.GetMessage(out _, 0, 0, 0) > 0)
            {
            }

            NativeMethods.UnhookWindowsHookEx(_hook);
        }

        private nint Callback(int nCode, nint wParam, nint lParam)
        {
            // Only the low 32 bits of dwExtraInfo reach a low-level mouse hook (see MouseHookFilter.Marker).
            var extra = unchecked((uint)Marshal.ReadInt32(lParam, ExtraInfoOffset));
            if (nCode >= 0 && (extra == unchecked((uint)TestInputMarker) || extra == MouseHookFilter.Marker))
            {
                _seen.Enqueue(((int)wParam, (uint)Marshal.ReadInt32(lParam, MouseDataOffset)));
                return 1;
            }

            return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
        }
    }
}
