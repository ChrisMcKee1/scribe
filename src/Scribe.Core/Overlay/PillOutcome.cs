using System.Globalization;
using Scribe.Core.Cleanup;
using Scribe.Core.TextInjection;

namespace Scribe.Core.Overlay;

/// <summary>What the recording pill says about a finished dictation.</summary>
public enum PillOutcomeKind
{
    /// <summary>
    /// All of the text reached the target, the space after the dictation included, and AI cleanup ran on it or was not
    /// asked for. A check and "Typed", briefly.
    /// </summary>
    Typed,

    /// <summary>
    /// All of the text reached the target, but AI cleanup was asked for and did not clean it: it failed, or it was on and
    /// not ready. The text went in as recognized. A caution triangle, "Typed without AI cleanup" and the reason.
    /// </summary>
    TypedWithoutCleanup,

    /// <summary>Nothing reached the target. An error icon, "Nothing typed" and the next step.</summary>
    NothingTyped,

    /// <summary>Some of the text reached the target and the rest did not. An error icon, "Not all of it was typed" and the next step.</summary>
    PartlyTyped,
}

/// <summary>
/// What the recording pill shows for a finished dictation, decided from what the pipeline produced (<see cref="Of"/>).
/// The controller hands it on with the dictation's return to idle, so it rides that change's presentation revision and
/// a late outcome can never cover a newer recording.
/// </summary>
/// <remarks>
/// The truth rules of the palette decision: "Typed" and "Typed without AI cleanup" appear only when the whole insertion
/// succeeded, the space after the dictation included, and a partial insertion, or a dictation left for the recovery copy,
/// is the error state and never a check. <see cref="Detail"/> is shown on the pill; it can name a microphone, so the
/// overlay logs only its length.
/// </remarks>
public sealed record PillOutcome
{
    /// <summary>
    /// The next step after an insertion that failed: the dictation was kept for recovery before anything was typed
    /// (<see cref="DictationInsertion"/>), and the tray's Copy last dictation gives it back.
    /// </summary>
    public const string RecoveryStep = "Copy it from the tray menu";

    /// <summary>The reason shown when AI cleanup did not clean the text and gave no reason of its own.</summary>
    public const string CleanupDidNotRun = "AI cleanup did not run.";

    private PillOutcome(PillOutcomeKind kind, string detail)
    {
        Kind = kind;
        Detail = detail;
    }

    /// <summary>Which state the pill shows.</summary>
    public PillOutcomeKind Kind { get; }

    /// <summary>
    /// The second line, on one line: the reason for <see cref="PillOutcomeKind.TypedWithoutCleanup"/> (the cleanup's
    /// diagnostics-safe form, never its display detail), the next step for the error states, empty for
    /// <see cref="PillOutcomeKind.Typed"/>.
    /// </summary>
    public string Detail { get; }

    /// <summary>How long the pill shows the outcome before it hides.</summary>
    public TimeSpan Hold => Kind == PillOutcomeKind.Typed ? PillTiming.TypedHold : PillTiming.NoticeHold;

    /// <summary>How long the outcome keeps the pill on screen, its fade out included: what the helper's lifetime keeps it for.</summary>
    public TimeSpan OnScreen => Hold + PillTiming.FadeOut;

    /// <summary>
    /// What the pill shows for a dictation that finished, or <c>null</c> when it has nothing to say: the dictation was
    /// discarded quietly (no speech was heard, or the dictionary left nothing to type), so the pill just hides.
    /// </summary>
    /// <param name="insertion">How typing into the target went, or <c>null</c> when the dictation ended before insertion.</param>
    /// <param name="cleanupRequested">Whether the capture asked for AI cleanup, from its own settings (the dictation-only hotkey turns it off).</param>
    /// <param name="cleanup">What AI cleanup returned, or <c>null</c> when it never ran.</param>
    /// <param name="failure">
    /// The message the dictation raised when it ended without inserting anything (it carries its own next step), or
    /// <c>null</c>. Only consulted when there was no insertion: whatever was reported, the insertion says what arrived.
    /// </param>
    public static PillOutcome? Of(InjectionResult? insertion, bool cleanupRequested, CleanupResult? cleanup, string? failure)
    {
        if (insertion is not null)
        {
            return insertion switch
            {
                // Nothing was given to type, so nothing can be claimed about it.
                { Total: 0 } => null,

                // The injector reports success only when all of what it was given arrived, and it was given the space too.
                { Succeeded: true } => cleanupRequested && MissingCleanupReason(cleanup) is { } reason
                    ? new PillOutcome(PillOutcomeKind.TypedWithoutCleanup, reason)
                    : new PillOutcome(PillOutcomeKind.Typed, string.Empty),

                // Sent counts what the target accepted before the insertion stopped.
                { Sent: > 0 } => new PillOutcome(PillOutcomeKind.PartlyTyped, RecoveryStep),
                _ => new PillOutcome(PillOutcomeKind.NothingTyped, RecoveryStep),
            };
        }

        var step = OneLine(failure);
        return step.Length == 0 ? null : new PillOutcome(PillOutcomeKind.NothingTyped, SentenceCase(step));
    }

    // Why the text went in without AI cleanup that was asked for, or null when cleanup ran (a partly degraded result still
    // cleaned the text) or skipped on purpose (nothing to clean, or nothing handed over for a withdrawn library scope).
    private static string? MissingCleanupReason(CleanupResult? cleanup) => cleanup switch
    {
        { Outcome: CleanupOutcome.Failed } => ReasonOrFallback(cleanup.FailureReason),
        { SkippedUnexpectedly: true } => ReasonOrFallback(cleanup.SkipReason),
        _ => null,
    };

    private static string ReasonOrFallback(string? reason)
    {
        var line = OneLine(reason);
        return line.Length == 0 ? CleanupDidNotRun : line;
    }

    // The pipe carries one line per command, and the pill shows one line of detail.
    private static string OneLine(string? text) =>
        (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

    // The failure messages are written to follow "Scribe: " in the tray's tooltip; on the pill they start the line.
    private static string SentenceCase(string text) =>
        char.IsLower(text[0]) ? char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..] : text;
}
