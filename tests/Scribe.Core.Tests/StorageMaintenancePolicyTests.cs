using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.StorageTime;

namespace Scribe.Core.Tests;

/// <summary>
/// What one maintenance pass keeps and removes, independent of how the pass was scheduled. The clock
/// starts two hours ahead of the wall clock so blobs written by the test are already past the
/// unreferenced-audio grace period.
/// </summary>
public sealed class StorageMaintenancePolicyTests : IDisposable
{
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow.AddHours(2);
    private readonly TempDatabaseFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private StorageMaintenance Create(ScribeDatabase db, StorageMaintenanceOptions? options = null) =>
        new(db, new HistoryRepository(db), new CleanupFailureLog(db), NullLogger.Instance,
            new ManualTimeProvider(_now), options ?? StorageMaintenanceOptions.Default);

    private static CapturedAudio Audio(int samples) => new(new float[samples], 16000);

    private static AppSettings Settings(int retentionDays)
    {
        var settings = AppSettings.CreateDefault();
        settings.HistoryRetentionDays = retentionDays;
        return settings;
    }

    [Fact]
    public void Cleanup_failures_are_pruned_even_when_cleanup_never_succeeds()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var failures = new CleanupFailureLog(db);
        failures.Add(CleanupFailure.New("stale") with { TimestampUtc = _now.AddDays(-8) });
        failures.Add(CleanupFailure.New("fresh") with { TimestampUtc = _now.AddDays(-1) });
        using var maintenance = Create(db);

        var report = maintenance.RunOnce(settings: null)!;

        Assert.Equal(1, report.CleanupFailuresRemoved);
        Assert.Equal("fresh", Assert.Single(failures.GetRecent()).Reason);
    }

    [Fact]
    public void Text_follows_the_retention_setting_and_is_never_deleted_without_it()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var history = new HistoryRepository(db);
        history.Add(new HistoryEntry(0, _now.AddDays(-400), "ancient", 1, 1));
        history.Add(new HistoryEntry(0, _now.AddDays(-40), "older", 1, 1));
        history.Add(new HistoryEntry(0, _now.AddDays(-1), "recent", 1, 1));
        using var maintenance = Create(db);

        // Settings unreadable: text is the user's, so nothing is deleted.
        Assert.Equal(0, maintenance.RunOnce(settings: null)!.HistoryEntriesRemoved);

        // 0 means forever.
        Assert.Equal(0, maintenance.RunOnce(Settings(0))!.HistoryEntriesRemoved);
        Assert.Equal(3, history.GetRecent().Count);

        Assert.Equal(1, maintenance.RunOnce(Settings(90))!.HistoryEntriesRemoved);
        Assert.Equal(1, maintenance.RunOnce(Settings(30))!.HistoryEntriesRemoved);
        Assert.Equal("recent", Assert.Single(history.GetRecent()).Text);
    }

    [Fact]
    public void Audio_older_than_seven_days_is_removed_but_its_text_stays()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var history = new HistoryRepository(db);
        var eightDays = history.Add(new HistoryEntry(0, _now.AddDays(-8), "eight days", 1, 1), Audio(100));
        var sixDays = history.Add(new HistoryEntry(0, _now.AddDays(-6), "six days", 1, 1), Audio(100));
        using var maintenance = Create(db);

        var report = maintenance.RunOnce(Settings(90))!;

        Assert.Equal(1, report.AudioEntriesExpired);
        Assert.Equal(1, report.Unreferenced.BlobsDeleted);
        Assert.Equal(AudioBlobCodec.EncodedLength(100, AudioBlobEncoding.Pcm16), report.Unreferenced.BytesDeleted);
        var entries = history.GetRecent().ToDictionary(e => e.Text);
        Assert.Null(entries["eight days"].AudioBlobId);
        Assert.Null(history.GetAudio(eightDays.AudioBlobId!.Value));
        Assert.Equal(sixDays.AudioBlobId, entries["six days"].AudioBlobId);
        Assert.Equal(new StoredAudioUsage(1, AudioBlobCodec.EncodedLength(100, AudioBlobEncoding.Pcm16)), report.AudioAfter);
    }

    [Fact]
    public void Stored_audio_over_the_cap_is_evicted_oldest_first_and_shared_audio_leaves_every_entry()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var history = new HistoryRepository(db);
        var ids = new List<HistoryEntry>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(history.Add(new HistoryEntry(0, _now.AddHours(-10 + i), $"entry {i}", 1, 1), Audio(1000)));
        }

        // A newer entry sharing the second-oldest recording loses it with the entry that owns it.
        history.Add(new HistoryEntry(0, _now.AddMinutes(-5), "shares entry 1", 1, 1, AudioBlobId: ids[1].AudioBlobId));

        using var maintenance = Create(db, StorageMaintenanceOptions.Default with { MaxStoredAudioBytes = 5000 });

        var report = maintenance.RunOnce(Settings(90))!;

        // 5 recordings of 2004 bytes (header plus 1000 samples) against a 5000-byte cap: the three
        // oldest go, leaving two.
        var blob = AudioBlobCodec.EncodedLength(1000, AudioBlobEncoding.Pcm16);
        Assert.Equal(3, report.Evicted.BlobsDeleted);
        Assert.Equal(3 * blob, report.Evicted.BytesDeleted);
        Assert.Equal(4, report.Evicted.EntriesCleared);
        Assert.Equal(new StoredAudioUsage(2, 2 * blob), report.AudioAfter);

        var entries = history.GetRecent(100).ToDictionary(e => e.Text);
        Assert.Equal(6, entries.Count);
        Assert.Null(entries["entry 0"].AudioBlobId);
        Assert.Null(entries["entry 1"].AudioBlobId);
        Assert.Null(entries["shares entry 1"].AudioBlobId);
        Assert.Null(entries["entry 2"].AudioBlobId);
        Assert.Equal(ids[3].AudioBlobId, entries["entry 3"].AudioBlobId);
        Assert.Equal(ids[4].AudioBlobId, entries["entry 4"].AudioBlobId);
    }

    [Fact]
    public void Damaged_copies_are_pruned_by_the_pass_and_first_seen_times_survive_a_restart()
    {
        using var db = _folder.Open();
        db.Initialize();
        var stale = _folder.DatabasePath + ".corrupt-" + DamagedDatabaseCopies.FormatStamp(_now.AddDays(-20).UtcDateTime);
        var newest = _folder.DatabasePath + ".corrupt-" + DamagedDatabaseCopies.FormatStamp(_now.AddDays(-19).UtcDateTime);
        File.WriteAllBytes(stale, new byte[64]);
        File.WriteAllBytes(newest, new byte[64]);

        // First sight: both are recorded, neither goes, however old their stamps are.
        using (var first = Create(db))
        {
            Assert.Equal(0, first.RunOnce(Settings(90))!.DamagedCopies.FilesDeleted);
        }

        Assert.True(File.Exists(stale));
        Assert.Contains(DamagedDatabaseCopies.FormatStamp(_now.AddDays(-20).UtcDateTime),
            new SettingsRepository(db).Get(DamagedCopyLedger.SettingsKey));

        // A later session, fifteen days on, reads the recorded times back from the database.
        using var later = new StorageMaintenance(db, new HistoryRepository(db), new CleanupFailureLog(db), NullLogger.Instance,
            new ManualTimeProvider(_now.AddDays(15)), StorageMaintenanceOptions.Default);
        var report = later.RunOnce(Settings(90))!;

        Assert.Equal(1, report.DamagedCopies.FilesDeleted);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void An_existing_database_is_converted_to_incremental_once_and_its_file_actually_shrinks()
    {
        LegacyDatabase.Create(_folder.DatabasePath);
        var samples = new float[16_000];
        for (var i = 0; i < 40; i++)
        {
            var blob = LegacyDatabase.InsertFloatBlob(_folder.DatabasePath, samples, _now.AddDays(-30));
            LegacyDatabase.InsertHistory(_folder.DatabasePath, _now.AddDays(-30), $"entry {i}", blob);
        }

        using var db = _folder.Open();
        db.Initialize();
        DatabaseProbe.Checkpoint(db);
        var sizeBefore = _folder.FileLength();
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));

        var options = StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => long.MaxValue / 4,
            ConversionQuietPeriod = TimeSpan.Zero,
        };
        using var maintenance = Create(db, options);

        var report = maintenance.RunOnce(Settings(90))!;

        Assert.Equal(40, report.AudioEntriesExpired);
        Assert.Equal(40, report.Unreferenced.BlobsDeleted);
        Assert.Equal(ReclaimMethod.ConvertedToIncremental, report.Reclaim.Method);
        Assert.Equal(2, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA freelist_count;"));
        Assert.True(_folder.FileLength() < sizeBefore / 4, $"{_folder.FileLength()} vs {sizeBefore}");
        Assert.Equal(0, _folder.FileLength("-wal"));
        Assert.False(report.Reclaim.CheckpointBusy);

        // The text survived the rewrite, and the next pass has nothing left to convert.
        Assert.Equal(40, new HistoryRepository(db).GetRecent(100).Count);
        Assert.Equal(ReclaimMethod.None, maintenance.RunOnce(Settings(90))!.Reclaim.Method);
    }

    [Fact]
    public void Conversion_waits_for_disk_space_rather_than_filling_the_disk()
    {
        LegacyDatabase.Create(_folder.DatabasePath);
        for (var i = 0; i < 10; i++)
        {
            var blob = LegacyDatabase.InsertFloatBlob(_folder.DatabasePath, new float[16_000], _now.AddDays(-30));
            LegacyDatabase.InsertHistory(_folder.DatabasePath, _now.AddDays(-30), $"entry {i}", blob);
        }

        using var db = _folder.Open();
        using var maintenance = Create(db, StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => 1024,
        });

        var report = maintenance.RunOnce(Settings(90))!;

        Assert.Equal(ReclaimMethod.DeferredForDiskSpace, report.Reclaim.Method);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
        Assert.True(DatabaseProbe.QueryInt64(db, "PRAGMA freelist_count;") > 0);
    }

    [Fact]
    public void The_space_check_is_sized_on_live_pages_so_a_mostly_free_database_on_a_tight_disk_still_shrinks()
    {
        const long Mb = 1024 * 1024;
        var margin = StorageMaintenanceOptions.Default.VacuumFreeSpaceMarginBytes;

        // The maintainer's file: 474 MB, of which 37 MB is live, with 900 MB free on the one volume
        // that holds both the database and TEMP.
        Assert.True(StorageMaintenance.HasRoomForVacuum(37 * Mb, databaseFree: 900 * Mb, tempFree: 900 * Mb, margin));

        // About twice the live bytes beside the database and once in TEMP, a tenth more, plus the margin.
        var (database, temp) = StorageMaintenance.VacuumSpaceNeeded(37 * Mb, margin);
        Assert.Equal((2 * (long)Math.Ceiling(37 * Mb * 1.1)) + margin, database);
        Assert.Equal((long)Math.Ceiling(37 * Mb * 1.1) + margin, temp);

        // Sized on the whole file, as before, the same disk would have refused; and a file that
        // really is all live pages still needs that room.
        Assert.False(StorageMaintenance.HasRoomForVacuum(474 * Mb, 900 * Mb, 900 * Mb, margin));

        // Either volume can be the one that is short.
        Assert.False(StorageMaintenance.HasRoomForVacuum(37 * Mb, databaseFree: database - 1, tempFree: 900 * Mb, margin));
        Assert.False(StorageMaintenance.HasRoomForVacuum(37 * Mb, databaseFree: 900 * Mb, tempFree: temp - 1, margin));

        // Free space Windows cannot report does not block it; SQLite rolls back cleanly if it runs out.
        Assert.True(StorageMaintenance.HasRoomForVacuum(37 * Mb, null, null, margin));
    }

    [Fact]
    public void Conversion_goes_ahead_when_free_space_covers_the_live_pages_but_not_the_whole_file()
    {
        LegacyDatabase.Create(_folder.DatabasePath);
        for (var i = 0; i < 40; i++)
        {
            var blob = LegacyDatabase.InsertFloatBlob(_folder.DatabasePath, new float[32_000], _now.AddDays(-30));
            LegacyDatabase.InsertHistory(_folder.DatabasePath, _now.AddDays(-30), $"entry {i}", blob);
        }

        using var db = _folder.Open();
        db.Initialize();
        DatabaseProbe.Checkpoint(db);
        var file = _folder.FileLength();

        // Once the expired audio is gone, this is room for the live pages many times over, but not
        // for the whole file even once.
        var free = file / 2;
        using var maintenance = Create(db, StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            VacuumFreeSpaceMarginBytes = 0,
            ConversionQuietPeriod = TimeSpan.Zero,
            FreeSpaceProbe = _ => free,
        });

        var report = maintenance.RunOnce(Settings(90))!;

        Assert.Equal(40, report.Unreferenced.BlobsDeleted);
        Assert.Equal(ReclaimMethod.ConvertedToIncremental, report.Reclaim.Method);
        Assert.True(_folder.FileLength() < file / 4, $"{_folder.FileLength()} vs {file}");
    }

    [Fact]
    public void A_session_on_default_settings_keeps_all_text_but_still_applies_the_audio_limits()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var history = new HistoryRepository(db);
        history.Add(new HistoryEntry(0, _now.AddDays(-400), "ancient", 1, 1), Audio(1000));
        for (var i = 0; i < 4; i++)
        {
            history.Add(new HistoryEntry(0, _now.AddHours(-4 + i), $"recent {i}", 1, 1), Audio(1000));
        }

        var blob = AudioBlobCodec.EncodedLength(1000, AudioBlobEncoding.Pcm16);
        using var maintenance = Create(db, StorageMaintenanceOptions.Default with { MaxStoredAudioBytes = 2 * blob });

        // The 90 days in use are the default, because the saved settings could not be read.
        maintenance.KeepAllTextThisSession();
        var report = maintenance.RunOnce(Settings(90))!;

        Assert.Equal(0, report.HistoryEntriesRemoved);
        Assert.Equal(0, report.RetentionDays);
        Assert.Contains(history.GetRecent(100), e => e.Text == "ancient" && e.AudioBlobId is null);
        Assert.Equal(1, report.AudioEntriesExpired);
        Assert.Equal(1, report.Unreferenced.BlobsDeleted);
        Assert.Equal(2, report.Evicted.BlobsDeleted);
        Assert.Equal(new StoredAudioUsage(2, 2 * blob), report.AudioAfter);

        // Without the latch the same settings delete that text: the latch is what kept it.
        using var ordinary = Create(db);
        Assert.Equal(1, ordinary.RunOnce(Settings(90))!.HistoryEntriesRemoved);
    }

    [Fact]
    public void Cap_eviction_rechecks_the_cap_so_audio_a_user_deleted_meanwhile_never_costs_extra()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var history = new HistoryRepository(db);
        var entries = new List<HistoryEntry>();
        for (var i = 0; i < 6; i++)
        {
            entries.Add(history.Add(new HistoryEntry(0, _now.AddHours(-6 + i), $"entry {i}", 1, 1), Audio(1000)));
        }

        var blob = AudioBlobCodec.EncodedLength(1000, AudioBlobEncoding.Pcm16);

        // Six recordings against a three-recording cap, so the pass plans to evict the three oldest.
        // Before its slice runs, the user deletes the two newest dictations, which leaves only one
        // recording over the cap.
        var hooked = new HookedHistory(history)
        {
            OnEvictSlice = slice =>
            {
                if (slice == 1)
                {
                    history.Delete(entries[5].Id);
                    history.Delete(entries[4].Id);
                }
            },
        };
        using var maintenance = new StorageMaintenance(db, hooked, new CleanupFailureLog(db), NullLogger.Instance,
            new ManualTimeProvider(_now), StorageMaintenanceOptions.Default with { MaxStoredAudioBytes = 3 * blob });

        var report = maintenance.RunOnce(Settings(90))!;

        Assert.Equal(1, report.Evicted.BlobsDeleted);
        Assert.Equal(new StoredAudioUsage(3, 3 * blob), report.AudioAfter);
        var kept = history.GetRecent(100).ToDictionary(e => e.Text);
        Assert.Equal(4, kept.Count);
        Assert.Null(kept["entry 0"].AudioBlobId);
        Assert.NotNull(kept["entry 1"].AudioBlobId);
        Assert.NotNull(kept["entry 2"].AudioBlobId);
        Assert.NotNull(kept["entry 3"].AudioBlobId);
    }

    [Fact]
    public void Freed_pages_in_an_incremental_database_go_back_to_the_disk_in_bounded_steps()
    {
        using var db = _folder.Open();
        var history = new HistoryRepository(db);
        for (var i = 0; i < 60; i++)
        {
            history.Add(new HistoryEntry(0, _now.AddDays(-10), $"entry {i}", 1, 1), Audio(16_000));
        }

        DatabaseProbe.Checkpoint(db);
        var sizeBefore = _folder.FileLength();
        Assert.Equal(2, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));

        // Steps of 64 pages force several incremental_vacuum statements for about 2 MB of audio.
        using var maintenance = Create(db, StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            IncrementalStepBytes = 1,
        });

        var report = maintenance.RunOnce(Settings(90))!;

        Assert.Equal(60, report.Unreferenced.BlobsDeleted);
        Assert.Equal(ReclaimMethod.Incremental, report.Reclaim.Method);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA freelist_count;"));
        Assert.True(_folder.FileLength() < sizeBefore / 4, $"{_folder.FileLength()} vs {sizeBefore}");
        Assert.Equal(0, _folder.FileLength("-wal"));
    }

    [Fact]
    public void Small_frees_are_left_for_reuse_unless_history_was_cleared()
    {
        using var db = _folder.Open();
        var history = new HistoryRepository(db);
        var entries = Enumerable.Range(0, 20)
            .Select(i => history.Add(new HistoryEntry(0, _now, $"entry {i}", 1, 1), Audio(8_000)))
            .ToList();
        var time = new ManualTimeProvider(_now);
        using var maintenance = new StorageMaintenance(
            db, history, new CleanupFailureLog(db), NullLogger.Instance, time, StorageMaintenanceOptions.Default);
        maintenance.Start(() => Settings(90));

        // One deleted entry frees far less than the threshold: the pages stay for the next recording.
        history.Delete(entries[0].Id);
        var afterDelete = maintenance.RunOnce(Settings(90))!;
        Assert.Equal(ReclaimMethod.None, afterDelete.Reclaim.Method);
        Assert.True(DatabaseProbe.QueryInt64(db, "PRAGMA freelist_count;") > 0);

        // Clear is the user asking for everything back, so it reclaims below the threshold too.
        DatabaseProbe.Checkpoint(db);
        var sizeBefore = _folder.FileLength();
        history.Clear();
        var afterClear = maintenance.RunOnce(Settings(90))!;

        Assert.Equal(ReclaimMethod.Incremental, afterClear.Reclaim.Method);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA freelist_count;"));
        Assert.True(_folder.FileLength() < sizeBefore / 2, $"{_folder.FileLength()} vs {sizeBefore}");
    }

    [Fact]
    public void The_settings_copy_names_the_enforced_limits_without_dashes()
    {
        foreach (var hint in new[]
                 {
                     StorageRetentionPolicy.StoredAudioHint,
                     StorageRetentionPolicy.TextRetentionHint,
                     StorageRetentionPolicy.DataFileHint,
                 })
        {
            Assert.Contains($"{StorageRetentionPolicy.AudioRetentionDays} days", hint);
            Assert.Contains($"{StorageRetentionPolicy.MaxStoredAudioMegabytes} MB", hint);
            Assert.DoesNotContain('\u2014', hint);
            Assert.DoesNotContain('\u2013', hint);
            Assert.DoesNotContain('-', hint);
        }

        Assert.Contains("compact", StorageRetentionPolicy.StoredAudioHint);
        Assert.Contains("Set to 0 to keep text forever", StorageRetentionPolicy.TextRetentionHint);
        Assert.Contains("Audio already saved is removed after", StorageRetentionPolicy.DataFileHint);
        Assert.Equal(7, StorageRetentionPolicy.AudioRetentionDays);
        Assert.Equal(250L * 1024 * 1024, StorageRetentionPolicy.MaxStoredAudioBytes);
        Assert.Equal(14, StorageRetentionPolicy.DamagedCopyRetentionDays);
        Assert.Equal(7, StorageRetentionPolicy.CleanupFailureRetentionDays);
    }
}
