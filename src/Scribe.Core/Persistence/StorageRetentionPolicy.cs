namespace Scribe.Core.Persistence;

/// <summary>
/// The fixed limits on what Scribe keeps on disk beside the user's own history setting, and the
/// Settings copy that describes them. One place for the numbers, so the text a user reads can never
/// disagree with what <see cref="StorageMaintenance"/> actually does.
/// </summary>
/// <remarks>
/// These are policy, not settings: the stored audio copy is a diagnostic aid rather than a record
/// the user curates, and at about 1.9 MB per dictated minute (16-bit PCM at 16 kHz) it is the one
/// thing in <c>scribe.db</c> that can grow into gigabytes. Text history keeps its own user setting,
/// <see cref="Models.AppSettings.HistoryRetentionDays"/>.
/// </remarks>
public static class StorageRetentionPolicy
{
    /// <summary>Days a stored audio copy is kept. The history entry's text outlives it.</summary>
    public const int AudioRetentionDays = 7;

    /// <summary>Ceiling on all stored audio together, in binary megabytes as Windows displays them.</summary>
    public const int MaxStoredAudioMegabytes = 250;

    /// <summary><see cref="MaxStoredAudioMegabytes"/> in bytes, measured as stored blob length.</summary>
    public const long MaxStoredAudioBytes = MaxStoredAudioMegabytes * 1024L * 1024L;

    /// <summary>Days an AI cleanup failure sample is kept, whatever later cleanups do.</summary>
    public const int CleanupFailureRetentionDays = 7;

    /// <summary>
    /// Days an older damaged-database copy left by a startup repair is kept after this build first
    /// sees it. The newest copy is kept indefinitely, whatever its age, because it can be the only
    /// recovery point.
    /// </summary>
    public const int DamagedCopyRetentionDays = 14;

    public static TimeSpan AudioRetention => TimeSpan.FromDays(AudioRetentionDays);

    public static TimeSpan CleanupFailureRetention => TimeSpan.FromDays(CleanupFailureRetentionDays);

    public static TimeSpan DamagedCopyRetention => TimeSpan.FromDays(DamagedCopyRetentionDays);

    /// <summary>Settings hint under "Keep audio with history".</summary>
    public static string StoredAudioHint { get; } =
        "Keeps the audio of each dictation on this PC for up to " +
        $"{AudioRetentionDays} days ({MaxStoredAudioMegabytes} MB in total), then deletes it. " +
        "Scribe doesn't play recordings back.";

    /// <summary>Settings hint under "Keep dictation history for".</summary>
    public static string TextRetentionHint { get; } =
        "Older dictations are deleted automatically.";

    /// <summary>About page hint under "Scribe data file".</summary>
    public static string DataFileHint { get; } =
        "One file holds your history, dictionary, snippets, profiles, settings and any saved recordings. " +
        $"Recordings are removed after {AudioRetentionDays} days, sooner once they pass " +
        $"{MaxStoredAudioMegabytes} MB, or when you delete their entry.";
}
