using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Benchmarks;

/// <summary>
/// The dictionary cleanup's scan as Settings runs it (your words only, the last 1,000 dictations): public
/// <see cref="DictionaryUsageAnalyzer.Analyze(IReadOnlyList{string}, IReadOnlyList{DictionaryEntry}, IReadOnlyList{DictionaryLibrary}, int, int)"/>
/// with no flag, so this same file runs on the baseline (77b22af, a regex per term) and on the change (a term an ordinal
/// search cannot find is counted as 0 without one). A sixth of the dictionary is used; the rest never turns up, which is
/// what the scan is for.
/// </summary>
[MemoryDiagnoser]
public class LangCleanupScanBenchmarks
{
    private List<string> _transcripts = null!;
    private List<DictionaryEntry> _dictionary = null!;

    [Params(250, 1_500)]
    public int Entries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var shipped = BuiltInDictionaryLibraries.All
            .SelectMany(pack => pack.Entries)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Pattern) && !string.IsNullOrWhiteSpace(entry.Replacement))
            .DistinctBy(entry => entry.Pattern.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        _dictionary = [];
        for (var copy = 0; _dictionary.Count < Entries; copy++)
        {
            foreach (var entry in shipped.Take(Entries - _dictionary.Count))
            {
                var suffix = copy == 0 ? string.Empty : $" {copy}";
                _dictionary.Add(entry with { Id = _dictionary.Count + 1, Pattern = entry.Pattern + suffix, Replacement = entry.Replacement + suffix });
            }
        }

        var used = _dictionary.Take(Entries / 6).ToList();
        var random = new Random(1_000);
        string[] prose = ["the", "and", "please", "send", "review", "tomorrow", "meeting", "we", "should", "ship", "it", "today", "K8s", "GPT4"];
        _transcripts = new List<string>(1_000);
        for (var i = 0; i < 1_000; i++)
        {
            var words = new List<string>();
            for (var w = random.Next(12, 40); w > 0; w--)
            {
                if (random.Next(6) == 0)
                {
                    var term = used[random.Next(used.Count)];
                    words.Add(random.Next(2) == 0 ? term.Pattern : term.Replacement);
                }
                else
                {
                    words.Add(prose[random.Next(prose.Length)]);
                }
            }

            _transcripts.Add(string.Join(' ', words));
        }

        if (!Scan().HasEnoughEvidence)
        {
            throw new InvalidOperationException("The corpus is too small for the scan to judge anything.");
        }
    }

    [Benchmark]
    public DictionaryUsageReport Scan() => DictionaryUsageAnalyzer.Analyze(_transcripts, _dictionary, Array.Empty<DictionaryLibrary>());
}

/// <summary>
/// The suggestion miner's shape test, whose letter-digit pattern takes one digit where it had a run (the same tokens match):
/// <see cref="DictionarySuggestionMiner.IsCandidate(string)"/> over 2,000 tokens of prose and jargon, and Learn from
/// history's <see cref="DictionarySuggestionMiner.Mine"/> over 5,000 dictations. No flag, so this same file runs on the
/// baseline (77b22af) and on the change.
/// </summary>
[MemoryDiagnoser]
public class LangMinerBenchmarks
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private string[] _tokens = null!;
    private List<HistoryEntry> _history = null!;

    [GlobalSetup]
    public void Setup()
    {
        string[] words =
        [
            "the", "and", "please", "review", "meeting", "tomorrow", "pipeline", "deployed", "K8s", "S3", "net10", "GPT4", "gpt4o",
            "Win11", "OpenAI", "GitHub", "ReBAC", "iOS", "TODO", "OK", "ASAP", ".NET", "x64", "arm64", "v2", "H100",
        ];
        var random = new Random(2_000);
        _tokens = [.. Enumerable.Range(0, 2_000).Select(_ => words[random.Next(words.Length)])];
        _history = new List<HistoryEntry>(5_000);
        for (var i = 0; i < 5_000; i++)
        {
            var text = string.Join(' ', Enumerable.Range(0, random.Next(8, 40)).Select(_ => words[random.Next(words.Length)]));
            _history.Add(new HistoryEntry(i + 1, Now.AddMinutes(-random.Next(90 * 24 * 60)), text, 9_000, 300));
        }
    }

    [Benchmark]
    public int JudgeTokens()
    {
        var candidates = 0;
        foreach (var token in _tokens)
        {
            if (DictionarySuggestionMiner.IsCandidate(token))
            {
                candidates++;
            }
        }

        return candidates;
    }

    [Benchmark]
    public int LearnFromHistory() => DictionarySuggestionMiner.Mine(_history, []).Count;
}

/// <summary>
/// The dictionary pass over every shipped word pack plus a small dictionary (1,554 input rows, which compile to 1,334
/// rules), built without naming a flag: the workload of <c>LangMatcherPassBenchmarks</c>' Old arm, so this same file runs on
/// the baseline (77b22af) and on the change, where MatcherPrefilter is off by default, and shows the switched-off path costs
/// what the old one did. Each text is cut at the last space at or before <see cref="MaxCharacters"/>; the setup prints what
/// its case compiled and cut.
/// </summary>
[MemoryDiagnoser]
public class LangMatcherFlagOffBenchmarks
{
    private TextPostProcessor _processor = null!;
    private CompiledDictionaryRules _rules = null!;
    private string _cleaned = null!;
    private string _raw = null!;

    [Params(130, 1865)]
    public int MaxCharacters { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var library = BuiltInDictionaryLibraries.All.SelectMany(pack => pack.Entries).ToList();
        DictionaryEntry[] dictionary =
        [
            DictionaryEntry.New("dot net", ".NET"), DictionaryEntry.New("york", "New York"), DictionaryEntry.New("comma", ","),
            DictionaryEntry.New("github", "GitHub"), DictionaryEntry.New("azure", "Azure"),
        ];
        _processor = new TextPostProcessor(new RepresentativeWorkload.DictionaryStub(dictionary), NullLogger<TextPostProcessor>.Instance);
        _rules = _processor.Compile(dictionary, library);

        const string raw = "so i pushed the dot net api changes to github and the azure devops pipeline ran the tests before the blazor front end deployed then we flew to york ";
        const string cleaned = "So I pushed the .NET API changes to GitHub, and the Azure DevOps pipeline ran the tests before the Blazor front end deployed. Then we flew to New York. ";
        _raw = Repeat(raw, MaxCharacters);
        _cleaned = Repeat(cleaned, MaxCharacters);
        Console.WriteLine(
            $"// {nameof(LangMatcherFlagOffBenchmarks)}: {dictionary.Length + library.Count} input rows compiled to {_rules.Count} rules; " +
            $"raw text {_raw.Length} and cleaned text {_cleaned.Length} characters (limit {MaxCharacters}).");
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