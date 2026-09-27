using System.Globalization;
using BenchmarkDotNet.Attributes;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;

namespace Scribe.Benchmarks;

/// <summary>
/// "Search all word packs" and the terms grid's Sort menu at the sizes plan 3.14 names: the 1,549 shipped rows, and
/// 10,000 and 100,000 library terms (the shipped rows plus custom libraries of up to 10,000 terms each). The search
/// runs on every debounced keystroke (150 ms), so its time at 100,000 terms is what decides whether the page needs
/// anything smarter than a scan.
/// </summary>
[MemoryDiagnoser]
public class LibrarySearchBenchmarks
{
    private const int TermsPerCustomLibrary = 10_000;

    private LibraryWorkspace _workspace = null!;
    private LibrarySearch _search = null!;
    private LibraryTermSort _sort = null!;
    private IReadOnlyList<DraftTermRow> _largest = [];

    [Params(1_549, 10_000, 100_000)]
    public int Terms { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var libraries = new List<CatalogLibrary>();
        var total = 0;
        foreach (var shipped in BuiltInDictionaryLibraries.All)
        {
            var rows = shipped.Entries
                .Select(entry => TermValues.FromEntry(entry))
                .Select(values => new LibraryRow(LibraryTermKey.From(values.Spoken), values, TermOrigin.Shipped, values))
                .ToList();
            total += rows.Count;
            libraries.Add(new CatalogLibrary(
                new LibraryContent(shipped.Id, true, shipped.Name, shipped.Category, shipped.Description, rows),
                LibraryFileState.Available, null, null));
        }

        for (var library = 0; total < Terms; library++)
        {
            var count = Math.Min(TermsPerCustomLibrary, Terms - total);
            var rows = Enumerable.Range(0, count)
                .Select(i => LibraryRow.Custom(new TermValues($"team term {library} {i}", i % 7 == 0 ? $"GitHub term {i}" : $"Term {library}-{i}")))
                .ToList();
            var id = $"custom-team-{library}";
            libraries.Add(new CatalogLibrary(
                new LibraryContent(id, false, $"Team {library}", "Custom", null, rows), LibraryFileState.Available, id + ".csv", null));
            total += count;
        }

        var ids = libraries.Select(library => library.Content.Id).ToList();
        var catalog = new LibraryCatalog(
            1,
            LibraryPrecedence.Order(libraries, library => library.Content.Id, library => library.Content.BuiltIn, library => library.FileName).ToList(),
            LibraryLocalState.Create(ids, ids, null, null, null, LocalStateHealth.Ok),
            [],
            [],
            filesAwaitingRelease: 0);
        _workspace = new LibraryWorkspace(catalog, new UnusedOverlay(), (_, builtIn, _) => builtIn);
        _search = LibrarySearch.For(CultureInfo.GetCultureInfo("en-US"));
        _sort = LibraryTermSort.For(CultureInfo.GetCultureInfo("en-US"));
        _largest = libraries.OrderByDescending(library => library.Content.Rows.Count).Select(library => _workspace.RowsOf(library.Content.Id)).First();
        _ = _workspace.Draft;
    }

    [Benchmark]
    [BenchmarkCategory("Libraries")]
    public LibrarySearchResult SearchCommonTerm() => _search.Search(_workspace, "github");

    [Benchmark]
    [BenchmarkCategory("Libraries")]
    public LibrarySearchResult SearchAccentedNoMatch() => _search.Search(_workspace, "crème brûlée");

    [Benchmark]
    [BenchmarkCategory("Libraries")]
    public IReadOnlyList<DraftTermRow> SortLargestLibraryBySpoken() => _sort.Sort(_largest, LibraryTermSortOrder.SpokenAscending);

    // Searching and sorting never change a built-in row, so the workspace is never asked to.
    private sealed class UnusedOverlay : IBuiltInLibraryOverlay
    {
        public BuiltInEditsReadResult ReadEdits(string libraryId, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();

        public byte[] WriteEdits(BuiltInLibraryEdits edits) => throw new NotSupportedException();

        public IReadOnlyList<LibraryRow> Apply(DictionaryLibrary shipped, BuiltInLibraryEdits? edits) => throw new NotSupportedException();

        public LibraryRow Edit(LibraryRow row, TermValues values) => throw new NotSupportedException();

        public LibraryRow SetEnabled(LibraryRow row, bool enabled) => throw new NotSupportedException();

        public LibraryRow? RestoreShipped(LibraryRow row) => throw new NotSupportedException();

        public LibraryRow Add(TermValues values) => throw new NotSupportedException();

        public LibraryRow ResolveReview(LibraryRow row, TermReviewChoice choice) => throw new NotSupportedException();

        public BuiltInLibraryEdits? Collect(DictionaryLibrary shipped, BuiltInLibraryEdits? committed, IReadOnlyList<LibraryRow> rows) =>
            throw new NotSupportedException();

        public IReadOnlyList<TermValues> AuthoredTerms(BuiltInLibraryEdits edits) => throw new NotSupportedException();
    }
}
