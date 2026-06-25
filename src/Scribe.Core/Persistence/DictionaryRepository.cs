using Microsoft.Data.Sqlite;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <inheritdoc cref="IDictionaryRepository"/>
public sealed class DictionaryRepository : IDictionaryRepository
{
    private readonly ScribeDatabase _database;

    public DictionaryRepository(ScribeDatabase database) => _database = database;

    public IReadOnlyList<DictionaryEntry> GetAll() => Query(enabledOnly: false);

    public IReadOnlyList<DictionaryEntry> GetEnabled() => Query(enabledOnly: true);

    private List<DictionaryEntry> Query(bool enabledOnly)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, pattern, replacement, whole_word, enabled FROM dictionary"
            + (enabledOnly ? " WHERE enabled = 1" : string.Empty)
            + " ORDER BY pattern;";

        var results = new List<DictionaryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new DictionaryEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return results;
    }

    public DictionaryEntry Add(DictionaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO dictionary (pattern, replacement, whole_word, enabled)
            VALUES ($pattern, $replacement, $whole_word, $enabled);
            SELECT last_insert_rowid();
            """;
        BindBody(command, entry);
        var id = (long)(command.ExecuteScalar() ?? 0L);
        return entry with { Id = id };
    }

    public void Update(DictionaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE dictionary
            SET pattern = $pattern, replacement = $replacement,
                whole_word = $whole_word, enabled = $enabled
            WHERE id = $id;
            """;
        BindBody(command, entry);
        command.Parameters.AddWithValue("$id", entry.Id);
        command.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dictionary WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public int SeedIfEmpty(IEnumerable<DictionaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using var connection = _database.Open();
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM dictionary;";
            if ((long)(count.ExecuteScalar() ?? 0L) > 0) return 0;
        }

        using var transaction = connection.BeginTransaction();
        var added = 0;
        foreach (var entry in entries)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO dictionary (pattern, replacement, whole_word, enabled)
                VALUES ($pattern, $replacement, $whole_word, $enabled)
                ON CONFLICT (pattern) DO NOTHING;
                """;
            BindBody(command, entry);
            added += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return added;
    }

    private static void BindBody(SqliteCommand command, DictionaryEntry entry)
    {
        command.Parameters.AddWithValue("$pattern", entry.Pattern);
        command.Parameters.AddWithValue("$replacement", entry.Replacement);
        command.Parameters.AddWithValue("$whole_word", entry.WholeWord ? 1 : 0);
        command.Parameters.AddWithValue("$enabled", entry.Enabled ? 1 : 0);
    }
}
