using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.Benchmarks;

/// <summary>
/// What one log line costs its caller: the old shape, which opened, appended to and closed the daily
/// file inside every call, against the queued shape, which formats the line, enqueues it and returns.
/// Both arms format a representative line at the call site, as the provider does.
/// <para>
/// The queued Debug arm drains the queue in an iteration cleanup, outside the measurement, so it reports
/// the caller's cost while the writer keeps up rather than the disk's. The queued Warning arm waits for
/// each line to be written, which is what a prompt line costs. These are per-call costs on this machine
/// and disk; they say nothing about user-visible latency on their own.
/// </para>
/// <para>
/// The iteration cleanup forces one invocation per iteration, so the queued Debug arm's iterations are
/// short and BenchmarkDotNet warns about it; fifteen iterations, rather than the short run's three, keep
/// its error bars meaningful without writing gigabytes of temporary log.
/// </para>
/// </summary>
[MemoryDiagnoser]
[IterationCount(15)]
public class LogWriterBenchmarks
{
    // Sized so each invocation runs for tens of milliseconds: a synchronous append costs a file open
    // and close, a queued Debug line only an enqueue.
    private const int SynchronousCallsPerInvoke = 200;
    private const int QueuedCallsPerInvoke = 50_000;
    private const string Category = "DictationController";
    private const string Message =
        "dictation #7 stop reason=HotkeyReleased held=00:00:03.412 captured=3.40s device='Microphone (USB Audio)'";

    private readonly LogRecord[] _single = new LogRecord[1];
    private string _root = string.Empty;
    private DailyLogFile _synchronousFile = null!;
    private BackgroundLogWriter _queued = null!;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "scribe-logbench-" + Guid.NewGuid().ToString("N"));

        // Budget off: the benchmark writes far more than a real day, and past the budget the sink would
        // start dropping Debug lines, which would flatter the synchronous arm. The queue is sized so no
        // invocation overflows it, which would measure the drop path instead of the enqueue.
        _synchronousFile = DailyLogFile.Open(Path.Combine(_root, "sync"), null, dailyBudgetBytes: 0);
        _queued = new BackgroundLogWriter(
            DailyLogFile.Open(Path.Combine(_root, "queued"), null, dailyBudgetBytes: 0),
            new BackgroundLogWriterOptions
            {
                MaxQueuedRecords = QueuedCallsPerInvoke * 4,
                MaxQueuedChars = QueuedCallsPerInvoke * 4L * 256,
            });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _queued.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Temp folder; best effort.
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = SynchronousCallsPerInvoke)]
    public void SynchronousAppendPerCall()
    {
        for (var i = 0; i < SynchronousCallsPerInvoke; i++)
        {
            _single[0] = Create(LogLevel.Debug);
            _synchronousFile.Write(_single);
        }
    }

    [Benchmark(OperationsPerInvoke = QueuedCallsPerInvoke)]
    public void QueuedDebugLine()
    {
        for (var i = 0; i < QueuedCallsPerInvoke; i++)
        {
            _queued.Write(Create(LogLevel.Debug));
        }
    }

    [IterationCleanup(Target = nameof(QueuedDebugLine))]
    public void DrainQueue() => _queued.Flush(TimeSpan.FromSeconds(60));

    [Benchmark(OperationsPerInvoke = SynchronousCallsPerInvoke)]
    public void QueuedWarningLine()
    {
        for (var i = 0; i < SynchronousCallsPerInvoke; i++)
        {
            _queued.Write(Create(LogLevel.Warning), prompt: true);
        }
    }

    private static LogRecord Create(LogLevel level)
    {
        var now = DateTime.Now;
        return new LogRecord(now, level, LogLineFormat.Format(now, level, Category, Message));
    }
}
