using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Benchmarks;

/// <summary>
/// DATA-O-08 (PerfFlags.HistoryStageTiming) and DATA-A-10 against a real file: a History page read (the newest 20 entries), a
/// text-only history write committed to disk, the same write with its stages timed, and one pooled open with its
/// configuration batch. The same source runs on the baseline, where only today's arms exist.
/// </summary>
[MemoryDiagnoser]
public class HistoryStoreBenchmarks
{
    private static readonly HistoryEntry Entry =
        new(0, new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero), "a sentence someone said, about this long", 3400, 120, 40, "notepad", null, "parakeet");

    private string _root = string.Empty;
    private ScribeDatabase _database = null!;
    private HistoryRepository _today = null!;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "scribe-history-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _database = new ScribeDatabase(new AppPaths(_root), NullLogger<ScribeDatabase>.Instance);
        _database.Initialize();
        _today = new HistoryRepository(_database);
        for (var i = 0; i < 200; i++)
        {
            _today.Add(Entry with { TimestampUtc = Entry.TimestampUtc.AddMinutes(i) });
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _database.Dispose();
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
    public int RecentWithTodaysProbes() => _today.GetRecent(20).Count;

    [Benchmark]
    public long AddWithTodaysProbes() => _today.Add(Entry).Id;

    [Benchmark]
    public void PooledOpen()
    {
        using var connection = _database.Open();
    }

#if !SCRIBE_BASELINE
    [Benchmark]
    public long AddWithStagesTimed()
    {
        var stages = new HistoryWriteStages();
        stages.Start();
        return _today.Add(Entry, null, stages).Id;
    }
#endif
}
