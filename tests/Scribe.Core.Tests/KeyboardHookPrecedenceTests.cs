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
    private const nint RemoteWindow = 0x1111;
    private const nint OtherRemoteWindow = 0x1122;
    private const nint LocalWindow = 0x2222;

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
    public void The_move_is_logged_by_the_client_s_process_name_and_nothing_else()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;

        rig.Notice(RemoteWindow);

        var line = Assert.Single(rig.Log.Entries);
        Assert.StartsWith(
            "Information: Remote desktop client msrdc is in front; the keyboard hook moves ahead of its hook and is kept there.",
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

        rig.Publish(); // a newer notice, published but not yet handled when the second move falls due
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
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        rig.Foreground = RemoteWindow;
        var local = rig.Publish();
        var older = new Thread(() => rig.Precedence.OnForegroundChanged(LocalWindow, local)) { IsBackground = true };
        older.Start();
        Assert.True(atLookup.Wait(TimeSpan.FromSeconds(10)), "The older lookup never started.");

        rig.Notice(RemoteWindow);
        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        release.Set();
        Assert.True(older.Join(TimeSpan.FromSeconds(10)), "The older lookup never finished.");

        Assert.Equal(KeyboardHookPrecedence.FirstMoveDelay, rig.Time.Timer.Due);
        rig.Time.Timer.Fire();
        Assert.Equal(["move"], rig.Requests);
    }

    [Fact]
    public void A_notice_no_newer_than_the_last_one_decided_changes_nothing()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        var first = rig.Publish();
        var second = rig.Publish();

        rig.Precedence.OnForegroundChanged(RemoteWindow, second);
        rig.Precedence.OnForegroundChanged(LocalWindow, first);
        rig.Precedence.OnForegroundChanged(LocalWindow, second);

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
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        var tick = new Thread(rig.Time.Timer.Fire) { IsBackground = true };
        tick.Start();
        Assert.True(atRead.Wait(TimeSpan.FromSeconds(10)), "The first schedule's tick never looked up what is in front.");

        rig.Foreground = OtherRemoteWindow;
        rig.Notice(OtherRemoteWindow);
        var armedAt = rig.Time.Elapsed;
        release.Set();
        Assert.True(tick.Join(TimeSpan.FromSeconds(10)), "The tick never finished.");

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
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(nint Window, long Revision)>();
        using var notice = new ForegroundNotice((window, revision) => seen.Enqueue((window, revision)));
        Assert.Equal(0, notice.PublishedRevision);

        notice.Notify(RemoteWindow);
        Assert.Equal(1, notice.PublishedRevision); // on the notifying thread, before any handler runs
        Assert.True(SpinWait.SpinUntil(() => seen.Count == 1, TimeSpan.FromSeconds(10)), "The first notice was not handled.");
        notice.Notify(LocalWindow);
        Assert.Equal(2, notice.PublishedRevision);
        Assert.True(SpinWait.SpinUntil(() => seen.Count == 2, TimeSpan.FromSeconds(10)), "The second notice was not handled.");

        Assert.Equal([(RemoteWindow, 1L), (LocalWindow, 2L)], seen.ToArray());
    }

    private sealed class Rig
    {
        private long _revision;

        public Rig()
        {
            Precedence = new KeyboardHookPrecedence(
                () =>
                {
                    BeforeForegroundRead?.Invoke();
                    return Foreground;
                },
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
                        _ => null,
                    };
                },
                () => Interlocked.Read(ref _revision),
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

        public bool ThrowFromQuery { get; set; }

        /// <summary>Runs on the thread of a step's foreground read, before it (a step's lookup only).</summary>
        public Action? BeforeForegroundRead { get; set; }

        /// <summary>Runs on the thread of every process lookup, a notice's or a step's, before it.</summary>
        public Action<nint>? BeforeLookup { get; set; }

        public List<string> Requests { get; } = [];

        /// <summary>Each move asked for: the foreground revision and the window its step judged.</summary>
        public List<(long Revision, nint Window)> Moves { get; } = [];

        public TextInjectionFakes.CapturingLogger<KeyboardHookPrecedenceTests> Log { get; } = new();

        public OneTimerClock Time { get; } = new();

        public KeyboardHookPrecedence Precedence { get; }

        /// <summary>The revision the next notice gets, published the way the hook thread's notice publishes it.</summary>
        public long Publish() => Interlocked.Increment(ref _revision);

        /// <summary>A foreground notice for <paramref name="window"/>, handled on this thread with a newer revision.</summary>
        public void Notice(nint window) => Precedence.OnForegroundChanged(window, Publish());
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
