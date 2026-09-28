using Microsoft.Data.Sqlite;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// DATA-A-06 (<see cref="PerfFlags.GroupHistorySchemaProbes"/>): one read of the history table's columns per operation
/// decides each column as that column's own probe does, repairs a missing one the same way, falls back the same way when
/// the repair is refused, and is never kept past its operation. Today's probe is the oracle, on twin databases.
/// </summary>
public sealed class HistorySchemaProbeTests
{
    public static TheoryData<string> Tables() => ["complete", "other case", "missing two", "view"];

    [Theory]
    [MemberData(nameof(Tables))]
    public void One_read_of_the_columns_decides_each_column_as_its_own_probe_does(string table)
    {
        using var today = Connection(table);
        using var grouped = Connection(table);
        var columns = new TableColumns(grouped, "history");

        foreach (var (name, declaration) in new[]
                 {
                     ("cleanup_ms", "INTEGER NULL"), ("transcription_model_id", "TEXT NULL"), ("ai_rating", "INTEGER NULL"),
                 })
        {
            Assert.Equal(
                HistoryRepository.EnsureColumn(today, "history", name, declaration),
                columns.Ensure(name, declaration));
        }

        Assert.Equal(Schema(today), Schema(grouped));
    }

    [Fact]
    public void A_column_another_connection_adds_after_the_read_is_found_when_the_repair_is_refused()
    {
        var name = "file:grouped-" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared";
        using var first = new SqliteConnection($"Data Source={name}");
        using var second = new SqliteConnection($"Data Source={name}");
        first.Open();
        second.Open();
        Execute(first, "CREATE TABLE history (id INTEGER PRIMARY KEY, cleanup_ms INTEGER NULL);");
        var columns = new TableColumns(first, "history");
        Assert.True(columns.Ensure("cleanup_ms", "INTEGER NULL"));

        Execute(second, "ALTER TABLE history ADD COLUMN ai_rating INTEGER NULL;");

        // The read this operation made no longer lists it, the repair finds it there already, and the table is read again,
        // which is what the column's own probe, made now, would have found.
        Assert.True(columns.Ensure("ai_rating", "INTEGER NULL"));
        Assert.True(HistoryRepository.EnsureColumn(second, "history", "ai_rating", "INTEGER NULL"));
    }

    [Fact]
    public void A_refused_repair_whose_follow_up_read_fails_too_answers_false_as_the_column_s_own_probe_does()
    {
        // The review's fault: the first metadata read is allowed, then ALTER and every later metadata read are refused.
        bool Probe(bool grouped)
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            Execute(connection, "CREATE TABLE history (id INTEGER PRIMARY KEY);");
            var reads = 0;
            SQLitePCL.delegate_authorizer authorizer = (_, action, _, _, _, _) =>
                action == SQLitePCL.raw.SQLITE_PRAGMA
                    ? ++reads == 1 ? SQLitePCL.raw.SQLITE_OK : SQLitePCL.raw.SQLITE_DENY
                    : action == SQLitePCL.raw.SQLITE_ALTER_TABLE ? SQLitePCL.raw.SQLITE_DENY : SQLitePCL.raw.SQLITE_OK;
            SQLitePCL.raw.sqlite3_set_authorizer(connection.Handle, authorizer, null);
            try
            {
                return grouped
                    ? new TableColumns(connection, "history").Ensure("ai_rating", "INTEGER NULL")
                    : HistoryRepository.EnsureColumn(connection, "history", "ai_rating", "INTEGER NULL");
            }
            finally
            {
                SQLitePCL.raw.sqlite3_set_authorizer(connection.Handle, (SQLitePCL.delegate_authorizer)null!, null);
                GC.KeepAlive(authorizer);
            }
        }

        Assert.False(Probe(grouped: false));
        Assert.False(Probe(grouped: true));
    }

    [Fact]
    public void A_history_read_whose_repair_and_follow_up_read_are_refused_returns_its_rows_either_way()
    {
        string Read(PerfFlags flags)
        {
            using var folder = new TempDatabaseFolder();
            using var database = folder.Open();
            var repository = new HistoryRepository(database, flags: flags);
            repository.Add(new HistoryEntry(0, new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero), "synthetic", 10, 1));

            // ai_rating goes, and the pooled connection the read will take refuses its repair and every metadata read after.
            var repairRefused = false;
            SQLitePCL.delegate_authorizer authorizer = (_, action, _, _, _, _) =>
            {
                if (action == SQLitePCL.raw.SQLITE_ALTER_TABLE)
                {
                    repairRefused = true;
                    return SQLitePCL.raw.SQLITE_DENY;
                }

                return repairRefused && action == SQLitePCL.raw.SQLITE_PRAGMA ? SQLitePCL.raw.SQLITE_DENY : SQLitePCL.raw.SQLITE_OK;
            };
            SQLitePCL.sqlite3 handle;
            using (var connection = database.Open())
            {
                Execute(connection, "ALTER TABLE history DROP COLUMN ai_rating;");
                handle = connection.Handle!;
                SQLitePCL.raw.sqlite3_set_authorizer(handle, authorizer, null);
            }

            try
            {
                var entries = repository.GetRecent();
                Assert.True(repairRefused);
                return string.Join("\n", entries.Select(entry => entry.ToString()));
            }
            finally
            {
                SQLitePCL.raw.sqlite3_set_authorizer(handle, (SQLitePCL.delegate_authorizer)null!, null);
                GC.KeepAlive(authorizer);
                folder.ReleasePooledConnections();
            }
        }

        var today = Read(PerfFlags.None);
        Assert.Contains("synthetic", today, StringComparison.Ordinal);
        Assert.Equal(today, Read(PerfFlags.Parse(PerfFlags.GroupHistorySchemaProbes)));
    }

    public static TheoryData<string> Schemas() => ["current", "missing the three newer columns", "missing the rating"];

    [Theory]
    [MemberData(nameof(Schemas))]
    public void History_reads_and_writes_come_out_the_same_with_one_read_per_operation(string schema)
    {
        var today = Exercise(schema, PerfFlags.None);
        var grouped = Exercise(schema, PerfFlags.Parse(PerfFlags.GroupHistorySchemaProbes));

        Assert.Equal(today, grouped);
    }

    private static string Exercise(string schema, PerfFlags flags)
    {
        using var database = ScribeDatabase.CreateInMemory();
        database.Initialize();
        switch (schema)
        {
            case "missing the three newer columns":
                ExecuteOn(database, "ALTER TABLE history DROP COLUMN cleanup_ms;");
                ExecuteOn(database, "ALTER TABLE history DROP COLUMN transcription_model_id;");
                ExecuteOn(database, "ALTER TABLE history DROP COLUMN ai_rating;");
                break;
            case "missing the rating":
                ExecuteOn(database, "ALTER TABLE history DROP COLUMN ai_rating;");
                break;
        }

        var repository = new HistoryRepository(database, flags: flags);
        var first = repository.Add(
            new HistoryEntry(0, new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero), "first entry", 1200, 30, 40, "notepad", null, "parakeet"),
            new CapturedAudio([0.1f, 0.2f, -0.3f], 16_000));
        repository.Add(new HistoryEntry(0, new DateTimeOffset(2026, 9, 21, 9, 1, 0, TimeSpan.Zero), "second entry", 800, 20));
        repository.SetAiRating(first.Id, AiRating.Useful);

        var recent = repository.GetRecent();
        var older = repository.GetOlder(new DateTimeOffset(2026, 9, 21, 9, 1, 0, TimeSpan.Zero), long.MaxValue, 10);
        var found = repository.Search("first", 10);
        using var connection = database.Open();
        return string.Join(
            "\n",
            [
                .. recent.Select(entry => entry.ToString()),
                "--",
                .. older.Select(entry => entry.ToString()),
                "--",
                .. found.Select(entry => entry.ToString()),
                "--",
                Schema(connection),
            ]);
    }

    private static SqliteConnection Connection(string table)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        switch (table)
        {
            case "complete":
                Execute(connection, "CREATE TABLE history (id INTEGER PRIMARY KEY, cleanup_ms INTEGER NULL, transcription_model_id TEXT NULL, ai_rating INTEGER NULL);");
                break;
            case "other case":
                Execute(connection, "CREATE TABLE history (id INTEGER PRIMARY KEY, CLEANUP_MS INTEGER NULL, Transcription_Model_Id TEXT NULL, AI_RATING INTEGER NULL);");
                break;
            case "missing two":
                Execute(connection, "CREATE TABLE history (id INTEGER PRIMARY KEY, cleanup_ms INTEGER NULL);");
                break;
            case "view":
                // A view answers PRAGMA table_info but refuses ALTER TABLE: the repair fails and the caller falls back.
                Execute(connection, "CREATE TABLE raw (id INTEGER PRIMARY KEY, cleanup_ms INTEGER NULL);");
                Execute(connection, "CREATE VIEW history AS SELECT id, cleanup_ms FROM raw;");
                break;
        }

        return connection;
    }

    private static string Schema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(name || ' ' || type, ', ') FROM pragma_table_info('history');";
        return (string)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecuteOn(ScribeDatabase database, string sql)
    {
        using var connection = database.Open();
        Execute(connection, sql);
    }
}
