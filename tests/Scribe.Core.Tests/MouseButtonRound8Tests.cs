using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 8. A9: settling a mouse debt asks for the mouse hook's sync alone, never for the keyboard's leak repair,
/// and the repair never runs while binding capture owns input. After a state clear the engine's view of the keys is
/// incomplete (capture tracks none at all), so a repair asked for then found a modifier the user was holding down in
/// Windows and not in the engine, and released it under the user's finger. A10: the cold-path allocation test runs in a
/// load context of its own, where nothing another test ran can have warmed what it measures.
/// </summary>
public sealed class MouseButtonRound8Tests
{
    private const uint Back = MouseButtons.Back;
    private const uint LeftCtrl = 0xA2;
    private const uint PageDown = 0x22;
    private const int BackDebt = 1 << (int)MouseButtons.Back;

    // A hang guard, never the verdict: every wait below is for something certain to happen.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static HotkeyBinding Chord(uint first, uint second) => HotkeyCaptureSession.Build([first, second], HotkeyMode.Hold);

    // Astra's A9 sequence, exactly. Left Ctrl and Back are bound; the chord is pressed, so Back's swallowed press owes its
    // release; the lock screen takes the input and both are released there, unseen (the desktop reset keeps the debt);
    // back on the desktop the user chooses Set, holds Left Ctrl and presses Back to capture a chord. Capture passes both
    // untracked, and Back's new press forgives the old debt. At ea36b06 that press asked for the keyboard's leak repair,
    // which found Left Ctrl down in Windows and not in the engine, and sent a Ctrl-up while the user held Ctrl.
    [Fact]
    public void The_capture_sequence_that_forgives_a_debt_releases_no_key()
    {
        var windowsKeys = new HashSet<uint>();
        var injected = new List<uint>();
        using var h = Harness(Chord(LeftCtrl, Back), windowsKeys, injected);

        windowsKeys.Add(LeftCtrl);
        Play(h, h.Down(LeftCtrl));
        Assert.True(Play(h, h.ButtonDown(Back)).Suppress);
        Assert.Equal(BackDebt, h.Engine.OwedButtonReleases);

        h.Engine.OnDesktopSwitchNotice(() => false); // the lock screen: Ctrl and Back go up there, where no hook sees them
        windowsKeys.Remove(LeftCtrl);
        Assert.Equal(BackDebt, h.Engine.OwedButtonReleases);

        h.Router.SetCaptureMode(true); // Set
        h.Engine.OnWake();
        windowsKeys.Add(LeftCtrl);
        Assert.False(Play(h, h.Down(LeftCtrl)).Suppress);
        var press = Play(h, h.ButtonDown(Back));

        Assert.Empty(injected);
        Assert.False(press.Suppress);
        Assert.False(press.RequestReconcile);
        Assert.True(press.RequestMouseHookSync); // the forgiven debt may have been a drain-only hook's last
        Assert.Equal(0, h.Engine.OwedButtonReleases);

        Assert.False(Play(h, h.ButtonUp(Back)).Suppress);
        Assert.False(Play(h, h.Up(LeftCtrl)).Suppress);
        Assert.Empty(injected);
    }

    // The same harm through the trigger round 6 gave a button, a swallowed release: a release swallowed only for a debt from
    // before a state clear. The clear forgot the keys the user holds (a UAC prompt taken with the chord held, Set chosen,
    // or new bindings saved), so a repair then saw Left Ctrl down in Windows and not in the engine. Only a release the
    // bindings themselves swallowed asks for the repair now (the control below).
    [Theory]
    [InlineData("a desktop switch")]
    [InlineData("capture")]
    [InlineData("new bindings")]
    public void A_release_swallowed_only_for_a_debt_from_before_a_state_clear_releases_no_key(string clear)
    {
        var windowsKeys = new HashSet<uint> { LeftCtrl };
        var injected = new List<uint>();
        using var h = Harness(Chord(LeftCtrl, Back), windowsKeys, injected);
        Play(h, h.Down(LeftCtrl));
        Assert.True(Play(h, h.ButtonDown(Back)).Suppress);

        switch (clear)
        {
            case "a desktop switch":
                h.Engine.OnDesktopSwitchNotice(() => false); // and back, with Ctrl and Back held throughout
                break;
            case "capture":
                h.Router.SetCaptureMode(true);
                h.Engine.OnWake();
                break;
            default:
                h.Router.UpdateBindings(Chord(LeftCtrl, PageDown), null); // saved with the chord still held
                h.Engine.OnWake();
                break;
        }

        var release = Play(h, h.ButtonUp(Back));

        Assert.Empty(injected);
        Assert.True(release.Suppress); // still owed, and Windows never saw the press
        Assert.False(release.RequestReconcile);
        Assert.True(release.RequestMouseHookSync);
        Assert.Equal(0, h.Engine.OwedButtonReleases);
    }

    // The trigger the bindings own is kept, as round 6 had it: a release they swallowed asks for the repair (and for the
    // sync, since it paid a debt), and the repair, whose view of the chord's keys is whole then, leaves alone the Left
    // Ctrl the user still holds.
    [Fact]
    public void A_release_the_bindings_swallowed_still_asks_for_the_key_repair()
    {
        var windowsKeys = new HashSet<uint> { LeftCtrl };
        var injected = new List<uint>();
        using var h = Harness(Chord(LeftCtrl, Back), windowsKeys, injected);
        Play(h, h.Down(LeftCtrl));
        Assert.True(Play(h, h.ButtonDown(Back)).Suppress);

        var release = Play(h, h.ButtonUp(Back));

        Assert.True(release.Suppress);
        Assert.True(release.RequestReconcile);
        Assert.True(release.RequestMouseHookSync);
        Assert.Empty(injected);
    }

    // Defense in depth: whatever asks for it, the repair does not run while capture owns input, from Set's request until
    // the engine has applied capture's end, because capture tracks no key. A repair asked for just before Set was chosen
    // can otherwise run inside capture: its pass waits 25 ms for the input queue to settle. Once capture is over the
    // repair runs as before (the last step is the control), and a pass that only syncs the mouse hook repairs nothing.
    [Fact]
    public void The_key_repair_never_runs_while_capture_owns_input()
    {
        var injected = new List<uint>();
        using var h = Harness(HotkeyBinding.DefaultDictation, [PageDown], injected); // Windows holds a key the engine never saw

        h.Router.SetCaptureMode(true);
        h.Service.RunReconcilePassForTests(repairKeys: true); // requested, not yet applied
        h.Engine.OnWake();
        h.Service.RunReconcilePassForTests(repairKeys: true); // applied
        h.Router.SetCaptureMode(false);
        h.Service.RunReconcilePassForTests(repairKeys: true); // its end requested, the engine still capturing
        Assert.Empty(injected);

        h.Engine.OnWake();
        h.Service.RunReconcilePassForTests(repairKeys: false);
        Assert.Empty(injected);
        h.Service.RunReconcilePassForTests(repairKeys: true);
        Assert.Equal(new[] { PageDown }, injected);
    }

    // A10 (Grok's G6). In the shared test host other tests warm MouseHookFilter's initializer and the runtime's first-call
    // work for the P/Invokes, so the in-process measurement stayed green with either cost restored. Here the scenario runs
    // in a load context of its own, with fresh copies of this assembly and Scribe.Core from the output folder (everything
    // else, xunit included, comes from the default context), so what it measures is cold in every run, alone or not.
    // In the collection that runs alone (stream TR, item 1): no other test runs while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void The_cold_hook_callback_path_allocates_nothing_in_a_fresh_load()
        {
            var (result, _) = ColdPathMeasurement.RunFresh("mouse-cold-path", typeof(MouseColdPathScenario));

            ColdPathMeasurement.AssertAsExpected(
                [0, 0, 0, 0, 1, 1, 1, 1, 0, 0],
                result,
                ["MouseHookFilter's first read", "the fast path for a move", "an unbound key down and up",
                    "an owed release"],
                "mouse-cold-path",
                typeof(MouseColdPathScenario));
        }
    }

    // The signal's one word (A9): a sync-only request runs a pass that repairs nothing, a repair request one that repairs,
    // and a repair asked for is never lost to a sync-only signal that coalesced with it, whichever pass takes it. Since
    // round 10 the word is the key view epoch the repair was asked for in (A12), zero for none.
    [Fact]
    public void The_signal_tells_each_pass_whether_the_key_repair_was_asked_for()
    {
        using var passes = new BlockingCollection<long>();
        using var signal = new HotkeyReconcileSignal(passes.Add);

        signal.SignalMouseHookSync();
        Assert.True(passes.TryTake(out var syncOnly, Bound), "The sync-only signal ran no pass.");
        Assert.Equal(0, syncOnly);

        signal.Signal(7);
        Assert.True(passes.TryTake(out var repair, Bound), "The repair signal ran no pass.");
        Assert.Equal(7, repair);

        signal.SignalMouseHookSync();
        signal.Signal(8);
        long repairedAt = 0;
        while (repairedAt == 0 && passes.TryTake(out var pass, Bound))
        {
            repairedAt = pass;
        }

        Assert.Equal(8, repairedAt); // the repair asked for beside a sync-only signal is not lost
    }

    // The same split through MouseHookFilter and a real signal: after the last mouse binding went, an owed release asks for
    // a pass that only syncs the mouse hook, and a release the bindings swallowed (the control, a bound Back) for one that
    // repairs keys too, in the key view the release was judged in. Observed where the request is made, on this thread
    // (review round 3, item 6): the signal counts each request as it is asked, and the hold keeps its word for this test to
    // read, so nothing here waits for the pool to run a pass (the signal's own tests above cover that hop).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_mouse_filter_asks_for_the_key_repair_only_for_a_release_the_bindings_swallowed(bool stillBound)
    {
        using var h = new HotkeyEngineHarness(
            HotkeyCaptureSession.Build([Back], HotkeyMode.Hold), buttonDownInWindows: _ => false);
        using var hold = new ManualResetEventSlim(false);
        using var signal = new HotkeyReconcileSignal(_ => { }) { HoldBeforeTakingForTests = hold };
        var message = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MSLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(new NativeMethods.MSLLHOOKSTRUCT { mouseData = 1u << 16 }, message, fDeleteOld: false);
            Assert.True(MouseHookFilter.Swallows(0, MouseHookFilter.WM_XBUTTONDOWN, message, h.Engine, signal));
            if (!stillBound)
            {
                h.Router.UpdateBindings(HotkeyBinding.DefaultDictation, null); // drain-only from here
                h.Engine.OnWake();
            }

            Assert.Equal((0L, 0L), (signal.RepairRequests, signal.SyncRequestsForTests)); // the press asked for nothing
            Assert.True(MouseHookFilter.Swallows(0, MouseHookFilter.WM_XBUTTONUP, message, h.Engine, signal));

            Assert.Equal(stillBound ? (1L, 0L) : (0L, 1L), (signal.RepairRequests, signal.SyncRequestsForTests));
            Assert.Equal(stillBound ? h.Engine.KeyViewEpoch : 0, signal.PendingRepairAtForTests);
        }
        finally
        {
            hold.Set();
            Marshal.FreeHGlobal(message);
        }
    }

    // Marshal.Prelink replaces round 7's priming call: the service's constructor prelinks exactly the two P/Invokes the
    // hook callbacks call, before either hook exists, and the old first call to GetAsyncKeyState is gone. The cold
    // measurement above is what shows the prelink pays.
    [Fact]
    public void The_service_prelinks_the_two_native_calls_the_hook_callbacks_make()
    {
        Assert.Equal(new[] { "CallNextHookEx", "GetAsyncKeyState" }, NativeMethods.PrelinkHookCalls().Order().ToArray());

        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var constructor = typeof(HotkeyService).GetConstructors(Any)
            .Single(candidate => candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(HotkeyCommandRouter)));
        Assert.Contains(
            MouseButtonRound7Tests.Callees(constructor),
            callee => callee.DeclaringType == typeof(NativeMethods) && callee.Name == nameof(NativeMethods.PrelinkHookCalls));

        var createRouter = typeof(HotkeyService).GetMethod("CreateRouter", Any)!;
        Assert.DoesNotContain(MouseButtonRound7Tests.Callees(createRouter), callee => callee.Name == "Invoke");
    }

    private static HotkeyEngineHarness Harness(HotkeyBinding binding, HashSet<uint> windowsKeys, List<uint> injected) =>
        new(
            binding,
            buttonDownInWindows: _ => false,
            keyDownInWindows: windowsKeys.Contains,
            releaseLeakedKey: key =>
            {
                injected.Add(key);
                return true;
            });

    // What the service does with a decision, run at once on this thread: MouseHookFilter's signal, then the pass it starts,
    // which repairs keys only when the decision asks for that and otherwise only syncs the mouse hook.
    private static HookDecision Play(HotkeyEngineHarness h, HookDecision decision)
    {
        if (decision.RequestReconcile || decision.RequestMouseHookSync)
        {
            h.Service.RunReconcilePassForTests(repairKeys: decision.RequestReconcile);
        }

        return decision;
    }
}
