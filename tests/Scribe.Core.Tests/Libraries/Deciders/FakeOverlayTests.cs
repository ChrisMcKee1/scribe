using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// The deciders' fake overlay follows the amended 3.2.1 rules the deciders rely on, so their tests can catch an upgrade,
/// review, off-intent or pinned defect: per-field authorship with a changed field based on the shipped value in use, the
/// off intent kept through an edit, Turn on against a disabled shipped row, reviews raised and resolved, and Collect's
/// refusals and order. Pinned here because the fake stands in for the overlay stream's code until the integration commit.
/// </summary>
public sealed class FakeOverlayTests
{
    private static DictionaryLibrary Version(string written, bool enabled = true) =>
        new(GitHubId, "GitHub", "Developer", null, BuiltIn: true,
        [
            new DictionaryEntry(0, "get hub", written, true, enabled),
            DictionaryEntry.New("copilot", "Copilot"),
        ]);

    [Fact]
    public void An_edit_authors_only_the_changed_field_based_on_the_shipped_value_in_use()
    {
        var v1 = Version("GitHub");
        var row = TestOverlay.Apply(v1, null)[0];
        var edited = TestOverlay.Edit(row, row.Values with { WholeWord = false });
        Assert.Equal((TermOrigin.Edited, row.Values with { WholeWord = false }, row.Values), (edited.Origin, edited.Values, edited.Edit!.Base));

        // A later version corrects the written form, which the user never touched: it arrives, and nothing asks.
        var v2 = Version("GitHub, Inc.");
        var upgraded = TestOverlay.Apply(v2, TestOverlay.Collect(v1, null, [edited]))[0];
        Assert.Equal((new TermValues("get hub", "GitHub, Inc.", WholeWord: false), (TermReview?)null), (upgraded.Values, upgraded.Review));

        // Typing the written form now bases it on that version's value, and typing that value back inherits again.
        var typed = TestOverlay.Edit(upgraded, upgraded.Values with { Written = "GH" });
        Assert.Equal(("GH", "GitHub, Inc."), (typed.Values.Written, typed.Edit!.Base!.Written));
        var back = TestOverlay.Edit(typed, typed.Values with { Written = "GitHub, Inc." });
        Assert.Equal(back.Edit!.Base!.Written, back.Edit.Value!.Written);
        Assert.Equal("GitHub v3", TestOverlay.Apply(Version("GitHub v3"), TestOverlay.Collect(v2, null, [back]))[0].Values.Written);
    }

    [Fact]
    public void A_row_turned_off_and_edited_while_shipped_off_stays_off_when_a_later_version_ships_it_on()
    {
        var v1 = Version("GitHub");
        var off = TestOverlay.SetEnabled(TestOverlay.Apply(v1, null)[0], false);
        Assert.Equal(TermOrigin.Off, off.Origin);

        var v2 = Version("GitHub", enabled: false);
        var shownOff = TestOverlay.Apply(v2, TestOverlay.Collect(v1, null, [off]))[0];
        var edited = TestOverlay.Edit(shownOff, shownOff.Values with { Written = "GH" });
        Assert.Equal((false, true), (edited.Values.Enabled, edited.Edit!.Base!.Enabled));

        var later = TestOverlay.Apply(Version("GitHub"), TestOverlay.Collect(v2, null, [edited]))[0];
        Assert.Equal((new TermValues("get hub", "GH", Enabled: false), (TermReview?)null), (later.Values, later.Review));
    }

    [Fact]
    public void Turning_on_an_off_row_whose_shipped_row_is_disabled_authors_an_edited_turn_on()
    {
        var v1 = Version("GitHub");
        var off = TestOverlay.SetEnabled(TestOverlay.Apply(v1, null)[0], false);
        var v2 = Version("GitHub", enabled: false);
        var shownOff = TestOverlay.Apply(v2, TestOverlay.Collect(v1, null, [off]))[0];

        var on = TestOverlay.SetEnabled(shownOff, true);
        Assert.Equal((TermOrigin.Edited, true, BuiltInTermIntent.Edited), (on.Origin, on.Values.Enabled, on.Edit!.Intent));

        // While the version ships the row on, Turn on only takes the off entry away.
        var shipped = TestOverlay.SetEnabled(off, true);
        Assert.Equal((TermOrigin.Shipped, (BuiltInTermEdit?)null), (shipped.Origin, shipped.Edit));
    }

    [Fact]
    public void A_field_both_sides_changed_asks_and_resolving_returns_the_resolved_row()
    {
        var v1 = Version("GitHub");
        var edited = TestOverlay.Edit(TestOverlay.Apply(v1, null)[0], new TermValues("get hub", "GH"));
        var v2 = Version("GitHub Inc");
        var asked = TestOverlay.Apply(v2, TestOverlay.Collect(v1, null, [edited]))[0];
        Assert.Equal(new TermReview(new TermValues("get hub", "GH"), new TermValues("get hub", "GitHub Inc"), TermFields.Written), asked.Review);

        var kept = TestOverlay.ResolveReview(asked, TermReviewChoice.KeepMine);
        Assert.Equal((new TermValues("get hub", "GH"), (TermReview?)null), (kept.Values, kept.Review));
        var updated = TestOverlay.ResolveReview(asked, TermReviewChoice.UseUpdated);
        Assert.Equal(
            (TermOrigin.Pinned, new TermValues("get hub", "GitHub Inc"), BuiltInTermIntent.Pinned),
            (updated.Origin, updated.Values, updated.Edit!.Intent));
        Assert.Same(edited, TestOverlay.ResolveReview(edited, TermReviewChoice.KeepMine));
    }

    [Fact]
    public void Collect_refuses_a_repeated_key_and_a_row_it_would_not_give_and_writes_one_order()
    {
        var v1 = Version("GitHub");
        var rows = TestOverlay.Apply(v1, null);
        var edited = TestOverlay.Edit(rows[0], rows[0].Values with { Written = "GH" });
        var copilotOff = TestOverlay.SetEnabled(rows[1], false);
        var added = TestOverlay.Add(new TermValues("gh cli", "GitHub CLI"));

        Assert.Throws<ArgumentException>(() => TestOverlay.Collect(v1, null, [edited, TestOverlay.Add(new TermValues("get hub", "Again"))]));
        Assert.Throws<ArgumentException>(() => TestOverlay.Collect(v1, null, [edited with { Values = new TermValues("get hub", "Not from its entry") }]));
        Assert.Throws<ArgumentException>(() => TestOverlay.Edit(edited with { Values = new TermValues("get hub", "Forged") }, new TermValues("get hub", "X")));
        Assert.Throws<ArgumentException>(() => TestOverlay.Edit(edited, new TermValues("get hub", "Half \uD83D")));

        // Shipped rows' entries in shipped order whatever the order given, then additions in the order given.
        var document = TestOverlay.Collect(v1, null, [added, copilotOff, edited])!;
        Assert.Equal(["get hub", "copilot", "gh cli"], document.Terms.Select(term => term.Key.Value));
    }
}
