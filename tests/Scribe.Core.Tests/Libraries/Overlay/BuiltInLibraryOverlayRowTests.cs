using Scribe.Core.Libraries;
using static Scribe.Core.Tests.Libraries.Overlay.OverlayTestData;

namespace Scribe.Core.Tests.Libraries.Overlay;

/// <summary>
/// The editing commands on a built-in row (contract 3.2.1, transitions): Edit, Turn off or on, Restore built-in values,
/// Add, and resolving a review, with what each leaves in the document and what each refuses.
/// </summary>
public sealed class BuiltInLibraryOverlayRowTests
{
    private static readonly TermValues GetHub = T("get hub", "GitHub");

    [Fact]
    public void Editing_a_shipped_row_starts_an_edit_based_on_the_shipped_values()
    {
        var shipped = Assert.Single(BuiltInOverlay.Apply(Shipped(GetHub), null));

        var edited = BuiltInOverlay.Edit(shipped, T("get hub", "GitHub Enterprise"));

        Assert.Equal(
            new LibraryRow(
                K("get hub"),
                T("get hub", "GitHub Enterprise"),
                TermOrigin.Edited,
                GetHub,
                new BuiltInTermEdit(K("get hub"), BuiltInTermIntent.Edited, GetHub, T("get hub", "GitHub Enterprise"))),
            edited);
    }

    [Fact]
    public void A_value_typed_back_to_the_shipped_one_stays_authored_as_pinned_until_restore()
    {
        var shipped = Assert.Single(BuiltInOverlay.Apply(Shipped(GetHub), null));
        var edited = BuiltInOverlay.Edit(shipped, T("get hub", "GitHub Enterprise"));

        var typedBack = BuiltInOverlay.Edit(edited, GetHub);

        Assert.Equal(TermOrigin.Pinned, typedBack.Origin);
        Assert.Equal(GetHub, typedBack.Values);
        Assert.NotNull(typedBack.Edit);
        Assert.NotNull(BuiltInOverlay.Collect(Shipped(GetHub), null, [typedBack]));

        var restored = BuiltInOverlay.RestoreShipped(typedBack);
        Assert.Equal(shipped, restored);
        Assert.Null(BuiltInOverlay.Collect(Shipped(GetHub), null, [restored!]));
    }

    [Fact]
    public void An_edit_that_changes_nothing_leaves_the_row_as_it_is()
    {
        var shipped = Assert.Single(BuiltInOverlay.Apply(Shipped(GetHub), null));

        Assert.Same(shipped, BuiltInOverlay.Edit(shipped, T("get hub", "GitHub")));
        var edited = BuiltInOverlay.Edit(shipped, T("get hub", "GitHub Enterprise"));
        Assert.Same(edited, BuiltInOverlay.Edit(edited, T("get hub", "GitHub Enterprise")));
        Assert.Same(shipped, BuiltInOverlay.SetEnabled(shipped, enabled: true));
    }

    [Fact]
    public void Turning_a_shipped_row_off_records_an_off_intent_and_turning_it_on_removes_it()
    {
        var shipped = Assert.Single(BuiltInOverlay.Apply(Shipped(GetHub), null));

        var off = BuiltInOverlay.SetEnabled(shipped, enabled: false);

        Assert.Equal(
            new LibraryRow(K("get hub"), GetHub with { Enabled = false }, TermOrigin.Off, GetHub, new BuiltInTermEdit(K("get hub"), BuiltInTermIntent.Off, GetHub, null)),
            off);
        Assert.Equal(shipped, BuiltInOverlay.SetEnabled(off, enabled: true));

        // An edit that changes only the check box is the same command.
        Assert.Equal(off, BuiltInOverlay.Edit(shipped, GetHub with { Enabled = false }));
        Assert.Equal(shipped, BuiltInOverlay.Edit(off, GetHub));
    }

    [Fact]
    public void Editing_a_turned_off_row_keeps_it_off_as_an_edit_of_the_shipped_values()
    {
        var off = BuiltInOverlay.SetEnabled(Assert.Single(BuiltInOverlay.Apply(Shipped(GetHub), null)), enabled: false);

        var edited = BuiltInOverlay.Edit(off, T("get hub", "GitHub Enterprise", enabled: false));

        Assert.Equal(TermOrigin.Edited, edited.Origin);
        Assert.Equal(T("get hub", "GitHub Enterprise", enabled: false), edited.Values);
        Assert.Equal(new BuiltInTermEdit(K("get hub"), BuiltInTermIntent.Edited, GetHub, T("get hub", "GitHub Enterprise", enabled: false)), edited.Edit);

        var on = BuiltInOverlay.SetEnabled(edited, enabled: true);
        Assert.Equal(T("get hub", "GitHub Enterprise"), on.Values);
        Assert.Equal(TermOrigin.Edited, on.Origin);
    }

    [Fact]
    public void Turning_an_authored_row_off_and_on_again_gives_back_the_same_row()
    {
        var shipped = Shipped(GetHub, T("copilot", "Copilot"));
        var document = Document(
            Edited("get hub", GetHub, T("git hub", "GitHub")),
            Pinned("copilot", T("copilot", "Copilot"), T("copilot", "Copilot")),
            Added("gh cli", T("gh cli", "GitHub CLI")));

        foreach (var row in BuiltInOverlay.Apply(shipped, document))
        {
            var off = BuiltInOverlay.SetEnabled(row, enabled: false);
            Assert.False(off.Values.Enabled);
            Assert.Equal(row, BuiltInOverlay.SetEnabled(off, enabled: true));
        }
    }

    [Fact]
    public void Restore_built_in_values_removes_the_intent_and_is_no_command_for_a_row_that_does_not_ship()
    {
        var shipped = Shipped(GetHub, T("copilot", "Copilot"), T("octo cat", "Octocat"), T("gh cli", "GH CLI"));
        var document = Document(
            Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")),
            Pinned("copilot", T("copilot", "Copilot"), T("copilot", "GitHub Copilot")),
            Off("octo cat", T("octo cat", "Octocat")),
            Added("gh cli", T("gh cli", "GitHub CLI")),
            Added("zulu", T("zulu", "Zulu")),
            Edited("old term", T("old term", "Old"), T("old term", "Older")));
        var rows = BuiltInOverlay.Apply(shipped, document);
        var asShipped = BuiltInOverlay.Apply(shipped, null);

        foreach (var key in new[] { "get hub", "copilot", "octo cat", "gh cli" })
        {
            Assert.Equal(Row(asShipped, key), BuiltInOverlay.RestoreShipped(Row(rows, key)));
        }

        Assert.Null(BuiltInOverlay.RestoreShipped(Row(rows, "zulu")));
        Assert.Null(BuiltInOverlay.RestoreShipped(Row(rows, "old term")));
        Assert.Same(Row(asShipped, "get hub"), BuiltInOverlay.RestoreShipped(Row(asShipped, "get hub")));
    }

    [Fact]
    public void An_added_row_is_keyed_by_its_spoken_form_and_needs_one()
    {
        var added = BuiltInOverlay.Add(T("  GH  CLI ", "GitHub CLI"));

        Assert.Equal("GH  CLI", added.Key.Value);
        Assert.Equal(TermOrigin.Added, added.Origin);
        Assert.Equal(T("  GH  CLI ", "GitHub CLI"), added.Values);
        Assert.Null(added.Shipped);
        Assert.Equal(new BuiltInTermEdit(K("GH  CLI"), BuiltInTermIntent.Added, null, T("  GH  CLI ", "GitHub CLI")), added.Edit);
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Add(T(" \t ", "Nothing")));
        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.Add(null!));
    }

    [Fact]
    public void Editing_an_added_row_replaces_its_values_and_keeps_its_key()
    {
        var added = BuiltInOverlay.Add(T("gh cli", "GitHub CLI"));

        var renamed = BuiltInOverlay.Edit(added, T("github cli", "GitHub CLI", wholeWord: false));

        Assert.Equal(K("gh cli"), renamed.Key);
        Assert.Equal(TermOrigin.Added, renamed.Origin);
        Assert.Equal(T("github cli", "GitHub CLI", wholeWord: false), renamed.Values);
        Assert.Equal(BuiltInTermIntent.Added, renamed.Edit?.Intent);
    }

    [Fact]
    public void Keep_my_changes_records_the_shipped_values_and_use_updated_values_pins_them()
    {
        var v2 = T("get hub", "GitHub, Inc.");
        var document = Document(Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")));
        var asking = Assert.Single(BuiltInOverlay.Apply(Shipped(v2), document));

        var kept = BuiltInOverlay.ResolveReview(asking, TermReviewChoice.KeepMine);
        var updated = BuiltInOverlay.ResolveReview(asking, TermReviewChoice.UseUpdated);

        Assert.Equal(document.Terms[0] with { Acknowledged = v2 }, kept.Edit);
        Assert.Equal(T("get hub", "GitHub Enterprise"), kept.Values);
        Assert.Equal(new BuiltInTermEdit(K("get hub"), BuiltInTermIntent.Pinned, v2, v2), updated.Edit);
        Assert.Equal(v2, updated.Values);
        Assert.Equal(TermOrigin.Pinned, updated.Origin);

        // A row with no review is left as it is by either choice.
        Assert.Same(kept, BuiltInOverlay.ResolveReview(kept, TermReviewChoice.UseUpdated));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuiltInOverlay.ResolveReview(asking, (TermReviewChoice)7));
    }

    [Fact]
    public void An_edit_after_an_upgrade_shows_exactly_what_the_user_typed()
    {
        // The user wrote "GitHub Enterprise" over "GitHub"; a later version ships "GitHub, Inc." and the row asks. The user
        // types the old shipped value "GitHub": the row must show "GitHub", not the new shipped value (replacing the
        // user's value alone, against the old base, would read "GitHub" as "left alone" and show "GitHub, Inc.").
        var document = Document(Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")));
        var asking = Assert.Single(BuiltInOverlay.Apply(Shipped(T("get hub", "GitHub, Inc.")), document));
        Assert.Equal(TermFields.Written, asking.Review?.Differing);

        var typed = BuiltInOverlay.Edit(asking, T("get hub", "GitHub"));

        Assert.Equal(T("get hub", "GitHub"), typed.Values);
        Assert.Null(typed.Review);
        Assert.Equal(TermOrigin.Edited, typed.Origin);
    }

    [Fact]
    public void An_edit_asks_nothing_about_the_field_it_changes_and_keeps_open_questions_about_the_others()
    {
        // Both Spoken and Written were changed by the user and, differently, by a later version: two questions. The user
        // retypes Written: that question is answered, the Spoken one stays.
        var @base = T("get hub", "GitHub");
        var document = Document(Edited("get hub", @base, T("git hub", "GitHub Enterprise")));
        var shipped = T("Get Hub", "GitHub, Inc.");
        var asking = Assert.Single(BuiltInOverlay.Apply(Shipped(shipped), document));
        Assert.Equal(TermFields.Spoken | TermFields.Written, asking.Review?.Differing);

        var typed = BuiltInOverlay.Edit(asking, T("git hub", "GitHub Enterprise Server"));

        Assert.Equal(T("git hub", "GitHub Enterprise Server"), typed.Values);
        Assert.Equal(new TermReview(typed.Values, shipped, TermFields.Spoken), typed.Review);

        // The same for a pinned entry.
        var pinnedDocument = Document(Pinned("get hub", @base, T("git hub", "GitHub Enterprise")));
        var pinnedAsking = Assert.Single(BuiltInOverlay.Apply(Shipped(shipped), pinnedDocument));
        Assert.Equal(TermFields.Spoken | TermFields.Written, pinnedAsking.Review?.Differing);
        var pinnedTyped = BuiltInOverlay.Edit(pinnedAsking, T("git hub", "GitHub Enterprise Server"));
        Assert.Equal(new TermReview(pinnedTyped.Values, shipped, TermFields.Spoken), pinnedTyped.Review);
        Assert.Equal(@base, pinnedTyped.Edit?.Base);
    }

    [Fact]
    public void An_edit_to_one_field_leaves_the_other_fields_following_later_versions()
    {
        // WholeWord took a shipped change the user never made; the user then edits Written. A version that changes
        // WholeWord back must reach the row too, because the user never wrote that field.
        var document = Document(Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")));
        var v2 = T("get hub", "GitHub", wholeWord: false);
        var row = Assert.Single(BuiltInOverlay.Apply(Shipped(v2), document));
        Assert.Equal(T("get hub", "GitHub Enterprise", wholeWord: false), row.Values);

        var edited = BuiltInOverlay.Edit(row, T("get hub", "GitHub Enterprise Server", wholeWord: false));
        var (stored, _) = SaveAndReload(Shipped(v2), document, [edited]);

        var v3 = Assert.Single(BuiltInOverlay.Apply(Shipped(T("get hub", "GitHub", wholeWord: true)), stored));
        Assert.Equal(T("get hub", "GitHub Enterprise Server", wholeWord: true), v3.Values);
        Assert.Null(v3.Review);
    }

    [Fact]
    public void Row_commands_refuse_custom_rows_and_rows_the_overlay_did_not_give()
    {
        var custom = LibraryRow.Custom(GetHub);
        var shipped = Assert.Single(BuiltInOverlay.Apply(Shipped(GetHub), null));
        var forged = shipped with { Values = T("get hub", "Forged") };
        var detached = new LibraryRow(K("get hub"), GetHub, TermOrigin.Edited, GetHub);

        foreach (var row in new[] { custom, forged, detached })
        {
            Assert.Throws<ArgumentException>(() => BuiltInOverlay.Edit(row, T("get hub", "X")));
            Assert.Throws<ArgumentException>(() => BuiltInOverlay.SetEnabled(row, enabled: false));
            Assert.Throws<ArgumentException>(() => BuiltInOverlay.RestoreShipped(row));
            Assert.Throws<ArgumentException>(() => BuiltInOverlay.ResolveReview(row, TermReviewChoice.KeepMine));
        }

        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.Edit(null!, GetHub));
        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.Edit(shipped, null!));
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Edit(shipped, new TermValues("get hub", null!)));
        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.SetEnabled(null!, enabled: true));
        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.RestoreShipped(null!));
        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.ResolveReview(null!, TermReviewChoice.KeepMine));
    }

    [Fact]
    public void Row_commands_never_change_the_row_they_are_given()
    {
        var document = Document(Edited("get hub", GetHub, T("get hub", "GitHub Enterprise")));
        var row = Assert.Single(BuiltInOverlay.Apply(Shipped(T("get hub", "GitHub, Inc.")), document));
        var copy = row with { };

        BuiltInOverlay.Edit(row, T("get hub", "X"));
        BuiltInOverlay.SetEnabled(row, enabled: false);
        BuiltInOverlay.RestoreShipped(row);
        BuiltInOverlay.ResolveReview(row, TermReviewChoice.KeepMine);
        BuiltInOverlay.ResolveReview(row, TermReviewChoice.UseUpdated);

        Assert.Equal(copy, row);
        Assert.Equal(T("get hub", "GitHub Enterprise"), document.Terms[0].Value);
    }

    [Fact]
    public void Authored_terms_are_the_values_of_edited_pinned_and_added_entries_in_document_order()
    {
        var document = Document(
            Off("octo cat", T("octo cat", "Octocat")),
            Added("gh cli", T("gh cli", "GitHub CLI")),
            Edited("get hub", GetHub, T("git hub", "GitHub", enabled: false)),
            Pinned("copilot", T("copilot", "Copilot"), T("copilot", "GitHub Copilot")));

        Assert.Equal(
            [T("gh cli", "GitHub CLI"), T("git hub", "GitHub", enabled: false), T("copilot", "GitHub Copilot")],
            BuiltInOverlay.AuthoredTerms(document));
        Assert.Empty(BuiltInOverlay.AuthoredTerms(Document(Off("octo cat", T("octo cat", "Octocat")))));
        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.AuthoredTerms(null!));
    }

    [Fact]
    public void There_is_one_overlay_and_it_is_the_interface_the_other_streams_use()
    {
        IBuiltInLibraryOverlay overlay = BuiltInLibraryOverlay.Instance;

        Assert.Same(BuiltInLibraryOverlay.Instance, overlay);
    }
}
