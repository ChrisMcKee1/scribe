using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Scribe.Core.Tests.Concurrency;

/// <summary>
/// A logger that keeps every entry, rendered AND structured, so a test can prove what never reaches a log. Checking
/// only the rendered message is not enough: structured sinks keep every argument whether the template uses it or not.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        _entries.Enqueue(new CapturedLogEntry(logLevel, formatter(state, exception), [.. values], exception));
    }
}

internal sealed record CapturedLogEntry(
    LogLevel Level,
    string Message,
    KeyValuePair<string, object?>[] State,
    Exception? Exception)
{
    /// <summary>True when the text appears anywhere the entry could carry it: message, structured values, exception.</summary>
    public bool Mentions(string text) =>
        Message.Contains(text, StringComparison.OrdinalIgnoreCase)
        || State.Any(pair => Convert.ToString(pair.Value)?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
        || (Exception?.ToString().Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

    public object? Value(string key) => State.FirstOrDefault(pair => pair.Key == key).Value;
}

/// <summary>Deterministic proof that a race actually overlapped, instead of a sleep that hopes it did.</summary>
internal static class BlockedThreads
{
    /// <summary>A safety net against a hung test, never part of what a test asserts.</summary>
    public static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns once <paramref name="thread"/> is blocked. Documented for <see cref="System.Threading.ThreadState.WaitSleepJoin"/>:
    /// a thread requesting a lock with Monitor.Enter or waiting with Monitor.Wait is reported as blocked, so a test that
    /// parks a thread on exactly one lock can know it is waiting there.
    /// </summary>
    public static void WaitUntilBlocked(Thread thread)
    {
        var started = Stopwatch.GetTimestamp();
        while ((thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
        {
            if (!thread.IsAlive)
            {
                throw new InvalidOperationException("The thread finished instead of blocking.");
            }

            if (Stopwatch.GetElapsedTime(started) > SafetyTimeout)
            {
                throw new TimeoutException("The thread never blocked.");
            }

            Thread.Yield();
        }
    }

    /// <summary>Starts a background thread running <paramref name="work"/>.</summary>
    public static Thread Start(Action work)
    {
        var thread = new Thread(() => work()) { IsBackground = true };
        thread.Start();
        return thread;
    }

    /// <summary>Joins with the safety timeout and fails loudly instead of hanging.</summary>
    public static void Join(Thread thread)
    {
        if (!thread.Join(SafetyTimeout))
        {
            throw new TimeoutException("The thread did not finish.");
        }
    }
}

/// <summary>
/// Sets a test's gates on every way out of the scope it is declared in (stream TR round 4, A5). A thread a test holds at a
/// gate (a production thread, a fake's, or one the test started to run production code) waits for its release and for
/// nothing else, because what the test asserts rests on that thread staying where it is until the test lets it go; so a
/// test that fails before its own release still releases here, and ends rather than hangs. A using declaration is a
/// finally over every statement after it, and runs before the ones declared earlier are disposed: declare it after the
/// gates and after whatever the held thread belongs to.
/// </summary>
internal sealed class ReleaseAtExit(params ManualResetEventSlim[] gates) : IDisposable
{
    public void Dispose()
    {
        foreach (var gate in gates)
        {
            gate.Set();
        }
    }
}

/// <summary>A time provider whose clock and timers only move when a test says so.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _timestamp;

    public IReadOnlyList<ManualTimer> Timers
    {
        get { lock (_timers) { return [.. _timers]; } }
    }

    /// <summary>The clock only advances here, so due times and elapsed checks are fully under the test's control.</summary>
    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    // One timestamp unit per TimeSpan tick, so every conversion in a test is exact whatever the real clock's frequency.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) =>
        Interlocked.Add(ref _timestamp, (long)(by.TotalSeconds * TimestampFrequency));

    /// <summary>Runs with each timer as it is created, on the creating thread, once it is recorded: lets a test wait for one.</summary>
    public Action<ManualTimer>? Created { get; set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        Created?.Invoke(timer);
        return timer;
    }
}

/// <summary>
/// A timer that records what was done to it, and throws from Change once disposed exactly as
/// <see cref="System.Threading.Timer"/> does, so any use after dispose fails the test.
/// </summary>
internal sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
{
    private readonly object _sync = new();
    private readonly List<string> _events = [];

    /// <summary>Runs inside Change, before it records anything; lets a test hold a Change in progress.</summary>
    public Action? DuringChange { get; set; }

    public TimeSpan DueTime { get; private set; } = dueTime;

    public int DisposeCount { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>Everything done to the timer, in order: "change:{due}" or "dispose".</summary>
    public IReadOnlyList<string> Events
    {
        get { lock (_sync) { return [.. _events]; } }
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        DuringChange?.Invoke();
        lock (_sync)
        {
            DueTime = dueTime;
            _events.Add($"change:{dueTime}");
        }

        return true;
    }

    /// <summary>Delivers a tick, as the pool would, whatever state the timer is in.</summary>
    public void Fire() => callback(state);

    public void Dispose()
    {
        lock (_sync)
        {
            IsDisposed = true;
            DisposeCount++;
            _events.Add("dispose");
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
