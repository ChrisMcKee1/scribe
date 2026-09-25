using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 7. A7: no process is opened, and nothing but GetAsyncKeyState is called, inside the mouse hook's
/// deadline to decide a release: a release a gap has touched passes without a read, and one no gap has touched is read
/// once. A8: nothing on the mouse callback's path makes a delegate, and the one the release decision invokes is made
/// before either hook exists; that the first owed release after production initialization allocates nothing is measured
/// cold by <see cref="MouseButtonRound8Tests"/>.
/// </summary>
public sealed class MouseButtonRound7Tests
{
    private const uint Back = MouseButtons.Back;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    private static HotkeyBinding Bare(uint button) => HotkeyCaptureSession.Build([button], HotkeyMode.Hold);

    // The first owed release after production initialization, which round 7 measured here in the shared test host, is now
    // measured in a load context of its own (MouseButtonRound8Tests, A10): here other tests had warmed what it measured.

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

    // G5 (Grok, round 6): an event that settles a debt asks for the check that drops a drain-only mouse hook kept for the
    // debt, passed or swallowed; an event that settles nothing asks for none. Since round 8 (A9) that is the mouse hook's
    // sync alone: none of these releases was one the bindings swallowed (the last mouse binding went before it), so none
    // asks for the keyboard's leak repair.
    [Theory]
    [InlineData("a release swallowed", true)]
    [InlineData("a release let through, Windows holding the button", true)]
    [InlineData("a new press that forgives the debt", true)]
    [InlineData("a release nothing owed", false)]
    public void Every_event_that_settles_a_debt_asks_for_the_check_that_drops_a_drain_only_hook(string what, bool asks)
    {
        var windowsHoldsBack = what.Contains("Windows holding", StringComparison.Ordinal);
        using var h = new HotkeyEngineHarness(Bare(Back), buttonDownInWindows: _ => windowsHoldsBack);
        if (what != "a release nothing owed")
        {
            Assert.True(h.ButtonDown(Back).Suppress);
        }

        h.Router.UpdateBindings(HotkeyBinding.DefaultDictation, null); // the last mouse binding goes: drain-only
        h.Engine.OnWake();

        var decision = what == "a new press that forgives the debt" ? h.ButtonDown(Back) : h.ButtonUp(Back);

        Assert.Equal(asks, decision.RequestMouseHookSync);
        Assert.False(decision.RequestReconcile);
        Assert.Equal(0, h.Engine.OwedButtonReleases);
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
        yield return typeof(HotkeyReconcileSignal).GetMethod(nameof(HotkeyReconcileSignal.SignalMouseHookSync), Any)!;
        yield return typeof(HotkeyReconcileSignal).GetMethod("Set", Any)!;
    }

    internal static IEnumerable<MethodBase> Callees(MethodBase method) =>
        Instructions(method)
            .Where(instruction => instruction.Code == OpCodes.Call || instruction.Code == OpCodes.Callvirt)
            .Select(instruction => method.Module.ResolveMethod(instruction.Token)!);

    // The method's IL, one instruction at a time, with the 4-byte token of those that carry a method, field, type or token (zero
    // otherwise).
    internal static IEnumerable<(OpCode Code, int Token)> Instructions(MethodBase method)
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
            var token = code.OperandType is OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType or OperandType.InlineField
                ? BitConverter.ToInt32(il, i)
                : 0;
            yield return (code, token);
            i += size;
        }
    }

    private static string Name(MethodBase method) => $"{method.DeclaringType!.Name}.{method.Name}";
}
