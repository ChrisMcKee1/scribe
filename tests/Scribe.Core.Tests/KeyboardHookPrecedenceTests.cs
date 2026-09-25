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

        rig.Precedence.OnForegroundChanged(RemoteWindow);

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
        rig.Precedence.OnForegroundChanged(RemoteWindow);
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
        rig.Precedence.OnForegroundChanged(RemoteWindow);
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();
        Assert.Equal(KeyboardHookPrecedence.KeepAheadPeriod, rig.Time.Timer.Due);

        rig.Foreground = LocalWindow;
        rig.Precedence.OnForegroundChanged(LocalWindow);

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
        rig.Precedence.OnForegroundChanged(RemoteWindow);
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

        rig.Precedence.OnForegroundChanged(RemoteWindow);

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

        rig.Precedence.OnForegroundChanged(window);

        Assert.Null(rig.Time.Timer.Due);
        Assert.Empty(rig.Requests);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public void Switching_away_before_the_first_move_cancels_it()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Precedence.OnForegroundChanged(RemoteWindow);

        rig.Foreground = LocalWindow;
        rig.Precedence.OnForegroundChanged(LocalWindow);

        Assert.Null(rig.Time.Timer.Due);
        rig.Time.Timer.Fire(); // a tick already on its way when the timer was stopped
        Assert.Empty(rig.Requests);
    }

    [Fact]
    public void Switching_away_after_the_first_move_drops_the_second_and_keeps_the_release()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Precedence.OnForegroundChanged(RemoteWindow);
        rig.Time.Timer.Fire();

        rig.Foreground = LocalWindow;
        rig.Precedence.OnForegroundChanged(LocalWindow);

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
        rig.Precedence.OnForegroundChanged(RemoteWindow);

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
        rig.Precedence.OnForegroundChanged(RemoteWindow);
        rig.Time.Timer.Fire();

        rig.Foreground = LocalWindow;
        rig.Time.Timer.Fire();
        Assert.Equal(["move"], rig.Requests);
        Assert.Equal(KeyboardHookPrecedence.RetiredGrace, rig.Time.Timer.Due);

        rig.Time.Timer.Fire();
        Assert.Equal(["move", "release"], rig.Requests);
    }

    [Fact]
    public void Another_remote_client_coming_to_the_front_starts_over()
    {
        var rig = new Rig();
        rig.Foreground = RemoteWindow;
        rig.Precedence.OnForegroundChanged(RemoteWindow);
        rig.Time.Timer.Fire();
        rig.Time.Timer.Fire();

        rig.Foreground = OtherRemoteWindow;
        rig.Precedence.OnForegroundChanged(OtherRemoteWindow);

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
        rig.Precedence.OnForegroundChanged(RemoteWindow);

        rig.Precedence.Dispose();
        rig.Time.Timer.Fire();
        rig.Precedence.OnForegroundChanged(RemoteWindow);

        Assert.Empty(rig.Requests);
        Assert.True(rig.Time.Timer.Disposed);
    }

    [Fact]
    public void A_failing_process_query_or_request_never_escapes_to_the_pool()
    {
        var rig = new Rig { ThrowFromQuery = true };
        rig.Foreground = RemoteWindow;

        rig.Precedence.OnForegroundChanged(RemoteWindow);

        Assert.Null(rig.Time.Timer.Due);
        Assert.Empty(rig.Requests);
    }

    private sealed class Rig
    {
        public Rig()
        {
            Precedence = new KeyboardHookPrecedence(
                () => Foreground,
                window =>
                {
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
                () => Requests.Add("move"),
                () => Requests.Add("release"),
                Log,
                Time);
        }

        public nint Foreground { get; set; }

        public bool ThrowFromQuery { get; set; }

        public List<string> Requests { get; } = [];

        public TextInjectionFakes.CapturingLogger<KeyboardHookPrecedenceTests> Log { get; } = new();

        public OneTimerClock Time { get; } = new();

        public KeyboardHookPrecedence Precedence { get; }
    }

    /// <summary>A clock with one timer, which fires only when the test says so.</summary>
    internal sealed class OneTimerClock : TimeProvider
    {
        private ScriptedTimer? _timer;

        public ScriptedTimer Timer => Volatile.Read(ref _timer)!;

        /// <summary>The timer once made (a service makes it as its hook installation is built), or null.</summary>
        public ScriptedTimer? MadeTimer => Volatile.Read(ref _timer);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ScriptedTimer(callback, state);
            Assert.Null(Interlocked.CompareExchange(ref _timer, timer, null));
            timer.Change(dueTime, period);
            return timer;
        }
    }

    /// <summary>
    /// A timer that ticks only when the test fires it. The pool thread arms it while the test reads it, so its state is
    /// read and written under a lock.
    /// </summary>
    internal sealed class ScriptedTimer(TimerCallback callback, object? state) : ITimer
    {
        private readonly object _gate = new();
        private TimeSpan? _due;
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
                return !_disposed;
            }
        }

        /// <summary>Runs the callback, armed or not: a real timer can deliver a tick that was already on its way.</summary>
        public void Fire()
        {
            lock (_gate)
            {
                _due = null;
            }

            callback(state);
        }

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
