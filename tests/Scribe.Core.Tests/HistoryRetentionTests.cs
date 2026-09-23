using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// The retention primitives <see cref="StorageMaintenance"/> drives. The rule that runs through all
/// of them: a blob is deleted only once no surviving entry references it, and an entry that loses
/// its audio keeps its text.
/// </summary>
public class HistoryRetentionTests
{
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private static CapturedAudio Audio(int samples, float level = 0.25f) =>
        new(Enumerable.Repeat(level, samples).ToArray(), 16000);

    private static HistoryEntry Entry(string text, DateTimeOffset timestamp, long? blobId = null) =>
        new(0, timestamp, text, 1000, 50, AudioBlobId: blobId);

    // What a capture of this many samples occupies as stored: the PCM16 header, then 2 bytes each.
    private static long Stored(int samples) => AudioBlobCodec.EncodedLength(samples, AudioBlobEncoding.Pcm16);

    [Fact]
    public void New_audio_is_stored_as_pcm16_at_half_the_float_size()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new HistoryRepository(db);

        var saved = repo.Add(Entry("hello", _now), Audio(1000));

        var blob = Assert.Single(repo.ListStoredAudio());
        Assert.Equal(saved.AudioBlobId, blob.Id);
        Assert.Equal(AudioBlobCodec.Pcm16HeaderLength + (1000 * sizeof(short)), blob.Bytes);
        Assert.Equal((long)AudioBlobEncoding.Pcm16,
            DatabaseProbe.QueryInt64(db, $"SELECT encoding FROM audio_blobs WHERE id = {blob.Id};"));
        Assert.All(repo.GetAudio(blob.Id)!.Samples, s => Assert.InRange(s, 0.25f - 2e-5f, 0.25f + 2e-5f));
    }

    [Fact]
    public void Expired_audio_leaves_the_text_and_frees_only_blobs_no_newer_entry_uses()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new HistoryRepository(db);
        var old = repo.Add(Entry("old", _now.AddDays(-8)), Audio(100));
        var recent = repo.Add(Entry("recent", _now.AddDays(-6)), Audio(100));

        // An old entry sharing its recording with a new one: the new entry must keep it playable.
        var sharedOld = repo.Add(Entry("shared old", _now.AddDays(-9)), Audio(100));
        repo.Add(Entry("shared new", _now.AddDays(-1), sharedOld.AudioBlobId));

        var cleared = repo.ClearAudioOlderThan(_now.AddDays(-7));

        Assert.Equal(2, cleared);
        var entries = repo.GetRecent().ToDictionary(e => e.Text);
        Assert.Equal(4, entries.Count);
        Assert.Null(entries["old"].AudioBlobId);
        Assert.Null(entries["shared old"].AudioBlobId);
        Assert.Equal(recent.AudioBlobId, entries["recent"].AudioBlobId);
        Assert.Equal(sharedOld.AudioBlobId, entries["shared new"].AudioBlobId);

        var unreferenced = repo.ListStoredAudio().Where(b => !b.Referenced).Select(b => b.Id).ToArray();
        Assert.Equal(new[] { old.AudioBlobId!.Value }, unreferenced);

        var deleted = repo.DeleteUnreferencedAudio(unreferenced, DateTimeOffset.UtcNow.AddHours(1));

        Assert.Equal(new AudioDeletion(BlobsDeleted: 1, BytesDeleted: Stored(100), EntriesCleared: 0), deleted);
        Assert.Null(repo.GetAudio(old.AudioBlobId!.Value));
        Assert.NotNull(repo.GetAudio(sharedOld.AudioBlobId!.Value));
        Assert.NotNull(repo.GetAudio(recent.AudioBlobId!.Value));
    }

    [Fact]
    public void Unreferenced_delete_spares_young_blobs_and_rechecks_ownership()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new HistoryRepository(db);

        // Written through AddAudioBlob and not attached yet: it may belong to an entry on its way.
        var pending = repo.AddAudioBlob(Audio(10));

        // Handed over as a candidate, but referenced by the time the delete runs.
        var referenced = repo.Add(Entry("kept", _now), Audio(10)).AudioBlobId!.Value;

        var deleted = repo.DeleteUnreferencedAudio([pending, referenced], storedBeforeUtc: _now.AddHours(-1));

        Assert.Equal(0, deleted.BlobsDeleted);
        Assert.NotNull(repo.GetAudio(pending));
        Assert.NotNull(repo.GetAudio(referenced));

        // Once it is old enough the unattached one goes; the referenced one never does.
        deleted = repo.DeleteUnreferencedAudio([pending, referenced], storedBeforeUtc: DateTimeOffset.UtcNow.AddHours(2));

        Assert.Equal(1, deleted.BlobsDeleted);
        Assert.Null(repo.GetAudio(pending));
        Assert.NotNull(repo.GetAudio(referenced));
    }

    [Fact]
    public void Eviction_drops_a_shared_recording_from_every_entry_and_keeps_all_text()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new HistoryRepository(db);
        var first = repo.Add(Entry("first", _now.AddHours(-3)), Audio(500));
        repo.Add(Entry("second", _now.AddHours(-2), first.AudioBlobId));
        var third = repo.Add(Entry("third", _now.AddHours(-1)), Audio(500));

        var evicted = repo.EvictAudio([first.AudioBlobId!.Value], maxStoredBytes: 0);

        Assert.Equal(new AudioDeletion(BlobsDeleted: 1, BytesDeleted: Stored(500), EntriesCleared: 2), evicted);
        var entries = repo.GetRecent().ToDictionary(e => e.Text);
        Assert.Equal(3, entries.Count);
        Assert.Null(entries["first"].AudioBlobId);
        Assert.Null(entries["second"].AudioBlobId);
        Assert.Equal(third.AudioBlobId, entries["third"].AudioBlobId);
        Assert.Equal(new StoredAudioUsage(1, Stored(500)), repo.GetStoredAudioUsage());
    }

    [Fact]
    public void Inventory_lists_blobs_oldest_first_with_stored_sizes_and_references()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new HistoryRepository(db);
        var a = repo.Add(Entry("a", _now), Audio(3)).AudioBlobId!.Value;
        var orphan = repo.AddAudioBlob(Audio(5));
        var b = repo.Add(Entry("b", _now), Audio(7)).AudioBlobId!.Value;

        var blobs = repo.ListStoredAudio();

        Assert.Equal(
            new[]
            {
                new StoredAudioBlob(a, Stored(3), Referenced: true),
                new StoredAudioBlob(orphan, Stored(5), Referenced: false),
                new StoredAudioBlob(b, Stored(7), Referenced: true),
            },
            blobs);
        Assert.Equal(new StoredAudioUsage(3, Stored(3) + Stored(5) + Stored(7)), repo.GetStoredAudioUsage());
    }

    [Fact]
    public void Deleting_old_entries_keeps_newer_text_and_leaves_their_audio_for_the_unreferenced_pass()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new HistoryRepository(db);
        var old = repo.Add(Entry("old", _now.AddDays(-100)), Audio(4));
        repo.Add(Entry("new", _now), Audio(4));

        var removed = repo.DeleteEntriesOlderThan(_now.AddDays(-90));

        Assert.Equal(1, removed);
        Assert.Equal("new", Assert.Single(repo.GetRecent()).Text);
        Assert.Contains(repo.ListStoredAudio(), b => b.Id == old.AudioBlobId && !b.Referenced);
    }

    [Fact]
    public void A_capture_too_large_for_the_audio_cap_is_saved_as_text_only()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new HistoryRepository(db) { MaxAudioBlobBytes = Stored(5) };

        var saved = repo.Add(Entry("long dictation", _now), Audio(6));

        Assert.Null(saved.AudioBlobId);
        Assert.Equal("long dictation", Assert.Single(repo.GetRecent()).Text);
        Assert.Empty(repo.ListStoredAudio());
        Assert.Throws<ArgumentOutOfRangeException>(() => repo.AddAudioBlob(Audio(6)));

        // Exactly at the limit still fits.
        Assert.NotNull(repo.Add(Entry("fits", _now), Audio(5)).AudioBlobId);
    }

    [Fact]
    public void Writes_that_change_storage_signal_after_they_commit_and_housekeeping_stays_silent()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new HistoryRepository(db);
        var seen = new List<(StorageChange Change, int VisibleEntries)>();

        // Reading inside the handler proves the write had already committed when it was raised.
        db.StorageChanged += change => seen.Add((change, repo.GetRecent().Count));

        var withAudio = repo.Add(Entry("with audio", _now), Audio(4));
        repo.Add(Entry("text only", _now));
        repo.ClearAudioOlderThan(_now.AddDays(1));
        repo.EvictAudio([withAudio.AudioBlobId!.Value], maxStoredBytes: 0);
        repo.DeleteEntriesOlderThan(_now.AddDays(-30));
        repo.Delete(withAudio.Id);
        repo.Delete(12345);
        repo.Clear();

        Assert.Equal(
            new[]
            {
                (StorageChange.AudioStored, 1),
                (StorageChange.HistoryEntryDeleted, 1),
                (StorageChange.HistoryCleared, 0),
            },
            seen);
    }

    [Fact]
    public void A_throwing_change_handler_never_fails_the_write_or_starves_other_handlers()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repo = new HistoryRepository(db);
        var reached = false;
        db.StorageChanged += _ => throw new InvalidOperationException("subscriber bug");
        db.StorageChanged += _ => reached = true;

        var saved = repo.Add(Entry("x", _now), Audio(2));

        Assert.NotNull(saved.AudioBlobId);
        Assert.True(reached);
    }
}
