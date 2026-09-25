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
        Assert.Equal(new[] { Message(ButtonDown(sentinel)), Message(ButtonUp(sentinel)) }, guard.Seen);
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

        // The guard swallows each of these, so the pointer never moves and nothing scrolls.
        NativeMethods.INPUT[] inputs =
        [
            Mouse(MouseEventMove, dx: 1), Mouse(MouseEventMove, dx: -1),
            Mouse(MouseEventWheel, data: 120), Mouse(MouseEventHWheel, data: 120),
            ButtonDown(MouseButtons.Middle), ButtonUp(MouseButtons.Middle),
            ButtonDown(MouseButtons.Forward), ButtonUp(MouseButtons.Forward),
            Mouse(MouseEventMove, dx: 1), Mouse(MouseEventMove, dx: -1),
        ];
        Inject(inputs);

        Assert.True(guard.WaitFor(inputs.Length), $"The guard received {guard.Seen.Length} of {inputs.Length} events.");
        Assert.Equal(inputs.Select(Message).Select(Comparable).ToArray(), guard.Seen.Select(Comparable).ToArray());
        Assert.Equal(4, service.MouseButtonEventsSeen); // the buttons only: no move or wheel reached the engine
        Assert.Equal(0, Volatile.Read(ref events));
    }

    // mouseData means nothing for a move, so only the message is compared for one.
    private static (int Message, uint MouseData) Comparable((int Message, uint MouseData) seen) =>
        seen.Message == MouseHookFilter.WM_MOUSEMOVE ? (seen.Message, 0u) : seen;

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
            guard.Seen);
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
        Assert.Equal(new[] { Message(ButtonDown(MouseButtons.Middle)), Message(ButtonUp(MouseButtons.Middle)) }, guard.Seen);
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

        // What the leaked-input check sends for a bound button Windows still holds: Windows hands the hook only the low
        // half of its marker, which the hook still recognizes, so the engine never takes the release as the user's.
        Inject(NativeMethods.MarkedMouseButtonUp(MouseButtons.Forward));

        Assert.True(guard.WaitFor(1), "Scribe's own release never came through the service's hook.");
        Assert.Equal(new[] { (MouseHookFilter.WM_XBUTTONUP, 0x0002_0000u) }, guard.Seen);
        Assert.Equal(0, service.MouseButtonEventsSeen);
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

        public bool WaitFor(int count) => SpinWait.SpinUntil(() => _seen.Count >= count, HookTimeout);

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
