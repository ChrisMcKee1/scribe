using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// The audio storage additions against real files. They must never bump user_version: builds up to
/// 0.4.2 refuse a database whose user_version is above 7, and the Store and direct-download builds
/// share one database. So the encoding column and the index are applied on every open, an upgraded
/// file keeps playing its float32 audio while new audio is 16-bit PCM, a repair carries each blob's
/// encoding, and the file still works for the SQL an older build runs.
/// </summary>
public sealed class AudioStorageSchemaTests : IDisposable
{
    private readonly TempDatabaseFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_v7_database_with_float_audio_gains_the_additions_keeps_user_version_7_and_new_audio_is_half_the_size()
    {
        var samples = Enumerable.Range(0, 4000).Select(i => MathF.Sin(i / 20f) * 0.8f).ToArray();
        LegacyDatabase.Create(_folder.DatabasePath);
        var legacyBlob = LegacyDatabase.InsertFloatBlob(_folder.DatabasePath, samples, DateTimeOffset.UtcNow);
        LegacyDatabase.InsertHistory(_folder.DatabasePath, DateTimeOffset.UtcNow, "legacy", legacyBlob);
        Assert.Equal(0, LegacyDatabase.QueryInt64(_folder.DatabasePath, "PRAGMA auto_vacuum;"));

        using var db = _folder.Open();
        var repo = new HistoryRepository(db);

        Assert.Equal(7, DatabaseProbe.QueryInt64(db, "PRAGMA user_version;"));
        Assert.True(DatabaseProbe.HasColumn(db, "audio_blobs", "encoding"));
        Assert.Equal(1, DatabaseProbe.QueryInt64(db,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_history_audio_blob';"));

        // Opening never rewrites the file; the one-time conversion is maintenance's job.
        Assert.Equal(0, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));

        // The old blob reads back through the column default, bit for bit.
        Assert.Equal(samples, repo.GetAudio(legacyBlob)!.Samples);
        Assert.Equal((long)AudioBlobEncoding.Float32,
            DatabaseProbe.QueryInt64(db, $"SELECT encoding FROM audio_blobs WHERE id = {legacyBlob};"));

        var saved = repo.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "new", 250, 20), new CapturedAudio(samples, 16000));

        var sizes = repo.ListStoredAudio().ToDictionary(b => b.Id, b => b.Bytes);
        Assert.Equal(samples.Length * sizeof(float), sizes[legacyBlob]);
        Assert.Equal(AudioBlobCodec.Pcm16HeaderLength + (sizes[legacyBlob] / 2), sizes[saved.AudioBlobId!.Value]);
        Assert.Equal((long)AudioBlobEncoding.Pcm16,
            DatabaseProbe.QueryInt64(db, $"SELECT encoding FROM audio_blobs WHERE id = {saved.AudioBlobId};"));

        var decoded = repo.GetAudio(saved.AudioBlobId!.Value)!.Samples;
        Assert.Equal(samples.Length, decoded.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.InRange(MathF.Abs(decoded[i] - samples[i]), 0f, 2e-5f);
        }
    }

    [Fact]
    public void A_brand_new_database_starts_in_incremental_auto_vacuum_at_schema_7_with_the_additions()
    {
        using (var db = _folder.Open())
        {
            Assert.Equal(2, DatabaseProbe.QueryInt64(db, "PRAGMA auto_vacuum;"));
            Assert.Equal(7, DatabaseProbe.QueryInt64(db, "PRAGMA user_version;"));
            Assert.True(DatabaseProbe.HasColumn(db, "audio_blobs", "encoding"));
        }

        // Applying the additions again on the next open is a no-op, not a duplicate ALTER.
        using var reopened = _folder.Open();
        reopened.Initialize();
        Assert.True(DatabaseProbe.HasColumn(reopened, "audio_blobs", "encoding"));
    }

    [Fact]
    public void An_existing_database_is_never_switched_to_incremental_just_by_opening_it()
    {
        LegacyDatabase.Create(_folder.DatabasePath);

        using (var db = _folder.Open())
        {
            db.Initialize();
        }

        using var reopened = _folder.Open();
        Assert.Equal(0, DatabaseProbe.QueryInt64(reopened, "PRAGMA auto_vacuum;"));
    }

    [Fact]
    public void A_database_written_by_this_build_still_works_for_the_sql_an_older_build_runs()
    {
        var pcm = new[] { 0.5f, -0.25f, 0.125f };
        using (var db = _folder.Open())
        {
            new HistoryRepository(db).Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "new build", 1, 1), new CapturedAudio(pcm, 16000));
        }

        _folder.ReleasePooledConnections();

        // What 0.4.2 does, frozen here on purpose: refuse a user_version above 7, then write and read
        // with its own column lists, which know nothing of encoding.
        using var old = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _folder.DatabasePath,
            Pooling = false,
            ForeignKeys = true,
        }.ToString());
        old.Open();
        Assert.True(Scalar(old, "PRAGMA user_version;") <= 7);

        var oldSamples = new[] { 0.9f, -0.9f };
        var oldBlob = Scalar(old,
            "INSERT INTO audio_blobs (sample_rate, samples, created_utc) VALUES (16000, $samples, $created); SELECT last_insert_rowid();",
            ("$samples", MemoryMarshal.AsBytes(oldSamples.AsSpan()).ToArray()),
            ("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        var oldEntry = Scalar(old,
            """
            INSERT INTO history (timestamp_utc, text, audio_ms, decode_ms, target_app, audio_blob_id, cleanup_ms, transcription_model_id)
            VALUES ($ts, 'old build', 1, 1, NULL, $blob, NULL, NULL);
            SELECT last_insert_rowid();
            """,
            ("$ts", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            ("$blob", oldBlob));
        Assert.Equal(2, Scalar(old,
            "SELECT COUNT(*) FROM (SELECT id, timestamp_utc, text, audio_ms, decode_ms, cleanup_ms, target_app, audio_blob_id, transcription_model_id, ai_rating FROM history ORDER BY timestamp_utc DESC LIMIT 100);"));
        Scalar(old, "UPDATE history SET ai_rating = 1 WHERE id = $id; SELECT changes();", ("$id", oldEntry));
        Scalar(old,
            "DELETE FROM history WHERE timestamp_utc < $cutoff; DELETE FROM audio_blobs WHERE created_utc < $cutoff AND id NOT IN (SELECT audio_blob_id FROM history WHERE audio_blob_id IS NOT NULL); SELECT changes();",
            ("$cutoff", DateTimeOffset.UtcNow.AddDays(-90).ToString("O", CultureInfo.InvariantCulture)));
        old.Close();

        // Back in this build, the older build's blob is float32 through the column default.
        using var reopened = _folder.Open();
        Assert.Equal(7, DatabaseProbe.QueryInt64(reopened, "PRAGMA user_version;"));
        Assert.Equal(oldSamples, new HistoryRepository(reopened).GetAudio(oldBlob)!.Samples);
    }

    [Fact]
    public void A_database_that_lost_the_column_regains_it_lazily_and_old_rows_still_decode()
    {
        var samples = new[] { 0.5f, -0.25f, 0.125f };
        LegacyDatabase.Create(_folder.DatabasePath);
        var legacyBlob = LegacyDatabase.InsertFloatBlob(_folder.DatabasePath, samples, DateTimeOffset.UtcNow);

        using var db = _folder.Open();
        db.Initialize();

        // As if the additions could not be applied at open, for example after a repair.
        using (var raw = db.Open())
        using (var drop = raw.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE audio_blobs DROP COLUMN encoding;";
            drop.ExecuteNonQuery();
        }

        var repo = new HistoryRepository(db);
        Assert.False(DatabaseProbe.HasColumn(db, "audio_blobs", "encoding"));
        Assert.Equal(samples, repo.GetAudio(legacyBlob)!.Samples);

        var saved = repo.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, "new", 1, 1), new CapturedAudio(samples, 16000));

        Assert.True(DatabaseProbe.HasColumn(db, "audio_blobs", "encoding"));
        Assert.Equal(samples, repo.GetAudio(legacyBlob)!.Samples);
        Assert.Equal(AudioBlobCodec.EncodedLength(samples.Length, AudioBlobEncoding.Pcm16),
            repo.ListStoredAudio().Single(b => b.Id == saved.AudioBlobId).Bytes);
    }

    [Fact]
    public void A_pcm16_blob_whose_column_an_older_builds_repair_dropped_still_decodes_from_its_header()
    {
        var samples = Enumerable.Range(0, 1600).Select(i => MathF.Sin(i / 10f) * 0.6f).ToArray();
        long blobId;
        using (var db = _folder.Open())
        {
            blobId = new HistoryRepository(db).AddAudioBlob(new CapturedAudio(samples, 16000));
        }

        _folder.ReleasePooledConnections();

        // What a 0.4.2 repair leaves: its own v7 table, so the blob keeps its bytes but loses the
        // column, which this build then adds back with the float32 default.
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _folder.DatabasePath,
            Pooling = false,
        }.ToString()))
        {
            old.Open();
            Scalar(old, "ALTER TABLE audio_blobs DROP COLUMN encoding; SELECT 0;");
        }

        using var reopened = _folder.Open();
        Assert.Equal((long)AudioBlobEncoding.Float32,
            DatabaseProbe.QueryInt64(reopened, $"SELECT encoding FROM audio_blobs WHERE id = {blobId};"));

        var decoded = new HistoryRepository(reopened).GetAudio(blobId)!.Samples;
        Assert.Equal(samples.Length, decoded.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.InRange(MathF.Abs(decoded[i] - samples[i]), 0f, 2e-5f);
        }
    }

    [Fact]
    public void A_repair_carries_each_salvaged_blobs_encoding_into_the_rebuilt_database()
    {
        var samples = Enumerable.Range(0, 2000).Select(i => (i % 200 - 100) / 100f).ToArray();
        long blobId;
        using (var db = _folder.Open())
        {
            var repo = new HistoryRepository(db);
            blobId = repo.AddAudioBlob(new CapturedAudio(samples, 16000));

            // Bulk text at the tail of the file, so the damage below lands far from the audio.
            for (var i = 0; i < 50; i++)
            {
                repo.Add(new HistoryEntry(0, DateTimeOffset.UtcNow, new string('x', 4000), 1000, 50));
            }
        }

        _folder.ReleasePooledConnections();
        CorruptTail(_folder.DatabasePath, pages: 4);

        using var repaired = _folder.Open();
        repaired.Initialize();
        Assert.True(repaired.RepairedAtStartup);
        Assert.Equal(7, DatabaseProbe.QueryInt64(repaired, "PRAGMA user_version;"));

        // Carried as PCM16, column and all; the blob's own header would decode it correctly even
        // without the column (see the test above).
        var decoded = new HistoryRepository(repaired).GetAudio(blobId)!.Samples;
        Assert.Equal(samples.Length, decoded.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.InRange(MathF.Abs(decoded[i] - samples[i]), 0f, 2e-5f);
        }
    }

    [Fact]
    public void A_repair_of_a_v7_file_restores_its_float_audio_as_float()
    {
        var samples = new[] { 0.1f, 0.2f, -0.3f };
        LegacyDatabase.Create(_folder.DatabasePath);
        var blobId = LegacyDatabase.InsertFloatBlob(_folder.DatabasePath, samples, DateTimeOffset.UtcNow);
        for (var i = 0; i < 50; i++)
        {
            LegacyDatabase.InsertHistory(_folder.DatabasePath, DateTimeOffset.UtcNow, new string('y', 4000), blobId: null);
        }

        CorruptTail(_folder.DatabasePath, pages: 4);

        using var repaired = _folder.Open();
        repaired.Initialize();

        Assert.True(repaired.RepairedAtStartup);
        Assert.Equal(samples, new HistoryRepository(repaired).GetAudio(blobId)!.Samples);
    }

    [Fact]
    public void The_repair_copy_is_stamped_in_the_gregorian_calendar_whatever_the_user_culture()
    {
        var original = CultureInfo.CurrentCulture;
        var thai = new CultureInfo("th-TH");

        // The premise: Thai uses the Buddhist calendar by default, so 2026 formats as 2569 there.
        Assert.NotEqual(
            DateTime.UtcNow.ToString("yyyy", CultureInfo.InvariantCulture),
            DateTime.UtcNow.ToString("yyyy", thai));
        try
        {
            CultureInfo.CurrentCulture = thai;
            File.WriteAllBytes(_folder.DatabasePath, new byte[8192]);

            using var db = _folder.Open();
            db.Initialize();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        var copy = Path.GetFileName(Assert.Single(Directory.GetFiles(_folder.Root, "scribe.db.corrupt-*")));
        Assert.StartsWith("scribe.db.corrupt-" + DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture), copy);
    }

    private static long Scalar(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static void CorruptTail(string path, int pages)
    {
        const int pageSize = 4096;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var garbage = new byte[pageSize * pages];
        new Random(42).NextBytes(garbage);
        stream.Seek(-garbage.Length, SeekOrigin.End);
        stream.Write(garbage);
    }
}
