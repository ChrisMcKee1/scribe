using Microsoft.Data.Sqlite;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <inheritdoc cref="ISnippetRepository"/>
public sealed class SnippetRepository : ISnippetRepository
{
    private readonly ScribeDatabase _database;

    public SnippetRepository(ScribeDatabase database) => _database = database;

    public IReadOnlyList<Snippet> GetAll() => Query(enabledOnly: false);

    public IReadOnlyList<Snippet> GetEnabled() => Query(enabledOnly: true);

    private List<Snippet> Query(bool enabledOnly)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, phrase, template, enabled FROM snippets"
            + (enabledOnly ? " WHERE enabled = 1" : string.Empty)
            + " ORDER BY phrase;";

        var results = new List<Snippet>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Snippet(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3)));
        }

        return results;
    }

    public void SaveAll(IReadOnlyList<Snippet> snippets)
    {
        ArgumentNullException.ThrowIfNull(snippets);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        SaveAll(connection, transaction, snippets);
        transaction.Commit();
    }

    internal static void SaveAll(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Snippet> snippets)
    {
        ArgumentNullException.ThrowIfNull(snippets);

        // Delete first so a phrase can move from a deleted row to a new one within one save
        // without tripping the unique index (same pattern as DictionaryRepository.SaveAll).
        var keptIds = snippets.Where(s => s.Id != 0).Select(s => s.Id).ToHashSet();
        var toDelete = new List<long>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id FROM snippets;";
            using var reader = read.ExecuteReader();
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
            delete.CommandText = "DELETE FROM snippets WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        foreach (var snippet in snippets)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = snippet.Id == 0
                ? """
                  INSERT INTO snippets (phrase, template, enabled)
                  VALUES ($phrase, $template, $enabled);
                  """
                : """
                  UPDATE snippets
                  SET phrase = $phrase, template = $template, enabled = $enabled
                  WHERE id = $id;
                  """;
            command.Parameters.AddWithValue("$phrase", snippet.Phrase);
            command.Parameters.AddWithValue("$template", snippet.Template);
            command.Parameters.AddWithValue("$enabled", snippet.Enabled ? 1 : 0);
            if (snippet.Id != 0)
            {
                command.Parameters.AddWithValue("$id", snippet.Id);
            }

            command.ExecuteNonQuery();
        }

    }
}
