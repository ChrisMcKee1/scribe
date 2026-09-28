using BenchmarkDotNet.Attributes;
using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;

namespace Scribe.Benchmarks;

/// <summary>
/// The first vocabulary build's adoption plan at a start that records no custom library's markers (combined.md row 3,
/// LANG-O-02): the planner that folded the shipped values eagerly, copied as the old arm, against the lazy one. Warm, the old arm pays
/// the fold of 1,549 shipped forms; cold, in a fresh process, its first fold also builds SpokenFormFold's tables, which
/// the lazy plan never touches (run cold with <c>--strategy ColdStart --launchCount 5 --warmupCount 0 --iterationCount 1</c>).
/// Under ColdStart only the time is the first call's: the Allocated column comes from the memory diagnoser's separate, warm
/// run. The first plan's own allocation is <c>--lang-probe adoption-first-call eager|lazy</c>'s (<see cref="LangProbe"/>).
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Libraries")]
public class LangAdoptionPlanBenchmarks
{
    private LibraryCatalog _catalog = null!;
    private readonly LibraryStateContext _later = new(false, false, GenerationStored: true, CommitWitnessed: true);

    [GlobalSetup]
    public void Setup()
    {
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
        var team = new LibraryContent("team", false, "Team", "Custom", null, [LibraryRow.Custom(new TermValues("kube", "K8s"))]);
        var hash = new LibraryContentHash(new string('a', 64));
        libraries.Add(new CatalogLibrary(team, LibraryFileState.Available, "team.csv", hash));
        var ids = libraries.Select(l => l.Content.Id).ToList();
        var state = LibraryLocalState.Create(
            ids, ids, [new("team", true)], [new LegacyMarker("team", LibraryTermKey.From("kube"))], null, LocalStateHealth.Ok,
            [new("team", hash)]);
        _catalog = new LibraryCatalog(3, libraries, state, [], [], 0);
    }

    [Benchmark(Baseline = true)]
    public LibraryAdoption? EagerPlan()
    {
        // The old planner's first step, then the same plan: nothing to record, so it returns null.
        _ = LibraryTiers.ShippedValues(_catalog.Libraries.Where(l => l.Content.BuiltIn).Select(l => l.Content));
        return LibraryAdoptionPlanner.Plan(_catalog, _later, LibraryDecisions.DefaultAiPermission, static _ => []);
    }

    [Benchmark]
    public LibraryAdoption? LazyPlan() => LibraryAdoptionPlanner.Plan(_catalog, _later, LibraryDecisions.DefaultAiPermission);
}
