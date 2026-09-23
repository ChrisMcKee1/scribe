namespace Scribe.Core.Lifecycle;

/// <summary>
/// A one-shot timer that other threads can keep scheduling while its owner closes it, and that never delivers a tick
/// belonging to an earlier schedule.
/// </summary>
/// <remarks>
/// <para>
/// A disposed <see cref="Timer"/> throws <see cref="ObjectDisposedException"/> from <c>Change</c>, and an owner that
/// disposes its timers during shutdown cannot stop every other thread from re-arming them in the same moment. That is
/// exactly how the dictation controller's shutdown failed: processing finished after Dispose, re-armed the idle timer,
/// and the exception escaped into the host's teardown. Here every schedule and the close are serialized under one lock,
/// the timer is detached under that lock before it is disposed outside it, and a schedule after close is a no-op that
/// reports false.
/// </para>
/// <para>
/// A platform timer queues each tick to the thread pool, so a tick can arrive after the schedule that produced it was
/// canceled or replaced, and a disposed timer can still deliver one. Left alone, a duration ceiling armed for one
/// recording could end the next recording. Each schedule therefore records the moment it is due, on the timer's own
/// clock, and a tick is delivered only once that moment has come: a tick with nothing armed is dropped, and a tick that
/// arrives early re-arms the timer for the time that remains instead of being lost. Early covers both a stale tick from
/// a replaced schedule and a platform timer that fires slightly before its due time, which coarse timer resolution
/// allows. Each schedule delivers at most one tick.
/// </para>
/// <para>
/// The callback runs outside the lock and must tolerate a closed owner, typically by checking the owner's own closing
/// state first. It must not throw either, because an exception on a timer thread terminates the process.
/// </para>
/// </remarks>
public sealed class ClosableTimer : IDisposable
{
    /// <summary>The longest due time a platform timer accepts (4294967294 ms); longer schedules are clamped to it.</summary>
    public static readonly TimeSpan MaxDueTime = TimeSpan.FromMilliseconds(4_294_967_294d);

    // Re-arming for less than this would only spin: the platform timer cannot resolve a shorter wait anyway.
    private static readonly TimeSpan MinimumRearm = TimeSpan.FromMilliseconds(1);

    private readonly object _sync = new();
    private readonly Action _callback;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private bool _closed;

    // When the current schedule is due, in the time provider's timestamp units; null while nothing is armed, which is
    // also the state after the schedule's one tick was delivered.
    private long? _dueTimestamp;

    public ClosableTimer(Action callback, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
        _time = timeProvider ?? TimeProvider.System;
        _timer = _time.CreateTimer(
            static state => ((ClosableTimer)state!).OnTick(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>True once <see cref="Close"/> has run.</summary>
    public bool IsClosed
    {
        get { lock (_sync) { return _closed; } }
    }

    /// <summary>
    /// Arms the timer to fire once after <paramref name="dueTime"/>, replacing any earlier schedule and orphaning any tick
    /// that schedule still has in flight. <see cref="Timeout.InfiniteTimeSpan"/> disarms it. Returns false, and touches
    /// nothing, once the timer is closed.
    /// </summary>
    public bool Schedule(TimeSpan dueTime)
    {
        if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(dueTime), dueTime, "A due time must be non-negative or infinite.");
        }

        if (dueTime > MaxDueTime)
        {
            dueTime = MaxDueTime;
        }

        lock (_sync)
        {
            if (_closed || _timer is null)
            {
                return false;
            }

            _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? null : _time.GetTimestamp() + ToTimestampUnits(dueTime);
            return _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Disarms the timer, orphaning any tick already in flight. Returns false once the timer is closed.</summary>
    public bool Cancel() => Schedule(Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Marks the timer closed and detaches it under the lock, then disposes it outside the lock. Idempotent.
    /// </summary>
    public void Close()
    {
        ITimer? timer;
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _dueTimestamp = null;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    public void Dispose() => Close();

    private void OnTick()
    {
        lock (_sync)
        {
            // Closed, canceled, or this schedule's tick was already delivered: whatever produced this tick is gone.
            if (_closed || _timer is null || _dueTimestamp is not { } due)
            {
                return;
            }

            var remaining = _time.GetElapsedTime(_time.GetTimestamp(), due);
            if (remaining > TimeSpan.Zero)
            {
                if (remaining < MinimumRearm)
                {
                    remaining = MinimumRearm;
                }
                else if (remaining > MaxDueTime)
                {
                    remaining = MaxDueTime;
                }

                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            _dueTimestamp = null;
        }

        _callback();
    }

    private long ToTimestampUnits(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);
}
