namespace Scribe.Core.Persistence;

/// <summary>
/// Retention primitives over dictation history and its stored audio, driven by
/// <see cref="StorageMaintenance"/>. Kept apart from <see cref="IHistoryRepository"/> so anything
/// that wraps the everyday read and write surface does not also have to implement housekeeping.
/// </summary>
/// <remarks>
/// Every member that writes is one short transaction that takes the database write gate, so
/// maintenance can batch large deletions and let ordinary writes in between. None of them reads
/// audio content: the age of an entry comes from <c>history</c>, and sizes come from
/// <c>length(samples)</c>, which SQLite answers from the record header without loading the blob.
/// </remarks>
public interface IHistoryMaintenance
{
    /// <summary>Deletes history entries older than the cutoff; returns entries deleted.</summary>
    /// <remarks>Their audio becomes unreferenced and is left for <see cref="DeleteUnreferencedAudio"/>.</remarks>
    int DeleteEntriesOlderThan(DateTimeOffset cutoffUtc);

    /// <summary>
    /// Drops the audio reference from entries older than the cutoff, keeping their text; returns
    /// entries changed. A blob another, newer entry still references stays with that entry.
    /// </summary>
    int ClearAudioOlderThan(DateTimeOffset cutoffUtc);

    /// <summary>Every stored blob, oldest first, with its stored size and whether any entry references it.</summary>
    IReadOnlyList<StoredAudioBlob> ListStoredAudio();

    /// <summary>
    /// Deletes those of <paramref name="blobIds"/> that no entry references and that were stored
    /// before <paramref name="storedBeforeUtc"/>. The age floor protects a blob written through
    /// <see cref="IHistoryRepository.AddAudioBlob"/> whose entry has not been added yet.
    /// </summary>
    AudioDeletion DeleteUnreferencedAudio(IReadOnlyCollection<long> blobIds, DateTimeOffset storedBeforeUtc);

    /// <summary>
    /// Removes the given blobs, in order, from every entry that references them, keeping the entries'
    /// text, then deletes them, stopping as soon as the total stored is at or under
    /// <paramref name="maxStoredBytes"/>. The total is re-read inside the delete's own transaction,
    /// so audio a user deleted after the list was made never causes extra eviction.
    /// </summary>
    AudioDeletion EvictAudio(IReadOnlyCollection<long> blobIds, long maxStoredBytes);

    /// <summary>Totals for all stored audio.</summary>
    StoredAudioUsage GetStoredAudioUsage();

    /// <summary>
    /// Dictation activity is starting: storage maintenance must get out of the way now. A running
    /// VACUUM or reclamation step is interrupted (SQLite rolls it back), a batch of deletions ends
    /// at its next step, and heavy work resumes only once the app is idle again, with a backoff.
    /// Never blocks. The app raises it when a dictation is activated; every history write raises it
    /// on arrival, so callers of <see cref="IHistoryRepository.Add(Models.HistoryEntry, Models.CapturedAudio?)"/>
    /// need not.
    /// </summary>
    void RequestYield();
}

/// <summary>One stored audio blob as retention sees it.</summary>
public readonly record struct StoredAudioBlob(long Id, long Bytes, bool Referenced);

/// <summary>What a retention delete removed.</summary>
public readonly record struct AudioDeletion(int BlobsDeleted, long BytesDeleted, int EntriesCleared)
{
    public static AudioDeletion operator +(AudioDeletion left, AudioDeletion right) =>
        new(left.BlobsDeleted + right.BlobsDeleted,
            left.BytesDeleted + right.BytesDeleted,
            left.EntriesCleared + right.EntriesCleared);
}

/// <summary>How much audio is stored.</summary>
public readonly record struct StoredAudioUsage(int Blobs, long Bytes);
