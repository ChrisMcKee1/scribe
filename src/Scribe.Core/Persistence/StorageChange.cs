namespace Scribe.Core.Persistence;

/// <summary>A committed write that moves how much space history and its audio take.</summary>
internal enum StorageChange
{
    /// <summary>A new audio blob was stored; the audio cap may now be exceeded.</summary>
    AudioStored,

    /// <summary>One history entry, and possibly its audio, was deleted.</summary>
    HistoryEntryDeleted,

    /// <summary>All history and audio were cleared; everything freed should go back to the disk.</summary>
    HistoryCleared,
}
