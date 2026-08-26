namespace Scribe.Core.Cleanup;

using Scribe.Core.TextActions;

/// <summary>Why a text action did not produce usable text.</summary>
public enum TextActionFailure
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>Nothing was selected, or the selection was only whitespace.</summary>
    EmptySelection,

    /// <summary>AI cleanup is switched off in settings.</summary>
    NotEnabled,

    /// <summary>A model is configured but is not serving requests yet (loading, downloading, failed).</summary>
    NotReady,

    /// <summary>The selection is larger than one request can carry.</summary>
    TooLarge,

    /// <summary>The model call threw, timed out, or the endpoint rejected it.</summary>
    CallFailed,

    /// <summary>The model answered, but the answer failed the action's safety contract.</summary>
    Rejected,

    /// <summary>The user moved on before the answer came back.</summary>
    Cancelled,
}

/// <summary>
/// Outcome of one text action.
/// </summary>
/// <remarks>
/// Unlike <see cref="CleanupResult"/> this deliberately has no "degrade to the input" path. Cleanup
/// falls back to raw text because the alternative is losing a dictation. Here the user's text already
/// exists on screen, so the safe failure is to change nothing at all, and
/// <see cref="Text"/> is null on any failure so a caller cannot accidentally write something back.
/// </remarks>
/// <param name="Text">The transformed text on success, otherwise null.</param>
/// <param name="Failure">Why it failed, or <see cref="TextActionFailure.None"/>.</param>
/// <param name="Detail">A human-readable sentence for the palette to display. Empty on success.</param>
/// <param name="Rejection">
/// When <paramref name="Failure"/> is <see cref="TextActionFailure.Rejected"/>, which contract the
/// answer broke.
/// </param>
public sealed record TextActionResult(
    string? Text,
    TextActionFailure Failure,
    string Detail,
    TextActionSanitizer.RejectionReason Rejection = TextActionSanitizer.RejectionReason.None)
{
    /// <summary>True when <see cref="Text"/> holds something safe to write back.</summary>
    public bool Succeeded => Failure == TextActionFailure.None && !string.IsNullOrEmpty(Text);

    /// <summary>A successful transformation.</summary>
    public static TextActionResult Success(string text) =>
        new(text, TextActionFailure.None, string.Empty);

    /// <summary>A failure that leaves the user's text untouched.</summary>
    public static TextActionResult Failed(TextActionFailure failure, string detail) =>
        new(null, failure, detail);
}
