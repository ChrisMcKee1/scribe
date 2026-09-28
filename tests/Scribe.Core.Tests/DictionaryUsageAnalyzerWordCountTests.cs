using System.Text.RegularExpressions;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public partial class DictionaryUsageAnalyzerWordCountTests
{
    private static readonly string?[] Edges =
    [
        null, string.Empty, " ", "\t\n", "word", "don't", "don\u2019t", "'quoted'", "\u2019tis", "-leading", "trailing-",
        "state-of-the-art", "a--b", "a''b", "x'", "12 34", "1,000", "3.14", "e\u0301te", "\u00E9t\u00E9", "\uD835\uDCB3yz",
        "\u4E2D\u6587 \u65E5\u672C", "\u0661\u0662\u0663", "K8s, .NET! C# e.g. node.js", "#hash @at under_score", "a\u00A0b",
        "line one\nline two\r\nline three", "--", "'''", "can't won't shan't", "Ab\u0301c",
    ];

    [Fact]
    public void Words_are_counted_as_the_previous_implementation_counted_them()
    {
        foreach (var transcript in Edges)
        {
            Assert.Equal(OracleWords([transcript]), Scanned([transcript]));
        }

        var generated = Generated(seed: 3, count: 400);
        Assert.Equal(OracleWords(generated), Scanned(generated));
        Assert.Equal(OracleWords([.. Edges, .. generated]), Scanned([.. Edges, .. generated]));
    }

    [Fact]
    public void The_evidence_threshold_turns_at_the_same_word()
    {
        var transcripts = Generated(seed: 4, count: 60);
        var words = OracleWords(transcripts);

        Assert.True(DictionaryUsageAnalyzer.Analyze(transcripts!, [], [], minimumTranscripts: 1, minimumWords: words).HasEnoughEvidence);
        Assert.False(DictionaryUsageAnalyzer.Analyze(transcripts!, [], [], minimumTranscripts: 1, minimumWords: words + 1).HasEnoughEvidence);
    }

    // In the collection that runs alone: no other test allocates on this thread while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        [Fact]
        public void Counting_words_no_longer_builds_a_match_for_each_one()
        {
            // Too few dictations for a verdict, so the scan stops right after counting: what it allocates then is its lists
            // and the report, where the previous count built a Match, with its group arrays, for every word.
            var transcripts = Generated(seed: 5, count: 500);
            _ = DictionaryUsageAnalyzer.Analyze(transcripts!, [], [], minimumTranscripts: int.MaxValue);
            _ = OracleWords(transcripts);

            var oracleBefore = GC.GetAllocatedBytesForCurrentThread();
            var expected = OracleWords(transcripts);
            var oracleBytes = GC.GetAllocatedBytesForCurrentThread() - oracleBefore;

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var report = DictionaryUsageAnalyzer.Analyze(transcripts!, [], [], minimumTranscripts: int.MaxValue);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            Assert.Equal(expected, report.WordsScanned);
            Assert.True(
                allocated * 20 < oracleBytes,
                $"{allocated} bytes for the scan against {oracleBytes} for the previous word count alone. During it: {during}.");
        }
    }

    private static int Scanned(IReadOnlyList<string?> transcripts) =>
        DictionaryUsageAnalyzer.Analyze(transcripts!, Array.Empty<DictionaryEntry>(), Array.Empty<DictionaryLibrary>()).WordsScanned;

    // The count as it was: every usable transcript's words, one Match each.
    private static int OracleWords(IReadOnlyList<string?> transcripts) =>
        transcripts.Where(t => !string.IsNullOrWhiteSpace(t)).Sum(t => OracleWordLike().Matches(t!).Count);

    private static List<string?> Generated(int seed, int count)
    {
        var random = new Random(seed);
        string[] pieces =
        [
            "please", "send", "the", "draft", "don't", "can\u2019t", "state-of-the-art", "K8s", "GPT-4", "e.g.", "12", "3.5",
            "\u00E9t\u00E9", "na\u00EFve", "-", "'", "\u2019", "a--b", "x'y", "\uD835\uDCB3", "\u4E2D\u6587",
        ];
        var transcripts = new List<string?>(count);
        for (var i = 0; i < count; i++)
        {
            var words = Enumerable.Range(0, random.Next(40)).Select(_ => pieces[random.Next(pieces.Length)]);
            transcripts.Add(random.Next(15) == 0 ? null : string.Join(random.Next(3) == 0 ? ", " : " ", words));
        }

        return transcripts;
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*")]
    private static partial Regex OracleWordLike();
}
