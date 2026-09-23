using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Infrastructure;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>A per-test folder for real database files, deleted afterwards.</summary>
internal sealed class TempDatabaseFolder : IDisposable
{
    public TempDatabaseFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "scribe-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string DatabasePath => Path.Combine(Root, AppPaths.DatabaseFileName);

    public ScribeDatabase Open() => new(new AppPaths(Root), NullLogger<ScribeDatabase>.Instance);

    /// <summary>Releases the pooled connections to this folder's database, and only those (see <see cref="DatabasePools"/>).</summary>
    public void ReleasePooledConnections() => DatabasePools.Release(new AppPaths(Root));

    public long FileLength(string suffix = "")
    {
        var info = new FileInfo(DatabasePath + suffix);
        return info.Exists ? info.Length : 0;
    }

    public void Dispose()
    {
        ReleasePooledConnections();
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// Releases the connections <see cref="ScribeDatabase"/> pools for one database file, so a test can move,
/// damage, copy or delete that file. Only that file's pool: xUnit runs test classes in parallel, and the
/// process-wide <see cref="SqliteConnection.ClearAllPools"/> can dispose a pooled connection that another
/// class is using at that moment.
/// </summary>
internal static class DatabasePools
{
    /// <summary>The pool of the database <see cref="ScribeDatabase"/> opens for <paramref name="paths"/>.</summary>
    public static void Release(AppPaths paths)
    {
        // Pools are keyed by the exact connection string, so the key comes from the same builder the
        // database uses rather than from a copy of its settings.
        using var key = new SqliteConnection(ScribeDatabase.BuildFileConnectionString(paths.DatabasePath));
        SqliteConnection.ClearPool(key);
    }
}

/// <summary>
/// Builds databases the way a schema v7 build (0.4.2 and earlier) left them on disk. The schema is
/// frozen here on purpose: migration tests must start from what real installs have, not from what
/// the current code would create, and a v7 file has auto_vacuum NONE and float32 audio.
/// </summary>
internal static class LegacyDatabase
{
    private const string SchemaV7 = """
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
        CREATE TABLE dictionary (
            id INTEGER PRIMARY KEY AUTOINCREMENT, pattern TEXT NOT NULL, replacement TEXT NOT NULL,
            whole_word INTEGER NOT NULL DEFAULT 1, enabled INTEGER NOT NULL DEFAULT 1);
        CREATE UNIQUE INDEX ux_dictionary_pattern ON dictionary (pattern);
        CREATE TABLE audio_blobs (
            id INTEGER PRIMARY KEY AUTOINCREMENT, sample_rate INTEGER NOT NULL, samples BLOB NOT NULL,
            created_utc TEXT NOT NULL);
        CREATE TABLE history (
            id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp_utc TEXT NOT NULL, text TEXT NOT NULL,
            audio_ms INTEGER NOT NULL, decode_ms INTEGER NOT NULL, target_app TEXT NULL,
            audio_blob_id INTEGER NULL REFERENCES audio_blobs (id) ON DELETE SET NULL,
            cleanup_ms INTEGER NULL, transcription_model_id TEXT NULL, ai_rating INTEGER NULL);
        CREATE INDEX ix_history_timestamp ON history (timestamp_utc DESC);
        CREATE TABLE cleanup_failures (
            id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp_utc TEXT NOT NULL, provider TEXT NULL,
            model TEXT NULL, reason TEXT NOT NULL, sample TEXT NULL);
        CREATE INDEX ix_cleanup_failures_timestamp ON cleanup_failures (timestamp_utc DESC);
        CREATE TABLE snippets (
            id INTEGER PRIMARY KEY AUTOINCREMENT, phrase TEXT NOT NULL, template TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1);
        CREATE UNIQUE INDEX ux_snippets_phrase ON snippets (phrase);
        """;

    public static void Create(string path, int userVersion = 7)
    {
        using var connection = OpenRaw(path);
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, SchemaV7);
        Execute(connection, $"PRAGMA user_version={userVersion.ToString(CultureInfo.InvariantCulture)};");
    }

    /// <summary>
    /// A v7 database with <paramref name="old"/> entries from 30 days before <paramref name="now"/>
    /// and <paramref name="recent"/> from an hour before it, each owning a 2 s float32 recording
    /// (128,000 bytes), so a VACUUM of it has real copying to do.
    /// </summary>
    public static void Seed(string path, DateTimeOffset now, int recent, int old)
    {
        Create(path);
        var samples = new float[32_000];
        InsertManyRecent(path, old, samples, now.AddDays(-30), "old");
        InsertManyRecent(path, recent, samples, now.AddHours(-1));
    }

    /// <summary>Stores samples exactly as a v7 build did: raw float32, no encoding column.</summary>
    public static long InsertFloatBlob(string path, float[] samples, DateTimeOffset createdUtc)
    {
        using var connection = OpenRaw(path);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO audio_blobs (sample_rate, samples, created_utc) VALUES (16000, $samples, $created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$samples", MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
        command.Parameters.AddWithValue("$created", createdUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return (long)command.ExecuteScalar()!;
    }

    public static long InsertHistory(string path, DateTimeOffset timestampUtc, string text, long? blobId)
    {
        using var connection = OpenRaw(path);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO history (timestamp_utc, text, audio_ms, decode_ms, audio_blob_id)
            VALUES ($ts, $text, 1000, 50, $blob);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ts", timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$blob", (object?)blobId ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    public static long QueryInt64(string path, string sql)
    {
        using var connection = OpenRaw(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Adds entries that each own a float32 blob, in one transaction so large seeds stay fast.</summary>
    public static void InsertManyRecent(string path, int count, float[] samples, DateTimeOffset timestampUtc, string textPrefix = "recent")
    {
        using var connection = OpenRaw(path);
        using var transaction = connection.BeginTransaction();
        var stamp = timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        using var blob = connection.CreateCommand();
        blob.CommandText =
            """
            INSERT INTO audio_blobs (sample_rate, samples, created_utc) VALUES (16000, $samples, $created);
            SELECT last_insert_rowid();
            """;
        blob.Parameters.AddWithValue("$samples", MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
        blob.Parameters.AddWithValue("$created", stamp);

        using var entry = connection.CreateCommand();
        entry.CommandText =
            """
            INSERT INTO history (timestamp_utc, text, audio_ms, decode_ms, audio_blob_id)
            VALUES ($ts, $text, 1000, 50, $blob);
            """;
        entry.Parameters.AddWithValue("$ts", stamp);
        var text = entry.Parameters.Add("$text", SqliteType.Text);
        var blobId = entry.Parameters.Add("$blob", SqliteType.Integer);
        for (var i = 0; i < count; i++)
        {
            blobId.Value = (long)blob.ExecuteScalar()!;
            text.Value = $"{textPrefix} {i}";
            entry.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static SqliteConnection OpenRaw(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();

        // Seed data needs no durability, and an fsync per seeded row made these tests take seconds.
        Execute(connection, "PRAGMA synchronous=OFF;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>Queries a live <see cref="ScribeDatabase"/> directly, for assertions about its file.</summary>
internal static class DatabaseProbe
{
    public static long QueryInt64(ScribeDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    public static string QueryString(ScribeDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    public static bool HasColumn(ScribeDatabase database, string table, string column)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Folds the WAL into the main file so file sizes compare like for like.</summary>
    public static void Checkpoint(ScribeDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }
}

/// <summary>The real repository, with hooks into chosen maintenance calls.</summary>
internal sealed class HookedHistory(HistoryRepository inner) : IHistoryMaintenance
{
    private int _deleteSlices;
    private int _evictSlices;

    public Action<int>? OnDeleteSlice { get; set; }

    public Action<int>? OnEvictSlice { get; set; }

    public Action? OnClearAudio { get; set; }

    public int DeleteEntriesOlderThan(DateTimeOffset cutoffUtc) => inner.DeleteEntriesOlderThan(cutoffUtc);

    public int ClearAudioOlderThan(DateTimeOffset cutoffUtc)
    {
        OnClearAudio?.Invoke();
        return inner.ClearAudioOlderThan(cutoffUtc);
    }

    public IReadOnlyList<StoredAudioBlob> ListStoredAudio() => inner.ListStoredAudio();

    public AudioDeletion DeleteUnreferencedAudio(IReadOnlyCollection<long> blobIds, DateTimeOffset storedBeforeUtc)
    {
        OnDeleteSlice?.Invoke(Interlocked.Increment(ref _deleteSlices));
        return inner.DeleteUnreferencedAudio(blobIds, storedBeforeUtc);
    }

    public AudioDeletion EvictAudio(IReadOnlyCollection<long> blobIds, long maxStoredBytes)
    {
        OnEvictSlice?.Invoke(Interlocked.Increment(ref _evictSlices));
        return inner.EvictAudio(blobIds, maxStoredBytes);
    }

    public StoredAudioUsage GetStoredAudioUsage() => inner.GetStoredAudioUsage();

    public void RequestYield() => inner.RequestYield();
}

/// <summary>Keeps every formatted log line, so a failing assertion can say what maintenance saw.</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    public string Text => string.Join(Environment.NewLine, _lines);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
}