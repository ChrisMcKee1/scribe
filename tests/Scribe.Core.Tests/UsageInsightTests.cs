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
}