using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Xunit;

namespace Scribe.Core.Tests;

/// <summary>
/// Guards the startup corruption check and salvage in <see cref="ScribeDatabase"/>. A malformed
/// database file must never silently serve partial data (the "my dictionary got wiped" failure
/// mode): startup detects it, moves the damaged file aside, and rebuilds a fresh database from
/// every row that is still readable.
/// </summary>
public sealed class DatabaseSalvageTests : IDisposable
{
    private readonly string _root;

    public DatabaseSalvageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "scribe-salvage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private ScribeDatabase CreateFileDatabase() =>
        new(new AppPaths(_root), NullLogger<ScribeDatabase>.Instance);

    private string DbPath => Path.Combine(_root, "scribe.db");

    [Fact]
    public void Healthy_database_passes_the_check_untouched()
    {
        using (var db = CreateFileDatabase())
        {
            new DictionaryRepository(db).Add(DictionaryEntry.New("spoken", "Written"));
        }

        using (var db = CreateFileDatabase())
        {
            db.Initialize();
            Assert.False(db.RepairedAtStartup);
            var entries = new DictionaryRepository(db).GetAll();
            Assert.Single(entries);
        }

        Assert.Empty(Directory.GetFiles(_root, "scribe.db.corrupt-*"));
    }

    [Fact]
    public void Corrupted_database_is_rebuilt_and_readable_rows_survive()
    {
        using (var db = CreateFileDatabase())
        {
            var dictionary = new DictionaryRepository(db);
            dictionary.Add(DictionaryEntry.New("jeffrey", "Geoffery"));
            dictionary.Add(DictionaryEntry.New("see sam", "CSAM"));

            // Bulk history pushes the file to many pages so damage at the tail lands far away
            // from the dictionary rows near the front of the file.
            var history = new HistoryRepository(db);
            for (var i = 0; i < 50; i++)
            {
                history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, new string('x', 4000), 1000, 50, null));
            }
        }

        CorruptTail(DbPath, pages: 4);

        using (var db = CreateFileDatabase())
        {
            db.Initialize();
            Assert.True(db.RepairedAtStartup);

            // The dictionary — the data the user actually curates — survived intact.
            var entries = new DictionaryRepository(db).GetAll();
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.Replacement == "Geoffery");
            Assert.Contains(entries, e => e.Replacement == "CSAM");

            // The rebuilt database is fully usable for writes.
            new DictionaryRepository(db).Add(DictionaryEntry.New("stew", "STU"));
        }

        // The damaged original was kept beside the database for manual recovery.
        Assert.Single(Directory.GetFiles(_root, "scribe.db.corrupt-*"));
    }

    [Fact]
    public void Unreadable_file_is_moved_aside_and_a_fresh_database_starts()
    {
        // Not even a SQLite header — open/quick_check fails outright, so nothing can be salvaged.
        File.WriteAllBytes(DbPath, new byte[8192]);

        using var db = CreateFileDatabase();
        db.Initialize();

        var dictionary = new DictionaryRepository(db);
        dictionary.Add(DictionaryEntry.New("spoken", "Written"));
        Assert.Single(dictionary.GetAll());
        Assert.Single(Directory.GetFiles(_root, "scribe.db.corrupt-*"));
    }

    // Overwrites the last few pages of the file with garbage. The first page (header + schema) and
    // the early pages holding the small tables stay intact, mimicking the partial corruption seen
    // in the field where the dictionary read fine on one query and failed on the next.
    private static void CorruptTail(string path, int pages)
    {
        const int pageSize = 4096;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var garbage = new byte[pageSize * pages];
        new Random(42).NextBytes(garbage);
        stream.Seek(-garbage.Length, SeekOrigin.End);
        stream.Write(garbage);
    }
}
