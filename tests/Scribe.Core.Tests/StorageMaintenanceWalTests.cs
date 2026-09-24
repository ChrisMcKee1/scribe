using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.StorageTime;

namespace Scribe.Core.Tests;

/// <summary>
/// PRIVACY.md promises that after history, a recording or a cleanup failure sample is deleted, storage
/// maintenance empties the write-ahead log, which can still hold the pages as they were before the
/// deletion. It used to checkpoint only when enough space was free to be worth reclaiming, so a single
/// deleted entry stayed readable in scribe.db-wal until the process exited. Each case drives a real
/// maintenance pass over a real database file and reads the files themselves; nothing checkpoints by hand.
/// </summary>
public sealed class StorageMaintenanceWalTests
{
    private static readonly StorageMaintenanceOptions Options = StorageMaintenanceOptions.Default;

    [Fact]
    public void Deleting_a_history_entry_has_the_pass_it_schedules_empty_the_wal()
    {
        const string Canary = "zq wal canary 41d7 said something private";
        using var folder = new TempDatabaseFolder();
        using var db = folder.Open();
        var history = new HistoryRepository(db);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var maintenance = Create(db, history, time);
        maintenance.Start(AppSettings.CreateDefault);
        var timer = time.SingleTimer;

        history.Add(Entry("an entry that stays"));
        var doomed = history.Add(Entry(Canary));
        Assert.True(FileContains(folder.DatabasePath + "-wal", Canary), "The entry never reached the WAL, so its absence would prove nothing.");

        history.Delete(doomed.Id);

        // The delete itself brings the pass forward; the pass is what empties the log.
        Assert.Equal(Options.TriggerDelay, timer.DueTime);
        Assert.True(FileContains(folder.DatabasePath + "-wal", Canary), "Nothing may have emptied the WAL before the pass.");
        timer.Fire();

        Assert.Equal(0, folder.FileLength("-wal"));
        Assert.False(FileContains(folder.DatabasePath, Canary), "The deleted text is still readable in scribe.db.");
        Assert.Single(history.GetRecent(10));
    }

    [Fact]
    public void A_pass_that_deletes_old_history_empties_the_wal_before_it_ends()
    {
        const string Canary = "zq retention canary 8e02 from long ago";
        using var folder = new TempDatabaseFolder();
        using var db = folder.Open();
        var history = new HistoryRepository(db);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var maintenance = Create(db, history, time);
        history.Add(Entry(Canary) with { TimestampUtc = time.GetUtcNow().AddDays(-200) });
        history.Add(Entry("a recent entry that stays"));
        Assert.True(FileContains(folder.DatabasePath + "-wal", Canary));

        var report = maintenance.RunOnce(RetentionDays(90))!;

        Assert.Equal(1, report.HistoryEntriesRemoved);
        Assert.Equal(0, folder.FileLength("-wal"));
        Assert.False(FileContains(folder.DatabasePath, Canary));
    }

    [Fact]
    public void Clearing_the_cleanup_failure_samples_has_the_next_pass_empty_the_wal()
    {
        const string Canary = "zq failure sample canary 5b3a raw dictation";
        using var folder = new TempDatabaseFolder();
        using var db = folder.Open();
        var failures = new CleanupFailureLog(db);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var maintenance = new StorageMaintenance(db, new HistoryRepository(db), failures, NullLogger.Instance, time, Options);
        maintenance.Start(AppSettings.CreateDefault);
        var timer = time.SingleTimer;
        failures.Add(CleanupFailure.New("timed out", "AzureFoundry", "deployment", Canary));
        Assert.True(FileContains(folder.DatabasePath + "-wal", Canary));

        Assert.Equal(1, failures.Clear());
        Assert.Equal(Options.TriggerDelay, timer.DueTime);
        timer.Fire();

        Assert.Equal(0, folder.FileLength("-wal"));
        Assert.False(FileContains(folder.DatabasePath, Canary));
    }

    [Fact]
    public void A_pass_that_yields_to_a_dictation_leaves_the_checkpoint_owed_to_the_next()
    {
        // The TRUNCATE takes the write gate, so it waits for the same idle moments as the rest of the
        // heavy work: a pass that yields skips it, and the pass after the backoff pays it.
        const string Canary = "zq yielded canary 2c9f dictated just now";
        using var folder = new TempDatabaseFolder();
        using var db = folder.Open();
        var yielded = 0;
        var history = new HookedHistory(new HistoryRepository(db))
        {
            OnClearAudio = () =>
            {
                if (Interlocked.Exchange(ref yielded, 1) == 0)
                {
                    db.RequestYield();
                }
            },
        };
        var repository = new HistoryRepository(db);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var maintenance = new StorageMaintenance(db, history, new CleanupFailureLog(db), NullLogger.Instance, time, Options);
        maintenance.Start(AppSettings.CreateDefault);
        var timer = time.SingleTimer;
        var doomed = repository.Add(Entry(Canary));
        repository.Delete(doomed.Id);

        timer.Fire();

        Assert.True(maintenance.LastReport!.Yielded);
        Assert.True(FileContains(folder.DatabasePath + "-wal", Canary), "A pass that yielded must not have taken the gate.");

        var backoff = maintenance.LastReport.RetryIn;
        Assert.Equal(backoff, timer.DueTime);
        time.Advance(backoff);
        timer.Fire();

        Assert.False(maintenance.LastReport!.Yielded);
        Assert.Equal(0, folder.FileLength("-wal"));
        Assert.False(FileContains(folder.DatabasePath, Canary));
    }

    [Fact]
    public void A_pass_with_nothing_deleted_leaves_the_wal_alone()
    {
        // Owed only by a deletion: an ordinary pass must not start taking the write gate for a checkpoint.
        using var folder = new TempDatabaseFolder();
        using var db = folder.Open();
        var history = new HistoryRepository(db);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var maintenance = Create(db, history, time);
        history.Add(Entry("an entry that stays"));
        var walBefore = folder.FileLength("-wal");
        Assert.True(walBefore > 0);

        maintenance.RunOnce(RetentionDays(90));

        Assert.Equal(walBefore, folder.FileLength("-wal"));
        Assert.Equal(ReclaimMethod.None, maintenance.LastReport!.Reclaim.Method);
    }

    private static StorageMaintenance Create(ScribeDatabase db, IHistoryMaintenance history, ManualTimeProvider time) =>
        new(db, history, new CleanupFailureLog(db), NullLogger.Instance, time, Options);

    private static AppSettings RetentionDays(int days)
    {
        var settings = AppSettings.CreateDefault();
        settings.HistoryRetentionDays = days;
        return settings;
    }

    private static HistoryEntry Entry(string text) =>
        new(0, DateTimeOffset.UtcNow, text, 1_000, 50, CleanupMilliseconds: null, TargetApp: null);

    // Read with sharing, since the database still has the files open. SQLite stores TEXT as UTF-8 here.
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
