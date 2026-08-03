namespace Scribe.Core.Diagnostics;

/// <summary>
/// Formats the derived columns of the history grid. Pure and UI-free so the rules below are
/// testable: the settings window's row type is a thin adapter over this.
/// </summary>
/// <remarks>
/// The history grid exists to answer "where did the time go?", so decode and AI cleanup are both
/// rendered in milliseconds even when cleanup runs into several seconds. Switching the larger of
/// the two to seconds would make the columns prettier and the comparison harder, which is the wrong
/// trade for a diagnostics view.
/// </remarks>
public static class HistoryRowFormat
{
    /// <summary>Shown when a value does not apply, matching the target-app column's convention.</summary>
    public const string NotApplicable = "n/a";

    /// <summary>
    /// Spoken length, in seconds to one decimal. Sub-100 ms clips would render as "0.0 s", so they
    /// round up to the smallest value that still reads as a real recording.
    /// </summary>
    public static string Audio(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            return NotApplicable;
        }

        var seconds = milliseconds / 1000.0;
        return seconds < 0.1 ? "0.1 s" : $"{seconds:0.0} s";
    }

    /// <summary>
    /// A pipeline stage's duration in milliseconds, thousands-separated so a four-digit cloud
    /// round-trip stays readable next to a three-digit local decode.
    /// </summary>
    /// <param name="milliseconds">
    /// Null when the stage did not run. AI cleanup is the case that matters: it is null when cleanup
    /// was switched off for that dictation and when it failed, and both are worth seeing in the grid
    /// rather than hiding behind a blank cell.
    /// </param>
    public static string Latency(int? milliseconds) =>
        milliseconds is { } value && value >= 0 ? $"{value:N0} ms" : NotApplicable;
}
