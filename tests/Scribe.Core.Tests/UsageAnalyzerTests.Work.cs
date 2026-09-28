using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Tests.Libraries.Composition;
using static Scribe.Core.Tests.TextPostProcessorReferencePipelineTests;

namespace Scribe.Core.Tests;

// The usage counting flags' work (review finding LANG-IR-01): the output oracles in UsageAnalyzerTests.Equivalence.cs pass
// with either change gone, so these count what the counting did (UsageAnalyzer.UsageWork). The first two call the core
// overload, which returns the work; the third takes the Usage page's own route, UsageReport.Build with the flags, and
// observes the work there (UsageAnalyzer.ObserveWork), so a hop that drops the flags fails it too (review round 2). Each
// expected count is worked out by hand from the fixture.
public sealed partial class UsageAnalyzerTests
{
    private static readonly PerfFlags SparseOnly = PerfFlags.Parse(PerfFlags.SparseUsageAggregation);
    private static readonly PerfFlags TermIndexOnly = PerfFlags.Parse(PerfFlags.UsageTermIndex);

    [Fact]
    public void Sparse_aggregation_reaches_only_the_terms_a_dictation_names_and_probes_no_other_term_s_forms()
    {
        var (terms, texts) = SparseFixture();
        var history = UsageEquivalenceCorpus.History(texts);

        UsageAnalyzer.UsageWork Work(PerfFlags flags, out UsageAnalyzer.Snapshot snapshot)
        {
            snapshot = UsageAnalyzer.Compute(
                history, terms, UsageEquivalenceCorpus.Now.AddDays(-90), UsageEquivalenceCorpus.Now, UsageEquivalenceCorpus.MayShare,
                TimeZoneInfo.Utc, 8, int.MaxValue, flags, out var work);
            return work;
        }

        var off = Work(PerfFlags.None, out var expected);
        Assert.Contains(expected.Terms, term => term.Covered);

        // Off: every form of every term (122 terms, 244 forms) is looked up in every dictation.
        Assert.Equal(30 * 244L, off.DenseFormProbes);
        Assert.Equal(0, off.OwnerVisits);

        // On: no dense lookup at all; each dictation reaches its two terms, and "quux" its two owners (three dictations).
        foreach (var flags in new[] { SparseOnly, PerfFlags.Parse($"{PerfFlags.SparseUsageAggregation},{PerfFlags.UsageTermIndex}") })
        {
            var on = Work(flags, out var actual);
            UsageEquivalenceCorpus.AssertSameSnapshot(expected, actual, $"flags '{string.Join(",", flags.On)}'");
            Assert.Equal(0, on.DenseFormProbes);
            Assert.Equal((30 * 2) + (3 * 2), on.OwnerVisits);
        }

        // UsageTermIndex alone leaves the aggregation dense.
        Assert.Equal(30 * 244L, Work(TermIndexOnly, out _).DenseFormProbes);
    }

    [Fact]
    public void The_term_index_runs_a_phrase_regex_only_on_the_dictations_the_search_finds_it_in()
    {
        var (terms, texts) = TermIndexFixture();
        var history = UsageEquivalenceCorpus.History(texts);

        UsageAnalyzer.UsageWork Work(PerfFlags flags, out UsageAnalyzer.Snapshot snapshot)
        {
            snapshot = UsageAnalyzer.Compute(
                history, terms, UsageEquivalenceCorpus.Now.AddDays(-90), UsageEquivalenceCorpus.Now, UsageEquivalenceCorpus.MayShare,
                TimeZoneInfo.Utc, 8, int.MaxValue, flags, out var work);
            return work;
        }

        var off = Work(PerfFlags.None, out var expected);
        Assert.Contains(expected.Terms, term => term.Covered);
        Assert.Equal((4, 5L * 4), (off.PhraseRegexesBuilt, off.PhraseRegexRuns));
        Assert.Equal(20L, Work(SparseOnly, out _).PhraseRegexRuns);

        foreach (var flags in new[] { TermIndexOnly, PerfFlags.Parse($"{PerfFlags.UsageTermIndex},{PerfFlags.SparseUsageAggregation}") })
        {
            var on = Work(flags, out var actual);
            UsageEquivalenceCorpus.AssertSameSnapshot(expected, actual, $"flags '{string.Join(",", flags.On)}'");
            Assert.Equal(2L + 3 + 1 + 4 + 1, on.PhraseRegexRuns);
        }

        // Without the dictation that is not sound, "kube ctl" is never needed, so its regex is never built.
        var sound = UsageEquivalenceCorpus.History([.. texts.Where(text => !text.Contains('\u212A'))]);
        var onSound = UsageAnalyzer.Compute(
            sound, terms, UsageEquivalenceCorpus.Now.AddDays(-90), UsageEquivalenceCorpus.Now, UsageEquivalenceCorpus.MayShare,
            TimeZoneInfo.Utc, 8, int.MaxValue, TermIndexOnly, out var soundWork);
        Assert.Contains(onSound.Terms, term => term.Covered);
        Assert.Equal((3, 2L + 3 + 1 + 1), (soundWork.PhraseRegexesBuilt, soundWork.PhraseRegexRuns));
    }

    [Theory]
    [InlineData("")]
    [InlineData(PerfFlags.SparseUsageAggregation)]
    [InlineData(PerfFlags.UsageTermIndex)]
    [InlineData(PerfFlags.SparseUsageAggregation + "," + PerfFlags.UsageTermIndex)]
    public void The_usage_page_s_report_does_the_counting_work_its_flags_choose(string flags)
    {
        // The route the Usage page takes: UsageReport.Build with the process's flags, as the Settings window calls it (with
        // the library service, a vocabulary source), then UsageReport's analyzer, then the ordinary Compute, then the core.
        // Every hop that must hand the flags on is between the call and the counts observed here; the counts are the core
        // tests' own.
        var perfFlags = PerfFlags.Parse(flags);
        var sparse = perfFlags.IsOn(PerfFlags.SparseUsageAggregation);
        var termIndex = perfFlags.IsOn(PerfFlags.UsageTermIndex);

        var (sparseTerms, sparseTexts) = SparseFixture();
        var aggregation = ThroughTheUsagePage(sparseTexts, sparseTerms, perfFlags);
        Assert.Equal(sparse ? 0 : 30 * 244L, aggregation.DenseFormProbes);
        Assert.Equal(sparse ? (30 * 2) + (3 * 2) : 0, aggregation.OwnerVisits);

        var (phraseTerms, phraseTexts) = TermIndexFixture();
        var phrases = ThroughTheUsagePage(phraseTexts, phraseTerms, perfFlags);
        Assert.Equal(termIndex ? 2L + 3 + 1 + 4 + 1 : 5L * 4, phrases.PhraseRegexRuns);
        Assert.Equal(4, phrases.PhraseRegexesBuilt);
    }

    // 120 terms with two single-token forms of their own ("zorblat7" spoken, "Zorblat7x" written), and two more sharing the
    // spoken form "quux". Each of 30 dictations names two of the terms, and every tenth also says "quux".
    private static (List<DictionaryEntry> Terms, List<string> Texts) SparseFixture()
    {
        var terms = new List<DictionaryEntry>();
        for (var i = 0; i < 120; i++)
        {
            terms.Add(DictionaryEntry.New($"zorblat{i}", $"Zorblat{i}x"));
        }

        terms.Add(DictionaryEntry.New("quux", "Quuxa"));
        terms.Add(DictionaryEntry.New("quux", "Quuxb"));
        var texts = Enumerable.Range(0, 30)
            .Select(d => $"please file zorblat{d} and Zorblat{d + 40}x today" + (d % 10 == 0 ? ", quux" : string.Empty))
            .ToList();
        return (terms, texts);
    }

    // Four phrases (the written forms are single tokens): three ASCII, which the search judges, and the non-ASCII
    // "caf\u00e9 au lait", which runs on every dictation, as every phrase does in a dictation holding U+212A.
    private static (List<DictionaryEntry> Terms, List<string> Texts) TermIndexFixture()
    {
        List<DictionaryEntry> terms =
        [
            DictionaryEntry.New("next js", "Nextjs"), DictionaryEntry.New("dev ops", "DevOps"),
            DictionaryEntry.New("kube ctl", "Kubectl"), DictionaryEntry.New("caf\u00e9 au lait", "Coffee"),
        ];
        List<string> texts =
        [
            "we use next js here",          // next js and the non-ASCII phrase: 2
            "dev ops and NEXT JS",          // next js, dev ops and the non-ASCII phrase: 3
            "plain words only",             // the non-ASCII phrase: 1
            "the \u212Aube ctl tool",       // not sound: all 4
            "caf\u00e9 au lait please",     // the non-ASCII phrase: 1
        ];
        return (terms, texts);
    }

    // One Usage page load of these dictations, with these terms as the saved dictionary and no word pack: the report the
    // window builds, and the work its one term count did.
    private static UsageAnalyzer.UsageWork ThroughTheUsagePage(IReadOnlyList<string> texts, IReadOnlyList<DictionaryEntry> terms, PerfFlags flags)
    {
        var observed = new List<UsageAnalyzer.UsageWork>();
        UsageReport.Result report;
        using (UsageAnalyzer.ObserveWork(observed.Add))
        {
            report = UsageReport.Build(
                new RecentHistory(UsageEquivalenceCorpus.History(texts)), new DictionaryStub(terms), new FakeVocabularySource(LibraryVocabulary.Empty),
                enabledLibraryIds: [], periodDays: 90, UsageEquivalenceCorpus.Now, CancellationToken.None, flags);
        }

        Assert.Contains(report.Snapshot.Terms, term => term.Covered);
        return Assert.Single(observed);
    }

    // Retained history as the report reads it: the newest entries first, up to the limit asked for.
    private sealed class RecentHistory(IReadOnlyList<HistoryEntry> entries) : IHistoryRepository
    {
        public HistoryEntry Add(HistoryEntry entry) => throw new NotSupportedException();

        public HistoryEntry Add(HistoryEntry entry, CapturedAudio? audio) => throw new NotSupportedException();

        public long AddAudioBlob(CapturedAudio audio) => throw new NotSupportedException();

        public IReadOnlyList<HistoryEntry> GetRecent(int limit = 100) => [.. entries.Take(limit)];

        public IReadOnlyList<HistoryEntry> GetOlder(DateTimeOffset beforeUtc, long beforeId, int limit) => throw new NotSupportedException();

        public IReadOnlyList<HistoryEntry> Search(string query, int limit) => throw new NotSupportedException();

        public CapturedAudio? GetAudio(long blobId) => throw new NotSupportedException();

        public void SetAiRating(long id, AiRating rating) => throw new NotSupportedException();

        public void Delete(long id) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public int PruneOlderThan(DateTimeOffset cutoffUtc) => throw new NotSupportedException();
    }
}
