using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Benchmarks;

/// <summary>
/// DATA-A-03 and DATA-O-07 (PerfFlags.ReuseSaveCommands, PerfFlags.DictionaryDiffSave): a Settings Save after one edit in
/// a personal dictionary of <see cref="Rows"/> rows, inside a transaction committed to a real file, as SaveBundle writes
/// it: a command per row today, one reused command per statement, and reused commands that leave out the unchanged rows.
/// Each invocation edits the same row back and forth, so every save changes exactly one row. The same source runs on the
/// baseline, where only today's arm exists.
/// </summary>
[MemoryDiagnoser]
public class DictionarySaveBenchmarks
{
    private string _root = string.Empty;
    private ScribeDatabase _database = null!;
    private List<DictionaryEntry> _editA = null!;
    private List<DictionaryEntry> _editB = null!;
    private bool _flip;

    [Params(200, 2000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "scribe-dictsave-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _database = new ScribeDatabase(new AppPaths(_root), NullLogger<ScribeDatabase>.Instance);
        _database.Initialize();
        var repository = new DictionaryRepository(_database);
        repository.AddRange([.. Enumerable.Range(0, Rows).Select(i => new DictionaryEntry(0, $"spoken form {i:D5}", $"Written {i}", i % 3 != 0, true))]);
        var stored = repository.GetAll();
        _editA = [.. stored];
        _editB = [.. stored];
        _editA[Rows / 2] = _editA[Rows / 2] with { Replacement = "edited A" };
        _editB[Rows / 2] = _editB[Rows / 2] with { Replacement = "edited B" };
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
    public void CommandPerRow()
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        DictionaryRepository.SaveAll(connection, transaction, Next());
        transaction.Commit();
    }

#if !SCRIBE_BASELINE
    [Benchmark]
    public void ReusedCommands()
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        DictionaryRepository.SaveAll(connection, transaction, Next(), DictionaryRepository.SaveCommands.Reused);
        transaction.Commit();
    }

    [Benchmark]
    public void ReusedSkippingUnchanged()
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        DictionaryRepository.SaveAll(connection, transaction, Next(), DictionaryRepository.SaveCommands.ReusedSkippingUnchanged);
        transaction.Commit();
    }
#endif

    private List<DictionaryEntry> Next()
    {
        _flip = !_flip;
        return _flip ? _editA : _editB;
    }
}
