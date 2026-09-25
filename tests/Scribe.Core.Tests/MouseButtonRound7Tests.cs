using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 7. A7: no process is opened, and nothing but GetAsyncKeyState is called, inside the mouse hook's
/// deadline to decide a release: a release a gap has touched passes without a read, and one no gap has touched is read
/// once. A8: nothing on the mouse callback's path makes a delegate, the one the release decision invokes is made before
/// either hook exists, and the first owed release after production initialization allocates nothing.
/// </summary>
public sealed class MouseButtonRound7Tests
{
    private const uint Back = MouseButtons.Back;
    private const uint XButton1 = 0x0001;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    private static HotkeyBinding Bare(uint button) => HotkeyCaptureSession.Build([button], HotkeyMode.Hold);

    // Production initialization (the service's constructor makes the view every engine is given), then the first owed
    // release this test makes, through the callback's path from MouseHookFilter with a real reconcile signal, and with no
    // warm-up of the reader: the reader is called for the first time inside the measurement. Run alone, in a test process
    // of its own, it is the process's first owed release too, and MouseHookFilter's initializer runs inside the first
    // measurement (without CreateRouter's first call to GetAsyncKeyState the release allocated 48 bytes there, the
    // runtime's cost of any P/Invoke's first call).
    [Fact]
    public void The_first_owed_release_after_production_initialization_allocates_nothing()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);
        using var h = new HotkeyEngineHarness(Bare(Back), buttonDownInWindows: service.WindowsButtonState);
        using var signal = new HotkeyReconcileSignal(static () => { });
        var message = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MSLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(
                new NativeMethods.MSLLHOOKSTRUCT { mouseData = XButton1 << 16 }, message, fDeleteOld: false);
            var beforeFilter = GC.GetAllocatedBytesForCurrentThread();
            var scribesOwn = MouseHookFilter.IsScribesOwn(message);
            var filterAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeFilter;

            Assert.True(MouseHookFilter.Swallows(0, MouseHookFilter.WM_XBUTTONDOWN, message, h.Engine, signal));
            h.Engine.OnDesktopSwitch(); // the machines forget Back and the debt stays: the release is the reader's to decide
            h.TakeTransitions();

            var before = GC.GetAllocatedBytesForCurrentThread();
            var swallowed = MouseHookFilter.Swallows(0, MouseHookFilter.WM_XBUTTONUP, message, h.Engine, signal);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.False(scribesOwn);
            Assert.Equal(0, filterAllocated);
            Assert.True(swallowed); // Windows does not hold Back while this runs
            Assert.Equal(0, allocated);
        }
        finally
        {
            Marshal.FreeHGlobal(message);
        }
    }

    // A8, whatever ran first: no method on the mouse callback's path loads a function pointer or constructs a delegate,
    // which is how a delegate is made in IL, lazily cached by the compiler or not. The delegates the path invokes are
    // fields set before either hook exists.
    [Fact]
    public void No_method_on_the_mouse_callback_path_makes_a_delegate()
    {
        foreach (var method in CallbackPath())
        {
            foreach (var (code, token) in Instructions(method))
            {
                Assert.False(
                    code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn,
                    $"{Name(method)} loads a function pointer ({code.Name}).");
                if (code == OpCodes.Newobj)
                {
                    var constructor = method.Module.ResolveMethod(token)!;
                    Assert.False(
                        typeof(Delegate).IsAssignableFrom(constructor.DeclaringType),
                        $"{Name(method)} constructs a {constructor.DeclaringType!.Name}.");
                }
            }
        }
    }

    // A7: the release decision's one native call is GetAsyncKeyState. The engine's methods on the path call nothing in
    // NativeMethods and no P/Invoke; the view the service gives them is IsKeyLogicallyDown, which calls GetAsyncKeyState
    // and nothing else.
    [Fact]
    public void Get_async_key_state_is_the_one_native_call_the_release_decision_makes()
    {
        foreach (var method in DecisionPath())
        {
            foreach (var callee in Callees(method))
            {
                Assert.NotEqual(typeof(NativeMethods), callee.DeclaringType);
                Assert.False(
                    callee.Attributes.HasFlag(MethodAttributes.PinvokeImpl),
                    $"{Name(method)} calls the P/Invoke {Name(callee)}.");
            }
        }

        var reader = typeof(NativeMethods).GetMethod("IsKeyLogicallyDown", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(new[] { "GetAsyncKeyState" }, Callees(reader).Select(callee => callee.Name).ToArray());

        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);
        Assert.Equal(reader, service.WindowsButtonState!.Method);
    }

    // No foreground, window, process or token query is left in NativeMethods for anything to call: round 6's reading went
    // with round 7.
    [Fact]
    public void No_process_or_foreground_inspection_is_left_to_call()
    {
        var names = typeof(NativeMethods)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.Name)
            .ToHashSet();

        foreach (var gone in new[]
                 {
                     "GetForegroundWindow", "GetWindowThreadProcessId", "OpenProcess", "OpenProcessToken",
                     "GetTokenInformation", "ProcessIntegrityLevel", "WindowAllowsKeyStateReads", "ReadMouseButtonState",
                     "MouseButtonStateInWindows",
                 })
        {
            Assert.DoesNotContain(gone, names);
        }
    }

    private static IEnumerable<MethodInfo> DecisionPath()
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        yield return typeof(HotkeyEngine).GetMethod(nameof(HotkeyEngine.OnMouseButtonEvent), Any)!;
        yield return typeof(HotkeyEngine).GetMethod("SettleRelease", Any)!;
        yield return typeof(HotkeyEngine).GetMethod("TryOweRelease", Any)!;
        yield return typeof(HotkeyEngine).GetMethod("WindowsHoldsButton", Any)!;
    }

    private static IEnumerable<MethodInfo> CallbackPath()
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var installation = typeof(HotkeyService).GetNestedType("HookInstallation", BindingFlags.NonPublic)!;
        yield return installation.GetMethod("MouseHookCallback", Any)!;
        foreach (var name in new[] { "Swallows", "IsButtonMessage", "IsScribesOwn", "ButtonOf", "IsDown" })
        {
            yield return typeof(MouseHookFilter).GetMethod(name, Any)!;
        }

        foreach (var method in DecisionPath())
        {
            yield return method;
        }

        yield return typeof(HotkeyEngine).GetMethod("OnInput", Any)!;
        yield return typeof(NativeMethods).GetMethod("IsKeyLogicallyDown", Any)!;
        yield return typeof(HotkeyReconcileSignal).GetMethod(nameof(HotkeyReconcileSignal.Signal), Any)!;
    }

    private static IEnumerable<MethodBase> Callees(MethodInfo method) =>
        Instructions(method)
            .Where(instruction => instruction.Code == OpCodes.Call || instruction.Code == OpCodes.Callvirt)
            .Select(instruction => method.Module.ResolveMethod(instruction.Token)!);

    // The method's IL, one instruction at a time, with the 4-byte token of those that carry one (zero otherwise).
    private static IEnumerable<(OpCode Code, int Token)> Instructions(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (var i = 0; i < il.Length;)
        {
            short value;
            if (il[i] == 0xFE)
            {
                value = unchecked((short)(0xFE00 | il[i + 1]));
                i += 2;
            }
            else
            {
                value = il[i];
                i += 1;
            }

            var code = OpCodesByValue[value];
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                _ => 4,
            };
            var token = code.OperandType is OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType
                ? BitConverter.ToInt32(il, i)
                : 0;
            yield return (code, token);
            i += size;
        }
    }

    private static string Name(MethodBase method) => $"{method.DeclaringType!.Name}.{method.Name}";
}
