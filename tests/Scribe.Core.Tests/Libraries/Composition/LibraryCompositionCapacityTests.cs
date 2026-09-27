using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Xunit.Abstractions;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The composition sizes its rule maps and lists once, from the rows that can supply a rule, instead of growing them from
/// empty on every composition (ledger LB1). What it composes is pinned by the composition, glossary and golden tests; these
/// pin the allocation and the order a presized map must keep.
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class LibraryCompositionCapacityTests(ITestOutputHelper output)
{
    private const int Rows = 1_000;

    [Fact]
    public void Coverage_lists_every_rule_once_in_rule_order()
    {
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("shared", "Shipped"), Shipped("kube", "Kubernetes"));
        var team = CustomLibrary("team", Custom("Shared", "Team"), Custom("helm", "Helm"), Custom("KUBE", "K8s", enabled: false));
        var composition = Compose(
            Catalog(State(enabled: ["github", "team"], accepted: [("team", H1)]), Committed(github), Committed(team, H1)),
            DictionaryEntry.New("get hub", "Mine"));

        var coverage = composition.Coverage();

        Assert.Equal(composition.Rules.Select(rule => rule.Key.Value), coverage.Keys);
        Assert.Equal("team", coverage["SHARED"].LibraryId);
        Assert.Equal("github", coverage["kube"].LibraryId);
    }

    [Fact]
    public void Composing_a_thousand_rules_allocates_no_growth_of_the_rule_maps()
    {
        var catalog = ThousandRuleCatalog();
        var budget = new GlossaryBudget(80);
        IReadOnlyList<DictionaryEntry> dictionary = [];
        Assert.Equal(Rows, LibraryComposition.Committed(catalog, dictionary, budget).Rules.Count);

        var bytes = Measure(() => LibraryComposition.Committed(catalog, dictionary, budget));
        output.WriteLine($"Committed over {Rows} rules: {bytes} bytes");

        // At 10c9a0b the two rule maps grew from empty through 3, 7, 17, ... 1,931 slots (3,631 slots of 28 bytes each)
        // and the rule list through 4 to 1,024 elements; sized once they take 1,103 slots and 1,000 elements. Measured on
        // x64: 344,552 bytes before and 193,608 after. The bound sits halfway, so it fails if the collections grow from
        // empty again, and leaves a wide margin for the runtime's object layout.
        Assert.True(bytes <= 269_000, $"Committed over {Rows} rules allocated {bytes} bytes; the bound is 269,000.");
    }

    [Fact]
    public void Coverage_of_a_thousand_rules_allocates_its_map_once()
    {
        var composition = LibraryComposition.Committed(ThousandRuleCatalog(), [], new GlossaryBudget(80));
        Assert.Equal(Rows, composition.Coverage().Count);

        var bytes = Measure(() => composition.Coverage());
        output.WriteLine($"Coverage of {Rows} rules: {bytes} bytes");

        // A map of 1,000 coverages (56-byte entries) grown from empty allocates 3,631 slots; sized once, 1,103. Measured on
        // x64: 218,448 bytes before and 66,352 after; the bound sits halfway.
        Assert.True(bytes <= 142_000, $"Coverage of {Rows} rules allocated {bytes} bytes; the bound is 142,000.");
    }

    private static LibraryCatalog ThousandRuleCatalog()
    {
        var rows = Enumerable.Range(0, Rows)
            .Select(i => Custom(FormattableString.Invariant($"term {i:D4}"), FormattableString.Invariant($"Term{i:D4}")))
            .ToArray();
        return Catalog(State(enabled: ["team"], accepted: [("team", H1)]), Committed(CustomLibrary("team", rows), H1));
    }

    // The smallest of three measured runs after a warm-up: the first calls JIT the path and load its types, and the
    // smallest run is the one no runtime bookkeeping landed in.
    private static long Measure(Func<object> compose)
    {
        for (var i = 0; i < 3; i++)
        {
            GC.KeepAlive(compose());
        }

        var smallest = long.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = compose();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
            smallest = Math.Min(smallest, bytes);
        }

        return smallest;
    }
}
