namespace Scribe.Core.Diagnostics;

/// <summary>
/// Flags a decode that returned far less text than its voiced audio can account for.
///
/// The recogniser has two degenerate modes on the same underlying problem. It either returns
/// nothing at all, which the pipeline treats as a failure, or it returns a single filler token. In
/// production the second mode produced the literal transcript "Yeah." fifteen times, including for
/// a 6.7 second capture and a 4.5 second one. Those sail through as success and get typed into the
/// user's document, so the only signal is the user noticing that a paragraph they just spoke became
/// one word.
///
/// The measure is characters per second of VOICED audio, summed across VAD speech segments. It
/// must not be measured against the trimmed capture, which spans the first word to the last and so
/// includes every thinking pause: a genuine "Yeah." followed by a long pause would otherwise look
/// identical to a collapse.
///
/// This is diagnostic only. The threshold was chosen from unlabelled production history using the
/// same ratio it tests, which cannot by itself separate a collapsed decode from a genuinely terse
/// speaker, so a positive result is logged for later analysis and never shown to the user or used
/// to discard text. Raising it, or surfacing it, needs captures labelled by listening to the
/// retained audio first.
/// </summary>
public static class TerseDecodeDetector
{
    /// <summary>
    /// Captures with less voiced audio than this are exempt. A genuine one-word answer is common
    /// and completely legitimate, and at these durations the ratio is too noisy to mean anything.
    /// </summary>
    public const double MinimumSpeechSeconds = 1.5;

    /// <summary>
    /// Characters per second of voiced audio below which a decode is recorded as suspect. Healthy
    /// dictation measured no lower than 11 across 85 real captures; the observed collapses
    /// measured no higher than 3.1. The threshold sits in that gap, nearer the collapses, because
    /// a missed collapse only costs a log line while a false positive would pollute the signal.
    /// </summary>
    public const double MinimumCharsPerSpeechSecond = 4.0;

    /// <summary>
    /// True when <paramref name="text"/> is implausibly short for <paramref name="speechSeconds"/>
    /// of voiced audio. False for anything short, empty, or comfortably within normal range.
    /// </summary>
    public static bool IsSuspiciouslyTerse(string? text, double speechSeconds)
    {
        // An empty decode is a different failure with its own handling; do not double-report it.
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (speechSeconds < MinimumSpeechSeconds) return false;
        if (double.IsNaN(speechSeconds) || double.IsInfinity(speechSeconds)) return false;

        return text.Trim().Length / speechSeconds < MinimumCharsPerSpeechSecond;
    }
}
