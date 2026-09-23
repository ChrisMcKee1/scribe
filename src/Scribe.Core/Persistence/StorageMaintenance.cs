using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <summary>
/// Keeps <c>scribe.db</c> and the files beside it bounded for the whole session: history text past
/// the user's <see cref="AppSettings.HistoryRetentionDays"/>, stored audio past
/// <see cref="StorageRetentionPolicy.AudioRetentionDays"/> days or over
/// <see cref="StorageRetentionPolicy.MaxStoredAudioMegabytes"/> MB in total, AI cleanup failure
/// samples past a week whatever later cleanups do, and damaged-database copies past two weeks. Then
/// it gives the freed pages back to the disk.
/// </summary>
/// <remarks>
/// <para>
/// One pass at a time, never on the caller's thread: shortly after <see cref="Start"/>, then hourly,
/// and soon after audio is stored or history is deleted.
/// </para>
/// <para>
/// Writers never queue behind it for long. Deletions run in slices of at most
/// <see cref="StorageMaintenanceOptions.BlobBatchBytes"/>, each its own short transaction under the
/// database write gate. Settings writes, which run on the UI thread, do not take the gate at all.
/// A database created before this class existed has auto_vacuum NONE, which never shrinks; the one
/// VACUUM that converts it to INCREMENTAL starts only when the app says nothing interactive is going
/// on (no dictation, no Settings window), nobody has written for
/// <see cref="StorageMaintenanceOptions.ConversionQuietPeriod"/>, and the gate is free, and the app's
/// answer is asked again immediately before it starts. It is preemptible: a writer that arrives
/// interrupts it, SQLite rolls it back, and it is tried again later. SQLite cannot interrupt the
/// VACUUM's final copy back into the database file, so a writer arriving in exactly that window
/// still waits for it. Later passes reclaim with <c>PRAGMA incremental_vacuum</c> in bounded, also
/// preemptible, steps. The WAL is backfilled by an ungated PASSIVE checkpoint and emptied by a short
/// TRUNCATE one.
/// </para>
/// <para>
/// Heavy work (deleting audio in slices, the cap, reclamation) yields to dictation. A dictation
/// starting (<see cref="IHistoryMaintenance.RequestYield"/>, raised by the app), a history write or a
/// settings save stops it at once: a running VACUUM or reclamation step is interrupted and rolls
/// back, a batch ends before its next slice, and the pass takes the gate no more. Heavy work is then
/// retried after a backoff that doubles with each consecutive yield (2, 4, 8 ... minutes, never more
/// than hourly), and only once the app is idle and nobody has written for the quiet period. Passes
/// meanwhile do only the light, single-statement steps.
/// </para>
/// <para>
/// Never throws. Logs counts, sizes and durations only: no paths, no text. <see cref="Stop"/>, which
/// the app calls first thing on exit, and <see cref="Dispose"/> close admission, interrupt a
/// preemptible statement and wait, bounded, for the pass in flight, which stops at its next step.
/// </para>
/// </remarks>
public sealed class StorageMaintenance : IDisposable
{
    private const long AutoVacuumNone = 0;
    private const long AutoVacuumIncremental = 2;
    private const int SqliteInterrupt = 9;
    private const int MaxRetentionDays = 36_500;
    private const long MinimumStepPages = 64;
    private const int MaxQuickReclaimRetries = 3;

    private readonly ScribeDatabase _database;
    private readonly IHistoryMaintenance _history;
    private readonly ICleanupFailureLog _failureLog;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly StorageMaintenanceOptions _options;

    // Scheduling runs on the monotonic timestamp, never the wall clock: a clock set back by a day
    // would otherwise push every triggered pass a day out. Retention cutoffs still use wall time,
    // because that is what the stored timestamps are.
    private readonly long _origin;

    private readonly object _lock = new();
    private ITimer? _timer;
    private Func<AppSettings>? _settings;
    private Func<bool>? _heavyMaintenanceAllowed;
    private bool _started;
    private bool _closed;
    private bool _disposed;
    private bool _running;
    private int _runningThread;
    private bool _reclaimRequested;
    private TimeSpan _followUpDelay = TimeSpan.MaxValue;
    private TimeSpan _nextDue = TimeSpan.MaxValue;
    private TimeSpan _lastFinished = TimeSpan.MinValue;
    private volatile bool _stopRequested;
    private volatile bool _keepAllText;
    private int _foregroundWork;

    // Touched only by the one pass that holds _running.
    private bool _checkpointPending;
    private int _quickReclaimRetries;
    private bool _conversionBlocked;
    private bool _loggedSpaceDeferral;
    private long _quietCount;
    private TimeSpan _quietSince;
    private TimeSpan _vacuumTime;
    private long _passActivity;
    private bool _yieldedThisPass;
    private bool _yieldPending;
    private int _consecutiveYields;
    private TimeSpan _heavyNotBefore = TimeSpan.MinValue;
    private TimeSpan _reclaimRetryIn;

    public StorageMaintenance(
        ScribeDatabase database,
        IHistoryMaintenance history,
        ICleanupFailureLog failureLog,
        ILogger<StorageMaintenance> logger)
        : this(database, history, failureLog, logger, TimeProvider.System, StorageMaintenanceOptions.Default)
    {
    }

    internal StorageMaintenance(
        ScribeDatabase database,
        IHistoryMaintenance history,
        ICleanupFailureLog failureLog,
        ILogger logger,
        TimeProvider time,
        StorageMaintenanceOptions options)
    {
        _database = database;
        _history = history;
        _failureLog = failureLog;
        _logger = logger;
        _time = time;
        _options = options;
        _origin = time.GetTimestamp();
        _quietCount = database.ActivityCount;
        _database.StorageChanged += OnStorageChanged;
    }

    /// <summary>
    /// Starts the schedule. <paramref name="settings"/> is read at the start of every pass, so a
    /// retention change in Settings applies without a restart. <paramref name="heavyMaintenanceAllowed"/>
    /// says whether the one-time VACUUM may run now: the app answers false while anything interactive
    /// that writes on the UI thread is going on (a dictation, an open Settings window). It is asked
    /// again immediately before the VACUUM starts; null means always allowed. Calling Start again, or
    /// after <see cref="Stop"/> or <see cref="Dispose"/>, does nothing.
    /// </summary>
    public void Start(Func<AppSettings> settings, Func<bool>? heavyMaintenanceAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            if (_started || _closed)
            {
                return;
            }

            _started = true;
            _settings = settings;
            _heavyMaintenanceAllowed = heavyMaintenanceAllowed;

            // Not flowing the caller's ExecutionContext: a timer that lives for the whole session
            // would otherwise run every pass under whatever ambient state (a trace activity, say)
            // happened to be current at startup. SuppressFlow throws if flow is already off.
            var suppressed = ExecutionContext.IsFlowSuppressed() ? (AsyncFlowControl?)null : ExecutionContext.SuppressFlow();
            try
            {
                _timer = _time.CreateTimer(
                    static state => ((StorageMaintenance)state!).OnTimer(),
                    this,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
            }
            finally
            {
                suppressed?.Undo();
            }

            ArmLocked(_options.InitialDelay);
        }
    }

    /// <summary>
    /// Deletes no history text for the rest of this session, whatever
    /// <see cref="AppSettings.HistoryRetentionDays"/> says. For a session running on compiled
    /// defaults rather than the user's saved choice: the stored settings could not be read
    /// (<see cref="ISettingsRepository.LastLoadFailed"/>), or a repair could not carry them over
    /// (<see cref="ScribeDatabase.SettingsLostInRepair"/>). The default keeps 90 days, and a user who
    /// chose to keep everything must not lose older text to a value they never picked. Audio limits
    /// are fixed policy and still apply. Safe before or after <see cref="Start"/>; cannot be undone.
    /// </summary>
    public void KeepAllTextThisSession()
    {
        if (_keepAllText)
        {
            return;
        }

        _keepAllText = true;
        TryLog(LogLevel.Information,
            "Storage maintenance will delete no history text this session: the settings in use are defaults, " +
            "not the saved choice. Stored audio limits still apply.");
    }

    /// <summary>
    /// Marks interactive work that writes on the UI thread, for as long as the returned scope lives:
    /// the tray's Learn from history, quick add, an open Settings window. A running VACUUM or
    /// reclamation step stops at once (it rolls back and is retried later), and the one-time VACUUM
    /// does not start again until every such scope is disposed, so a UI-thread write never meets the
    /// part of a VACUUM that cannot be interrupted. Never blocks; dispose on any thread; disposing
    /// twice is harmless.
    /// </summary>
    public IDisposable EnterForegroundWork()
    {
        Interlocked.Increment(ref _foregroundWork);
        _database.RequestYield();
        return new ForegroundWork(this);
    }

    /// <summary>The report of the most recent pass, for tests that drive passes through the timer.</summary>
    internal StorageMaintenanceReport? LastReport { get; private set; }

    /// <summary>
    /// Asks for a pass soon. Coalesced: a burst of requests becomes one pass, never sooner than
    /// <see cref="StorageMaintenanceOptions.MinimumSpacing"/> after the previous one, and a request
    /// during a pass becomes one more pass after it.
    /// </summary>
    /// <param name="reclaimEverything">
    /// Return every free page to the disk even below the usual threshold, as after Clear history.
    /// </param>
    internal void RequestRun(bool reclaimEverything = false)
    {
        lock (_lock)
        {
            if (!_started || _closed)
            {
                return;
            }

            _reclaimRequested |= reclaimEverything;
            if (_running)
            {
                RequestFollowUpLocked(_options.TriggerDelay);
                return;
            }

            var now = Elapsed;
            var due = Later(now + _options.TriggerDelay, _lastFinished + _options.MinimumSpacing);
            ArmLocked(due - now);
        }
    }

    /// <summary>
    /// Runs one pass on the calling thread and returns what it did, or <see langword="null"/> when a
    /// pass is already running or the service is disposed. Scheduled passes use the same guard.
    /// </summary>
    internal StorageMaintenanceReport? RunOnce(AppSettings? settings)
    {
        lock (_lock)
        {
            if (!TryBeginPassLocked())
            {
                return null;
            }
        }

        try
        {
            return RunPass(settings);
        }
        finally
        {
            EndPass();
        }
    }

    /// <summary>
    /// Stops maintenance for shutdown and waits at most <paramref name="timeout"/> for a pass in
    /// flight. No pass starts afterwards. A running VACUUM or reclamation step is interrupted with
    /// sqlite3_interrupt and SQLite rolls it back, so nothing is left half done; other steps are
    /// short and the pass stops at the next one. Returns true once no pass is running. Safe to call
    /// more than once, from any thread, and before <see cref="Dispose"/>.
    /// </summary>
    public bool Stop(TimeSpan timeout)
    {
        lock (_lock)
        {
            _closed = true;
            _stopRequested = true;
        }

        // The interrupt is repeated on every poll because one sent before the statement starts is a
        // no-op in SQLite. Past the bound the pass is left to finish on its own, which is safe:
        // SQLite commits or rolls back atomically even if the process then exits under it.
        var poll = _database.PreemptPollInterval;
        for (var waited = TimeSpan.Zero; ; waited += poll)
        {
            _database.InterruptPreemptible();
            lock (_lock)
            {
                if (!_running || _runningThread == Environment.CurrentManagedThreadId)
                {
                    return true;
                }

                if (waited >= timeout)
                {
                    break;
                }

                Monitor.Wait(_lock, poll);
            }
        }

        TryLog(LogLevel.Warning,
            "Storage maintenance was still finishing a step after {WaitedMs} ms at shutdown; it was left to complete on its own.",
            (long)timeout.TotalMilliseconds);
        return false;
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        _database.StorageChanged -= OnStorageChanged;

        // A callback already queued by the timer still runs after this, sees the service closed and
        // returns without touching the database.
        timer?.Dispose();
        Stop(_options.ShutdownWait);
    }

    private void OnTimer()
    {
        Func<AppSettings>? settings;
        lock (_lock)
        {
            _nextDue = TimeSpan.MaxValue;
            if (!TryBeginPassLocked())
            {
                return;
            }

            settings = _settings;
        }

        try
        {
            RunPass(ReadSettings(settings));
        }
        catch (Exception ex)
        {
            // RunPass guards every step; this is the last line so a timer thread never sees a throw.
            LogFailure("pass", ex);
        }
        finally
        {
            EndPass();
        }
    }

    private void OnStorageChanged(StorageChange change) =>
        RequestRun(reclaimEverything: change == StorageChange.HistoryCleared);

    private bool TryBeginPassLocked()
    {
        if (_closed)
        {
            return false;
        }

        if (_running)
        {
            RequestFollowUpLocked(_options.TriggerDelay);
            return false;
        }

        _running = true;
        _runningThread = Environment.CurrentManagedThreadId;
        _followUpDelay = TimeSpan.MaxValue;
        return true;
    }

    private void EndPass()
    {
        lock (_lock)
        {
            _running = false;
            _runningThread = 0;
            _lastFinished = Elapsed;
            if (_started && !_closed)
            {
                ArmLocked(_followUpDelay != TimeSpan.MaxValue ? _followUpDelay : _options.Interval);
            }

            _followUpDelay = TimeSpan.MaxValue;
            Monitor.PulseAll(_lock);
        }
    }

    // Only ever moves the next firing earlier; the timer is one-shot and re-armed after each pass.
    private void ArmLocked(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        var due = Elapsed + delay;
        if (_timer is null || due >= _nextDue)
        {
            return;
        }

        _nextDue = due;
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private TimeSpan Elapsed => _time.GetElapsedTime(_origin);

    // One more pass after this one, no sooner than the minimum spacing; the earliest request wins.
    private void RequestFollowUpLocked(TimeSpan delay)
    {
        var spaced = Later(delay, _options.MinimumSpacing);
        if (spaced < _followUpDelay)
        {
            _followUpDelay = spaced;
        }
    }

    private void RequestFollowUpPass(TimeSpan delay)
    {
        lock (_lock)
        {
            RequestFollowUpLocked(delay);
        }
    }

    private bool TakeReclaimRequest()
    {
        lock (_lock)
        {
            var requested = _reclaimRequested;
            _reclaimRequested = false;
            return requested;
        }
    }

    private AppSettings? ReadSettings(Func<AppSettings>? accessor)
    {
        if (accessor is null)
        {
            return null;
        }

        try
        {
            return accessor();
        }
        catch (Exception ex)
        {
            LogFailure("settings", ex);
            return null;
        }
    }

    // How long there has been no foreground activity (writes by anything but maintenance, and yield
    // requests). A change restarts the window from when this pass noticed it, which only ever makes
    // the window longer than the truth, never shorter.
    private TimeSpan QuietFor()
    {
        var count = _database.ActivityCount;
        var now = Elapsed;
        if (count != _quietCount)
        {
            _quietCount = count;
            _quietSince = now;
        }

        return now - _quietSince;
    }

    // True once the pass must stop: shutdown, or foreground activity since the pass began (a
    // dictation's activation or history write, a settings save). Checked between every heavy step.
    private bool Stopping
    {
        get
        {
            if (!_yieldedThisPass && _database.ActivityCount != _passActivity)
            {
                _yieldedThisPass = true;
            }

            return _stopRequested || _yieldedThisPass;
        }
    }

    private StorageMaintenanceReport RunPass(AppSettings? settings)
    {
        using var maintenanceThread = _database.EnterMaintenanceThread();
        var started = _time.GetTimestamp();
        var now = _time.GetUtcNow();
        var report = new StorageMaintenanceReport();
        QuietFor();
        _passActivity = _database.ActivityCount;
        _yieldedThisPass = false;
        _reclaimRetryIn = TimeSpan.Zero;
        if (_stopRequested)
        {
            // Disposed while the pass was reading settings: touch nothing, the database is closing.
            report.Stopped = true;
            return report;
        }

        // Light steps first: each is one short statement, and none of them rewrites audio.
        // Only with the user's own settings: a pass that cannot read them, or a session running on
        // defaults because they were unreadable or lost in a repair, must never delete their text.
        // Audio limits are fixed policy and do not depend on it.
        if (settings is { HistoryRetentionDays: > 0 } && !_keepAllText)
        {
            var days = Math.Min(settings.HistoryRetentionDays, MaxRetentionDays);
            report.RetentionDays = days;
            report.HistoryEntriesRemoved = Step(
                "history retention", 0, () => Gated(() => _history.DeleteEntriesOlderThan(now.AddDays(-days))));
        }

        if (!Stopping)
        {
            report.AudioEntriesExpired = Step(
                "audio retention", 0, () => Gated(() => _history.ClearAudioOlderThan(now - _options.AudioRetention)));
        }

        if (!Stopping)
        {
            report.CleanupFailuresRemoved = Step(
                "cleanup failure retention",
                0,
                () => Gated(() => _failureLog.PruneOlderThan(now - _options.CleanupFailureRetention)));
        }

        if (!Stopping && _database.FilePath is { } path)
        {
            report.DamagedCopies = Step(
                "damaged copy retention",
                default(DamagedCopyPruneResult),
                () => PruneDamagedCopies(path, now));
        }

        // Heavy steps: deleting audio in slices, the cap, and giving space back. Each one stops at
        // the next step boundary when foreground activity appears, and after a yield waits out a
        // backoff. The conversion VACUUM also waits for a quiet database and the app's answer.
        var heavyAllowed = HeavyWorkAllowed(out var heavyRetryIn);
        report.HeavyDeferred = !heavyAllowed;
        if (heavyAllowed && !Stopping)
        {
            report.Unreferenced = DeleteUnreferencedAudio(now);
        }

        if (heavyAllowed && !Stopping)
        {
            report.Evicted = EnforceAudioCap();
        }

        if (!_stopRequested)
        {
            report.Reclaim = heavyAllowed && !Stopping ? Reclaim(TakeReclaimRequest()) : BackfillOwedWal();
            report.AudioAfter = Step("audio usage", default(StoredAudioUsage), _history.GetStoredAudioUsage);
        }

        // Only a pass that was allowed heavy work can yield it; a light-only pass during a backoff
        // that sees a dictation does not stretch the backoff. Sampled again here so the quiet window
        // starts at the activity this pass saw, not at whenever the next pass notices it.
        report.Yielded = heavyAllowed && _yieldedThisPass && !_stopRequested;
        QuietFor();
        var heavyCompleted = heavyAllowed && !report.Yielded &&
                             report.Reclaim.Method is not (ReclaimMethod.DeferredForActivity or ReclaimMethod.Yielded);
        report.RetryIn = ScheduleAfterPass(report.Yielded, heavyAllowed, heavyCompleted, heavyRetryIn);
        report.Stopped = _stopRequested;
        report.LongestWriteGateWait = _database.TakeLongestWriteGateWait();
        report.Elapsed = _time.GetElapsedTime(started);
        LogReport(report);
        LastReport = report;
        return report;
    }

    // Heavy work is always allowed until a pass yields, and again as soon as the backoff after it
    // ends, even with Settings open or a dictation a minute ago: slices, the cap and the TRUNCATE
    // checkpoint each hold the gate only briefly and stop again at once for the next dictation. Only
    // the conversion VACUUM, which has a part nothing can interrupt, also waits for a quiet
    // database and the app's answer (ConvertAndVacuum).
    private bool HeavyWorkAllowed(out TimeSpan retryIn)
    {
        retryIn = TimeSpan.Zero;
        if (!_yieldPending)
        {
            return true;
        }

        var now = Elapsed;
        if (now < _heavyNotBefore)
        {
            retryIn = _heavyNotBefore - now;
            return false;
        }

        return true;
    }

    // Bounded retries with no storm: each consecutive yield doubles the wait before heavy work may
    // run again (2, 4, 8 ... minutes, never more than the hourly interval), and only a pass whose
    // heavy work ran to the end resets it. Passes triggered meanwhile do only light steps.
    private TimeSpan ScheduleAfterPass(bool yielded, bool heavyAllowed, bool heavyCompleted, TimeSpan heavyRetryIn)
    {
        if (_stopRequested)
        {
            return TimeSpan.Zero;
        }

        if (yielded)
        {
            _yieldPending = true;
            _consecutiveYields = Math.Min(_consecutiveYields + 1, 16);
            var backoff = TimeSpan.FromTicks(Math.Min(
                _options.ConversionQuietPeriod.Ticks << (_consecutiveYields - 1),
                _options.Interval.Ticks));
            _heavyNotBefore = Elapsed + backoff;
            RequestFollowUpPass(backoff);
            return backoff;
        }

        if (heavyCompleted)
        {
            _yieldPending = false;
            _consecutiveYields = 0;
            return TimeSpan.Zero;
        }

        if (heavyAllowed)
        {
            // Deferred inside the reclaim step, which has already asked for its own follow-up.
            return _reclaimRetryIn;
        }

        RequestFollowUpPass(heavyRetryIn);
        return heavyRetryIn;
    }

    // First-seen times survive restarts in the settings table; a copy is only ever deleted after this
    // build has known about it for the whole grace period, and the newest never is.
    private DamagedCopyPruneResult PruneDamagedCopies(string path, DateTimeOffset now)
    {
        var ledger = DamagedCopyLedger.Load(_database);
        var result = DamagedDatabaseCopies.Prune(path, now, _options.DamagedCopyRetention, ledger);
        if (result.LedgerChanged)
        {
            DamagedCopyLedger.Save(_database, ledger);
        }

        return result;
    }

    private AudioDeletion DeleteUnreferencedAudio(DateTimeOffset now)
    {
        var blobs = Step("audio inventory", (IReadOnlyList<StoredAudioBlob>)[], _history.ListStoredAudio);
        var storedBefore = now - _options.UnreferencedAudioGrace;
        var total = default(AudioDeletion);
        foreach (var slice in Slices(blobs.Where(blob => !blob.Referenced), _options.BlobBatchBytes, _options.BlobBatchSize))
        {
            if (Stopping)
            {
                break;
            }

            total += Step(
                "unreferenced audio", default(AudioDeletion), () => Gated(() => _history.DeleteUnreferencedAudio(slice, storedBefore)));
        }

        return total;
    }

    // Oldest audio first, by blob id (insertion order), until the stored total fits. A few rounds,
    // because a dictation can store audio between slices.
    private AudioDeletion EnforceAudioCap()
    {
        var total = default(AudioDeletion);
        for (var round = 0; round < 3 && !Stopping; round++)
        {
            var blobs = Step("audio inventory", (IReadOnlyList<StoredAudioBlob>)[], _history.ListStoredAudio);
            var stored = blobs.Sum(blob => blob.Bytes);
            if (stored <= _options.MaxStoredAudioBytes)
            {
                break;
            }

            var evict = new List<StoredAudioBlob>();
            foreach (var blob in blobs)
            {
                if (stored <= _options.MaxStoredAudioBytes)
                {
                    break;
                }

                evict.Add(blob);
                stored -= blob.Bytes;
            }

            foreach (var slice in Slices(evict, _options.BlobBatchBytes, _options.BlobBatchSize))
            {
                if (Stopping)
                {
                    break;
                }

                total += Step(
                    "audio cap", default(AudioDeletion), () => Gated(() => _history.EvictAudio(slice, _options.MaxStoredAudioBytes)));
            }
        }

        return total;
    }

    /// <summary>
    /// Groups blobs into slices of at most <paramref name="maxBytes"/> stored bytes and
    /// <paramref name="maxCount"/> blobs, in order; a blob larger than the byte limit is a slice of
    /// its own. Each slice is one transaction, so this bounds how long a writer waits behind one.
    /// </summary>
    internal static IEnumerable<long[]> Slices(IEnumerable<StoredAudioBlob> blobs, long maxBytes, int maxCount)
    {
        var slice = new List<long>();
        long bytes = 0;
        foreach (var blob in blobs)
        {
            if (slice.Count > 0 && (bytes + blob.Bytes > maxBytes || slice.Count >= maxCount))
            {
                yield return [.. slice];
                slice.Clear();
                bytes = 0;
            }

            slice.Add(blob.Id);
            bytes += blob.Bytes;
        }

        if (slice.Count > 0)
        {
            yield return [.. slice];
        }
    }

    private ReclaimOutcome Reclaim(bool reclaimEverything)
    {
        if (_database.FilePath is not { } path)
        {
            return default;
        }

        try
        {
            _vacuumTime = TimeSpan.Zero;
            using var connection = _database.Open();
            var stats = PageStats.Read(connection);
            var worthReclaiming = stats.FreeBytes >= _options.ReclaimThresholdBytes ||
                                  (reclaimEverything && stats.FreePages > 0);
            if (!worthReclaiming && !_checkpointPending && !_database.DeferAutoCheckpoint)
            {
                return new ReclaimOutcome(ReclaimMethod.None, stats.FreeBytes, 0, 0, CheckpointBusy: false);
            }

            var sizeBefore = DatabaseFilesLength(path);
            var method = ReclaimMethod.None;
            if (worthReclaiming)
            {
                method = stats.AutoVacuum switch
                {
                    AutoVacuumNone => ConvertAndVacuum(connection, path, stats),
                    AutoVacuumIncremental => VacuumIncrementally(connection, stats),

                    // FULL already truncates at every commit.
                    _ => ReclaimMethod.None,
                };
            }

            // A yield skips the TRUNCATE, which would hold the gate the dictation is waiting for;
            // the ungated PASSIVE backfill still runs.
            var busy = CheckpointWal(connection, truncate: !Stopping);
            _checkpointPending = busy || _yieldedThisPass;

            // The one-time conversion leaves a WAL as large as the live database, so a checkpoint a
            // reader blocked is worth retrying soon rather than in an hour. Bounded, so a reader that
            // never lets go costs a few quick passes and then only the hourly one. A yield is not
            // retried here: the pass schedules its own backoff.
            if (busy && _quickReclaimRetries < MaxQuickReclaimRetries)
            {
                _quickReclaimRetries++;
                RequestFollowUpPass(_options.TriggerDelay);
            }
            else if (!busy)
            {
                _quickReclaimRetries = 0;
            }

            return new ReclaimOutcome(method, stats.FreeBytes, sizeBefore, DatabaseFilesLength(path), busy, _vacuumTime);
        }
        catch (Exception ex)
        {
            LogFailure("space reclamation", ex);
            return new ReclaimOutcome(ReclaimMethod.Failed, 0, 0, 0, CheckpointBusy: false);
        }
    }

    // When heavy work is skipped or stopped, a backfill still owed after a VACUUM (writers have
    // automatic checkpoints off meanwhile) is done with an ungated PASSIVE checkpoint, which never
    // blocks a writer, so the WAL cannot grow for as long as the user keeps dictating.
    private ReclaimOutcome BackfillOwedWal()
    {
        if (!_database.DeferAutoCheckpoint || _database.FilePath is null)
        {
            return default;
        }

        try
        {
            using var connection = _database.Open();
            var (log, backfilled) = Checkpoint(connection, "PASSIVE");
            if (log >= 0 && log == backfilled)
            {
                _database.DeferAutoCheckpoint = false;
            }
        }
        catch (Exception ex)
        {
            LogFailure("wal backfill", ex);
        }

        return default;
    }

    private ReclaimMethod ConvertAndVacuum(SqliteConnection connection, string path, PageStats stats)
    {
        if (_conversionBlocked)
        {
            return ReclaimMethod.None;
        }

        var margin = _options.VacuumFreeSpaceMarginBytes;
        if (!HasRoomForVacuum(
                stats.LiveBytes,
                _options.FreeSpaceProbe(Path.GetDirectoryName(path)),
                _options.FreeSpaceProbe(Path.GetTempPath()),
                margin))
        {
            if (!_loggedSpaceDeferral)
            {
                _loggedSpaceDeferral = true;
                TryLog(LogLevel.Information,
                    "Storage maintenance deferred compacting the database: it needs about {NeededMb:F0} MB of free disk space " +
                    "beside the database and {TempMb:F0} MB in the temporary folder.",
                    ToMegabytes(VacuumSpaceNeeded(stats.LiveBytes, margin).Database),
                    ToMegabytes(VacuumSpaceNeeded(stats.LiveBytes, margin).Temp));
            }

            return ReclaimMethod.DeferredForDiskSpace;
        }

        // Only while the app says nothing interactive is going on, into a quiet database, and only
        // if the gate is free this instant: a VACUUM that has to queue behind a writer is one more
        // writer is likely to follow. A "not now" from the app is asked again a quiet period later,
        // so an open Settings window costs one short pass every few minutes, not a polling loop.
        var quietFor = QuietFor();
        var allowed = HeavyMaintenanceAllowed();
        if (!allowed || quietFor < _options.ConversionQuietPeriod || _database.WaitingWriters > 0)
        {
            _reclaimRetryIn = allowed && quietFor < _options.ConversionQuietPeriod
                ? _options.ConversionQuietPeriod - quietFor
                : _options.ConversionQuietPeriod;
            RequestFollowUpPass(_reclaimRetryIn);
            return ReclaimMethod.DeferredForActivity;
        }

        using (var gate = _database.TryEnterWriteScope())
        {
            if (!gate.Held)
            {
                _reclaimRetryIn = _options.ConversionQuietPeriod;
                RequestFollowUpPass(_reclaimRetryIn);
                return ReclaimMethod.DeferredForActivity;
            }

            try
            {
                // Automatic checkpoints off for this connection: its commit would otherwise copy the
                // whole database from the WAL inside the part of the VACUUM no writer can interrupt.
                // The ungated PASSIVE checkpoint after it does that copy instead.
                Execute(connection, "PRAGMA wal_autocheckpoint=0; PRAGMA auto_vacuum=INCREMENTAL;");
                using (_database.EnterPreemptible(connection))
                {
                    // Asked again at the last moment, as the app requires: a Settings window that
                    // opened, a dictation that started or a settings write since the checks above.
                    _options.BeforeLastCheck?.Invoke();
                    if (Stopping || _database.WaitingWriters > 0 || !HeavyMaintenanceAllowed())
                    {
                        _yieldedThisPass = true;
                        return ReclaimMethod.Yielded;
                    }

                    // From here on, foreground activity (a waiting writer, a dictation's yield, a settings
                    // save) stops the VACUUM within one poll interval or a few hundred bytecode steps,
                    // except during its final copy back into the database file.
                    _options.BeforePreemptibleStatement?.Invoke();

                    // Set before the VACUUM rather than after it: a connection opened while it runs,
                    // such as a settings save waiting out its final copy, must also leave the backfill
                    // of the rebuilt database to maintenance instead of doing it on its own thread at
                    // its next commit. Restored if the VACUUM does not commit, since it then wrote
                    // nothing to the WAL.
                    var deferredBefore = _database.DeferAutoCheckpoint;
                    _database.DeferAutoCheckpoint = true;
                    var committed = false;
                    var started = _time.GetTimestamp();
                    try
                    {
                        Execute(connection, "VACUUM;");
                        committed = true;
                    }
                    finally
                    {
                        _vacuumTime += _time.GetElapsedTime(started);
                        if (!committed)
                        {
                            _database.DeferAutoCheckpoint = deferredBefore;
                        }
                    }
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteInterrupt)
            {
                // A dictation, a writer or shutdown preempted it. sqlite.org and the VACUUM source
                // agree: the main database's write transaction is only committed by the final copy,
                // so an interrupt before it rolls everything back and nothing changed. The pass
                // schedules the retry with its backoff, for once the app is idle again.
                _yieldedThisPass = true;
                return ReclaimMethod.Yielded;
            }
            catch (SqliteException ex) when (!IsBusy(ex))
            {
                // Out of space, I/O or the like: not worth a full rewrite every hour this session.
                _conversionBlocked = true;
                throw;
            }
        }

        if (QueryInt64(connection, "PRAGMA auto_vacuum;") != AutoVacuumIncremental)
        {
            _conversionBlocked = true;
            TryLog(LogLevel.Warning,
                "Storage maintenance compacted the database, but it did not switch to incremental auto-vacuum.");
        }

        return ReclaimMethod.ConvertedToIncremental;
    }

    // Never throws: an app callback that fails reads as "not now", the safe answer. Foreground work
    // the app has marked (EnterForegroundWork) is a "not now" too.
    private bool HeavyMaintenanceAllowed()
    {
        if (Volatile.Read(ref _foregroundWork) > 0)
        {
            return false;
        }

        try
        {
            return _heavyMaintenanceAllowed?.Invoke() ?? true;
        }
        catch (Exception ex)
        {
            LogFailure("heavy maintenance check", ex);
            return false;
        }
    }

    private ReclaimMethod VacuumIncrementally(SqliteConnection connection, PageStats stats)
    {
        var stepPages = Math.Max(MinimumStepPages, _options.IncrementalStepBytes / stats.PageSize);
        var command = string.Create(CultureInfo.InvariantCulture, $"PRAGMA incremental_vacuum({stepPages});");
        var maxSteps = (stats.FreePages / stepPages) + 2;
        for (var step = 0; step < maxSteps; step++)
        {
            if (Stopping)
            {
                return ReclaimMethod.Yielded;
            }

            try
            {
                using (_database.EnterWriteScope())
                using (_database.EnterPreemptible(connection))
                {
                    if (Stopping)
                    {
                        return ReclaimMethod.Yielded;
                    }

                    _options.BeforePreemptibleStatement?.Invoke();
                    var started = _time.GetTimestamp();
                    try
                    {
                        Execute(connection, command);
                    }
                    finally
                    {
                        _vacuumTime += _time.GetElapsedTime(started);
                    }
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteInterrupt)
            {
                // The step rolled back; the pages freed by earlier steps stay freed.
                _yieldedThisPass = true;
                return ReclaimMethod.Yielded;
            }

            if (QueryInt64(connection, "PRAGMA freelist_count;") == 0)
            {
                break;
            }
        }

        return ReclaimMethod.Incremental;
    }

    // Backfill first, without the gate: a PASSIVE checkpoint never waits for or blocks readers or
    // writers, and with DeferAutoCheckpoint set no writer's commit does the copy on its own thread.
    // Only once that has copied everything does TRUNCATE empty the WAL file. It blocks writers while
    // it runs (sqlite.org), so it takes the gate, and with nothing left to copy it only has to wait
    // briefly for readers and reset the log. When PASSIVE could not finish (a reader still needs
    // older frames), the checkpoint counts as busy and the next pass tries again, rather than holding
    // the gate while TRUNCATE waits.
    private bool CheckpointWal(SqliteConnection connection, bool truncate)
    {
        var (log, backfilled) = Checkpoint(connection, "PASSIVE");
        var caughtUp = log >= 0 && log == backfilled;
        if (caughtUp)
        {
            _database.DeferAutoCheckpoint = false;
        }

        if (!truncate)
        {
            return false;
        }

        if (!caughtUp)
        {
            return true;
        }

        using var gate = _database.EnterWriteScope();
        SetBusyTimeout(connection, _options.CheckpointBusyTimeout);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = command.ExecuteReader();
            var busy = reader.Read() && reader.GetInt64(0) != 0;
            if (!busy)
            {
                _database.DeferAutoCheckpoint = false;
            }

            return busy;
        }
        finally
        {
            SetBusyTimeout(connection, TimeSpan.FromMilliseconds(ScribeDatabase.BusyTimeoutMs));
        }
    }

    // Columns 2 and 3 of the checkpoint row: frames in the WAL and frames already copied back, both
    // -1 when the checkpoint could not run (sqlite.org), which never counts as caught up.
    private static (long Log, long Backfilled) Checkpoint(SqliteConnection connection, string mode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA wal_checkpoint({mode});";
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(1), reader.GetInt64(2)) : (-1, -1);
    }

    private static void SetBusyTimeout(SqliteConnection connection, TimeSpan timeout) =>
        Execute(connection, string.Create(
            CultureInfo.InvariantCulture, $"PRAGMA busy_timeout={(long)Math.Max(0, timeout.TotalMilliseconds)};"));

    // sqlite.org, tempfiles.html section 2.9: VACUUM rebuilds the database into a temporary file,
    // then copies that back into the original, which in WAL mode goes through the WAL. Both copies
    // hold only the live pages, so the database's volume needs about twice the live bytes (the WAL
    // copy, plus the temporary file when TEMP is on the same volume, as it usually is) and the
    // temporary folder's volume about the live bytes, each with a tenth more in case the rebuild
    // packs pages less tightly, plus a fixed margin. Sized on live bytes rather than the whole file:
    // a database that is mostly free pages, on a disk that is nearly full, is exactly the one that
    // needs its space back. When Windows cannot report free space the VACUUM proceeds; SQLite rolls
    // it back cleanly if space runs out.
    internal static bool HasRoomForVacuum(long liveBytes, long? databaseFree, long? tempFree, long margin)
    {
        var (database, temp) = VacuumSpaceNeeded(liveBytes, margin);
        return (databaseFree is null || databaseFree >= database) && (tempFree is null || tempFree >= temp);
    }

    internal static (long Database, long Temp) VacuumSpaceNeeded(long liveBytes, long margin)
    {
        var rebuilt = (long)Math.Ceiling(liveBytes * 1.1);
        return ((2 * rebuilt) + margin, rebuilt + margin);
    }

    private T Gated<T>(Func<T> action)
    {
        using var writeScope = _database.EnterWriteScope();
        return action();
    }

    private T Step<T>(string step, T fallback, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            LogFailure(step, ex);
            return fallback;
        }
    }

    private static bool IsBusy(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY, SQLITE_LOCKED

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long QueryInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static long DatabaseFilesLength(string path) =>
        FileLength(path) + FileLength(path + "-wal");

    private static long FileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static TimeSpan Later(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private static double ToMegabytes(long bytes) => bytes / (1024d * 1024d);

    private void LogReport(StorageMaintenanceReport report)
    {
        var level = report.ChangedAnything ? LogLevel.Information : LogLevel.Debug;
        TryLog(level,
            "Storage maintenance: removed {HistoryRemoved} history entries (text retention {RetentionDays} days, 0 keeps " +
            "text); cleared audio from {AudioExpired} entries older than {AudioDays} days; evicted {Evicted} recordings " +
            "for the {CapMb} MB audio cap; deleted {Unreferenced} unreferenced recordings; freed {AudioFreedMb:F1} MB " +
            "of audio; removed {FailuresRemoved} cleanup failures and {CopyFiles} damaged database copy files. Stored " +
            "audio now {AudioBlobs} recordings, {AudioMb:F1} MB. Longest write-gate wait {GateWaitMs} ms. Took " +
            "{ElapsedMs} ms{Stopped}.",
            report.HistoryEntriesRemoved,
            report.RetentionDays,
            report.AudioEntriesExpired,
            (int)_options.AudioRetention.TotalDays,
            report.Evicted.BlobsDeleted,
            (long)ToMegabytes(_options.MaxStoredAudioBytes),
            report.Unreferenced.BlobsDeleted,
            ToMegabytes(report.Evicted.BytesDeleted + report.Unreferenced.BytesDeleted),
            report.CleanupFailuresRemoved,
            report.DamagedCopies.FilesDeleted,
            report.AudioAfter.Blobs,
            ToMegabytes(report.AudioAfter.Bytes),
            (long)report.LongestWriteGateWait.TotalMilliseconds,
            (long)report.Elapsed.TotalMilliseconds,
            report.Stopped ? ", stopped for shutdown" : string.Empty);

        if (report.Yielded || report.HeavyDeferred)
        {
            TryLog(report.Yielded ? LogLevel.Information : LogLevel.Debug,
                "Storage maintenance {Action} for foreground activity; heavy work is tried again in {RetrySeconds} s, " +
                "once the app is idle.",
                report.Yielded ? "stopped heavy work" : "skipped heavy work",
                (long)report.RetryIn.TotalSeconds);
        }

        var reclaimed = report.Reclaim.Method is ReclaimMethod.ConvertedToIncremental or ReclaimMethod.Incremental;
        var deferred = report.Reclaim.Method is ReclaimMethod.DeferredForActivity or ReclaimMethod.Yielded;
        if (reclaimed || deferred || report.Reclaim.CheckpointBusy)
        {
            TryLog(reclaimed || report.Reclaim.CheckpointBusy ? LogLevel.Information : LogLevel.Debug,
                "Storage maintenance space reclamation ({Method}): VACUUM {VacuumMs} ms; {FreeMb:F1} MB of free pages; " +
                "database files {BeforeMb:F1} MB to {AfterMb:F1} MB; checkpoint blocked by a reader: {Busy}.",
                report.Reclaim.Method,
                (long)report.Reclaim.VacuumTime.TotalMilliseconds,
                ToMegabytes(report.Reclaim.FreeBytesBefore),
                ToMegabytes(report.Reclaim.FileBytesBefore),
                ToMegabytes(report.Reclaim.FileBytesAfter),
                report.Reclaim.CheckpointBusy);
        }
    }

    // Exception messages are never logged: an IOException names the file (and so the user's
    // profile folder), and the codes are enough to tell a locked database from a full disk.
    private void LogFailure(string step, Exception ex)
    {
        if (ex is SqliteException sqlite)
        {
            TryLog(LogLevel.Warning,
                "Storage maintenance step '{Step}' failed with SQLite error {Code} (extended {ExtendedCode}); the next pass retries it.",
                step, sqlite.SqliteErrorCode, sqlite.SqliteExtendedErrorCode);
        }
        else
        {
            TryLog(LogLevel.Warning,
                "Storage maintenance step '{Step}' failed with {Type} (HRESULT 0x{HResult:X8}); the next pass retries it.",
                step, ex.GetType().Name, ex.HResult);
        }
    }

    private void TryLog(LogLevel level, string message, params object?[] args)
    {
        try
        {
            _logger.Log(level, message, args);
        }
        catch
        {
            // Diagnostics must never become the failure.
        }
    }

    private sealed class ForegroundWork(StorageMaintenance owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._foregroundWork);
            }
        }
    }

    private readonly record struct PageStats(long PageSize, long PageCount, long FreePages, long AutoVacuum)
    {
        public long FreeBytes => FreePages * PageSize;

        /// <summary>Bytes of pages in use: what a VACUUM rebuilds and then writes back.</summary>
        public long LiveBytes => (PageCount - FreePages) * PageSize;

        public static PageStats Read(SqliteConnection connection) => new(
            QueryInt64(connection, "PRAGMA page_size;"),
            QueryInt64(connection, "PRAGMA page_count;"),
            QueryInt64(connection, "PRAGMA freelist_count;"),
            QueryInt64(connection, "PRAGMA auto_vacuum;"));
    }
}

/// <summary>Limits and timing for <see cref="StorageMaintenance"/>. Tests shrink them.</summary>
internal sealed record StorageMaintenanceOptions
{
    public static StorageMaintenanceOptions Default { get; } = new();

    public TimeSpan AudioRetention { get; init; } = StorageRetentionPolicy.AudioRetention;

    public long MaxStoredAudioBytes { get; init; } = StorageRetentionPolicy.MaxStoredAudioBytes;

    public TimeSpan CleanupFailureRetention { get; init; } = StorageRetentionPolicy.CleanupFailureRetention;

    public TimeSpan DamagedCopyRetention { get; init; } = StorageRetentionPolicy.DamagedCopyRetention;

    /// <summary>
    /// An unreferenced blob younger than this may belong to an AddAudioBlob whose entry has not been
    /// added yet, so it is left for a later pass.
    /// </summary>
    public TimeSpan UnreferencedAudioGrace { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Free pages below this are left alone: the next recordings reuse them, and shrinking the file
    /// only for it to grow again is wasted I/O. Clear history reclaims regardless.
    /// </summary>
    public long ReclaimThresholdBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>
    /// Pages each incremental_vacuum step frees, expressed in bytes. Its commit (and the automatic
    /// checkpoint that follows it) cannot be interrupted, so this bounds each write-gate hold.
    /// </summary>
    public long IncrementalStepBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>
    /// Stored bytes deleted per transaction. Deleting a blob reads its whole overflow chain, so this,
    /// not the blob count, is what bounds how long a writer can wait behind one slice.
    /// </summary>
    public long BlobBatchBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Most blobs per transaction, for slices of many small blobs.</summary>
    public int BlobBatchSize { get; init; } = 64;

    public long VacuumFreeSpaceMarginBytes { get; init; } = 64L * 1024 * 1024;

    public Func<string?, long?> FreeSpaceProbe { get; init; } = FreeDiskSpace.TryGetAvailableBytes;

    /// <summary>
    /// How long nobody but maintenance must have written before the one-time conversion VACUUM
    /// starts, on top of the app's own "nothing interactive is open" answer. A writer interrupts the
    /// VACUUM almost at once for most of its run, but SQLite cannot interrupt its final copy back
    /// into the database file: measured at about 5 to 7 ms per MB of live data here, about 0.2 s for
    /// a typical upgrade and up to about 2.5 s at the audio cap.
    /// </summary>
    public TimeSpan ConversionQuietPeriod { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long the closing TRUNCATE checkpoint waits for readers. Short, because the write gate is
    /// held meanwhile; a blocked checkpoint is retried by a follow-up pass.
    /// </summary>
    public TimeSpan CheckpointBusyTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Longest <see cref="StorageMaintenance.Dispose"/> waits for a pass in flight.</summary>
    public TimeSpan ShutdownWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Test seam: runs just before a preemptible statement, after it is registered.</summary>
    public Action? BeforePreemptibleStatement { get; init; }

    /// <summary>Test seam: runs just before the conversion's last-moment checks.</summary>
    public Action? BeforeLastCheck { get; init; }

    /// <summary>
    /// First pass after startup: after the recognizer warm-load has done its disk reads, and soon
    /// enough that a short session still applies retention.
    /// </summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hourly: the audio window is seven days, so an hour late is well under one percent of it, and
    /// the cap is also checked right after audio is stored. What is left for the hourly pass is a
    /// handful of indexed queries.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Delay after audio is stored or history deleted, so a burst becomes one pass.</summary>
    public TimeSpan TriggerDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Least time between the end of one pass and the start of a triggered one.</summary>
    public TimeSpan MinimumSpacing { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>What one maintenance pass did.</summary>
internal sealed class StorageMaintenanceReport
{
    public int RetentionDays { get; set; }

    public int HistoryEntriesRemoved { get; set; }

    public int AudioEntriesExpired { get; set; }

    public AudioDeletion Unreferenced { get; set; }

    public AudioDeletion Evicted { get; set; }

    public int CleanupFailuresRemoved { get; set; }

    public DamagedCopyPruneResult DamagedCopies { get; set; }

    public ReclaimOutcome Reclaim { get; set; }

    public StoredAudioUsage AudioAfter { get; set; }

    /// <summary>
    /// Longest any writer other than maintenance waited for the write gate since the previous pass.
    /// </summary>
    public TimeSpan LongestWriteGateWait { get; set; }

    public bool Stopped { get; set; }

    /// <summary>
    /// Heavy work (audio deletion, the cap, reclamation) stopped for foreground activity: a
    /// dictation starting, a history write, a settings save. It is retried after a backoff, once
    /// the app is idle and quiet again.
    /// </summary>
    public bool Yielded { get; set; }

    /// <summary>Heavy work was skipped this pass: a backoff after a yield is still running, or the app is busy.</summary>
    public bool HeavyDeferred { get; set; }

    /// <summary>When heavy work is next tried, after a yield or a deferral; zero when not set by this pass.</summary>
    public TimeSpan RetryIn { get; set; }

    public TimeSpan Elapsed { get; set; }

    public bool ChangedAnything =>
        HistoryEntriesRemoved > 0 || AudioEntriesExpired > 0 || Unreferenced.BlobsDeleted > 0 ||
        Evicted.BlobsDeleted > 0 || CleanupFailuresRemoved > 0 || DamagedCopies.FilesDeleted > 0 ||
        Reclaim.Method is ReclaimMethod.ConvertedToIncremental or ReclaimMethod.Incremental;
}

internal enum ReclaimMethod
{
    /// <summary>Nothing worth reclaiming, or not a file database.</summary>
    None,

    /// <summary>The one-time switch from auto_vacuum NONE to INCREMENTAL, through VACUUM.</summary>
    ConvertedToIncremental,

    /// <summary>Freed pages returned with <c>PRAGMA incremental_vacuum</c>.</summary>
    Incremental,

    /// <summary>The conversion was skipped because the disk lacks room for VACUUM.</summary>
    DeferredForDiskSpace,

    /// <summary>The conversion waits for a quiet database and a free write gate; a later pass retries.</summary>
    DeferredForActivity,

    /// <summary>A writer or shutdown preempted a VACUUM or an incremental step; a later pass resumes.</summary>
    Yielded,

    /// <summary>Reclamation failed; the next pass retries.</summary>
    Failed,
}

/// <summary>What reclamation did, with file sizes covering <c>scribe.db</c> and its WAL.</summary>
internal readonly record struct ReclaimOutcome(
    ReclaimMethod Method,
    long FreeBytesBefore,
    long FileBytesBefore,
    long FileBytesAfter,
    bool CheckpointBusy,
    TimeSpan VacuumTime = default);
