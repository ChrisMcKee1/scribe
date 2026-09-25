using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.CleanupLogging;

namespace Scribe.Core.Tests;

/// <summary>
/// PRIVACY.md says Scribe tries to empty the write-ahead log when it closes normally, and that a database still in use
/// then can keep an earlier copy of deleted content in the log until the log is next emptied. It used to say the close
/// empties the log, and the close cannot promise that: it skips its checkpoint without the write gate, and SQLite reports
/// the checkpoint busy, leaving the log as it was, while another connection still uses it. Each case closes a real
/// database, reads the files, and reads the line the close logs from the checkpoint's result row.
/// </summary>
public sealed class ShutdownCheckpointTests
{
    private const string ClosedLine = "Closed the database";

    [Fact]
    public void A_quiet_close_empties_the_log_and_says_so()
    {
        const string Canary = "zq shutdown canary 7a1c quiet close";
        using var folder = new TempDatabaseFolder();
        var log = new CapturingLogger<ScribeDatabase>();
        var db = new ScribeDatabase(new AppPaths(folder.Root), log);
        var history = new HistoryRepository(db);
        history.Delete(history.Add(Entry(Canary)).Id);
        Assert.True(FileContains(WalPath(folder), Canary), "The entry never reached the WAL, so its absence would prove nothing.");

        db.Dispose();

        var line = Assert.Single(log.Entries, entry => entry.Message.StartsWith(ClosedLine, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("final write-ahead log checkpoint: Emptied", line.Message, StringComparison.Ordinal);
        Assert.EndsWith("The log file afterwards: gone.", line.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(WalPath(folder)));
        Assert.False(FileContains(folder.DatabasePath, Canary), "The deleted text is still readable in scribe.db.");
    }

    [Fact]
    public void A_close_while_another_connection_reads_an_older_snapshot_leaves_the_deleted_text_in_the_log()
    {
        const string Canary = "zq shutdown canary 3e9b busy close";
        using var folder = new TempDatabaseFolder();
        var log = new CapturingLogger<ScribeDatabase>();
        var db = new ScribeDatabase(new AppPaths(folder.Root), log)
        {
            ShutdownCheckpointBusyTimeout = TimeSpan.FromMilliseconds(50),
        };
        var history = new HistoryRepository(db);
        var doomed = history.Add(Entry(Canary));

        // Another program reading the database, still on the snapshot from before the deletion.
        using (var reader = OpenOutsideConnection(folder.DatabasePath))
        using (var snapshot = reader.BeginTransaction(deferred: true))
        {
            Assert.Equal(1L, Count(reader, snapshot));
            history.Delete(doomed.Id);

            db.Dispose();

            var line = Assert.Single(log.Entries, entry => entry.Message.StartsWith(ClosedLine, StringComparison.Ordinal));
            Assert.Equal(LogLevel.Information, line.Level);
            Assert.Contains("final write-ahead log checkpoint: Busy", line.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("The log file afterwards: gone.", line.Message, StringComparison.Ordinal);
            Assert.True(FileContains(WalPath(folder), Canary), "The busy close emptied the log after all.");
        }

        // The log is next emptied when the last connection closes: SQLite folds it in and deletes it.
        Assert.False(File.Exists(WalPath(folder)));
        Assert.False(FileContains(folder.DatabasePath, Canary), "The deleted text is still readable in scribe.db.");
    }

    [Fact]
    public void A_close_that_cannot_have_the_write_gate_skips_the_checkpoint_and_says_so()
    {
        using var folder = new TempDatabaseFolder();
        var log = new CapturingLogger<ScribeDatabase>();
        var db = new ScribeDatabase(new AppPaths(folder.Root), log) { ShutdownGateTimeout = TimeSpan.FromMilliseconds(50) };
        new HistoryRepository(db).Add(Entry("an entry that stays"));

        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            using (db.EnterWriteScope())
            {
                holding.Set();
                release.Wait();
            }
        });
        writer.Start();
        holding.Wait();
        try
        {
            db.Dispose();
        }
        finally
        {
            release.Set();
            writer.Join();
        }

        // No other connection had the database open, so closing the last one still folded the log in and deleted it.
        var line = Assert.Single(log.Entries, entry => entry.Message.StartsWith(ClosedLine, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("without its final write-ahead log checkpoint", line.Message, StringComparison.Ordinal);
        Assert.EndsWith("The log file afterwards: gone.", line.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(WalPath(folder)));
    }

    private static string WalPath(TempDatabaseFolder folder) => folder.DatabasePath + "-wal";

    private static HistoryEntry Entry(string text) =>
        new(0, DateTimeOffset.UtcNow, text, 1_000, 50, CleanupMilliseconds: null, TargetApp: null);

    // Unpooled, so disposing it really closes it, the way another program's connection ends.
    private static SqliteConnection OpenOutsideConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long Count(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM history;";
        return (long)command.ExecuteScalar()!;
    }

    // Read with sharing, since a connection may still have the files open. SQLite stores TEXT as UTF-8 here.
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
