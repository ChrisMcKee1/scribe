using Scribe.Core.Libraries;
using static Scribe.Core.Tests.Libraries.Overlay.OverlayTestData;

namespace Scribe.Core.Tests.Libraries.Overlay;

/// <summary>
/// O-1: the user's intent lasts until Restore built-in values, whatever later versions ship (review finding R4), through
/// real saves and loads (collect, write, read back, apply). Astra's R4 sequences, her A7 sequence, and the reset that
/// ends an intent on purpose. That an authored row keeps its tier against another library, and its place in the
/// glossary, is composition's to check at integration; here it stays authored, which is what composition ranks by.
/// </summary>
public sealed class BuiltInLibraryOverlayUpgradeSequenceTests
{
    private static readonly TermValues GetHub = T("get hub", "GitHub");
    private static readonly TermValues Copilot = T("copilot", "Copilot");
    private static readonly TermValues OctoCat = T("octo cat", "Octocat");

    [Fact]
    public void An_edit_a_later_version_adopts_stays_authored_as_pinned_and_keeps_its_base()
    {
        // R4 sequence 1: edit, then upstream adopts the value.
        var v1 = Shipped(GetHub, Copilot);
        var original = BuiltInOverlay.Apply(v1, null);
        var edited = BuiltInOverlay.Edit(Row(original, "get hub"), T("get hub", "GitHub Enterprise"));
        var (stored, _) = SaveAndReload(v1, null, Replace(original, Row(original, "get hub"), edited));
        Assert.Equal(BuiltInTermIntent.Edited, Assert.Single(stored!.Terms).Intent);

        var v2 = Shipped(T("get hub", "GitHub Enterprise"), Copilot);
        var adopted = Row(BuiltInOverlay.Apply(v2, stored), "get hub");

        Assert.Equal(TermOrigin.Pinned, adopted.Origin);
        Assert.NotEqual(TermOrigin.Shipped, adopted.Origin);
        Assert.Equal(T("get hub", "GitHub Enterprise"), adopted.Values);
        Assert.Null(adopted.Review);

        // Saving again in that version (the user changes another term) keeps the intent and its base as they were.
        var rows = BuiltInOverlay.Apply(v2, stored);
        rows = Replace(rows, Row(rows, "copilot"), BuiltInOverlay.Edit(Row(rows, "copilot"), T("copilot", "GitHub Copilot")));
        var (again, reloaded) = SaveAndReload(v2, stored, rows);
        var entry = Assert.Single(again!.Terms, term => term.Key == K("get hub"));
        Assert.Equal(BuiltInTermIntent.Edited, entry.Intent);
        Assert.Equal(GetHub, entry.Base);
        Assert.Equal(TermOrigin.Pinned, Row(reloaded, "get hub").Origin);

        // A later change to the field the user wrote asks rather than taking over.
        var v3 = Shipped(T("get hub", "GitHub, Inc."), Copilot);
        var asked = Row(BuiltInOverlay.Apply(v3, again), "get hub");
        Assert.Equal(T("get hub", "GitHub Enterprise"), asked.Values);
        Assert.Equal(TermFields.Written, asked.Review?.Differing);
    }

    [Fact]
    public void An_addition_a_later_version_ships_stays_authored_in_the_shipped_rows_place()
    {
        // R4 sequence 2: add, then upstream ships the key.
        var v1 = Shipped(GetHub);
        var rows = BuiltInOverlay.Apply(v1, null);
        var added = BuiltInOverlay.Add(T("gh cli", "GitHub CLI"));
        var (stored, reloaded) = SaveAndReload(v1, null, [.. rows, added]);
        Assert.Equal(TermOrigin.Added, Row(reloaded, "gh cli").Origin);

        var adopting = Shipped(GetHub, T("gh cli", "GitHub CLI"));
        var pinned = Row(BuiltInOverlay.Apply(adopting, stored), "gh cli");
        Assert.Equal(TermOrigin.Pinned, pinned.Origin);
        Assert.Equal(T("gh cli", "GitHub CLI"), pinned.Shipped);
        Assert.Equal(BuiltInTermIntent.Added, pinned.Edit?.Intent);

        var differing = Shipped(GetHub, T("gh cli", "GH CLI"));
        var kept = Row(BuiltInOverlay.Apply(differing, stored), "gh cli");
        Assert.Equal(TermOrigin.Added, kept.Origin);
        Assert.Equal(T("gh cli", "GitHub CLI"), kept.Values);
        Assert.Equal(T("gh cli", "GH CLI"), kept.Shipped);

        // Saved in the version that ships it, the addition is still the user's.
        var (again, _) = SaveAndReload(differing, stored, BuiltInOverlay.Apply(differing, stored));
        Assert.Equal(BuiltInTermIntent.Added, Assert.Single(again!.Terms).Intent);
    }

    [Fact]
    public void A_term_turned_off_stays_off_after_a_version_drops_it_and_a_later_one_brings_it_back()
    {
        // R4 sequence 3: turn off, upstream removes the row, then reintroduces it; the start in between loads only.
        var v1 = Shipped(GetHub, OctoCat);
        var original = BuiltInOverlay.Apply(v1, null);
        var off = BuiltInOverlay.SetEnabled(Row(original, "octo cat"), enabled: false);
        var (stored, _) = SaveAndReload(v1, null, Replace(original, Row(original, "octo cat"), off));
        Assert.Equal(BuiltInTermIntent.Off, Assert.Single(stored!.Terms).Intent);

        var v2 = Shipped(GetHub);
        Assert.DoesNotContain(BuiltInOverlay.Apply(v2, stored), row => row.Key == K("octo cat"));

        var v3 = Shipped(GetHub, T("octo cat", "Octocat"));
        var back = Row(BuiltInOverlay.Apply(v3, stored), "octo cat");
        Assert.Equal(TermOrigin.Off, back.Origin);
        Assert.False(back.Values.Enabled);
    }

    [Fact]
    public void A_term_turned_off_stays_off_through_a_save_made_while_a_version_did_not_ship_it()
    {
        // Astra's A7 sequence: turn a shipped term off, apply a version without it, edit another term there, collect and
        // save, apply a version that ships it again. The off entry has no row in between, so only the committed document
        // carries it through the Save.
        var v1 = Shipped(GetHub, Copilot, OctoCat);
        var rows = BuiltInOverlay.Apply(v1, null);
        rows = Replace(rows, Row(rows, "octo cat"), BuiltInOverlay.SetEnabled(Row(rows, "octo cat"), enabled: false));
        var (committed, _) = SaveAndReload(v1, null, rows);

        var v2 = Shipped(GetHub, Copilot);
        var withoutIt = BuiltInOverlay.Apply(v2, committed);
        Assert.DoesNotContain(withoutIt, row => row.Key == K("octo cat"));
        withoutIt = Replace(withoutIt, Row(withoutIt, "copilot"), BuiltInOverlay.Edit(Row(withoutIt, "copilot"), T("copilot", "GitHub Copilot")));
        var (saved, _) = SaveAndReload(v2, committed, withoutIt);

        var v3 = Shipped(GetHub, Copilot, OctoCat);
        var reloaded = BuiltInOverlay.Apply(v3, saved);
        Assert.Equal(TermOrigin.Off, Row(reloaded, "octo cat").Origin);
        Assert.False(Row(reloaded, "octo cat").Values.Enabled);
        Assert.Equal(T("copilot", "GitHub Copilot"), Row(reloaded, "copilot").Values);
    }

    [Fact]
    public void Restoring_the_shipped_values_ends_each_intent_so_later_versions_apply_as_shipped()
    {
        var v1 = Shipped(GetHub, Copilot, OctoCat);
        var rows = BuiltInOverlay.Apply(v1, null);
        rows = Replace(rows, Row(rows, "get hub"), BuiltInOverlay.Edit(Row(rows, "get hub"), T("get hub", "GitHub Enterprise")));
        rows = Replace(rows, Row(rows, "octo cat"), BuiltInOverlay.SetEnabled(Row(rows, "octo cat"), enabled: false));
        var gh = BuiltInOverlay.Add(T("gh cli", "GitHub CLI"));
        var (stored, authored) = SaveAndReload(v1, null, [.. rows, gh]);
        Assert.Equal(3, stored!.Terms.Count);

        var reset = authored
            .Select(row => BuiltInOverlay.RestoreShipped(row))
            .Where(row => row is not null)
            .Select(row => row!)
            .ToArray();
        var (none, reloaded) = SaveAndReload(v1, stored, reset);

        Assert.Null(none);
        AssertSameRows(BuiltInOverlay.Apply(v1, null), reloaded);

        var v2 = Shipped(T("get hub", "GitHub, Inc."), Copilot, T("octo cat", "The Octocat"), T("gh cli", "GH CLI"));
        Assert.All(BuiltInOverlay.Apply(v2, none), row => Assert.Equal(TermOrigin.Shipped, row.Origin));
    }

    [Fact]
    public void A_term_turned_off_stays_off_when_it_is_edited_in_a_version_that_ships_it_off()
    {
        // Round 2, A1 (Astra's sequence), through saves and loads: turned off in v1; v2 ships it off and the user edits
        // only its Written there; v3 ships it on again. The off intent is the user's, so it is still off, and nothing asks.
        var v1 = Shipped(GetHub, OctoCat);
        var rows = BuiltInOverlay.Apply(v1, null);
        rows = Replace(rows, Row(rows, "octo cat"), BuiltInOverlay.SetEnabled(Row(rows, "octo cat"), enabled: false));
        var (committed, _) = SaveAndReload(v1, null, rows);

        var v2 = Shipped(GetHub, T("octo cat", "Octocat", enabled: false));
        var inV2 = BuiltInOverlay.Apply(v2, committed);
        Assert.Equal(TermOrigin.Off, Row(inV2, "octo cat").Origin);
        inV2 = Replace(inV2, Row(inV2, "octo cat"), BuiltInOverlay.Edit(Row(inV2, "octo cat"), T("octo cat", "Octo Cat", enabled: false)));
        var (saved, reloadedInV2) = SaveAndReload(v2, committed, inV2);
        AssertSameRows(inV2, reloadedInV2);

        var v3 = Shipped(GetHub, OctoCat);
        var inV3 = Row(BuiltInOverlay.Apply(v3, saved), "octo cat");
        Assert.Equal(T("octo cat", "Octo Cat", enabled: false), inV3.Values);
        Assert.Null(inV3.Review);
    }

    [Fact]
    public void Turning_a_term_back_on_while_the_version_in_use_ships_it_off_keeps_it_on_as_the_users_choice()
    {
        // Round 2, part 2, G1 (the integrator's decision): the user turned an enabled shipped term off; a later version
        // ships it off too; the user clicks Turn on term. Removing the off intent would leave the row as that version ships
        // it, off, so the click would do nothing. The turn-on is recorded instead, as the user's own value of the check box
        // (authored on) while the other fields keep inheriting: it stays on in later versions whatever they ship for the
        // check box, and a correction to Written still reaches the row.
        var v1 = Shipped(GetHub, OctoCat);
        var rows = BuiltInOverlay.Apply(v1, null);
        rows = Replace(rows, Row(rows, "octo cat"), BuiltInOverlay.SetEnabled(Row(rows, "octo cat"), enabled: false));
        var (committed, _) = SaveAndReload(v1, null, rows);

        var shippedOff = T("octo cat", "Octocat", enabled: false);
        var v2 = Shipped(GetHub, shippedOff);
        var inV2 = BuiltInOverlay.Apply(v2, committed);
        Assert.Equal(TermOrigin.Off, Row(inV2, "octo cat").Origin);

        var turnedOn = BuiltInOverlay.SetEnabled(Row(inV2, "octo cat"), enabled: true);

        Assert.Equal(T("octo cat", "Octocat"), turnedOn.Values);
        Assert.Equal(TermOrigin.Edited, turnedOn.Origin);
        Assert.Null(turnedOn.Review);
        Assert.Equal(new BuiltInTermEdit(K("octo cat"), BuiltInTermIntent.Edited, shippedOff, shippedOff with { Enabled = true }), turnedOn.Edit);
        var (saved, reloadedInV2) = SaveAndReload(v2, committed, Replace(inV2, Row(inV2, "octo cat"), turnedOn));
        Assert.Equal(T("octo cat", "Octocat"), Row(reloadedInV2, "octo cat").Values);

        // A later version ships it on with a corrected Written: on, and the correction applies; one that ships it off
        // again leaves it on.
        var shippedOnCorrected = Row(BuiltInOverlay.Apply(Shipped(GetHub, T("octo cat", "The Octocat")), saved), "octo cat");
        Assert.Equal(T("octo cat", "The Octocat"), shippedOnCorrected.Values);
        Assert.Null(shippedOnCorrected.Review);
        var shippedOffAgain = Row(BuiltInOverlay.Apply(Shipped(GetHub, T("octo cat", "The Octocat", enabled: false)), saved), "octo cat");
        Assert.Equal(T("octo cat", "The Octocat"), shippedOffAgain.Values);
        Assert.Null(shippedOffAgain.Review);

        // In a version that ships the term on, Turn on term simply ends the off intent: the row is shipped again.
        var offInV1 = Row(BuiltInOverlay.Apply(v1, committed), "octo cat");
        Assert.Equal(Row(BuiltInOverlay.Apply(v1, null), "octo cat"), BuiltInOverlay.SetEnabled(offInV1, enabled: true));
    }

    [Fact]
    public void An_edit_that_leaves_a_field_alone_lets_later_versions_keep_correcting_that_field()
    {
        // The user changed only Spoken; each later version's Written reaches the row, across saves, and the user is
        // asked about nothing, because the user wrote nothing there.
        var v1 = Shipped(GetHub);
        var renamed = BuiltInOverlay.Edit(Assert.Single(BuiltInOverlay.Apply(v1, null)), T("git hub", "GitHub"));
        var (stored, _) = SaveAndReload(v1, null, [renamed]);

        foreach (var written in new[] { "GitHub, Inc.", "GitHub Corp", "GitHub" })
        {
            var version = Shipped(T("get hub", written));
            var row = Assert.Single(BuiltInOverlay.Apply(version, stored));
            Assert.Equal(T("git hub", written), row.Values);
            Assert.Null(row.Review);
            (stored, _) = SaveAndReload(version, stored, [row]);
        }
    }
}
