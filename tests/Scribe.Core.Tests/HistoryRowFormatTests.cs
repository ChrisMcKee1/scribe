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

    /// <summary>Pins the thread culture for one assertion and restores it afterwards.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) =>
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
