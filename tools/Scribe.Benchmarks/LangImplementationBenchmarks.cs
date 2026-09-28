using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Benchmarks;

/// <summary>
/// The dictionary pass with and without 0.5.1's MatcherPrefilter (combined.md row 6), on the TX-1 and TX-8 code: every
/// shipped word pack plus a small dictionary (1,554 rules), or renamed copies of them (10,432 rules), for one dictation's
/// cleaned text and raw source at three lengths. It measures the production post-processor; the setup checks that both
/// arms give the same text and records.
/// </summary>
[MemoryDiagnoser]
public class LangMatcherPassBenchmarks
{
    private TextPostProcessor _processor = null!;
    private CompiledDictionaryRules _rules = null!;
    private string _cleaned = null!;
    private string _raw = null!;

    public enum Path
    {
        Old,
        Prefilter,
    }

    [ParamsSource(nameof(Arms))]
    public Path Arm { get; set; }

    [ParamsSource(nameof(RuleCounts))]
    public int Rules { get; set; }

    [ParamsSource(nameof(CharacterCounts))]
    public int Characters { get; set; }

    public static IEnumerable<Path> Arms => LangBenchmarkSubset.Pick(nameof(Arm), Path.Old, Path.Prefilter);

    public static IEnumerable<int> RuleCounts => LangBenchmarkSubset.Pick(nameof(Rules), 1_554, 10_432);

    public static IEnumerable<int> CharacterCounts => LangBenchmarkSubset.Pick(nameof(Characters), 130, 412, 1865);

    [GlobalSetup]
    public void Setup()
    {
        var shipped = BuiltInDictionaryLibraries.All.SelectMany(pack => pack.Entries).ToList();
        var library = new List<DictionaryEntry>(shipped);
        for (var copy = 1; library.Count + 5 < Rules; copy++)
        {
            library.AddRange(shipped
                .Take(Rules - 5 - library.Count)
                .Select(entry => entry with { Pattern = $"{entry.Pattern} {copy}", Replacement = $"{entry.Replacement} {copy}" }));
        }

        DictionaryEntry[] dictionary =
        [
            DictionaryEntry.New("dot net", ".NET"), DictionaryEntry.New("york", "New York"), DictionaryEntry.New("comma", ","),
            DictionaryEntry.New("github", "GitHub"), DictionaryEntry.New("azure", "Azure"),
        ];
        var flags = Arm == Path.Prefilter ? PerfFlags.Parse(PerfFlags.MatcherPrefilter) : PerfFlags.None;
        _processor = new TextPostProcessor(
            new RepresentativeWorkload.DictionaryStub(dictionary), NullLogger<TextPostProcessor>.Instance, perfFlags: flags);
        _rules = _processor.Compile(dictionary, library);

        const string raw = "so i pushed the dot net api changes to github and the azure devops pipeline ran the tests before the blazor front end deployed then we flew to york ";
        const string cleaned = "So I pushed the .NET API changes to GitHub, and the Azure DevOps pipeline ran the tests before the Blazor front end deployed. Then we flew to New York. ";
        _raw = Repeat(raw, Characters);
        _cleaned = Repeat(cleaned, Characters);

        var old = new TextPostProcessor(new RepresentativeWorkload.DictionaryStub(dictionary), NullLogger<TextPostProcessor>.Instance);
        var expected = old.ProcessDetailed(_cleaned, _raw, old.Compile(dictionary, library));
        var actual = _processor.ProcessDetailed(_cleaned, _raw, _rules);
        if (expected.Text != actual.Text || !expected.Replacements.SequenceEqual(actual.Replacements))
        {
            throw new InvalidOperationException($"The {Arm} pass disagrees with the old pass.");
        }
    }

    [Benchmark]
    public TextPostProcessingResult CleanedWithRawSource() => _processor.ProcessDetailed(_cleaned, _raw, _rules);

    private static string Repeat(string sentence, int characters)
    {
        var text = string.Concat(Enumerable.Repeat(sentence, (characters / sentence.Length) + 1));
        var cut = text.LastIndexOf(' ', Math.Min(characters, text.Length - 1));
        return text[..(cut > 0 ? cut : characters)];
    }
}

/// <summary>
/// One Usage page load with every word pack on (combined.md rows 2 and 8): a complete
/// <see cref="UsageAnalyzer.Compute(IEnumerable{HistoryEntry}, IEnumerable{DictionaryEntry}, DateTimeOffset, DateTimeOffset, Func{DictionaryEntry, bool}, TimeZoneInfo, int, int, PerfFlags)"/>
/// over 5,000 synthetic histories on the new base (PD1 and PD1c included): the old counting, SparseUsageAggregation,
/// UsageTermIndex, and both. The path-like corpus adds delimiter-rich tokens. The setup checks every arm's terms.
/// </summary>
[MemoryDiagnoser]
public class LangUsageSnapshotBenchmarks
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private List<HistoryEntry> _history = null!;
    private List<DictionaryEntry> _terms = null!;
    private PerfFlags _flags = PerfFlags.None;

    public enum Path
    {
        Old,
        Sparse,
        TermIndex,
        Both,
    }

    [ParamsSource(nameof(Arms))]
    public Path Arm { get; set; }

    [ParamsSource(nameof(Corpora))]
    public string Corpus { get; set; } = "all packs";

    public static IEnumerable<Path> Arms => LangBenchmarkSubset.Pick(nameof(Arm), Path.Old, Path.Sparse, Path.TermIndex, Path.Both);

    public static IEnumerable<string> Corpora => LangBenchmarkSubset.Pick(nameof(Corpus), "all packs", "path-like");

    [GlobalSetup]
    public void Setup()
    {
        _flags = Arm switch
        {
            Path.Sparse => PerfFlags.Parse(PerfFlags.SparseUsageAggregation),
            Path.TermIndex => PerfFlags.Parse(PerfFlags.UsageTermIndex),
            Path.Both => PerfFlags.Parse($"{PerfFlags.SparseUsageAggregation},{PerfFlags.UsageTermIndex}"),
            _ => PerfFlags.None,
        };
        _terms = BuiltInDictionaryLibraries.All.SelectMany(pack => pack.Entries).ToList();
        var random = new Random(5_000);
        string[] prose = ["the", "and", "please", "send", "review", "tomorrow", "meeting", "OpenAI", "GitHub", "K8s,", "C#;", ".NET:", "e.g."];
        _history = new List<HistoryEntry>(5_000);
        for (var i = 0; i < 5_000; i++)
        {
            var words = new List<string>();
            for (var w = random.Next(8, 40); w > 0; w--)
            {
                if (Corpus == "path-like" && random.Next(6) == 0)
                {
                    // A delimiter-rich token: a path of known forms and noise.
                    words.Add(string.Join('/', Enumerable.Range(0, random.Next(8, 40)).Select(_ =>
                        random.Next(3) == 0 ? _terms[random.Next(_terms.Count)].Replacement.Replace(' ', '.') : "seg" + random.Next(100))));
                    continue;
                }

                if (random.Next(4) == 0)
                {
                    var term = _terms[random.Next(_terms.Count)];
                    words.Add(random.Next(2) == 0 ? term.Pattern : term.Replacement);
                }
                else
                {
                    words.Add(prose[random.Next(prose.Length)]);
                }
            }

            _history.Add(new HistoryEntry(i + 1, Now.AddMinutes(-random.Next(90 * 24 * 60)), string.Join(' ', words), 9_000, 300));
        }

        var expected = UsageAnalyzer.Compute(_history, _terms, Now.AddDays(-90), Now, null, TimeZoneInfo.Utc, 8, int.MaxValue);
        var actual = UsageAnalyzer.Compute(_history, _terms, Now.AddDays(-90), Now, null, TimeZoneInfo.Utc, 8, int.MaxValue, _flags);
        if (!expected.Terms.SequenceEqual(actual.Terms))
        {
            throw new InvalidOperationException($"The {Arm} counting disagrees with the old counting.");
        }
    }

    [Benchmark]
    public UsageAnalyzer.Snapshot Compute() =>
        UsageAnalyzer.Compute(_history, _terms, Now.AddDays(-90), Now, null, TimeZoneInfo.Utc, 8, 16, _flags);
}

/// <summary>
/// The case relations <see cref="SpokenFormFold"/> builds on its first use (combined.md row 14): the staged builder
/// against the builder it replaced, copied here as the old arm, both with the real hash.
/// </summary>
[MemoryDiagnoser]
public class LangCaseRelationBenchmarks
{
    [Benchmark(Baseline = true)]
    public int OldBuilder() => ReferenceBuildCaseRelated().Count;

    [Benchmark]
    public int StagedBuilder() =>
        SpokenFormFold.BuildCaseRelated(static c => string.GetHashCode(new ReadOnlySpan<char>(in c), StringComparison.OrdinalIgnoreCase)).Count;

    // SpokenFormFold.BuildCaseRelated before the staging change (a list for every distinct hash).
    private static Dictionary<char, List<char>> ReferenceBuildCaseRelated()
    {
        var related = new Dictionary<char, List<char>>();
        void Link(char a, char b)
        {
            if (a == b)
            {
                return;
            }

            Add(a, b);
            Add(b, a);
        }

        void Add(char from, char to)
        {
            if (!related.TryGetValue(from, out var list))
            {
                related[from] = list = [];
            }

            if (!list.Contains(to))
            {
                list.Add(to);
            }
        }

        var buckets = new Dictionary<int, List<char>>();
        for (var i = 0; i <= char.MaxValue; i++)
        {
            var c = (char)i;
            if (char.IsSurrogate(c))
            {
                continue;
            }

            Link(c, char.ToUpperInvariant(c));
            Link(c, char.ToLowerInvariant(c));
            var hash = string.GetHashCode(new ReadOnlySpan<char>(in c), StringComparison.OrdinalIgnoreCase);
            if (!buckets.TryGetValue(hash, out var bucket))
            {
                buckets[hash] = bucket = [];
            }

            bucket.Add(c);
        }

        foreach (var bucket in buckets.Values.Where(b => b.Count > 1))
        {
            for (var i = 0; i < bucket.Count; i++)
            {
                for (var j = i + 1; j < bucket.Count; j++)
                {
                    if (string.Equals(bucket[i].ToString(), bucket[j].ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        Link(bucket[i], bucket[j]);
                    }
                }
            }
        }

        Link('\u0130', 'i');
        Link('\u0131', 'i');
        return related;
    }
}

/// <summary>
/// Lets one run take a subset of a parameter's values, so each run in the shared bench lane stays under a minute: an
/// environment variable named SCRIBE_BENCH_ and the parameter's name in capitals (SCRIBE_BENCH_CHARACTERS) holds the values
/// to keep, separated by commas, compared without case. Unset or empty keeps every value, so a plain run measures them all,
/// and so does a list that names none of them: BenchmarkDotNet reads every class's sources, and two classes have an Arm.
/// </summary>
internal static class LangBenchmarkSubset
{
    public static IEnumerable<T> Pick<T>(string parameter, params T[] values)
    {
        var only = Environment.GetEnvironmentVariable("SCRIBE_BENCH_" + parameter.ToUpperInvariant());
        if (string.IsNullOrWhiteSpace(only))
        {
            return values;
        }

        var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = values
            .Where(value => wanted.Contains(Convert.ToString(value, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase))
            .ToList();
        return kept.Count > 0 ? kept : values;
    }
}