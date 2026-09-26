using System.Collections.Concurrent;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// Review round 10, A12: every key-up the leaked-key repair sends is conditional on the engine's key view being the one it
/// judged. Each clear or replacement of the view takes a new epoch (<see cref="HotkeyEngine.KeyViewEpoch"/>); a repair
/// request carries the epoch its trigger was seen at, and the pass checks it before its first read and again immediately
/// before each key-up, stopping at the first change. Barriers hold the pass before its reads (inside the scripted Windows
/// view) or after them (<see cref="SuppressedKeyReconciler.AfterReadsForTests"/>) while the view is cleared under it.
/// </summary>
public sealed class MouseButtonRound10Tests
{
    private const uint Back = MouseButtons.Back;
    private const uint LeftCtrl = 0xA2;
    private const uint PageDown = 0x22;

    // A hang guard, never the verdict: every wait is for something certain to happen.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public static TheoryData<string> PausePoints => new() { "before its reads", "before its key-up" };

    private static HotkeyBinding Chord(uint first, uint second) => HotkeyCaptureSession.Build([first, second], HotkeyMode.Hold);

    // New bindings saved while a pass is held: both machines forget their keys, and the pass stops.
    [Theory]
    [MemberData(nameof(PausePoints))]
    public void New_bindings_while_a_pass_is_held_stop_it(string pausedAt)
    {
        using var run = new PausedPass(pausedAt);

        run.Start();
        try
        {
            run.AwaitPaused();
            run.H.Router.UpdateBindings(Chord(LeftCtrl, PageDown), null);
            run.H.Engine.OnWake();
        }
        finally
        {
            run.Resume();
        }

        run.Finish();
        Assert.Empty(run.Injected);
    }

    // The order inside each clear: the engine takes the view's new epoch before it clears the first key, so a pass whose
    // reads come once the keys are cleared (here run right after the clear, before the site returns) already sees the new
    // epoch and stops. Taken after the clear instead, the pass would judge the cleared view in the old epoch and release
    // the Left Ctrl the user holds.
    [Theory]
    [InlineData("a desktop reset")]
    [InlineData("new bindings")]
    [InlineData("capture's start")]
    public void A_pass_that_reads_a_cleared_view_already_sees_its_new_epoch(string clear)
    {
        var injected = new ConcurrentQueue<uint>();
        using var h = Scripted(Chord(LeftCtrl, Back), key => key == LeftCtrl, injected);
        h.Down(LeftCtrl); // held, and tracked by the engine
        var requestedAt = h.Engine.KeyViewEpoch;
        var ran = false;
        h.Engine.AfterKeyViewClearedForTests = () =>
        {
            ran = true;
            h.Service.RunReconcilePassForTests(requestedAt: requestedAt);
        };

        switch (clear)
        {
            case "a desktop reset":
                h.Engine.OnDesktopSwitch();
                break;
            case "new bindings":
                h.Router.UpdateBindings(Chord(LeftCtrl, PageDown), null);
                h.Engine.OnWake();
                break;
            default:
                h.Router.SetCaptureMode(true);
                h.Engine.OnWake();
                break;
        }

        h.Engine.AfterKeyViewClearedForTests = null;
        Assert.True(ran);
        Assert.Empty(injected);
        Assert.NotEqual(requestedAt, h.Engine.KeyViewEpoch);
    }

    // A12, as Astra wrote it: Ctrl and Back bound, Ctrl physically held, a legitimate repair past its first check and held
    // up; Set waits its 250 ms (here 50) and publishes capture anyway; the hook applies capture (the view cleared); the
    // user cancels and the end is applied. At ef97346 the pass then found Left Ctrl down in Windows and absent from the
    // cleared view, with no admission pending and the generations equal, and sent a Ctrl-up. Held after its reads instead,
    // the pass had decided on a whole view and still sent it after the view was replaced. Now the view's epoch has
    // changed by then, and the pass stops.
    [Theory]
    [MemberData(nameof(PausePoints))]
    public void Capture_started_and_ended_while_a_pass_is_held_stops_it(string pausedAt)
    {
        using var run = new PausedPass(pausedAt);
        run.H.Service.CaptureAdmissionWaitForTests = TimeSpan.FromMilliseconds(50);

        run.Start();
        try
        {
            run.AwaitPaused();
            var capture = new Thread(() => run.H.Service.SetCaptureMode(true)) { IsBackground = true };
            capture.Start();
            Assert.True(capture.Join(Bound), "Capture's start waited past its bound.");
            run.H.Engine.OnWake(); // capture applied: the view cleared under the held pass
            run.H.Service.SetCaptureMode(false);
            run.H.Engine.OnWake(); // the user cancelled: capture's end applied by both machines
            Assert.False(run.H.Router.CaptureOwnsInput);
        }
        finally
        {
            run.Resume();
        }

        run.Finish();
        Assert.Empty(run.Injected);
    }

    // The desktop reset Grok noted was not excluded the way capture is: the lock screen or a secure desktop taken while a
    // pass is held. The machines forget their keys and the pass stops.
    [Theory]
    [MemberData(nameof(PausePoints))]
    public void A_desktop_reset_while_a_pass_is_held_stops_it(string pausedAt)
    {
        using var run = new PausedPass(pausedAt);

        run.Start();
        try
        {
            run.AwaitPaused();
            run.H.Engine.OnDesktopSwitch();
        }
        finally
        {
            run.Resume();
        }

        run.Finish();
        Assert.Empty(run.Injected);
    }

    // The reinstall Grok noted: the watchdog replaces the hook thread and its engine, which starts with an empty view,
    // while a pass is held. The pass stops: the current engine's view is not the one it judged.
    [Theory]
    [MemberData(nameof(PausePoints))]
    public void A_reinstall_while_a_pass_is_held_stops_it(string pausedAt)
    {
        using var run = new PausedPass(pausedAt);

        run.Start();
        try
        {
            run.AwaitPaused();
            _ = run.H.Router.BeginEngine(run.H.Transitions);
        }
        finally
        {
            run.Resume();
        }

        run.Finish();
        Assert.Empty(run.Injected);
    }

    // The control: a legitimate repair whose view does not change still sends its key-up, held or not.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_repair_whose_view_does_not_change_still_releases_a_leaked_key(bool held)
    {
        using var run = new PausedPass("before its key-up");
        if (!held)
        {
            run.H.Service.ReconcilerForTests.AfterReadsForTests = null;
        }

        run.Start();
        if (held)
        {
            run.AwaitPaused();
        }

        run.Resume();
        run.Finish();
        Assert.Equal(new[] { LeftCtrl }, run.Injected);
    }

    // The user presses the key again after the pass's reads and before its last check: the view shows it held now, and
    // the key-up is not sent. Only a press between that check and the SendInput itself can still be released.
    [Fact]
    public void A_key_pressed_again_before_the_last_check_is_not_released()
    {
        using var run = new PausedPass("before its key-up");

        run.Start();
        try
        {
            run.AwaitPaused();
            run.H.Down(LeftCtrl);
        }
        finally
        {
            run.Resume();
        }

        run.Finish();
        Assert.Empty(run.Injected);
    }

    // A clear between the trigger and the pass (the pass waits 25 ms, and longer on a busy pool): the request carries the
    // epoch its trigger was seen at, so a pass that starts after the view was cleared judges nothing.
    [Fact]
    public void A_view_cleared_between_the_request_and_the_pass_stops_it()
    {
        var injected = new ConcurrentQueue<uint>();
        using var h = Scripted(Chord(LeftCtrl, Back), key => key == LeftCtrl, injected);
        h.Down(LeftCtrl);
        Assert.True(h.ButtonDown(Back).Suppress);
        Assert.True(h.ButtonUp(Back).RequestReconcile);
        var requestedAt = h.Engine.KeyViewEpoch;

        h.Engine.OnDesktopSwitch();
        h.Service.RunReconcilePassForTests(requestedAt: requestedAt);

        Assert.Empty(injected);
    }

    // The consumer's repair after a dictation's release carries the epoch the release was seen at, the same way: a clear
    // before the consumer gets to it stops the pass; with no clear the leaked key is released. The pass the consumer's step
    // asks for is queued by the harness and run here, on the test's thread, so no pool delay is waited on (review round 3,
    // item 6: the pool's run after its 25 ms settle, waited for with a 10 s bound, missed it in full runs under load).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_dictation_release_repairs_only_the_view_it_was_seen_in(bool clearedBeforeDispatch)
    {
        var injected = new ConcurrentQueue<uint>();
        using var h = Scripted(HotkeyBinding.DefaultDictation, key => key == PageDown, injected);
        Assert.True(h.Down(PageDown).Suppress);
        Assert.True(h.Up(PageDown).Suppress); // Windows still holds Page Down: a leak the hook saw released
        var seenAt = h.Engine.KeyViewEpoch;
        if (clearedBeforeDispatch)
        {
            h.Engine.OnDesktopSwitch();
            Assert.NotEqual(seenAt, h.Engine.KeyViewEpoch);
        }

        var release = Assert.Single(h.DispatchAll(), transition => transition.Transition == HotkeyTransition.Deactivated);
        Assert.True(release.AllowReconcile);
        Assert.Equal(seenAt, release.KeyViewEpoch);
        Assert.Equal([seenAt], h.RunReconcilePasses()); // the consumer's step asked for one pass, at the release's epoch

        Assert.Equal(clearedBeforeDispatch ? [] : new[] { PageDown }, injected);
        Assert.Empty(h.TakeReconcilePasses());
    }

    // No replay (A13): capture's end queues no transition that could ask the consumer for a repair, and asking for the end
    // schedules none, whatever repair capture stopped.
    [Fact]
    public void Capture_s_end_asks_for_no_repair()
    {
        var injected = new ConcurrentQueue<uint>();
        using var h = Scripted(Chord(LeftCtrl, Back), key => key == LeftCtrl, injected);
        h.Service.SetCaptureMode(true);
        h.Engine.OnWake();
        h.Service.RunReconcilePassForTests(repairKeys: true); // stopped: capture owns input
        h.TakeTransitions();
        var scheduled = h.Service.RepairPassesScheduledForTests;

        h.Service.SetCaptureMode(false);
        h.Engine.OnWake();

        Assert.DoesNotContain(h.TakeTransitions(), transition => transition.AllowReconcile);
        Assert.Equal(scheduled, h.Service.RepairPassesScheduledForTests);
        Assert.Empty(h.TakeReconcilePasses());
        Assert.Empty(injected);
    }

    private static HotkeyEngineHarness Scripted(
        HotkeyBinding binding, Func<uint, bool> windowsHolds, ConcurrentQueue<uint> injected) =>
        new(
            binding,
            buttonDownInWindows: _ => false,
            keyDownInWindows: windowsHolds,
            releaseLeakedKey: key =>
            {
                injected.Enqueue(key);
                return true;
            });

    // A repair pass for Ctrl and Back bound, with Windows holding Left Ctrl, held on a thread of its own either before its
    // reads (inside the scripted Windows view; Left Ctrl held and tracked, as in A12) or after them (Left Ctrl a genuine
    // leak the engine saw released, so the pass has decided to release it).
    private sealed class PausedPass : IDisposable
    {
        private readonly ManualResetEventSlim _paused = new(false);
        private readonly ManualResetEventSlim _resume = new(false);
        private int _pausedOnce;
        private Thread? _pass;

        public PausedPass(string pausedAt)
        {
            var beforeReads = pausedAt == "before its reads";
            H = new HotkeyEngineHarness(
                Chord(LeftCtrl, Back),
                buttonDownInWindows: _ => false,
                keyDownInWindows: key =>
                {
                    if (beforeReads)
                    {
                        Pause();
                    }

                    return key == LeftCtrl;
                },
                releaseLeakedKey: key =>
                {
                    Injected.Enqueue(key);
                    return true;
                });
            if (beforeReads)
            {
                H.Down(LeftCtrl);
                Assert.True(H.ButtonDown(Back).Suppress);
                Assert.True(H.ButtonUp(Back).RequestReconcile);
            }
            else
            {
                H.Tap(LeftCtrl);
                H.Service.ReconcilerForTests.AfterReadsForTests = _ => Pause();
            }
        }

        public HotkeyEngineHarness H { get; }

        public ConcurrentQueue<uint> Injected { get; } = new();

        public void Start()
        {
            _pass = new Thread(() => H.Service.RunReconcilePassForTests(repairKeys: true))
            {
                IsBackground = true,
                Name = "test-repair-pass",
            };
            _pass.Start();
        }

        public void AwaitPaused() => Assert.True(_paused.Wait(Bound), "The pass never reached its pause.");

        public void Resume() => _resume.Set();

        public void Finish() => Assert.True(_pass!.Join(Bound), "The pass never finished.");

        public void Dispose()
        {
            _resume.Set();
            _pass?.Join(Bound);
            H.Dispose();
            _paused.Dispose();
            _resume.Dispose();
        }

        private void Pause()
        {
            if (Interlocked.Exchange(ref _pausedOnce, 1) == 0)
            {
                _paused.Set();
                _resume.Wait(Bound);
            }
        }
    }
}
