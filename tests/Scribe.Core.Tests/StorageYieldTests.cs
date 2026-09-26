using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.StorageTime;
using Xunit.Abstractions;

namespace Scribe.Core.Tests;

/// <summary>
/// Maintenance yields to dictation. A dictation starting, or its history write arriving, stops heavy
/// work at once: a running VACUUM is interrupted and rolls back, a batch of deletions ends at its next
/// slice, and the write gets the gate as soon as that step ends instead of queueing behind the whole job.
/// Heavy work comes back only after a backoff that doubles with each consecutive yield, and only once
/// the app is idle and the database quiet again.
/// </summary>
public sealed class StorageYieldTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Quiet = StorageMaintenanceOptions.Default.ConversionQuietPeriod;

    // One legacy recording: 32,000 float32 samples.
    private const long BlobBytes = 128_000;

    private static readonly StorageMaintenanceOptions ConversionOptions = StorageMaintenanceOptions.Default with
    {
        ReclaimThresholdBytes = 64 * 1024,
        FreeSpaceProbe = _ => long.MaxValue / 4,
    };

    private readonly TempDatabaseFolder _folder = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow.AddHours(2);

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_history_write_arriving_mid_vacuum_interrupts_it_commits_once_it_stops_and_the_vacuum_completes_later_when_idle()
    {
        LegacyDatabase.Seed(_folder.DatabasePath, _now, recent: 400, old: 40);
        using var db = _folder.Open();
        db.Initialize();
        var history = new HistoryRepository(db);
        var hooked = new HookedHistory(history);
        var hold = new PassEndHold(hooked);
        var time = new ManualTimeProvider(_now);
        var idle = true;
        Thread? writer = null;
        TimeSpan? writeTook = null;

        // A dictation's history write lands while the VACUUM is copying. It must not wait for the
        // VACUUM to finish: it asks maintenance to yield, queues for the gate, and commits.
        var midVacuum = new MidStatement(
            arrive: () =>
            {
                writer = new Thread(() =>
                {
                    var started = Stopwatch.GetTimestamp();
                    history.Add(
                        new HistoryEntry(0, _now, "dictated during vacuum", 1, 1),
                        new CapturedAudio(new float[1600], 16000));
                    writeTook = Stopwatch.GetElapsedTime(started);
                });
                writer.Start();
                hold.Arm(writer);
            },
            until: () => db.WaitingWriters == 1);
        db.PreemptibleProgressHook = midVacuum.OnProgress;
        using var maintenance = Create(db, hooked, time, ConversionOptions);
        maintenance.Start(() => SettingsWith(90), () => idle);
        time.Advance(Quiet);

        var first = maintenance.RunOnce(SettingsWith(90))!;

        Assert.True(midVacuum.Fired);
        Assert.Null(midVacuum.Failure);
        hold.AssertTheWriteCommittedWhileHeld();
        Assert.True(writer!.Join(Generous));
        output.WriteLine($"history write arriving mid-VACUUM committed in {writeTook!.Value.TotalMilliseconds:F1} ms");
        Assert.Equal(ReclaimMethod.Yielded, first.Reclaim.Method);
        Assert.True(first.Yielded);
        Assert.Equal(Quiet, first.RetryIn);
        var saved = Assert.Single(history.GetRecent(1000), e => e.Text == "dictated during vacuum");
        Assert.NotNull(history.GetAudio(saved.AudioBlobId!.Value));
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
        Assert.Equal("ok", DatabaseProbe.QueryString(db, "PRAGMA integrity_check;"));

        // Halfway through the backoff: light steps only, no second attempt.
        time.Advance(Quiet / 2);
        var second = maintenance.RunOnce(SettingsWith(90))!;
        Assert.True(second.HeavyDeferred);
        Assert.False(second.Yielded);
        Assert.Equal(ReclaimMethod.None, second.Reclaim.Method);
        Assert.Equal(Quiet / 2, second.RetryIn);

        // The backoff is over and the database quiet, but a Settings window is open: the other heavy
        // steps may run again, while the conversion waits for the app.
        time.Advance(Quiet / 2);
        idle = false;
        var third = maintenance.RunOnce(SettingsWith(90))!;
        Assert.False(third.HeavyDeferred);
        Assert.Equal(ReclaimMethod.DeferredForActivity, third.Reclaim.Method);
        Assert.Equal(Quiet, third.RetryIn);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));

        // Idle: the retried VACUUM runs to completion and keeps the write that interrupted the first.
        idle = true;
        var fourth = maintenance.RunOnce(SettingsWith(90))!;
        Assert.Equal(ReclaimMethod.ConvertedToIncremental, fourth.Reclaim.Method);
        Assert.False(fourth.Yielded);
        Assert.False(fourth.HeavyDeferred);
        Assert.Equal(2, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
        Assert.Equal("ok", DatabaseProbe.QueryString(db, "PRAGMA integrity_check;"));
        Assert.Single(history.GetRecent(1000), e => e.Text == "dictated during vacuum" && e.AudioBlobId is not null);
    }

    [Fact]
    public void A_dictation_starting_interrupts_a_running_vacuum_before_it_writes_anything()
    {
        LegacyDatabase.Seed(_folder.DatabasePath, _now, recent: 400, old: 40);
        using var db = _folder.Open();
        db.Initialize();
        var history = new HistoryRepository(db);
        var time = new ManualTimeProvider(_now);

        // The app raises the yield from the dictation controller's thread on activation.
        var activation = new MidStatement(arrive: () => OnAnotherThread(history.RequestYield), until: () => true);
        db.PreemptibleProgressHook = activation.OnProgress;
        using var maintenance = Create(db, history, time, ConversionOptions);
        maintenance.Start(() => SettingsWith(90), () => true);
        time.Advance(Quiet);

        var report = maintenance.RunOnce(SettingsWith(90))!;

        Assert.True(activation.Fired);
        Assert.Null(activation.Failure);
        Assert.Equal(ReclaimMethod.Yielded, report.Reclaim.Method);
        Assert.True(report.Yielded);
        Assert.Equal(Quiet, report.RetryIn);
        Assert.Equal(0, db.WaitingWriters);
        Assert.False(db.DeferAutoCheckpoint);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
        Assert.Equal("ok", DatabaseProbe.QueryString(db, "PRAGMA integrity_check;"));
    }

    [Fact]
    public void A_yield_raised_just_before_the_vacuum_starts_still_stops_it()
    {
        LegacyDatabase.Seed(_folder.DatabasePath, _now, recent: 400, old: 40);
        using var db = _folder.Open();
        db.Initialize();
        var history = new HistoryRepository(db);
        var time = new ManualTimeProvider(_now);

        // After every check has passed and before SQLite starts the statement, where sqlite3_interrupt
        // alone is a no-op: a settings save or an activation raises its yield exactly once.
        using var maintenance = Create(db, history, time, ConversionOptions with
        {
            BeforePreemptibleStatement = () => OnAnotherThread(history.RequestYield),
        });
        maintenance.Start(() => SettingsWith(90), () => true);
        time.Advance(Quiet);

        var report = maintenance.RunOnce(SettingsWith(90))!;

        Assert.Equal(ReclaimMethod.Yielded, report.Reclaim.Method);
        Assert.True(report.Yielded);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
        Assert.Equal("ok", DatabaseProbe.QueryString(db, "PRAGMA integrity_check;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_history_write_during_a_deletion_batch_gets_the_gate_after_one_slice_and_the_rest_resumes_after_the_backoff(bool cap)
    {
        // Twenty recordings to delete, one per slice: either unreferenced (their entries' audio
        // expired) or the oldest over a small cap.
        LegacyDatabase.Seed(_folder.DatabasePath, _now, recent: cap ? 20 : 2, old: cap ? 0 : 20);
        using var db = _folder.Open();
        db.Initialize();
        var history = new HistoryRepository(db);
        var hooked = new HookedHistory(history);
        var hold = new PassEndHold(hooked);
        var time = new ManualTimeProvider(_now);
        Thread? writer = null;
        TimeSpan? writeTook = null;
        var queued = false;

        // Maintenance holds the gate for the third slice when a dictation's write arrives and queues.
        void OnSlice(int slice)
        {
            if (slice != 3)
            {
                return;
            }

            writer = new Thread(() =>
            {
                var started = Stopwatch.GetTimestamp();
                history.Add(new HistoryEntry(0, _now, "dictated mid-batch", 1, 1), new CapturedAudio(new float[1600], 16000));
                writeTook = Stopwatch.GetElapsedTime(started);
            });
            writer.Start();
            queued = SpinWait.SpinUntil(() => db.WaitingWriters == 1, Generous);
            hold.Arm(writer);
        }

        if (cap)
        {
            hooked.OnEvictSlice = OnSlice;
        }
        else
        {
            hooked.OnDeleteSlice = OnSlice;
        }

        var idle = true;
        using var maintenance = new StorageMaintenance(db, hooked, new CleanupFailureLog(db), NullLogger.Instance, time,
            StorageMaintenanceOptions.Default with
            {
                BlobBatchSize = 1,
                MaxStoredAudioBytes = cap ? 5 * BlobBytes : StorageMaintenanceOptions.Default.MaxStoredAudioBytes,
                ReclaimThresholdBytes = long.MaxValue,
            });
        maintenance.Start(() => SettingsWith(90), () => idle);

        var first = maintenance.RunOnce(SettingsWith(90))!;

        Assert.True(queued);
        hold.AssertTheWriteCommittedWhileHeld();
        Assert.True(writer!.Join(Generous));
        output.WriteLine($"history write arriving mid-batch committed in {writeTook!.Value.TotalMilliseconds:F1} ms");
        Assert.True(first.Yielded);
        Assert.Equal(3, cap ? first.Evicted.BlobsDeleted : first.Unreferenced.BlobsDeleted);
        Assert.Single(history.GetRecent(1000), e => e.Text == "dictated mid-batch" && e.AudioBlobId is not null);
        var blobsAfterYield = history.GetStoredAudioUsage().Blobs;

        // During the backoff not one more slice runs.
        time.Advance(Quiet / 2);
        var second = maintenance.RunOnce(SettingsWith(90))!;
        Assert.True(second.HeavyDeferred);
        Assert.Equal(blobsAfterYield, history.GetStoredAudioUsage().Blobs);

        // Backoff over: the batch finishes, even with a Settings window open and a settings save a
        // moment ago, because only the conversion waits for those.
        time.Advance(Quiet / 2);
        idle = false;
        new SettingsRepository(db).Set("probe", "saved just now");
        var third = maintenance.RunOnce(SettingsWith(90))!;
        Assert.False(third.HeavyDeferred);
        Assert.False(third.Yielded);
        if (cap)
        {
            Assert.True(history.GetStoredAudioUsage().Bytes <= 5 * BlobBytes);
        }
        else
        {
            Assert.DoesNotContain(history.ListStoredAudio(), blob => !blob.Referenced);
        }

        Assert.Single(history.GetRecent(1000), e => e.Text == "dictated mid-batch" && e.AudioBlobId is not null);
    }

    [Fact]
    public void After_the_backoff_slices_the_cap_and_the_checkpoint_resume_even_with_settings_open_and_a_recent_save()
    {
        // A database this build created: incremental auto_vacuum, so no conversion is involved.
        using var db = _folder.Open();
        var history = new HistoryRepository(db);
        for (var i = 0; i < 12; i++)
        {
            // Old enough for their audio to expire, which leaves unreferenced blobs for the slices.
            history.Add(new HistoryEntry(0, _now.AddDays(-10).AddMinutes(i), $"old {i}", 1, 1), new CapturedAudio(new float[16_000], 16000));
        }

        for (var i = 0; i < 8; i++)
        {
            history.Add(new HistoryEntry(0, _now.AddMinutes(-60 + i), $"recent {i}", 1, 1), new CapturedAudio(new float[16_000], 16000));
        }

        var blob = AudioBlobCodec.EncodedLength(16_000, AudioBlobEncoding.Pcm16);
        var hooked = new HookedHistory(history)
        {
            // A dictation starts during the second slice.
            OnDeleteSlice = slice =>
            {
                if (slice == 2)
                {
                    history.RequestYield();
                }
            },
        };
        var asked = 0;
        var time = new ManualTimeProvider(_now);
        using var maintenance = new StorageMaintenance(db, hooked, new CleanupFailureLog(db), NullLogger.Instance, time,
            StorageMaintenanceOptions.Default with
            {
                BlobBatchSize = 1,
                MaxStoredAudioBytes = 3 * blob,
                ReclaimThresholdBytes = 64 * 1024,
            });

        // A Settings window is open the whole time.
        maintenance.Start(() => SettingsWith(90), () =>
        {
            asked++;
            return false;
        });

        var first = maintenance.RunOnce(SettingsWith(90))!;
        Assert.True(first.Yielded);
        Assert.Equal(2, first.Unreferenced.BlobsDeleted);
        Assert.Equal(Quiet, first.RetryIn);

        // The user saves settings half a minute before the backoff ends, so the database is not
        // quiet either; neither matters to these steps.
        hooked.OnDeleteSlice = null;
        time.Advance(Quiet - TimeSpan.FromSeconds(30));
        new SettingsRepository(db).Set("probe", "saved");
        time.Advance(TimeSpan.FromSeconds(30));
        var second = maintenance.RunOnce(SettingsWith(90))!;

        Assert.False(second.HeavyDeferred);
        Assert.False(second.Yielded);
        Assert.Equal(10, second.Unreferenced.BlobsDeleted);
        Assert.Equal(5, second.Evicted.BlobsDeleted);
        Assert.Equal(new StoredAudioUsage(3, 3 * blob), second.AudioAfter);
        Assert.Equal(ReclaimMethod.Incremental, second.Reclaim.Method);
        Assert.False(second.Reclaim.CheckpointBusy);
        Assert.Equal(0, _folder.FileLength("-wal"));
        Assert.Equal(0, asked); // only the conversion asks the app
    }

    [Fact]
    public void A_foreground_scope_stops_a_running_vacuum_and_holds_off_the_conversion_until_it_ends()
    {
        LegacyDatabase.Seed(_folder.DatabasePath, _now, recent: 400, old: 40);
        using var db = _folder.Open();
        db.Initialize();
        var history = new HistoryRepository(db);
        var time = new ManualTimeProvider(_now);
        using var maintenance = Create(db, history, time, ConversionOptions);
        IDisposable? foreground = null;

        // The tray's Learn from history starts, on the UI thread, while the VACUUM is copying.
        var learning = new MidStatement(
            arrive: () => OnAnotherThread(() => foreground = maintenance.EnterForegroundWork()),
            until: () => true);
        db.PreemptibleProgressHook = learning.OnProgress;
        maintenance.Start(() => SettingsWith(90), () => true);
        time.Advance(Quiet);

        var interrupted = maintenance.RunOnce(SettingsWith(90))!;
        Assert.True(learning.Fired);
        Assert.Null(learning.Failure);
        Assert.Equal(ReclaimMethod.Yielded, interrupted.Reclaim.Method);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));

        // Long after the backoff and the quiet period, with the app itself saying yes: while the
        // work is still going on, the conversion does not start.
        time.Advance(Quiet * 4);
        var held = maintenance.RunOnce(SettingsWith(90))!;
        Assert.Equal(ReclaimMethod.DeferredForActivity, held.Reclaim.Method);
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));

        // It ends (disposing twice is harmless), and the next pass converts.
        foreground!.Dispose();
        foreground.Dispose();
        var converted = maintenance.RunOnce(SettingsWith(90))!;
        Assert.Equal(ReclaimMethod.ConvertedToIncremental, converted.Reclaim.Method);
        Assert.Equal("ok", DatabaseProbe.QueryString(db, "PRAGMA integrity_check;"));
    }

    [Fact]
    public void Consecutive_yields_back_off_2_4_8_minutes_up_to_the_hourly_interval_and_completed_heavy_work_resets_it()
    {
        LegacyDatabase.Seed(_folder.DatabasePath, _now, recent: 4, old: 20);
        using var db = _folder.Open();
        db.Initialize();
        var history = new HistoryRepository(db);
        var time = new ManualTimeProvider(_now);
        var yieldAtLastCheck = true;
        var yieldBeforeStatement = false;
        using var maintenance = Create(db, history, time, ConversionOptions with
        {
            // A dictation starts every time the VACUUM is about to begin.
            BeforeLastCheck = () =>
            {
                if (yieldAtLastCheck)
                {
                    history.RequestYield();
                }
            },
            BeforePreemptibleStatement = () =>
            {
                if (yieldBeforeStatement)
                {
                    history.RequestYield();
                }
            },
        });
        maintenance.Start(() => SettingsWith(90), () => true);
        time.Advance(Quiet);

        var interval = StorageMaintenanceOptions.Default.Interval;
        TimeSpan[] backoffs = [Quiet, Quiet * 2, Quiet * 4, Quiet * 8, Quiet * 16, interval, interval];
        foreach (var backoff in backoffs)
        {
            var report = maintenance.RunOnce(SettingsWith(90))!;
            Assert.Equal(ReclaimMethod.Yielded, report.Reclaim.Method);
            Assert.True(report.Yielded);
            Assert.Equal(backoff, report.RetryIn);
            time.Advance(backoff);
        }

        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));

        // The user stops dictating: the conversion completes, which ends the backoff.
        yieldAtLastCheck = false;
        var converted = maintenance.RunOnce(SettingsWith(90))!;
        Assert.Equal(ReclaimMethod.ConvertedToIncremental, converted.Reclaim.Method);
        Assert.False(converted.Yielded);
        Assert.Equal(TimeSpan.Zero, converted.RetryIn);

        // A later yield, here during the incremental reclaim after Clear history, starts again at
        // the shortest backoff rather than an hour.
        history.Clear();
        yieldBeforeStatement = true;
        var later = maintenance.RunOnce(SettingsWith(90))!;
        Assert.True(later.Yielded);
        Assert.Equal(Quiet, later.RetryIn);
    }

    [Fact]
    public void Activity_during_a_light_only_pass_restarts_the_quiet_window_but_does_not_stretch_the_backoff()
    {
        LegacyDatabase.Seed(_folder.DatabasePath, _now, recent: 4, old: 20);
        using var db = _folder.Open();
        db.Initialize();
        var history = new HistoryRepository(db);
        var hooked = new HookedHistory(history);
        var time = new ManualTimeProvider(_now);
        var yieldAtLastCheck = true;
        using var maintenance = new StorageMaintenance(db, hooked, new CleanupFailureLog(db), NullLogger.Instance, time,
            ConversionOptions with
            {
                BeforeLastCheck = () =>
                {
                    if (yieldAtLastCheck)
                    {
                        history.RequestYield();
                    }
                },
            });
        maintenance.Start(() => SettingsWith(90), () => true);
        time.Advance(Quiet);
        Assert.Equal(Quiet, maintenance.RunOnce(SettingsWith(90))!.RetryIn);

        // Halfway through the backoff a dictation starts during the pass's light steps.
        time.Advance(Quiet / 2);
        hooked.OnClearAudio = history.RequestYield;
        var light = maintenance.RunOnce(SettingsWith(90))!;
        hooked.OnClearAudio = null;
        Assert.True(light.HeavyDeferred);
        Assert.False(light.Yielded);
        Assert.Equal(Quiet / 2, light.RetryIn);

        // The backoff is over, but the quiet window restarted at that dictation: the conversion
        // waits out the rest of it, though other heavy steps would already run.
        time.Advance(Quiet / 2);
        var waiting = maintenance.RunOnce(SettingsWith(90))!;
        Assert.False(waiting.HeavyDeferred);
        Assert.Equal(ReclaimMethod.DeferredForActivity, waiting.Reclaim.Method);
        Assert.Equal(Quiet / 2, waiting.RetryIn);

        // Quiet again: heavy work runs, and this second consecutive yield backs off 4 minutes, not 8.
        time.Advance(Quiet / 2);
        var second = maintenance.RunOnce(SettingsWith(90))!;
        Assert.True(second.Yielded);
        Assert.Equal(Quiet * 2, second.RetryIn);

        yieldAtLastCheck = false;
        time.Advance(Quiet * 2);
        Assert.Equal(ReclaimMethod.ConvertedToIncremental, maintenance.RunOnce(SettingsWith(90))!.Reclaim.Method);
    }

    private static StorageMaintenance Create(
        ScribeDatabase db, IHistoryMaintenance history, ManualTimeProvider time, StorageMaintenanceOptions options) =>
        new(db, history, new CleanupFailureLog(db), NullLogger.Instance, time, options);

    /// <summary>
    /// A write that arrives mid-step must get the gate when that step ends, not queue behind the rest of the job. That is
    /// checked by what maintenance still holds, never by how long the write took, which is the disk's: on CI, under the
    /// parallel suite, a mid-batch write took 2.6 s and 3.7 s against the 2 s bound this replaces. Maintenance is held at
    /// the last step of its pass, outside the gate, until the write commits, which it can do only if maintenance kept
    /// neither the gate nor a write transaction; after a yield the only step that can come before it is the PASSIVE WAL
    /// backfill, which never blocks a writer. What the write took still goes to the test output.
    /// </summary>
    private sealed class PassEndHold(HookedHistory hooked)
    {
        private bool? _committed;

        public void Arm(Thread writer) => hooked.OnAudioUsage = () =>
        {
            hooked.OnAudioUsage = null;
            _committed = writer.Join(Generous);
        };

        public void AssertTheWriteCommittedWhileHeld()
        {
            Assert.True(_committed is not null, "Maintenance's pass never reached its last step after the write arrived.");
            Assert.True(
                _committed == true,
                "The write could not commit while maintenance waited at the end of its pass, so maintenance kept the write " +
                "gate or a write transaction after it yielded.");
        }
    }

    private static AppSettings SettingsWith(int retentionDays)
    {
        var settings = AppSettings.CreateDefault();
        settings.HistoryRetentionDays = retentionDays;
        return settings;
    }

    // Like the app, which raises the yield from the controller's thread, never maintenance's.
    private static void OnAnotherThread(Action action)
    {
        var thread = new Thread(() => action());
        thread.Start();
        thread.Join(Generous);
    }

    /// <summary>
    /// Runs an action once from inside the next preemptible statement, through the database's
    /// progress hook. SQLite calls it only while a statement's bytecode runs, which inside a VACUUM is
    /// its interruptible copy and never its final file copy, so whatever the action starts
    /// demonstrably arrives mid-statement. The statement is held until <c>until</c> holds, so the
    /// arrival cannot race the statement's end. Nothing may throw into SQLite from here.
    /// </summary>
    private sealed class MidStatement(Action arrive, Func<bool> until)
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public Exception? Failure { get; private set; }

        public void OnProgress()
        {
            if (Interlocked.Exchange(ref _fired, 1) != 0)
            {
                return;
            }

            try
            {
                arrive();
                SpinWait.SpinUntil(until, Generous);
            }
            catch (Exception ex)
            {
                Failure = ex;
            }
        }
    }
}
