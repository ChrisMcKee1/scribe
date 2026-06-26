namespace Scribe.Core.Cleanup;

/// <summary>
/// Why a cleanup call produced the text it did. Lets the dictation pipeline tell a genuine
/// runtime failure (which should fall back to raw text and surface a visible warning) apart from
/// a deliberate skip or an already-clean transcript.
/// </summary>
public enum CleanupOutcome
{
    /// <summary>Cleanup did not run (disabled, engine not ready, or empty input). Not a failure.</summary>
    Skipped,

    /// <summary>The model ran and changed the text.</summary>
    Cleaned,

    /// <summary>The model ran and returned the text unchanged (it was already clean).</summary>
    Unchanged,

    /// <summary>
    /// The model call failed at runtime (threw, timed out, or returned nothing usable) and the
    /// pipeline fell back to the raw transcription. This is the state that drives the visible
    /// "intelligence failed" feedback and is recorded to the failure log.
    /// </summary>
    Failed,
}

/// <summary>
/// Result of a single <see cref="ITextCleanupService.CleanAsync"/> call. <see cref="Text"/> is always
/// safe to inject: on <see cref="CleanupOutcome.Failed"/> it is the original raw input, so dictation is
/// never lost. <see cref="FailureReason"/> is set on <see cref="CleanupOutcome.Failed"/> and may also be
/// set on a successful <see cref="CleanupOutcome.Cleaned"/> result to flag a *partial* failure (some
/// segments of a long, chunked dictation failed while others succeeded) — in that case the text is still
/// the best available cleaned output and no hard failure is signalled.
/// </summary>
/// <param name="Text">The text to inject (cleaned, unchanged, or the raw fallback).</param>
/// <param name="Outcome">How the call resolved.</param>
/// <param name="FailureReason">Human-readable failure detail, or <c>null</c> when fully successful.</param>
public sealed record CleanupResult(string Text, CleanupOutcome Outcome, string? FailureReason = null)
{
    /// <summary>A skip result that passes the input through untouched.</summary>
    public static CleanupResult Skip(string text) => new(text, CleanupOutcome.Skipped);

    /// <summary>True when the model ran and altered the text.</summary>
    public bool Changed => Outcome == CleanupOutcome.Cleaned;
}
