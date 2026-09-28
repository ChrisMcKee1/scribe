using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;

namespace Scribe.Benchmarks;

/// <summary>
/// DATA-O-09 (PerfFlags.BackgroundDiagnosticsExport): what "Save diagnostics..." holds the dispatcher for. Today the
/// dispatcher writes the zip itself; with the flag it only hands the zip to <see cref="DiagnosticsExport"/>, whose worker
/// then writes the same zip (the handing-over arm waits for it outside the measurement). Seven days of about 4 MB of
/// realistic lines, some in a format the copy redacts. The same source runs on the baseline, where only today's arm exists.
/// </summary>
[MemoryDiagnoser]
[IterationCount(5)]
public class DiagnosticsExportBenchmarks
{
    private const int Days = 7;
    private const int BytesPerDay = 4 * 1024 * 1024;

    private static readonly DateOnly Today = new(2026, 9, 21);
    private string _root = string.Empty;
    private string _logs = string.Empty;
#if !SCRIBE_BASELINE
    private DiagnosticsExport _export = null!;
    private Task<DiagnosticsExportResult>? _pending;
#endif

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "scribe-export-bench-" + Guid.NewGuid().ToString("N"));
        _logs = Directory.CreateDirectory(Path.Combine(_root, "logs")).FullName;
        for (var day = 0; day < Days; day++)
        {
            using var writer = new StreamWriter(ScribeLogFiles.PathFor(_logs, Today.AddDays(-day)));
            var written = 0L;
            for (var i = 0; written < BytesPerDay; i++)
            {
                var line = i % 40 == 0
                    ? $"14:05:{i % 60:D2}.123 [Debug] TranscriptionService: Decoded 4520 ms of audio in 210 ms (RTF 0.05): \"a sentence someone said {i}\""
                    : $"14:05:{i % 60:D2}.123 [Information] DictationController: #{i} finished with outcome Typed after {i % 900} ms";
                writer.WriteLine(line);
                written += line.Length + 2;
            }
        }

#if !SCRIBE_BASELINE
        _export = new DiagnosticsExport(NullLogger<DiagnosticsExport>.Instance);
#endif
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

    [Benchmark(Baseline = true)]
    public long DispatcherWritesTheZip() =>
        DiagnosticsBundle.Create(_logs, Path.Combine(_root, "today.zip"), "report", Today).Bytes;

#if !SCRIBE_BASELINE
    [Benchmark]
    public void DispatcherHandsTheZipToAWorker() =>
        _pending = _export.RunAsync(_logs, Path.Combine(_root, "worker.zip"), "report", Today);

    [IterationCleanup(Target = nameof(DispatcherHandsTheZipToAWorker))]
    public void WaitForTheWorker() => _pending?.GetAwaiter().GetResult();
#endif
}
