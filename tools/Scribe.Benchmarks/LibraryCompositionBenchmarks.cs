using BenchmarkDotNet.Attributes;
using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;

namespace Scribe.Benchmarks;

/// <summary>
/// The library model's composition (W1b contracts 3.3, plan 3.14) at the three sizes the caps and the 10,000-term
/// notice are set from: the 1,549 shipped rows with every built-in on, and 10,000 and 100,000 library terms, where the
/// rest are custom libraries of 1,000 rows, some of whose spoken forms contradict shipped ones and carry the legacy
/// markers the first start adopts.
/// <para>
/// Committed and Preview are what a Save's publication and each Settings revision pay (Preview against the committed
/// catalog the draft was built from; PreviewOfARebuiltDraft when no draft row is the committed row object itself);
/// FirstStatus adds what the first Term details on a composition pays (the statuses' indexes and the glossary inclusion,
/// computed once); ComposeVocabulary is what dictation's vocabulary costs; EncodeState is the libraries.state row a
/// commit writes.
/// </para>
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Libraries")]
public class LibraryCompositionBenchmarks
{
    private const int CustomLibraryRows = 1_000;

    private LibraryCatalog _catalog = null!;
    private LibraryDraft _draft = null!;
    private LibraryDraft _rebuiltDraft = null!;
    private IReadOnlyList<LibraryIdentity> _identities = [];
    private IReadOnlyList<DictionaryEntry> _dictionary = [];
    private (string LibraryId, LibraryTermKey Key) _probe;
    private readonly GlossaryBudget _budget = new(CleanupPrompt.MaxGlossaryTermsLocal);

    [Params(1_549, 10_000, 100_000)]
    public int Terms { get; set; }

    [GlobalSetup]
    public void Setup()
    {
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
                // One row in twenty contradicts a shipped spoken form, as a legacy library might.
                var spoken = row % 20 == 0 ? shippedForms[(library * 37 + row) % shippedForms.Count] : $"bench {library} term {row}";
                return LibraryRow.Custom(new TermValues(spoken, $"Bench{library}x{row}"));
            }).ToList();
            libraries.Add(new CatalogLibrary(
                new LibraryContent(id, false, $"Bench {library}", "Custom", null, rows),
                LibraryFileState.Available, id + ".csv", new LibraryContentHash(new string((char)('a' + library % 6), 64))));
        }

        _identities = [.. libraries.Select(l => new LibraryIdentity(l.Content.Id, l.Content.BuiltIn, l.FileName))];
        var firstStart = new LibraryStateContext(RunningOnDefaults: false, DatabaseRepaired: false, GenerationStored: false);
        var document = _identities.Select(i => i.LegacyId).ToList();
        var read = LibraryComposer.Instance.ReadLocalState(document, null, _identities, firstStart);
        var adoption = LibraryComposer.Instance.PlanAdoption(new LibraryCatalog(0, libraries, read, [], [], 0), firstStart);
        _catalog = new LibraryCatalog(1, libraries, adoption?.State ?? read, [], [], 0);
        _draft = new LibraryDraft(
            2, 1, [.. libraries.Select(l => new DraftLibrary(l.Content, LibraryOrigin.Existing, l.State, FileName: l.FileName))],
            _catalog.LocalState, []);
        // Every row a fresh object with equal values, as a draft rebuilt from the editor's rows may hold, so the preview's
        // check of which files the Save keeps compares every row instead of meeting the committed lists themselves.
        _rebuiltDraft = new LibraryDraft(
            3, 1,
            [.. libraries.Select(l => new DraftLibrary(
                l.Content with { Rows = [.. l.Content.Rows.Select(row => row with { Values = row.Values with { } })] },
                LibraryOrigin.Existing, l.State, FileName: l.FileName))],
            _catalog.LocalState, []);
        _dictionary = [.. Enumerable.Range(0, 60).Select(i => DictionaryEntry.New($"personal term {i}", $"Personal{i}"))];
        var last = libraries[^1];
        _probe = (last.Content.Id, last.Content.Rows[^1].Key);
    }

    [Benchmark(Baseline = true)]
    public LibraryComposition Committed() => LibraryComposition.Committed(_catalog, _dictionary, _budget);

    [Benchmark]
    public LibraryComposition Preview() => LibraryComposition.Preview(_draft, _catalog, _dictionary, _budget);

    [Benchmark]
    public LibraryComposition PreviewOfARebuiltDraft() => LibraryComposition.Preview(_rebuiltDraft, _catalog, _dictionary, _budget);

    [Benchmark]
    public TermStatus FirstStatus() => LibraryComposition.Committed(_catalog, _dictionary, _budget).StatusOf(_probe.LibraryId, _probe.Key);

    [Benchmark]
    public LibraryVocabulary ComposeVocabulary() => LibraryComposer.Instance.ComposeVocabulary(_catalog);

    [Benchmark]
    public string? EncodeState() =>
        LibraryComposer.Instance.EncodeLocalState(_catalog.LocalState, _catalog.LocalState, _identities, _identities).StateValue;
}
