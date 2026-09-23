using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.StorageTime;

namespace Scribe.Core.Tests;

/// <summary>
/// The UI thread must never wait on a VACUUM: Settings save and ratings write on it. These pin how
/// maintenance keeps out of a writer's way: a waiting writer interrupts a preemptible statement, the
/// one-time conversion waits for a quiet database, a pending WAL backfill is never left for a
/// writer's own commit, deletions come in bounded slices, and shutdown interrupts rather than waits.
/// </summary>
public sealed class StoragePreemptionTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    // Never finishes on its own: counts an unbounded recursive sequence, so only an interrupt ends it.
    private const string EndlessQuery =
        "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT count(*) FROM c;";

    private readonly TempDatabaseFolder _folder = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow.AddHours(2);

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task A_writer_waiting_for_the_gate_interrupts_a_preemptible_statement_instead_of_waiting()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var history = new HistoryRepository(db);
        using var running = new ManualResetEventSlim();
        int? statementError = null;
        var laterStatementRan = false;

        var maintenance = new Thread(() =>
        {
            using var connection = db.Open();
            using (db.EnterWriteScope())
            {
                try
                {
                    using (db.EnterPreemptible(connection))
                    {
                        running.Set();
                        Execute(connection, EndlessQuery);
                    }
                }
                catch (SqliteException ex)
                {
                    statementError = ex.SqliteErrorCode;
                }
            }

            // The interrupt ended with the statement; the connection works normally afterwards.
            Execute(connection, "SELECT 1;");
            laterStatementRan = true;
        });
        maintenance.Start();
        Assert.True(running.Wait(Generous));

        try
        {
            // A dictation's history write: it queues for the gate, interrupts, then commits.
            await Task.Run(() => history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "kept", 1, 1), new CapturedAudio([0.1f])))
                .WaitAsync(Generous);
            Assert.True(maintenance.Join(Generous));
        }
        finally
        {
            // Never leave the endless statement spinning if an assertion above failed.
            while (maintenance.IsAlive)
            {
                db.InterruptPreemptible();
                maintenance.Join(10);
            }
        }

        Assert.Equal(9, statementError); // SQLITE_INTERRUPT
        Assert.True(laterStatementRan);
        Assert.Equal("kept", Assert.Single(history.GetRecent()).Text);
        Assert.False(db.InterruptPreemptible()); // nothing is registered any more
    }

    [Fact]
    public async Task A_settings_write_interrupts_a_preemptible_statement_without_taking_the_gate()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var settings = new SettingsRepository(db);
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int? statementError = null;

        // Holds the gate for the whole test: the settings write must never need it.
        var maintenance = new Thread(() =>
        {
            using var connection = db.Open();
            using (db.EnterWriteScope())
            {
                try
                {
                    using (db.EnterPreemptible(connection))
                    {
                        running.Set();
                        Execute(connection, EndlessQuery);
                    }
                }
                catch (SqliteException ex)
                {
                    statementError = ex.SqliteErrorCode;
                }

                release.Wait(Generous);
            }
        });
        maintenance.Start();
        Assert.True(running.Wait(Generous));

        try
        {
            await Task.Run(() => settings.Set("probe", "saved")).WaitAsync(Generous);
            Assert.True(SpinWait.SpinUntil(() => statementError is not null, Generous));
            Assert.Equal(0, db.WaitingWriters);
        }
        finally
        {
            release.Set();
            while (maintenance.IsAlive)
            {
                db.InterruptPreemptible();
                maintenance.Join(10);
            }
        }

        Assert.Equal(9, statementError);
        Assert.Equal("saved", settings.Get("probe"));
    }

    [Fact]
    public void A_preemptible_statement_runs_to_completion_when_no_writer_waits()
    {
        using var db = ScribeDatabase.CreateInMemory();
        using var connection = db.Open();
        using (db.EnterWriteScope())
        using (db.EnterPreemptible(connection))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 100000) SELECT count(*) FROM c;";
            Assert.Equal(100000L, command.ExecuteScalar());
        }
    }

    [Fact]
    public void A_yield_raised_before_a_preemptible_statement_starts_still_interrupts_it_but_only_while_registered()
    {
        using var db = ScribeDatabase.CreateInMemory();
        using var connection = db.Open();
        int? statementError = null;

        var maintenance = new Thread(() =>
        {
            using (db.EnterWriteScope())
            using (db.EnterPreemptible(connection))
            {
                // Nothing runs on the connection yet, so SQLite's interrupt alone would be lost: a
                // settings save or an activation raises its yield once and never repeats it.
                db.RequestYield();
                try
                {
                    Execute(connection, EndlessQuery);
                }
                catch (SqliteException ex)
                {
                    statementError = ex.SqliteErrorCode;
                }
            }
        });
        maintenance.Start();
        var finished = maintenance.Join(Generous);
        while (maintenance.IsAlive)
        {
            db.InterruptPreemptible();
            maintenance.Join(10);
        }

        Assert.True(finished);
        Assert.Equal(9, statementError); // SQLITE_INTERRUPT

        // Once the scope ends the connection is ordinary again: activity no longer stops its statements.
        db.RequestYield();
        using var command = connection.CreateCommand();
        command.CommandText =
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 100000) SELECT count(*) FROM c;";
        Assert.Equal(100000L, command.ExecuteScalar());
    }

    [Fact]
    public void The_conversion_waits_for_a_quiet_database_and_its_own_work_does_not_count_as_activity()
    {
        SeedLegacyDatabase(recent: 4, old: 20);
        using var db = _folder.Open();
        db.Initialize();
        var time = new ManualTimeProvider(_now);
        using var maintenance = Create(db, time, StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => long.MaxValue / 4,
        });
        maintenance.Start(() => SettingsWith(90));
        var timer = time.SingleTimer;

        // Someone wrote just now. The pass still prunes, but the rewrite waits for a lull.
        new SettingsRepository(db).Set("probe", "touched");
        timer.Fire();
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
        Assert.Equal(StorageMaintenanceOptions.Default.ConversionQuietPeriod, timer.DueTime);

        // The first pass's own deletions happened since, and do not restart the quiet window.
        time.Advance(StorageMaintenanceOptions.Default.ConversionQuietPeriod);
        var report = maintenance.RunOnce(SettingsWith(90))!;

        Assert.Equal(ReclaimMethod.ConvertedToIncremental, report.Reclaim.Method);
        Assert.Equal(2, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
    }

    [Fact]
    public async Task A_history_write_arriving_during_the_conversion_preempts_it_and_still_commits()
    {
        SeedLegacyDatabase(recent: 400, old: 40);
        using var db = _folder.Open();
        db.Initialize();
        db.PreemptPollInterval = TimeSpan.FromMilliseconds(1);
        var history = new HistoryRepository(db);
        Task? write = null;
        using var maintenance = Create(db, new ManualTimeProvider(_now), StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => long.MaxValue / 4,
            ConversionQuietPeriod = TimeSpan.Zero,

            // Runs once the VACUUM is registered and about to start: a dictation's history write
            // arrives and queues for the gate, interrupting the VACUUM as soon as it is running.
            BeforePreemptibleStatement = () =>
            {
                write ??= Task.Run(() => history.Add(
                    new HistoryEntry(0, _now, "dictated during vacuum", 1, 1), new CapturedAudio(new float[1600], 16000)));
                Assert.True(SpinWait.SpinUntil(() => db.WaitingWriters == 1, Generous));
            },
        });

        var report = maintenance.RunOnce(SettingsWith(90))!;

        Assert.Equal(ReclaimMethod.Yielded, report.Reclaim.Method);
        await write!.WaitAsync(Generous);
        var saved = Assert.Single(history.GetRecent(1000), e => e.Text == "dictated during vacuum");
        Assert.NotNull(history.GetAudio(saved.AudioBlobId!.Value));
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
        Assert.Equal("ok", DatabaseProbe.QueryString(db, "PRAGMA integrity_check;"));
        Assert.Equal(401, history.GetRecent(1000).Count(e => e.AudioBlobId is not null));
        Assert.False(db.DeferAutoCheckpoint);
    }

    [Fact]
    public void A_settings_write_just_before_the_conversion_makes_it_yield_without_waiting_for_the_gate()
    {
        SeedLegacyDatabase(recent: 4, old: 20);
        using var db = _folder.Open();
        db.Initialize();
        var settings = new SettingsRepository(db);
        using var saved = new ManualResetEventSlim();
        using var maintenance = Create(db, new ManualTimeProvider(_now), StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => long.MaxValue / 4,
            ConversionQuietPeriod = TimeSpan.Zero,

            // Maintenance holds the gate here. The tray's AI toggle saves settings on the UI thread,
            // which must complete now, and the VACUUM must then not start.
            BeforeLastCheck = () =>
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    settings.Set("probe", "toggled");
                    saved.Set();
                });
                Assert.True(saved.Wait(Generous));
            },
        });

        var report = maintenance.RunOnce(SettingsWith(90))!;

        Assert.Equal(ReclaimMethod.Yielded, report.Reclaim.Method);
        Assert.Equal("toggled", settings.Get("probe"));
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
    }

    [Fact]
    public void The_conversion_runs_only_when_the_app_allows_it_and_is_asked_again_right_before()
    {
        SeedLegacyDatabase(recent: 4, old: 20);
        using var db = _folder.Open();
        db.Initialize();
        var answers = new Queue<bool>([false, true, false, true, true]);
        var asked = 0;
        var time = new ManualTimeProvider(_now);
        using var maintenance = Create(db, time, StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => long.MaxValue / 4,
            ConversionQuietPeriod = TimeSpan.Zero,
        });
        maintenance.Start(() => SettingsWith(90), () =>
        {
            asked++;
            return answers.Dequeue();
        });

        // A Settings window is open: deferred, and nothing was rewritten.
        Assert.Equal(ReclaimMethod.DeferredForActivity, maintenance.RunOnce(SettingsWith(90))!.Reclaim.Method);

        // Allowed at the first check, but a dictation started before the VACUUM could begin.
        Assert.Equal(ReclaimMethod.Yielded, maintenance.RunOnce(SettingsWith(90))!.Reclaim.Method);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));

        // Allowed at both checks. Only the conversion asks: after the yield's backoff (zero here)
        // slices, the cap and the checkpoint go ahead without the app's answer.
        Assert.Equal(ReclaimMethod.ConvertedToIncremental, maintenance.RunOnce(SettingsWith(90))!.Reclaim.Method);
        Assert.Equal(5, asked);
        Assert.Equal(2, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
    }

    [Fact]
    public void An_app_answer_that_throws_means_not_now()
    {
        SeedLegacyDatabase(recent: 4, old: 20);
        using var db = _folder.Open();
        db.Initialize();
        using var maintenance = Create(db, new ManualTimeProvider(_now), StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => long.MaxValue / 4,
            ConversionQuietPeriod = TimeSpan.Zero,
        });
        maintenance.Start(() => SettingsWith(90), () => throw new InvalidOperationException("window disposed"));

        var report = maintenance.RunOnce(SettingsWith(90))!;

        Assert.Equal(ReclaimMethod.DeferredForActivity, report.Reclaim.Method);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
    }

    [Fact]
    public async Task Stop_interrupts_the_conversion_closes_the_service_and_reports_stopped()
    {
        SeedLegacyDatabase(recent: 400, old: 40);
        using var db = _folder.Open();
        db.Initialize();
        db.PreemptPollInterval = TimeSpan.FromMilliseconds(1);
        using var vacuumStarting = new ManualResetEventSlim();
        using var maintenance = Create(db, new ManualTimeProvider(_now), StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => long.MaxValue / 4,
            ConversionQuietPeriod = TimeSpan.Zero,
            BeforePreemptibleStatement = vacuumStarting.Set,
        });

        var pass = Task.Run(() => maintenance.RunOnce(SettingsWith(90)));
        Assert.True(vacuumStarting.Wait(Generous));

        Assert.True(await Task.Run(() => maintenance.Stop(Generous)).WaitAsync(Generous));
        var report = await pass.WaitAsync(Generous);

        Assert.Equal(ReclaimMethod.Yielded, report!.Reclaim.Method);
        Assert.True(report.Stopped);
        Assert.Null(maintenance.RunOnce(SettingsWith(90)));
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
        Assert.Equal("ok", DatabaseProbe.QueryString(db, "PRAGMA integrity_check;"));
        Assert.True(maintenance.Stop(TimeSpan.Zero)); // idempotent
    }

    [Fact]
    public void The_vacuum_duration_and_the_longest_write_gate_wait_are_reported()
    {
        SeedLegacyDatabase(recent: 4, old: 20);
        using var db = _folder.Open();
        db.Initialize();
        var history = new HistoryRepository(db);
        using var maintenance = new StorageMaintenance(db, history, new CleanupFailureLog(db), NullLogger.Instance,
            TimeProvider.System, StorageMaintenanceOptions.Default with
            {
                ReclaimThresholdBytes = 64 * 1024,
                FreeSpaceProbe = _ => long.MaxValue / 4,
                ConversionQuietPeriod = TimeSpan.Zero,
            });

        // A writer that had to wait: the gate is held until it is queued.
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using (db.EnterWriteScope())
            {
                holding.Set();
                release.Wait(Generous);
            }
        });
        holder.Start();
        Assert.True(holding.Wait(Generous));
        var writer = new Thread(() => history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "waited", 1, 1)));
        writer.Start();
        Assert.True(SpinWait.SpinUntil(() => db.WaitingWriters == 1, Generous));
        release.Set();
        Assert.True(writer.Join(Generous));
        Assert.True(holder.Join(Generous));

        var report = maintenance.RunOnce(SettingsWith(90))!;

        Assert.Equal(ReclaimMethod.ConvertedToIncremental, report.Reclaim.Method);
        Assert.True(report.Reclaim.VacuumTime > TimeSpan.Zero);
        Assert.True(report.LongestWriteGateWait > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, db.TakeLongestWriteGateWait()); // taken by the pass, then reset
    }

    [Fact]
    public async Task Dispose_during_the_conversion_interrupts_it_and_returns_promptly()
    {
        SeedLegacyDatabase(recent: 400, old: 40);
        using var db = _folder.Open();
        db.Initialize();
        db.PreemptPollInterval = TimeSpan.FromMilliseconds(1);
        using var vacuumStarting = new ManualResetEventSlim();
        var maintenance = Create(db, new ManualTimeProvider(_now), StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => long.MaxValue / 4,
            ConversionQuietPeriod = TimeSpan.Zero,
            BeforePreemptibleStatement = vacuumStarting.Set,
        });

        var pass = Task.Run(() => maintenance.RunOnce(SettingsWith(90)));
        Assert.True(vacuumStarting.Wait(Generous));

        await Task.Run(maintenance.Dispose).WaitAsync(Generous);
        var report = await pass.WaitAsync(Generous);

        Assert.Equal(ReclaimMethod.Yielded, report!.Reclaim.Method);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
        Assert.Equal("ok", DatabaseProbe.QueryString(db, "PRAGMA integrity_check;"));
    }

    [Fact]
    public void A_connection_opened_while_the_vacuum_runs_leaves_the_backfill_to_maintenance()
    {
        SeedLegacyDatabase(recent: 400, old: 40);
        using var db = _folder.Open();
        db.Initialize();
        long? duringVacuum = null;

        // Whoever opens a connection mid-VACUUM, a settings save waiting out its final copy say,
        // must not copy the rebuilt database back from the WAL on its own thread at its next commit.
        db.PreemptibleProgressHook = () =>
        {
            if (duringVacuum is null)
            {
                var probe = new Thread(() => duringVacuum = DatabaseProbe.QueryInt64(db, "PRAGMA wal_autocheckpoint;"));
                probe.Start();
                probe.Join(Generous);
            }
        };
        using var maintenance = Create(db, new ManualTimeProvider(_now), StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => long.MaxValue / 4,
            ConversionQuietPeriod = TimeSpan.Zero,
        });

        var report = maintenance.RunOnce(SettingsWith(90))!;

        Assert.Equal(ReclaimMethod.ConvertedToIncremental, report.Reclaim.Method);
        Assert.Equal(0, duringVacuum);

        // Caught up by the pass's own checkpoint, after which writers checkpoint as usual again.
        Assert.False(db.DeferAutoCheckpoint);
        Assert.Equal(1000, DatabaseProbe.QueryInt64(db, "PRAGMA wal_autocheckpoint;"));
    }

    [Fact]
    public void An_interrupted_vacuum_gives_writers_their_automatic_checkpoints_back_at_once()
    {
        SeedLegacyDatabase(recent: 400, old: 40);
        using var db = _folder.Open();
        db.Initialize();

        // A reader parked on an older snapshot keeps the pass's own PASSIVE backfill from catching
        // up (the pass's deletions come after that snapshot), so only restoring the setting when the
        // VACUUM rolls back can turn it off again.
        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _folder.DatabasePath,
            Pooling = false,
        }.ToString());
        reader.Open();
        using var query = reader.CreateCommand();
        query.CommandText = "SELECT id FROM history;";
        using var rows = query.ExecuteReader();
        Assert.True(rows.Read());

        var yielded = false;
        db.PreemptibleProgressHook = () =>
        {
            if (!yielded)
            {
                yielded = true;
                var dictation = new Thread(db.RequestYield);
                dictation.Start();
                dictation.Join(Generous);
            }
        };
        using var maintenance = Create(db, new ManualTimeProvider(_now), StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => long.MaxValue / 4,
            ConversionQuietPeriod = TimeSpan.Zero,
        });

        var report = maintenance.RunOnce(SettingsWith(90))!;

        Assert.Equal(ReclaimMethod.Yielded, report.Reclaim.Method);
        Assert.False(db.DeferAutoCheckpoint);
        Assert.Equal(1000, DatabaseProbe.QueryInt64(db, "PRAGMA wal_autocheckpoint;"));
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
    }

    [Fact]
    public void While_the_WAL_backfill_is_owed_new_connections_leave_checkpointing_to_maintenance()
    {
        SeedLegacyDatabase(recent: 4, old: 20);
        using var db = _folder.Open();
        db.Initialize();
        using var maintenance = Create(db, new ManualTimeProvider(_now), StorageMaintenanceOptions.Default with
        {
            ReclaimThresholdBytes = 64 * 1024,
            FreeSpaceProbe = _ => long.MaxValue / 4,
            ConversionQuietPeriod = TimeSpan.Zero,
            CheckpointBusyTimeout = TimeSpan.Zero,
        });

        // A reader parked on an older snapshot keeps the VACUUM's frames from being copied back.
        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _folder.DatabasePath,
            Pooling = false,
        }.ToString());
        reader.Open();
        using var query = reader.CreateCommand();
        query.CommandText = "SELECT id FROM history;";
        var rows = query.ExecuteReader();
        Assert.True(rows.Read());

        var converted = maintenance.RunOnce(SettingsWith(90))!;

        Assert.Equal(ReclaimMethod.ConvertedToIncremental, converted.Reclaim.Method);
        Assert.True(converted.Reclaim.CheckpointBusy);
        Assert.True(db.DeferAutoCheckpoint);

        // A writer's commit now never runs the big copy itself.
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA wal_autocheckpoint;"));

        rows.Dispose();
        var caughtUp = maintenance.RunOnce(SettingsWith(90))!;

        Assert.False(caughtUp.Reclaim.CheckpointBusy);
        Assert.False(db.DeferAutoCheckpoint);
        Assert.Equal(1000, DatabaseProbe.QueryInt64(db, "PRAGMA wal_autocheckpoint;"));
        Assert.Equal(0, _folder.FileLength("-wal"));
    }

    [Fact]
    public void A_large_WAL_left_by_an_earlier_session_is_backfilled_by_maintenance_not_the_first_writer()
    {
        using (var first = _folder.Open())
        {
            first.Initialize();
        }

        // An earlier session that ended before its backfill: about 20 MB of frames nobody copied
        // back. The raw connection stays open so SQLite's checkpoint-on-last-close does not run.
        using var leftover = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _folder.DatabasePath,
            Pooling = false,
        }.ToString());
        leftover.Open();
        Execute(leftover, "PRAGMA wal_autocheckpoint=0; PRAGMA synchronous=OFF;");
        Execute(leftover,
            """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 160)
            INSERT INTO audio_blobs (sample_rate, samples, created_utc) SELECT 16000, zeroblob(131072), '2026-01-01' FROM n;
            """);
        Assert.True(_folder.FileLength("-wal") > 16L * 1024 * 1024);

        using var db = _folder.Open();
        db.Initialize();

        Assert.True(db.DeferAutoCheckpoint);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA wal_autocheckpoint;"));

        using var maintenance = Create(db, new ManualTimeProvider(_now), StorageMaintenanceOptions.Default);
        var report = maintenance.RunOnce(SettingsWith(90))!;

        Assert.False(report.Reclaim.CheckpointBusy);
        Assert.False(db.DeferAutoCheckpoint);
        Assert.Equal(1000, DatabaseProbe.QueryInt64(db, "PRAGMA wal_autocheckpoint;"));
        Assert.Equal(0, _folder.FileLength("-wal"));
    }

    [Fact]
    public void Deletion_slices_are_bounded_by_stored_bytes_and_by_count()
    {
        var blobs = new[]
        {
            new StoredAudioBlob(1, 3, false),
            new StoredAudioBlob(2, 3, false),
            new StoredAudioBlob(3, 3, false),
            new StoredAudioBlob(4, 10, false), // larger than a slice: a slice of its own
            new StoredAudioBlob(5, 1, false),
            new StoredAudioBlob(6, 1, false),
            new StoredAudioBlob(7, 1, false),
        };

        var slices = StorageMaintenance.Slices(blobs, maxBytes: 6, maxCount: 2).ToList();

        Assert.Equal(
            new[] { new long[] { 1, 2 }, [3], [4], [5, 6], [7] },
            slices);
        Assert.Empty(StorageMaintenance.Slices([], 6, 2));
    }

    private StorageMaintenance Create(ScribeDatabase db, ManualTimeProvider time, StorageMaintenanceOptions options) =>
        new(db, new HistoryRepository(db), new CleanupFailureLog(db), NullLogger.Instance, time, options);

    private static AppSettings SettingsWith(int retentionDays)
    {
        var settings = AppSettings.CreateDefault();
        settings.HistoryRetentionDays = retentionDays;
        return settings;
    }

    // A v7 file with float32 audio: "recent" entries keep theirs through the pass (live data the
    // VACUUM must copy), "old" ones lose it (free pages that make the conversion worth doing).
    private void SeedLegacyDatabase(int recent, int old) =>
        LegacyDatabase.Seed(_folder.DatabasePath, _now, recent, old);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
