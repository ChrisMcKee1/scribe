using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Xunit.Abstractions;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// The statuses' comparison index keeps a spoken form's first row inline and lists any others after it, and a status makes
/// its set of listed libraries only once a second library holds the form (ledger LB3). The libraries a status names, and
/// their order, must not change.
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

    // Permission is off so the first status builds no glossary: only the index and the identity map are measured.
    private static LibraryCatalog ThousandRowCatalog()
    {
        var rows = Enumerable.Range(0, 1_000)
            .Select(i => Custom(FormattableString.Invariant($"term {i:D4}"), FormattableString.Invariant($"Term{i:D4}")))
            .ToArray();
        return Catalog(
            State(enabled: ["team"], ai: [("team", false)], accepted: [("team", H1)]), Committed(CustomLibrary("team", rows), H1));
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
