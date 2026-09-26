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
    /// <summary>Shown when AI cleanup did not leave a duration for this entry.</summary>
    public const string NotRecorded = "Not recorded";

    public const int RecentLimit = 200;

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

    /// <summary>AI cleanup duration for the History column, in seconds, or the not-recorded state.</summary>
    public static string CleanupTime(int? milliseconds) =>
        milliseconds is { } value && value >= 0 ? Seconds(value) : NotRecorded;

    public static string? RangeLine(int shownCount, int limit) =>
        shownCount >= limit ? $"Showing your latest {limit:N0} dictations." : null;

    public static string EmptyState(string verb, string shortcut) =>
        $"No dictations yet. {verb} {shortcut} in any app and speak.";

    public const string NoSearchMatches = "No shown dictations match your search.";

    public const string LoadingText = "Loading history...";

    public const string LoadFailedText = "Couldn't load your history.";

    public const string ClearSearch = "Clear search";

    public static HistoryToolbarState Toolbar(bool hasRows, bool hasSelection) =>
        new(CanCopy: hasSelection, CanDelete: hasSelection, CanDeleteAll: hasRows);

    /// <summary>
    /// Which parts of the History list show. A failure or a load in progress is said on a line above rows already shown
    /// (they stay as they are), and in the centred panel only when there are none; Try again goes with a failure only.
    /// </summary>
    public static HistoryLoadState LoadState(bool hasRows, bool loadFailed, bool searchActive, bool loading = false)
    {
        if (loadFailed || loading)
        {
            return new HistoryLoadState(
                ShowGrid: hasRows,
                ShowToolbar: hasRows,
                ShowCenteredStatus: !hasRows,
                ShowInlineStatus: hasRows,
                ShowSearchNoMatches: false,
                ShowRetry: loadFailed && !loading);
        }

        return new HistoryLoadState(
            ShowGrid: hasRows,
            ShowToolbar: hasRows,
            ShowCenteredStatus: !hasRows,
            ShowInlineStatus: false,
            ShowSearchNoMatches: hasRows && searchActive,
            ShowRetry: false);
    }

    public static string Details(int audioMilliseconds, int decodeMilliseconds, int? cleanupMilliseconds)
    {
        var recorded = Audio(audioMilliseconds);
        var recognized = Seconds(decodeMilliseconds);
        var cleanup = cleanupMilliseconds is { } value and >= 0
            ? $"AI cleanup took {Seconds(value)}."
            : "No AI cleanup time was recorded.";
        return $"Recorded {recorded}. Recognized in {recognized}. {cleanup}";
    }

    private static string Seconds(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "0.0 s";
        }

        var seconds = milliseconds / 1000.0;
        return seconds < 0.1 ? "0.1 s" : $"{seconds:0.0} s";
    }
}

public sealed record HistoryToolbarState(bool CanCopy, bool CanDelete, bool CanDeleteAll);

public sealed record HistoryLoadState(
    bool ShowGrid,
    bool ShowToolbar,
    bool ShowCenteredStatus,
    bool ShowInlineStatus,
    bool ShowSearchNoMatches,
    bool ShowRetry = false);

public static class HistorySettingsSummary
{
    public static string Describe(int retentionDays, bool storeAudio)
    {
        var retention = retentionDays switch
        {
            0 => "Keeps dictations until you delete them.",
            1 => "Keeps dictations for 1 day.",
            365 => "Keeps dictations for 1 year.",
            _ => $"Keeps dictations for {retentionDays:N0} days.",
        };
        var recordings = storeAudio
            ? "Saves a recording with each dictation for up to 7 days."
            : "Doesn't save recordings.";
        return $"{retention} {recordings}";
    }
}
