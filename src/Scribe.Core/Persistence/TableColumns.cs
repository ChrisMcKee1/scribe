using Microsoft.Data.Sqlite;

namespace Scribe.Core.Persistence;

/// <summary>
/// One table's columns, read once for one operation (DATA-A-06, <see cref="Diagnostics.PerfFlags.GroupHistorySchemaProbes"/>),
/// with the same lazy repair each column's own probe makes: a column that is missing is added, and the table is read
/// again to see whether it now is. Never kept past the operation, so a repair, a rebuild or another build's change is
/// seen by the next one, as today.
/// </summary>
internal sealed class TableColumns
{
    private readonly SqliteConnection _connection;
    private readonly string _table;
    private HashSet<string>? _names;

    /// <param name="table">A trusted constant; it is placed in the statement text, as today's probes place it.</param>
    internal TableColumns(SqliteConnection connection, string table)
    {
        _connection = connection;
        _table = table;
    }

    /// <summary>
    /// Whether the table has <paramref name="columnName"/>, adding it with <paramref name="declaration"/> when it is
    /// missing. False when it is missing and cannot be added: the caller falls back as it always has.
    /// </summary>
    internal bool Ensure(string columnName, string declaration)
    {
        _names ??= Read(_connection, _table);
        if (_names.Contains(columnName))
        {
            return true;
        }

        // Upgrade older databases lazily the first time history is read/written in a newer build.
        // If this fails, history still works without the newer column. Names are trusted constants.
        try
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {_table} ADD COLUMN {columnName} {declaration};";
            alter.ExecuteNonQuery();
        }
        catch
        {
            // Read again either way: another connection may have added it since this operation read the table.
            _names = Read(_connection, _table);
            return _names.Contains(columnName);
        }

        _names = Read(_connection, _table);
        return _names.Contains(columnName);
    }

    private static HashSet<string> Read(SqliteConnection connection, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }
}
