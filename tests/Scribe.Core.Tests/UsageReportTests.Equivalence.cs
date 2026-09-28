using Scribe.Core.Diagnostics;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using static Scribe.Core.Tests.Libraries.Composition.Lib;
using Oracle = Scribe.Core.Tests.UsageAnalyzerMemoryTests.Oracle;

namespace Scribe.Core.Tests;

// The report half of the usage merge gates (combined.md rows 1 and 2; sign-off SIG-LANG-01): the whole report, its
// snapshot and the scope its shareable labels bind to, is what the report gave when its snapshot came from UsageAnalyzer
// as it was at 10c9a0b (UsageAnalyzerMemoryTests.Oracle).
public sealed partial class UsageReportTests
{
    [Fact]
    public void Cheap_path_preserves_snapshot_and_scope()
    {
        UsageReport.SnapshotComputation legacy = (entries, knownTerms, since, now, mayShare) =>
            Oracle.Compute(entries, knownTerms, since, now, mayShare);
        var random = new Random(52_019);
        var shipped = BuiltInDictionaryLibraries.All.SelectMany(library => library.Entries).ToList();
        foreach (var culture in UsageEquivalenceCorpus.Cultures)
        {
            UsageEquivalenceCorpus.InCulture(culture, () =>
            {
                for (var round = 0; round < 8; round++)
                {
                    var (vocabulary, libraryForms) = Vocabulary(random, shipped);
                    DictionaryEntry[] dictionary =
                    [
                        DictionaryEntry.New("contoso", "Contoso"), DictionaryEntry.New("deny alias", "Widget"),
                        DictionaryEntry.New("sig", "Best,\nChris"), DictionaryEntry.New("kube", "Kubernetes"),
                    ];
                    var history = History(random, 120, [.. libraryForms, .. dictionary.Select(entry => entry.Pattern), "Contoso", "Kubernetes"]);
                    foreach (var periodDays in new int?[] { 7, null })
                    {
                        var expected = UsageReport.Build(
                            _ => history, () => dictionary, () => vocabulary, periodDays, Now, CancellationToken.None, legacy);

                        // Row 1's code with no flag on, then every combination of the counting flags (rows 2, 8 and 12).
                        foreach (var flags in UsageEquivalenceCorpus.FlagCombinations)
                        {
                            var actual = UsageReport.Build(
                                _ => history, () => dictionary, () => vocabulary, periodDays, Now, CancellationToken.None,
                                UsageReport.Analyzer(PerfFlags.Parse(flags)));
                            var context = $"{culture} round {round} period {periodDays?.ToString() ?? "all"} flags '{flags}'";

                            UsageEquivalenceCorpus.AssertSameSnapshot(expected.Snapshot, actual.Snapshot, context);
                            Assert.Equal(expected.PeriodCapped, actual.PeriodCapped);
                            Assert.Equal(expected.LibraryScope.Generation, actual.LibraryScope.Generation);
                            Assert.Equal(
                                expected.LibraryScope.PermittedContent.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                                actual.LibraryScope.PermittedContent.OrderBy(pair => pair.Key, StringComparer.Ordinal));
                        }

                        if (round == 0 && periodDays is null)
                        {
                            // The comparison reaches a shareable library label, so the scope is not trivially empty.
                            Assert.NotEmpty(expected.LibraryScope.PermittedContent);
                        }
                    }
                }
            });
        }
    }

    // Three custom libraries over a slice of the shipped rows, each on, and permitted for AI cleanup at random (the first
    // always), so labels come from permitted, withheld and dictionary terms alike.
    private static (LibraryVocabulary Vocabulary, List<string> Forms) Vocabulary(Random random, IReadOnlyList<DictionaryEntry> shipped)
    {
        var forms = new List<string>();
        var libraries = new List<CatalogLibrary>();
        var permissions = new List<(string, bool)>();
        var accepted = new List<(string, LibraryContentHash)>();
        LibraryContentHash[] hashes = [H1, H2, H3];
        for (var l = 0; l < 3; l++)
        {
            var id = "pack" + l;
            var rows = Enumerable.Range(0, 40)
                .Select(_ => shipped[random.Next(shipped.Count)])
                .Select(entry => Custom(entry.Pattern, entry.Replacement, entry.WholeWord))
                .ToArray();
            forms.AddRange(rows.Select(row => row.Values.Spoken).Where(_ => random.Next(2) == 0));
            libraries.Add(Committed(CustomLibrary(id, rows), hashes[l]));
            permissions.Add((id, l == 0 || random.Next(2) == 0));
            accepted.Add((id, hashes[l]));
        }

        var catalog = Catalog(
            State(enabled: ["pack0", "pack1", "pack2"], ai: [.. permissions], accepted: [.. accepted]), [.. libraries]);
        return (LibraryComposer.Instance.ComposeVocabulary(catalog), forms);
    }

    private static List<HistoryEntry> History(Random random, int count, IReadOnlyList<string> forms)
    {
        var entries = new List<HistoryEntry>(count);
        string[] plain = ["the", "and", "please", "send", "review", "OpenAI", "GitHub", "CloudThing", "\u212Aube"];
        for (var i = 0; i < count; i++)
        {
            var words = Enumerable.Range(0, random.Next(3, 20))
                .Select(_ => random.Next(2) == 0 && forms.Count > 0 ? forms[random.Next(forms.Count)] : plain[random.Next(plain.Length)]);
            entries.Add(new HistoryEntry(
                i + 1, Now.AddHours(-random.Next(24 * 30)), string.Join(random.Next(3) == 0 ? ", " : " ", words), 1_000, 100,
                TargetApp: random.Next(2) == 0 ? "notepad" : null));
        }

        return [.. entries.OrderByDescending(entry => entry.TimestampUtc)];
    }
}
