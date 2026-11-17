using Scribe.Core.Vocabulary;

namespace Scribe.Core.Settings;

/// <summary>Where a failure to finish a committed Settings save was met (<see cref="SettingsApplicationDebt.Report"/>).</summary>
public enum SettingsApplicationStep
{
    /// <summary>Reading the stored settings back, or applying them, after a Save committed.</summary>
    Apply,

    /// <summary>An attempt of the background retry.</summary>
    Retry,

    /// <summary>The reference repair that follows a Save settled after its outcome was not known.</summary>
    ReferenceRepair,

    /// <summary>Settling a Save whose outcome was not known.</summary>
    Settlement,
}

/// <summary>
/// The application of stored settings that a committed Settings save still owes dictation: reading them back or applying
/// them failed, so dictation keeps running on the previous ones (a history retention of forever stored while the old
/// seven days go on deleting history). One owner for the whole process, in memory only, so a closed Settings window
/// cannot take the debt with it.
/// </summary>
/// <remarks>
/// <para>
/// The debt is paid in two ways. A window pays it in the foreground, on every settlement and Save outcome
/// (<see cref="WordPackSaveProtocol"/>), with the application the window asked for. And once the host has attached its own
/// (<see cref="Attach"/>), a failure starts one background retry, bounded to the attempts in the delays it was given, one
/// at a time, so the stored settings come into use with no window and no further Save. A retry waits on a single
/// <see cref="TimeProvider"/> delay between attempts, which settling the debt, raising it again or <see cref="Dispose"/>
/// cuts short: no timer polls, nothing waits for the retry to finish, and nothing is written anywhere. Once the attempts
/// are used up the debt stays owed in memory, and the next <see cref="Owe"/>, <see cref="Resume"/> (a Settings window
/// opening) or Save starts again.
/// </para>
/// <para>
/// Every application is of the settings as stored when it reads them, never of a document the caller holds, and an
/// application that began before the newest <see cref="Owe"/> cannot settle it (<see cref="Begin"/>, <see cref="Settle"/>):
/// it may have read the document before the Save that failed to apply committed it.
/// </para>
/// </remarks>
public sealed class SettingsApplicationDebt : IDisposable
{
    /// <summary>The waits before the background retry's attempts: five attempts over about thirteen minutes.</summary>
    public static IReadOnlyList<TimeSpan> DefaultRetryDelays { get; } =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    ];

    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<TimeSpan> _delays;
    private readonly CancellationTokenSource _shutdown = new();
    private Action<SettingsApplicationStep, Exception>? _report;
    private Func<Task<VocabularyRefresh>>? _apply;
    private Run? _run;
    private bool _owed;
    private bool _disposed;
    private long _epoch;

    /// <param name="time">The clock the retry's waits run on.</param>
    /// <param name="reportFailure">Told of each failure by its step, to log its shape; see <see cref="Report"/>.</param>
    /// <param name="retryDelays">The waits before the background retry's attempts; none means no background retry.</param>
    public SettingsApplicationDebt(
        TimeProvider? time = null,
        Action<SettingsApplicationStep, Exception>? reportFailure = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _time = time ?? TimeProvider.System;
        _report = reportFailure;
        _delays = retryDelays ?? DefaultRetryDelays;
    }

    /// <summary>True from a failed application until one that began after it goes through.</summary>
    public bool IsOwed
    {
        get
        {
            lock (_sync)
            {
                return _owed;
            }
        }
    }

    /// <summary>True while the background retry is running or waiting for its next attempt.</summary>
    public bool IsRetrying
    {
        get
        {
            lock (_sync)
            {
                return _run is not null;
            }
        }
    }

    /// <summary>
    /// Gives the owner how the host puts the stored settings into effect without a window: it reads them and applies them
    /// on whatever thread the host needs, and answers with the generation the application asked for. A failure it ends in
    /// is an attempt that did not go through; an answer of any outcome hands the settings over. Without it a debt is only
    /// kept, for a window to pay. <paramref name="reportFailure"/> replaces the failure callback when given.
    /// </summary>
    public void Attach(Func<Task<VocabularyRefresh>> applyStored, Action<SettingsApplicationStep, Exception>? reportFailure = null)
    {
        ArgumentNullException.ThrowIfNull(applyStored);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _apply = applyStored;
            _report = reportFailure ?? _report;
        }

        Resume();
    }

    /// <summary>
    /// A Save committed and its stored settings could not be put into effect. Owed until an application that begins after
    /// this goes through, and the background retry starts, or begins again from its first wait, when the host attached one.
    /// Never blocks and never throws.
    /// </summary>
    public void Owe()
    {
        Run? start;
        TaskCompletionSource? wake;
        lock (_sync)
        {
            _owed = true;
            _epoch++;
            (start, wake) = Arm();
        }

        wake?.TrySetResult();
        Start(start);
    }

    /// <summary>
    /// Starts a fresh background retry for a debt that is still owed and has none running, as when a Settings window opens
    /// after the last one's attempts ran out. Does nothing otherwise.
    /// </summary>
    public void Resume()
    {
        Run? start;
        TaskCompletionSource? wake;
        lock (_sync)
        {
            if (!_owed || _run is not null)
            {
                return;
            }

            (start, wake) = Arm();
        }

        wake?.TrySetResult();
        Start(start);
    }

    /// <summary>
    /// What an application is judged against: take it before the application reads the stored settings, and pass it to
    /// <see cref="Settle"/> once the answer is in.
    /// </summary>
    public long Begin()
    {
        lock (_sync)
        {
            return _epoch;
        }
    }

    /// <summary>
    /// An application that began at <paramref name="epoch"/> put the stored settings into effect. Ends the debt, and a
    /// retry waiting for its next attempt, unless the debt was raised again since: such an application may have read the
    /// settings before the Save that raised it committed them. Returns whether it ended the debt.
    /// </summary>
    public bool Settle(long epoch)
    {
        TaskCompletionSource? wake;
        lock (_sync)
        {
            if (epoch != _epoch)
            {
                return false;
            }

            _owed = false;
            wake = _run?.Wake;
        }

        wake?.TrySetResult();
        return true;
    }

    /// <summary>
    /// Tells the host a failure after a commit was met, so it can log the failure's shape: the callback is the host's, runs
    /// on the failing thread, and whatever it does or throws changes nothing a Save reports or a retry does. It receives
    /// the exception only to describe it, never to show it.
    /// </summary>
    public void Report(SettingsApplicationStep step, Exception failure)
    {
        Action<SettingsApplicationStep, Exception>? report;
        lock (_sync)
        {
            report = _report;
        }

        if (report is null)
        {
            return;
        }

        try
        {
            report(step, failure);
        }
        catch
        {
            // Saying a failure happened is best effort: a sink that throws must not change what the Save reports, and must
            // not end the retry that is trying to put the settings into effect.
        }
    }

    /// <summary>
    /// Stops the retry for good, because the process is going away. Never waits for an attempt in flight, which is left to
    /// the process's exit; the debt stays owed. Idempotent.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            _shutdown.Cancel();
        }
        catch
        {
            // A callback registered on the token threw; every wait it was waking is cancelled either way.
        }
    }

    // Caller holds _sync. A running retry is told to begin again from its first wait, since the debt is new; otherwise one
    // is started, when there is something to run it with. Only the caller, outside the lock, wakes or starts it.
    private (Run? Start, TaskCompletionSource? Wake) Arm()
    {
        if (_disposed || _apply is null || _delays.Count == 0)
        {
            return (null, null);
        }

        if (_run is { } running)
        {
            running.Attempts = 0;
            return (null, running.Wake);
        }

        _run = new Run();
        return (_run, null);
    }

    private void Start(Run? run)
    {
        if (run is not null)
        {
            _ = Task.Run(() => RetryAsync(run));
        }
    }

    // One run at a time, and only ever started by Arm under the lock. The decision to end is made under that same lock,
    // so an Owe that arrives while the run is deciding either resets its attempts first or finds it gone and starts another.
    private async Task RetryAsync(Run run)
    {
        try
        {
            while (true)
            {
                TimeSpan delay;
                Func<Task<VocabularyRefresh>> apply;
                TaskCompletionSource wake;
                lock (_sync)
                {
                    if (!_owed || _disposed || _apply is null || run.Attempts >= _delays.Count)
                    {
                        _run = null;
                        return;
                    }

                    delay = _delays[run.Attempts++];
                    apply = _apply;

                    // Completed, from any thread, by whatever settles or raises the debt while the run waits. Its
                    // continuation runs on the pool, never inside the caller that completed it.
                    wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    run.Wake = wake;
                }

                var woken = await WaitAsync(delay, wake.Task).ConfigureAwait(false);
                lock (_sync)
                {
                    run.Wake = null;
                }

                if (!woken && IsOwed)
                {
                    await AttemptAsync(apply).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // Nothing here may reach the thread pool as an unhandled exception.
            lock (_sync)
            {
                if (ReferenceEquals(_run, run))
                {
                    _run = null;
                }
            }

            Report(SettingsApplicationStep.Retry, ex);
        }
    }

    // True when the wait was cut short: woken, or the process is ending. The delay's own source belongs to this call alone,
    // so cutting the delay short is never racing anything.
    private async Task<bool> WaitAsync(TimeSpan delay, Task wake)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var elapsed = Task.Delay(delay, _time, stop.Token);
        var first = await Task.WhenAny(elapsed, wake).ConfigureAwait(false);
        if (ReferenceEquals(first, wake))
        {
            stop.Cancel();
        }

        try
        {
            await elapsed.ConfigureAwait(false);
            return ReferenceEquals(first, wake);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private async Task AttemptAsync(Func<Task<VocabularyRefresh>> apply)
    {
        var epoch = Begin();
        try
        {
            // Any answer hands the settings over; the vocabulary generation they asked for has its own retries.
            _ = await apply().WaitAsync(_shutdown.Token).ConfigureAwait(false);
            Settle(epoch);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // The process is ending, and there is nothing left to apply the settings to.
        }
        catch (Exception ex)
        {
            Report(SettingsApplicationStep.Retry, ex);
        }
    }

    private sealed class Run
    {
        /// <summary>Waits used since the debt was last raised; under the owner's lock.</summary>
        public int Attempts { get; set; }

        /// <summary>Completed to cut the run's current wait short; under the owner's lock.</summary>
        public TaskCompletionSource? Wake { get; set; }
    }
}
