namespace Scribe.Overlay;

/// <summary>
/// The states the recording pill can show. The first three follow the dictation; the outcomes say what a finished
/// dictation did, the way <c>Scribe.Core.Overlay.PillOutcomeKind</c> decides it (same names; the overlay deliberately
/// has no Scribe.Core reference, and a test keeps the two in step).
/// </summary>
public enum OverlayState
{
    /// <summary>Hidden / parked (no pill visible).</summary>
    Hidden,

    /// <summary>Capturing microphone input: the listening edge and five live level bars.</summary>
    Listening,

    /// <summary>Transcribing or cleaning up: three dots; the words say which.</summary>
    Processing,

    /// <summary>All of the text was typed, the space after it included: a check and "Typed", briefly.</summary>
    Typed,

    /// <summary>All of the text was typed, but AI cleanup was asked for and did not clean it: a caution triangle and the reason.</summary>
    TypedWithoutCleanup,

    /// <summary>Nothing was typed: an error icon, the error edge and the next step.</summary>
    NothingTyped,

    /// <summary>Part of the text was typed: an error icon, the error edge and the next step.</summary>
    PartlyTyped,

    /// <summary>
    /// The AI cleanup failure flash the outcomes replace, drawn like an error ("Intelligence failed" and the reason). Only
    /// the app shell's <c>ShowFailed</c> still sends it; it goes once the shell shows outcomes.
    /// </summary>
    Failed,
}
