using System.Collections.ObjectModel;
using System.Reflection;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Settings;
using Xunit.Abstractions;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The composition sizes its rule maps and lists once, from the rows that can supply a rule, instead of growing them from
/// empty on every composition (ledger LB1). What it composes is pinned by the composition, glossary and golden tests; these
/// pin the allocation, the room each sized collection has, and the order a presized map must keep.
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

    // Review round 1, item 2: the allocation bounds above measure several sizings together, so one map growing from empty
    // again stayed under them. Each collection the composition sizes is checked on its own here, by the room it has after
    // it is filled (EnsureCapacity(0) and List<T>.Capacity read it without growing anything): exactly what a collection
    // sized for its count gets, never what growth from empty reaches, which for these counts is always more.
    [Fact]
    public void Every_collection_the_composition_sizes_has_exactly_the_room_its_count_needs()
    {
        const int Personal = 100;
        var catalog = ThousandRuleCatalog();
        IReadOnlyList<DictionaryEntry> dictionary =
        [
            .. Enumerable.Range(0, Personal)
                .Select(i => DictionaryEntry.New(FormattableString.Invariant($"personal {i:D3}"), FormattableString.Invariant($"Personal{i:D3}"))),
        ];
        var composition = LibraryComposition.Committed(catalog, dictionary, new GlossaryBudget(80));
        Assert.Equal(Rows, composition.Rules.Count);
        Assert.NotEqual(SizedFor(Personal), GrownTo(Personal));
        Assert.NotEqual(SizedFor(Rows), GrownTo(Rows));
        Assert.NotEqual(Rows, new List<int>(Enumerable.Range(0, Rows).Where(_ => true)).Capacity);

        var (personalByKey, ruleByKey, sourceOfRule) = composition.MapCapacities();
        Assert.True(SizedFor(Personal) == personalByKey, $"The personal dictionary's map has room for {personalByKey}.");
        Assert.True(SizedFor(Rows) == ruleByKey, $"The rule map has room for {ruleByKey}.");
        Assert.True(SizedFor(Rows) == sourceOfRule, $"The map of each rule's library has room for {sourceOfRule}.");

        var ruleList = Assert.IsType<List<ComposedRule>>(
            typeof(ReadOnlyCollection<ComposedRule>).GetProperty("Items", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(composition.Rules));
        Assert.True(Rows == ruleList.Capacity, $"The rule list has room for {ruleList.Capacity}.");

        var coverage = Assert.IsType<Dictionary<string, LibraryCoverage>>(composition.Coverage()).EnsureCapacity(0);
        Assert.True(SizedFor(Rows) == coverage, $"Coverage's map has room for {coverage}.");
        var names = composition.RuleLibraryNames().EnsureCapacity(0);
        Assert.True(SizedFor(Rows) == names, $"The Save prompt's map of library names has room for {names}.");

        var vocabulary = LibraryComposer.Instance.ComposeVocabulary(catalog);
        Assert.Equal(Rows, vocabulary.Entries.Count);
        Assert.True(LibraryVocabularyOrigins.TryGet(vocabulary, out var origins));
        var byOrigin = Assert.IsType<Dictionary<DictionaryEntry, string>>(origins).EnsureCapacity(0);
        Assert.True(SizedFor(Rows) == byOrigin, $"The vocabulary's map of origins has room for {byOrigin}.");
    }

    // The room a map sized for count entries gets, and the room one grown from empty reaches once it holds them.
    private static int SizedFor(int count) => new Dictionary<int, int>(count).EnsureCapacity(0);

    private static int GrownTo(int count)
    {
        var map = new Dictionary<int, int>();
        for (var i = 0; i < count; i++)
        {
            map.Add(i, i);
        }

        return map.EnsureCapacity(0);
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
