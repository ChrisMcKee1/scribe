using System.Globalization;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Tests;

/// <summary>
/// The history grid's derived columns. These are the numbers a user reads when they want to know
/// why a dictation felt slow, so "blank", "zero" and "did not run" have to stay distinguishable.
/// </summary>
public class HistoryRowFormatTests
{
    [Fact]
    public void Cleanup_that_did_not_run_reads_as_not_applicable_rather_than_zero()
    {
        // Null means AI cleanup was off or failed for that dictation. Rendering it as "0 ms" would
        // claim the step ran instantly, which is the opposite of what happened.
        Assert.Equal(HistoryRowFormat.NotApplicable, HistoryRowFormat.Latency(null));
    }

    [Fact]
    public void A_stage_that_really_took_no_measurable_time_still_reports_a_number()
    {
        Assert.Equal("0 ms", HistoryRowFormat.Latency(0));
    }

    [Fact]
    public void A_negative_duration_is_treated_as_missing()
    {
        // Nothing produces this today; a clock adjustment mid-request is the plausible source. A
        // dash is honest, "-4 ms" invites a bug report.
        Assert.Equal(HistoryRowFormat.NotApplicable, HistoryRowFormat.Latency(-4));
    }

    [Fact]
    public void Cleanup_column_uses_seconds_or_not_recorded()
    {
        using var _ = new CultureScope("en-US");

        Assert.Equal("0.9 s", HistoryRowFormat.CleanupTime(900));
        Assert.Equal("Not recorded", HistoryRowFormat.CleanupTime(null));
    }

    [Fact]
    public void Range_line_appears_only_at_the_recent_limit()
    {
        Assert.Null(HistoryRowFormat.RangeLine(199, 200));
        Assert.Equal("Showing your latest 200 dictations.", HistoryRowFormat.RangeLine(200, 200));
    }

    [Fact]
    public void Empty_state_uses_the_current_shortcut_action()
    {
        Assert.Equal(
            "No dictations yet. Hold Page Down in any app and speak.",
            HistoryRowFormat.EmptyState("Hold", "Page Down"));
        Assert.Equal(
            "No dictations yet. Press Page Down in any app and speak.",
            HistoryRowFormat.EmptyState("Press", "Page Down"));
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, true, true, true, true)]
    public void Toolbar_state_depends_on_rows_and_selection(
        bool hasRows,
        bool hasSelection,
        bool copy,
        bool delete,
        bool deleteAll)
    {
        Assert.Equal(new HistoryToolbarState(copy, delete, deleteAll), HistoryRowFormat.Toolbar(hasRows, hasSelection));
    }

    [Fact]
    public void Load_state_places_failures_without_covering_rows()
    {
        Assert.Equal(
            new HistoryLoadState(ShowGrid: false, ShowToolbar: false, ShowCenteredStatus: true, ShowInlineStatus: false, ShowSearchNoMatches: false, ShowRetry: true),
            HistoryRowFormat.LoadState(hasRows: false, loadFailed: true, searchActive: false));
        Assert.Equal(
            new HistoryLoadState(ShowGrid: true, ShowToolbar: true, ShowCenteredStatus: false, ShowInlineStatus: true, ShowSearchNoMatches: false, ShowRetry: true),
            HistoryRowFormat.LoadState(hasRows: true, loadFailed: true, searchActive: false));
        Assert.Equal(
            new HistoryLoadState(ShowGrid: true, ShowToolbar: true, ShowCenteredStatus: false, ShowInlineStatus: false, ShowSearchNoMatches: false),
            HistoryRowFormat.LoadState(hasRows: true, loadFailed: false, searchActive: false));
        Assert.Equal(
            new HistoryLoadState(ShowGrid: true, ShowToolbar: true, ShowCenteredStatus: false, ShowInlineStatus: false, ShowSearchNoMatches: true),
            HistoryRowFormat.LoadState(hasRows: true, loadFailed: false, searchActive: true));
    }

    [Fact]
    public void A_retry_says_it_is_loading_where_the_failure_was_and_never_over_the_rows()
    {
        // Review of a1e3867, item 5: Try again with rows shown drew "Loading history..." in the centred panel over them.
        Assert.Equal(
            new HistoryLoadState(ShowGrid: true, ShowToolbar: true, ShowCenteredStatus: false, ShowInlineStatus: true, ShowSearchNoMatches: false, ShowRetry: false),
            HistoryRowFormat.LoadState(hasRows: true, loadFailed: false, searchActive: false, loading: true));
        Assert.Equal(
            new HistoryLoadState(ShowGrid: false, ShowToolbar: false, ShowCenteredStatus: true, ShowInlineStatus: false, ShowSearchNoMatches: false, ShowRetry: false),
            HistoryRowFormat.LoadState(hasRows: false, loadFailed: false, searchActive: false, loading: true));
        Assert.Equal("Loading history...", HistoryRowFormat.LoadingText);
        Assert.Equal("Couldn't load your history.", HistoryRowFormat.LoadFailedText);
    }

    [Theory]
    [InlineData(90, false, "Keeps dictations for 90 days. Doesn't save recordings.")]
    [InlineData(1, false, "Keeps dictations for 1 day. Doesn't save recordings.")]
    [InlineData(365, true, "Keeps dictations for 1 year. Saves a recording with each dictation for up to 7 days.")]
    [InlineData(0, true, "Keeps dictations until you delete them. Saves a recording with each dictation for up to 7 days.")]
    public void History_settings_summary_names_retention_and_recordings(int days, bool recordings, string expected) =>
        Assert.Equal(expected, HistorySettingsSummary.Describe(days, recordings));

    [Theory]
    [InlineData(412, "412 ms")]
    [InlineData(3412, "3,412 ms")]
    [InlineData(120_000, "120,000 ms")]
    public void Durations_are_grouped_so_a_cloud_round_trip_stays_readable(int ms, string expected)
    {
        // Both decode and cleanup stay in milliseconds. Promoting the larger one to seconds would
        // read better in isolation and defeat the column's only purpose, which is comparing them.
        using var _ = new CultureScope("en-US");
        Assert.Equal(expected, HistoryRowFormat.Latency(ms));
    }

    [Fact]
    public void Durations_follow_the_users_number_format()
    {
        // A German user reads "3.412" as three thousand, so grouping must be culture-aware rather
        // than pinned to invariant. This also pins down that the test above is culture-scoped, not
        // accidentally passing because the agent's machine happens to be en-US.
        using var _ = new CultureScope("de-DE");
        Assert.Equal("3.412 ms", HistoryRowFormat.Latency(3412));
    }

    [Theory]
    [InlineData(1200, "1.2 s")]
    [InlineData(45_600, "45.6 s")]
    public void Audio_length_reads_in_seconds(int ms, string expected)
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal(expected, HistoryRowFormat.Audio(ms));
    }

    [Fact]
    public void A_very_short_clip_does_not_render_as_zero_seconds()
    {
        // 40 ms of audio is a real recording that produced a real row. "0.0 s" reads as a bug.
        using var _ = new CultureScope("en-US");
        Assert.Equal("0.1 s", HistoryRowFormat.Audio(40));
    }

    [Fact]
    public void No_audio_reads_as_not_applicable()
    {
        Assert.Equal(HistoryRowFormat.NotApplicable, HistoryRowFormat.Audio(0));
    }

    [Fact]
    public void Details_line_reports_the_recorded_cleanup_time()
    {
        using var _ = new CultureScope("en-US");

        Assert.Equal(
            "Recorded 12.0 s. Recognized in 0.4 s. AI cleanup took 0.9 s.",
            HistoryRowFormat.Details(12_000, 400, 900));
    }

    [Fact]
    public void Details_line_says_when_cleanup_time_was_not_recorded()
    {
        using var _ = new CultureScope("en-US");

        Assert.Equal(
            "Recorded 12.0 s. Recognized in 0.4 s. No AI cleanup time was recorded.",
            HistoryRowFormat.Details(12_000, 400, null));
    }

    /// <summary>Pins the thread culture for one assertion and restores it afterwards.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) =>
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
