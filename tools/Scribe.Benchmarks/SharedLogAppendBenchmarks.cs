using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Scribe.Core.Diagnostics;

namespace Scribe.Benchmarks;

/// <summary>
/// DATA-O-02 (PerfFlags.AppendOnlyLog): what one <see cref="DailyLogFile.Write"/> costs its writer thread today, against
/// the same write through a handle that may only append. One line (most batches) and a 512-line batch (a burst). The
/// same source runs on the baseline, where only today's arms exist (SCRIBE_BASELINE). The file is deleted after each
/// iteration, so the runs never write more than a few megabytes.
/// </summary>
[MemoryDiagnoser]
[IterationCount(15)]
public class SharedLogAppendBenchmarks
{
    private const int OneLineWritesPerInvoke = 200;
    private const int BatchWritesPerInvoke = 20;
    private const int BatchLines = 512;

    private string _root = string.Empty;
    private DailyLogFile _today = null!;
    private LogRecord[] _oneLine = null!;
    private LogRecord[] _batch = null!;
#if !SCRIBE_BASELINE
    private DailyLogFile _appendOnly = null!;
#endif

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "scribe-logappend-bench-" + Guid.NewGuid().ToString("N"));

        // Budget off, as in LogWriterBenchmarks: past a day's budget the sink drops Debug lines.
        _today = DailyLogFile.Open(Path.Combine(_root, "today"), null, dailyBudgetBytes: 0);
#if !SCRIBE_BASELINE
        _appendOnly = DailyLogFile.Open(
            Path.Combine(_root, "append-only"), null, dailyBudgetBytes: 0, appendMode: AppendOnlyLogMode.Fixed(true));
#endif
        var now = DateTime.Now;
        _oneLine = [Line(now, 0)];
        _batch = [.. Enumerable.Range(0, BatchLines).Select(i => Line(now, i))];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Temp folder; best effort.
        }
    }

    [IterationCleanup]
    public void DeleteTheDayFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*.log", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OneLineWritesPerInvoke)]
    public void OneLineToday()
    {
        for (var i = 0; i < OneLineWritesPerInvoke; i++)
        {
            _today.Write(_oneLine);
        }
    }

    [Benchmark(OperationsPerInvoke = BatchWritesPerInvoke)]
    public void BatchToday()
    {
        for (var i = 0; i < BatchWritesPerInvoke; i++)
        {
            _today.Write(_batch);
        }
    }

#if !SCRIBE_BASELINE
    [Benchmark(OperationsPerInvoke = OneLineWritesPerInvoke)]
    public void OneLineAppendOnly()
    {
        for (var i = 0; i < OneLineWritesPerInvoke; i++)
        {
            _appendOnly.Write(_oneLine);
        }
    }

    [Benchmark(OperationsPerInvoke = BatchWritesPerInvoke)]
    public void BatchAppendOnly()
    {
        for (var i = 0; i < BatchWritesPerInvoke; i++)
        {
            _appendOnly.Write(_batch);
        }
    }
#endif

    private static LogRecord Line(DateTime now, int i) => new(
        now,
        LogLevel.Debug,
        LogLineFormat.Format(
            now, LogLevel.Debug, "DictationController",
            $"#{i} stop reason=HotkeyReleased held=00:00:03.412 captured=3.40s device='Microphone (USB Audio)'"));
}
