using Scribe.Core.Libraries;
using static Scribe.Core.Tests.Libraries.Overlay.OverlayTestData;

namespace Scribe.Core.Tests.Libraries.Overlay;

/// <summary>
/// <see cref="BuiltInLibraryOverlay.Collect"/>, which turns a built-in's rows back into its edits document (O-8, review
/// finding A7): not every entry has a row, so the committed document carries what the rows cannot show.
/// </summary>
public sealed class BuiltInLibraryOverlayCollectTests
{
    private static readonly TermValues GetHub = T("get hub", "GitHub");
    private static readonly TermValues Copilot = T("copilot", "Copilot");
    private static readonly TermValues OctoCat = T("octo cat", "Octocat");

    [Fact]
    public void Collect_keeps_every_committed_entry_whose_key_has_no_row_except_additions_and_rows_no_longer_shipped()
    {
        // O-8. The running version ships "get hub" and "copilot" but not "octo cat" or "old term".
        var shipped = Shipped(GetHub, Copilot);
        var committed = Document(
            Off("octo cat", OctoCat),
            Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")),
            Off("copilot", Copilot),
            Added("gh cli", T("gh cli", "GitHub CLI")),
            Edited("old term", T("old term", "Old"), T("old term", "Older")));

        // A caller that gives no rows at all: the intents of shipped rows and the inert off entry stay; the addition and
        // the no-longer-shipped row, which the user can delete, go.
        var collected = BuiltInOverlay.Collect(shipped, committed, []);

        AssertSameDocument(
            Document(
                Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")),
                Off("copilot", Copilot),
                Off("octo cat", OctoCat)),
            collected);
    }

    [Fact]
    public void An_inert_off_entry_survives_a_save_of_every_row_the_version_shows()
    {
        var shipped = Shipped(GetHub, Copilot);
        var committed = Document(Off("octo cat", OctoCat), Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")));
        var rows = BuiltInOverlay.Apply(shipped, committed);
        Assert.DoesNotContain(rows, row => row.Key == K("octo cat"));

        var collected = BuiltInOverlay.Collect(shipped, committed, rows);

        AssertSameDocument(Document(Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")), Off("octo cat", OctoCat)), collected);
    }

    [Fact]
    public void A_deleted_addition_and_a_deleted_row_no_longer_shipped_leave_the_document()
    {
        var shipped = Shipped(GetHub);
        var committed = Document(
            Added("gh cli", T("gh cli", "GitHub CLI")),
            Edited("old term", T("old term", "Old"), T("old term", "Older")),
            Added("octo", T("octo", "Octo")));
        var rows = BuiltInOverlay.Apply(shipped, committed);
        var kept = rows.Where(row => row.Key != K("gh cli") && row.Key != K("old term")).ToArray();

        var collected = BuiltInOverlay.Collect(shipped, committed, kept);

        AssertSameDocument(Document(Added("octo", T("octo", "Octo"))), collected);
    }

    [Fact]
    public void A_row_the_user_restored_or_turned_back_on_takes_its_entry_away()
    {
        var shipped = Shipped(GetHub, OctoCat);
        var committed = Document(Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")), Off("octo cat", OctoCat));
        var rows = BuiltInOverlay.Apply(shipped, committed);
        rows = Replace(rows, Row(rows, "get hub"), BuiltInOverlay.RestoreShipped(Row(rows, "get hub")));
        rows = Replace(rows, Row(rows, "octo cat"), BuiltInOverlay.SetEnabled(Row(rows, "octo cat"), enabled: true));

        Assert.Null(BuiltInOverlay.Collect(shipped, committed, rows));
    }

    [Fact]
    public void An_addition_the_version_now_ships_goes_when_its_row_is_left_out_like_any_addition()
    {
        var shipped = Shipped(GetHub, T("gh cli", "GH CLI"));
        var committed = Document(Added("gh cli", T("gh cli", "GitHub CLI")));
        var rows = BuiltInOverlay.Apply(shipped, committed);
        Assert.Equal(TermOrigin.Added, Row(rows, "gh cli").Origin);

        Assert.Null(BuiltInOverlay.Collect(shipped, committed, [Row(rows, "get hub")]));
        AssertSameDocument(committed, BuiltInOverlay.Collect(shipped, committed, rows));
    }

    [Fact]
    public void A_row_for_the_key_of_an_inert_off_entry_replaces_it()
    {
        // The user added a term with the spoken form of a shipped row this version does not ship and the user had turned
        // off: the row the user can see decides, as for every key that has a row.
        var shipped = Shipped(GetHub);
        var committed = Document(Off("octo cat", OctoCat));
        var rows = BuiltInOverlay.Apply(shipped, committed);

        var collected = BuiltInOverlay.Collect(shipped, committed, [.. rows, BuiltInOverlay.Add(T("octo cat", "OctoCat"))]);

        AssertSameDocument(Document(Added("octo cat", T("octo cat", "OctoCat"))), collected);
    }

    [Fact]
    public void Collecting_the_rows_a_document_gives_returns_that_document()
    {
        // A document Scribe wrote comes back unchanged from its own rows, entries in the same order, so nothing is
        // rewritten that the user did not change.
        var v1 = Shipped(GetHub, Copilot, OctoCat, T("gh cli", "GitHub CLI"));
        var rows = BuiltInOverlay.Apply(v1, null);
        rows = Replace(rows, Row(rows, "gh cli"), BuiltInOverlay.Edit(Row(rows, "gh cli"), T("gh cli", "GH CLI")));
        rows = Replace(rows, Row(rows, "octo cat"), BuiltInOverlay.SetEnabled(Row(rows, "octo cat"), enabled: false));
        rows = Replace(rows, Row(rows, "get hub"), BuiltInOverlay.Edit(Row(rows, "get hub"), T("git hub", "GitHub")));
        rows = [.. rows, BuiltInOverlay.Add(T("zulu", "Zulu")), BuiltInOverlay.Add(T("alpha", "Alpha"))];
        var document = BuiltInOverlay.Collect(v1, null, rows);

        Assert.Equal(["get hub", "octo cat", "gh cli", "zulu", "alpha"], document!.Terms.Select(term => term.Key.Value));
        AssertSameDocument(document, BuiltInOverlay.Collect(v1, document, BuiltInOverlay.Apply(v1, document)));
    }

    [Fact]
    public void Entries_of_rows_the_version_does_not_ship_keep_the_order_the_rows_have()
    {
        // Deleted and added back: the addition moves to the end, as the editor appends it, and the next load agrees.
        var shipped = Shipped(GetHub);
        var committed = Document(Added("alpha", T("alpha", "Alpha")), Added("bravo", T("bravo", "Bravo")));
        var rows = BuiltInOverlay.Apply(shipped, committed);
        rows = [.. rows.Where(row => row.Key != K("alpha")), BuiltInOverlay.Add(T("alpha", "ALPHA"))];

        var (stored, reloaded) = SaveAndReload(shipped, committed, rows);

        Assert.Equal(["bravo", "alpha"], stored!.Terms.Select(term => term.Key.Value));
        AssertSameRows(rows, reloaded);
    }

    [Fact]
    public void Collect_writes_one_order_whatever_the_order_of_the_shipped_rows_or_of_a_document_written_by_hand()
    {
        // Round 2, part 2 (Grok's note): Collect does not require the rows in the order Apply gives them. The entries of
        // shipped rows come in shipped order wherever those rows sit, the rows this version does not ship keep their
        // relative order, and a document whose entries were ordered by hand is written back in that one order.
        var shipped = Shipped(GetHub, Copilot, OctoCat);
        var handOrdered = Document(
            Added("zulu", T("zulu", "Zulu")),
            Off("octo cat", OctoCat),
            Added("alpha", T("alpha", "Alpha")),
            Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")));
        var rows = BuiltInOverlay.Apply(shipped, handOrdered);
        var canonical = Document(
            Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")),
            Off("octo cat", OctoCat),
            Added("zulu", T("zulu", "Zulu")),
            Added("alpha", T("alpha", "Alpha")));

        AssertSameDocument(canonical, BuiltInOverlay.Collect(shipped, handOrdered, rows));

        // The shipped rows moved about, even after the rows this version does not ship: the same document.
        var shuffled = new[] { rows[2], rows[3], rows[0], rows[4], rows[1] };
        Assert.Equal(["octo cat", "zulu", "get hub", "alpha", "copilot"], shuffled.Select(row => row.Key.Value));
        AssertSameDocument(canonical, BuiltInOverlay.Collect(shipped, handOrdered, shuffled));
    }

    [Fact]
    public void Collect_refuses_rows_it_did_not_give_and_rows_that_repeat_a_key()
    {
        var shipped = Shipped(GetHub, OctoCat);
        var rows = BuiltInOverlay.Apply(shipped, null);
        var renamed = BuiltInOverlay.Edit(Row(rows, "get hub"), T("git hub", "GitHub"));

        // The user renamed "get hub" and then added "get hub" again: two rows with one key, which no document can hold.
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Collect(shipped, null, [renamed, Row(rows, "octo cat"), BuiltInOverlay.Add(T("get hub", "Get Hub"))]));

        // A custom row, a row whose values do not come from its entry, and a row of another version.
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Collect(shipped, null, [LibraryRow.Custom(GetHub)]));
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Collect(shipped, null, [renamed with { Values = T("git hub", "Git Hub") }]));
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Collect(shipped, null, [Row(rows, "octo cat") with { Origin = TermOrigin.Off }]));
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Collect(Shipped(T("get hub", "GitHub, Inc.")), null, [Row(rows, "get hub")]));
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Collect(shipped, null, [BuiltInOverlay.Add(T("get hub", "Get Hub"))]));
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Collect(shipped, new BuiltInLibraryEdits("microsoft-azure", []), rows));
        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.Collect(shipped, null, null!));
        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.Collect(null!, null, rows));

        // The document of every shipped row as it ships is none.
        Assert.Null(BuiltInOverlay.Collect(shipped, null, rows));
        Assert.Null(BuiltInOverlay.Collect(shipped, null, []));
    }
}
