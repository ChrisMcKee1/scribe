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

    private const int BusyTimeoutMs = 10_000;
    private const int SchemaVersion = 7;

    // Tables copied out of a damaged database during salvage, ordered so foreign-key targets
    // (audio_blobs) are restored before the rows that reference them (history).
    private static readonly string[] SalvageTables =
        { "settings", "dictionary", "snippets", "audio_blobs", "history", "cleanup_failures" };

    private static int s_providerInitialized;

    private readonly string _connectionString;
    private readonly bool _isMemory;
    private readonly ILogger<ScribeDatabase> _logger;
    private readonly object _gate = new();

    private SqliteConnection? _keepAlive;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// True when startup found the database file corrupted and rebuilt it from whatever rows were
    /// still readable. The app can surface this to the user (some history may be missing); the
    /// damaged original is kept beside the database as <c>scribe.db.corrupt-*</c>.
    /// </summary>
    public bool RepairedAtStartup { get; private set; }

    /// <summary>
    /// True when a startup repair could not carry the settings row into the rebuilt database, so
    /// the app is running on compiled defaults. This is the one salvage failure a user cannot
    /// shrug off (hotkeys, AI cleanup and libraries all silently reset), so the shell must say it
    /// plainly instead of the generic "some history may be missing".
    /// </summary>
    public bool SettingsLostInRepair { get; private set; }

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

    private static string BuildFileConnectionString(string path) =>
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
            Configure(connection);
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
            }

            var keepAlive = new SqliteConnection(_connectionString);
            try
            {
                keepAlive.Open();
                Configure(keepAlive);

                if (!_isMemory)
                {
                    // WAL is persistent and only meaningful for a file database; on :memory: SQLite
                    // silently keeps its MEMORY journal, so this is best-effort.
                    Execute(keepAlive, "PRAGMA journal_mode=WAL;");
                }

                Migrate(keepAlive);

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
    // was built with.
    private static void Configure(SqliteConnection connection) =>
        Execute(connection, $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA synchronous=FULL;");

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

    private string TryMoveDamagedAside()
    {
        var dbPath = DataSourcePath();

        // Release any pooled handles on the damaged file so it can be renamed.
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));

        var asidePath = $"{dbPath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
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
    // schema, then copies every row that can still be read out of the damaged file. Per-table and
    // per-row failures are skipped: a broken history page must not cost the user their dictionary.
    private void RebuildFromDamagedFile()
    {
        var asidePath = TryMoveDamagedAside();

        using var fresh = new SqliteConnection(_connectionString);
        fresh.Open();
        Configure(fresh);
        Execute(fresh, "PRAGMA journal_mode=WAL;");
        Migrate(fresh);

        var salvaged = new List<string>();
        var asideConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = asidePath,
            // ReadWrite (not ReadOnly) so SQLite can recover the moved-along WAL into a snapshot.
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        var settingsCopied = 0;
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

                // The settings row is one small record and the single most painful loss, so it
                // alone earns a second attempt on a fresh connection: reopening after the
                // checkpoint above reads the recovered main image directly.
                if (table == "settings" && copied == 0)
                {
                    using var reopened = new SqliteConnection(asideConnectionString);
                    reopened.Open();
                    Configure(reopened);
                    copied = TryCopyTable(reopened, fresh, table);
                }

                if (table == "settings")
                {
                    settingsCopied = copied;
                }

                salvaged.Add($"{table}: {copied}");
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

        SettingsLostInRepair = settingsCopied == 0;
        _logger.LogWarning(
            "Database rebuilt after corruption. Rows recovered: {Salvaged}. The damaged original was kept at {Aside}.",
            string.Join(", ", salvaged), asidePath);
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
            throw new InvalidOperationException(
                $"This database uses schema v{current}, but this Scribe build supports only v{SchemaVersion}. " +
                "Install a newer Scribe version instead of downgrading.");
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

    private static bool HistoryNeedsCleanupColumn(
        SqliteConnection connection,
        SqliteTransaction transaction) =>
        HistoryNeedsColumn(connection, transaction, "cleanup_ms");

    private static bool HistoryNeedsColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string columnName)
    {
        using var tableCommand = connection.CreateCommand();
        tableCommand.Transaction = transaction;
        tableCommand.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'history';";
        if (tableCommand.ExecuteScalar() is null)
        {
            return false;
        }

        using var columnCommand = connection.CreateCommand();
        columnCommand.Transaction = transaction;
        columnCommand.CommandText = "PRAGMA table_info(history);";
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_keepAlive is not null && !_isMemory)
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
}
