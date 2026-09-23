// The storage suites' own test clock and timer, in a namespace of their own. The concurrency suites
// have a ManualTimeProvider and ManualTimer of their own in Scribe.Core.Tests.Concurrency, and a type
// in the enclosing namespace would win over theirs in every file that imports it.

namespace Scribe.Core.Tests.StorageTime;

/// <summary>
/// A clock the test owns. Time moves only when the test says so, and timers fire only when the test
/// calls <see cref="ManualTimer.Fire"/>, so scheduling tests need no sleeps. The wall clock and the
/// monotonic timestamp move together through <see cref="Advance"/>, and apart through
/// <see cref="SetWallClock"/>, the way a user correcting the system time moves only one of them.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;
    private long _ticks;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _now += by;
        Interlocked.Add(ref _ticks, by.Ticks);
    }

    public void SetWallClock(DateTimeOffset value) => _now = value;

    public ManualTimer SingleTimer
    {
        get
        {
            lock (_timers)
            {
                return Assert.Single(_timers);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state);
        timer.Change(dueTime, period);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }
}

internal sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
{
    private volatile bool _disposed;

    /// <summary>The armed due time, or null while disarmed or disposed.</summary>
    public TimeSpan? DueTime { get; private set; }

    public bool Disposed => _disposed;

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        if (_disposed)
        {
            return false;
        }

        DueTime = dueTime == Timeout.InfiniteTimeSpan ? null : dueTime;
        return true;
    }

    /// <summary>
    /// Runs the callback on the calling thread. Deliberately works after disposal too, because a
    /// real timer can deliver a callback that was already queued when it was disposed.
    /// </summary>
    public void Fire()
    {
        DueTime = null;
        callback(state);
    }

    public void Dispose()
    {
        _disposed = true;
        DueTime = null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
