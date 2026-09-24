using System.Text;
using Microsoft.Data.Sqlite;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// A deleted transcript must not stay readable in the database file. The bundled e_sqlite3 is built
/// without SQLITE_SECURE_DELETE, so without the pragma SQLite leaves deleted rows' bytes in their
/// page, or in a freed page, until something overwrites them. These tests pin the setting on every
/// connection the database hands out and, more to the point, the bytes on disk.
/// </summary>
public sealed class SecureDeleteTests
{
    [Fact]
    public void The_bundled_sqlite_does_not_turn_secure_delete_on_by_itself()
    {
        // The reason ScribeDatabase sets it: a plain connection to the same native library reads 0. If
        // a future bundle turns it on by default, this fails, and the pragma is merely redundant.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        Assert.Equal(0L, Query(connection, "PRAGMA secure_delete;"));
    }

    [Fact]
    public void Every_connection_the_database_hands_out_zeroes_what_it_deletes()
    {
        using var folder = new TempDatabaseFolder();
        using var file = folder.Open();
        using var memory = ScribeDatabase.CreateInMemory();

        foreach (var database in new[] { file, memory })
        {
            // Twice, so a connection taken back out of the pool is checked as well as a new one.
            for (var i = 0; i < 2; i++)
            {
                using var connection = database.Open();
                Assert.Equal(1L, Query(connection, "PRAGMA secure_delete;"));
            }
        }
    }

    [Fact]
    public void A_deleted_history_entry_leaves_no_copy_of_its_text_in_the_database_file()
    {
        const string Canary = "zq secure delete canary 7c1e said something private";
        using var folder = new TempDatabaseFolder();
        using (var database = folder.Open())
        {
            var history = new HistoryRepository(database);
            history.Add(Entry("an entry that stays"));
            var doomed = history.Add(Entry(Canary));
            history.Add(Entry("another entry that stays"));
            DatabaseProbe.Checkpoint(database);
            Assert.True(
                FileContains(folder.DatabasePath, Canary),
                "The canary never reached the file, so its absence below would prove nothing.");

            history.Delete(doomed.Id);
            DatabaseProbe.Checkpoint(database);

            Assert.False(FileContains(folder.DatabasePath, Canary), "The deleted transcript is still readable in scribe.db.");
            Assert.False(FileContains(folder.DatabasePath + "-wal", Canary), "The deleted transcript is still readable in the WAL.");
            Assert.Equal(2, history.GetRecent(10).Count());
        }
    }

    [Fact]
    public void Clearing_history_leaves_no_copy_of_any_transcript_in_the_database_file()
    {
        using var folder = new TempDatabaseFolder();
        using (var database = folder.Open())
        {
            var history = new HistoryRepository(database);
            var canaries = Enumerable.Range(0, 40).Select(i => $"zq cleared canary {i:D2} with enough words to fill a page").ToList();
            foreach (var canary in canaries)
            {
                history.Add(Entry(canary));
            }

            DatabaseProbe.Checkpoint(database);
            Assert.All(canaries, canary => Assert.True(FileContains(folder.DatabasePath, canary)));

            history.Clear();
            DatabaseProbe.Checkpoint(database);

            Assert.DoesNotContain(canaries, canary => FileContains(folder.DatabasePath, canary));
        }
    }

    [Fact]
    public void Clearing_history_deletes_recordings_in_slices_so_the_log_stays_near_one_slice()
    {
        // With secure delete every freed page is written again, as zeros, and a WAL holds a whole
        // transaction, so one statement for every recording needed their whole size in WAL at once.
        const int Recordings = 24;
        const int SamplesEach = 256 * 1024;
        using var folder = new TempDatabaseFolder();
        using (var database = folder.Open())
        {
            var history = new HistoryRepository(database) { ClearSliceBytes = 512 * 1024 };
            for (var i = 0; i < Recordings; i++)
            {
                history.Add(Entry($"entry {i} with a recording"), new CapturedAudio(Samples(SamplesEach, i)));
            }

            var stored = history.GetStoredAudioUsage();
            Assert.Equal(Recordings, stored.Blobs);
            DatabaseProbe.Checkpoint(database);

            history.Clear();

            Assert.Empty(history.GetRecent(100));
            Assert.Equal(default, history.GetStoredAudioUsage());

            // One slice plus the automatic checkpoint's threshold (1,000 pages) of headroom, and far below
            // what a single statement for all of them wrote into the WAL.
            var wal = folder.FileLength("-wal");
            Assert.True(wal < stored.Bytes / 2, $"The WAL grew to {wal:N0} bytes for {stored.Bytes:N0} bytes of recordings.");
        }
    }

    private static float[] Samples(int count, int seed)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = (((i * 31) + seed) % 200 - 100) / 128f;
        }

        return samples;
    }

    private static HistoryEntry Entry(string text) =>
        new(0, DateTimeOffset.UtcNow, text, 1_000, 50, CleanupMilliseconds: null, TargetApp: null);

    private static long Query(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // Read with sharing, since the database still has the file open. SQLite stores TEXT as UTF-8 here.
    private static bool FileContains(string path, string text)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;
    }
}
