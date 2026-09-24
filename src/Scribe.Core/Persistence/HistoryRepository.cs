using System.Globalization;
using Microsoft.Data.Sqlite;
using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <inheritdoc cref="IHistoryRepository"/>
/// <remarks>
/// New audio is stored as 16-bit PCM (<see cref="AudioBlobEncoding.Pcm16"/>) behind a header that
/// identifies it even where the encoding column is missing; blobs written by earlier builds stay
/// float32 and still decode. Every write takes the database write gate, so it waits for a
/// maintenance VACUUM rather than failing on a busy database.
/// </remarks>
public sealed class HistoryRepository : IHistoryRepository, IHistoryMaintenance
{
    // Round-trips timestamps losslessly with offset, sortable as text for the timestamp index.
    private const string TimestampFormat = "O";

    private readonly ScribeDatabase _database;

    public HistoryRepository(ScribeDatabase database) => _database = database;

    /// <summary>
    /// Largest encoded capture this repository stores. A capture that could never fit under the
    /// stored-audio cap would only be evicted on the next maintenance pass, so it is not written at
    /// all and its entry is saved as text only. Settable for tests, which cannot allocate 250 MB.
    /// </summary>
    internal long MaxAudioBlobBytes { get; init; } = StorageRetentionPolicy.MaxStoredAudioBytes;

    /// <summary>
    /// Most stored bytes of recordings <see cref="Clear"/> deletes in one transaction: the slice storage
    /// maintenance uses. Settable for tests.
    /// </summary>
    internal long ClearSliceBytes { get; init; } = StorageMaintenanceOptions.Default.BlobBatchBytes;

    public HistoryEntry Add(HistoryEntry entry)
        => Add(entry, audio: null);

    public HistoryEntry Add(HistoryEntry entry, CapturedAudio? audio)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // A dictation's write is arriving: maintenance stops heavy work now, so the gate below is
        // free within one short step instead of after a whole VACUUM.
        _database.RequestYield();

        HistoryEntry saved;
        var storedAudio = false;
        using (_database.EnterWriteScope())
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            long? blobId = entry.AudioBlobId;
            if (audio is not null && InsertAudioBlob(connection, audio) is { } newBlobId)
            {
                blobId = newBlobId;
                storedAudio = true;
            }

            var hasCleanupColumn = EnsureHistoryColumn(connection, "cleanup_ms", "INTEGER NULL");
            var hasModelColumn = EnsureHistoryColumn(connection, "transcription_model_id", "TEXT NULL");
            var optionalColumns =
                (hasCleanupColumn ? ", cleanup_ms" : string.Empty) +
                (hasModelColumn ? ", transcription_model_id" : string.Empty);
            var optionalValues =
                (hasCleanupColumn ? ", $cleanup_ms" : string.Empty) +
                (hasModelColumn ? ", $model_id" : string.Empty);
            using var historyCommand = connection.CreateCommand();
            historyCommand.Transaction = transaction;
            historyCommand.CommandText =
                $"""
                INSERT INTO history (timestamp_utc, text, audio_ms, decode_ms, target_app, audio_blob_id{optionalColumns})
                VALUES ($ts, $text, $audio_ms, $decode_ms, $target_app, $blob_id{optionalValues});
                SELECT last_insert_rowid();
                """;
            historyCommand.Parameters.AddWithValue("$ts", entry.TimestampUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            historyCommand.Parameters.AddWithValue("$text", entry.Text);
            historyCommand.Parameters.AddWithValue("$audio_ms", entry.AudioMilliseconds);
            historyCommand.Parameters.AddWithValue("$decode_ms", entry.DecodeMilliseconds);
            if (hasCleanupColumn)
            {
                historyCommand.Parameters.AddWithValue("$cleanup_ms", (object?)entry.CleanupMilliseconds ?? DBNull.Value);
            }

            if (hasModelColumn)
            {
                historyCommand.Parameters.AddWithValue("$model_id", (object?)entry.TranscriptionModelId ?? DBNull.Value);
            }

            historyCommand.Parameters.AddWithValue("$target_app", (object?)entry.TargetApp ?? DBNull.Value);
            historyCommand.Parameters.AddWithValue("$blob_id", (object?)blobId ?? DBNull.Value);

            var id = (long)(historyCommand.ExecuteScalar() ?? 0L);
            transaction.Commit();
            saved = entry with { Id = id, AudioBlobId = blobId };
        }

        if (storedAudio)
        {
            _database.NotifyStorageChanged(StorageChange.AudioStored);
        }

        return saved;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The capture is larger than the stored-audio cap, so it could never be kept.
    /// </exception>
    public long AddAudioBlob(CapturedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);

        long blobId;
        using (_database.EnterWriteScope())
        {
            using var connection = _database.Open();
            blobId = InsertAudioBlob(connection, audio)
                ?? throw new ArgumentOutOfRangeException(
                    nameof(audio), "The capture is larger than the stored audio limit.");
        }

        _database.NotifyStorageChanged(StorageChange.AudioStored);
        return blobId;
    }

    // Writes one blob in the current format and returns its id, or null when the capture is too
    // large to keep. The PCM16 header makes a blob readable even where the encoding column is
    // missing, so the format no longer depends on the column; the column is still written wherever
    // it exists, for anything that reads it.
    private long? InsertAudioBlob(SqliteConnection connection, CapturedAudio audio)
    {
        if (AudioBlobCodec.EncodedLength(audio.Samples.Length, AudioBlobEncoding.Pcm16) > MaxAudioBlobBytes)
        {
            return null;
        }

        var hasEncoding = EnsureColumn(connection, "audio_blobs", "encoding", "INTEGER NOT NULL DEFAULT 0");
        using var command = connection.CreateCommand();
        command.CommandText = hasEncoding
            ? """
              INSERT INTO audio_blobs (sample_rate, samples, created_utc, encoding)
              VALUES ($rate, $samples, $created, $encoding);
              SELECT last_insert_rowid();
              """
            : """
              INSERT INTO audio_blobs (sample_rate, samples, created_utc)
              VALUES ($rate, $samples, $created);
              SELECT last_insert_rowid();
              """;
        command.Parameters.AddWithValue("$rate", audio.SampleRate);
        command.Parameters.AddWithValue("$samples", AudioBlobCodec.EncodePcm16(audio.Samples));
        command.Parameters.AddWithValue("$created", FormatTimestamp(DateTimeOffset.UtcNow));
        if (hasEncoding)
        {
            command.Parameters.AddWithValue("$encoding", (int)AudioBlobEncoding.Pcm16);
        }

        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public IReadOnlyList<HistoryEntry> GetRecent(int limit = 100)
    {
        using var connection = _database.Open();
        var hasCleanupColumn = EnsureHistoryColumn(connection, "cleanup_ms", "INTEGER NULL");
        var hasModelColumn = EnsureHistoryColumn(connection, "transcription_model_id", "TEXT NULL");
        var hasRatingColumn = EnsureHistoryColumn(connection, "ai_rating", "INTEGER NULL");
        var cleanupExpression = hasCleanupColumn ? "cleanup_ms" : "NULL";
        var modelExpression = hasModelColumn ? "transcription_model_id" : "NULL";
        var ratingExpression = hasRatingColumn ? "ai_rating" : "NULL";
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT id, timestamp_utc, text, audio_ms, decode_ms, {cleanupExpression},
                   target_app, audio_blob_id, {modelExpression}, {ratingExpression}
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
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? AiRating.Unrated : (AiRating)reader.GetInt32(9)));
        }

        return results;
    }

    public CapturedAudio? GetAudio(long blobId)
    {
        using var connection = _database.Open();
        var hasEncoding = HasColumn(connection, "audio_blobs", "encoding");
        using var command = connection.CreateCommand();
        command.CommandText = hasEncoding
            ? "SELECT sample_rate, samples, encoding FROM audio_blobs WHERE id = $id;"
            : "SELECT sample_rate, samples FROM audio_blobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", blobId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var sampleRate = reader.GetInt32(0);
        var bytes = (byte[])reader[1];
        var encoding = hasEncoding && !reader.IsDBNull(2)
            ? (AudioBlobEncoding)reader.GetInt32(2)
            : AudioBlobEncoding.Float32;
        return AudioBlobCodec.Decode(bytes, encoding) is { } samples
            ? new CapturedAudio(samples, sampleRate)
            : null;
    }

    /// <summary>
    /// Records what the user thought of an AI-cleaned result, or clears it when they tap the same
    /// thumb again.
    /// </summary>
    /// <remarks>
    /// Writes the column defensively rather than assuming the migration ran: this repository is
    /// used against databases that were rebuilt from a damaged file, where a table can come back
    /// without the newest column, and a rating that throws would take the History page down over an
    /// opinion nobody needed to record.
    /// </remarks>
    public void SetAiRating(long id, AiRating rating)
    {
        using var writeScope = _database.EnterWriteScope();
        using var connection = _database.Open();
        if (!EnsureHistoryColumn(connection, "ai_rating", "INTEGER NULL"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE history SET ai_rating = $rating WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue(
            "$rating", rating == AiRating.Unrated ? DBNull.Value : (int)rating);
        command.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        int changes;
        using (_database.EnterWriteScope())
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
            changes = command.ExecuteNonQuery();
            transaction.Commit();
        }

        if (changes > 0)
        {
            _database.NotifyStorageChanged(StorageChange.HistoryEntryDeleted);
        }
    }

    public void Clear()
    {
        int entries;
        List<StoredAudioBlob> recordings;
        using (_database.EnterWriteScope())
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM history;";
            entries = command.ExecuteNonQuery();
            recordings = ListStoredAudio(connection);
            transaction.Commit();
        }

        /*
         * Recordings go in slices, each its own transaction, the way storage maintenance deletes them.
         *
         * With secure delete on, every page a deleted recording frees is written again, as zeros, and the
         * WAL holds a whole transaction until it commits. One statement for all of them (up to 250 MB)
         * needed that much free disk at once, and Clear could fail on a nearly full one; a slice needs
         * only its own size, and the automatic checkpoint after it lets the next slice reuse the log. A
         * single recording larger than a slice is still one transaction of its own size. Each slice checks
         * again that nothing references its recordings, so one picked up since the list was read stays.
         */
        var recordingsDeleted = 0;
        try
        {
            foreach (var slice in StorageMaintenance.Slices(recordings, ClearSliceBytes, StorageMaintenanceOptions.Default.BlobBatchSize))
            {
                using (_database.EnterWriteScope())
                {
                    using var connection = _database.Open();
                    using var transaction = connection.BeginTransaction();
                    recordingsDeleted += DeleteUnreferencedAudio(connection, slice, DateTimeOffset.MaxValue).BlobsDeleted;
                    transaction.Commit();
                }
            }
        }
        finally
        {
            // Even when a slice fails (a full disk): the history rows are gone already, so maintenance must
            // still empty the WAL of them, and it deletes the recordings left behind as unreferenced audio.
            if (entries > 0 || recordingsDeleted > 0)
            {
                _database.NotifyStorageChanged(StorageChange.HistoryCleared);
            }
        }
    }

    public int PruneOlderThan(DateTimeOffset cutoffUtc)
    {
        // Same shape as CleanupFailureLog.PruneOlderThan: the timestamp format ("O") sorts as
        // text, so an indexed string comparison is a correct age filter. Audio blobs go second,
        // and only those both old AND orphaned: a blob still referenced by a surviving entry
        // (however old) must stay playable, mirroring the ownership rule in Delete().
        try
        {
            using var writeScope = _database.EnterWriteScope();
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            var removed = DeleteEntriesOlderThan(connection, cutoffUtc);
            var unreferenced = ListStoredAudio(connection)
                .Where(blob => !blob.Referenced)
                .Select(blob => blob.Id)
                .ToList();
            DeleteUnreferencedAudio(connection, unreferenced, cutoffUtc);

            transaction.Commit();
            return removed;
        }
        catch
        {
            // Retention is housekeeping; a locked or damaged database must never take down startup.
            return 0;
        }
    }

    // --- IHistoryMaintenance --------------------------------------------------------------

    public int DeleteEntriesOlderThan(DateTimeOffset cutoffUtc)
    {
        using var writeScope = _database.EnterWriteScope();
        using var connection = _database.Open();
        return DeleteEntriesOlderThan(connection, cutoffUtc);
    }

    public int ClearAudioOlderThan(DateTimeOffset cutoffUtc)
    {
        using var writeScope = _database.EnterWriteScope();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE history SET audio_blob_id = NULL
            WHERE audio_blob_id IS NOT NULL AND timestamp_utc < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", FormatTimestamp(cutoffUtc));
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<StoredAudioBlob> ListStoredAudio()
    {
        using var connection = _database.Open();
        return ListStoredAudio(connection);
    }

    public AudioDeletion DeleteUnreferencedAudio(IReadOnlyCollection<long> blobIds, DateTimeOffset storedBeforeUtc)
    {
        ArgumentNullException.ThrowIfNull(blobIds);
        if (blobIds.Count == 0)
        {
            return default;
        }

        using var writeScope = _database.EnterWriteScope();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var deleted = DeleteUnreferencedAudio(connection, blobIds, storedBeforeUtc);
        transaction.Commit();
        return deleted;
    }

    public AudioDeletion EvictAudio(IReadOnlyCollection<long> blobIds, long maxStoredBytes)
    {
        ArgumentNullException.ThrowIfNull(blobIds);
        if (blobIds.Count == 0)
        {
            return default;
        }

        using var writeScope = _database.EnterWriteScope();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        // What is stored now, read inside this write transaction (BEGIN IMMEDIATE, so nothing can
        // commit between this read and the deletes): a user delete since the inventory may already
        // have brought the total under the cap, and then nothing more has to go.
        using var total = connection.CreateCommand();
        total.CommandText = "SELECT COALESCE(SUM(length(samples)), 0) FROM audio_blobs;";
        var stored = Convert.ToInt64(total.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);

        using var detach = connection.CreateCommand();
        detach.CommandText = "UPDATE history SET audio_blob_id = NULL WHERE audio_blob_id = $id;";
        var detachId = detach.Parameters.Add("$id", SqliteType.Integer);

        using var size = connection.CreateCommand();
        size.CommandText = "SELECT length(samples) FROM audio_blobs WHERE id = $id;";
        var sizeId = size.Parameters.Add("$id", SqliteType.Integer);

        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM audio_blobs WHERE id = $id;";
        var deleteId = delete.Parameters.Add("$id", SqliteType.Integer);

        var result = default(AudioDeletion);
        foreach (var blobId in blobIds)
        {
            if (stored <= maxStoredBytes)
            {
                break;
            }

            detachId.Value = blobId;
            var cleared = detach.ExecuteNonQuery();

            sizeId.Value = blobId;
            if (size.ExecuteScalar() is not long bytes)
            {
                result += new AudioDeletion(0, 0, cleared);
                continue;
            }

            deleteId.Value = blobId;
            result += new AudioDeletion(delete.ExecuteNonQuery(), bytes, cleared);
            stored -= bytes;
        }

        transaction.Commit();
        return result;
    }

    public StoredAudioUsage GetStoredAudioUsage()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(length(samples)), 0) FROM audio_blobs;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new StoredAudioUsage(reader.GetInt32(0), reader.GetInt64(1))
            : default;
    }

    public void RequestYield() => _database.RequestYield();

    private static int DeleteEntriesOlderThan(SqliteConnection connection, DateTimeOffset cutoffUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history WHERE timestamp_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", FormatTimestamp(cutoffUtc));
        return command.ExecuteNonQuery();
    }

    // length(samples) is answered from the record header without loading the blob, and the
    // referenced check is an index lookup (ix_history_audio_blob), so this stays cheap however much
    // audio is stored. Reading created_utc here instead would walk every blob's overflow pages,
    // because it is stored after the samples in each record.
    private static List<StoredAudioBlob> ListStoredAudio(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT b.id, length(b.samples),
                   EXISTS (SELECT 1 FROM history AS h WHERE h.audio_blob_id = b.id)
            FROM audio_blobs AS b
            ORDER BY b.id;
            """;
        var blobs = new List<StoredAudioBlob>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            blobs.Add(new StoredAudioBlob(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) != 0));
        }

        return blobs;
    }

    // Re-checks ownership per blob inside the caller's transaction, so a blob that picked up a
    // reference since it was listed is never deleted. created_utc is only read for these few rows.
    private static AudioDeletion DeleteUnreferencedAudio(
        SqliteConnection connection,
        IReadOnlyCollection<long> blobIds,
        DateTimeOffset storedBeforeUtc)
    {
        using var size = connection.CreateCommand();
        size.CommandText =
            """
            SELECT length(samples) FROM audio_blobs
            WHERE id = $id
                AND created_utc < $before
                AND NOT EXISTS (SELECT 1 FROM history WHERE audio_blob_id = $id);
            """;
        var sizeId = size.Parameters.Add("$id", SqliteType.Integer);
        size.Parameters.AddWithValue("$before", FormatTimestamp(storedBeforeUtc));

        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM audio_blobs WHERE id = $id;";
        var deleteId = delete.Parameters.Add("$id", SqliteType.Integer);

        var result = default(AudioDeletion);
        foreach (var blobId in blobIds)
        {
            sizeId.Value = blobId;
            if (size.ExecuteScalar() is not long bytes)
            {
                continue;
            }

            deleteId.Value = blobId;
            result += new AudioDeletion(delete.ExecuteNonQuery(), bytes, 0);
        }

        return result;
    }

    // Always UTC: the stored strings compare as text, which is only a time comparison when every
    // value carries the same offset.
    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static bool EnsureHistoryColumn(
        SqliteConnection connection,
        string columnName,
        string declaration) =>
        EnsureColumn(connection, "history", columnName, declaration);

    private static bool EnsureColumn(
        SqliteConnection connection,
        string table,
        string columnName,
        string declaration)
    {
        if (HasColumn(connection, table, columnName))
        {
            return true;
        }

        // Upgrade older databases lazily the first time history is read/written in a newer build.
        // If this fails, history still works without the newer column. Names are trusted constants.
        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {columnName} {declaration};";
            alter.ExecuteNonQuery();
        }
        catch
        {
            return false;
        }

        return HasColumn(connection, table, columnName);
    }

    private static bool HasColumn(
        SqliteConnection connection,
        string table,
        string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
