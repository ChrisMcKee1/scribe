using Scribe.Core.Diagnostics;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class UsageTrendNormalizerTests
{
    [Fact]
    public void Normalize_returns_empty_for_empty_input()
    {
        var normalized = UsageTrendNormalizer.Normalize([]);

        Assert.Empty(normalized);
    }

    [Fact]
    public void Normalize_returns_zero_heights_when_all_buckets_are_zero()
    {
        UsageAnalyzer.TrendPoint[] trend =
        [
            new(new DateOnly(2026, 7, 1), 0, 0),
            new(new DateOnly(2026, 7, 2), 0, 0),
        ];

        var normalized = UsageTrendNormalizer.Normalize(trend);

        Assert.Equal(2, normalized.Count);
        Assert.All(normalized, point => Assert.Equal(0, point.RelativeHeight));
        Assert.Equal(trend, normalized.Select(point => point.Trend));
    }

    [Fact]
    public void Normalize_scales_dictations_relative_to_the_largest_bucket()
    {
        var normalized = UsageTrendNormalizer.Normalize(
        [
            new(new DateOnly(2026, 7, 1), 1, 10),
            new(new DateOnly(2026, 7, 2), 2, 20),
            new(new DateOnly(2026, 7, 3), 4, 40),
        ]);

        Assert.Collection(
            normalized,
            point => Assert.Equal(0.25, point.RelativeHeight),
            point => Assert.Equal(0.5, point.RelativeHeight),
            point => Assert.Equal(1, point.RelativeHeight));
    }
}
