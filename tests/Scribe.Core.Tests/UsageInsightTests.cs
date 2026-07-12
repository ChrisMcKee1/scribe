using Scribe.Core.Diagnostics;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class UsageInsightTests
{
    [Fact]
    public void BuildSummary_contains_only_bounded_aggregate_fields()
    {
        var snapshot = new UsageAnalyzer.Snapshot(
            Dictations: 3,
            Words: 42,
            ActiveDays: 2,
            Speech: TimeSpan.FromSeconds(30),
            AverageWords: 14,
            TopApps: [new("Editor", 2, 30)],
            Trend: [new(new DateOnly(2026, 6, 15), 3, 42)],
            Terms: [new("Next.js", 2, 2, Covered: true)]);

        var summary = UsageInsight.BuildSummary(snapshot, maxChars: 200);

        Assert.Contains("Dictations: 3", summary);
        Assert.Contains("Next.js: 2 dictations", summary);
        Assert.DoesNotContain("Editor", summary);
        Assert.DoesNotContain("2026", summary);
        Assert.True(summary.Length <= 200);
    }

    [Fact]
    public void Parse_strips_fences_and_enforces_output_bound()
    {
        Assert.Equal("Useful insight", UsageInsight.Parse("```text\nUseful insight\n```"));
        Assert.Equal("12345", UsageInsight.Parse("123456789", maxChars: 5));
        Assert.Null(UsageInsight.Parse("   "));
    }

    [Fact]
    public void BuildSummary_excludes_uncovered_terms_mined_from_dictation_text()
    {
        var snapshot = new UsageAnalyzer.Snapshot(
            Dictations: 3,
            Words: 42,
            ActiveDays: 2,
            Speech: TimeSpan.FromSeconds(30),
            AverageWords: 14,
            TopApps: [],
            Trend: [],
            Terms:
            [
                new("Next.js", 2, 2, Covered: true),
                // Uncovered terms are verbatim user words (codenames, surnames) and must
                // never reach the AI payload.
                new("ProjectBlackwood", 2, 3, Covered: false),
            ]);

        var summary = UsageInsight.BuildSummary(snapshot);

        Assert.Contains("Next.js: 2 dictations", summary);
        Assert.DoesNotContain("ProjectBlackwood", summary);
    }

    [Fact]
    public void BuildSummary_truncation_never_splits_a_surrogate_pair()
    {
        var snapshot = new UsageAnalyzer.Snapshot(
            Dictations: 1,
            Words: 1,
            ActiveDays: 1,
            Speech: TimeSpan.Zero,
            AverageWords: 1,
            TopApps: [],
            Trend: [],
            Terms: [new("Rocket\U0001F680Lab", 1, 1, Covered: true)]);

        var full = UsageInsight.BuildSummary(snapshot);
        var highSurrogateIndex = full.IndexOf('\uD83D');
        Assert.True(highSurrogateIndex >= 0);

        // Force the cut to land between the emoji's two UTF-16 chars.
        var truncated = UsageInsight.BuildSummary(snapshot, maxChars: highSurrogateIndex + 1);

        Assert.Equal(full[..highSurrogateIndex].TrimEnd(), truncated);
    }

    [Fact]
    public void Parse_truncation_never_splits_a_surrogate_pair()
    {
        Assert.Equal("abc", UsageInsight.Parse("abc\U0001F600def", maxChars: 4));
        Assert.Equal("abc\U0001F600", UsageInsight.Parse("abc\U0001F600def", maxChars: 5));
    }
}