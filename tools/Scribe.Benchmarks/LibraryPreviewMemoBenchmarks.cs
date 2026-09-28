using BenchmarkDotNet.Attributes;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Benchmarks;

/// <summary>
/// What the Settings window pays for its library previews, over the catalogs of <see cref="LibraryCompositionBenchmarks"/>
/// (the 1,549 shipped rows with every built-in on, then custom libraries of 1,000 rows up to 10,000 and 100,000 terms).
/// The Word packs page composes a preview at every debounced search refresh and every edit, and Your words one at every
/// refresh of its coverage badges and glossary count, each against the draft of the current revision and dictionary
/// entries built afresh from the grid: equal values, other objects.
/// <para>
/// The Hit arms ask for a preview Preview has kept: HitRepeatedPreview is a refresh that changed nothing the preview reads
/// (a search keystroke), HitRepeatedPreviewWithStatuses adds the status the page reads for every row of the selected
/// library, and HitTwoCallers is both pages refreshing against one draft (the grid's dictionary order with the saved
/// budget, and the dictionary sorted with the budget on screen). The Miss arm, MissNewDraftPreview, asks for the preview
/// of a revision seen for the first time, as after an edit, which composes and keeps it; it includes building that draft.
/// Setup composes what the window composed when the draft appeared. A fresh composition on its own is
/// <see cref="LibraryCompositionBenchmarks.Preview"/>.
/// </para>
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Libraries")]
public class LibraryPreviewMemoBenchmarks
{
    private const int CustomLibraryRows = 1_000;

    private LibraryCatalog _catalog = null!;
    private LibraryDraft _draft = null!;
    private IReadOnlyList<DraftLibrary> _draftLibraries = [];
    private IReadOnlyList<DictionaryEntry> _grid = [];
    private IReadOnlyList<DictionaryEntry> _sorted = [];
    private string _selectedId = "";
    private IReadOnlyList<LibraryTermKey> _selectedKeys = [];
    private readonly GlossaryBudget _savedBudget = new(CleanupPrompt.MaxGlossaryTermsLocal);
    private readonly GlossaryBudget _onScreenBudget = new(CleanupPrompt.MaxGlossaryTermsCloud);

    [Params(1_549, 10_000, 100_000)]
    public int Terms { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // The same catalog as LibraryCompositionBenchmarks builds.
        var shippedForms = BuiltInDictionaryLibraries.All.SelectMany(l => l.Entries).Select(e => e.Pattern).ToList();
        var libraries = BuiltInDictionaryLibraries.All
            .Select(library => new CatalogLibrary(
                new LibraryContent(library.Id, true, library.Name, library.Category, library.Description,
                    [.. library.Entries.Select(entry =>
                    {
                        var values = TermValues.FromEntry(entry);
                        return new LibraryRow(LibraryTermKey.From(values.Spoken), values, TermOrigin.Shipped, Shipped: values);
                    })]),
                LibraryFileState.Available, FileName: null, ContentHash: null))
            .ToList();

        var remaining = Math.Max(0, Terms - libraries.Sum(l => l.Content.Rows.Count));
        for (var library = 0; remaining > 0; library++)
        {
            var count = Math.Min(CustomLibraryRows, remaining);
            remaining -= count;
            var id = $"custom-bench-{library:D3}";
            var rows = Enumerable.Range(0, count).Select(row =>
            {
                var spoken = row % 20 == 0 ? shippedForms[(library * 37 + row) % shippedForms.Count] : $"bench {library} term {row}";
                return LibraryRow.Custom(new TermValues(spoken, $"Bench{library}x{row}"));
            }).ToList();
            libraries.Add(new CatalogLibrary(
                new LibraryContent(id, false, $"Bench {library}", "Custom", null, rows),
                LibraryFileState.Available, id + ".csv", new LibraryContentHash(new string((char)('a' + library % 6), 64))));
        }

        var identities = libraries.Select(l => new LibraryIdentity(l.Content.Id, l.Content.BuiltIn, l.FileName)).ToList();
        var firstStart = new LibraryStateContext(RunningOnDefaults: false, DatabaseRepaired: false, GenerationStored: false);
        var read = LibraryComposer.Instance.ReadLocalState(identities.Select(i => i.LegacyId).ToList(), null, identities, firstStart);
        var adoption = LibraryComposer.Instance.PlanAdoption(new LibraryCatalog(0, libraries, read, [], [], 0), firstStart);
        _catalog = new LibraryCatalog(1, libraries, adoption?.State ?? read, [], [], 0);
        _draftLibraries = [.. libraries.Select(l => new DraftLibrary(l.Content, LibraryOrigin.Existing, l.State, FileName: l.FileName))];
        _draft = new LibraryDraft(2, 1, _draftLibraries, _catalog.LocalState, []);

        var first = Enumerable.Range(0, 60).Select(i => DictionaryEntry.New($"personal term {i}", $"Personal{i}")).ToList();
        var firstSorted = first.OrderBy(entry => entry.Pattern, StringComparer.Ordinal).ToList();
        _ = LibraryComposition.Preview(_draft, _catalog, first, _savedBudget);
        _ = LibraryComposition.Preview(_draft, _catalog, firstSorted, _onScreenBudget);
        _grid = [.. first.Select(entry => entry with { })];
        _sorted = [.. firstSorted.Select(entry => entry with { })];

        var selected = libraries[^1].Content;
        _selectedId = selected.Id;
        _selectedKeys = [.. selected.Rows.Select(row => row.Key)];
    }

    [Benchmark(Baseline = true)]
    public LibraryComposition HitRepeatedPreview() => LibraryComposition.Preview(_draft, _catalog, _grid, _savedBudget);

    [Benchmark]
    public int HitRepeatedPreviewWithStatuses()
    {
        var composition = LibraryComposition.Preview(_draft, _catalog, _grid, _savedBudget);
        var own = 0;
        foreach (var key in _selectedKeys)
        {
            if (composition.StatusOf(_selectedId, key).Winner == TermWinner.ThisRow)
            {
                own++;
            }
        }

        return own;
    }

    [Benchmark]
    public int HitTwoCallers() =>
        LibraryComposition.Preview(_draft, _catalog, _grid, _savedBudget).Rules.Count +
        LibraryComposition.Preview(_draft, _catalog, _sorted, _onScreenBudget).Rules.Count;

    [Benchmark]
    public LibraryComposition MissNewDraftPreview() =>
        LibraryComposition.Preview(new LibraryDraft(3, 1, _draftLibraries, _catalog.LocalState, []), _catalog, _grid, _savedBudget);
}
