using System.Globalization;
using System.Runtime.InteropServices;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <inheritdoc cref="IHistoryRepository"/>
public sealed class HistoryRepository : IHistoryRepository
{
    // Round-trips timestamps losslessly with offset, sortable as text for the timestamp index.
    private const string TimestampFormat = "O";

    private readonly ScribeDatabase _database;

    public HistoryRepository(ScribeDatabase database) => _database = database;

    public HistoryEntry Add(HistoryEntry entry)
        => Add(entry, audio: null);

    public HistoryEntry Add(HistoryEntry entry, CapturedAudio? audio)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        long? blobId = entry.AudioBlobId;
        if (audio is not null)
        {
            using var audioCommand = connection.CreateCommand();
            audioCommand.Transaction = transaction;
            audioCommand.CommandText =
                """
                INSERT INTO audio_blobs (sample_rate, samples, created_utc)
                VALUES ($rate, $samples, $created);
                SELECT last_insert_rowid();
                """;
            audioCommand.Parameters.AddWithValue("$rate", audio.SampleRate);
            audioCommand.Parameters.AddWithValue("$samples", ToBytes(audio.Samples));
            audioCommand.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            blobId = (long)(audioCommand.ExecuteScalar() ?? 0L);
        }

        using var historyCommand = connection.CreateCommand();
        historyCommand.Transaction = transaction;
        historyCommand.CommandText =
            """
            INSERT INTO history (timestamp_utc, text, audio_ms, decode_ms, target_app, audio_blob_id)
            VALUES ($ts, $text, $audio_ms, $decode_ms, $target_app, $blob_id);
            SELECT last_insert_rowid();
            """;
        historyCommand.Parameters.AddWithValue("$ts", entry.TimestampUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        historyCommand.Parameters.AddWithValue("$text", entry.Text);
        historyCommand.Parameters.AddWithValue("$audio_ms", entry.AudioMilliseconds);
        historyCommand.Parameters.AddWithValue("$decode_ms", entry.DecodeMilliseconds);
        historyCommand.Parameters.AddWithValue("$target_app", (object?)entry.TargetApp ?? DBNull.Value);
        historyCommand.Parameters.AddWithValue("$blob_id", (object?)blobId ?? DBNull.Value);

        var id = (long)(historyCommand.ExecuteScalar() ?? 0L);
        transaction.Commit();
        return entry with { Id = id, AudioBlobId = blobId };
    }

    public long AddAudioBlob(CapturedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO audio_blobs (sample_rate, samples, created_utc)
            VALUES ($rate, $samples, $created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$rate", audio.SampleRate);
        command.Parameters.AddWithValue("$samples", ToBytes(audio.Samples));
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));

        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public IReadOnlyList<HistoryEntry> GetRecent(int limit = 100)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, timestamp_utc, text, audio_ms, decode_ms, target_app, audio_blob_id
            FROM history
            ORDER BY timestamp_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<HistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new HistoryEntry(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        }

        return results;
    }

    public CapturedAudio? GetAudio(long blobId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sample_rate, samples FROM audio_blobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", blobId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var sampleRate = reader.GetInt32(0);
        var bytes = (byte[])reader[1];
        return new CapturedAudio(ToFloats(bytes), sampleRate);
    }

    public void Delete(long id)
    {
        using var connection = _database.Open();
                using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                        """
                        DELETE FROM audio_blobs
                        WHERE id = (SELECT audio_blob_id FROM history WHERE id = $id)
                            AND NOT EXISTS (
                                    SELECT 1 FROM history
                                    WHERE audio_blob_id = (SELECT audio_blob_id FROM history WHERE id = $id)
                                        AND id <> $id
                            );
                        DELETE FROM history WHERE id = $id;
                        """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
                transaction.Commit();
    }

    public void Clear()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history; DELETE FROM audio_blobs;";
        command.ExecuteNonQuery();
    }

    internal static byte[] ToBytes(float[] samples) =>
        MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();

    internal static float[] ToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * sizeof(float));
        return floats;
    }
}
