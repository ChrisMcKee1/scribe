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
    string? TranscriptionModelId = null,
    AiRating AiRating = AiRating.Unrated);

/// <summary>
/// What the user said about an AI-cleaned result. Deliberately about the OUTPUT and not about
/// Scribe: Microsoft's Store policy requires that any in-app rating of the APP route to the Store's
/// own rating mechanism regardless of sentiment, and treats a private thumbs-down path paired with
/// a public thumbs-up path as a fraudulent practice. Keeping this scoped to one result is what
/// makes it a quality signal rather than a rating funnel.
/// </summary>
public enum AiRating
{
    /// <summary>No opinion recorded, which is true of almost every row.</summary>
    Unrated = 0,

    /// <summary>The result was useful.</summary>
    Useful = 1,

    /// <summary>The result was not useful. This is the state that offers the report path.</summary>
    NotUseful = -1,
}
