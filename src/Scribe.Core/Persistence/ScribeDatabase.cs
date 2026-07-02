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
    // bundle_e_sqlite3 3.0.3 ships native SQLite 3.50.4 (well past the CVE-2025-6965 fix in
    // 3.50.2). The runtime smoke test asserts this exact version to prove the pinned native loads.
    public const string ExpectedSqliteVersion = "3.50.4";

    private const int BusyTimeoutMs = 10_000;
    private const int SchemaVersion = 3;

    private static int s_providerInitialized;

    private readonly string _connectionString;
    private readonly bool _isMemory;
    private readonly ILogger<ScribeDatabase> _logger;
    private readonly object _gate = new();

    private SqliteConnection? _keepAlive;
    private bool _initialized;
    private bool _disposed;

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
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Configure(connection);
        return connection;
    }

    /// <summary>Forces provider initialization and schema migration without returning a connection.</summary>
    public void Initialize() => EnsureInitialized();

    private void EnsureInitialized()
    {
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

            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
            Configure(_keepAlive);

            if (!_isMemory)
            {
                // WAL is persistent and only meaningful for a file database; on :memory: SQLite
                // silently keeps its MEMORY journal, so this is best-effort.
                Execute(_keepAlive, "PRAGMA journal_mode=WAL;");
            }

            Migrate(_keepAlive);

            var version = QueryScalar(_keepAlive, "SELECT sqlite_version();");
            _logger.LogInformation(
                "SQLite database ready (native {Version}, schema v{Schema}, {Mode}).",
                version, SchemaVersion, _isMemory ? "in-memory" : "file");

            _initialized = true;
        }
    }

    private static void Configure(SqliteConnection connection) =>
        Execute(connection, $"PRAGMA busy_timeout={BusyTimeoutMs};");

    private static void Migrate(SqliteConnection connection)
    {
        var current = Convert.ToInt32(QueryScalar(connection, "PRAGMA user_version;"));
        if (current >= SchemaVersion) return;

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

        // PRAGMA user_version does not accept parameters; SchemaVersion is a trusted constant.
        Execute(connection, $"PRAGMA user_version={SchemaVersion};", transaction);
        transaction.Commit();
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

    // v3: voice snippets — a spoken trigger phrase expands to a saved (possibly multi-line)
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
}
