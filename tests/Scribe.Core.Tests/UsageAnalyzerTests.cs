using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class UsageAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compute_uses_one_period_for_all_metrics_and_orders_apps_deterministically()
    {
        var entries = new[]
        {
            Entry(1, Now.AddDays(-1), "one two three", 3_000, "Visual Studio Code"),
            Entry(2, Now.AddDays(-2), "four five", 2_000, "Terminal"),
            Entry(3, Now.AddDays(-3), "six", 1_000, "terminal"),
            Entry(4, Now.AddDays(-40), "excluded words", 9_000, "Excluded"),
        };

        var snapshot = UsageAnalyzer.Compute(
            entries,
            [],
            Now.AddDays(-30),
            Now,
            TimeZoneInfo.Utc);

        Assert.Equal(3, snapshot.Dictations);
        Assert.Equal(6, snapshot.Words);
        Assert.Equal(3, snapshot.ActiveDays);
        Assert.Equal(TimeSpan.FromSeconds(6), snapshot.Speech);
        Assert.Equal(2, snapshot.AverageWords);
        Assert.Collection(
            snapshot.TopApps,
            app => Assert.Equal(new UsageAnalyzer.AppUsage("Terminal", 2, 3), app),
            app => Assert.Equal(new UsageAnalyzer.AppUsage("Visual Studio Code", 1, 3), app));
        Assert.Equal(31, snapshot.Trend.Count);
        Assert.Equal(3, snapshot.Trend.Sum(point => point.Dictations));
    }

    [Fact]
    public void Compute_counts_unicode_words_and_normalizes_blank_app_names()
    {
        var snapshot = UsageAnalyzer.Compute(
            [Entry(1, Now, "naïve café 東京 don't state-of-the-art", 1_000, "  ")],
            [],
            Now.AddDays(-1),
            Now,
            TimeZoneInfo.Utc);

        Assert.Equal(5, snapshot.Words);
        var app = Assert.Single(snapshot.TopApps);
        Assert.Equal("Unknown app", app.Name);
    }

    [Fact]
    public void Compute_recognizes_patterns_and_canonical_multiword_replacements()
    {
        var entries = new[]
        {
            Entry(1, Now, "Deploy with Tailwind CSS and Next.js.", 1_000, null),
            Entry(2, Now.AddHours(-1), "Use tailwind css with next js.", 1_000, null),
        };
        var terms = new[]
        {
            DictionaryEntry.New("tailwind css", "Tailwind CSS"),
            DictionaryEntry.New("next js", "Next.js"),
        };

        var snapshot = UsageAnalyzer.Compute(entries, terms, Now.AddDays(-1), Now, TimeZoneInfo.Utc);

        Assert.Contains(snapshot.Terms, term =>
            term == new UsageAnalyzer.TermUsage("Tailwind CSS", 2, 2, Covered: true));
        Assert.Contains(snapshot.Terms, term =>
            term == new UsageAnalyzer.TermUsage("Next.js", 2, 2, Covered: true));
    }

    [Fact]
    public void Compute_suggests_only_recurring_jargon_shapes()
    {
        var entries = new[]
        {
            Entry(1, Now, "Hello CloudThing from ProjectAlpha", 1_000, null),
            Entry(2, Now.AddHours(-1), "Hello CloudThing again", 1_000, null),
            Entry(3, Now.AddHours(-2), "Hello ordinary prose", 1_000, null),
        };

        var snapshot = UsageAnalyzer.Compute(entries, [], Now.AddDays(-1), Now, TimeZoneInfo.Utc);

        var term = Assert.Single(snapshot.Terms);
        Assert.Equal(new UsageAnalyzer.TermUsage("CloudThing", 2, 2, Covered: false), term);
    }

    [Fact]
    public void Compute_uses_week_buckets_for_long_periods_and_fills_gaps()
    {
        var snapshot = UsageAnalyzer.Compute(
            [Entry(1, Now.AddDays(-40), "one", 1_000, null)],
            [],
            Now.AddDays(-90),
            Now,
            TimeZoneInfo.Utc);

        Assert.True(snapshot.Trend.Count is >= 13 and <= 14);
        Assert.Equal(1, snapshot.Trend.Sum(point => point.Dictations));
        Assert.True(snapshot.Trend.SequenceEqual(snapshot.Trend.OrderBy(point => point.Start)));
    }

    [Fact]
    public void Compute_reports_trend_granularity_instead_of_leaving_callers_to_guess()
    {
        var daily = UsageAnalyzer.Compute(
            [Entry(1, Now.AddDays(-1), "one", 1_000, null)],
            [],
            Now.AddDays(-30),
            Now,
            TimeZoneInfo.Utc);
        var weekly = UsageAnalyzer.Compute(
            [Entry(1, Now.AddDays(-1), "one", 1_000, null)],
            [],
            Now.AddDays(-90),
            Now,
            TimeZoneInfo.Utc);

        Assert.Equal(UsageAnalyzer.TrendGranularity.Daily, daily.Granularity);
        Assert.Equal(UsageAnalyzer.TrendGranularity.Weekly, weekly.Granularity);
        // A 90 day period yields ~13 weekly points, so a Trend.Count > 31 heuristic mislabels
        // weekly buckets as days; Granularity is the only reliable signal.
        Assert.True(weekly.Trend.Count <= 31);
    }

    [Fact]
    public void Compute_skips_dictionary_entries_with_no_usable_forms()
    {
        // A 1-char pattern plus 1-char replacement leaves an empty form list; this used to
        // throw InvalidOperationException from Max() and blank the whole Usage page.
        var snapshot = UsageAnalyzer.Compute(
            [Entry(1, Now, "a short note", 1_000, null)],
            [DictionaryEntry.New(" a ", " b ")],
            Now.AddDays(-1),
            Now,
            TimeZoneInfo.Utc);

        Assert.DoesNotContain(snapshot.Terms, term => term.Covered);
    }

    [Fact]
    public void Compute_counts_dotted_and_leading_dot_forms_with_word_boundaries()
    {
        var entries = new[]
        {
            Entry(1, Now, "Ship Next.js and .NET apps.", 1_000, null),
            Entry(2, Now.AddHours(-1), "The dotnet CLI targets .net today.", 1_000, null),
        };
        var terms = new[]
        {
            DictionaryEntry.New("next js", "Next.js"),
            DictionaryEntry.New("dot net", ".NET"),
        };

        var snapshot = UsageAnalyzer.Compute(entries, terms, Now.AddDays(-1), Now, TimeZoneInfo.Utc);

        Assert.Contains(snapshot.Terms, term =>
            term == new UsageAnalyzer.TermUsage("Next.js", 1, 1, Covered: true));
        // ".net" matches case-insensitively in both entries; the dot in "dotnet" is absent so
        // the leading-dot form must not fire there.
        Assert.Contains(snapshot.Terms, term =>
            term == new UsageAnalyzer.TermUsage(".NET", 2, 2, Covered: true));
    }

    [Fact]
    public void Compute_does_not_match_forms_inside_larger_words()
    {
        var entries = new[]
        {
            Entry(1, Now, "Trusted setups crust and thrust", 1_000, null),
            Entry(2, Now.AddHours(-1), "Rust is fine", 1_000, null),
        };

        var snapshot = UsageAnalyzer.Compute(
            entries,
            [DictionaryEntry.New("rust", "Rust")],
            Now.AddDays(-1),
            Now,
            TimeZoneInfo.Utc);

        var term = Assert.Single(snapshot.Terms, term => term.Covered);
        Assert.Equal(new UsageAnalyzer.TermUsage("Rust", 1, 1, Covered: true), term);
    }

    [Fact]
    public void Compute_matches_multiword_forms_case_insensitively_and_takes_max_across_forms()
    {
        var entries = new[]
        {
            Entry(1, Now, "TAILWIND CSS everywhere", 1_000, null),
            Entry(2, Now.AddHours(-1), "Use next js and Next.js side by side", 1_000, null),
        };
        var terms = new[]
        {
            DictionaryEntry.New("tailwind css", "Tailwind CSS"),
            DictionaryEntry.New("next js", "Next.js"),
        };

        var snapshot = UsageAnalyzer.Compute(entries, terms, Now.AddDays(-1), Now, TimeZoneInfo.Utc);

        Assert.Contains(snapshot.Terms, term =>
            term == new UsageAnalyzer.TermUsage("Tailwind CSS", 1, 1, Covered: true));
        // Pattern and replacement each match once in entry 2; occurrences take the max across
        // forms (one spoken term), not the sum, which would double count it as 2.
        Assert.Contains(snapshot.Terms, term =>
            term == new UsageAnalyzer.TermUsage("Next.js", 1, 1, Covered: true));
    }

    [Fact]
    public void Compute_preserves_non_overlapping_counts_for_single_token_forms()
    {
        var snapshot = UsageAnalyzer.Compute(
            [Entry(1, Now, "a-a-a", 1_000, null)],
            [DictionaryEntry.New("a-a", "A-A")],
            Now.AddDays(-1),
            Now,
            TimeZoneInfo.Utc);

        var term = Assert.Single(snapshot.Terms, term => term.Covered);
        Assert.Equal(new UsageAnalyzer.TermUsage("A-A", 1, 1, Covered: true), term);
    }

    private static HistoryEntry Entry(
        long id,
        DateTimeOffset timestamp,
        string text,
        int audioMilliseconds,
        string? targetApp) =>
        new(id, timestamp, text, audioMilliseconds, 100, CleanupMilliseconds: null, TargetApp: targetApp);
}