using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The hook callbacks' cold path, which <see cref="MouseButtonRound8Tests"/> runs in a load context of its own. There this
/// assembly and Scribe.Core are fresh copies, so their type initializers, statics and P/Invoke bindings are cold whatever
/// the test process ran before; only the framework and the test libraries are shared. Public and without a test
/// attribute, so that test finds it by name in the fresh copy and nothing runs it on its own.
/// </summary>
public static class MouseColdPathScenario
{
    private const int WmMouseMove = 0x0200;
    private const uint UnboundKey = 0x7C; // F13: no binding here uses it

    /// <summary>
    /// Production initialization (the service's constructor), then four first calls, each measured alone on this thread
    /// with <see cref="GC.GetAllocatedBytesForCurrentThread"/>, with nothing called beforehand to warm it:
    /// <list type="number">
    /// <item>MouseHookFilter's type initializer, which its first read of a message runs;</item>
    /// <item>the mouse callback's fast path for a move, one comparison and the first call to CallNextHookEx (made outside
    /// any hook, where it has nothing to pass on and returns zero);</item>
    /// <item>the first key event, for a key no binding uses, down and up through the engine;</item>
    /// <item>the first owed release through the mouse callback's path, with a real reconcile signal: its release decision
    /// is the first call to GetAsyncKeyState.</item>
    /// </list>
    /// Returns those four byte counts, then 1 or 0 for: the message was not Scribe's own, the move was passed on, the press
    /// was swallowed and the release was swallowed, then CallNextHookEx's result.
    /// </summary>
    public static long[] Run()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);

        // Start's first step, before either hook exists: the service's own statics (the watchdog's periods).
        RuntimeHelpers.RunClassConstructor(typeof(HotkeyService).TypeHandle);
        using var h = new HotkeyEngineHarness(
            HotkeyCaptureSession.Build([MouseButtons.Back], HotkeyMode.Hold),
            buttonDownInWindows: service.WindowsButtonState);
        using var signal = new HotkeyReconcileSignal(static _ => { });
        var message = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MSLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(new NativeMethods.MSLLHOOKSTRUCT { mouseData = 1u << 16 }, message, fDeleteOld: false);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var scribesOwn = MouseHookFilter.IsScribesOwn(message);
            var filter = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            var passedOn = !MouseHookFilter.IsButtonMessage(WmMouseMove);
            var next = NativeMethods.CallNextHookEx(0, -1, WmMouseMove, 0);
            var fastPath = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            var keyDown = h.Engine.OnKeyEvent(UnboundKey, isDown: true);
            var keyUp = h.Engine.OnKeyEvent(UnboundKey, isDown: false);
            var key = GC.GetAllocatedBytesForCurrentThread() - before;

            var pressed = MouseHookFilter.Swallows(0, MouseHookFilter.WM_XBUTTONDOWN, message, h.Engine, signal);
            h.Engine.OnDesktopSwitch(); // the machines forget Back and the debt stays: the release is the reader's to decide
            h.TakeTransitions();

            before = GC.GetAllocatedBytesForCurrentThread();
            var swallowed = MouseHookFilter.Swallows(0, MouseHookFilter.WM_XBUTTONUP, message, h.Engine, signal);
            var release = GC.GetAllocatedBytesForCurrentThread() - before;

            return
            [
                filter,
                fastPath,
                key,
                release,
                scribesOwn ? 0 : 1,
                passedOn ? 1 : 0,
                pressed ? 1 : 0,
                swallowed ? 1 : 0,
                next,
                keyDown.Suppress || keyUp.Suppress ? 1 : 0,
            ];
        }
        finally
        {
            Marshal.FreeHGlobal(message);
        }
    }
}
