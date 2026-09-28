using BenchmarkDotNet.Attributes;
using Scribe.Core.Diagnostics;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Benchmarks;

/// <summary>
/// A complete Usage page snapshot over 5,000 synthetic histories with every word pack's terms known (combined.md row 1),
/// through the internal overload both trees have, with no flag: this same file runs on the baseline (phrase regexes
/// compiled) and on the change (interpreted), so the two builds measure one source.
/// </summary>
[MemoryDiagnoser]
public class LangUsageRow1Benchmarks
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private List<HistoryEntry> _history = null!;
    private List<DictionaryEntry> _terms = null!;

    [Params("all packs", "default packs")]
    public string Terms { get; set; } = "all packs";

    [GlobalSetup]
    public void Setup()
    {
        var packs = Terms == "all packs"
            ? BuiltInDictionaryLibraries.All
            : BuiltInDictionaryLibraries.All.Where(pack => AppSettings.DefaultLibraryIds.Contains(pack.Id));
        _terms = packs.SelectMany(pack => pack.Entries).ToList();
        var random = new Random(5_000);
        string[] prose = ["the", "and", "please", "send", "review", "tomorrow", "meeting", "OpenAI", "GitHub", "K8s,", "C#;", ".NET:", "e.g."];
        _history = new List<HistoryEntry>(5_000);
        for (var i = 0; i < 5_000; i++)
        {
            var words = new List<string>();
            for (var w = random.Next(8, 40); w > 0; w--)
            {
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
    }

    [Benchmark]
    public UsageAnalyzer.Snapshot Compute() =>
        UsageAnalyzer.Compute(_history, _terms, Now.AddDays(-90), Now, null, TimeZoneInfo.Utc, 8, 16);
}
