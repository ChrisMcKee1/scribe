using System.Text;
using System.Text.RegularExpressions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Oracle = Scribe.Core.Tests.UsageAnalyzerMemoryTests.Oracle;

namespace Scribe.Core.Tests;

// The usage merge gates of the language work (combined.md rows 1, 2, 4 and 8; sign-off SIG-LANG-01). The oracle is
// UsageAnalyzer as it was at 10c9a0b, verbatim (UsageAnalyzerMemoryTests.Oracle): a MatchCollection per phrase per
// dictation, a params array per token trim, and compiled phrase regexes. It decides every term record.
public sealed partial class UsageAnalyzerTests
{
    [Fact]
    public void Cheap_path_matches_legacy_terms_for_seeded_histories()
    {
        // Row 1 (no flag): Regex.Count, span trims and interpreted phrase regexes return what the old code returned, as
        // unbounded ordered records with their sharing, for Astra's corpus under three cultures.
        var cases = 0;
        foreach (var culture in UsageEquivalenceCorpus.Cultures)
        {
            UsageEquivalenceCorpus.InCulture(culture, () =>
            {
                foreach (var (name, terms, texts) in UsageEquivalenceCorpus.Scenarios())
                {
                    var history = UsageEquivalenceCorpus.History(texts);
                    var expected = LegacyTerms(history, terms);
                    var actual = UsageAnalyzer.Compute(
                        history, terms, UsageEquivalenceCorpus.Now.AddDays(-90), UsageEquivalenceCorpus.Now,
                        UsageEquivalenceCorpus.MayShare, TimeZoneInfo.Utc, maxTerms: int.MaxValue).Terms;
                    UsageEquivalenceCorpus.AssertSameTerms(expected, actual, $"{culture} {name}");
                    cases++;
                }
            });
        }

        Assert.Equal(3 * 73, cases);
    }

    [Fact]
    public void Every_counting_flag_set_matches_legacy_terms_for_seeded_histories()
    {
        // Rows 2 and 8: each flag, alone and together, over the same corpus and cultures, called twice (a second call
        // must not see anything the first one left behind). The old code runs once per case; its compiled phrase regexes
        // are what makes it slow.
        foreach (var culture in UsageEquivalenceCorpus.Cultures)
        {
            UsageEquivalenceCorpus.InCulture(culture, () =>
            {
                foreach (var (name, terms, texts) in UsageEquivalenceCorpus.Scenarios())
                {
                    var history = UsageEquivalenceCorpus.History(texts);
                    var expected = LegacyTerms(history, terms);
                    foreach (var flags in UsageEquivalenceCorpus.FlagCombinations)
                    {
                        var perfFlags = PerfFlags.Parse(flags);
                        for (var call = 0; call < 2; call++)
                        {
                            var actual = UsageAnalyzer.ExtractTermsForTesting(
                                history, terms, int.MaxValue, UsageEquivalenceCorpus.MayShare, perfFlags, out _);
                            UsageEquivalenceCorpus.AssertSameTerms(expected, actual, $"{culture} {name} flags '{flags}' call {call}");
                        }
                    }
                }
            });
        }
    }

    [Fact]
    public void Every_counting_flag_set_gives_the_legacy_snapshot_for_generated_histories()
    {
        // The whole snapshot (words, apps, trend, terms with sharing), bounded as the page asks for it and unbounded, over
        // every shipped row and over random slices of them.
        var random = new Random(51_027);
        var shipped = BuiltInDictionaryLibraries.All.SelectMany(library => library.Entries).ToList();
        foreach (var round in Enumerable.Range(0, 4))
        {
            var terms = round % 2 == 0 ? shipped : [.. shipped.Where(_ => random.Next(4) == 0)];
            var history = GeneratedHistory(random, 250, terms);
            var since = UsageEquivalenceCorpus.Now.AddDays(-60);
            foreach (var maxTerms in new[] { 16, int.MaxValue })
            {
                var expected = Oracle.Compute(
                    history, terms, since, UsageEquivalenceCorpus.Now, UsageEquivalenceCorpus.MayShare, TimeZoneInfo.Utc, 8, maxTerms);
                Assert.Contains(expected.Terms, term => term.Covered);
                foreach (var flags in UsageEquivalenceCorpus.FlagCombinations)
                {
                    var actual = UsageAnalyzer.Compute(
                        history, terms, since, UsageEquivalenceCorpus.Now, UsageEquivalenceCorpus.MayShare, TimeZoneInfo.Utc, 8, maxTerms,
                        PerfFlags.Parse(flags));
                    UsageEquivalenceCorpus.AssertSameSnapshot(expected, actual, $"round {round} max {maxTerms} flags '{flags}'");
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(1_024)]
    public void Long_delimited_tokens_are_counted_as_the_old_code_counted_them_under_every_flag_set(int segments)
    {
        // Path-like tokens of many segments, combining marks, .NET, C#, dotted tokens, a-a inside a-a-a and supplementary
        // characters, each form its own term so every per-form count is compared.
        DictionaryEntry[] terms =
        [
            DictionaryEntry.New("seg", "Seg"), DictionaryEntry.New("a-a", "A-A"), DictionaryEntry.New(".net", ".NET"),
            DictionaryEntry.New("c#", "C#"), DictionaryEntry.New("node.js", "Node.js"), DictionaryEntry.New("e\u0301te", "\u00C9t\u00E9"),
            DictionaryEntry.New("\U00010400x", "Deseret"), DictionaryEntry.New("x/y", "X/Y"), DictionaryEntry.New("seg/seg", "SegSeg"),
        ];
        string[] parts = ["seg", "a-a-a", ".net", "c#", "node.js", "e\u0301te", "\U00010400x", "x/y", "segx", "a-a"];
        var random = new Random(segments);
        var texts = new List<string>();
        for (var t = 0; t < 4; t++)
        {
            var token = new StringBuilder();
            for (var s = 0; s < segments; s++)
            {
                token.Append(s == 0 ? string.Empty : random.Next(3) == 0 ? "." : "/").Append(parts[random.Next(parts.Length)]);
            }

            texts.Add(token.ToString() + " and " + token.ToString().ToUpperInvariant());
        }

        var history = UsageEquivalenceCorpus.History(texts);
        var expected = LegacyTerms(history, terms);
        Assert.Contains(expected, term => term.Covered);
        foreach (var flags in UsageEquivalenceCorpus.FlagCombinations)
        {
            var actual = UsageAnalyzer.ExtractTermsForTesting(
                history, terms, int.MaxValue, UsageEquivalenceCorpus.MayShare, PerfFlags.Parse(flags), out _);
            UsageEquivalenceCorpus.AssertSameTerms(expected, actual, $"{segments} segments, flags '{flags}'");
        }
    }

    [Fact]
    public void The_term_index_builds_only_the_phrase_regexes_a_dictation_may_need()
    {
        // Row 8's operation count: every phrase regex is built on the old path, and under UsageTermIndex only those of phrases
        // an ordinal search finds in some dictation (or that it cannot judge: non-ASCII, or a dictation holding U+212A).
        DictionaryEntry[] terms =
        [
            DictionaryEntry.New("next js", "Next.js"), DictionaryEntry.New("visual studio", "Visual Studio"),
            DictionaryEntry.New("dev ops", "DevOps"), DictionaryEntry.New("caf\u00e9 au lait", "Coffee"), DictionaryEntry.New("kube ctl", "Kubectl"),
        ];
        int Built(string flags, params string[] texts)
        {
            _ = UsageAnalyzer.ExtractTermsForTesting(UsageEquivalenceCorpus.History(texts), terms, int.MaxValue, null, PerfFlags.Parse(flags), out var built);
            return built;
        }

        // Five phrases: "next js", "visual studio", "dev ops", "kube ctl" and the non-ASCII "caf\u00e9 au lait", which the search
        // never judges, so it is built whenever a dictation is counted (the written forms are single tokens).
        Assert.Equal(5, Built(string.Empty, "we use next js"));
        Assert.Equal(5, Built(PerfFlags.SparseUsageAggregation, "we use next js"));
        Assert.Equal(2, Built(PerfFlags.UsageTermIndex, "we use NEXT JS"));
        Assert.Equal(1, Built(PerfFlags.UsageTermIndex, "nothing to see"));
        Assert.Equal(1, Built(PerfFlags.UsageTermIndex, "caf\u00e9 au lait"));
        Assert.Equal(5, Built(PerfFlags.UsageTermIndex, "\u212Aube ctl"));
        Assert.Equal(3, Built($"{PerfFlags.UsageTermIndex},{PerfFlags.SparseUsageAggregation}", "visual studio", "dev ops"));
        Assert.Equal(0, Built(PerfFlags.UsageTermIndex));
    }

    [Fact]
    public void The_two_counting_flags_are_read_independently()
    {
        Assert.Equal(default, UsageAnalyzer.UsageCounting.From(null));
        Assert.Equal(default, UsageAnalyzer.UsageCounting.From(PerfFlags.None));
        Assert.Equal(new UsageAnalyzer.UsageCounting(true, false), UsageAnalyzer.UsageCounting.From(PerfFlags.Parse(PerfFlags.SparseUsageAggregation)));
        Assert.Equal(new UsageAnalyzer.UsageCounting(false, true), UsageAnalyzer.UsageCounting.From(PerfFlags.Parse(PerfFlags.UsageTermIndex)));
        Assert.Equal(
            new UsageAnalyzer.UsageCounting(true, true),
            UsageAnalyzer.UsageCounting.From(PerfFlags.Parse($"{PerfFlags.UsageTermIndex},{PerfFlags.SparseUsageAggregation}")));
    }
    [Fact]
    public void Phrase_regexes_use_the_matchers_options_so_the_ordinal_guard_holds_for_them()
    {
        // UsageTermIndex's phrase prefilter rests on OrdinalPrefilter's guard, which is read with the matcher's options.
        Assert.Equal(TextPostProcessor.DictionaryMatchOptions, UsageAnalyzer.PhraseRegexOptions);
        Assert.Equal(RegexOptions.None, UsageAnalyzer.PhraseRegexOptions & RegexOptions.Compiled);
    }

    [Fact]
    public void Word_count_matches_legacy_regex_for_seeded_unicode()
    {
        // Row 4 (no flag): Word().Count(text) against the old Word().Matches(text).Count, same generated pattern, with null,
        // empty and white-space inputs, under three cultures.
        var texts = UsageEquivalenceCorpus.UnicodeTexts(seed: 4_051, count: 2_000);
        foreach (var culture in UsageEquivalenceCorpus.Cultures)
        {
            UsageEquivalenceCorpus.InCulture(culture, () =>
            {
                foreach (var text in texts.Concat([null, string.Empty, " ", "\t\r\n"]))
                {
                    Assert.Equal(Oracle.CountWords(text), UsageAnalyzer.CountWords(text));
                }
            });
        }
    }

    private static IReadOnlyList<UsageAnalyzer.TermUsage> LegacyTerms(IReadOnlyList<HistoryEntry> history, IReadOnlyList<DictionaryEntry> terms) =>
        Oracle.Compute(
            history, terms, UsageEquivalenceCorpus.Now.AddDays(-90), UsageEquivalenceCorpus.Now, UsageEquivalenceCorpus.MayShare,
            TimeZoneInfo.Utc, maxTerms: int.MaxValue).Terms;

    // Dictations mixing prose, jargon shapes and every term's spoken and written forms in any case.
    private static List<HistoryEntry> GeneratedHistory(Random random, int count, IReadOnlyList<DictionaryEntry> terms)
    {
        string[] plain = ["the", "and", "please", "send", "review", "OpenAI", "GitHub", "K8s,", "C#;", ".NET:", "e.g.", "\u212Aelvin", "\u0130stanbul", "a-a-a", "x/y"];
        string?[] apps = ["WINWORD", "ms-teams", null, "Code"];
        var entries = new List<HistoryEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var text = new StringBuilder();
            for (var w = random.Next(4, 40); w > 0; w--)
            {
                if (terms.Count > 0 && random.Next(3) == 0)
                {
                    var term = terms[random.Next(terms.Count)];
                    var form = random.Next(2) == 0 ? term.Pattern : term.Replacement;
                    text.Append(random.Next(3) switch { 0 => form.ToUpperInvariant(), 1 => form.ToLowerInvariant(), _ => form });
                }
                else
                {
                    text.Append(plain[random.Next(plain.Length)]);
                }

                text.Append(random.Next(8) switch { 0 => ", ", 1 => ". ", 2 => "\n", _ => " " });
            }

            entries.Add(new HistoryEntry(
                i + 1, UsageEquivalenceCorpus.Now.AddMinutes(-random.Next(90 * 24 * 60)), text.ToString(),
                random.Next(20_000), 100, TargetApp: apps[random.Next(apps.Length)]));
        }

        return entries;
    }
}
