using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Hotkeys;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// The real keyboard hook's moves ahead of another program's hook, and the probe it swallows (stream RD). Like the other
/// <c>Start_</c> tests these install a global hook, so the local desktop filter leaves them to CI and to a private desktop
/// nobody switches to. The ones that inject keys run only where the input desktop is a throwaway machine's (CI, or
/// SCRIBE_INPUT_INJECTION_TESTS=1), and a hook of the test's own swallows everything they inject before it can reach a
/// window. They measure on real Windows the rule the moves rest on: SetWindowsHookEx "always installs a hook procedure at
/// the beginning of a hook chain" (Hooks Overview), so a hook registered after Scribe's, as a Remote Desktop client
/// registers its own when its window comes to the front, sees every key first.
/// </summary>
public partial class HotkeyServiceTests
{
    private const uint EventSystemForeground = 0x0003;
    private const uint F20 = 0x83;
    private const uint F21 = 0x84;
    private const uint RightCtrl = 0xA3;

    [Fact]
    public void Start_moves_the_keyboard_hook_ahead_on_its_own_thread_and_keeps_its_engine()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.DefaultDictation, () => true);
        service.Start();
        var engine = service.CurrentEngineForTests;
        var epoch = engine!.KeyViewEpoch;
        var first = service.KeyboardHookHandle;
        Assert.NotEqual(0, first);

        MoveAhead(service, 1);

        // A registration of its own ahead of every hook registered before it, the same engine and key view, and the one it
        // replaced still registered, kept for an event already on its way to it.
        Assert.NotEqual(first, service.KeyboardHookHandle);
        Assert.Same(engine, service.CurrentEngineForTests);
        Assert.Equal(epoch, engine.KeyViewEpoch);
        Assert.False(engine.IsRetired);
        Assert.Equal(1, service.RetiredKeyboardHooks);

        service.ReleaseRetiredKeyboardHooksNow(force: true);
        Assert.True(SpinWait.SpinUntil(() => service.RetiredKeyboardHooks == 0, HookTimeout), "The replaced registration was never released.");
        Assert.Equal(0, service.RetiredKeyboardHooksFoundGone);

        // Released means unhooked: there is nothing left for the test to remove.
        Assert.False(UnhookWindowsHookEx(first), "The replaced registration was still registered after its release.");
    }

    [Fact]
    public void Start_keeps_a_replaced_registration_until_its_grace_is_over()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.DefaultDictation, () => true);
        service.Start();
        MoveAhead(service, 1);

        // A release asked for well inside the grace keeps it, and a second move, which the hook thread handles after that
        // release, keeps both: each replaced registration waits out its grace.
        service.ReleaseRetiredKeyboardHooksNow(force: false);
        MoveAhead(service, 2);
        Assert.Equal(2, service.RetiredKeyboardHooks);

        service.Stop();
    }

    [Fact]
    public void Start_never_releases_a_replaced_registration_inside_its_grace_however_fast_the_moves_come()
    {
        // Review round 2, item 3 (A3 = G2). Moves 300 ms apart: the fifth used to find every slot taken and unhook the oldest
        // registration at once, 1.2 s after it was replaced, so an event still inside a hook ahead of it reached none of
        // Scribe's. The clock is the test's (the grace is timed on the service's), and nothing in front schedules moves of
        // its own; each move is asked for directly.
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        using var service = ScriptedForegroundService(clock, () => 0);
        service.Start();
        for (var move = 1; move <= RetiredHookRegistrations.Capacity; move++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(move == 1 ? 0 : 300));
            MoveAhead(service, move);
        }

        var handle = service.KeyboardHookHandle;
        Assert.Equal(RetiredHookRegistrations.Capacity, service.RetiredKeyboardHooks);

        // The fifth, 1.2 s after the first: every kept registration is inside its grace, so it waits for the oldest's to end,
        // and a sixth asked for meanwhile waits with it (a retry the thread's own timer makes early changes nothing).
        clock.Advance(TimeSpan.FromMilliseconds(300));
        service.MoveKeyboardHookAheadNow();
        AwaitFifthMove(service);
        Assert.Equal(800, service.KeyboardMoveRetryDueMsForTests);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        service.MoveKeyboardHookAheadNow();
        Assert.True(
            SpinWait.SpinUntil(() => service.KeyboardHookMovesDeferredForSlots >= 2, HookTimeout),
            "The sixth move neither waited nor was made.");
        Assert.Equal(500, service.KeyboardMoveRetryDueMsForTests);
        Assert.Equal(RetiredHookRegistrations.Capacity, service.KeyboardHookMoves);
        Assert.Equal(handle, service.KeyboardHookHandle);
        Assert.Equal(0, service.RetiredKeyboardHooksReleased);

        // 2 s after the first was replaced: the retry releases it and makes one move for the two that waited.
        clock.Advance(TimeSpan.FromMilliseconds(500));
        service.RetryDeferredKeyboardMoveNowForTests();
        Assert.True(
            SpinWait.SpinUntil(() => service.KeyboardHookMoves == RetiredHookRegistrations.Capacity + 1, HookTimeout),
            "The move that waited was never made.");
        AwaitQuiet(service);
        Assert.Equal(RetiredHookRegistrations.Capacity + 1, service.KeyboardHookMoves);
        Assert.Equal(1, service.RetiredKeyboardHooksReleased);
        Assert.Equal(RetiredHookRegistrations.Capacity, service.RetiredKeyboardHooks);
        Assert.NotEqual(handle, service.KeyboardHookHandle);
        Assert.Equal(0, service.RetiredKeyboardHooksFoundGone);
    }

    [Fact]
    public void Start_makes_a_move_held_back_for_a_slot_by_its_own_timer_once_the_oldest_grace_ends()
    {
        // The same wait with no test seam: the hook thread's own timer (SetTimer, with no TimerProc, so WM_TIMER reaches its
        // message loop) retries the move until a slot is free.
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        using var service = ScriptedForegroundService(clock, () => 0);
        service.Start();
        for (var move = 1; move <= RetiredHookRegistrations.Capacity; move++)
        {
            MoveAhead(service, move);
        }

        clock.Advance(TimeSpan.FromMilliseconds(RetiredHookRegistrations.GraceMs - 1));
        service.MoveKeyboardHookAheadNow();
        AwaitFifthMove(service);
        Assert.Equal(1, service.KeyboardMoveRetryDueMsForTests);
        Assert.Equal(RetiredHookRegistrations.Capacity, service.KeyboardHookMoves);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(
            SpinWait.SpinUntil(() => service.KeyboardHookMoves == RetiredHookRegistrations.Capacity + 1, HookTimeout),
            "The thread's own timer never made the move that waited.");
        Assert.Equal(RetiredHookRegistrations.Capacity, service.RetiredKeyboardHooksReleased);
    }

    [Fact]
    public void Start_reinstalls_the_hook_when_a_replaced_registration_was_already_gone()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.DefaultDictation, () => true);
        service.Start();
        var engine = service.CurrentEngineForTests;
        var first = service.KeyboardHookHandle;
        MoveAhead(service, 1);

        // What a registration Windows removed for a missed deadline is to the release: one that is already gone.
        Assert.True(UnhookWindowsHookEx(first), $"Could not remove the registration (Win32 error {Marshal.GetLastWin32Error()}).");
        service.ReleaseRetiredKeyboardHooksNow(force: true);
        Assert.True(SpinWait.SpinUntil(() => service.RetiredKeyboardHooksFoundGone == 1, HookTimeout), "The loss was never counted.");

        // For a while no registration may have seen the keys: the watchdog starts the key state over, as for a hook that
        // stopped receiving events.
        service.MaintainKeyboardHookNow();
        Assert.NotSame(engine, service.CurrentEngineForTests);
        Assert.True(engine!.IsRetired);
        Assert.Equal(0, service.KeyboardHookMoves);
    }

    [Fact]
    public void Start_leaves_no_keyboard_registration_behind_when_it_stops()
    {
        // The thread releases every registration it made, current and replaced, before it ends. Windows also removes a
        // thread's hooks when the thread ends, so this would hold without that release (mutation M5 in the RD report); the
        // release stays because the hooks documentation asks an application to unhook what it installed.
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.DefaultDictation, () => true);
        service.Start();
        var handles = new List<nint> { service.KeyboardHookHandle };
        MoveAhead(service, 1);
        handles.Add(service.KeyboardHookHandle);
        MoveAhead(service, 2);
        handles.Add(service.KeyboardHookHandle);

        service.Stop();

        foreach (var handle in handles)
        {
            Assert.False(UnhookWindowsHookEx(handle), $"Registration 0x{handle:X} outlived Stop.");
        }
    }

    [Fact]
    public void Start_moves_the_hook_ahead_when_a_remote_client_comes_to_the_front()
    {
        // The foreground notice is raised the way any process can raise one (NotifyWinEvent), for the desktop window, which
        // the scripted process lookup names msrdc; the clock is the test's, so the move is due when the test says. Nothing
        // is in front when the hook installs, so the notice alone arms the move.
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        var remote = GetDesktopWindow();
        var inFront = (nint)0;
        var asked = new ConcurrentQueue<nint>();
        using var service = new HotkeyService(
            NullLogger<HotkeyService>.Instance,
            new HotkeyCommandRouter(HotkeyBinding.DefaultDictation),
            () => true,
            foregroundWindow: () => Volatile.Read(ref inFront),
            processNameOfWindow: window =>
            {
                asked.Enqueue(window);
                return window == remote ? "msrdc" : null;
            },
            time: clock);
        service.Start();
        Assert.True(service.ForegroundNoticesInstalled, "The hook thread is not told of foreground changes.");

        Volatile.Write(ref inFront, remote);
        NotifyWinEvent(EventSystemForeground, remote, ObjectIdWindow, ChildIdSelf);
        Assert.True(
            SpinWait.SpinUntil(() => clock.MadeTimer?.Due == KeyboardHookPrecedence.FirstMoveDelay, HookTimeout),
            "The notice never reached the pool side, or it scheduled no move.");
        Assert.Contains(remote, asked);
        Assert.Equal(0, service.KeyboardHookMoves);

        clock.Timer.Fire();
        Assert.True(SpinWait.SpinUntil(() => service.KeyboardHookMoves == 1, HookTimeout), "The hook thread never moved its hook.");
        clock.Timer.Fire();
        Assert.True(SpinWait.SpinUntil(() => service.KeyboardHookMoves == 2, HookTimeout), "The second move never came.");

        // The grace is timed on the service's clock, the test's: the second move comes exactly 2 s after the first, when the
        // registration the first replaced has been replaced for its whole grace, so it is released then, and one is kept.
        Assert.Equal(1, service.RetiredKeyboardHooks);
        Assert.Equal(1, service.RetiredKeyboardHooksReleased);
    }

    [Fact]
    public void Start_keeps_ahead_of_a_remote_client_already_in_front_when_its_hook_installs()
    {
        // No foreground notice at all: the client was in front before Scribe's hook installed (a start, or a reinstall).
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        var remote = GetDesktopWindow();
        using var service = new HotkeyService(
            NullLogger<HotkeyService>.Instance,
            new HotkeyCommandRouter(HotkeyBinding.DefaultDictation),
            () => true,
            foregroundWindow: () => remote,
            processNameOfWindow: window => window == remote ? "msrdc" : null,
            time: clock);
        service.Start();

        Assert.True(
            SpinWait.SpinUntil(() => clock.MadeTimer?.Due == KeyboardHookPrecedence.FirstMoveDelay, HookTimeout),
            "The window in front when the hook installed was never looked at.");
        clock.Timer.Fire();
        Assert.True(SpinWait.SpinUntil(() => service.KeyboardHookMoves == 1, HookTimeout), "The hook thread never moved its hook.");
    }

    [Fact]
    public void Start_waits_to_move_the_hook_while_a_swallowed_key_is_held_and_moves_once_it_is_released()
    {
        // A move while a swallowed key is held would put Scribe's hook ahead of one that may have forwarded the press into
        // a remote session, and Scribe would swallow the release it never sees (see KeyboardHookMoveSafetyTests).
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.Legacy, () => true);
        service.Start();
        var first = service.KeyboardHookHandle;
        var engine = service.CurrentEngineForTests!;

        // Played as the hook thread plays it, between its messages (this desktop has no input): Right Ctrl held, swallowed.
        Assert.True(engine.OnKeyEvent(RightCtrl, isDown: true).Suppress);

        service.MoveKeyboardHookAheadNow();
        Assert.True(
            SpinWait.SpinUntil(() => service.KeyboardHookMovesDeferred == 1, HookTimeout),
            "The move was made while a swallowed key was held.");
        Assert.Equal(first, service.KeyboardHookHandle);
        Assert.Equal(0, service.KeyboardHookMoves);
        Assert.Same(engine, service.CurrentEngineForTests);

        // The release, swallowed like its press, and the reconcile pass a swallowed release asks for: the move that waited
        // is made then, once.
        Assert.True(engine.OnKeyEvent(RightCtrl, isDown: false).Suppress);
        service.RunReconcilePassForTests(repairKeys: false);
        Assert.True(SpinWait.SpinUntil(() => service.KeyboardHookMoves == 1, HookTimeout), "The move that waited was never made.");
        Assert.NotEqual(first, service.KeyboardHookHandle);

        service.RunReconcilePassForTests(repairKeys: false);
        AwaitQuiet(service);
        Assert.Equal(1, service.KeyboardHookMoves);
        Assert.Equal(1, service.KeyboardHookMovesDeferred);
    }

    [Fact]
    public void Start_makes_no_move_from_a_reconcile_pass_when_no_move_waited()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.Legacy, () => true);
        service.Start();

        service.RunReconcilePassForTests(repairKeys: false);
        AwaitQuiet(service);

        Assert.Equal(0, service.KeyboardHookMoves);
        Assert.Equal(0, service.KeyboardHookMovesDeferred);
    }

    [Fact]
    public void Start_drops_a_move_that_waited_for_a_key_once_another_window_came_to_the_front()
    {
        // Review round 2, item 5 (A5). The move waits while the swallowed key is held; a local app comes to the front; the
        // key's release asks for the move again. Made then, it would reorder the system-wide chain with a local app in front
        // (possibly ahead of a local key remapper), for nothing. The window handles are the test's own, never real windows:
        // the notice is published the way the hook thread's WinEvent callback publishes one.
        var inFront = ScriptedRemoteWindow;
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        using var service = ScriptedForegroundService(clock, () => Volatile.Read(ref inFront));
        service.Start();
        Assert.True(
            SpinWait.SpinUntil(() => clock.MadeTimer?.Due == KeyboardHookPrecedence.FirstMoveDelay, HookTimeout),
            "The remote client in front when the hook installed scheduled no move.");
        var engine = service.CurrentEngineForTests!;
        Assert.True(engine.OnKeyEvent(RightCtrl, isDown: true).Suppress);

        clock.Timer.Fire();
        Assert.True(SpinWait.SpinUntil(() => service.KeyboardHookMovesDeferred == 1, HookTimeout), "The move did not wait for the key.");

        Volatile.Write(ref inFront, ScriptedLocalWindow);
        service.NoticeForegroundForTests(ScriptedLocalWindow);
        Assert.True(engine.OnKeyEvent(RightCtrl, isDown: false).Suppress);
        service.RunReconcilePassForTests(repairKeys: false);
        AwaitQuiet(service);

        Assert.Equal(0, service.KeyboardHookMoves);
        Assert.Equal(1, service.KeyboardHookMovesDropped);
    }

    [Fact]
    public void Start_drops_a_move_another_window_coming_to_the_front_overtook_before_the_hook_thread_took_it()
    {
        // Review round 2, item 5 (A5): the move is posted with the remote client in front, and a local app comes to the front
        // before the hook thread handles the message; the hook thread checks right before it registers.
        var inFront = ScriptedRemoteWindow;
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        using var atMove = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        using var service = ScriptedForegroundService(clock, () => Volatile.Read(ref inFront));
        service.BeforeKeyboardMoveForTests = () =>
        {
            atMove.Set();
            proceed.Wait(HookTimeout);
        };
        service.Start();
        Assert.True(
            SpinWait.SpinUntil(() => clock.MadeTimer?.Due == KeyboardHookPrecedence.FirstMoveDelay, HookTimeout),
            "The remote client in front when the hook installed scheduled no move.");

        clock.Timer.Fire();
        var took = atMove.Wait(HookTimeout);
        Volatile.Write(ref inFront, ScriptedLocalWindow);
        service.NoticeForegroundForTests(ScriptedLocalWindow);
        proceed.Set();
        Assert.True(took, "The hook thread never took the move.");
        AwaitQuiet(service);

        Assert.Equal(0, service.KeyboardHookMoves);
        Assert.Equal(1, service.KeyboardHookMovesDropped);
    }

    // The hook thread has taken a fifth move with every kept registration inside its grace: it must have waited, releasing
    // nothing and moving nothing (a failure names what it did instead).
    private static void AwaitFifthMove(HotkeyService service)
    {
        Assert.True(
            SpinWait.SpinUntil(
                () => service.KeyboardHookMovesDeferredForSlots >= 1 || service.KeyboardHookMoves > RetiredHookRegistrations.Capacity,
                HookTimeout),
            "The hook thread never took the fifth move.");
        Assert.True(
            service.RetiredKeyboardHooksReleased == 0 && service.KeyboardHookMoves == RetiredHookRegistrations.Capacity,
            $"The fifth move was made, releasing {service.RetiredKeyboardHooksReleased} registration(s) inside their grace.");
    }

    [Fact]
    public void Start_restores_a_lost_sequence_at_the_watchdog_s_upkeep_while_a_remote_client_stays_in_front()
    {
        // Review round 3, item 2 (A7): the watchdog retried only a move that waited, and never looked at what is in front
        // again, so a sequence lost while a remote client stayed in front was not restored until the foreground changed.
        // Here the first move's tick reads the window in front while R is losing activation and Windows has none, which ends
        // the sequence, and no notice follows (R regaining activation publishes no change). The watchdog's upkeep runs the
        // pool side's recovery poll, which finds R in front with nothing scheduled and starts the sequence over, on the
        // upkeep's own thread and publishing no notice (review round 4, item 1).
        var inFront = ScriptedRemoteWindow;
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        using var service = ScriptedForegroundService(clock, () => Volatile.Read(ref inFront));
        service.Start();
        Assert.True(
            SpinWait.SpinUntil(() => clock.MadeTimer?.Due == KeyboardHookPrecedence.FirstMoveDelay, HookTimeout),
            "The remote client in front when the hook installed scheduled no move.");

        Volatile.Write(ref inFront, 0);
        clock.Timer.Fire();
        Assert.Null(clock.Timer.Due);
        Volatile.Write(ref inFront, ScriptedRemoteWindow);

        var published = service.ForegroundNoticesPublishedForTests;
        service.MaintainKeyboardHookNow();
        Assert.Equal(published, service.ForegroundNoticesPublishedForTests);
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, clock.Timer.Due);
        clock.Timer.Fire();
        Assert.True(SpinWait.SpinUntil(() => service.KeyboardHookMoves == 1, HookTimeout), "The restored sequence moved nothing.");

        // Mid-sequence, the upkeep changes nothing: the next step stays as scheduled.
        Assert.Equal(KeyboardHookPrecedence.SecondMoveDelay, clock.Timer.Due);
        published = service.ForegroundNoticesPublishedForTests;
        service.MaintainKeyboardHookNow();
        Assert.Equal(published, service.ForegroundNoticesPublishedForTests);
        Assert.Equal(KeyboardHookPrecedence.SecondMoveDelay, clock.Timer.Due);
    }

    // Window handles of the test's own, never real windows, named by the scripted process lookup below.
    private const nint ScriptedRemoteWindow = 0x1111;
    private const nint ScriptedLocalWindow = 0x2222;

    [Fact]
    public void Start_keeps_a_sequence_a_real_notice_started_while_the_watchdog_s_upkeep_was_sampling_the_foreground()
    {
        // Review round 4, item 1 (A8), Astra's sequence: the upkeep found no step scheduled and sampled the local window in
        // front; before it went on, the remote client came to the front and its real notice started the sequence. The upkeep
        // then published its old sample as a newer notice, which cancelled the client's first move (and took the notice's
        // one window slot, which could hide the real notice from the pool altogether). The upkeep now publishes nothing, and
        // starts a sequence only if none was started and no notice was published while it looked. The notice is published as
        // the WinEvent callback publishes it, with no lock of the service's: the upkeep holds the service's lock throughout.
        var inFront = ScriptedLocalWindow;
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        using var sampled = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        Thread? upkeep = null;
        using var service = ScriptedForegroundService(clock, () => SampleHeldOnThread(ref inFront, upkeep, sampled, resume));
        service.Start();
        var (notify, published, decided) = service.ForegroundNoticeForTests!.Value;
        AwaitDecided(decided, published());
        upkeep = new Thread(service.MaintainKeyboardHookNow) { IsBackground = true, Name = "test-watchdog-upkeep" };
        upkeep.Start(); // assigned first, so the scripted foreground knows the upkeep's thread
        Assert.True(sampled.Wait(HookTimeout), "The upkeep never sampled the window in front.");

        Volatile.Write(ref inFront, ScriptedRemoteWindow);
        var before = published();
        notify(ScriptedRemoteWindow); // the client's real WinEvent
        Assert.True(
            SpinWait.SpinUntil(() => clock.Timer.Due == KeyboardHookPrecedence.FirstMoveDelay, HookTimeout),
            "The client's notice scheduled no move.");

        resume.Set();
        Assert.True(upkeep.Join(HookTimeout), "The upkeep never finished.");
        Assert.Equal(before + 1, published()); // the client's notice, and nothing of the upkeep's
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, clock.Timer.Due);
        clock.Timer.Fire();
        Assert.True(SpinWait.SpinUntil(() => service.KeyboardHookMoves == 1, HookTimeout), "The client's first move was not made.");
    }

    [Fact]
    public void Start_starts_nothing_for_a_client_the_upkeep_sampled_when_another_window_s_notice_came_while_it_looked()
    {
        // Review round 4, item 1 (A8): the upkeep sampled the remote client while its sequence was lost, and before it went on
        // a local window came to the front and its real notice was decided. The upkeep's sample is older than that notice: it
        // must start nothing. It used to publish the client as a newer notice, which scheduled a first move with the local
        // window in front.
        var inFront = ScriptedRemoteWindow;
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        using var sampled = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        Thread? upkeep = null;
        using var service = ScriptedForegroundService(clock, () => SampleHeldOnThread(ref inFront, upkeep, sampled, resume));
        service.Start();
        var (notify, published, decided) = service.ForegroundNoticeForTests!.Value;
        LoseTheSequence(clock, ref inFront);
        upkeep = new Thread(service.MaintainKeyboardHookNow) { IsBackground = true, Name = "test-watchdog-upkeep" };
        upkeep.Start(); // assigned first, so the scripted foreground knows the upkeep's thread
        Assert.True(sampled.Wait(HookTimeout), "The upkeep never sampled the window in front.");

        Volatile.Write(ref inFront, ScriptedLocalWindow);
        var before = published();
        notify(ScriptedLocalWindow); // the local window's real WinEvent, decided on the pool
        AwaitDecided(decided, before + 1);

        resume.Set();
        Assert.True(upkeep.Join(HookTimeout), "The upkeep never finished.");
        Assert.Equal(before + 1, published());
        Assert.Null(clock.Timer.Due);
    }

    [Fact]
    public void Start_makes_a_move_judged_on_the_newest_revision_whatever_the_upkeep_sampled_meanwhile()
    {
        // Review round 4, item 1 (A8): the upkeep sampled the remote client while its sequence was lost; meanwhile the client's
        // own notice started it again and its first move was posted, judged on that notice's revision. The upkeep's
        // publication of its sample then advanced the revision, and the hook thread dropped the move as judged on an older
        // foreground. The upkeep now publishes nothing, so the move is made.
        var inFront = ScriptedRemoteWindow;
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        using var sampled = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        using var atMove = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        Thread? upkeep = null;
        using var service = ScriptedForegroundService(clock, () => SampleHeldOnThread(ref inFront, upkeep, sampled, resume));
        service.Start();
        var (notify, published, _) = service.ForegroundNoticeForTests!.Value;
        LoseTheSequence(clock, ref inFront);
        upkeep = new Thread(service.MaintainKeyboardHookNow) { IsBackground = true, Name = "test-watchdog-upkeep" };
        upkeep.Start(); // assigned first, so the scripted foreground knows the upkeep's thread
        Assert.True(sampled.Wait(HookTimeout), "The upkeep never sampled the window in front.");

        var before = published();
        notify(ScriptedRemoteWindow); // the client's own notice starts the sequence again
        Assert.True(
            SpinWait.SpinUntil(() => clock.Timer.Due == KeyboardHookPrecedence.FirstMoveDelay, HookTimeout),
            "The client's notice scheduled no move.");
        service.BeforeKeyboardMoveForTests = () =>
        {
            atMove.Set();
            proceed.Wait(HookTimeout);
        };
        clock.Timer.Fire(); // the first move, posted with the notice's revision
        Assert.True(atMove.Wait(HookTimeout), "The hook thread never took the move.");

        resume.Set();
        Assert.True(upkeep.Join(HookTimeout), "The upkeep never finished.");
        proceed.Set();
        AwaitQuiet(service);

        Assert.Equal(before + 1, published());
        Assert.Equal(0, service.KeyboardHookMovesDropped);
        Assert.Equal(1, service.KeyboardHookMoves);
    }

    // The remote client in front when the hook installed gets its first move scheduled; that move's tick then reads the
    // window in front while the client is losing activation and Windows has none, which ends the sequence, and the client is
    // in front again with no notice after it: the state the watchdog's upkeep recovers from.
    private static void LoseTheSequence(KeyboardHookPrecedenceTests.OneTimerClock clock, ref nint inFront)
    {
        Assert.True(
            SpinWait.SpinUntil(() => clock.MadeTimer?.Due == KeyboardHookPrecedence.FirstMoveDelay, HookTimeout),
            "The remote client in front when the hook installed scheduled no move.");
        Volatile.Write(ref inFront, 0);
        clock.Timer.Fire();
        Assert.Null(clock.Timer.Due);
        Volatile.Write(ref inFront, ScriptedRemoteWindow);
    }

    // The scripted window in front, read as Windows would answer; on the upkeep's own thread, the first read is held after it
    // is taken, as a sample the upkeep has made and not yet acted on.
    private static nint SampleHeldOnThread(
        ref nint inFront, Thread? upkeep, ManualResetEventSlim sampled, ManualResetEventSlim resume)
    {
        var window = Volatile.Read(ref inFront);
        if (upkeep is not null && ReferenceEquals(Thread.CurrentThread, upkeep) && !sampled.IsSet)
        {
            sampled.Set();
            resume.Wait(HookTimeout);
        }

        return window;
    }

    // The pool side has decided the notice published with the given revision (a notice for a local window schedules
    // nothing, so the wait is on the decision itself).
    private static void AwaitDecided(Func<long> decided, long revision) =>
        Assert.True(
            SpinWait.SpinUntil(() => decided() >= revision, HookTimeout),
            $"The pool side never decided revision {revision} (decided: {decided()}).");

    [Fact]
    public void Start_drops_a_move_when_another_window_came_to_the_front_before_its_notice_reached_the_hook_thread()
    {
        // The WinEvent for a foreground change reaches the hook thread only when it next takes its messages, and a move
        // already in its queue can be judged first: the window check covers that. Here no notice is published at all.
        var inFront = ScriptedRemoteWindow;
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        using var atMove = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        using var service = ScriptedForegroundService(clock, () => Volatile.Read(ref inFront));
        service.BeforeKeyboardMoveForTests = () =>
        {
            atMove.Set();
            proceed.Wait(HookTimeout);
        };
        service.Start();
        Assert.True(
            SpinWait.SpinUntil(() => clock.MadeTimer?.Due == KeyboardHookPrecedence.FirstMoveDelay, HookTimeout),
            "The remote client in front when the hook installed scheduled no move.");

        clock.Timer.Fire();
        var took = atMove.Wait(HookTimeout);
        Volatile.Write(ref inFront, ScriptedLocalWindow);
        proceed.Set();
        Assert.True(took, "The hook thread never took the move.");
        AwaitQuiet(service);

        Assert.Equal(0, service.KeyboardHookMoves);
        Assert.Equal(1, service.KeyboardHookMovesDropped);
    }

    [Fact]
    public void Start_drops_a_move_whose_foreground_changed_under_it_even_with_the_same_window_back_in_front()
    {
        // The client left the front and came back before the hook thread took the move: it may register its hook again on
        // its return, whose own sequence moves after it, so this move, judged on the older foreground, is not made.
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        using var atMove = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        using var service = ScriptedForegroundService(clock, () => ScriptedRemoteWindow);
        service.BeforeKeyboardMoveForTests = () =>
        {
            atMove.Set();
            proceed.Wait(HookTimeout);
        };
        service.Start();
        Assert.True(
            SpinWait.SpinUntil(() => clock.MadeTimer?.Due == KeyboardHookPrecedence.FirstMoveDelay, HookTimeout),
            "The remote client in front when the hook installed scheduled no move.");

        clock.Timer.Fire();
        var took = atMove.Wait(HookTimeout);
        service.NoticeForegroundForTests(ScriptedLocalWindow);
        service.NoticeForegroundForTests(ScriptedRemoteWindow);
        proceed.Set();
        Assert.True(took, "The hook thread never took the move.");
        AwaitQuiet(service);

        Assert.Equal(0, service.KeyboardHookMoves);
        Assert.Equal(1, service.KeyboardHookMovesDropped);
    }

    // A service over the legacy binding whose window in front, and the process of each window, the test scripts, on a clock
    // the test owns.
    private static HotkeyService ScriptedForegroundService(KeyboardHookPrecedenceTests.OneTimerClock clock, Func<nint> inFront) =>
        new(
            NullLogger<HotkeyService>.Instance,
            new HotkeyCommandRouter(HotkeyBinding.Legacy),
            () => true,
            foregroundWindow: inFront,
            processNameOfWindow: window => window switch
            {
                ScriptedRemoteWindow => "msrdc",
                ScriptedLocalWindow => "notepad",
                _ => null,
            },
            time: clock);

    [Fact]
    public void Start_judges_a_key_through_a_replaced_registration_without_swallowing_it()
    {
        // Each registration's own delegate, called as Windows calls it (this desktop has no input, so the test plays the
        // hook thread between its messages). CallNextHookEx outside a hook returns 0, so a call that passes an event on
        // returns 0, and one that swallows it returns 1.
        var events = new ConcurrentQueue<string>();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.Legacy, () => true);
        service.Activated += (_, _) => events.Enqueue("start");
        service.Deactivated += (_, _) => events.Enqueue("stop");
        service.Start();
        MoveAhead(service, 1);
        var (current, replaced) = service.KeyboardProcsForTests!.Value;
        Assert.NotNull(replaced);
        var engine = service.CurrentEngineForTests!;

        // Right Ctrl's press through the replaced registration, as if it entered the chain before the move: judged, so the
        // dictation starts, and passed on, with nothing left swallowed.
        Assert.Equal(0, Call(replaced!, NativeMethods.WM_KEYDOWN, RightCtrl, time: 1, flags: 0x01));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 1, HookTimeout), "The press was not judged.");
        Assert.False(engine.HoldsSwallowedKey);

        // Its release through the current registration is passed on too, and ends the dictation.
        Assert.Equal(0, Call(current, NativeMethods.WM_KEYUP, RightCtrl, time: 2, flags: 0x81));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout), "The release ended nothing.");
        Assert.Equal(["start", "stop"], events.ToArray());

        // The control: a new press through the current registration is swallowed, as ever.
        Assert.Equal(1, Call(current, NativeMethods.WM_KEYDOWN, RightCtrl, time: 3, flags: 0x01));
        Assert.True(engine.HoldsSwallowedKey);
        Assert.Equal(1, Call(current, NativeMethods.WM_KEYUP, RightCtrl, time: 4, flags: 0x81));
    }

    // One call of a registration's delegate for a key event, with a KBDLLHOOKSTRUCT of the test's own.
    private static nint Call(NativeMethods.LowLevelKeyboardProc proc, int message, uint key, uint time, uint flags)
    {
        using var hook = new KeyboardHookFilterTests.KeyMessage(
            new NativeMethods.KBDLLHOOKSTRUCT { vkCode = key, scanCode = 0x1D, flags = flags, time = time });
        return proc(0, message, hook.Pointer);
    }

    [Fact]
    public void Start_lets_a_key_nobody_saw_go_down_through_with_its_whole_keystroke_right_after_a_move()
    {
        // Review round 2, item 1. The registration's own delegate, called as Windows calls it (this desktop has no input),
        // with the time stamps Windows would give: 0 is passed on, 1 swallowed. Right Ctrl was held before the move and a
        // hook ahead of Scribe's kept its press, so its next repeat, after the move, is the first of it Scribe sees.
        var events = new ConcurrentQueue<string>();
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.Legacy, () => true);
        service.Activated += (_, _) => events.Enqueue("start");
        service.Deactivated += (_, _) => events.Enqueue("stop");
        service.Start();
        MoveAhead(service, 1);
        var engine = service.CurrentEngineForTests!;
        var moved = engine.AheadSince;
        var window = engine.UncertaintyWindowMs;
        Assert.InRange(window, KeyRepeatTiming.UncertaintyWindowMs(0, 31), KeyRepeatTiming.WorstCaseWindowMs);
        var current = service.KeyboardProcsForTests!.Value.Current;

        Assert.Equal(0, Call(current, NativeMethods.WM_KEYDOWN, RightCtrl, time: moved + 1, flags: 0x01));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 1, HookTimeout), "The uncertain press started nothing.");
        Assert.Equal(0, Call(current, NativeMethods.WM_KEYDOWN, RightCtrl, time: moved + 31, flags: 0x01));
        Assert.Equal(0, Call(current, NativeMethods.WM_KEYUP, RightCtrl, time: moved + 61, flags: 0x81));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout), "The release ended nothing.");

        // Its release was seen, so the next press is swallowed, as ever.
        Assert.Equal(1, Call(current, NativeMethods.WM_KEYDOWN, RightCtrl, time: moved + 91, flags: 0x01));
        Assert.Equal(1, Call(current, NativeMethods.WM_KEYUP, RightCtrl, time: moved + 121, flags: 0x81));

        // A second move opens a new window, and a first press once it is over is an ordinary one.
        MoveAhead(service, 2);
        var movedAgain = engine.AheadSince;
        current = service.KeyboardProcsForTests!.Value.Current;
        Assert.Equal(1, Call(current, NativeMethods.WM_KEYDOWN, RightCtrl, time: movedAgain + (uint)window, flags: 0x01));
        Assert.Equal(1, Call(current, NativeMethods.WM_KEYUP, RightCtrl, time: movedAgain + (uint)window + 30, flags: 0x81));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 6, HookTimeout), "A press or release was not judged.");
        Assert.Equal(["start", "stop", "start", "stop", "start", "stop"], events.ToArray());
        Assert.Equal(1, engine.UncertainPresses);
    }

    [Fact]
    public void Start_opens_the_window_after_a_reinstall_but_not_at_its_first_install()
    {
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.Legacy, () => true);
        service.Start();
        var first = service.CurrentEngineForTests!;
        var current = service.KeyboardProcsForTests!.Value.Current;

        // The first install replaced no registration of Scribe's: even a press stamped at the start of time is ordinary.
        Assert.Equal(1, Call(current, NativeMethods.WM_KEYDOWN, RightCtrl, time: 1, flags: 0x01));
        Assert.Equal(1, Call(current, NativeMethods.WM_KEYUP, RightCtrl, time: 2, flags: 0x81));
        Assert.Equal(0, first.UncertainPresses);

        // A reinstall is the watchdog's answer to a hook ahead of Scribe's that keeps the keys: the new registration is
        // the newest, and a key held across it may have had its press kept.
        service.ReinstallHookNow();
        var engine = service.CurrentEngineForTests!;
        Assert.NotSame(first, engine);
        current = service.KeyboardProcsForTests!.Value.Current;
        Assert.Equal(0, Call(current, NativeMethods.WM_KEYDOWN, RightCtrl, time: engine.AheadSince + 1, flags: 0x01));
        Assert.Equal(0, Call(current, NativeMethods.WM_KEYUP, RightCtrl, time: engine.AheadSince + 30, flags: 0x81));
        Assert.Equal(1, engine.UncertainPresses);
    }

    [Fact]
    public void Start_does_not_move_the_hook_for_any_other_window_in_front()
    {
        var clock = new KeyboardHookPrecedenceTests.OneTimerClock();
        var local = GetDesktopWindow();
        var asked = new ConcurrentQueue<nint>();
        using var service = new HotkeyService(
            NullLogger<HotkeyService>.Instance,
            new HotkeyCommandRouter(HotkeyBinding.DefaultDictation),
            () => true,
            foregroundWindow: () => local,
            processNameOfWindow: window =>
            {
                asked.Enqueue(window);
                return window == local ? "notepad" : null;
            },
            time: clock);
        service.Start();

        NotifyWinEvent(EventSystemForeground, local, ObjectIdWindow, ChildIdSelf);
        Assert.True(SpinWait.SpinUntil(() => asked.Contains(local), HookTimeout), "The notice never reached the pool side.");

        Assert.Null(clock.Timer.Due);
        Assert.Equal(0, service.KeyboardHookMoves);
    }

    [Fact]
    public void Start_sees_keys_first_again_once_moved_ahead_of_a_hook_that_keeps_them()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        var events = new ConcurrentQueue<string>();
        using var service = new HotkeyService(
            NullLogger<HotkeyService>.Instance, HotkeyCaptureSession.Build([F21], HotkeyMode.Hold), () => true);
        service.Activated += (_, _) => events.Enqueue("start");
        service.Deactivated += (_, _) => events.Enqueue("stop");
        service.Start();

        // Registered after Scribe's, as a Remote Desktop client registers its own when its window is activated, and kept
        // every key it is given, as the client keeps the keys it forwards: it is called first, and Scribe sees nothing.
        using var client = new InjectedKeyboardHook(swallow: true);
        Inject(Key(F21, up: false), Key(F21, up: true));
        Assert.True(client.WaitFor(2), "The hook ahead of Scribe's never saw the keys.");
        Assert.Empty(events);

        // Moved ahead, Scribe's is called first: the bound key starts and stops a dictation, and never reaches the client.
        // Pressed once the uncertainty window after the move is over (review round 2, item 1): the times are the test's,
        // which KEYBDINPUT.time lets it choose, so no clock is waited on.
        MoveAhead(service, 1);
        var afterWindow = service.CurrentEngineForTests!.AheadSince + (uint)service.CurrentEngineForTests!.UncertaintyWindowMs;
        Inject(KeyAt(F21, up: false, afterWindow));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 1, HookTimeout), "The bound key started nothing once moved ahead.");
        Inject(KeyAt(F21, up: true, afterWindow + 30));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout), "The bound key's release ended nothing.");
        Assert.Equal(["start", "stop"], events.ToArray());
        Assert.Equal(2, client.Seen.Length);
    }

    [Fact]
    public void Start_lets_the_rest_of_a_keystroke_a_hook_ahead_of_it_kept_through_after_a_move()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        // Review round 2, item 1 (Astra's A1, Grok's G1). The chain, newest first, once the move is made: Scribe's new
        // registration, a hook that forwards every key into a remote session and keeps it, as a Remote Desktop client's can,
        // the registration the move replaced, and the test's guard, which swallows whatever reaches it.
        var events = new ConcurrentQueue<string>();
        using var guard = new InjectedKeyboardHook(swallow: true);
        using var service = new HotkeyService(
            NullLogger<HotkeyService>.Instance, HotkeyCaptureSession.Build([F21], HotkeyMode.Hold), () => true);
        service.Activated += (_, _) => events.Enqueue("start");
        service.Deactivated += (_, _) => events.Enqueue("stop");
        service.Start();
        using var client = new InjectedKeyboardHook(swallow: true);

        // The bound key goes down while the client is first: it forwards the press and keeps it, and Scribe sees nothing.
        Inject(Key(F21, up: false));
        Assert.True(client.WaitFor(1), "The hook ahead of Scribe's never saw the press.");
        var engine = service.CurrentEngineForTests!;
        Assert.False(engine.IsPressed(F21));

        // Moved ahead while the key is still held. Its repeats reach Scribe first now, and its release after them: every one
        // goes on to the client, which forwarded the press, so the remote session gets the whole keystroke.
        MoveAhead(service, 1);
        var moved = engine.AheadSince;
        Inject(KeyAt(F21, up: false, moved + 1));
        Inject(KeyAt(F21, up: false, moved + 31));
        Inject(KeyAt(F21, up: true, moved + 61));
        Assert.True(client.WaitFor(4), "Scribe kept part of a keystroke the client forwarded.");
        Assert.Equal([(F21, false, false), (F21, false, false), (F21, false, false), (F21, true, false)], client.Seen);
        Assert.Equal([moved + 1, moved + 31, moved + 61], client.Times[1..]);

        // Push-to-talk still worked: the repeat started the dictation and the release ended it.
        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout), "The keystroke did not dictate.");

        // The next press after that release, and a first press once a later move's window is over, are swallowed as ever.
        // An unbound key sent after each shows the swallowed ones never reached the client.
        Inject(KeyAt(F21, up: false, moved + 91), KeyAt(F21, up: true, moved + 121), KeyAt(F20, up: false, moved + 151), KeyAt(F20, up: true, moved + 181));
        Assert.True(client.WaitFor(6), "The unbound key never came through.");
        MoveAhead(service, 2);
        var afterWindow = engine.AheadSince + (uint)engine.UncertaintyWindowMs;
        Inject(KeyAt(F21, up: false, afterWindow), KeyAt(F21, up: true, afterWindow + 30), KeyAt(F20, up: false, afterWindow + 60), KeyAt(F20, up: true, afterWindow + 90));
        Assert.True(client.WaitFor(8), "The second unbound key never came through.");
        Assert.Equal(
            [(F21, false, false), (F21, false, false), (F21, false, false), (F21, true, false),
             (F20, false, false), (F20, true, false), (F20, false, false), (F20, true, false)],
            client.Seen);
        Assert.True(SpinWait.SpinUntil(() => events.Count == 6, HookTimeout), "A swallowed press did not dictate.");
        Assert.Equal(2, engine.UncertainPresses); // the held key's first repeat, and F20's first press inside the window
    }

    // A test key with the time stamp the test chooses (KEYBDINPUT.time: "If this parameter is zero, the system will provide
    // its own time stamp"), which a low-level hook receives as KBDLLHOOKSTRUCT.time.
    private static NativeMethods.INPUT KeyAt(uint virtualKey, bool up, uint time)
    {
        var input = Key(virtualKey, up);
        input.U.ki.time = time;
        return input;
    }

    [Fact]
    public void Start_decides_each_key_once_while_a_replaced_registration_is_kept()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        // The chain, newest first: Scribe's new registration, another program's hook that passes every key on, the
        // registration the move replaced, and the test's guard, which swallows whatever reaches it.
        using var guard = new InjectedKeyboardHook(swallow: true);
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.DefaultDictation, () => true);
        service.Start();
        using var other = new InjectedKeyboardHook(swallow: false);
        MoveAhead(service, 1);

        Inject(Key(F20, up: false), Key(F20, up: true));

        // Each event went down the whole chain once, and came back through the replaced registration as an echo, which
        // passed it on untouched rather than deciding it again.
        Assert.True(guard.WaitFor(2), "The keys never reached the end of the chain.");
        Assert.Equal([(F20, false, false), (F20, true, false)], other.Seen);
        Assert.Equal([(F20, false, false), (F20, true, false)], guard.Seen);
        Assert.Equal(2, service.KeyEventEchoes);

        // Once released, the replaced registration is out of the chain: no more echoes.
        service.ReleaseRetiredKeyboardHooksNow(force: true);
        Assert.True(SpinWait.SpinUntil(() => service.RetiredKeyboardHooks == 0, HookTimeout));
        Inject(Key(F20, up: false), Key(F20, up: true));
        Assert.True(guard.WaitFor(4));
        Assert.Equal(2, service.KeyEventEchoes);
    }

    [Fact]
    public void Start_swallows_its_own_probe_and_passes_everything_else()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        using var guard = new InjectedKeyboardHook(swallow: true);
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance, HotkeyBinding.DefaultDictation, () => true);
        service.Start();

        // The watchdog's probe, then Scribe's own marked F20, the probe key from anyone else, and a test key to wait for:
        // Windows passes injected events through the hooks one at a time, in order.
        Assert.True(NativeMethods.SendMarkedKeyEvent(NativeMethods.VK_PROBE, keyUp: true), "The probe was not sent.");
        Assert.True(NativeMethods.SendMarkedKeyEvent((ushort)F20, keyUp: false));
        Assert.True(NativeMethods.SendMarkedKeyEvent((ushort)F20, keyUp: true));
        Inject(Key(NativeMethods.VK_PROBE, up: true), Key(F21, up: false), Key(F21, up: true));

        Assert.True(guard.WaitFor(5), "The events Scribe passes on never reached the end of the chain.");
        Assert.Equal(
            [(F20, false, true), (F20, true, true), (NativeMethods.VK_PROBE, true, false), (F21, false, false), (F21, true, false)],
            guard.Seen);
    }

    // One move ahead, asked of the hook thread, and the wait for it: everything queued before it has been handled by then.
    private static void MoveAhead(HotkeyService service, long moves)
    {
        service.MoveKeyboardHookAheadNow();
        Assert.True(
            SpinWait.SpinUntil(() => service.KeyboardHookMoves >= moves, HookTimeout),
            "The hook thread never moved its keyboard hook ahead.");
    }

    // A barrier: a release request (which keeps every registration still within its grace) and the wait for the hook
    // thread to handle it, so every message posted before it has been handled too.
    private static void AwaitQuiet(HotkeyService service)
    {
        var handled = service.RetiredReleaseRequestsHandled;
        service.ReleaseRetiredKeyboardHooksNow(force: false);
        Assert.True(
            SpinWait.SpinUntil(() => service.RetiredReleaseRequestsHandled > handled, HookTimeout),
            "The hook thread never handled the release request.");
    }

    [Fact]
    public void Start_lets_a_key_already_on_its_way_to_a_replaced_registration_through_and_still_judges_it()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        var events = new ConcurrentQueue<string>();

        // The chain, newest first, once the move is made: Scribe's new registration, a hook that holds the bound key's
        // press until that move, as a Remote Desktop client's hook can hold a key it forwards, the registration the move
        // replaced, and the test's guard, which swallows whatever reaches it.
        using var guard = new InjectedKeyboardHook(swallow: true);
        using var service = new HotkeyService(
            NullLogger<HotkeyService>.Instance, HotkeyCaptureSession.Build([F21], HotkeyMode.Hold), () => true);
        service.Activated += (_, _) => events.Enqueue("start");
        service.Deactivated += (_, _) => events.Enqueue("stop");
        service.Start();
        using var arrived = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        using var client = new InjectedKeyboardHook(swallow: false, onEvent: (key, up) =>
        {
            // Released as soon as the move is made; the bound keeps it inside LowLevelHooksTimeout, after which Windows
            // would remove this hook and pass the press on without it.
            if (key == F21 && !up && !arrived.IsSet)
            {
                arrived.Set();
                proceed.Wait(TimeSpan.FromMilliseconds(500));
            }
        });

        // The press enters the chain before the move: inside the client's hook when Scribe's new registration lands.
        // SendInput returns only once the chain has run, so it is sent from a thread of its own.
        Exception? injectFailure = null;
        var press = new Thread(() =>
        {
            try
            {
                Inject(Key(F21, up: false));
            }
            catch (Exception ex)
            {
                injectFailure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "test-inject",
        };
        press.Start();
        Assert.True(arrived.Wait(HookTimeout), "The press never reached the hook ahead of Scribe's.");
        service.MoveKeyboardHookAheadNow();
        var moved = SpinWait.SpinUntil(() => service.KeyboardHookMoves == 1, TimeSpan.FromMilliseconds(450));
        proceed.Set();
        Assert.True(moved, "The hook thread did not move ahead while the press was held in the hook ahead of it.");
        Assert.True(press.Join(HookTimeout), "The press was never delivered.");
        Assert.Null(injectFailure);

        // Judged there, through the replaced registration: the dictation started, and the press went on to the hooks
        // behind Scribe's rather than being swallowed behind a hook that saw it.
        Assert.True(SpinWait.SpinUntil(() => events.Count == 1, HookTimeout), "The press was never judged.");
        Assert.True(guard.WaitFor(1), "The press stopped at the replaced registration.");

        // Its release enters the chain at the new registration, which lets it through too, and ends the dictation.
        Inject(Key(F21, up: true));
        Assert.True(SpinWait.SpinUntil(() => events.Count == 2, HookTimeout), "The release ended nothing.");
        Assert.True(guard.WaitFor(2), "The release was swallowed.");
        Assert.Equal(["start", "stop"], events.ToArray());
        Assert.Equal([(F21, false, false), (F21, true, false)], guard.Seen);
        Assert.Equal((F21, false, false), client.Seen[0]);
    }

    [Fact]
    public void Start_measures_the_marker_windows_hands_a_keyboard_hook_for_scribe_s_own_key()
    {
        if (!InputInjectionAllowed())
        {
            return;
        }

        // Windows keeps only the low 32 bits of a mouse event's extra information (MB, measured on both CI runners), and hands
        // a keyboard hook the whole value (this test, on both). Scribe recognizes its own keyboard input either way
        // (KeyboardHookFilter.IsScribesOwn); this pins which one a keyboard hook gets.
        using var guard = new InjectedKeyboardHook(swallow: true);
        Assert.True(NativeMethods.SendMarkedKeyEvent((ushort)F20, keyUp: true), "The marked key was not sent.");
        Assert.True(guard.WaitFor(1), "The marked key never reached the hook.");

        var extra = Assert.Single(guard.ExtraInfo);
        Assert.True(
            extra == SyntheticInputMarker.Value,
            $"Windows handed the keyboard hook 0x{(ulong)extra:X16} for Scribe's marker 0x{(ulong)SyntheticInputMarker.Value:X16}.");
    }

    /// <summary>
    /// A low-level keyboard hook of the test's own, on its own thread, registered when it is made, so ahead of every hook
    /// registered before it and behind every one registered after. It records each event that carries the test's marker or
    /// Scribe's (whole, or as the low half Windows may keep of it), with the extra information it arrived with, calls
    /// <c>onEvent</c> for it on its own thread, inside the callback, and swallows it or passes it on; real input carries
    /// neither marker and passes untouched.
    /// </summary>
    private sealed class InjectedKeyboardHook : IDisposable
    {
        private readonly NativeMethods.LowLevelKeyboardProc _proc;
        private readonly bool _swallow;
        private readonly Action<uint, bool>? _onEvent;
        private readonly ConcurrentQueue<(uint Key, bool Up, bool Scribes)> _seen = new();
        private readonly ConcurrentQueue<nuint> _extraInfo = new();
        private readonly ConcurrentQueue<uint> _times = new();
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private uint _threadId;
        private nint _hook;
        private int _error;

        public InjectedKeyboardHook(bool swallow, Action<uint, bool>? onEvent = null)
        {
            _swallow = swallow;
            _onEvent = onEvent;
            _proc = Callback;
            _thread = new Thread(Run) { IsBackground = true, Name = "test-keyboard-hook" };
            _thread.Start();
            Assert.True(_ready.Wait(HookTimeout), "The test's hook thread never started.");
            Assert.True(_hook != 0, $"The test's hook could not be installed (Win32 error {_error}); nothing is injected without it.");
        }

        public (uint Key, bool Up, bool Scribes)[] Seen => _seen.ToArray();

        public nuint[] ExtraInfo => _extraInfo.ToArray();

        /// <summary>The time stamp each recorded event arrived with (KBDLLHOOKSTRUCT.time), in the order seen.</summary>
        public uint[] Times => _times.ToArray();

        public bool WaitFor(int events) => SpinWait.SpinUntil(() => _seen.Count >= events, HookTimeout);

        public void Dispose()
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, 0, 0);
            _thread.Join(HookTimeout);
            _ready.Dispose();
        }

        private static bool Carries(nuint extra, nuint marker) => extra == marker || extra == (nuint)(uint)marker;

        private void Run()
        {
            NativeMethods.EnsureMessageQueue();
            _threadId = NativeMethods.GetCurrentThreadId();
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
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
            var extra = (nuint)Marshal.ReadIntPtr(lParam, KeyboardHookFilter.ExtraInfoOffset);
            var scribes = Carries(extra, SyntheticInputMarker.Value);
            if (nCode >= 0 && (scribes || Carries(extra, TestInputMarker)))
            {
                var message = (int)wParam;
                var up = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                var key = (uint)Marshal.ReadInt32(lParam, KeyboardHookFilter.VkCodeOffset);
                _extraInfo.Enqueue(extra);
                _times.Enqueue((uint)Marshal.ReadInt32(lParam, KeyboardHookFilter.TimeOffset));
                _seen.Enqueue((key, up, scribes));
                _onEvent?.Invoke(key, up);
                if (_swallow)
                {
                    return 1;
                }
            }

            return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
        }
    }
}
