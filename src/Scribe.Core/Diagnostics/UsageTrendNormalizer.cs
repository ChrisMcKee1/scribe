namespace Scribe.Core.Diagnostics;

/// <summary>Normalizes usage trend buckets for presentation without UI-specific dimensions.</summary>
public static class UsageTrendNormalizer
{
    public sealed record Point(UsageAnalyzer.TrendPoint Trend, double RelativeHeight);

    public static IReadOnlyList<Point> Normalize(IEnumerable<UsageAnalyzer.TrendPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var source = points.ToList();
        if (source.Count == 0)
        {
            return [];
        }

        var maximum = source.Max(point => Math.Max(0, point.Dictations));
        return source
            .Select(point => new Point(
                point,
                maximum == 0
                    ? 0
                    : Math.Clamp(point.Dictations / (double)maximum, 0, 1)))
            .ToList();
    }
}
