using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Scribe.Core.Diagnostics;

/// <summary>What <see cref="DiagnosticsExport.RunAsync"/> did with a request.</summary>
public sealed class DiagnosticsExportResult
{
    private DiagnosticsExportResult(DiagnosticsBundleResult? bundle) => Bundle = bundle;

    /// <summary>A request made while another export was still being written: nothing was written for it.</summary>
    public static DiagnosticsExportResult Refused { get; } = new(null);

    /// <summary>The bundle written, or null when the request was refused.</summary>
    public DiagnosticsBundleResult? Bundle { get; }

    /// <summary>Whether the request was refused because another export was still being written.</summary>
    [MemberNotNullWhen(false, nameof(Bundle))]
    public bool WasRefused => Bundle is null;

    internal static DiagnosticsExportResult Written(DiagnosticsBundleResult bundle) => new(bundle);
}

/// <summary>
/// Writes the diagnostics bundle on a worker, one at a time, for Settings' "Save diagnostics..." (DATA-O-09,
/// <see cref="PerfFlags.BackgroundDiagnosticsExport"/>).
/// </summary>
/// <remarks>
/// <see cref="DiagnosticsBundle.Create"/> redacts every retained day line by line into an Optimal zip; on the dispatcher
/// that froze the window for about half a second over a week of logs. Here it runs unchanged on the thread pool, so the
/// zip, its redaction, its report and every failure it can leave behind are the synchronous path's. The owner, not the
/// window, logs each outcome by its shape, so a result that lands after the window closed still reaches the log. A second
/// request while one is being written is refused, never joined to the running one (it may name another destination). Quit
/// waits a bounded time for a running export (<see cref="WaitForIdle"/>).
/// </remarks>
public sealed class DiagnosticsExport
{
    private readonly ILogger _log;
    private readonly Func<Func<DiagnosticsBundleResult>, Task<DiagnosticsBundleResult>> _schedule;
    private readonly Func<string, string, string, DateOnly, DiagnosticsBundleResult> _create;
    private readonly object _gate = new();
    private Task<DiagnosticsExportResult>? _running;

    public DiagnosticsExport(ILogger<DiagnosticsExport> log)
        : this(log, static work => Task.Run(work), create: null)
    {
    }

    /// <param name="schedule">Runs the export's work; tests hold it to decide when it runs.</param>
    /// <param name="create">Writes the bundle; <see cref="DiagnosticsBundle.Create"/> unless a test injects a failure.</param>
    internal DiagnosticsExport(
        ILogger log,
        Func<Func<DiagnosticsBundleResult>, Task<DiagnosticsBundleResult>> schedule,
        Func<string, string, string, DateOnly, DiagnosticsBundleResult>? create)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(schedule);
        _log = log;
        _schedule = schedule;
        _create = create ?? (static (logs, destination, report, day) => DiagnosticsBundle.Create(logs, destination, report, day));
    }

    /// <summary>Whether an export is being written now.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running is not null;
            }
        }
    }

    /// <summary>
    /// Writes the bundle to <paramref name="destination"/> on a worker, with everything the caller read on its own thread
    /// captured first. Refused (<see cref="DiagnosticsExportResult.Refused"/>) while another export is being written. A
    /// failure to write the destination faults the task with the exception <see cref="DiagnosticsBundle.Create"/> threw,
    /// after it is logged by its shape.
    /// </summary>
    public Task<DiagnosticsExportResult> RunAsync(string logsDirectory, string destination, string report, DateOnly day)
    {
        var done = new TaskCompletionSource<DiagnosticsExportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_running is not null)
            {
                return Task.FromResult(DiagnosticsExportResult.Refused);
            }

            _running = done.Task;
        }

        Task<DiagnosticsBundleResult> work;
        try
        {
            work = _schedule(() => _create(logsDirectory, destination, report, day));
        }
        catch (Exception ex)
        {
            Finish(done, bundle: null, ex);
            return done.Task;
        }

        work.ContinueWith(
            static (finished, state) =>
            {
                var (owner, done) = ((DiagnosticsExport, TaskCompletionSource<DiagnosticsExportResult>))state!;
                if (finished.IsCompletedSuccessfully)
                {
                    owner.Finish(done, finished.Result, failure: null);
                }
                else
                {
                    owner.Finish(done, bundle: null, finished.Exception?.InnerException ?? new OperationCanceledException());
                }
            },
            (this, done),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return done.Task;
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for an export being written to end, its outcome logged. True when none is
    /// being written, or it ended in time.
    /// </summary>
    public bool WaitForIdle(TimeSpan timeout)
    {
        Task<DiagnosticsExportResult>? running;
        lock (_gate)
        {
            running = _running;
        }

        if (running is null)
        {
            return true;
        }

        try
        {
            return ((IAsyncResult)running).AsyncWaitHandle.WaitOne(timeout);
        }
        catch (Exception)
        {
            // A handle that cannot be waited on is an export that cannot be waited for: quit goes on.
            return false;
        }
    }

    // Logs the outcome by its shape, then lets the next request in, then completes the caller's task: the caller sees the
    // export idle and its outcome already in the log. Nothing here may throw into the worker's continuation.
    private void Finish(TaskCompletionSource<DiagnosticsExportResult> done, DiagnosticsBundleResult? bundle, Exception? failure)
    {
        try
        {
            if (bundle is not null)
            {
                _log.LogInformation(
                    "Wrote a diagnostics bundle with {Count} log file(s), {Bytes} bytes.", bundle.LogFileCount, bundle.Bytes);
            }
            else
            {
                _log.LogWarning("Could not write the diagnostics bundle. ({Failure})", FailureShape.Describe(failure));
            }
        }
        catch (Exception)
        {
            // Diagnostics must never change what the export reports.
        }

        lock (_gate)
        {
            _running = null;
        }

        if (bundle is not null)
        {
            done.TrySetResult(DiagnosticsExportResult.Written(bundle));
        }
        else
        {
            done.TrySetException(failure ?? new InvalidOperationException("The diagnostics export ended without a result."));
        }
    }
}
