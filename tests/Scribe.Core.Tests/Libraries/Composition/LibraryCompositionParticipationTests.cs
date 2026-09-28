using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Xunit.Abstractions;
using static Scribe.Core.Tests.Libraries.Composition.Lib;

namespace Scribe.Core.Tests.Libraries.Composition;

/// <summary>
/// A library that does not take part in a composition (off, paused or pending deletion) never supplies a rule, so the
/// composition keeps no rule entry for its rows (ledger LB10). What the page says about those rows must not change.
/// </summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class LibraryCompositionParticipationTests(ITestOutputHelper output)
{
    [Fact]
    public void Rows_of_a_library_that_is_off_report_who_writes_their_spoken_form()
    {
        var github = BuiltInLibrary("github", Shipped("get hub", "GitHub"), Shipped("kube", "Kubernetes"));
        var off = CustomLibrary("off",
            Custom("get hub", "Off Hub"), Custom("kube", "Kubernetes"), Custom("mine", "Off Mine"), Custom("alone", "Alone"),
            Custom("quiet", "Quiet", enabled: false));
        var composition = Compose(
            Catalog(State(enabled: ["github"], accepted: [("off", H1)]), Committed(github), Committed(off, H1)),
            DictionaryEntry.New("mine", "Mine"));

        var hub = composition.StatusOf("off", Key("get hub"));
        Assert.Equal(TermWinner.OtherLibrary, hub.Winner);
        Assert.Equal("github", hub.WinningLibraryId);
        Assert.Equal("GitHub", hub.WinningEntry?.Replacement);
        Assert.Equal(TermMarker.None, hub.Marker);
        Assert.Empty(hub.SameResultIn);
        Assert.Equal(["github"], hub.DifferentResultIn);
        Assert.Equal(GlossaryInclusion.NotApplied, hub.Glossary);

        var kube = composition.StatusOf("off", Key("kube"));
        Assert.Equal(TermWinner.OtherLibrary, kube.Winner);
        Assert.Equal(["github"], kube.SameResultIn);
        Assert.Empty(kube.DifferentResultIn);
        Assert.Equal(TermWinner.Dictionary, composition.StatusOf("off", Key("mine")).Winner);
        Assert.Equal(TermWinner.None, composition.StatusOf("off", Key("alone")).Winner);
        Assert.Equal(TermWinner.None, composition.StatusOf("off", Key("quiet")).Winner);

        // The library that is on is unaffected, and never lists the one that is off as a holder.
        var shipped = composition.StatusOf("github", Key("get hub"));
        Assert.Equal(TermWinner.ThisRow, shipped.Winner);
        Assert.Empty(shipped.SameResultIn);
        Assert.Empty(shipped.DifferentResultIn);

        Assert.Equal(["github"], composition.EnabledLibraries.Select(library => library.Id));
        Assert.Equal(
            ["get hub", "kube", "mine", "alone", "quiet"], composition.Filter("off", TermFilter.All).Select(key => key.Value));
        Assert.Equal(["quiet"], composition.Filter("off", TermFilter.TurnedOff).Select(key => key.Value));
    }

    [Fact]
    public void A_preview_keeps_no_rule_entry_for_a_library_the_draft_deletes_or_turns_off()
    {
        var team = CustomLibrary("team", Custom("get hub", "Team Hub"));
        var gone = CustomLibrary("gone", Custom("get hub", "Gone Hub"));
        var off = CustomLibrary("off", Custom("get hub", "Off Hub"));
        var committed = Catalog(
            State(enabled: ["team", "gone", "off"], accepted: [("team", H1), ("gone", H2), ("off", H3)]),
            Committed(team, H1), Committed(gone, H2), Committed(off, H3));
        var draft = Draft(
            7, State(enabled: ["team", "gone"], accepted: [("team", H1), ("gone", H2), ("off", H3)]),
            Draft(team), Draft(gone, pendingDelete: true), Draft(off));

        var preview = LibraryComposition.Preview(draft, committed, [], new GlossaryBudget(80));

        Assert.Equal("Team Hub", Winner(preview, "get hub"));
        Assert.Equal(TermWinner.ThisRow, preview.StatusOf("team", Key("get hub")).Winner);
        Assert.Equal(TermWinner.OtherLibrary, preview.StatusOf("gone", Key("get hub")).Winner);
        Assert.Equal(TermWinner.OtherLibrary, preview.StatusOf("off", Key("get hub")).Winner);
        Assert.Equal(["team"], preview.EnabledLibraries.Select(library => library.Id));
    }

    [Fact]
    public void A_large_library_that_is_off_costs_no_entry_per_row()
    {
        var rows = Enumerable.Range(0, 1_000)
            .Select(i => Custom(FormattableString.Invariant($"term {i:D4}"), FormattableString.Invariant($"Term{i:D4}")))
            .ToArray();
        var catalog = Catalog(
            State(enabled: ["small"], accepted: [("small", H1), ("large", H2)]),
            Committed(CustomLibrary("small", Custom("get hub", "GitHub")), H1),
            Committed(CustomLibrary("large", rows), H2));
        var budget = new GlossaryBudget(80);
        IReadOnlyList<DictionaryEntry> dictionary = [];
        for (var i = 0; i < 3; i++)
        {
            GC.KeepAlive(LibraryComposition.Committed(catalog, dictionary, budget));
        }

        var smallest = long.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var composition = LibraryComposition.Committed(catalog, dictionary, budget);
            smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
            GC.KeepAlive(composition);
        }

        output.WriteLine($"Committed with a 1,000-row library off: {smallest} bytes");

        // Every row of the library that is off used to get a DictionaryEntry (48 bytes) and a slot for it (8 bytes): 56,000
        // bytes the composition never read. Its competing keys and marker flags stay, since the page's statuses use them.
        // Measured on x64: 68,592 bytes before and 12,568 after (56,024 less: those rows and the array's header); the
        // bound sits halfway.
        Assert.True(smallest <= 40_500, $"Committed with a 1,000-row library off allocated {smallest} bytes; the bound is 40,500.");
    }
}
