using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using static Scribe.Core.Tests.TextPostProcessorReferencePipelineTests;

namespace Scribe.Core.Tests;

/// <summary>
/// The matcher's prefilter (<see cref="PerfFlags.MatcherPrefilter"/>, combined.md row 6, E.6) on 0.5.1's TX-1 and TX-8 code,
/// against the same processor with it off deciding every result: the same text and the same replacement records, in order.
/// The fuzz corpus: Astra's adversarial rules and texts under three cultures, with and without snippets, and sources absent,
/// equal to the text and cleaned; every shipped word pack at its own size and at about 10,000 rules; U+212A texts and
/// non-ASCII spoken forms (which always run their regex); and rules reused across vocabulary generations.
/// </summary>
public sealed class MatcherFlagEquivalenceTests
{
    private static readonly PerfFlags Prefilter = PerfFlags.Parse(PerfFlags.MatcherPrefilter);

    // Astra's adversarial rules (AstraChallengeChecks.CheckMatcherCases): overlapping substring rules, the expansion guard,
    // tight punctuation, an emptying rule, a lone surrogate, a $-replacement, a leading-space pattern and non-ASCII forms.
    private static readonly DictionaryEntry[] Adversarial =
    [
        DictionaryEntry.New("xa", "Q", wholeWord: false),
        DictionaryEntry.New("aa", "R", wholeWord: false),
        DictionaryEntry.New("ab", "Y", wholeWord: false),
        DictionaryEntry.New("bc", "X", wholeWord: false),
        DictionaryEntry.New("york", "New York"),
        DictionaryEntry.New("azure", "Azure"),
        DictionaryEntry.New("kube", "Kube"),
        DictionaryEntry.New("i", "I"),
        DictionaryEntry.New("comma", ","),
        DictionaryEntry.New("um", ""),
        DictionaryEntry.New("er", "", wholeWord: false),
        DictionaryEntry.New("a-a", "A-A", wholeWord: false),
        DictionaryEntry.New(".net", ".NET"),
        DictionaryEntry.New("\u03c3", "S"),
        DictionaryEntry.New("\ud800", "H", wholeWord: false),
        DictionaryEntry.New("pay", "$1"),
        DictionaryEntry.New(" gap", "GAP"),
        DictionaryEntry.New("stra\u00dfe", "Stra\u00dfe"),
        DictionaryEntry.New("\u212Aelvin", "Kelvin"),
    ];

    private static readonly Snippet[] Snippets =
    [
        new(1, "insert signature", "Regards,\nChris"),
        new(2, "brb", "be right back, azure"),
        new(3, "new york", "big apple"),
        new(4, "comma", ","),
    ];

    [Fact]
    public void Adversarial_rules_give_the_old_text_and_records_with_the_prefilter_under_three_cultures()
    {
        var texts = new List<string>
        {
            "xaaa", "abc abcd", "New York york New york", "Azure azure Azure", "um um", "a-a-a-a", "KUBE \u212Aube kube",
            "i I \u0130 \u0131", "\u03c3 \u03c2 \u03a3", "azure_x x_azure x\u0301azure azure\u0301x", "\0 azure \ud800",
            "pay comma azure .net", " gap gap", "azure  \t , azure", "er er er", "insert signature brb new york comma",
            "STRASSE stra\u00dfe \u212Aelvin kelvin", "brb, brb. brb", "new york, new york",
        };
        var random = new Random(16492);
        string[] units = ["xaaa", "azure", "Azure", "um", "er", "york", "New York", "a-a-a", "\u212Aube", "I", "\u0130", "\u03c2",
            "pay", "comma", " gap", ".net", "\ud800", "_", "brb", "insert signature", "new york", "\u212Aelvin", "stra\u00dfe"];
        for (var i = 0; i < 200; i++)
        {
            texts.Add(string.Join(" ", Enumerable.Range(0, 12).Select(_ => units[random.Next(units.Length)])));
        }

        var comparisons = 0;
        foreach (var culture in UsageEquivalenceCorpus.Cultures)
        {
            UsageEquivalenceCorpus.InCulture(culture, () =>
            {
                comparisons += CompareAll(Adversarial, [], Snippets, texts);
                comparisons += CompareAll(Adversarial, [], [], texts);
            });
        }

        // 219 texts, three sources each, two snippet sets, three cultures.
        Assert.Equal(219 * 3 * 2 * 3, comparisons);
    }

    [Theory]
    [InlineData(1, 1_500)]
    [InlineData(7, 300)]
    public void Every_shipped_word_pack_gives_the_old_text_and_records_with_the_prefilter(int copies, int dictations)
    {
        // Every shipped row as one vocabulary, merged under a small dictionary as dictation merges them; with copies above
        // one, renamed copies of the shipped rows take the vocabulary to about 10,000 rules. Dictations are made of the rules'
        // own spoken and written forms in any case, with prose, U+212A and non-ASCII noise, so both the rules the search finds
        // and the texts it may not judge are exercised.
        var shipped = BuiltInDictionaryLibraries.All.SelectMany(pack => pack.Entries).ToList();
        var library = new List<DictionaryEntry>(shipped);
        for (var copy = 1; copy < copies; copy++)
        {
            library.AddRange(shipped.Select(entry => entry with { Pattern = $"{entry.Pattern} {copy}", Replacement = $"{entry.Replacement} {copy}" }));
        }

        DictionaryEntry[] dictionary = [DictionaryEntry.New("york", "New York"), DictionaryEntry.New("azure", "AZURE"), DictionaryEntry.New("comma", ",")];
        var random = new Random(51_101 + copies);
        string[] noise = ["the", "and", "\u212Aube", "\u0130", "\u03c2", ",", ".", "um", "new", "york", "caf\u00e9", "\u00fcber"];
        var texts = new List<string>(dictations);
        for (var i = 0; i < dictations; i++)
        {
            texts.Add(string.Join(random.Next(4) == 0 ? ", " : " ", Enumerable.Range(0, random.Next(4, 24)).Select(_ =>
            {
                if (random.Next(3) == 0)
                {
                    return noise[random.Next(noise.Length)];
                }

                var entry = library[random.Next(library.Count)];
                var form = random.Next(2) == 0 ? entry.Pattern : entry.Replacement;
                return random.Next(3) switch { 0 => form.ToUpperInvariant(), 1 => form.ToLowerInvariant(), _ => form };
            })));
        }

        var comparisons = CompareAll(dictionary, library, Snippets, texts);
        Assert.Equal(dictations * 3, comparisons);
    }

    [Fact]
    public void Rules_reused_across_generations_give_the_old_path_s_results_with_the_prefilter()
    {
        // TX-8 hands a later generation the rule compiled for an unchanged entry; the rule's ASCII test belongs to its spoken
        // form, so an entry whose spoken form changes (including to and from non-ASCII) gets a new rule and a new answer.
        var shipped = BuiltInDictionaryLibraries.All.SelectMany(pack => pack.Entries).ToList();
        var random = new Random(20_261_101);
        var baseline = Processor([], [], [], PerfFlags.None);
        var prefiltered = Processor([], [], [], Prefilter);
        for (var generation = 0; generation < 8; generation++)
        {
            var entries = shipped
                .Where(_ => random.Next(3) != 0)
                .Select(entry => random.Next(12) switch
                {
                    0 => entry with { Replacement = entry.Replacement + " (edited)" },
                    1 => entry with { Pattern = "\u212A" + entry.Pattern },
                    2 => entry with { Pattern = entry.Pattern.ToUpperInvariant() },
                    _ => entry,
                })
                .ToList();
            var oldRules = baseline.Compile(entries, []);
            var reused = prefiltered.Compile(entries, []);
            for (var i = 0; i < 25; i++)
            {
                var text = string.Join(
                    random.Next(2) == 0 ? " and " : ", ",
                    Enumerable.Range(0, 6).Select(_ => entries[random.Next(entries.Count)].Pattern + (random.Next(4) == 0 ? " \u212Aube" : string.Empty)));
                Same(baseline.ProcessDetailed(text, null, oldRules), prefiltered.ProcessDetailed(text, null, reused), text, null);
                Same(baseline.ProcessDetailed(text, text.ToLowerInvariant(), oldRules), prefiltered.ProcessDetailed(text, text.ToLowerInvariant(), reused), text, text.ToLowerInvariant());
            }
        }
    }

    [Fact]
    public void The_prefilter_is_read_from_the_flags_once_and_is_off_by_default()
    {
        var logger = NullLogger<TextPostProcessor>.Instance;
        var stub = new DictionaryStub([]);
        Assert.False(new TextPostProcessor(stub, logger).UseMatcherPrefilter);
        Assert.False(new TextPostProcessor(stub, logger, perfFlags: PerfFlags.None).UseMatcherPrefilter);
        Assert.True(new TextPostProcessor(stub, logger, perfFlags: Prefilter).UseMatcherPrefilter);
        Assert.False(new TextPostProcessor(stub, logger, perfFlags: PerfFlags.Parse(PerfFlags.SparseUsageAggregation)).UseMatcherPrefilter);
    }

    [Fact]
    public void The_container_hands_the_post_processor_the_process_flags()
    {
        // AddScribeCore registers TextPostProcessor by type, and its flags parameter is optional, so the container fills it
        // with the registered PerfFlags. The flags are registered here after AddScribeCore (the last registration wins)
        // rather than through SCRIBE_PERF_FLAGS, which a test never sets.
        var root = Path.Combine(Path.GetTempPath(), "scribe-di-matcher-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (flags, expected) in new[] { (PerfFlags.None, false), (Prefilter, true) })
            {
                var services = new ServiceCollection();
                services.AddScribeCore();
                services.AddSingleton(new AppPaths(root));
                services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
                services.AddSingleton(flags);
                using var provider = services.BuildServiceProvider();
                var processor = Assert.IsType<TextPostProcessor>(provider.GetRequiredService<ITextPostProcessor>());
                Assert.Equal(expected, processor.UseMatcherPrefilter);
            }
        }
        finally
        {
            DatabasePools.Release(new AppPaths(root));
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a leftover temp folder is harmless, and none may have been written.
            }
        }
    }
    [Fact]
    public void A_rule_the_search_rules_out_never_runs_its_regex_and_one_it_finds_always_does()
    {
        // The skip is observable only as work not done, so the candidates a pass collects are compared directly: in a sound
        // text only the rules whose spoken form occurs contribute, and in a text holding U+212A every rule is judged by its
        // regex, which here finds "\u212Aube" for the rule "kube".
        DictionaryEntry[] entries = [DictionaryEntry.New("kube", "Kube"), DictionaryEntry.New("helm", "Helm"), DictionaryEntry.New("caf\u00e9", "Caf\u00e9")];
        var rules = Processor([], [], [], PerfFlags.None).Compile(entries, []).Rules;

        Assert.Equal(
            TextPostProcessor.Candidates(rules, "kube and helm")!.Select(c => c.Pattern),
            TextPostProcessor.Candidates(rules, "kube and helm", prefilter: true)!.Select(c => c.Pattern));
        Assert.Null(TextPostProcessor.Candidates(rules, "nothing here", prefilter: true));
        Assert.Equal(["kube"], TextPostProcessor.Candidates(rules, "\u212Aube", prefilter: true)!.Select(c => c.Pattern));
        Assert.Equal(["caf\u00e9"], TextPostProcessor.Candidates(rules, "CAF\u00c9", prefilter: true)!.Select(c => c.Pattern));
    }

    private static int CompareAll(
        IReadOnlyList<DictionaryEntry> dictionary, IReadOnlyList<DictionaryEntry> library, IReadOnlyList<Snippet> snippets, IReadOnlyList<string> texts)
    {
        var baseline = Processor(dictionary, library, snippets, PerfFlags.None);
        var prefiltered = Processor(dictionary, library, snippets, Prefilter);
        var oldRules = baseline.Compile(dictionary, library);
        var newRules = prefiltered.Compile(dictionary, library);
        var comparisons = 0;
        foreach (var text in texts)
        {
            var cleaned = baseline.ProcessDetailed(text, null, oldRules).Text;
            foreach (var source in new[] { null, text, cleaned })
            {
                Same(baseline.ProcessDetailed(text, source, oldRules), prefiltered.ProcessDetailed(text, source, newRules), text, source);
                comparisons++;
            }
        }

        // The processors' own rules (Reload's path) agree too, for the first few texts.
        foreach (var text in texts.Take(10))
        {
            Same(baseline.ProcessDetailed(text, text), prefiltered.ProcessDetailed(text, text), text, text);
        }

        return comparisons;
    }

    private static TextPostProcessor Processor(
        IReadOnlyList<DictionaryEntry> dictionary, IReadOnlyList<DictionaryEntry> library, IReadOnlyList<Snippet> snippets, PerfFlags flags) =>
        new(new DictionaryStub(dictionary), NullLogger<TextPostProcessor>.Instance, new SnippetStub(snippets),
            library.Count == 0 ? null : new FixedLibraries(library), flags);

    private static void Same(TextPostProcessingResult expected, TextPostProcessingResult actual, string text, string? source)
    {
        var context = $"text '{Escape(text)}', source '{(source is null ? "null" : Escape(source))}'";
        Assert.True(string.Equals(expected.Text, actual.Text, StringComparison.Ordinal), $"{context}: '{Escape(actual.Text)}', the old path gave '{Escape(expected.Text)}'.");
        Assert.True(
            expected.Replacements.SequenceEqual(actual.Replacements),
            $"{context}: [{string.Join("; ", actual.Replacements)}], the old path gave [{string.Join("; ", expected.Replacements)}].");
    }

    private static string Escape(string value) => value.Replace("\n", "\\n").Replace("\t", "\\t");

    private sealed class FixedLibraries(IReadOnlyList<DictionaryEntry> entries) : IDictionaryLibraryService
    {
        public IReadOnlyList<DictionaryLibrary> GetLibraries() => [];

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries() => entries;

        public IReadOnlyList<DictionaryEntry> GetEnabledLibraryEntries(IReadOnlyCollection<string> enabledIds) => entries;

        public DictionaryLibrary Import(string csv, string? suggestedName) => throw new NotSupportedException();

        public void Remove(string id) => throw new NotSupportedException();
    }
}
