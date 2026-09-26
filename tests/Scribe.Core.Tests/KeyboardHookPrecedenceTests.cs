using Microsoft.Extensions.Logging;
using Scribe.Core.Hotkeys;

namespace Scribe.Core.Tests;

/// <summary>
/// The pool side of keeping Scribe's keyboard hook ahead of a Remote Desktop or virtual machine client's: when such a
/// client's window comes to the front, the hook thread is asked to register its hook afresh, after the client has
/// registered its own on activation, once more in case it registered late, and then to release what the moves replaced.
/// Driven on a clock the test owns: nothing here sleeps.
/// </summary>
public class KeyboardHookPrecedenceTests
{
    // A hang guard, never the verdict: every wait below is for something certain to happen.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private const nint RemoteWindow = 0x1111;
    private const nint OtherRemoteWindow = 0x1122;
    private const nint LocalWindow = 0x2222;
    private const nint OtherLocalWindow = 0x2233;

    [Fact]
    public void A_remote_client_in_front_gets_two_moves_after_it_registers_and_then_a_release()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;

        rig.Notice(RemoteWindow);

        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        Assert.Empty(rig.Requests);

        rig.Time.Timer.Fire();
        Assert.Equal(["move"], rig.Requests);
        Assert.Equal(KeyboardHookPrecedence.SecondMoveDelay, rig.Time.Timer.Due);

        rig.Time.Timer.Fire();
        Assert.Equal(["move", "move"], rig.Requests);
        Assert.Equal(KeyboardHookPrecedence.RetiredGrace, rig.Time.Timer.Due);

        rig.Time.Timer.Fire();
        Assert.Equal(["move", "move", "release"], rig.Requests);

        // The client is still in front: the next move is a keep-ahead period away.
        Assert.Equal(KeyboardHookPrecedence.KeepAheadPeriod, rig.Time.Timer.Due);
    }

    [Fact]
    public void While_a_remote_client_stays_in_front_the_hook_moves_ahead_again_every_keep_ahead_period()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();

        for (var period = 1; period <= 2; period++)
        {
            Assert.Equal(KeyboardHookPrecedence.KeepAheadPeriod, rig.Time.Timer.Due);
            rig.Time.Timer.Fire();
            Assert.Equal(KeyboardHookPrecedence.RetiredGrace, rig.Time.Timer.Due);
            rig.Time.Timer.Fire();
        }

        Assert.Equal(["move", "move", "release", "move", "release", "move", "release"], rig.Requests);
        Assert.Single(rig.Log.Entries); // once, when the client came to the front
    }

    [Fact]
    public void The_keep_ahead_moves_stop_once_the_remote_client_leaves_the_front()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();
        Assert.Equal(KeyboardHookPrecedence.KeepAheadPeriod, rig.Time.Timer.Due);

        rig.Foreground = LocalWindow;
        rig.Notice(LocalWindow);

        Assert.Null(rig.Time.Timer.Due);
        rig.Time.Timer.Fire(); // a tick already on its way when the timer was stopped
        Assert.Equal(["move", "move", "release"], rig.Requests);
    }

    [Fact]
    public void A_keep_ahead_move_that_falls_due_with_the_client_no_longer_in_front_is_not_made()
    {
        // The notice for the change can still be on its way: the keep-ahead move asks Windows what is in front first.
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();

        rig.Foreground = LocalWindow;
        rig.Time.Timer.Fire();

        Assert.Equal(["move", "move", "release"], rig.Requests);
        Assert.Null(rig.Time.Timer.Due);
    }

    [Fact]
    public void The_scheduled_move_is_logged_by_the_client_s_process_name_and_nothing_else()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;

        rig.Notice(RemoteWindow);

        var line = Assert.Single(rig.Log.Entries);
        Assert.StartsWith(
            "Information: Remote desktop client msrdc is in front; a move of the keyboard hook ahead of its hook is " +
            "scheduled, and repeated while it stays in front.",
            line);
    }

    [Theory]
    [InlineData(0x2222)] // LocalWindow
    [InlineData(0)]
    public void Any_other_window_in_front_asks_for_nothing(int window)
    {
        var rig = new Rig();
        rig.Foreground = window;

        rig.Notice(window);

        Assert.Null(rig.Time.Timer.Due);
        Assert.Empty(rig.Requests);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public void Switching_away_before_the_first_move_cancels_it()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);

        rig.Foreground = LocalWindow;
        rig.Notice(LocalWindow);

        Assert.Null(rig.Time.Timer.Due);
        rig.Time.Timer.Fire(); // a tick already on its way when the timer was stopped
        Assert.Empty(rig.Requests);
    }

    [Fact]
    public void Switching_away_after_the_first_move_drops_the_second_and_keeps_the_release()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();

        rig.Foreground = LocalWindow;
        rig.Notice(LocalWindow);

        Assert.Equal(KeyboardHookPrecedence.RetiredGrace, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        Assert.Equal(["move", "release"], rig.Requests);
        Assert.Null(rig.Time.Timer.Due);
    }

    [Fact]
    public void A_move_that_falls_due_with_the_client_no_longer_in_front_is_not_made()
    {
        // The notice for the change back may still be on its way: each move asks Windows what is in front first.
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);

        rig.Foreground = LocalWindow;
        rig.Time.Timer.Fire();

        Assert.Empty(rig.Requests);
        Assert.Null(rig.Time.Timer.Due);
    }

    [Fact]
    public void A_second_move_that_falls_due_with_the_client_gone_is_skipped_and_the_first_is_still_released()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();

        rig.Foreground = LocalWindow;
        rig.Time.Timer.Fire();
        Assert.Equal(["move"], rig.Requests);
        Assert.Equal(KeyboardHookPrecedence.RetiredGrace, rig.Time.Timer.Due);

        rig.Time.Timer.Fire();
        Assert.Equal(["move", "release"], rig.Requests);
    }

    [Fact]
    public void The_same_remote_window_coming_to_the_front_again_does_not_restart_a_sequence_under_way()
    {
        // Review round 2, item 3 (Grok's G2): every remote notice restarted the sequence at the first move, so notices about
        // 300 ms apart each produced a move, and the fifth found every kept registration still inside its grace.
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();

        rig.Notice(RemoteWindow);
        Assert.Equal(KeyboardHookPrecedence.SecondMoveDelay, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        rig.Notice(RemoteWindow);
        Assert.Equal(KeyboardHookPrecedence.RetiredGrace, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        rig.Notice(RemoteWindow);
        Assert.Equal(KeyboardHookPrecedence.KeepAheadPeriod, rig.Time.Timer.Due);

        Assert.Equal(["move", "move", "release"], rig.Requests);
        Assert.Single(rig.Log.Entries);
    }

    [Fact]
    public void A_repeated_notice_for_the_window_whose_first_move_is_pending_does_not_postpone_it()
    {
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        rig.Time.Advance(TimeSpan.FromMilliseconds(200));

        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();

        Assert.Equal(["move"], rig.Requests);
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Elapsed);
    }

    [Fact]
    public void The_same_remote_window_back_in_front_after_another_one_starts_over()
    {
        // Deactivated and activated again, the client may register its hook again: the first move follows its return.
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();

        rig.Foreground = LocalWindow;
        rig.Notice(LocalWindow);
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);

        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
    }

    [Fact]
    public void A_step_that_finds_the_client_gone_lets_its_return_start_over()
    {
        // The notice for the change can still be on its way when a step finds no remote client in front; the client coming
        // back afterwards is a return, not a repeat.
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();

        rig.Foreground = LocalWindow;
        rig.Time.Timer.Fire();
        Assert.Equal(KeyboardHookPrecedence.RetiredGrace, rig.Time.Timer.Due);
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);

        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        Assert.Equal(["move"], rig.Requests);
    }

    [Fact]
    public void Another_remote_client_coming_to_the_front_starts_over()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();

        rig.Foreground = OtherRemoteWindow;
        rig.Notice(OtherRemoteWindow);

        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();
        Assert.Equal(["move", "move", "move", "move", "release"], rig.Requests);
        Assert.Equal(2, rig.Log.Entries.Count);
        Assert.Contains("Remote desktop client vmconnect is in front", rig.Log.Entries[1]);
    }

    [Fact]
    public void Nothing_is_asked_once_disposed()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);

        rig.Precedence.Dispose();
        rig.Time.Timer.Fire();
        rig.Notice(RemoteWindow);

        Assert.Empty(rig.Requests);
        Assert.True(rig.Time.Timer.Disposed);
    }

    [Fact]
    public void Every_move_carries_the_foreground_revision_and_the_window_its_step_judged()
    {
        // Review round 2, item 5 (A5): the hook thread makes a move only while the foreground it was judged on is still the
        // one Windows has in front (the notice published no newer revision since, and the window is still in front).
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();

        rig.Publish(RemoteWindow); // a newer notice, published but not yet handled when the second move falls due
        rig.Time.Timer.Fire();

        Assert.Equal([(1L, RemoteWindow), (2L, RemoteWindow)], rig.Moves);
    }

    [Fact]
    public void A_failing_process_query_or_request_never_escapes_to_the_pool()
    {
        var rig = new Rig { ThrowFromQuery = true };
        rig.Foreground = RemoteWindow;

        rig.Notice(RemoteWindow);

        Assert.Null(rig.Time.Timer.Due);
        Assert.Empty(rig.Requests);
    }

    [Fact]
    public void An_older_lookup_that_finishes_last_cannot_cancel_the_newest_remote_schedule()
    {
        // Review round 2, item 4 (A4). The notice's pool callbacks can overlap (.NET re-arms a registered wait before it runs
        // its callback), and each looks its window's process up before it takes the gate: a slow lookup of a local window
        // published first can finish after a remote window published later armed the first move.
        var rig = new Rig();
        using var atLookup = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        rig.BeforeLookup = window =>
        {
            if (window == LocalWindow)
            {
                atLookup.Set();
                release.Wait(Bound);
            }
        };
        rig.Foreground = RemoteWindow;
        var local = rig.Publish(LocalWindow);
        var older = new Thread(() => rig.Precedence.OnForegroundChanged(LocalWindow, local.Revision, local.Changes))
        {
            IsBackground = true,
        };
        older.Start();
        Assert.True(atLookup.Wait(Bound), "The older lookup never started.");

        rig.Notice(RemoteWindow);
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        release.Set();
        Assert.True(older.Join(Bound), "The older lookup never finished.");

        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        Assert.Equal(["move"], rig.Requests);
    }

    [Fact]
    public void A_notice_no_newer_than_the_last_one_decided_changes_nothing()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        var first = rig.Publish(LocalWindow);
        var second = rig.Publish(RemoteWindow);

        rig.Precedence.OnForegroundChanged(RemoteWindow, second.Revision, second.Changes);
        rig.Precedence.OnForegroundChanged(LocalWindow, first.Revision, first.Changes);
        rig.Precedence.OnForegroundChanged(LocalWindow, second.Revision, second.Changes);

        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        Assert.Single(rig.Log.Entries);
    }

    [Fact]
    public void A_tick_of_an_earlier_schedule_cannot_take_a_newer_one_before_it_is_due()
    {
        // Review round 2, item 4 (A4). A step's tick reads the step, looks up what is in front outside the gate and takes the
        // gate again. Two schedules both at the first move looked alike, so a tick of the first, held up in its lookup while
        // a second remote window armed its own first move, made that move at once, before the new client had registered.
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);
        using var atRead = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var hold = 1;
        rig.BeforeForegroundRead = () =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                atRead.Set();
                release.Wait(Bound);
            }
        };
        var tick = new Thread(rig.Time.Timer.Fire) { IsBackground = true };
        tick.Start();
        Assert.True(atRead.Wait(Bound), "The first schedule's tick never looked up what is in front.");

        rig.Foreground = OtherRemoteWindow;
        rig.Notice(OtherRemoteWindow);
        var armedAt = rig.Time.Elapsed;
        release.Set();
        Assert.True(tick.Join(Bound), "The tick never finished.");

        Assert.Empty(rig.Requests);
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        Assert.Equal(["move"], rig.Requests);
        Assert.Equal(armedAt + KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Elapsed);
    }

    [Fact]
    public void A_tick_that_arrives_early_waits_for_the_time_that_remains()
    {
        // As ClosableTimer does it: each schedule records when it is due, and a tick is taken only once that moment has come.
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);

        rig.Time.Timer.Tick();
        Assert.Empty(rig.Requests);
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);

        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        rig.Time.Timer.Tick();
        Assert.Empty(rig.Requests);
        Assert.Equal(TimeSpan.FromMilliseconds(150), rig.Time.Timer.Due);

        rig.Time.Timer.Fire();
        Assert.Equal(["move"], rig.Requests);

        // The next step is armed now; a late delivery of the tick just taken is early for it and moves nothing.
        rig.Time.Timer.Tick();
        Assert.Equal(["move"], rig.Requests);
        Assert.Equal(KeyboardHookPrecedence.SecondMoveDelay, rig.Time.Timer.Due);
    }

    [Fact]
    public void Each_foreground_notice_is_published_at_once_with_a_newer_revision_which_its_handler_receives()
    {
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(nint Window, long Revision, long Changes)>();
        using var notice = new ForegroundNotice((window, revision, changes) => seen.Enqueue((window, revision, changes)));
        Assert.Equal(0, notice.PublishedRevision);

        notice.Notify(RemoteWindow);
        Assert.Equal(1, notice.PublishedRevision); // on the notifying thread, before any handler runs
        Assert.True(SpinWait.SpinUntil(() => seen.Count == 1, Bound), "The first notice was not handled.");
        notice.Notify(LocalWindow);
        Assert.Equal(2, notice.PublishedRevision);
        Assert.True(SpinWait.SpinUntil(() => seen.Count == 2, Bound), "The second notice was not handled.");
        notice.Notify(LocalWindow);
        Assert.True(SpinWait.SpinUntil(() => seen.Count == 3, Bound), "The third notice was not handled.");

        Assert.Equal([(RemoteWindow, 1L, 1L), (LocalWindow, 2L, 2L), (LocalWindow, 3L, 2L)], seen.ToArray());
    }

    [Fact]
    public void A_publication_counts_a_change_only_when_its_window_differs_from_the_one_published_before_it()
    {
        // Review round 3, item 2 (A7): what tells a repeat of the same window from that window back in front after another.
        var publication = new ForegroundPublication();
        Assert.Equal(((nint)0, 0L, 0L), publication.Read());

        publication.Publish(RemoteWindow);
        publication.Publish(RemoteWindow);
        Assert.Equal((RemoteWindow, 2L, 1L), publication.Read());

        publication.Publish(LocalWindow);
        publication.Publish(RemoteWindow);
        Assert.Equal((RemoteWindow, 4L, 3L), publication.Read());
        Assert.Equal(4, publication.Revision);
        Assert.Equal(3, publication.Changes);
    }

    [Fact]
    public void The_client_back_in_front_after_a_window_whose_notice_coalesced_away_cannot_lose_its_first_move()
    {
        // Review round 3, item 2 (A7), sequence one. R's first move falls due; its tick takes the schedule and, the
        // foreground having gone to a local window L, looks L up (held here). The foreground returns to R. L's notice and R's
        // coalesced (a ForegroundNotice hands its handler only the latest window), so R's notice looked like a repeat of the
        // window the sequence served: it was kept as it was, the tick then found L, and armed nothing, and R stayed in front
        // with no move scheduled. The change count tells R's return from a repeat, so the sequence starts over, and the tick
        // in flight finds its schedule gone.
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        using var atLookup = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        rig.BeforeLookup = window =>
        {
            if (window == LocalWindow)
            {
                atLookup.Set();
                release.Wait(Bound);
            }
        };
        rig.Foreground = LocalWindow;
        var tick = new Thread(rig.Time.Timer.Fire) { IsBackground = true };
        tick.Start();
        Assert.True(atLookup.Wait(Bound), "The tick never looked the local window up.");

        rig.Publish(LocalWindow); // L's notice, coalesced into R's: never handled on its own
        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);
        release.Set();
        Assert.True(tick.Join(Bound), "The tick never finished.");

        Assert.Empty(rig.Requests);
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        Assert.Equal(["move"], rig.Requests);
    }

    [Fact]
    public void The_client_back_in_front_after_a_window_whose_notice_coalesced_away_starts_over_from_the_keep_ahead_wait()
    {
        // Review round 3, item 2 (A7), sequence two: R, then L, then R again, quickly, with L's notice coalesced away. The
        // return kept the long keep-ahead wait instead of the first move a return gets (the client may register its hook
        // again on its activation).
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();
        Assert.Equal(KeyboardHookPrecedence.KeepAheadPeriod, rig.Time.Timer.Due);

        rig.Publish(LocalWindow);
        rig.Notice(RemoteWindow);

        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        Assert.Equal(["move", "move", "release", "move"], rig.Requests);
    }

    [Fact]
    public void A_tick_in_flight_when_the_same_window_is_noticed_again_cannot_apply_what_it_judged_before()
    {
        // Review round 3, item 2 (A7): a tick in flight when a newer notice is decided must not apply its outcome, on the
        // same-window path too. R's first move falls due; its tick reads the window in front at a moment R is losing
        // activation and Windows has none (GetForegroundWindow: "The foreground window can be NULL in certain
        // circumstances, such as when a window is losing activation"), and R's notice of regaining it, with no other window
        // published between, is decided while that tick is still in flight. A repeat of the same window keeps the sequence,
        // and the tick then ended it: nothing scheduled while R stays in front. Now the notice takes the step back, to run at
        // once on a fresh read.
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        using var atRead = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        rig.AfterForegroundRead = window =>
        {
            if (window == 0)
            {
                atRead.Set();
                release.Wait(Bound);
            }
        };
        rig.Foreground = 0;
        var tick = new Thread(rig.Time.Timer.Fire) { IsBackground = true };
        tick.Start();
        Assert.True(atRead.Wait(Bound), "The tick never read the window in front.");

        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow); // the same window, and nothing else published since: a repeat
        release.Set();
        Assert.True(tick.Join(Bound), "The tick never finished.");

        Assert.Empty(rig.Requests);
        Assert.Equal(TimeSpan.Zero, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        Assert.Equal(["move"], rig.Requests);
        Assert.Equal(KeyboardHookPrecedence.SecondMoveDelay, rig.Time.Timer.Due);
    }

    [Fact]
    public void A_tick_in_flight_when_another_window_is_noticed_cannot_apply_what_it_judged_before()
    {
        // R's release falls due; its tick finds R in front and is still looking R up when a local window's notice is decided.
        // The release stays due (moves were made), and the tick used to go on to release and schedule a keep-ahead move for
        // R, 30 s later, with L in front. Now the notice takes the step back, and a fresh tick releases and ends the sequence.
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();
        Assert.Equal(KeyboardHookPrecedence.RetiredGrace, rig.Time.Timer.Due);
        using var atLookup = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var hold = 1;
        rig.BeforeLookup = window =>
        {
            if (window == RemoteWindow && Interlocked.Exchange(ref hold, 0) == 1)
            {
                atLookup.Set();
                release.Wait(Bound);
            }
        };
        var tick = new Thread(rig.Time.Timer.Fire) { IsBackground = true };
        tick.Start();
        Assert.True(atLookup.Wait(Bound), "The tick never looked the client up.");

        rig.Foreground = LocalWindow;
        rig.Notice(LocalWindow);
        release.Set();
        Assert.True(tick.Join(Bound), "The tick never finished.");

        Assert.Equal(["move", "move"], rig.Requests);
        Assert.Equal(TimeSpan.Zero, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        Assert.Equal(["move", "move", "release"], rig.Requests);
        Assert.Null(rig.Time.Timer.Due);
    }

    [Fact]
    public void The_recovery_starts_the_sequence_for_a_remote_client_left_in_front_with_nothing_scheduled()
    {
        // Review round 3, item 2, as round 4 (item 1, A8) remade it: the watchdog's recovery poll finds the client in front
        // with no step scheduled (its first move's tick read no window in front, as the client was losing activation, and no
        // notice followed) and starts the sequence itself. It publishes nothing: the revision the moves are judged on stays.
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        rig.Foreground = 0;
        rig.Time.Timer.Fire();
        Assert.Null(rig.Time.Timer.Due);
        rig.Foreground = RemoteWindow;
        var revision = rig.PublishedRevision;

        rig.Recover();

        Assert.Equal(revision, rig.PublishedRevision);
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        Assert.StartsWith(
            "Information: Remote desktop client msrdc is in front with no move scheduled; a move of the keyboard hook ahead " +
            "of its hook is scheduled, and repeated while it stays in front.",
            rig.Log.Entries[^1]);
        rig.Time.Timer.Fire();
        Assert.Equal([(revision, RemoteWindow)], rig.Moves);
    }

    [Theory]
    [InlineData(0x2222)] // LocalWindow
    [InlineData(0)]
    public void The_recovery_starts_nothing_for_any_other_window_in_front_and_publishes_nothing(int window)
    {
        var rig = new Rig { Foreground = window };
        rig.Notice(window);
        var revision = rig.PublishedRevision;

        rig.Recover();

        Assert.Equal(revision, rig.PublishedRevision);
        Assert.Null(rig.Time.Timer.Due);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public void The_recovery_changes_nothing_while_a_step_is_scheduled()
    {
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        rig.Time.Timer.Fire();
        Assert.Equal(KeyboardHookPrecedence.SecondMoveDelay, rig.Time.Timer.Due);
        var revision = rig.PublishedRevision;

        rig.Recover();

        Assert.Equal(revision, rig.PublishedRevision);
        Assert.Equal(KeyboardHookPrecedence.SecondMoveDelay, rig.Time.Timer.Due);
        Assert.Single(rig.Log.Entries);
    }

    [Fact]
    public void A_real_notice_decided_while_the_recovery_samples_the_foreground_keeps_the_sequence_it_started()
    {
        // Review round 4, item 1 (A8), Astra's sequence: the recovery found no step scheduled and sampled the local window in
        // front; before it went on, the client came to the front and its real notice started the sequence. The recovery's
        // old sample must neither cancel that sequence nor advance the revision its moves are judged on.
        var rig = new Rig { Foreground = LocalWindow };
        rig.Notice(LocalWindow);
        using var sampled = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        var recovery = new Thread(rig.Recover) { IsBackground = true };
        rig.AfterForegroundRead = window =>
        {
            if (ReferenceEquals(Thread.CurrentThread, recovery) && !sampled.IsSet)
            {
                sampled.Set();
                resume.Wait(Bound);
            }
        };
        recovery.Start();
        Assert.True(sampled.Wait(Bound), "The recovery never sampled the window in front.");

        rig.Foreground = RemoteWindow;
        rig.Notice(RemoteWindow);
        var revision = rig.PublishedRevision;
        resume.Set();
        Assert.True(recovery.Join(Bound), "The recovery never finished.");

        Assert.Equal(revision, rig.PublishedRevision);
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        Assert.Equal([(revision, RemoteWindow)], rig.Moves);
    }

    [Fact]
    public void A_notice_published_while_the_recovery_looks_the_client_up_decides_instead_of_it()
    {
        // The recovery sampled the client and is looking its process up when a local window comes to the front and its real
        // notice is decided. At its commit the published revision has moved, so the recovery's sample is older than a
        // notice, and it starts nothing.
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        rig.Foreground = 0;
        rig.Time.Timer.Fire();
        rig.Foreground = RemoteWindow;
        using var looking = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        var recovery = new Thread(rig.Recover) { IsBackground = true };
        rig.BeforeLookup = window =>
        {
            if (ReferenceEquals(Thread.CurrentThread, recovery) && window == RemoteWindow && !looking.IsSet)
            {
                looking.Set();
                resume.Wait(Bound);
            }
        };
        recovery.Start();
        Assert.True(looking.Wait(Bound), "The recovery never looked the client up.");

        rig.Foreground = LocalWindow;
        rig.Notice(LocalWindow);
        resume.Set();
        Assert.True(recovery.Join(Bound), "The recovery never finished.");

        Assert.Null(rig.Time.Timer.Due);
        Assert.Empty(rig.Moves);
    }

    [Fact]
    public void An_older_notice_decided_after_the_recovery_cannot_cancel_the_sequence_it_started()
    {
        // A local window's notice was published before the recovery read the revision, and its handler is still looking the
        // window up; meanwhile the client came to the front (its own notice not published yet). The recovery samples the
        // client and starts the sequence: its sample is newer than that notice, so the notice, decided late, changes nothing.
        var rig = new Rig { Foreground = LocalWindow };
        rig.Notice(LocalWindow);
        using var looking = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        var handler = new Thread(rig.Deliver) { IsBackground = true };
        rig.BeforeLookup = window =>
        {
            if (ReferenceEquals(Thread.CurrentThread, handler) && !looking.IsSet)
            {
                looking.Set();
                resume.Wait(Bound);
            }
        };
        rig.Publish(OtherLocalWindow);
        handler.Start();
        Assert.True(looking.Wait(Bound), "The notice's handler never looked the window up.");

        rig.Foreground = RemoteWindow;
        rig.Recover();
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        resume.Set();
        Assert.True(handler.Join(Bound), "The notice's handler never finished.");

        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        Assert.Equal(["move"], rig.Requests);
    }

    [Fact]
    public void A_failing_lookup_in_the_recovery_never_escapes_and_starts_nothing()
    {
        var rig = new Rig { Foreground = RemoteWindow, ThrowFromQuery = true };

        rig.Recover();

        Assert.Null(rig.Time.Timer.Due);
        Assert.Empty(rig.Requests);
    }

    [Fact]
    public void A_notice_published_after_the_recovery_sampled_the_client_decides_instead_of_it()
    {
        // The recovery reads the revision before the window: here it has sampled the client (held right after the read) when
        // a local window comes to the front and its notice is decided. Read the other way round, the revision would already
        // include that notice, and the recovery would start a sequence for a client no longer in front.
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        rig.Foreground = 0;
        rig.Time.Timer.Fire();
        rig.Foreground = RemoteWindow;
        using var sampled = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        var recovery = new Thread(rig.Recover) { IsBackground = true };
        rig.AfterForegroundRead = _ =>
        {
            if (ReferenceEquals(Thread.CurrentThread, recovery) && !sampled.IsSet)
            {
                sampled.Set();
                resume.Wait(Bound);
            }
        };
        recovery.Start();
        Assert.True(sampled.Wait(Bound), "The recovery never sampled the window in front.");

        rig.Foreground = LocalWindow;
        rig.Notice(LocalWindow);
        resume.Set();
        Assert.True(recovery.Join(Bound), "The recovery never finished.");

        Assert.Null(rig.Time.Timer.Due);
    }

    [Fact]
    public void A_notice_decided_while_the_recovery_looks_keeps_the_step_its_sequence_reached()
    {
        // The client's own notice was published before the recovery read the revision, and is decided while the recovery
        // looks the client up: its sequence starts, and its first move is made. The recovery's commit finds a step scheduled
        // and leaves it: restarting from the first move would add moves the sequence did not ask for.
        var rig = new Rig { Foreground = RemoteWindow };
        rig.Notice(RemoteWindow);
        rig.Foreground = 0;
        rig.Time.Timer.Fire();
        rig.Foreground = RemoteWindow;
        using var handlerLooking = new ManualResetEventSlim(false);
        using var handlerResume = new ManualResetEventSlim(false);
        using var recoveryLooking = new ManualResetEventSlim(false);
        using var recoveryResume = new ManualResetEventSlim(false);
        var handler = new Thread(rig.Deliver) { IsBackground = true };
        var recovery = new Thread(rig.Recover) { IsBackground = true };
        rig.BeforeLookup = _ =>
        {
            if (ReferenceEquals(Thread.CurrentThread, handler) && !handlerLooking.IsSet)
            {
                handlerLooking.Set();
                handlerResume.Wait(Bound);
            }
            else if (ReferenceEquals(Thread.CurrentThread, recovery) && !recoveryLooking.IsSet)
            {
                recoveryLooking.Set();
                recoveryResume.Wait(Bound);
            }
        };
        rig.Publish(RemoteWindow);
        handler.Start();
        Assert.True(handlerLooking.Wait(Bound), "The notice's handler never looked the client up.");
        recovery.Start();
        Assert.True(recoveryLooking.Wait(Bound), "The recovery never looked the client up.");

        handlerResume.Set();
        Assert.True(handler.Join(Bound), "The notice's handler never finished.");
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        Assert.Equal(KeyboardHookPrecedence.SecondMoveDelay, rig.Time.Timer.Due);
        recoveryResume.Set();
        Assert.True(recovery.Join(Bound), "The recovery never finished.");

        Assert.Equal(KeyboardHookPrecedence.SecondMoveDelay, rig.Time.Timer.Due);
        Assert.Equal(["move"], rig.Requests);
    }

    private sealed class Rig
    {
        // The publication a ForegroundNotice makes, with its rule for what counts as a change; delivered synchronously here.
        private readonly ForegroundPublication _publication = new();

        public Rig()
        {
            Precedence = new KeyboardHookPrecedence(
                ReadForeground,
                window =>
                {
                    BeforeLookup?.Invoke(window);
                    if (ThrowFromQuery)
                    {
                        throw new InvalidOperationException("the process is gone");
                    }

                    return window switch
                    {
                        RemoteWindow => "msrdc",
                        OtherRemoteWindow => "vmconnect",
                        LocalWindow => "notepad",
                        OtherLocalWindow => "explorer",
                        _ => null,
                    };
                },
                () => _publication.Revision,
                (revision, window) =>
                {
                    Moves.Add((revision, window));
                    Requests.Add("move");
                },
                () => Requests.Add("release"),
                Log,
                Time);
        }

        public nint Foreground { get; set; }

        // The window in front as the precedence reads it, for every step and for the recovery, with the test's hooks around it.
        private nint ReadForeground()
        {
            BeforeForegroundRead?.Invoke();
            var window = Foreground;
            AfterForegroundRead?.Invoke(window);
            return window;
        }

        public bool ThrowFromQuery { get; set; }

        /// <summary>Runs on the thread of a step's foreground read, before it (a step's lookup only).</summary>
        public Action? BeforeForegroundRead { get; set; }

        /// <summary>Runs on the thread of a step's foreground read, with the window it read, before the step goes on.</summary>
        public Action<nint>? AfterForegroundRead { get; set; }

        /// <summary>Runs on the thread of every process lookup, a notice's or a step's, before it.</summary>
        public Action<nint>? BeforeLookup { get; set; }

        public List<string> Requests { get; } = [];

        /// <summary>Each move asked for: the foreground revision and the window its step judged.</summary>
        public List<(long Revision, nint Window)> Moves { get; } = [];

        public TextInjectionFakes.CapturingLogger<KeyboardHookPrecedenceTests> Log { get; } = new();

        public OneTimerClock Time { get; } = new();

        public KeyboardHookPrecedence Precedence { get; }

        /// <summary>
        /// A notice for <paramref name="window"/> published as the hook thread's WinEvent callback publishes one, and not
        /// handled: a later notice's handler reads only the latest window, so this one coalesces into it. Returns what a
        /// handler reading now would be handed.
        /// </summary>
        public (long Revision, long Changes) Publish(nint window)
        {
            _publication.Publish(window);
            var (_, revision, changes) = _publication.Read();
            return (revision, changes);
        }

        /// <summary>The notice's handler, run on this thread, for the latest publication.</summary>
        public void Deliver()
        {
            var (window, revision, changes) = _publication.Read();
            Precedence.OnForegroundChanged(window, revision, changes);
        }

        /// <summary>How many notices have been published: the revision a handler reading now would be handed.</summary>
        public long PublishedRevision => _publication.Revision;

        /// <summary>The watchdog's recovery poll, run on this thread.</summary>
        public void Recover() => Precedence.RecoverIfIdle();

        /// <summary>A foreground notice for <paramref name="window"/>, published and handled on this thread.</summary>
        public void Notice(nint window)
        {
            Publish(window);
            Deliver();
        }
    }

    /// <summary>
    /// A clock with one timer, which fires only when the test says so, and a time that moves only when the test or a firing
    /// timer moves it (<see cref="ScriptedTimer.Fire"/> brings it to the tick's due time).
    /// </summary>
    internal sealed class OneTimerClock : TimeProvider
    {
        private ScriptedTimer? _timer;
        private long _now;

        public ScriptedTimer Timer => Volatile.Read(ref _timer)!;

        /// <summary>The timer once made (a service makes it as its hook installation is built), or null.</summary>
        public ScriptedTimer? MadeTimer => Volatile.Read(ref _timer);

        /// <summary>How far the clock has moved since it was made.</summary>
        public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _now));

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _now);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _now, by.Ticks);

        /// <summary>Moves the clock forward to <paramref name="timestamp"/>, never back.</summary>
        public void AdvanceTo(long timestamp)
        {
            for (var now = Interlocked.Read(ref _now); now < timestamp; now = Interlocked.Read(ref _now))
            {
                if (Interlocked.CompareExchange(ref _now, timestamp, now) == now)
                {
                    return;
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ScriptedTimer(this, callback, state);
            Assert.Null(Interlocked.CompareExchange(ref _timer, timer, null));
            timer.Change(dueTime, period);
            return timer;
        }
    }

    /// <summary>
    /// A timer that ticks only when the test fires it. The pool thread arms it while the test reads it, so its state is
    /// read and written under a lock.
    /// </summary>
    internal sealed class ScriptedTimer(OneTimerClock clock, TimerCallback callback, object? state) : ITimer
    {
        private readonly object _gate = new();
        private TimeSpan? _due;
        private long _armedAt;
        private bool _disposed;

        /// <summary>When the armed tick is due, or null while disarmed.</summary>
        public TimeSpan? Due
        {
            get
            {
                lock (_gate)
                {
                    return _due;
                }
            }
        }

        public bool Disposed
        {
            get
            {
                lock (_gate)
                {
                    return _disposed;
                }
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            lock (_gate)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : dueTime;
                _armedAt = clock.GetTimestamp();
                return !_disposed;
            }
        }

        /// <summary>
        /// The armed tick arrives when it is due: the clock is brought to that moment first. With nothing armed it still runs
        /// the callback, as a real timer can deliver a tick that was already on its way.
        /// </summary>
        public void Fire()
        {
            long? dueAt;
            lock (_gate)
            {
                dueAt = _due is { } due ? _armedAt + due.Ticks : null;
                _due = null;
            }

            if (dueAt is { } moment)
            {
                clock.AdvanceTo(moment);
            }

            callback(state);
        }

        /// <summary>A tick arrives now, whatever is armed and whatever the time: one queued for a schedule since replaced.</summary>
        public void Tick() => callback(state);

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _due = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
