using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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

/// <summary>
/// A logger whose sink fails for every entry whose template starts with one prefix, and keeps every other entry: for the
/// diagnostics that must never cost the work they describe. Counts how often it threw, so a test can prove the site ran.
/// </summary>
internal sealed class TemplateFailingLogger<T>(string templatePrefix) : ILogger<T>
{
    private readonly ConcurrentQueue<string> _written = new();
    private int _thrown;

    /// <summary>The templates of the entries it kept.</summary>
    public IReadOnlyList<string> Written => [.. _written];

    /// <summary>How many entries it refused by throwing.</summary>
    public int Thrown => Volatile.Read(ref _thrown);

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
        var template = values.FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string ?? string.Empty;
        if (template.StartsWith(templatePrefix, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _thrown);
            throw new IOException("The log sink failed.");
        }

        _written.Enqueue(template);
    }
}

/// <summary>Deterministic proof that a race actually overlapped, instead of a sleep that hopes it did.</summary>
internal static class BlockedThreads
{
    /// <summary>A safety net against a hung test, never part of what a test asserts.</summary>
    public static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(30);

    // What the work of each thread Start made threw, kept for the test's thread. Weak: a thread's record goes with it.
    private static readonly ConditionalWeakTable<Thread, StrongBox<Exception?>> Failures = new();

    /// <summary>
    /// Returns once <paramref name="thread"/> is blocked. Documented for <see cref="System.Threading.ThreadState.WaitSleepJoin"/>:
    /// a thread requesting a lock with Monitor.Enter or waiting with Monitor.Wait is reported as blocked, so a test that
    /// parks a thread on exactly one lock can know it is waiting there. A thread of <see cref="Start"/> that ends instead,
    /// because its work threw, has what it threw rethrown here.
    /// </summary>
    public static void WaitUntilBlocked(Thread thread)
    {
        var started = Stopwatch.GetTimestamp();
        while ((thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
        {
            if (!thread.IsAlive)
            {
                if ((thread.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
                {
                    // It has ended, so this returns at once, and what its work kept is visible after it.
                    thread.Join(SafetyTimeout);
                    ThrowIfFailed(thread);
                }

                throw new InvalidOperationException("The thread finished instead of blocking.");
            }

            if (Stopwatch.GetElapsedTime(started) > SafetyTimeout)
            {
                throw new TimeoutException("The thread never blocked.");
            }

            Thread.Yield();
        }
    }

    /// <summary>
    /// Starts a background thread running <paramref name="work"/>. Nothing the work throws leaves the thread, where it would
    /// end the test host and hide the failure (stream TR round 6b): it is kept, and <see cref="Join"/> and
    /// <see cref="WaitUntilBlocked"/> rethrow it on the test's thread. A thread that outlives its join keeps what it throws
    /// afterwards, on a test that has already failed.
    /// </summary>
    public static Thread Start(Action work)
    {
        // Made before the thread starts, so keeping a failure is one write, which cannot throw.
        var failure = new StrongBox<Exception?>();
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref failure.Value, ex);
            }
        })
        {
            IsBackground = true,
        };
        Failures.Add(thread, failure);
        thread.Start();
        return thread;
    }

    /// <summary>
    /// Joins with the safety timeout and fails loudly instead of hanging, then rethrows, with its own stack, whatever the
    /// work of a thread of <see cref="Start"/> threw.
    /// </summary>
    public static void Join(Thread thread)
    {
        if (!thread.Join(SafetyTimeout))
        {
            throw new TimeoutException("The thread did not finish.");
        }

        ThrowIfFailed(thread);
    }

    private static void ThrowIfFailed(Thread thread)
    {
        if (Failures.TryGetValue(thread, out var failure) && Volatile.Read(ref failure.Value) is { } thrown)
        {
            ExceptionDispatchInfo.Throw(thrown);
        }
    }
}

/// <summary>
/// A hang guard for a task certain to complete (stream TR round 7b): true once it has, false only if it has not within the
/// bound, so the test fails with its own message instead of waiting for ever and stopping the run. Never a latency check:
/// the bound only turns a hang into a failure. What the task threw is rethrown, a TimeoutException of its own included.
/// </summary>
internal static class HangGuard
{
    public static async Task<bool> Completes(Task task, TimeSpan bound)
    {
        try
        {
            await task.WaitAsync(bound).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException timeout) when (!ReferenceEquals(timeout, task.Exception?.InnerException))
        {
            return false; // the bound ran out: the task's own TimeoutException is not this one, and is rethrown
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

/// <summary>
/// A background thread of a test's own that keeps what its work throws instead of letting it end the test host, which would
/// hide the failure (stream TR round 5): the test that started it joins it and then calls <see cref="ThrowIfFailed"/>. It
/// counts as started only once its start returned (round 6), and a join of one not started returns at once, so a finally
/// can join every thread a test made whatever failed first, a start that threw included.
/// </summary>
internal sealed class TestThread
{
    private readonly Thread _thread;
    private ExceptionDispatchInfo? _failure;
    private bool _started;

    public TestThread(Action work, string name)
    {
        _thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(ex));
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
    }

    public bool IsAlive => _thread.IsAlive;

    public void Start()
    {
        _thread.Start();

        // Only now: a thread whose start threw is still unstarted, and joining one throws ThreadStateException, which
        // would replace the failure a finally is unwinding.
        Volatile.Write(ref _started, true);
    }

    public bool Join(TimeSpan timeout) => !Volatile.Read(ref _started) || _thread.Join(timeout);

    public void ThrowIfFailed() => Volatile.Read(ref _failure)?.Throw();
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
