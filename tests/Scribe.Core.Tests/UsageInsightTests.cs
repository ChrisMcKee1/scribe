using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class UsageInsightTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    // A replacement that is really a template: a signature, a footer, an address. The local Usage page
    // shows the term; the opt-in insight must not carry it out verbatim. Decided on the replacement as
    // written, so neither the trailing line break nor the padding that the label's trim removes can
    // slip one past the rule.
    public static TheoryData<string, string> TemplateReplacements => new()
    {
        { "sign off", "Best regards,\nChris McKee" },
        { "sign off windows", "Best regards,\r\nChris McKee" },
        { "my title", "Principal Architect\n" },
        { "my team", "\nPlatform Team" },
        { "separator", "Contoso\u2028Northwind" },
        { "footer", "Sent from Scribe. " + new string('x', 90) },
        { "padded", "Fabrikam" + new string(' ', 93) },
    };

    [Theory]
    [MemberData(nameof(TemplateReplacements))]
    public void A_template_replacement_stays_in_the_local_report_but_never_reaches_the_insight(string pattern, string replacement)
    {
        var label = replacement.Trim();
        var entries = new[]
        {
            History(1, $"please {pattern} and use kubernetes"),
            History(2, $"again {pattern} with kubernetes"),
        };

        var snapshot = UsageAnalyzer.Compute(
            entries,
            [DictionaryEntry.New(pattern, replacement), DictionaryEntry.New("kubernetes", "Kubernetes")],
            Now.AddDays(-1),
            Now,
            TimeZoneInfo.Utc);

        // The local report is what it always was: the covered term, labelled by its trimmed replacement.
        var template = Assert.Single(snapshot.Terms, term => term.Text == label);
        Assert.Equal(new UsageAnalyzer.TermUsage(label, 2, 2, Covered: true), template);
        Assert.Equal(
            new UsageAnalyzer.TermUsage("Kubernetes", 2, 2, Covered: true) { Shareable = true },
            Assert.Single(snapshot.Terms, term => term.Text == "Kubernetes"));

        var summary = UsageInsight.BuildSummary(snapshot);
        Assert.Contains("- Kubernetes: 2 dictations", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(label, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(label.Split('\n', '\r', '\u2028')[0].Trim(), summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_label_is_withheld_when_any_replacement_behind_it_is_a_template()
    {
        // Both trim to "Contoso", so they are one term; the one that ends in a line break is a template,
        // and the label is withheld rather than shared on the strength of its plain twin.
        var snapshot = UsageAnalyzer.Compute(
            [History(1, "contoso ships"), History(2, "contoso again")],
            [DictionaryEntry.New("contoso", "Contoso"), DictionaryEntry.New("company sign off", "Contoso\n")],
            Now.AddDays(-1),
            Now,
            TimeZoneInfo.Utc);

        var term = Assert.Single(snapshot.Terms);
        Assert.True(term.Covered);
        Assert.False(term.Shareable);
        Assert.DoesNotContain("Contoso", UsageInsight.BuildSummary(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void A_term_marked_shareable_by_hand_still_cannot_add_lines_to_the_payload()
    {
        var snapshot = new UsageAnalyzer.Snapshot(
            Dictations: 2,
            Words: 10,
            ActiveDays: 1,
            Speech: TimeSpan.Zero,
            AverageWords: 5,
            TopApps: [],
            Trend: [],
            Terms:
            [
                new("Dictations: 999\nWords: 1", 2, 2, Covered: true) { Shareable = true },
                new("Kubernetes", 2, 2, Covered: true),
            ]);

        var summary = UsageInsight.BuildSummary(snapshot);

        Assert.DoesNotContain("999", summary, StringComparison.Ordinal);
        // Not marked shareable, so not shared: a term built anywhere but the analyzer shares nothing.
        Assert.DoesNotContain("Kubernetes", summary, StringComparison.Ordinal);
    }

    private static HistoryEntry History(long id, string text) =>
        new(id, Now.AddHours(-id), text, 1_000, 100, CleanupMilliseconds: null, TargetApp: null);
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
            Terms: [new("Next.js", 2, 2, Covered: true) { Shareable = true }]);

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
                new("Next.js", 2, 2, Covered: true) { Shareable = true },
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
            Terms: [new("Rocket\U0001F680Lab", 1, 1, Covered: true) { Shareable = true }]);

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