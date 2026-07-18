namespace Scribe.Core.Models;

/// <summary>A recorded dictation, stored for review and (later) auto-learning.</summary>
public sealed record HistoryEntry(
    long Id,
    DateTimeOffset TimestampUtc,
    string Text,
    int AudioMilliseconds,
    int DecodeMilliseconds,
    int? CleanupMilliseconds = null,
    string? TargetApp = null,
    long? AudioBlobId = null,
    string? TranscriptionModelId = null);
