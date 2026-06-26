namespace Scribe.Core.Models;

/// <summary>
/// A recorded runtime failure of the AI cleanup stage. Persisted so the user can see, in Settings,
/// when intelligence fell back to raw transcription and why. Entries are pruned to a rolling
/// one-week window on each successful cleanup and at app startup, so the log never grows unbounded.
/// </summary>
public sealed record CleanupFailure(
    long Id,
    DateTimeOffset TimestampUtc,
    string? Provider,
    string? Model,
    string Reason,
    string? Sample)
{
    /// <summary>A not-yet-persisted failure (Id 0) stamped at the current UTC time.</summary>
    public static CleanupFailure New(
        string reason, string? provider = null, string? model = null, string? sample = null) =>
        new(0, DateTimeOffset.UtcNow, provider, model, reason, sample);
}
