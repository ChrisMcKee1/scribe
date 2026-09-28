using System.Reflection;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Xunit.Abstractions;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The statuses' comparison index keeps a spoken form's first row inline and lists any others after it, and a status makes
/// its set of listed libraries only once a second library holds the form (ledger LB3). The libraries a status names, and
/// their order, must not change. Each saving is also checked on its own: the index and a library's row lookup by the room
/// they end with, the inline first row by the first status measured per form held once, and the set by warm statuses.
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class LibraryCompositionStatusOrderTests(ITestOutputHelper output)
{
    [Fact]
    public void Other_libraries_are_listed_once_each_in_precedence_order()
    {
        var terminology = BuiltInLibrary("ai-terminology", Shipped("shared", "Same"), Shipped("alone", "Alone"));
        var github = BuiltInLibrary("github", Shipped("shared", "Other"), Shipped("pair", "Pair"));
        var alpha = CustomLibrary("alpha", Custom("shared", "Same"), Custom("pair", "Pair"));
        var beta = CustomLibrary("beta", Custom("shared", "Other", wholeWord: false));
        var gamma = CustomLibrary("gamma", Custom("shared", "Same"), Custom("SHARED", "Same again"));
        var composition = Compose(Catalog(
            State(enabled: ["ai-terminology", "github", "alpha", "beta", "gamma"], accepted: [("alpha", H1), ("beta", H2), ("gamma", H3)]),
            Committed(gamma, H3), Committed(beta, H2), Committed(alpha, H1), Committed(github), Committed(terminology)));

        var fromAlpha = composition.StatusOf("alpha", Key("shared"));
        Assert.Equal(["ai-terminology", "gamma"], fromAlpha.SameResultIn);
        Assert.Equal(["github", "beta"], fromAlpha.DifferentResultIn);

        var fromTerminology = composition.StatusOf("ai-terminology", Key("shared"));
        Assert.Equal(["alpha", "gamma"], fromTerminology.SameResultIn);
        Assert.Equal(["github", "beta"], fromTerminology.DifferentResultIn);

        // gamma holds the form twice: it lists every other library once and never itself.
        var fromGamma = composition.StatusOf("gamma", Key("shared"));
        Assert.Equal(["ai-terminology", "alpha"], fromGamma.SameResultIn);
        Assert.Equal(["github", "beta"], fromGamma.DifferentResultIn);

        var pair = composition.StatusOf("github", Key("pair"));
        Assert.Equal(["alpha"], pair.SameResultIn);
        Assert.Empty(pair.DifferentResultIn);

        var alone = composition.StatusOf("ai-terminology", Key("alone"));
        Assert.Empty(alone.SameResultIn);
        Assert.Empty(alone.DifferentResultIn);
    }

    [Fact]
    public void A_library_holding_a_form_twice_is_judged_by_its_first_row()
    {
        var alpha = CustomLibrary("alpha", Custom("dup", "First"), Custom("dup", "Second"));
        var beta = CustomLibrary("beta", Custom("dup", "Second"));
        var composition = Compose(Catalog(
            State(enabled: ["alpha", "beta"], accepted: [("alpha", H1), ("beta", H2)]), Committed(alpha, H1), Committed(beta, H2)));

        var fromBeta = composition.StatusOf("beta", Key("dup"));
        Assert.Empty(fromBeta.SameResultIn);
        Assert.Equal(["alpha"], fromBeta.DifferentResultIn);

        var fromAlpha = composition.StatusOf("alpha", Key("dup"));
        Assert.Empty(fromAlpha.SameResultIn);
        Assert.Equal(["beta"], fromAlpha.DifferentResultIn);
    }

    [Fact]
    public void The_first_status_indexes_a_thousand_forms_without_a_list_each()
    {
        var catalog = ThousandRowCatalog();
        var budget = new GlossaryBudget(80);
        IReadOnlyList<DictionaryEntry> dictionary = [];
        var key = Key("term 0500");

        var first = Smallest(() =>
        {
            var composition = LibraryComposition.Committed(catalog, dictionary, budget);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var status = composition.StatusOf("team", key);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(status);
            return bytes;
        });
        output.WriteLine($"First StatusOf over 1,000 forms: {first} bytes");

        // The first status builds the index of every form's rows and the library's row identities. At 10c9a0b the index
        // grew from empty and held a list per form (a List object and a four-slot array), and the identity map grew from
        // empty too. Measured on x64: 324,840 bytes before and 79,912 after; the bound sits halfway.
        Assert.True(first <= 202_000, $"The first status allocated {first} bytes; the bound is 202,000.");
    }

    [Fact]
    public void A_status_of_a_form_only_its_library_holds_makes_no_set()
    {
        var composition = LibraryComposition.Committed(ThousandRowCatalog(), [], new GlossaryBudget(80));
        var keys = Enumerable.Range(0, 100).Select(i => Key(FormattableString.Invariant($"term {i:D4}"))).ToArray();
        foreach (var key in keys)
        {
            GC.KeepAlive(composition.StatusOf("team", key));
        }

        var bytes = Smallest(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var key in keys)
            {
                GC.KeepAlive(composition.StatusOf("team", key));
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        output.WriteLine($"100 warm statuses: {bytes} bytes");

        // Each status used to make a HashSet of listed libraries, seeded with its own, even when no other library holds the
        // form. The status itself and its two result lists stay. Measured on x64: 36,000 bytes before and 18,400 after for
        // 100 statuses; the bound sits halfway.
        Assert.True(bytes <= 27_200, $"100 statuses allocated {bytes} bytes; the bound is 27,200.");
    }

    // The bounds above measure several of LB3's savings together, so each is checked on its own too (the set made only for
    // a second library is the one the warm statuses above measure alone). The index and a library's row lookup end with
    // exactly the room a map sized for their count gets, never what growth from empty reaches, which for 1,000 entries is
    // more. The composition keeps both private; the test reads them by reflection, without growing either.
    [Fact]
    public void The_index_and_a_library_s_row_lookup_have_exactly_the_room_their_counts_need()
    {
        var composition = LibraryComposition.Committed(ThousandRowCatalog(), [], new GlossaryBudget(80));
        GC.KeepAlive(composition.StatusOf("team", Key("term 0500")));
        Assert.NotEqual(SizedFor(1_000), GrownTo(1_000));

        var index = Capacity(Index(composition));
        var lookup = Capacity(RowLookup(composition, "team"));

        Assert.True(SizedFor(1_000) == index, $"The index of spoken forms has room for {index}.");
        Assert.True(SizedFor(1_000) == lookup, $"The library's row lookup has room for {lookup}.");
    }

    // The first status of a library with one row builds the index of every library's forms and that library's one-entry
    // row lookup, so per form it measures the index: for forms held once, their slots in the index and nothing else, each
    // form's row held inline. A form held twice lists only its second row.
    [Fact]
    public void The_first_status_indexes_forms_held_once_inline_with_no_list_each()
    {
        var catalog = ThousandRowCatalogWith(CustomLibrary("tiny", Custom("lonely", "Lonely")));
        IReadOnlyList<DictionaryEntry> dictionary = [];
        var budget = new GlossaryBudget(80);

        var bytes = Smallest(() =>
        {
            var composition = LibraryComposition.Committed(catalog, dictionary, budget);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var status = composition.StatusOf("tiny", Key("lonely"));
            var after = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(status);
            return after;
        });
        var perForm = bytes / 1_001.0;
        output.WriteLine($"First StatusOf indexing 1,001 forms held once: {bytes} bytes, {perForm:F1} per form");

        var index = Index(LibraryComposition.Committed(catalog, dictionary, budget));
        Assert.Equal(1_001, index.Count);
        foreach (var form in index.Keys)
        {
            var holders = index[form]!;
            Assert.True(holders.GetType().IsValueType, $"The index holds a {holders.GetType().Name} for a form held once.");
            Assert.Null(holders.GetType().GetProperty("Rest")!.GetValue(holders));
        }

        var twice = Index(Compose(Catalog(
            State(enabled: ["alpha", "beta"], accepted: [("alpha", H1), ("beta", H2)]),
            Committed(CustomLibrary("alpha", Custom("shared", "Alpha")), H1), Committed(CustomLibrary("beta", Custom("shared", "Beta")), H2))));
        var shared = twice[Key("shared")]!;
        var rest = Assert.IsAssignableFrom<System.Collections.IEnumerable>(shared.GetType().GetProperty("Rest")!.GetValue(shared));
        Assert.Single(rest.Cast<object>());

        // Held inline, a form held once takes only its slot in the index; a List per form (a List object and a four-slot
        // array) cost about 102 bytes more each. Measured on x64: 49.1 bytes per form, and 151.4 with a List per form (the
        // older bound above still passed that at 182,264 bytes); the bound sits halfway.
        Assert.True(perForm <= 100, $"The first status allocated {perForm:F1} bytes per form; the bound is 100.");
    }

    // Permission is off so the first status builds no glossary: only the index and the identity map are measured.
    private static LibraryCatalog ThousandRowCatalog()
    {
        var rows = Enumerable.Range(0, 1_000)
            .Select(i => Custom(FormattableString.Invariant($"term {i:D4}"), FormattableString.Invariant($"Term{i:D4}")))
            .ToArray();
        return Catalog(
            State(enabled: ["team"], ai: [("team", false)], accepted: [("team", H1)]), Committed(CustomLibrary("team", rows), H1));
    }

    // The same 1,000 forms and one more library, permission off for both.
    private static LibraryCatalog ThousandRowCatalogWith(LibraryContent extra)
    {
        var rows = Enumerable.Range(0, 1_000)
            .Select(i => Custom(FormattableString.Invariant($"term {i:D4}"), FormattableString.Invariant($"Term{i:D4}")))
            .ToArray();
        return Catalog(
            State(enabled: ["team", extra.Id], ai: [("team", false), (extra.Id, false)], accepted: [("team", H1), (extra.Id, H2)]),
            Committed(CustomLibrary("team", rows), H1), Committed(extra, H2));
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // The composition's index of each spoken form's rows, built the way the first status builds it (its Lazy).
    private static System.Collections.IDictionary Index(LibraryComposition composition)
    {
        var lazy = typeof(LibraryComposition).GetField("_rowsByKey", Private)!.GetValue(composition)!;
        return (System.Collections.IDictionary)lazy.GetType().GetProperty(nameof(Lazy<object>.Value))!.GetValue(lazy)!;
    }

    // A library's lookup of its rows by identity, which its first status builds.
    private static object RowLookup(LibraryComposition composition, string libraryId)
    {
        var byId = (System.Collections.IDictionary)typeof(LibraryComposition).GetField("_byId", Private)!.GetValue(composition)!;
        var source = byId[libraryId]!;
        return source.GetType().GetField("_rowsByIdentity", Private)!.GetValue(source)
            ?? throw new InvalidOperationException("The library's row lookup has not been built.");
    }

    private static int Capacity(object map) =>
        (int)map.GetType().GetMethod(nameof(Dictionary<int, int>.EnsureCapacity))!.Invoke(map, [0])!;

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

    // Warmed, then the smallest of three runs: the first calls JIT the path and load its types.
    private static long Smallest(Func<long> measure)
    {
        for (var i = 0; i < 3; i++)
        {
            measure();
        }

        return Math.Min(measure(), Math.Min(measure(), measure()));
    }
}
