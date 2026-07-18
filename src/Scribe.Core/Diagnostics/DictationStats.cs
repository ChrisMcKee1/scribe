using Scribe.Core.Models;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// Latency/volume statistics computed from stored dictation history: the numbers behind the
/// Diagnostics performance panel. Decode time, AI cleanup time, and real-time factor come from
/// per-dictation history, so no separate telemetry is collected.
/// </summary>
public static class DictationStats
{
    public sealed record MetricSummary(
        double Average,
        double Min,
        double Max,
        double P50,
        double P95);

    /// <summary>Aggregated view of the dictations inside the window. Null when there were none.</summary>
    public sealed record Snapshot(
        int Count,
        TimeSpan TotalAudio,
        MetricSummary DecodeMs,
        int CleanupCount,
        MetricSummary? CleanupMs,
        double FastestRtf,
        double RtfP50,
        double RtfP95,
        double LongestAudioSeconds);

    /// <summary>
    /// Computes stats over the entries newer than <paramref name="since"/>. Entries with a
    /// non-positive audio length are skipped (RTF would be undefined). Returns null when nothing
    /// qualifies, so the panel can show a friendly empty state instead of zeros.
    /// </summary>
    public static Snapshot? Compute(IEnumerable<HistoryEntry> entries, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var decodeMs = new List<double>();
        var rtf = new List<double>();
        var cleanupMs = new List<double>();
        long totalAudioMs = 0;
        double longestAudioMs = 0;

        foreach (var entry in entries)
        {
            if (entry.TimestampUtc < since || entry.AudioMilliseconds <= 0)
            {
                continue;
            }

            decodeMs.Add(entry.DecodeMilliseconds);
            rtf.Add(entry.DecodeMilliseconds / (double)entry.AudioMilliseconds);
            if (entry.CleanupMilliseconds is > 0)
            {
                cleanupMs.Add(entry.CleanupMilliseconds.Value);
            }

            totalAudioMs += entry.AudioMilliseconds;
            longestAudioMs = Math.Max(longestAudioMs, entry.AudioMilliseconds);
        }

        if (decodeMs.Count == 0)
        {
            return null;
        }

        decodeMs.Sort();
        rtf.Sort();
        cleanupMs.Sort();

        return new Snapshot(
            Count: decodeMs.Count,
            TotalAudio: TimeSpan.FromMilliseconds(totalAudioMs),
            DecodeMs: Summarize(decodeMs),
            CleanupCount: cleanupMs.Count,
            CleanupMs: cleanupMs.Count > 0 ? Summarize(cleanupMs) : null,
            FastestRtf: rtf.FirstOrDefault(value => value > 0),
            RtfP50: Percentile(rtf, 0.50),
            RtfP95: Percentile(rtf, 0.95),
            LongestAudioSeconds: longestAudioMs / 1000.0);
    }

    private static MetricSummary Summarize(IReadOnlyList<double> sortedAscending)
    {
        var average = sortedAscending.Average();
        return new MetricSummary(
            Average: average,
            Min: sortedAscending[0],
            Max: sortedAscending[^1],
            P50: Percentile(sortedAscending, 0.50),
            P95: Percentile(sortedAscending, 0.95));
    }

    /// <summary>
    /// Linear-interpolation percentile over an ascending-sorted list (the R-7 / Excel method). With
    /// a handful of samples this reads sensibly — the P95 of 3 dictations is near the max, not an
    /// arbitrary bucket edge.
    /// </summary>
    internal static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        ArgumentNullException.ThrowIfNull(sortedAscending);
        if (sortedAscending.Count == 0)
        {
            throw new ArgumentException("At least one sample is required.", nameof(sortedAscending));
        }

        if (sortedAscending.Count == 1)
        {
            return sortedAscending[0];
        }

        var rank = Math.Clamp(p, 0, 1) * (sortedAscending.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sortedAscending[lower];
        }

        var weight = rank - lower;
        return sortedAscending[lower] + ((sortedAscending[upper] - sortedAscending[lower]) * weight);
    }
}
