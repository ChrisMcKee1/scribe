using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;

namespace Scribe.Core.Persistence;

/// <summary>
/// Owns the SQLite connection string, performs one-time provider initialization and schema
/// migration, and hands out configured short-lived connections. A single keep-alive connection
/// is retained for the database's lifetime so that an in-memory (test) database is not torn down
/// between operations and the file database keeps its WAL active.
/// </summary>
public sealed class ScribeDatabase : IDisposable
{
    // bundle_e_sqlite3 3.0.5 ships native SQLite 3.53.4, well past the CVE-2025-6965 fix in 3.50.2.
    // The runtime smoke test asserts this exact version, so a transitive bundle silently replacing
    // the pinned native fails the build rather than shipping. Bump this constant deliberately
    // alongside the package, and never downgrade it below 3.50.2.
    public const string ExpectedSqliteVersion = "3.53.4";

    internal const int BusyTimeoutMs = 10_000;

    // Deliberately still 7. Every build up to 0.4.2 throws at startup on a user_version above its
    // own, and the Store and direct-download builds share ScribeData, so bumping it would lock out
    // anyone who moves between channels, rolls back, or runs a Store build that updates later. The
    // later additions (audio_blobs.encoding, ix_history_audio_blob) are ones those builds read and
    // write without noticing, so EnsureAdditiveSchema applies them on every open instead.
    private const int SchemaVersion = 7;

    // Tables copied out of a damaged database during salvage, ordered so foreign-key targets
    // (audio_blobs) are restored before the rows that reference them (history). TryCopyTable copies
    // every column the damaged file and the fresh schema share, which is what carries
    // audio_blobs.encoding across a repair; a file written only by older builds has no such column
    // and its blobs take the float32 default, which is what they are.
    private static readonly string[] SalvageTables =
        { "settings", "dictionary", "snippets", "audio_blobs", "history", "cleanup_failures" };

    private static int s_providerInitialized;

    private readonly string _connectionString;
    private readonly bool _isMemory;
    private readonly ILogger<ScribeDatabase> _logger;
    private readonly object _gate = new();

    // Serializes Scribe's writers against storage maintenance, whose VACUUM holds SQLite's write
    // lock for longer than the busy timeout can be trusted to cover. A writer waits here and then
    // succeeds instead of failing with SQLITE_BUSY. It is an I/O gate rather than a state lock:
    // SQLite admits one writer at a time anyway, so holding it across the write costs a writer
    // nothing it was not already paying. Maintenance keeps its holds short (bounded slices), and its
    // one long statement, the VACUUM, is preemptible: a waiting writer interrupts it rather than
    // waiting behind it. Settings writes do not take the gate at all, because they run on the UI
    // thread (Settings save, the tray AI toggle): they call RequestYield and let SQLite's busy
    // handling order them. Only the VACUUM's final copy back cannot be interrupted, which is why it
    // runs only when the app reports nothing interactive is open and after a quiet period
    // (StorageMaintenanceOptions.ConversionQuietPeriod). Lock order: always taken before _gate (Open
    // takes _gate inside it), never while holding any other lock, and never across an await.
    private readonly Lock _writeGate = new();
    private int _waitingWriters;

    // The connection running a preemptible maintenance statement, if any. Guarded by _preemptLock,
    // which also keeps the connection from closing while sqlite3_interrupt runs against it.
    private readonly Lock _preemptLock = new();
    private SqliteConnection? _preemptible;
    private long _preemptActivity;

    // sqlite3_interrupt is a no-op while no statement runs on the connection, so a yield raised just
    // before a preemptible statement starts would be lost, and a yield is raised once rather than
    // repeated like a waiting writer's interrupt. This progress handler makes it stick: SQLite calls
    // it every PreemptProgressSteps bytecode steps of the registered statement, and a non-zero answer
    // aborts that statement with SQLITE_INTERRUPT, rolled back exactly like an interrupt.
    private const int PreemptProgressSteps = 100;
    private static readonly SQLitePCL.delegate_progress s_onPreemptibleProgress =
        static state => state is ScribeDatabase database && database.ShouldAbortPreemptible() ? 1 : 0;

    private int _maintenanceThreadId;
    private long _activity;
    private long _longestGateWaitTicks;
    private volatile bool _deferAutoCheckpoint;

    // SQLite's compiled default, stated here because Open switches it off while maintenance owes the
    // WAL a large backfill (see DeferAutoCheckpoint).
    private const int WalAutoCheckpointPages = 1000;

    // Far above what ordinary use leaves in the WAL (auto-checkpoints keep it near 4 MB, and a clean
    // shutdown truncates it), so only an unfinished maintenance backfill reaches it.
    private const long DeferredBackfillWalBytes = 16L * 1024 * 1024;

    private SqliteConnection? _keepAlive;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// True when startup found the database file corrupted and rebuilt it from whatever rows were
    /// still readable. The app can surface this to the user (some history may be missing); the
    /// damaged original is kept beside the database as <c>scribe.db.corrupt-*</c>. The newest copy
    /// is kept indefinitely; older ones are removed only once this build has known about them for
    /// <see cref="StorageRetentionPolicy.DamagedCopyRetentionDays"/> days (<see cref="DamagedDatabaseCopies"/>).
    /// </summary>
    public bool RepairedAtStartup { get; private set; }

    /// <summary>
    /// True when a startup repair could not carry a readable settings document into the rebuilt
    /// database, so the app is running on compiled defaults. This is the one salvage failure a user
    /// cannot shrug off (hotkeys, AI cleanup and libraries all silently reset), so the shell must say
    /// it plainly instead of the generic "some history may be missing". Judged by the document itself,
    /// never by how many settings rows came back. The loss is also recorded in the database before the repair
    /// recovers anything, so later starts treat the missing document the same way until a readable one is saved
    /// (<see cref="SettingsRepository"/>), whenever a crash or a failure ends the repair.
    /// </summary>
    public bool SettingsLostInRepair { get; private set; }

    /// <summary>
    /// True when a startup repair recovered no dictionary entry, so the dictionary the user sees is the
    /// seed Scribe installs into an empty one, and the repair notice must not claim otherwise.
    /// </summary>
    public bool DictionaryLostInRepair { get; private set; }

    /// <summary>
    /// Raised after a committed write that changes how much space history or its audio uses, so
    /// <see cref="StorageMaintenance"/> can enforce limits soon instead of on its next hourly pass.
    /// Lives on the database rather than a repository because every repository instance over this
    /// file writes through it. Raised outside the write gate; handlers must be quick.
    /// </summary>
    internal event Action<StorageChange>? StorageChanged;

    /// <summary>The database file, or <see langword="null"/> for an in-memory test database.</summary>
    internal string? FilePath => _isMemory ? null : DataSourcePath();

    /// <summary>Writers currently blocked on <see cref="EnterWriteScope"/>.</summary>
    internal int WaitingWriters => Volatile.Read(ref _waitingWriters);

    /// <summary>
    /// Foreground activity seen so far: every write-gate entry made by anything other than the
    /// storage maintenance pass, plus every <see cref="RequestYield"/>. Maintenance compares it
    /// between steps to stop for a dictation, and over time to tell a quiet database from a busy one,
    /// without counting its own work.
    /// </summary>
    internal long ActivityCount => Interlocked.Read(ref _activity);

    /// <summary>
    /// While set, connections from <see cref="Open"/> have automatic checkpoints off. Maintenance sets
    /// it straight after a VACUUM, whose WAL holds the whole database: sqlite.org runs an automatic
    /// checkpoint "by the same thread that does the COMMIT", so the next writer, possibly the UI
    /// thread, would otherwise copy all of it into the database file. Maintenance backfills with an
    /// ungated PASSIVE checkpoint on its own thread and then clears it.
    /// </summary>
    internal bool DeferAutoCheckpoint
    {
        get => _deferAutoCheckpoint;
        set => _deferAutoCheckpoint = value;
    }

    /// <summary>
    /// Longest a writer waits for the gate before going ahead without it, where SQLite's own busy
    /// handling applies exactly as it did before the gate existed. Far longer than any real
    /// maintenance hold, so it only matters if a holder is stuck: a writer on the UI thread then
    /// fails the way it always could, instead of hanging the app. Settable for tests.
    /// </summary>
    internal TimeSpan WriteGateTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often a waiting writer repeats its interrupt. sqlite3_interrupt is a no-op when no
    /// statement is running, so a single call made just before a preemptible statement starts would
    /// be lost; repeating it bounds how long the statement can run on. Settable for tests.
    /// </summary>
    internal TimeSpan PreemptPollInterval { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Bound on the write-gate wait in <see cref="Dispose"/>, so exit never waits long.</summary>
    internal TimeSpan ShutdownGateTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Test seam: runs during a startup repair each time a step of the rebuild has committed, with the rebuilt
    /// database's connection and the step: "schema" before anything is recorded or recovered, then the name of each
    /// table salvaged. A test can capture what a crash at that moment leaves on disk, or make the next step fail.
    /// Unset in the app.
    /// </summary>
    internal Action<SqliteConnection, string>? RepairStepCommitted { get; set; }

    /// <summary>
    /// Enters the write gate for one write operation. Dispose the scope to leave it. Reentrant on the
    /// same thread, so a maintenance step can wrap repository calls that enter it again. While it
    /// waits, it interrupts any preemptible maintenance statement holding the gate.
    /// </summary>
    internal WriteScope EnterWriteScope()
    {
        var external = Environment.CurrentManagedThreadId != Volatile.Read(ref _maintenanceThreadId);
        if (external)
        {
            Interlocked.Increment(ref _activity);
        }

        return EnterWriteScope(WriteGateTimeout, recordWait: external);
    }

    /// <summary>Enters the write gate only if it is free right now; otherwise the scope is not held.</summary>
    internal WriteScope TryEnterWriteScope() =>
        _writeGate.TryEnter() ? new WriteScope(_writeGate) : default;

    /// <summary>
    /// Foreground work is starting (a dictation, a history write, a settings save): interrupts a
    /// running preemptible VACUUM or reclamation step, or one registered and about to start, which
    /// SQLite rolls back, and makes maintenance stop at its next step and stay out of the way until
    /// the app is idle again. Never blocks, so it is safe from the UI thread and the keyboard hook's
    /// callers.
    /// </summary>
    internal void RequestYield()
    {
        Interlocked.Increment(ref _activity);
        InterruptPreemptible();
    }

    /// <summary>
    /// The longest time a writer other than maintenance waited for the write gate since the last
    /// call, then resets it. Maintenance logs it with each pass.
    /// </summary>
    internal TimeSpan TakeLongestWriteGateWait() =>
        TimeSpan.FromTicks(Interlocked.Exchange(ref _longestGateWaitTicks, 0));

    private WriteScope EnterWriteScope(TimeSpan timeout, bool recordWait = false)
    {
        if (_writeGate.TryEnter())
        {
            return new WriteScope(_writeGate);
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _waitingWriters);
        try
        {
            var poll = PreemptPollInterval;
            for (var waited = TimeSpan.Zero; waited < timeout; waited += poll)
            {
                InterruptPreemptible();
                if (_writeGate.TryEnter(poll))
                {
                    return new WriteScope(_writeGate);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waitingWriters);
            if (recordWait)
            {
                RecordGateWait(System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }
        }

        try
        {
            _logger.LogWarning(
                "A database write waited {Seconds:F0} s for the write gate and is going ahead without it.",
                timeout.TotalSeconds);
        }
        catch
        {
            // Diagnostics must never turn a write that can still succeed into a failure.
        }

        return default;
    }

    /// <summary>
    /// Registers <paramref name="connection"/> as running a statement that yields to writers: until
    /// the scope is disposed, a writer that has to wait for the write gate interrupts it instead, and
    /// so does any foreground activity from now on (a waiting writer, a gate entry by anything but
    /// maintenance, <see cref="RequestYield"/>), even if it happened before the statement started.
    /// The statement then fails with SQLITE_INTERRUPT and SQLite rolls it back. Only for maintenance
    /// work that is safe to abandon and retry, run by a thread that holds the write gate.
    /// </summary>
    internal PreemptibleScope EnterPreemptible(SqliteConnection connection)
    {
        var activity = Interlocked.Read(ref _activity);
        if (connection.Handle is { } handle)
        {
            SQLitePCL.raw.sqlite3_progress_handler(handle, PreemptProgressSteps, s_onPreemptibleProgress, this);
        }

        lock (_preemptLock)
        {
            Volatile.Write(ref _preemptActivity, activity);
            _preemptible = connection;
        }

        return new PreemptibleScope(this, connection);
    }

    /// <summary>
    /// Test seam: runs inside SQLite's progress callback while a preemptible statement executes, on
    /// the thread running it, before the check for foreground activity. Unset in the app.
    /// </summary>
    internal Action? PreemptibleProgressHook { get; set; }

    // Runs inside SQLite, on the maintenance thread, while the preemptible statement executes: reads
    // counters only, never touches the connection, and never throws into native code.
    private bool ShouldAbortPreemptible()
    {
        try
        {
            PreemptibleProgressHook?.Invoke();
        }
        catch (Exception)
        {
            // A test hook that fails must not become an exception inside SQLite.
        }

        return Interlocked.Read(ref _activity) != Volatile.Read(ref _preemptActivity) ||
               Volatile.Read(ref _waitingWriters) > 0;
    }

    /// <summary>
    /// Interrupts the registered preemptible statement. sqlite.org: safe from another thread as long
    /// as the connection stays open, which the lock guarantees. Returns false when none is registered.
    /// Never throws: it runs on the dictation path and in every waiting writer, where a failed
    /// interrupt must cost at most the wait it was meant to shorten.
    /// </summary>
    internal bool InterruptPreemptible()
    {
        lock (_preemptLock)
        {
            if (_preemptible?.Handle is not { } handle)
            {
                return false;
            }

            try
            {
                SQLitePCL.raw.sqlite3_interrupt(handle);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Marks the calling thread as the maintenance pass, whose gate entries are not activity.</summary>
    internal MaintenanceThreadScope EnterMaintenanceThread()
    {
        Volatile.Write(ref _maintenanceThreadId, Environment.CurrentManagedThreadId);
        return new MaintenanceThreadScope(this);
    }

    private void RecordGateWait(TimeSpan waited)
    {
        var ticks = waited.Ticks;
        var seen = Interlocked.Read(ref _longestGateWaitTicks);
        while (ticks > seen)
        {
            var previous = Interlocked.CompareExchange(ref _longestGateWaitTicks, ticks, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }

    internal void NotifyStorageChanged(StorageChange change) =>
        ResilientEvent.InvokeAll(
            StorageChanged,
            change,
            ex => _logger.LogDebug("A storage change handler threw {Type}.", ex.GetType().Name));

    /// <summary>Holds the write gate until disposed, unless the wait for it timed out.</summary>
    internal readonly ref struct WriteScope
    {
        private readonly Lock? _gate;

        internal WriteScope(Lock gate) => _gate = gate;

        /// <summary>False when the wait timed out and the write goes ahead ungated.</summary>
        internal bool Held => _gate is not null;

        public void Dispose() => _gate?.Exit();
    }

    /// <summary>Unregisters the preemptible statement when disposed.</summary>
    internal readonly ref struct PreemptibleScope
    {
        private readonly ScribeDatabase? _database;
        private readonly SqliteConnection? _connection;

        internal PreemptibleScope(ScribeDatabase database, SqliteConnection connection)
        {
            _database = database;
            _connection = connection;
        }

        public void Dispose()
        {
            if (_database is not { } database)
            {
                return;
            }

            lock (database._preemptLock)
            {
                database._preemptible = null;
            }

            // Off again before the connection goes back to the pool, where anyone may reuse it.
            if (_connection?.Handle is { } handle)
            {
                try
                {
                    SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null);
                }
                catch (Exception)
                {
                    // Only if the connection is already gone, and its handler with it.
                }
            }
        }
    }

    /// <summary>Clears the maintenance thread mark when disposed.</summary>
    internal readonly ref struct MaintenanceThreadScope
    {
        private readonly ScribeDatabase? _database;

        internal MaintenanceThreadScope(ScribeDatabase database) => _database = database;

        public void Dispose()
        {
            if (_database is { } database)
            {
                Volatile.Write(ref database._maintenanceThreadId, 0);
            }
        }
    }

    private ScribeDatabase(string connectionString, bool isMemory, ILogger<ScribeDatabase> logger)
    {
        _connectionString = connectionString;
        _isMemory = isMemory;
        _logger = logger;
    }

    /// <summary>Creates a database backed by the per-user file at <see cref="AppPaths.DatabasePath"/>.</summary>
    public ScribeDatabase(AppPaths paths, ILogger<ScribeDatabase> logger)
        : this(BuildFileConnectionString(paths.DatabasePath), isMemory: false, logger)
    {
    }

    /// <summary>
    /// Creates an isolated, shared-cache in-memory database for tests. The database lives only
    /// while the keep-alive connection is open, so the instance must be disposed to release it.
    /// </summary>
    internal static ScribeDatabase CreateInMemory(ILogger<ScribeDatabase>? logger = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = $"scribe-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        };
        return new ScribeDatabase(builder.ToString(), isMemory: true, logger ?? NullLogger<ScribeDatabase>.Instance);
    }

    /// <summary>
    /// The connection string for the database file at <paramref name="path"/>. Microsoft.Data.Sqlite keys
    /// its connection pools by this exact string, so anything that must release this file's pooled
    /// connections, and only those, builds its key from here.
    /// </summary>
    internal static string BuildFileConnectionString(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 10,
        }.ToString();

    /// <summary>Opens a configured connection, initializing the provider and schema on first use.</summary>
    public SqliteConnection Open()
    {
        EnsureInitialized();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            Configure(connection, autoCheckpoint: !_deferAutoCheckpoint);
            return connection;
        }
    }

    /// <summary>Forces provider initialization and schema migration without returning a connection.</summary>
    public void Initialize() => EnsureInitialized();

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Volatile.Read(ref _initialized)) return;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;

            // Microsoft.Data.Sqlite auto-initializes SQLitePCLRaw, but doing it explicitly once is
            // idempotent and removes any ambiguity about which bundle provides the native library.
            if (Interlocked.Exchange(ref s_providerInitialized, 1) == 0)
            {
                SQLitePCL.Batteries_V2.Init();
            }

            if (!_isMemory)
            {
                // A corrupted file must be caught before anything reads through it: a "malformed"
                // database can serve some tables and fail others, which shows up to the user as
                // settings or dictionary entries silently vanishing. Detect it now and rebuild
                // from whatever is still readable, keeping the damaged original beside the new file.
                EnsureFileDatabaseHealthy();

                // A WAL this large at startup is a backfill an earlier session did not finish, for
                // example one that exited straight after a maintenance VACUUM. Leave that copy to
                // maintenance's first pass rather than the first writer's commit, which at startup
                // is the UI thread.
                _deferAutoCheckpoint = FileLengthOrZero(DataSourcePath() + "-wal") >= DeferredBackfillWalBytes;
            }

            var keepAlive = new SqliteConnection(_connectionString);
            try
            {
                keepAlive.Open();
                Configure(keepAlive, autoCheckpoint: !_deferAutoCheckpoint);

                if (!_isMemory)
                {
                    EnableIncrementalAutoVacuumIfNew(keepAlive);

                    // WAL is persistent and only meaningful for a file database; on :memory: SQLite
                    // silently keeps its MEMORY journal, so this is best-effort.
                    Execute(keepAlive, "PRAGMA journal_mode=WAL;");
                }

                Migrate(keepAlive);
                EnsureAdditiveSchema(keepAlive);

                // A rebuild recorded the loss before it recovered anything; a repair that fell back to a fresh file,
                // or recovered nothing, has not. Best effort here: such a file holds nothing recovered, so no later
                // start can delete anything by taking it for a first run, and this start already knows.
                if (SettingsLostInRepair && !TryRecordSettingsLoss(keepAlive, out var recordFailure))
                {
                    _logger.LogWarning(
                        "Could not record that the repair lost the settings; only this start treats them as lost ({Failure}).",
                        Diagnostics.FailureShape.Describe(recordFailure));
                }

                var version = QueryScalar(keepAlive, "SELECT sqlite_version();");
                _logger.LogInformation(
                    "SQLite database ready (native {Version}, schema v{Schema}, {Mode}).",
                    version, SchemaVersion, _isMemory ? "in-memory" : "file");

                _keepAlive = keepAlive;
                _initialized = true;
            }
            catch
            {
                keepAlive.Dispose();
                throw;
            }
        }
    }

    // synchronous=FULL is explicit rather than inherited: with WAL it costs one fsync per commit
    // (Scribe writes are rare and small) and guarantees the database survives power loss, not just
    // process death. Relying on the compiled default left durability to whatever the native bundle
    // was built with. wal_autocheckpoint is per connection and pooled connections are reused, so it
    // is set on every open rather than only when it changes.
    private static void Configure(SqliteConnection connection, bool autoCheckpoint = true) =>
        Execute(connection, string.Create(
            CultureInfo.InvariantCulture,
            $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint={(autoCheckpoint ? WalAutoCheckpointPages : 0)};"));

    // sqlite.org: auto_vacuum can change from NONE only while a database is new (before its first
    // page is written, which journal_mode=WAL already does) or through a full VACUUM. A brand-new
    // file therefore takes incremental mode for free here, and StorageMaintenance can later return
    // freed pages to the disk with incremental_vacuum. Existing NONE databases are converted once
    // by StorageMaintenance instead, because that conversion needs the VACUUM.
    private static void EnableIncrementalAutoVacuumIfNew(SqliteConnection connection)
    {
        if (Convert.ToInt64(QueryScalar(connection, "PRAGMA page_count;"), CultureInfo.InvariantCulture) == 0)
        {
            Execute(connection, "PRAGMA auto_vacuum=INCREMENTAL;");
        }
    }

    // Probe retries: a transient error (lock held by an exiting instance, an antivirus scan) must
    // never be mistaken for corruption, so non-corruption errors are retried before startup fails.
    private const int ProbeAttempts = 3;
    private static readonly TimeSpan ProbeRetryDelay = TimeSpan.FromMilliseconds(750);

    // Startup corruption check for the file database. quick_check walks the tree structure of every
    // table; a healthy database answers "ok". A damaged one either answers with the first problem or
    // throws SQLITE_CORRUPT/SQLITE_NOTADB; both trigger a rebuild that salvages every readable row
    // into a fresh file. The check runs on a throwaway connection so a rebuild never races the
    // keep-alive.
    //
    // Any OTHER SqliteException is deliberately NOT corruption. This used to treat every probe
    // error as a corrupt database, and a "database is locked" from an instance still shutting down
    // (the single-instance mutex releases the moment a process dies, before its SQLite handles
    // close) made a healthy database get moved aside and rebuilt as empty - which is how a user's
    // settings vanished on 2026-08-31. A transient error is retried; if it persists, startup fails
    // loudly with the data intact, which is always recoverable in a way a destructive rebuild of a
    // healthy file is not.
    private void EnsureFileDatabaseHealthy()
    {
        if (!File.Exists(DataSourcePath()))
        {
            return; // Brand-new install; nothing to verify.
        }

        string verdict;
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                using var probe = new SqliteConnection(_connectionString);
                probe.Open();
                Configure(probe);
                verdict = QueryScalar(probe, "PRAGMA quick_check(1);").ToString() ?? string.Empty;
                break;
            }
            catch (SqliteException ex) when (IsCorruptionError(ex))
            {
                verdict = ex.Message;
                break;
            }
            catch (SqliteException ex) when (attempt < ProbeAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Database probe hit a transient error (attempt {Attempt}/{Max}); retrying.",
                    attempt, ProbeAttempts);
                Thread.Sleep(ProbeRetryDelay);
            }
        }

        if (string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _logger.LogError(
            "SQLite database failed its startup integrity check ({Verdict}); rebuilding from salvageable data.",
            verdict);

        try
        {
            RebuildFromDamagedFile();
            RepairedAtStartup = true;
        }
        catch (Exception ex)
        {
            // Salvage is best-effort: if even the rebuild fails, get the damaged file out of the way
            // so a completely fresh database can be created; the aside copy remains for manual
            // recovery. Losing data is bad; failing to start at all is worse. This still counts as
            // a repair with lost settings, so the user hears about it instead of quietly finding
            // every preference reset.
            _logger.LogError(ex, "Database salvage failed; starting fresh. The damaged file is kept alongside.");
            TryMoveDamagedAside();
            RepairedAtStartup = true;
            SettingsLostInRepair = true;
            DictionaryLostInRepair = true;
        }
    }

    /// <summary>
    /// The two result codes SQLite reserves for a genuinely damaged file: SQLITE_CORRUPT (11,
    /// malformed image) and SQLITE_NOTADB (26, not a database file at all). Everything else -
    /// BUSY, LOCKED, IOERR, CANTOPEN - describes the environment, not the file, and must never
    /// justify a destructive rebuild.
    /// </summary>
    private static bool IsCorruptionError(SqliteException ex) =>
        ex.SqliteErrorCode is 11 or 26 || (ex.SqliteExtendedErrorCode & 0xFF) is 11 or 26;

    private string DataSourcePath() => new SqliteConnectionStringBuilder(_connectionString).DataSource;

    private static long FileLengthOrZero(string path)
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

    private string TryMoveDamagedAside()
    {
        var dbPath = DataSourcePath();

        // Release any pooled handles on the damaged file so it can be renamed.
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));

        // Invariant, so the stamp is always Gregorian: DamagedDatabaseCopies dates the copy from it.
        var asidePath = $"{dbPath}.corrupt-{DamagedDatabaseCopies.FormatStamp(DateTime.UtcNow)}";
        if (File.Exists(dbPath))
        {
            File.Move(dbPath, asidePath);
        }

        // The sidecars must follow the rename: SQLite associates them by file name, and a fresh
        // database must never start life against the damaged file's write-ahead log.
        MoveIfExists(dbPath + "-wal", asidePath + "-wal");
        MoveIfExists(dbPath + "-shm", asidePath + "-shm");
        return asidePath;
    }

    private static void MoveIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
    }

    // Moves the damaged database aside, creates a fresh one at the original path with the current
    // schema, records that the settings may be lost, then copies every row that can still be read out
    // of the damaged file, withdrawing the record once a readable settings document is back. Per-table
    // and per-row failures are skipped: a broken history page must not cost the user their dictionary.
    private void RebuildFromDamagedFile()
    {
        var asidePath = TryMoveDamagedAside();

        using var fresh = new SqliteConnection(_connectionString);
        fresh.Open();
        Configure(fresh);
        EnableIncrementalAutoVacuumIfNew(fresh);
        Execute(fresh, "PRAGMA journal_mode=WAL;");
        Migrate(fresh);

        // Before salvage: TryCopyTable only copies columns the fresh schema has, so adding the column
        // first carries each blob's recorded encoding across. PCM16 blobs also identify themselves by
        // their header, but the first builds of that format wrote none.
        EnsureAdditiveSchema(fresh);
        RepairStepCommitted?.Invoke(fresh, "schema");

        // The loss is recorded before a single row is recovered, and withdrawn below only once a readable document is
        // back. So no crash and no failure can leave recovered history in a database whose next start reads as a first
        // run, which would apply the default retention and delete what the user chose to keep. If the record cannot be
        // written, nothing is recovered: every row stays in the damaged copy, and this database starts as the empty one
        // it then is.
        if (!TryRecordSettingsLoss(fresh, out var recordFailure))
        {
            SettingsLostInRepair = true;
            DictionaryLostInRepair = true;
            _logger.LogError(
                "Database rebuilt empty: the repair could not record that the settings may be lost ({Failure}), so it " +
                "recovered nothing. The damaged original was kept at {Aside}.",
                Diagnostics.FailureShape.Describe(recordFailure), asidePath);
            return;
        }

        var salvaged = new List<string>();
        var asideConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = asidePath,
            // ReadWrite (not ReadOnly) so SQLite can recover the moved-along WAL into a snapshot.
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        try
        {
            using var damaged = new SqliteConnection(asideConnectionString);
            damaged.Open();
            Configure(damaged);

            // Fold the damaged file's WAL into its main image before reading anything. Without
            // this, salvage reads through the merged WAL view, and a torn WAL frame can make a
            // table unreadable during salvage that is perfectly intact once the WAL is discarded -
            // the 2026-08-31 repair read 0 settings rows from a file whose settings survived.
            try { Execute(damaged, "PRAGMA wal_checkpoint(TRUNCATE);"); }
            catch (SqliteException) { /* best effort; salvage proceeds on the merged view */ }

            foreach (var table in SalvageTables)
            {
                var copied = TryCopyTable(damaged, fresh, table);

                // The settings document is one small record and the single most painful loss, so it
                // alone earns a second attempt on a fresh connection: reopening after the checkpoint
                // above reads the recovered main image directly. The rows already copied are kept.
                if (table == "settings" && !SettingsDocumentSurvived(fresh))
                {
                    using var reopened = new SqliteConnection(asideConnectionString);
                    reopened.Open();
                    Configure(reopened);
                    copied += TryCopyTable(reopened, fresh, table);
                }

                salvaged.Add($"{table}: {copied}");
                RepairStepCommitted?.Invoke(fresh, table);
            }

            // Data migrations run before salvage because the fresh database starts empty. Reapply
            // the idempotent v4 cleanup after copying so damaged pre-v4 databases cannot restore the
            // timestamp-keyed snippet rows that migration intentionally removes.
            Execute(fresh, SchemaV4);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The damaged database could not be reopened for salvage; starting fresh.");
        }

        // Judged by the document, not by the table: a recovered damaged-copy ledger, migration flag or recovery copy
        // says nothing about the user's choices, and counting those rows as the settings once let a repair that lost
        // the document run the session on default retention. The dictionary is judged by its rows: they are its content.
        SettingsLostInRepair = !SettingsDocumentSurvived(fresh);
        DictionaryLostInRepair = !HasAnyRow(fresh, "dictionary");
        if (!SettingsLostInRepair)
        {
            TryForgetSettingsLoss(fresh);
        }

        _logger.LogWarning(
            "Database rebuilt after corruption. Rows recovered: {Salvaged}; the settings document was {Settings}. " +
            "The damaged original was kept at {Aside}.",
            string.Join(", ", salvaged), SettingsLostInRepair ? "lost" : "recovered", asidePath);
    }

    // Whether the rebuilt database holds a settings document the repository can read. Never throws: a document that
    // cannot even be looked for did not survive.
    private static bool SettingsDocumentSurvived(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", SettingsRepository.SettingsKey);
            return SettingsRepository.IsReadableDocument(command.ExecuteScalar() as string);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Table names come from this file, never from data. Never throws: a table that cannot be read holds nothing.
    private static bool HasAnyRow(SqliteConnection connection, string table)
    {
        try
        {
            return QueryScalar(connection, $"SELECT EXISTS (SELECT 1 FROM {table});") is long found && found != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The record that makes the loss outlive this start: until a readable document is saved, later starts run on
    // defaults the user never chose exactly as this one does (SettingsRepository reads the row). A statement of its
    // own, so it is committed, and durable (synchronous=FULL), once this returns true. Never throws; each caller
    // decides what a failure costs.
    private static bool TryRecordSettingsLoss(SqliteConnection connection, out Exception? failure)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO NOTHING;";
            command.Parameters.AddWithValue("$key", SettingsRepository.LostMarkerKey);
            command.Parameters.AddWithValue("$value", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    // A repair records the loss before it recovers anything, so once it has a readable document back the record is
    // no longer true. Best effort: a record beside a readable document changes nothing, since it only counts while no
    // document is stored, and the next save removes it.
    private void TryForgetSettingsLoss(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", SettingsRepository.LostMarkerKey);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "Could not withdraw the settings-loss record after the repair recovered the document ({Failure}).",
                Diagnostics.FailureShape.Describe(ex));
        }
    }

    // Copies one table's readable rows into the rebuilt database. Column lists are intersected so a
    // damaged file left behind by an older schema still contributes what it has. Reading stops at
    // the first unreadable page (SQLite cannot seek past it); individual bad rows are skipped.
    // Table names come from the fixed SalvageTables list, never from data.
    private int TryCopyTable(SqliteConnection source, SqliteConnection target, string table)
    {
        try
        {
            var sourceColumns = ListColumns(source, table);
            var targetColumns = ListColumns(target, table);
            var columns = sourceColumns.Where(targetColumns.Contains).ToList();
            if (columns.Count == 0)
            {
                return 0;
            }

            var columnList = string.Join(", ", columns);
            var placeholders = string.Join(", ", columns.Select((_, i) => $"$p{i}"));

            using var read = source.CreateCommand();
            read.CommandText = $"SELECT {columnList} FROM {table};";

            using var transaction = target.BeginTransaction();
            using var insert = target.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT OR IGNORE INTO {table} ({columnList}) VALUES ({placeholders});";
            // No explicit SqliteType: the binding type is inferred from each row's value, so
            // integer, text and blob columns all round-trip faithfully.
            var parameters = new SqliteParameter[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                parameters[i] = insert.CreateParameter();
                parameters[i].ParameterName = $"$p{i}";
                insert.Parameters.Add(parameters[i]);
            }

            var copied = 0;
            using var reader = read.ExecuteReader();
            while (true)
            {
                try
                {
                    if (!reader.Read())
                    {
                        break;
                    }
                }
                catch (SqliteException)
                {
                    break; // Hit the damaged region; keep what was read so far.
                }

                try
                {
                    for (var i = 0; i < columns.Count; i++)
                    {
                        parameters[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                    }

                    copied += insert.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // One unreadable or constraint-violating row must not abandon the rest.
                }
            }

            transaction.Commit();
            return copied;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Table {Table} could not be salvaged.", table);
            return 0;
        }
    }

    private static List<string> ListColumns(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static void Migrate(SqliteConnection connection)
    {
        var current = Convert.ToInt32(QueryScalar(connection, "PRAGMA user_version;"));
        if (current > SchemaVersion)
        {
            throw new NewerDatabaseSchemaException(current, SchemaVersion);
        }

        if (current == SchemaVersion) return;

        using var transaction = connection.BeginTransaction();
        if (current < 1)
        {
            Execute(connection, SchemaV1, transaction);
        }

        if (current < 2)
        {
            Execute(connection, SchemaV2, transaction);
        }

        if (current < 3)
        {
            Execute(connection, SchemaV3, transaction);
        }

        if (current < 4)
        {
            Execute(connection, SchemaV4, transaction);
        }

        if (current < 5 && HistoryNeedsCleanupColumn(connection, transaction))
        {
            Execute(connection, SchemaV5, transaction);
        }

        if (current < 6 && HistoryNeedsColumn(connection, transaction, "transcription_model_id"))
        {
            Execute(connection, SchemaV6, transaction);
        }

        if (current < 7 && HistoryNeedsColumn(connection, transaction, "ai_rating"))
        {
            Execute(connection, SchemaV7, transaction);
        }

        // PRAGMA user_version does not accept parameters; SchemaVersion is a trusted constant.
        Execute(connection, $"PRAGMA user_version={SchemaVersion};", transaction);
        transaction.Commit();
    }

    // Applied on every open, after Migrate (which returns early once user_version matches), in the
    // probe-then-ALTER style of HistoryRepository.EnsureHistoryColumn. Each step is optional: if one
    // fails, audio is still written as PCM16, whose header says what it is without the column
    // (HistoryRepository re-checks the column itself), and retention finds references with a table
    // scan, so a failure is logged and never stops startup.
    private void EnsureAdditiveSchema(SqliteConnection connection)
    {
        try
        {
            if (TableNeedsColumn(connection, transaction: null, "audio_blobs", "encoding"))
            {
                Execute(connection, AudioEncodingColumn);
            }

            if (TableExists(connection, transaction: null, "history"))
            {
                Execute(connection, HistoryAudioIndex);
            }
        }
        catch (SqliteException ex)
        {
            try
            {
                _logger.LogWarning(
                    "Could not add the audio encoding column or index (SQLite error {Code}); stored audio still " +
                    "identifies its own format.",
                    ex.SqliteErrorCode);
            }
            catch
            {
                // Diagnostics must never become the failure.
            }
        }
    }

    private static bool HistoryNeedsCleanupColumn(
        SqliteConnection connection,
        SqliteTransaction transaction) =>
        HistoryNeedsColumn(connection, transaction, "cleanup_ms");

    private static bool HistoryNeedsColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string columnName) =>
        TableNeedsColumn(connection, transaction, "history", columnName);

    // True when the table exists and lacks the column, so a partially migrated or salvaged database
    // converges instead of failing on a duplicate ALTER. Table names come from trusted constants:
    // PRAGMA table_info does not take parameters.
    private static bool TableNeedsColumn(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string columnName)
    {
        if (!TableExists(connection, transaction, table))
        {
            return false;
        }

        using var columnCommand = connection.CreateCommand();
        columnCommand.Transaction = transaction;
        columnCommand.CommandText = $"PRAGMA table_info({table});";
        using var reader = columnCommand.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        using var tableCommand = connection.CreateCommand();
        tableCommand.Transaction = transaction;
        tableCommand.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        tableCommand.Parameters.AddWithValue("$name", table);
        return tableCommand.ExecuteScalar() is not null;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static object QueryScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() ?? string.Empty;
    }

    public void Dispose()
    {
        // The write gate first, in the same order writers take it, so a writer or maintenance step
        // still inside a transaction finishes before the final checkpoint and the keep-alive close.
        // Bounded, and it interrupts a preemptible VACUUM, so exit never waits long on maintenance.
        using var writeScope = EnterWriteScope(ShutdownGateTimeout);
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            // Skipped when the gate could not be had: the checkpoint would then sit in the busy
            // handler behind that writer for up to BusyTimeoutMs. The WAL is folded in on next open.
            if (_keepAlive is not null && !_isMemory && writeScope.Held)
            {
                // Fold the WAL back into the main file on clean shutdown so scribe.db alone is a
                // complete, consistent snapshot; anything that copies or backs up just the main
                // file (migrations, user backups) then can't capture a torn state.
                try { Execute(_keepAlive, "PRAGMA wal_checkpoint(TRUNCATE);"); }
                catch { /* best effort */ }
            }

            _keepAlive?.Dispose();
            _keepAlive = null;
        }

        if (!_isMemory)
        {
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        }
    }

    private const string SchemaV1 = """
        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE dictionary (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            pattern     TEXT NOT NULL,
            replacement TEXT NOT NULL,
            whole_word  INTEGER NOT NULL DEFAULT 1,
            enabled     INTEGER NOT NULL DEFAULT 1
        );
        CREATE UNIQUE INDEX ux_dictionary_pattern ON dictionary (pattern);

        CREATE TABLE audio_blobs (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            sample_rate INTEGER NOT NULL,
            samples     BLOB NOT NULL,
            created_utc TEXT NOT NULL
        );

        CREATE TABLE history (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            text          TEXT NOT NULL,
            audio_ms      INTEGER NOT NULL,
            decode_ms     INTEGER NOT NULL,
            target_app    TEXT NULL,
            audio_blob_id INTEGER NULL REFERENCES audio_blobs (id) ON DELETE SET NULL
        );
        CREATE INDEX ix_history_timestamp ON history (timestamp_utc DESC);
        """;

    // v2: records when AI cleanup failed at runtime so the user can see it in Settings. Rows are
    // pruned to a rolling one-week window on each success and at startup, so the log stays small.
    private const string SchemaV2 = """
        CREATE TABLE cleanup_failures (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            provider      TEXT NULL,
            model         TEXT NULL,
            reason        TEXT NOT NULL,
            sample        TEXT NULL
        );
        CREATE INDEX ix_cleanup_failures_timestamp ON cleanup_failures (timestamp_utc DESC);
        """;

    // v3: voice snippets: a spoken trigger phrase expands to a saved (possibly multi-line)
    // template during post-processing. Separate from `dictionary` because templates are long,
    // matched as whole phrases, and never fed to the AI glossary. ("phrase" not "trigger": TRIGGER
    // is a reserved word in SQLite.)
    private const string SchemaV3 = """
        CREATE TABLE snippets (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            phrase   TEXT NOT NULL,
            template TEXT NOT NULL,
            enabled  INTEGER NOT NULL DEFAULT 1
        );
        CREATE UNIQUE INDEX ux_snippets_phrase ON snippets (phrase);
        """;

    // v4: purge snippets whose trigger phrase is exactly the round-trip DateTimeOffset timestamp
    // written by a past uncommitted build. Match the complete 33-character shape, including seven
    // fractional digits and a numeric offset, so a legitimate phrase that merely starts with a
    // timestamp remains untouched.
    internal const string SchemaV4 = """
        DELETE FROM snippets
        WHERE length(phrase) = 33
          AND phrase GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9][+-][0-9][0-9]:[0-9][0-9]';
        """;

    // v5: persist optional AI cleanup duration (milliseconds) alongside decode duration so
    // diagnostics can report cleanup latency distributions without parsing trace logs.
    private const string SchemaV5 = """
        ALTER TABLE history ADD COLUMN cleanup_ms INTEGER NULL;
        """;

    // v6: identify the recognizer that produced each decode so model-specific performance
    // statistics never mix Parakeet with Moonshine or unknown historical rows.
    private const string SchemaV6 = """
        ALTER TABLE history ADD COLUMN transcription_model_id TEXT NULL;
        """;

    /// <summary>
    /// Whether the user marked an AI-cleaned result useful. NULL means unrated, which is the
    /// overwhelming majority of rows and the reason this is nullable rather than defaulted: an
    /// unrated result and a result nobody minded are different facts, and collapsing them would
    /// make the counts meaningless the moment anyone tried to read them.
    /// </summary>
    private const string SchemaV7 = """
        ALTER TABLE history ADD COLUMN ai_rating INTEGER NULL;
        """;

    // New audio is stored as 16-bit PCM, about half the size of the raw float samples. The column
    // records each blob's layout (AudioBlobEncoding; PCM16 blobs also open with a header saying so),
    // and SQLite supplies the default for every row written without it, including rows an older
    // build writes into this same file, so their audio keeps decoding as float32. ADD COLUMN only
    // edits the schema text, so this is instant however much audio is stored.
    private const string AudioEncodingColumn = """
        ALTER TABLE audio_blobs ADD COLUMN encoding INTEGER NOT NULL DEFAULT 0;
        """;

    // Retention asks "does any entry still reference this blob?" before deleting one, and SQLite
    // runs the same lookup for the ON DELETE SET NULL foreign key on every blob delete. Without an
    // index each of those is a full scan of history.
    private const string HistoryAudioIndex = """
        CREATE INDEX IF NOT EXISTS ix_history_audio_blob ON history (audio_blob_id);
        """;
}
