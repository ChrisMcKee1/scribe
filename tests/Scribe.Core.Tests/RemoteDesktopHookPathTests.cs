using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Scribe.Core.Hotkeys;
using Scribe.Core.TextInjection;

namespace Scribe.Core.Tests;

/// <summary>
/// What stream RD added to the hook thread keeps every rule stream MB established for it (AGENTS.md, "Hotkey hook
/// threading"): nothing on a callback's path takes a lock, logs, makes a delegate, allocates, or queries a process or the
/// foreground window, and nothing the hook thread runs between messages for the moves ahead takes a lock either. The
/// process lookup the moves need runs on the pool (<see cref="KeyboardHookPrecedence"/>).
/// </summary>
public sealed class RemoteDesktopHookPathTests
{
    [Fact]
    public void The_keyboard_callback_s_new_path_makes_no_delegate_takes_no_lock_logs_nothing_and_queries_no_process()
    {
        foreach (var method in KeyboardCallbackPath())
        {
            foreach (var (code, token) in MouseButtonRound7Tests.Instructions(method))
            {
                Assert.False(code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn, $"{Name(method)} loads a function pointer ({code.Name}).");
                if (code == OpCodes.Newobj)
                {
                    var constructor = method.Module.ResolveMethod(token)!;
                    Assert.False(
                        typeof(Delegate).IsAssignableFrom(constructor.DeclaringType),
                        $"{Name(method)} constructs a {constructor.DeclaringType!.Name}.");
                }
            }

            foreach (var callee in MouseButtonRound7Tests.Callees(method))
            {
                Assert.False(IsForbiddenOnTheHookPath(callee), $"{Name(method)} calls {callee.DeclaringType!.Name}.{callee.Name}.");
            }
        }
    }

    [Fact]
    public void Nothing_the_hook_thread_runs_for_the_moves_ahead_takes_a_lock_or_queries_a_process()
    {
        foreach (var method in MoveAheadPath())
        {
            foreach (var callee in MouseButtonRound7Tests.Callees(method))
            {
                Assert.False(IsForbiddenOnTheHookPath(callee), $"{Name(method)} calls {callee.DeclaringType!.Name}.{callee.Name}.");
            }
        }
    }

    // The hook thread's end disposes the pool side of the moves ahead and the foreground notice, after every hook of the
    // installation is unhooked, so no callback can be waiting on it. None of those disposals calls a monitor or lock of
    // its own; inside .NET, disposing a timer and unregistering a wait take the runtime's own locks, as disposing the
    // reconcile signal already did (stream MB), which no hook callback or repair ever holds.
    [Fact]
    public void The_hook_thread_s_end_calls_no_monitor_or_lock_of_its_own()
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var installation = typeof(HotkeyService).GetNestedType("HookInstallation", BindingFlags.NonPublic)!;
        foreach (var method in new MethodBase[]
                 {
                     installation.GetMethod("DisposeSignals", Any)!,
                     typeof(KeyboardHookPrecedence).GetMethod(nameof(KeyboardHookPrecedence.Dispose), Any)!,
                     typeof(ForegroundNotice).GetMethod(nameof(ForegroundNotice.Dispose), Any)!,
                     typeof(HotkeyReconcileSignal).GetMethod(nameof(HotkeyReconcileSignal.Dispose), Any)!,
                 })
        {
            foreach (var callee in MouseButtonRound7Tests.Callees(method))
            {
                Assert.False(
                    callee.DeclaringType == typeof(Monitor) || callee.DeclaringType == typeof(Lock) ||
                    callee.DeclaringType == typeof(Lock.Scope),
                    $"{Name(method)} calls {callee.DeclaringType!.Name}.{callee.Name}.");
            }
        }
    }

    // In the shared test host other tests warm what this measures, so, as MouseButtonRound8Tests does for the mouse, the
    // scenario runs in a load context of its own with fresh copies of this assembly and Scribe.Core.
    [Fact]
    public void The_keyboard_callback_s_new_path_allocates_nothing_cold()
    {
        var folder = Path.GetDirectoryName(typeof(RemoteDesktopHookPathTests).Assembly.Location)!;
        var context = new FreshCopies(folder);
        var tests = context.LoadFromAssemblyName(typeof(RemoteDesktopHookPathTests).Assembly.GetName());
        var scenario = tests.GetType(typeof(KeyboardColdPathScenario).FullName!, throwOnError: true)!;

        var measured = (long[])scenario.GetMethod(nameof(KeyboardColdPathScenario.Run))!.Invoke(null, null)!;

        Assert.NotSame(typeof(RemoteDesktopHookPathTests).Assembly, tests);
        Assert.Equal(new long[] { 0, 0, 0, 0, 0, 0, 1, 1, 1, 1 }, measured);
    }

    // The warning no longer asserts a missed deadline: a Remote Desktop client's hook, called first, can keep every key.
    [Fact]
    public void The_watchdog_says_why_the_hook_can_stop_receiving_events()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var service = File.ReadAllText(Path.Combine(root.FullName, "src", "Scribe.Core", "Hotkeys", "HotkeyService.cs"));
        Assert.DoesNotContain("Windows removes low-level \" +", service, StringComparison.Ordinal);
        Assert.Contains("either Windows removed it (a callback that missed the", service, StringComparison.Ordinal);
        Assert.Contains("another program's keyboard hook, such as a Remote Desktop client's, now receives keys", service, StringComparison.Ordinal);
        Assert.Contains("In front: {App} (remote desktop client: {Remote}). Reinstalling.", service, StringComparison.Ordinal);
    }

    private static bool IsForbiddenOnTheHookPath(MethodBase callee)
    {
        var type = callee.DeclaringType!;
        return type == typeof(Monitor) || type == typeof(Lock) || type == typeof(Lock.Scope) ||
            type == typeof(WindowOwners) || type == typeof(KeyScanCodes) || type == typeof(RemoteClientProcesses) ||
            type == typeof(KeyboardHookPrecedence) || type == typeof(ThreadPool) || type == typeof(Task) ||
            type.FullName!.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal) ||
            (type == typeof(InjectionNativeMethods) && callee.Name is "GetForegroundWindow" or "GetWindowThreadProcessId");
    }

    // The keyboard callback, through the registration's own delegate, the foreground notice's callback and the hop it
    // makes, and what the keyboard callback calls that stream RD added.
    private static IEnumerable<MethodBase> KeyboardCallbackPath()
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var installation = typeof(HotkeyService).GetNestedType("HookInstallation", BindingFlags.NonPublic)!;
        var registration = installation.GetNestedType("KeyboardRegistration", BindingFlags.NonPublic)!;
        yield return registration.GetMethod("Callback", Any)!;
        yield return installation.GetMethod("HookCallback", Any)!;
        yield return installation.GetMethod("ForegroundCallback", Any)!;
        yield return typeof(KeyboardHookFilter).GetMethod(nameof(KeyboardHookFilter.Route), Any)!;
        yield return typeof(KeyboardHookFilter).GetMethod(nameof(KeyboardHookFilter.IsScribesOwn), Any)!;
        yield return typeof(KeyboardHookFilter).GetMethod(nameof(KeyboardHookFilter.IsProbe), Any)!;
        yield return typeof(KeyboardHookFilter).GetMethod(nameof(KeyboardHookFilter.Identity), Any)!;
        yield return typeof(KeyEventIdentity).GetMethod(nameof(KeyEventIdentity.Same), Any)!;
        foreach (var name in new[] { nameof(KeyEventPassOn.IsEcho), nameof(KeyEventPassOn.Enter), nameof(KeyEventPassOn.Leave) })
        {
            yield return typeof(KeyEventPassOn).GetMethod(name, Any)!;
        }

        yield return typeof(ForegroundNotice).GetMethod(nameof(ForegroundNotice.Notify), Any)!;
    }

    // What the hook thread runs between messages for a move ahead and its clean-up. Since review round 2 (item 5) the move
    // also reads the window in front, through the service's delegate (one GetForegroundWindow in production, which waits
    // for no other thread and which this scan cannot follow), to drop a move the user left the remote client before; no
    // process is looked up there, which the forbidden list checks.
    private static IEnumerable<MethodBase> MoveAheadPath()
    {
        const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var installation = typeof(HotkeyService).GetNestedType("HookInstallation", BindingFlags.NonPublic)!;
        foreach (var name in new[]
                 {
                     "MoveAhead", "StillInFront", "DeferMove", "ForgetDeferredMove", "RetryDeferredMove", "FreeRegistration",
                     "ReleaseRetired", "Release",
                 })
        {
            yield return installation.GetMethod(name, Declared)!;
        }

        yield return typeof(HotkeyEngine).GetProperty(nameof(HotkeyEngine.HoldsSwallowedKey))!.GetMethod!;
        yield return typeof(ChordStateMachine).GetProperty(nameof(ChordStateMachine.HoldsSwallowedKey))!.GetMethod!;
        yield return typeof(KeySet).GetMethod(nameof(KeySet.ContainsAnyExcept), Declared)!;
        foreach (var method in typeof(RetiredHookRegistrations).GetMethods(Declared))
        {
            yield return method;
        }
    }

    private static string Name(MethodBase method) => $"{method.DeclaringType!.Name}.{method.Name}";

    private sealed class FreshCopies(string folder) : AssemblyLoadContext("keyboard-cold-path")
    {
        protected override Assembly? Load(AssemblyName name) =>
            name.Name is "Scribe.Core" or "Scribe.Core.Tests"
                ? LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll"))
                : null;
    }
}
