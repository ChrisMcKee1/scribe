using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.StorageTime;

namespace Scribe.Core.Tests;

/// <summary>
/// How maintenance passes are scheduled, serialized against writers and stopped. Every interleaving
/// is driven with a manual clock, manual timer firings and blocking fakes; nothing here sleeps.
/// </summary>
public class StorageMaintenanceSchedulingTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);
    private static readonly StorageMaintenanceOptions Options = StorageMaintenanceOptions.Default;

    [Fact]
    public void The_first_pass_follows_startup_and_later_passes_run_hourly()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var settings = new CountingSettings();
        using var maintenance = Create(db, new HistoryRepository(db), time);

        maintenance.Start(settings.Read);
        var timer = time.SingleTimer;
        Assert.Equal(Options.InitialDelay, timer.DueTime);

        timer.Fire();

        Assert.Equal(1, settings.Calls);
        Assert.Equal(Options.Interval, timer.DueTime);
    }

    [Fact]
    public void Stored_audio_and_deletes_bring_a_pass_forward_but_never_closer_than_the_spacing()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var history = new HistoryRepository(db);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var maintenance = Create(db, history, time);

        // Nothing is scheduled by a write before Start: the first pass covers it anyway.
        history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "early", 1, 1), new CapturedAudio([0.1f]));

        maintenance.Start(AppSettings.CreateDefault);
        var timer = time.SingleTimer;
        Assert.Equal(Options.InitialDelay, timer.DueTime);

        // Before any pass, stored audio pulls the first pass in to the trigger delay.
        var saved = history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "x", 1, 1), new CapturedAudio([0.1f]));
        Assert.Equal(Options.TriggerDelay, timer.DueTime);

        timer.Fire();
        Assert.Equal(Options.Interval, timer.DueTime);

        // Right after a pass, a delete still waits out the minimum spacing.
        history.Delete(saved.Id);
        Assert.Equal(Options.MinimumSpacing, timer.DueTime);

        // A second request in the same window coalesces instead of pushing the pass out.
        history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "y", 1, 1), new CapturedAudio([0.1f]));
        Assert.Equal(Options.MinimumSpacing, timer.DueTime);

        timer.Fire();
        time.Advance(TimeSpan.FromMinutes(5));
        history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "z", 1, 1), new CapturedAudio([0.1f]));
        Assert.Equal(Options.TriggerDelay, timer.DueTime);
    }

    [Fact]
    public async Task Passes_never_overlap_and_a_request_during_one_becomes_exactly_one_more()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var settings = new BlockingSettings();
        using var maintenance = Create(db, new HistoryRepository(db), time);
        maintenance.Start(settings.Read);
        var timer = time.SingleTimer;

        var first = Task.Run(timer.Fire);
        Assert.True(settings.Entered.Wait(Generous));

        // While the first pass is inside, neither another firing nor a direct run starts one.
        timer.Fire();
        timer.Fire();
        maintenance.RequestRun();
        Assert.Null(maintenance.RunOnce(AppSettings.CreateDefault()));
        Assert.Equal(1, settings.Calls);

        settings.Release.Set();
        await first.WaitAsync(Generous);

        // Those refusals collapse into one follow-up pass, due after the spacing, then hourly again.
        Assert.Equal(Options.MinimumSpacing, timer.DueTime);
        timer.Fire();
        Assert.Equal(2, settings.Calls);
        Assert.Equal(Options.Interval, timer.DueTime);
    }

    [Fact]
    public async Task A_write_during_a_maintenance_step_waits_for_the_gate_and_then_succeeds()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var blocking = new BlockingHistoryMaintenance();
        var failures = new CleanupFailureLog(db);
        var history = new HistoryRepository(db);
        using var maintenance = new StorageMaintenance(
            db, blocking, failures, NullLogger.Instance, new ManualTimeProvider(DateTimeOffset.UtcNow), Options);

        var pass = Task.Run(() => maintenance.RunOnce(AppSettings.CreateDefault()));
        Assert.True(blocking.Entered.Wait(Generous));

        // The pass is inside a gated step. Both kinds of writer the brief names must queue, not fail.
        var historyWrite = Task.Run(() => history.Add(
            new HistoryEntry(0, DateTimeOffset.UtcNow, "dictated during maintenance", 1, 1),
            new CapturedAudio([0.2f, 0.3f])));
        var failureWrite = Task.Run(() => failures.Add(CleanupFailure.New("timed out")));
        Assert.True(SpinWait.SpinUntil(() => db.WaitingWriters == 2, Generous));
        Assert.False(historyWrite.IsCompleted);
        Assert.False(failureWrite.IsCompleted);

        blocking.Release.Set();

        await Task.WhenAll(pass, historyWrite, failureWrite).WaitAsync(Generous);
        Assert.Equal("dictated during maintenance", Assert.Single(history.GetRecent()).Text);
        Assert.Equal(1, failures.Count());
    }

    [Fact]
    public async Task Ratings_wait_for_the_gate_but_settings_writes_never_do()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var settings = new SettingsRepository(db);
        var history = new HistoryRepository(db);
        var entry = history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "rate me", 1, 1));
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

        // Settings save runs on the UI thread, so it must finish while maintenance holds the gate.
        await Task.Run(() => settings.Set("probe", "value")).WaitAsync(Generous);
        Assert.Equal("value", settings.Get("probe"));
        Assert.Equal(0, db.WaitingWriters);

        var ratingWrite = Task.Run(() => history.SetAiRating(entry.Id, AiRating.Useful));
        Assert.True(SpinWait.SpinUntil(() => db.WaitingWriters == 1, Generous));
        Assert.False(ratingWrite.IsCompleted);

        release.Set();
        await ratingWrite.WaitAsync(Generous);
        Assert.True(holder.Join(Generous));
        Assert.Equal(AiRating.Useful, Assert.Single(history.GetRecent()).AiRating);
    }

    [Fact]
    public async Task Dictionary_and_snippet_writes_wait_for_the_gate_too()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var dictionary = new DictionaryRepository(db);
        var snippets = new SnippetRepository(db);
        var existing = dictionary.Add(DictionaryEntry.New("azure", "Azure"));

        Task[] writes;
        using (db.EnterWriteScope())
        {
            writes =
            [
                Task.Run(() => dictionary.Add(DictionaryEntry.New("github", "GitHub"))),
                Task.Run(() => dictionary.Update(existing with { Replacement = "AZURE" })),
                Task.Run(() => snippets.SaveAll([Snippet.New("sign off", "Thanks")])),
            ];
            Assert.True(SpinWait.SpinUntil(() => db.WaitingWriters == writes.Length, Generous));
            Assert.All(writes, write => Assert.False(write.IsCompleted));
        }

        await Task.WhenAll(writes).WaitAsync(Generous);
        Assert.Equal(new[] { "AZURE", "GitHub" }, dictionary.GetAll().Select(e => e.Replacement));
        Assert.Equal("sign off", Assert.Single(snippets.GetAll()).Phrase);
    }

    [Fact]
    public async Task A_writer_stuck_behind_the_gate_goes_ahead_after_the_timeout_instead_of_hanging()
    {
        using var db = ScribeDatabase.CreateInMemory();
        db.WriteGateTimeout = TimeSpan.FromMilliseconds(50);
        var history = new HistoryRepository(db);
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var heldByHolder = false;

        // The holder never lets go by itself, as if stuck.
        var holder = new Thread(() =>
        {
            using var scope = db.EnterWriteScope();
            heldByHolder = scope.Held;
            holding.Set();
            release.Wait(Generous);
        });
        holder.Start();
        Assert.True(holding.Wait(Generous));
        Assert.True(heldByHolder);

        // The write still completes, ungated, and SQLite itself serializes it, which is exactly
        // what happened before the gate existed.
        await Task.Run(() => history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "late", 1, 1))).WaitAsync(Generous);
        Assert.Equal(0, db.WaitingWriters);
        Assert.Equal("late", Assert.Single(history.GetRecent()).Text);

        release.Set();
        Assert.True(holder.Join(Generous));

        // Once the holder is gone the gate is simply taken again.
        Assert.True(TryHoldGate(db));
    }

    private static bool TryHoldGate(ScribeDatabase db)
    {
        using var scope = db.EnterWriteScope();
        return scope.Held;
    }

    [Fact]
    public void A_wall_clock_set_back_does_not_push_a_triggered_pass_out()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var history = new HistoryRepository(db);
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var maintenance = Create(db, history, time);
        maintenance.Start(AppSettings.CreateDefault);
        var timer = time.SingleTimer;
        timer.Fire();
        Assert.Equal(Options.Interval, timer.DueTime);

        // The user corrects a clock that ran a day fast. Scheduling runs on the monotonic
        // timestamp, so stored audio still brings the next pass in by the usual spacing rather
        // than a day and a half.
        time.SetWallClock(time.GetUtcNow().AddDays(-1));
        history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "x", 1, 1), new CapturedAudio([0.1f]));

        Assert.Equal(Options.MinimumSpacing, timer.DueTime);
    }

    [Fact]
    public void A_checkpoint_a_reader_blocks_is_retried_soon_a_few_times_then_hourly()
    {
        using var folder = new TempDatabaseFolder();
        using var db = folder.Open();
        var history = new HistoryRepository(db);
        for (var i = 0; i < 8; i++)
        {
            history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, $"entry {i}", 1, 1), new CapturedAudio(new float[8_000]));
        }

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow.AddHours(2));
        var log = new CapturingLogger();
        using var maintenance = new StorageMaintenance(
            db, history, new CleanupFailureLog(db), log, time,
            Options with { CheckpointBusyTimeout = TimeSpan.Zero });
        maintenance.Start(AppSettings.CreateDefault);
        var timer = time.SingleTimer;

        // Seen once in about eighty runs under parallel load and never since: if it fails again, say
        // what the pass saw rather than only which due time was wrong.
        string Diagnose(string step)
        {
            var last = maintenance.LastReport;
            return $"{step}: due={timer.DueTime}; last pass: reclaim={last?.Reclaim.Method} busy={last?.Reclaim.CheckpointBusy} " +
                   $"free={last?.Reclaim.FreeBytesBefore} files={last?.Reclaim.FileBytesBefore}->{last?.Reclaim.FileBytesAfter} " +
                   $"yielded={last?.Yielded} heavyDeferred={last?.HeavyDeferred} retryIn={last?.RetryIn} stopped={last?.Stopped}; " +
                   $"wal={folder.FileLength("-wal")} bytes; deferAutoCheckpoint={db.DeferAutoCheckpoint}; " +
                   $"activity={db.ActivityCount}; log (SQLite codes appear here):{Environment.NewLine}{log.Text}";
        }

        // A reader parked mid-query on another connection pins a snapshot that lives in the WAL,
        // so no TRUNCATE checkpoint can finish until it lets go.
        using var reader = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = folder.DatabasePath, Pooling = false }.ToString());
        reader.Open();
        using var query = reader.CreateCommand();
        query.CommandText = "SELECT id FROM history;";
        var rows = query.ExecuteReader();
        Assert.True(rows.Read());

        history.Clear();
        for (var retry = 0; retry < 3; retry++)
        {
            timer.Fire();
            Assert.True(timer.DueTime == Later(Options.TriggerDelay, Options.MinimumSpacing), Diagnose($"retry {retry}"));
        }

        timer.Fire();
        Assert.True(timer.DueTime == Options.Interval, Diagnose("after the retries"));

        // Once the reader is done the pending checkpoint completes and the WAL is emptied.
        rows.Dispose();
        var report = maintenance.RunOnce(AppSettings.CreateDefault())!;
        Assert.False(report.Reclaim.CheckpointBusy, Diagnose("reader gone"));
        Assert.True(folder.FileLength("-wal") == 0, Diagnose("reader gone"));
    }

    private static TimeSpan Later(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    [Fact]
    public async Task Dispose_waits_for_the_pass_in_flight_and_nothing_runs_after_it()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var settings = new BlockingSettings();
        var recording = new BlockingHistoryMaintenance(block: false);
        var maintenance = new StorageMaintenance(db, recording, new CleanupFailureLog(db), NullLogger.Instance, time, Options);
        maintenance.Start(settings.Read);
        var timer = time.SingleTimer;

        var pass = Task.Run(timer.Fire);
        Assert.True(settings.Entered.Wait(Generous));

        var activityBeforeDispose = db.ActivityCount;
        var dispose = Task.Run(maintenance.Dispose);

        // Dispose disposes the timer, then stops and waits: the pass is still blocked, so it cannot
        // return. The stop counts as activity, so the counter moving proves the stop was requested
        // before the pass is released; the timer alone is disposed a step earlier.
        Assert.True(SpinWait.SpinUntil(() => timer.Disposed, Generous));
        Assert.True(SpinWait.SpinUntil(() => db.ActivityCount != activityBeforeDispose, Generous));
        Assert.False(dispose.IsCompleted);

        settings.Release.Set();
        await dispose.WaitAsync(Generous);
        await pass.WaitAsync(Generous);

        // The pass saw the stop before its first step and left the database alone.
        Assert.Equal(0, recording.Calls);

        // A firing that was already queued when the timer died finds the service closed.
        timer.Fire();
        Assert.Equal(1, settings.Calls);
        Assert.Null(maintenance.RunOnce(AppSettings.CreateDefault()));
        maintenance.RequestRun();
        Assert.Null(timer.DueTime);

        // Idempotent, and a late Start does not resurrect the schedule.
        maintenance.Dispose();
        maintenance.Start(settings.Read);
        Assert.Equal(1, settings.Calls);
    }

    [Fact]
    public void A_settings_accessor_that_throws_skips_text_retention_but_not_the_rest()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var history = new HistoryRepository(db);
        var failures = new CleanupFailureLog(db);
        history.Add(new HistoryEntry(0, DateTimeOffset.UtcNow.AddDays(-400), "ancient", 1, 1));
        failures.Add(CleanupFailure.New("stale") with { TimestampUtc = DateTimeOffset.UtcNow.AddDays(-30) });
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var maintenance = new StorageMaintenance(db, history, failures, NullLogger.Instance, time, Options);

        maintenance.Start(() => throw new InvalidOperationException("controller gone"));
        time.SingleTimer.Fire();

        Assert.Single(history.GetRecent());
        Assert.Equal(0, failures.Count());
        Assert.Equal(Options.Interval, time.SingleTimer.DueTime);
    }

    private static StorageMaintenance Create(ScribeDatabase db, IHistoryMaintenance history, TimeProvider time) =>
        new(db, history, new CleanupFailureLog(db), NullLogger.Instance, time, Options);

    private sealed class CountingSettings
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public AppSettings Read()
        {
            Interlocked.Increment(ref _calls);
            return AppSettings.CreateDefault();
        }
    }

    /// <summary>Blocks the first read until released, so a pass can be held open deterministically.</summary>
    private sealed class BlockingSettings
    {
        private int _calls;

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public AppSettings Read()
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.Set();
                Release.Wait(Generous);
            }

            return AppSettings.CreateDefault();
        }
    }

    /// <summary>Holds the pass inside its audio-retention step, which maintenance runs under the gate.</summary>
    private sealed class BlockingHistoryMaintenance(bool block = true) : IHistoryMaintenance
    {
        private int _calls;

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public int DeleteEntriesOlderThan(DateTimeOffset cutoffUtc)
        {
            Interlocked.Increment(ref _calls);
            return 0;
        }

        public int ClearAudioOlderThan(DateTimeOffset cutoffUtc)
        {
            Interlocked.Increment(ref _calls);
            if (block)
            {
                Entered.Set();
                Release.Wait(Generous);
            }

            return 0;
        }

        public IReadOnlyList<StoredAudioBlob> ListStoredAudio()
        {
            Interlocked.Increment(ref _calls);
            return [];
        }

        public AudioDeletion DeleteUnreferencedAudio(IReadOnlyCollection<long> blobIds, DateTimeOffset storedBeforeUtc)
        {
            Interlocked.Increment(ref _calls);
            return default;
        }

        public AudioDeletion EvictAudio(IReadOnlyCollection<long> blobIds, long maxStoredBytes)
        {
            Interlocked.Increment(ref _calls);
            return default;
        }

        public StoredAudioUsage GetStoredAudioUsage()
        {
            Interlocked.Increment(ref _calls);
            return default;
        }

        public void RequestYield()
        {
        }
    }
}
