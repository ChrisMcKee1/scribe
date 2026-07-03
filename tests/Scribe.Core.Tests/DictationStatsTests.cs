using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class DictationStatsTests
{
    private static HistoryEntry Entry(int audioMs, int decodeMs, double ageHours = 1) =>
        new(0, DateTimeOffset.UtcNow.AddHours(-ageHours), "text", audioMs, decodeMs);

    [Fact]
    public void Percentile_interpolates_between_samples()
    {
        double[] sorted = [100, 200, 300, 400];

        Assert.Equal(250, DictationStats.Percentile(sorted, 0.50));
        Assert.Equal(100, DictationStats.Percentile(sorted, 0.0));
        Assert.Equal(400, DictationStats.Percentile(sorted, 1.0));
        Assert.Equal(385, DictationStats.Percentile(sorted, 0.95), precision: 6);
    }

    [Fact]
    public void Percentile_of_a_single_sample_is_that_sample()
    {
        Assert.Equal(42, DictationStats.Percentile([42.0], 0.95));
    }

    [Fact]
    public void Compute_aggregates_decode_and_rtf()
    {
        var entries = new[]
        {
            Entry(audioMs: 10_000, decodeMs: 1_000),  // RTF 0.10
            Entry(audioMs: 20_000, decodeMs: 3_000),  // RTF 0.15
            Entry(audioMs: 5_000, decodeMs: 1_000),   // RTF 0.20
        };

        var stats = DictationStats.Compute(entries, DateTimeOffset.UtcNow.AddDays(-7));

        Assert.NotNull(stats);
        Assert.Equal(3, stats!.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(35_000), stats.TotalAudio);
        Assert.Equal(1_000, stats.DecodeP50Ms);
        Assert.Equal(0.15, stats.RtfP50, precision: 6);
        Assert.Equal(20, stats.LongestAudioSeconds);
    }

    [Fact]
    public void Compute_excludes_entries_outside_the_window_and_zero_length_audio()
    {
        var entries = new[]
        {
            Entry(audioMs: 10_000, decodeMs: 1_000, ageHours: 1),
            Entry(audioMs: 10_000, decodeMs: 9_999, ageHours: 24 * 30), // too old
            Entry(audioMs: 0, decodeMs: 50),                            // undefined RTF
        };

        var stats = DictationStats.Compute(entries, DateTimeOffset.UtcNow.AddDays(-7));

        Assert.NotNull(stats);
        Assert.Equal(1, stats!.Count);
        Assert.Equal(1_000, stats.DecodeP95Ms);
    }

    [Fact]
    public void Compute_returns_null_when_nothing_qualifies()
    {
        Assert.Null(DictationStats.Compute([], DateTimeOffset.UtcNow.AddDays(-7)));
        Assert.Null(DictationStats.Compute(
            [Entry(audioMs: 0, decodeMs: 10)], DateTimeOffset.UtcNow.AddDays(-7)));
    }
}
