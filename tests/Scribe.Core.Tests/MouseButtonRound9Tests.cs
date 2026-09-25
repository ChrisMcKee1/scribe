using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 9, A11: the leaked-key repair is excluded from binding capture, not merely checked against it. Capture's
/// start and the repair's key-ups are serialized on one gate on the requesting side: a key-up is either sent before
/// capture is requested or not at all, capture's start waits for at most the one key-up already being sent (for a bounded
/// time), and capture stays in force until both machines have applied its end. Barriers in the scripted Windows view and
/// in the scripted key-up hold the pass where round 8's check could be overtaken.
/// </summary>
public sealed class MouseButtonRound9Tests
{
    private const uint Back = MouseButtons.Back;
    private const uint Forward = MouseButtons.Forward;
    private const uint LeftCtrl = 0xA2;
    private const uint RightCtrl = 0xA3;
    private const uint PageDown = 0x22;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static HotkeyBinding Chord(uint first, uint second) => HotkeyCaptureSession.Build([first, second], HotkeyMode.Hold);

    // A11, the start, as Astra wrote it: Left Ctrl and Back bound; the chord's release asks for a legitimate repair while
    // Ctrl is still held; the pass has made its first check and is about to read Windows' view when Set is chosen. At
    // 75a1dc8 capture was requested and applied in that gap, clearing the engine's view, and the pass then found Left
    // Ctrl down in Windows and not in the engine and sent a Ctrl-up during the capture. Now capture's start waits for
    // the gate the pass holds for that key, so the pass judges a view capture has not touched, and sends nothing.
    [Fact]
    public void Capture_chosen_while_a_repair_is_about_to_read_waits_for_it_and_nothing_is_sent()
    {
        var injected = new ConcurrentQueue<uint>();
        var events = new ConcurrentQueue<string>();
        using var atRead = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        using var h = PausedAtFirstRead(Chord(LeftCtrl, Back), null, [LeftCtrl], atRead, resume, injected, events);
        h.Service.CaptureAdmissionWaitForTests = Bound;
        h.Down(LeftCtrl);
        Assert.True(h.ButtonDown(Back).Suppress);
        Assert.True(h.ButtonUp(Back).RequestReconcile); // legitimate: a release the bindings swallowed

        var pass = StartPass(h);
        Thread? capture = null;
        try
        {
            Assert.True(atRead.Wait(Bound), "The pass never reached its first read.");
            capture = StartCapture(h, events);
            AwaitAdmissionUnderWay(h, capture);
            h.Engine.OnWake(); // the hook thread applies whatever capture has posted by now
        }
        finally
        {
            resume.Set();
        }

        Assert.True(pass.Join(Bound), "The pass never finished.");
        Assert.True(capture!.Join(Bound), "Capture's start never returned.");
        h.Engine.OnWake();

        Assert.Empty(injected);
        Assert.True(h.Router.CaptureOwnsInput);
    }

    // The same gap when capture's start stops waiting (its bound is exceeded, here made short): capture is requested and
    // applied under the paused pass, whose reads then see the cleared view. The pass checks again after its reads and
    // before the key-up, so what they decided is not sent.
    [Fact]
    public void Capture_that_stops_waiting_for_a_repair_still_gets_no_key_up_decided_on_its_view()
    {
        var injected = new ConcurrentQueue<uint>();
        var events = new ConcurrentQueue<string>();
        using var atRead = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        using var h = PausedAtFirstRead(Chord(LeftCtrl, Back), null, [LeftCtrl], atRead, resume, injected, events);
        h.Service.CaptureAdmissionWaitForTests = TimeSpan.FromMilliseconds(50);
        h.Down(LeftCtrl);
        Assert.True(h.ButtonDown(Back).Suppress);
        Assert.True(h.ButtonUp(Back).RequestReconcile);

        var pass = StartPass(h);
        try
        {
            Assert.True(atRead.Wait(Bound), "The pass never reached its first read.");
            var capture = StartCapture(h, events);
            Assert.True(capture.Join(Bound), "Capture's start waited past its bound.");
            h.Engine.OnWake(); // capture applied: the engine's view is cleared under the paused pass
        }
        finally
        {
            resume.Set();
        }

        Assert.True(pass.Join(Bound), "The pass never finished.");
        Assert.Empty(injected);
    }

    // A key-up already being sent when Set is chosen: capture's start waits for it, and the pass sends no other, although
    // Windows still holds a second leaked key. At 75a1dc8 capture started at once and the pass sent the second key-up
    // into the capture.
    [Fact]
    public void Capture_chosen_while_a_key_up_is_being_sent_waits_for_it_and_no_other_is_sent()
    {
        var injected = new ConcurrentQueue<uint>();
        var events = new ConcurrentQueue<string>();
        using var sending = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        using var h = PausedInFirstKeyUp(sending, resume, injected, events);
        h.Service.CaptureAdmissionWaitForTests = Bound;

        var pass = StartPass(h);
        Thread? capture = null;
        try
        {
            Assert.True(sending.Wait(Bound), "The pass never started sending.");
            capture = StartCapture(h, events);
            AwaitAdmissionUnderWay(h, capture);
            Assert.False(h.Service.KeyRepairAllowedForTests); // the pending start already stops the pass's next key
            h.Engine.OnWake();
        }
        finally
        {
            resume.Set();
        }

        Assert.True(pass.Join(Bound), "The pass never finished.");
        Assert.True(capture!.Join(Bound), "Capture's start never returned.");
        h.Engine.OnWake();

        Assert.Equal(new[] { LeftCtrl }, injected);
        Assert.Equal(new[] { "sent A2", "capture admitted" }, events);
    }

    // The bound: a key-up that outlasts capture's wait (here 50 ms) is the one that can still arrive after capture has
    // started, decided before it on a view capture had not touched; the pass sends no other.
    [Fact]
    public void Capture_stops_waiting_for_a_slow_key_up_after_its_bound_and_no_other_is_sent()
    {
        var injected = new ConcurrentQueue<uint>();
        var events = new ConcurrentQueue<string>();
        using var sending = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        using var h = PausedInFirstKeyUp(sending, resume, injected, events);
        h.Service.CaptureAdmissionWaitForTests = TimeSpan.FromMilliseconds(50);

        var pass = StartPass(h);
        try
        {
            Assert.True(sending.Wait(Bound), "The pass never started sending.");
            var capture = StartCapture(h, events);
            Assert.True(capture.Join(Bound), "Capture's start waited past its bound.");
            h.Engine.OnWake();
        }
        finally
        {
            resume.Set();
        }

        Assert.True(pass.Join(Bound), "The pass never finished.");

        Assert.Equal(new[] { LeftCtrl }, injected);
        Assert.Equal(new[] { "capture admitted", "sent A2" }, events);
    }

    // A11, the end: capture stays in force until both machines have applied its end. The seam pauses the end between the
    // standard machine and the dictation-only one and runs a repair there: at 75a1dc8 the engine had already published
    // that capture was over, so the pass judged the emptied view and released the Left Ctrl the user held for the capture.
    [Fact]
    public void Capture_is_in_force_until_both_machines_have_applied_its_end()
    {
        var injected = new ConcurrentQueue<uint>();
        using var h = Scripted(Chord(LeftCtrl, Back), Chord(RightCtrl, Forward), key => key == LeftCtrl, injected);
        h.Service.SetCaptureMode(true);
        h.Engine.OnWake();
        h.Service.SetCaptureMode(false);
        bool? ownsInputBetweenMachines = null;
        h.Engine.BetweenCaptureMachinesForTests = () =>
        {
            ownsInputBetweenMachines = h.Router.CaptureOwnsInput;
            h.Service.RunReconcilePassForTests(repairKeys: true);
        };

        h.Engine.OnWake();
        h.Engine.BetweenCaptureMachinesForTests = null;

        Assert.Empty(injected);
        Assert.True(ownsInputBetweenMachines);
        Assert.False(h.Router.CaptureOwnsInput); // both machines have applied it now
    }

    // The control: once capture has fully ended, a real leak (Windows holds a key the engine saw released, or never saw)
    // is repaired as before. Nothing ran by itself when capture ended: a pass capture stopped is not replayed.
    [Fact]
    public void A_repair_after_capture_has_fully_ended_still_releases_a_leaked_key()
    {
        var injected = new ConcurrentQueue<uint>();
        using var h = Scripted(HotkeyBinding.DefaultDictation, null, key => key == PageDown, injected);
        h.Service.SetCaptureMode(true);
        h.Engine.OnWake();
        h.Service.RunReconcilePassForTests(repairKeys: true);
        h.Service.SetCaptureMode(false);
        h.Service.RunReconcilePassForTests(repairKeys: true); // its end requested, not applied
        var passes = h.Service.ReconcilePassesRun;
        h.Engine.OnWake();

        Assert.Equal(passes, h.Service.ReconcilePassesRun);
        Assert.Empty(injected);

        h.Service.RunReconcilePassForTests(repairKeys: true);
        Assert.Equal(new[] { PageDown }, injected);
    }

    // The first check is what keeps the reads out of capture: a pass that starts while capture owns input reads nothing,
    // so it cannot carry a reading of the emptied view past capture's end to a key-up (the second check alone, made after
    // capture had ended, would let it through).
    [Fact]
    public void A_repair_that_starts_during_capture_reads_nothing()
    {
        var injected = new ConcurrentQueue<uint>();
        var events = new ConcurrentQueue<string>();
        using var atRead = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        using var h = PausedAtFirstRead(Chord(LeftCtrl, Back), null, [LeftCtrl], atRead, resume, injected, events);
        h.Service.SetCaptureMode(true);
        h.Engine.OnWake();

        var pass = StartPass(h);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => !pass.IsAlive || atRead.IsSet, Bound), "The pass neither ended nor read.");
            h.Service.SetCaptureMode(false);
            h.Engine.OnWake(); // capture fully ended while a pass that had read would be paused
        }
        finally
        {
            resume.Set();
        }

        Assert.True(pass.Join(Bound), "The pass never finished.");
        Assert.False(atRead.IsSet);
        Assert.Empty(injected);
    }

    // A replacement engine starts with the capture requests it was built from already applied, so a reinstall after a
    // capture does not leave the repair waiting for an end no engine will apply.
    [Fact]
    public void A_reinstall_after_capture_leaves_the_repair_working()
    {
        var injected = new ConcurrentQueue<uint>();
        using var h = Scripted(HotkeyBinding.DefaultDictation, null, key => key == PageDown, injected);
        h.Service.SetCaptureMode(true);
        h.Engine.OnWake();
        h.Service.SetCaptureMode(false);
        h.Engine.OnWake();

        _ = h.Router.BeginEngine(h.Transitions);

        Assert.False(h.Router.CaptureOwnsInput);
        h.Service.RunReconcilePassForTests(repairKeys: true);
        Assert.Equal(new[] { PageDown }, injected);
    }

    // Found while doing this: a pass that outlives the hooks (scheduled just before Stop) has no view of the keys to judge
    // by, and treated every key Windows holds as leaked. It releases nothing now.
    [Fact]
    public void A_repair_pass_with_no_hook_running_releases_nothing()
    {
        var injected = new ConcurrentQueue<uint>();
        using var service = new HotkeyService(
            NullLogger<HotkeyService>.Instance,
            new HotkeyCommandRouter(HotkeyBinding.DefaultDictation),
            () => true,
            key => key == PageDown,
            key =>
            {
                injected.Enqueue(key);
                return true;
            });

        service.RunReconcilePassForTests(repairKeys: true);

        Assert.Empty(injected);
    }

    // Requirement 2: the gate belongs to the requesting threads and the repair alone. Nothing the hook thread runs, in a
    // callback or between messages, calls into Monitor or System.Threading.Lock or touches the gate or the admission
    // count; so capture's start can wait behind a key-up in flight, and the key-up behind the hook chain, but no thread
    // can wait for the hook thread while the hook thread waits for it.
    [Fact]
    public void Nothing_the_hook_thread_runs_takes_a_lock_or_touches_the_repair_gate()
    {
        const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
        var gate = typeof(HotkeyService).GetField("_repairGate", Instance);
        var admissions = typeof(HotkeyService).GetField("_captureAdmissions", Instance);

        foreach (var method in HookThreadMethods())
        {
            foreach (var callee in MouseButtonRound7Tests.Callees(method))
            {
                Assert.False(
                    callee.DeclaringType == typeof(Monitor) || callee.DeclaringType == typeof(Lock) ||
                    callee.DeclaringType == typeof(Lock.Scope),
                    $"{method.DeclaringType!.Name}.{method.Name} calls {callee.DeclaringType!.Name}.{callee.Name}.");
            }

            foreach (var (code, token) in MouseButtonRound7Tests.Instructions(method))
            {
                if (code.OperandType == OperandType.InlineField)
                {
                    var field = method.Module.ResolveField(token);
                    Assert.False(
                        field == gate || field == admissions,
                        $"{method.DeclaringType!.Name}.{method.Name} touches {field!.Name}.");
                }
            }
        }
    }

    private static IEnumerable<MethodBase> HookThreadMethods()
    {
        const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var installation = typeof(HotkeyService).GetNestedType("HookInstallation", BindingFlags.NonPublic)!;
        foreach (var name in new[]
                 {
                     "HookCallback", "MouseHookCallback", "DesktopSwitchCallback", "Run", "SyncMouseHook", "RemoveMouseHook",
                 })
        {
            yield return installation.GetMethod(name, Declared)!;
        }

        foreach (var type in new[]
                 {
                     typeof(HotkeyEngine), typeof(ChordStateMachine), typeof(KeySet), typeof(MouseHookFilter),
                     typeof(HotkeyTriggerArbiter), typeof(HotkeyTransitionQueue),
                 })
        {
            foreach (var method in type.GetMethods(Declared))
            {
                yield return method;
            }
        }

        foreach (var name in new[] { nameof(HotkeyReconcileSignal.Signal), nameof(HotkeyReconcileSignal.SignalMouseHookSync), "Set" })
        {
            yield return typeof(HotkeyReconcileSignal).GetMethod(name, Declared)!;
        }
    }

    // A harness whose scripted Windows view pauses the pass at its first read, and whose key-ups are recorded.
    private static HotkeyEngineHarness PausedAtFirstRead(
        HotkeyBinding binding,
        HotkeyBinding? dictationOnly,
        HashSet<uint> windowsKeys,
        ManualResetEventSlim atRead,
        ManualResetEventSlim resume,
        ConcurrentQueue<uint> injected,
        ConcurrentQueue<string> events)
    {
        var paused = 0;
        return new HotkeyEngineHarness(
            binding,
            dictationOnly,
            buttonDownInWindows: _ => false,
            keyDownInWindows: key =>
            {
                if (Interlocked.Exchange(ref paused, 1) == 0)
                {
                    atRead.Set();
                    resume.Wait(Bound);
                }

                return windowsKeys.Contains(key);
            },
            releaseLeakedKey: key =>
            {
                injected.Enqueue(key);
                events.Enqueue($"sent {key:X2}");
                return true;
            });
    }

    // Left Ctrl and Right Ctrl, the chords' keys for each trigger, both left down in Windows after the engine saw them go
    // up: two genuine leaks. The first key-up (Left Ctrl, the standard binding's) pauses until resumed.
    private static HotkeyEngineHarness PausedInFirstKeyUp(
        ManualResetEventSlim sending, ManualResetEventSlim resume, ConcurrentQueue<uint> injected, ConcurrentQueue<string> events)
    {
        var paused = 0;
        var h = new HotkeyEngineHarness(
            Chord(LeftCtrl, Back),
            Chord(RightCtrl, Forward),
            buttonDownInWindows: _ => false,
            keyDownInWindows: key => key is LeftCtrl or RightCtrl,
            releaseLeakedKey: key =>
            {
                if (Interlocked.Exchange(ref paused, 1) == 0)
                {
                    sending.Set();
                    resume.Wait(Bound);
                }

                injected.Enqueue(key);
                events.Enqueue($"sent {key:X2}");
                return true;
            });
        h.Tap(LeftCtrl);
        h.Tap(RightCtrl);
        return h;
    }

    private static HotkeyEngineHarness Scripted(
        HotkeyBinding binding, HotkeyBinding? dictationOnly, Func<uint, bool> windowsHolds, ConcurrentQueue<uint> injected) =>
        new(
            binding,
            dictationOnly,
            buttonDownInWindows: _ => false,
            keyDownInWindows: windowsHolds,
            releaseLeakedKey: key =>
            {
                injected.Enqueue(key);
                return true;
            });

    // A repair pass on a thread of its own, so the test can act while a barrier holds it.
    private static Thread StartPass(HotkeyEngineHarness h)
    {
        var pass = new Thread(() => h.Service.RunReconcilePassForTests(repairKeys: true))
        {
            IsBackground = true,
            Name = "test-repair-pass",
        };
        pass.Start();
        return pass;
    }

    // Set chosen, on a thread of its own, as Settings' UI thread asks for it.
    private static Thread StartCapture(HotkeyEngineHarness h, ConcurrentQueue<string> events)
    {
        var capture = new Thread(() =>
        {
            h.Service.SetCaptureMode(true);
            events.Enqueue("capture admitted");
        })
        {
            IsBackground = true,
            Name = "test-capture-start",
        };
        capture.Start();
        return capture;
    }

    // Until capture's start has returned, or is being admitted and waits for the repair's gate.
    private static void AwaitAdmissionUnderWay(HotkeyEngineHarness h, Thread capture) =>
        Assert.True(
            SpinWait.SpinUntil(() => !capture.IsAlive || h.Service.CaptureAdmissionPendingForTests, Bound),
            "Capture's start never got under way.");
}
