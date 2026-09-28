using Microsoft.Data.Sqlite;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <inheritdoc cref="IDictionaryRepository"/>
public sealed class DictionaryRepository : IDictionaryRepository
{
    private readonly ScribeDatabase _database;
    private readonly SaveCommands _saveCommands;
    private readonly bool _reuseCommands;

    public DictionaryRepository(ScribeDatabase database, Diagnostics.PerfFlags? flags = null)
    {
        _database = database;
        _saveCommands = SaveCommandsFor(flags);
        _reuseCommands = flags?.IsOn(Diagnostics.PerfFlags.ReuseSaveCommands) == true;
    }

    /// <summary>
    /// How a save writes its rows. <see cref="PerRow"/> is today's command per row; <see cref="Reused"/> prepares one
    /// DELETE, one INSERT and one UPDATE per save and rebinds them (DATA-A-03, <see cref="Diagnostics.PerfFlags.ReuseSaveCommands"/>);
    /// <see cref="ReusedSkippingUnchanged"/> also skips an UPDATE that would write exactly what the row already holds
    /// (DATA-O-07, <see cref="Diagnostics.PerfFlags.DictionaryDiffSave"/>, which is built on the reused commands).
    /// </summary>
    internal enum SaveCommands
    {
        PerRow,
        Reused,
        ReusedSkippingUnchanged,
    }

    internal static SaveCommands SaveCommandsFor(Diagnostics.PerfFlags? flags) =>
        flags?.IsOn(Diagnostics.PerfFlags.DictionaryDiffSave) == true ? SaveCommands.ReusedSkippingUnchanged
        : flags?.IsOn(Diagnostics.PerfFlags.ReuseSaveCommands) == true ? SaveCommands.Reused
        : SaveCommands.PerRow;

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

        return AddRange([entry])[0];
    }

    public IReadOnlyList<DictionaryEntry> AddRange(IReadOnlyList<DictionaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return [];
        }

        using var writeScope = _database.EnterWriteScope();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var persisted = new List<DictionaryEntry>(entries.Count);
        if (_reuseCommands)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO dictionary (pattern, replacement, whole_word, enabled)
                VALUES ($pattern, $replacement, $whole_word, $enabled);
                SELECT last_insert_rowid();
                """;
            var body = BodyParameters(insert);
            foreach (var entry in entries)
            {
                ArgumentNullException.ThrowIfNull(entry);
                body.Bind(entry);
                var id = (long)(insert.ExecuteScalar() ?? 0L);
                persisted.Add(entry with { Id = id });
            }

            transaction.Commit();
            return persisted;
        }

        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO dictionary (pattern, replacement, whole_word, enabled)
                VALUES ($pattern, $replacement, $whole_word, $enabled);
                SELECT last_insert_rowid();
                """;
            BindBody(command, entry);
            var id = (long)(command.ExecuteScalar() ?? 0L);
            persisted.Add(entry with { Id = id });
        }

        transaction.Commit();
        return persisted;
    }

    public void Update(DictionaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var writeScope = _database.EnterWriteScope();
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
        using var writeScope = _database.EnterWriteScope();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dictionary WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SaveAll(IReadOnlyList<DictionaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using var writeScope = _database.EnterWriteScope();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        SaveAll(connection, transaction, entries, _saveCommands);
        transaction.Commit();
    }

    internal static void SaveAll(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        IReadOnlyList<DictionaryEntry> entries,
        SaveCommands saveCommands = SaveCommands.PerRow)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (saveCommands != SaveCommands.PerRow)
        {
            SaveAllReusingCommands(connection, transaction, entries, saveCommands == SaveCommands.ReusedSkippingUnchanged);
            return;
        }

        // Delete first so an edit that renames row A to row B's old pattern while deleting B never
        // trips the unique index mid-save.
        var keptIds = entries.Where(e => e.Id != 0).Select(e => e.Id).ToHashSet();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id FROM dictionary;";
            var toDelete = new List<long>();
            using (var reader = read.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    if (!keptIds.Contains(id))
                    {
                        toDelete.Add(id);
                    }
                }
            }

            foreach (var id in toDelete)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM dictionary WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }
        }

        foreach (var entry in entries)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = entry.Id == 0
                ? """
                  INSERT INTO dictionary (pattern, replacement, whole_word, enabled)
                  VALUES ($pattern, $replacement, $whole_word, $enabled);
                  """
                : """
                  UPDATE dictionary
                  SET pattern = $pattern, replacement = $replacement,
                      whole_word = $whole_word, enabled = $enabled
                  WHERE id = $id;
                  """;
            BindBody(command, entry);
            if (entry.Id != 0)
            {
                command.Parameters.AddWithValue("$id", entry.Id);
            }

            command.ExecuteNonQuery();
        }

    }

    // SaveAll with one prepared DELETE, INSERT and UPDATE rebound per row (DATA-A-03): the same statements in the same
    // order, the same parameter types. With skipUnchanged (DATA-O-07) an UPDATE is left out only when the row it names
    // already holds exactly what it would write: the stored bytes of both texts and the stored integers of both flags,
    // compared with what binding the entry writes (so a flag stored as 2 is rewritten as 1, as today). The rows are
    // compared as the save changes them, entry by entry, so an id listed twice ends with its last entry, as today; an id
    // the inventory does not hold is always written, since a row inserted earlier in this save may carry it.
    private static void SaveAllReusingCommands(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<DictionaryEntry> entries, bool skipUnchanged)
    {
        // Delete first so an edit that renames row A to row B's old pattern while deleting B never
        // trips the unique index mid-save.
        var keptIds = entries.Where(e => e.Id != 0).Select(e => e.Id).ToHashSet();
        var stored = skipUnchanged ? new Dictionary<long, StoredRow>() : null;
        var toDelete = new List<long>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = skipUnchanged
                ? """
                  SELECT id, CAST(pattern AS BLOB), typeof(pattern), CAST(replacement AS BLOB), typeof(replacement),
                         whole_word, typeof(whole_word), enabled, typeof(enabled)
                  FROM dictionary;
                  """
                : "SELECT id FROM dictionary;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!keptIds.Contains(id))
                {
                    toDelete.Add(id);
                }
                else if (stored is not null)
                {
                    stored[id] = StoredRow.Read(reader);
                }
            }
        }

        if (toDelete.Count > 0)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM dictionary WHERE id = $id;";
            var deleteId = delete.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in toDelete)
            {
                deleteId.Value = id;
                delete.ExecuteNonQuery();
            }
        }

        SqliteCommand? insert = null;
        SqliteCommand? update = null;
        BodyParameterSet insertBody = default;
        BodyParameterSet updateBody = default;
        SqliteParameter? updateId = null;
        try
        {
            foreach (var entry in entries)
            {
                if (entry.Id == 0)
                {
                    if (insert is null)
                    {
                        insert = connection.CreateCommand();
                        insert.Transaction = transaction;
                        insert.CommandText =
                            """
                            INSERT INTO dictionary (pattern, replacement, whole_word, enabled)
                            VALUES ($pattern, $replacement, $whole_word, $enabled);
                            """;
                        insertBody = BodyParameters(insert);
                    }

                    insertBody.Bind(entry);
                    insert.ExecuteNonQuery();
                    continue;
                }

                StoredRow written = default;
                if (stored is not null)
                {
                    written = StoredRow.Of(entry);
                    if (stored.TryGetValue(entry.Id, out var current) && current.Equals(written))
                    {
                        continue;
                    }
                }

                if (update is null)
                {
                    update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText =
                        """
                        UPDATE dictionary
                        SET pattern = $pattern, replacement = $replacement,
                            whole_word = $whole_word, enabled = $enabled
                        WHERE id = $id;
                        """;
                    updateBody = BodyParameters(update);
                    updateId = update.Parameters.Add("$id", SqliteType.Integer);
                }

                updateBody.Bind(entry);
                updateId!.Value = entry.Id;
                update.ExecuteNonQuery();
                if (stored is not null && stored.ContainsKey(entry.Id))
                {
                    stored[entry.Id] = written;
                }
            }
        }
        finally
        {
            insert?.Dispose();
            update?.Dispose();
        }
    }

    // A row's values as SQLite holds them, for DATA-O-07's comparison: each text as its stored bytes and storage class,
    // each flag as its stored value and storage class. Equal only when an UPDATE would write the very same thing.
    private readonly record struct StoredRow(
        string Pattern, string PatternType, string Replacement, string ReplacementType,
        object? WholeWord, string WholeWordType, object? Enabled, string EnabledType)
    {
        // Byte strings are compared as Base64 text, which is exact and gives the record value equality.
        internal static StoredRow Read(SqliteDataReader reader) => new(
            Bytes(reader, 1), reader.GetString(2), Bytes(reader, 3), reader.GetString(4),
            reader.GetValue(5), reader.GetString(6), reader.GetValue(7), reader.GetString(8));

        // What binding the entry stores: UTF-8 text (with the replacement a lone surrogate gets) and 1 or 0. A null text,
        // which binding refuses, matches no stored row, so its UPDATE runs and fails as it always did.
        internal static StoredRow Of(DictionaryEntry entry) => new(
            TextBytes(entry.Pattern), "text", TextBytes(entry.Replacement), "text",
            entry.WholeWord ? 1L : 0L, "integer", entry.Enabled ? 1L : 0L, "integer");

        private static string TextBytes(string? text) =>
            text is null ? "unbound" : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

        private static string Bytes(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? "null" : Convert.ToBase64String((byte[])reader.GetValue(ordinal));
    }

    // The four body parameters of a reused command, typed as AddWithValue types them from the same values.
    private readonly struct BodyParameterSet(
        SqliteParameter pattern, SqliteParameter replacement, SqliteParameter wholeWord, SqliteParameter enabled)
    {
        public void Bind(DictionaryEntry entry)
        {
            pattern.Value = entry.Pattern;
            replacement.Value = entry.Replacement;
            wholeWord.Value = entry.WholeWord ? 1 : 0;
            enabled.Value = entry.Enabled ? 1 : 0;
        }
    }

    private static BodyParameterSet BodyParameters(SqliteCommand command) => new(
        command.Parameters.Add("$pattern", SqliteType.Text),
        command.Parameters.Add("$replacement", SqliteType.Text),
        command.Parameters.Add("$whole_word", SqliteType.Integer),
        command.Parameters.Add("$enabled", SqliteType.Integer));

    public int SeedIfEmpty(IEnumerable<DictionaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using var writeScope = _database.EnterWriteScope();
        using var connection = _database.Open();
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM dictionary;";
            if ((long)(count.ExecuteScalar() ?? 0L) > 0) return 0;
        }

        using var transaction = connection.BeginTransaction();
        var added = 0;
        if (_reuseCommands)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO dictionary (pattern, replacement, whole_word, enabled)
                VALUES ($pattern, $replacement, $whole_word, $enabled)
                ON CONFLICT (pattern) DO NOTHING;
                """;
            var body = BodyParameters(insert);
            foreach (var entry in entries)
            {
                body.Bind(entry);
                added += insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return added;
        }

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

    public int DisableUnmodifiedEntries(IEnumerable<DictionaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using var writeScope = _database.EnterWriteScope();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var disabled = 0;

        if (_reuseCommands)
        {
            using var disable = connection.CreateCommand();
            disable.Transaction = transaction;
            disable.CommandText =
                """
                UPDATE dictionary
                SET enabled = 0
                WHERE pattern = $pattern AND replacement = $replacement AND enabled = 1;
                """;
            var pattern = disable.Parameters.Add("$pattern", SqliteType.Text);
            var replacement = disable.Parameters.Add("$replacement", SqliteType.Text);
            foreach (var entry in entries)
            {
                pattern.Value = entry.Pattern;
                replacement.Value = entry.Replacement;
                disabled += disable.ExecuteNonQuery();
            }

            transaction.Commit();
            return disabled;
        }

        foreach (var entry in entries)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            // Matching the replacement as well as the pattern is what makes this safe: an entry the
            // user edited no longer matches, so their change survives. Requiring enabled = 1 means a
            // deliberate re-enable is not undone on a later launch.
            command.CommandText =
                """
                UPDATE dictionary
                SET enabled = 0
                WHERE pattern = $pattern AND replacement = $replacement AND enabled = 1;
                """;
            command.Parameters.AddWithValue("$pattern", entry.Pattern);
            command.Parameters.AddWithValue("$replacement", entry.Replacement);
            disabled += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return disabled;
    }

    private static void BindBody(SqliteCommand command, DictionaryEntry entry)
    {
        command.Parameters.AddWithValue("$pattern", entry.Pattern);
        command.Parameters.AddWithValue("$replacement", entry.Replacement);
        command.Parameters.AddWithValue("$whole_word", entry.WholeWord ? 1 : 0);
        command.Parameters.AddWithValue("$enabled", entry.Enabled ? 1 : 0);
    }
}
