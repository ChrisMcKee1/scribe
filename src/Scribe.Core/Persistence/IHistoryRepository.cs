using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <summary>Stores and retrieves dictation history and (optionally) the captured audio.</summary>
public interface IHistoryRepository
{
    /// <summary>Inserts a history row and returns it with its assigned id.</summary>
    HistoryEntry Add(HistoryEntry entry);

    /// <summary>Atomically inserts a history row and its optional captured audio.</summary>
    HistoryEntry Add(HistoryEntry entry, CapturedAudio? audio);

    /// <summary>Stores raw capture samples and returns the new blob id.</summary>
    long AddAudioBlob(CapturedAudio audio);

    /// <summary>Returns the most recent entries, newest first, up to <paramref name="limit"/>.</summary>
    IReadOnlyList<HistoryEntry> GetRecent(int limit = 100);

    /// <summary>Loads a stored audio blob, or <see langword="null"/> when it no longer exists.</summary>
    CapturedAudio? GetAudio(long blobId);

    /// <summary>Deletes a history row and its uniquely owned audio blob.</summary>
    void Delete(long id);

    /// <summary>Removes all history and stored audio.</summary>
    void Clear();
}
