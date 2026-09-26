using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// The first calls of what stream RD added to the keyboard callback's path, which <see cref="RemoteDesktopHookPathTests"/>
/// runs in a load context of its own, as <see cref="MouseColdPathScenario"/> is run: there this assembly and Scribe.Core
/// are fresh copies, so their type initializers and statics are cold whatever ran before. Public and without a test
/// attribute, so that test finds it by name in the fresh copy and nothing runs it on its own.
/// </summary>
public static class KeyboardColdPathScenario
{
    /// <summary>
    /// Production initialization (the service's constructor and its type initializer, Start's first step before any hook
    /// exists), then five first calls, each measured alone on this thread with
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/>, with nothing warmed:
    /// <list type="number">
    /// <item>KeyboardHookFilter's reads of a key event's identity and its extra information, and the probe test;</item>
    /// <item>the echo check and a pass on and off (KeyEventPassOn), as the callback makes them around CallNextHookEx;</item>
    /// <item>the foreground notice's hop to the pool (an interlocked exchange, one or two increments and a SetEvent);</item>
    /// <item>the callback's route for a key no binding uses, through the current registration, down and up;</item>
    /// <item>the route of an echo through a replaced registration;</item>
    /// <item>the route of an uncertain key-down and its release, right after the hook became the newest registration.</item>
    /// </list>
    /// Returns those six byte counts, then 1 or 0 for: the probe was recognized, the echo was recognized, a new event
    /// was not taken for an echo; then how many key-downs the engine judged uncertain (1).
    /// </summary>
    public static long[] Run()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);
        RuntimeHelpers.RunClassConstructor(typeof(HotkeyService).TypeHandle);
        using var h = new HotkeyEngineHarness(HotkeyBinding.Legacy);
        var passOn = new KeyEventPassOn();
        using var notice = new ForegroundNotice(static (_, _, _) => { });
        var message = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.KBDLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(
                new NativeMethods.KBDLLHOOKSTRUCT
                {
                    vkCode = NativeMethods.VK_PROBE, scanCode = 0, flags = 0x90, time = 7, dwExtraInfo = SyntheticInputMarker.Value,
                },
                message,
                fDeleteOld: false);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var identity = KeyboardHookFilter.Identity(message);
            var extraInfo = (nuint)Marshal.ReadIntPtr(message, KeyboardHookFilter.ExtraInfoOffset);
            var probe = KeyboardHookFilter.IsProbe(identity.VirtualKey, isUp: true, extraInfo);
            var reads = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            var fresh = !passOn.IsEcho(identity);
            passOn.Enter(identity);
            var echo = passOn.IsEcho(identity);
            passOn.Leave();
            var pass = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            notice.Notify(0x1234);
            var hop = GC.GetAllocatedBytesForCurrentThread() - before;

            // F20: bound by nothing here.
            var down = new KeyEventIdentity(0x83, 0x6B, 0, 8);
            var up = new KeyEventIdentity(0x83, 0x6B, 0x80, 9);
            before = GC.GetAllocatedBytesForCurrentThread();
            _ = KeyboardHookFilter.Route(h.Engine, passOn, throughCurrentRegistration: true, down, isDown: true, 0);
            _ = KeyboardHookFilter.Route(h.Engine, passOn, throughCurrentRegistration: true, up, isDown: false, 0);
            var route = GC.GetAllocatedBytesForCurrentThread() - before;

            passOn.Enter(down);
            before = GC.GetAllocatedBytesForCurrentThread();
            _ = KeyboardHookFilter.Route(h.Engine, passOn, throughCurrentRegistration: false, down, isDown: true, 0);
            var echoRoute = GC.GetAllocatedBytesForCurrentThread() - before;
            passOn.Leave();

            // Review round 2, item 1: right after the hook became the newest registration, the route of a key-down of a key
            // the engine has not seen (uncertain, so judged and passed on) and of its release, inside the window.
            h.Engine.SetUncertaintyWindow(875);
            h.Engine.OnRegisteredAhead(100);
            var unseen = new KeyEventIdentity(0x84, 0x6C, 0, 150);
            var unseenUp = new KeyEventIdentity(0x84, 0x6C, 0x80, 180);
            before = GC.GetAllocatedBytesForCurrentThread();
            _ = KeyboardHookFilter.Route(h.Engine, passOn, throughCurrentRegistration: true, unseen, isDown: true, 0);
            _ = KeyboardHookFilter.Route(h.Engine, passOn, throughCurrentRegistration: true, unseenUp, isDown: false, 0);
            var uncertainRoute = GC.GetAllocatedBytesForCurrentThread() - before;

            return
            [
                reads, pass, hop, route, echoRoute, uncertainRoute, probe ? 1 : 0, echo ? 1 : 0, fresh ? 1 : 0,
                h.Engine.UncertainPresses,
            ];
        }
        finally
        {
            Marshal.FreeHGlobal(message);
        }
    }
}
