using System.Globalization;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <inheritdoc cref="ICleanupFailureLog"/>
public sealed class CleanupFailureLog : ICleanupFailureLog
{
    // Round-trips timestamps losslessly with offset, sortable as text for the timestamp index.
    private const string TimestampFormat = "O";
    private const int SampleMaxChars = 200;

    private readonly ScribeDatabase _database;

    public CleanupFailureLog(ScribeDatabase database) => _database = database;

    public CleanupFailure Add(CleanupFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO cleanup_failures (timestamp_utc, provider, model, reason, sample)
            VALUES ($ts, $provider, $model, $reason, $sample);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ts", failure.TimestampUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$provider", (object?)failure.Provider ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)failure.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", failure.Reason);
        command.Parameters.AddWithValue("$sample", (object?)Truncate(failure.Sample) ?? DBNull.Value);

        var id = (long)(command.ExecuteScalar() ?? 0L);
        return failure with { Id = id };
    }

    public IReadOnlyList<CleanupFailure> GetRecent(int limit = 50)
    {
        if (limit <= 0)
        {
            return [];
        }

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, timestamp_utc, provider, model, reason, sample
            FROM cleanup_failures
            ORDER BY timestamp_utc DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<CleanupFailure>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CleanupFailure(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return results;
    }

    public int Count()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cleanup_failures;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    public int Clear()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cleanup_failures;";
        return command.ExecuteNonQuery();
    }

    public int PruneOlderThan(DateTimeOffset cutoffUtc)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM cleanup_failures WHERE timestamp_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery();
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= SampleMaxChars ? trimmed : trimmed[..SampleMaxChars];
    }
}
