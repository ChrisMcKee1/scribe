using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;

namespace Scribe.Core.Tests;

/// <summary>
/// The first calls of what stream RD added to the keyboard callback's path, which <see cref="RemoteDesktopHookPathTests"/>
/// runs in a load context of its own, as <see cref="MouseColdPathScenario"/> is run: there this assembly and Scribe.Core
/// are fresh copies, so their type initializers and statics are cold whatever ran before. It runs once in the default
/// context first (<see cref="ColdPathMeasurement.RunFresh"/>), so the framework's process-wide first uses on its path are
/// taken there and not in a window (stream TR, item 1). Public and without a test attribute, so that test finds it by name
/// in the fresh copy and nothing runs it on its own.
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
    /// Returns, first, those six byte counts, then 1 or 0 for: the probe was recognized, the echo was recognized, a new event
    /// was not taken for an echo; then how many key-downs the engine judged uncertain (1). Then, for a failure's message
    /// (stream TR, item 2), read outside each window and allocating nothing: what the runtime did on this thread in each
    /// window (<see cref="RuntimeWork"/>, <see cref="RuntimeWork.Width"/> values each). Each window is marked in the
    /// runtime's event stream for a capture (<see cref="WindowMarks"/>).
    /// </summary>
    public static long[][] Run()
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

            // The failure message's readings, used once first, so that no first-call cost of theirs lands in a window.
            var work = new long[6 * RuntimeWork.Width];
            RuntimeWork.Now().Since(RuntimeWork.Now()).Write(work, 0);

            WindowMarks.Open(0);
            var runtime = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var identity = KeyboardHookFilter.Identity(message);
            var extraInfo = (nuint)Marshal.ReadIntPtr(message, KeyboardHookFilter.ExtraInfoOffset);
            var probe = KeyboardHookFilter.IsProbe(identity.VirtualKey, isUp: true, extraInfo);
            var reads = GC.GetAllocatedBytesForCurrentThread() - before;
            RuntimeWork.Now().Since(runtime).Write(work, 0);
            WindowMarks.Close(0);

            WindowMarks.Open(1);
            runtime = RuntimeWork.Now();
            before = GC.GetAllocatedBytesForCurrentThread();
            var fresh = !passOn.IsEcho(identity);
            passOn.Enter(identity);
            var echo = passOn.IsEcho(identity);
            passOn.Leave();
            var pass = GC.GetAllocatedBytesForCurrentThread() - before;
            RuntimeWork.Now().Since(runtime).Write(work, RuntimeWork.Width);
            WindowMarks.Close(1);

            WindowMarks.Open(2);
            runtime = RuntimeWork.Now();
            before = GC.GetAllocatedBytesForCurrentThread();
            notice.Notify(0x1234);
            var hop = GC.GetAllocatedBytesForCurrentThread() - before;
            RuntimeWork.Now().Since(runtime).Write(work, 2 * RuntimeWork.Width);
            WindowMarks.Close(2);

            // F20: bound by nothing here.
            var down = new KeyEventIdentity(0x83, 0x6B, 0, 8);
            var up = new KeyEventIdentity(0x83, 0x6B, 0x80, 9);
            WindowMarks.Open(3);
            runtime = RuntimeWork.Now();
            before = GC.GetAllocatedBytesForCurrentThread();
            _ = KeyboardHookFilter.Route(h.Engine, passOn, throughCurrentRegistration: true, down, isDown: true, 0);
            _ = KeyboardHookFilter.Route(h.Engine, passOn, throughCurrentRegistration: true, up, isDown: false, 0);
            var route = GC.GetAllocatedBytesForCurrentThread() - before;
            RuntimeWork.Now().Since(runtime).Write(work, 3 * RuntimeWork.Width);
            WindowMarks.Close(3);

            passOn.Enter(down);
            WindowMarks.Open(4);
            runtime = RuntimeWork.Now();
            before = GC.GetAllocatedBytesForCurrentThread();
            _ = KeyboardHookFilter.Route(h.Engine, passOn, throughCurrentRegistration: false, down, isDown: true, 0);
            var echoRoute = GC.GetAllocatedBytesForCurrentThread() - before;
            RuntimeWork.Now().Since(runtime).Write(work, 4 * RuntimeWork.Width);
            WindowMarks.Close(4);
            passOn.Leave();

            // Review round 2, item 1: right after the hook became the newest registration, the route of a key-down of a key
            // the engine has not seen (uncertain, so judged and passed on) and of its release, inside the window.
            h.Engine.SetUncertaintyWindow(875);
            h.Engine.OnRegisteredAhead(100);
            var unseen = new KeyEventIdentity(0x84, 0x6C, 0, 150);
            var unseenUp = new KeyEventIdentity(0x84, 0x6C, 0x80, 180);
            WindowMarks.Open(5);
            runtime = RuntimeWork.Now();
            before = GC.GetAllocatedBytesForCurrentThread();
            _ = KeyboardHookFilter.Route(h.Engine, passOn, throughCurrentRegistration: true, unseen, isDown: true, 0);
            _ = KeyboardHookFilter.Route(h.Engine, passOn, throughCurrentRegistration: true, unseenUp, isDown: false, 0);
            var uncertainRoute = GC.GetAllocatedBytesForCurrentThread() - before;
            RuntimeWork.Now().Since(runtime).Write(work, 5 * RuntimeWork.Width);
            WindowMarks.Close(5);

            long[] measured =
            [
                reads, pass, hop, route, echoRoute, uncertainRoute, probe ? 1 : 0, echo ? 1 : 0, fresh ? 1 : 0,
                h.Engine.UncertainPresses,
            ];
            return [measured, work];
        }
        finally
        {
            Marshal.FreeHGlobal(message);
        }
    }
}
