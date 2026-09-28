using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
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

/// <summary>
/// The queue cost without disk I/O: the current monitor-backed writer against the channel shape used
/// by the overlay, both with a no-op sink so the benchmark measures producer-side queuing.
/// </summary>
[MemoryDiagnoser]
public class LogQueueMechanismBenchmarks
{
    private const int CallsPerInvoke = 50_000;
    private BackgroundLogWriter _current = null!;
    private ChannelLogWriter _channel = null!;
    private LogRecord _record;

    [GlobalSetup]
    public void Setup()
    {
        _current = new BackgroundLogWriter(
            new NoOpSink(),
            new BackgroundLogWriterOptions
            {
                MaxQueuedRecords = CallsPerInvoke * 4,
                MaxQueuedChars = CallsPerInvoke * 4L * 256,
            });
        _channel = new ChannelLogWriter(CallsPerInvoke * 4);
        var now = DateTime.Now;
        _record = new LogRecord(now, LogLevel.Debug, LogLineFormat.Format(now, LogLevel.Debug, "Trace", "trace dictation.process outcome=typed (12ms)"));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _current.Dispose();
        _channel.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = CallsPerInvoke)]
    public void CurrentMonitorQueue()
    {
        for (var i = 0; i < CallsPerInvoke; i++)
        {
            _current.Write(_record);
        }
    }

    [IterationCleanup(Target = nameof(CurrentMonitorQueue))]
    public void DrainCurrent() => _current.Flush(TimeSpan.FromSeconds(60));

    [Benchmark(OperationsPerInvoke = CallsPerInvoke)]
    public void ChannelQueue()
    {
        for (var i = 0; i < CallsPerInvoke; i++)
        {
            _channel.Write(_record);
        }
    }

    [IterationCleanup(Target = nameof(ChannelQueue))]
    public void DrainChannel() => _channel.Flush(TimeSpan.FromSeconds(60));

    private sealed class NoOpSink : ILogRecordSink
    {
        public void Write(IReadOnlyList<LogRecord> batch)
        {
        }
    }

    private sealed class ChannelLogWriter : IDisposable
    {
        private readonly Channel<LogRecord> _channel;
        private readonly Task _reader;
        private long _completed;
        private long _accepted;

        public ChannelLogWriter(int capacity)
        {
            _channel = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            _reader = Task.Factory.StartNew(
                Run,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }

        public void Write(LogRecord record)
        {
            if (_channel.Writer.TryWrite(record))
            {
                Interlocked.Increment(ref _accepted);
            }
        }

        public bool Flush(TimeSpan timeout)
        {
            var target = Interlocked.Read(ref _accepted);
            var deadline = Environment.TickCount64 + (long)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
            while (Interlocked.Read(ref _completed) < target)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return false;
                }

                Thread.Sleep((int)Math.Min(10, remaining));
            }

            return true;
        }

        public void Dispose()
        {
            _channel.Writer.TryComplete();
            try
            {
                _reader.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        private async Task Run()
        {
            await foreach (var _ in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Interlocked.Increment(ref _completed);
            }
        }
    }
}

/// <summary>Cost of the live redaction guard that runs before a line reaches disk.</summary>
[MemoryDiagnoser]
public class LogRedactionBenchmarks
{
    private const string Ordinary =
        "14:05:09.123 [Information] DictationController: #7 stopped (reason=HotkeyReleased, held=00:00:03.412).";

    private const string KnownTranscript =
        "14:05:09.123 [Debug] TranscriptionService: Decoded 4520 ms of audio in 210 ms (RTF 0.05): \"send the memo\"";

    [Benchmark(Baseline = true)]
    public string OrdinaryLine() => HistoricalLogRedaction.RedactEntry(Ordinary);

    [Benchmark]
    public string RecognizedSensitiveLine() => HistoricalLogRedaction.RedactEntry(KnownTranscript);
}

/// <summary>
/// LoggerMessage source generation against the regular extension method, through an enabled logger that
/// formats the message the same way FileLoggerProvider does.
/// </summary>
[MemoryDiagnoser]
public class LoggerMessageBenchmarks
{
    private readonly ILogger _logger = new FormattingLogger();

    [Benchmark(Baseline = true)]
    public void ExtensionMethod() => _logger.LogInformation("trace {Span}", "dictation.process outcome=typed (12ms)");

    [Benchmark]
    public void SourceGenerated() => BenchmarkLogMessages.TraceInformation(_logger, "dictation.process outcome=typed (12ms)");

    private sealed class FormattingLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _ = formatter(state, exception);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

internal static partial class BenchmarkLogMessages
{
    [LoggerMessage(EventId = 4201, Level = LogLevel.Information, Message = "trace {Span}")]
    public static partial void TraceInformation(ILogger logger, string span);
}
