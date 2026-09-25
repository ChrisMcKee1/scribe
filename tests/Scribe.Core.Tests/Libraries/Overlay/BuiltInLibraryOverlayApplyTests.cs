using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using static Scribe.Core.Tests.Libraries.Overlay.OverlayTestData;

namespace Scribe.Core.Tests.Libraries.Overlay;

/// <summary>
/// How an edits document applies to the rows a version ships (contract 3.2.1): the per-field upgrade merge of an edited
/// entry and its questions (O-2), pinned, off and added entries, rows a version stopped shipping (O-3), the order of the
/// rows, and an edited row whose spoken form the user renamed (O-9). A review, when a field asks, lists every field in
/// which the user's version and the updated one differ (round 2, A4).
/// </summary>
public sealed class BuiltInLibraryOverlayApplyTests
{
    private enum FieldState
    {
        Untouched,          // U = B, S = B
        ShippedChanged,     // U = B, S != B: the shipped change reaches the row
        UserChanged,        // U != B, S = B: the user's value stays
        BothSame,           // U != B, S != B, U = S
        BothDiffer,         // U != B, S != B, U != S: the user's value stays and the row asks
        BothDifferKept,     // as BothDiffer, and Keep my changes already recorded this S (A = S): no question
    }

    [Fact]
    public void With_no_document_every_row_is_shipped_in_shipped_order()
    {
        var shipped = Shipped(T("get hub", "GitHub"), T("copilot", "Copilot"), T("octo cat", "Octocat"));

        var rows = BuiltInOverlay.Apply(shipped, edits: null);

        Assert.Equal(["get hub", "copilot", "octo cat"], rows.Select(row => row.Key.Value));
        Assert.All(rows, row =>
        {
            Assert.Equal(TermOrigin.Shipped, row.Origin);
            Assert.Equal(row.Values, row.Shipped);
            Assert.Null(row.Edit);
            Assert.Null(row.Review);
        });
    }

    [Fact]
    public void Every_built_in_applies_with_unique_row_keys_which_edits_documents_identify_rows_by()
    {
        // An entry names its shipped row by key; two shipped rows with one key could not be told apart.
        // BuiltInLibraryDataTests.No_library_contradicts_itself keeps the data that way.
        foreach (var library in BuiltInDictionaryLibraries.All)
        {
            var rows = BuiltInOverlay.Apply(library, edits: null);

            Assert.Equal(library.Entries.Count, rows.Count);
            Assert.Equal(rows.Count, rows.Select(row => row.Key).Distinct().Count());
            Assert.All(rows, row => Assert.Equal(TermOrigin.Shipped, row.Origin));
        }
    }

    [Fact]
    public void Every_combination_of_field_changes_merges_field_by_field_and_asks_only_where_both_changed_differently()
    {
        // O-2: every field in every state at once (Spoken and Written have six states, the two flags four, since a flag
        // both sides changed was changed to the same value), with no acknowledgment and with one.
        var stringStates = Enum.GetValues<FieldState>();
        FieldState[] flagStates = [FieldState.Untouched, FieldState.ShippedChanged, FieldState.UserChanged, FieldState.BothSame];
        var combinations = 0;
        foreach (var spoken in stringStates)
        {
            foreach (var written in stringStates)
            {
                foreach (var wholeWord in flagStates)
                {
                    foreach (var enabled in flagStates)
                    {
                        FieldState[] states = [spoken, written, wholeWord, enabled];
                        var anyKept = states.Contains(FieldState.BothDifferKept);
                        foreach (var withAcknowledgment in anyKept ? [true] : new[] { false, true })
                        {
                            CheckEditedMerge(states, withAcknowledgment);
                            combinations++;
                        }
                    }
                }
            }
        }

        Assert.Equal(576 + 400, combinations);
    }

    [Fact]
    public void A_pinned_entry_applies_whole_and_asks_where_the_shipped_value_differs_from_it_and_from_the_acknowledged()
    {
        // O-2 for pinned: per field the shipped value equals the user's, differs and asks, or differs and was kept.
        var states = new[] { "equal", "differs", "kept" };
        var combinations = 0;
        foreach (var spoken in states)
        {
            foreach (var written in states)
            {
                foreach (var wholeWord in states)
                {
                    foreach (var enabled in states)
                    {
                        string[] fields = [spoken, written, wholeWord, enabled];
                        foreach (var withAcknowledgment in fields.Contains("kept") ? [true] : new[] { false, true })
                        {
                            CheckPinned(fields, withAcknowledgment);
                            combinations++;
                        }
                    }
                }
            }
        }

        Assert.Equal(81 + 16, combinations);
    }

    [Fact]
    public void Keep_my_changes_asks_once_and_asks_again_when_a_later_version_changes_the_field_again()
    {
        var v1 = T("get hub", "GitHub");
        var v2 = T("get hub", "GitHub, Inc.");
        var v3 = T("get hub", "GitHub Corp");
        var document = Document(Edited("get hub", v1, T("get hub", "GitHub Enterprise")));

        var asking = Row(BuiltInOverlay.Apply(Shipped(v2), document), "get hub");
        Assert.Equal(new TermReview(T("get hub", "GitHub Enterprise"), v2, TermFields.Written), asking.Review);

        var kept = BuiltInOverlay.ResolveReview(asking, TermReviewChoice.KeepMine);
        Assert.Null(kept.Review);
        Assert.Equal(T("get hub", "GitHub Enterprise"), kept.Values);
        Assert.Equal(v2, kept.Edit!.Acknowledged);
        Assert.Equal(v1, kept.Edit.Base);

        var (stored, reloaded) = SaveAndReload(Shipped(v2), document, [kept]);
        Assert.Null(Row(reloaded, "get hub").Review);

        var again = Row(BuiltInOverlay.Apply(Shipped(v3), stored), "get hub");
        Assert.Equal(new TermReview(T("get hub", "GitHub Enterprise"), v3, TermFields.Written), again.Review);
    }

    [Fact]
    public void Use_updated_values_takes_the_shipped_values_pinned_and_a_later_change_asks()
    {
        var v1 = T("get hub", "GitHub");
        var v2 = T("get hub", "GitHub, Inc.", wholeWord: true);
        var document = Document(Edited("get hub", v1, T("get hub", "GitHub Enterprise"), acknowledged: T("get hub", "GitHub.com")));
        var asking = Row(BuiltInOverlay.Apply(Shipped(v2), document), "get hub");

        var updated = BuiltInOverlay.ResolveReview(asking, TermReviewChoice.UseUpdated);

        Assert.Equal(TermOrigin.Pinned, updated.Origin);
        Assert.Equal(v2, updated.Values);
        Assert.Null(updated.Review);
        Assert.Equal(new BuiltInTermEdit(K("get hub"), BuiltInTermIntent.Pinned, v2, v2), updated.Edit);

        var (stored, reloaded) = SaveAndReload(Shipped(v2), document, [updated]);
        Assert.Equal(TermOrigin.Pinned, Row(reloaded, "get hub").Origin);

        // Pinned takes no later shipped change without asking, in any field.
        var v3 = v2 with { WholeWord = false };
        var later = Row(BuiltInOverlay.Apply(Shipped(v3), stored), "get hub");
        Assert.Equal(TermOrigin.Edited, later.Origin);
        Assert.Equal(v2, later.Values);
        Assert.Equal(new TermReview(v2, v3, TermFields.WholeWord), later.Review);
    }

    [Fact]
    public void A_review_lists_every_field_use_updated_values_would_replace_not_only_the_one_that_asks()
    {
        // Round 2, A4 (Astra's case): the user changed Spoken and Written, a later version changed only Written. Only
        // Written asks, but Use updated values replaces both, so the review lists both; which of them conflicts is the
        // shell's to work out from the values, if it wants to show that.
        var getHub = T("get hub", "GitHub");
        var user = T("git hub", "GitHub Enterprise");
        var shipped = T("get hub", "GitHub, Inc.");

        var row = Assert.Single(BuiltInOverlay.Apply(Shipped(shipped), Document(Edited("get hub", getHub, user))));

        Assert.Equal(new TermReview(user, shipped, TermFields.Spoken | TermFields.Written), row.Review);
        Assert.Equal(shipped, BuiltInOverlay.ResolveReview(row, TermReviewChoice.UseUpdated).Values);

        // With nothing asking there is no review, however the two versions differ.
        var alone = Assert.Single(BuiltInOverlay.Apply(Shipped(T("get hub", "GitHub", wholeWord: false)), Document(Edited("get hub", getHub, user))));
        Assert.Equal(T("git hub", "GitHub Enterprise", wholeWord: false), alone.Values);
        Assert.Null(alone.Review);
    }

    [Fact]
    public void A_pinned_review_lists_a_field_already_kept_beside_the_one_that_asks()
    {
        // Keep my changes was chosen for this very Written; the change to WholeWord is new and asks. Use updated values
        // would replace both, so both are listed.
        var user = T("get hub", "GitHub", wholeWord: false);
        var shipped = T("get hub", "GitHub, Inc.", wholeWord: true);
        var entry = Pinned("get hub", T("get hub", "GitHub"), user, acknowledged: T("get hub", "GitHub, Inc.", wholeWord: false));

        var row = Assert.Single(BuiltInOverlay.Apply(Shipped(shipped), Document(entry)));

        Assert.Equal(new TermReview(user, shipped, TermFields.Written | TermFields.WholeWord), row.Review);
    }

    [Fact]
    public void An_off_entry_shows_the_shipped_values_of_the_running_version_turned_off()
    {
        var v1 = T("octo cat", "Octocat");
        var v2 = T("octo cat", "The Octocat", wholeWord: false);
        var document = Document(Off("octo cat", v1));

        var row = Row(BuiltInOverlay.Apply(Shipped(v2), document), "octo cat");

        Assert.Equal(TermOrigin.Off, row.Origin);
        Assert.Equal(v2 with { Enabled = false }, row.Values);
        Assert.Equal(v2, row.Shipped);
        Assert.Same(document.Terms[0], row.Edit);
        Assert.Null(row.Review);
    }

    [Fact]
    public void An_added_entry_stays_authored_when_a_later_version_ships_its_key()
    {
        var added = T("gh cli", "GitHub CLI");
        var document = Document(Added("gh cli", added));

        var before = BuiltInOverlay.Apply(Shipped(T("get hub", "GitHub")), document);
        Assert.Equal(["get hub", "gh cli"], before.Select(row => row.Key.Value));
        Assert.Equal(new LibraryRow(K("gh cli"), added, TermOrigin.Added, null, document.Terms[0]), before[1]);

        // Shipped as the user wrote it: pinned, authored, in the shipped row's place.
        var same = BuiltInOverlay.Apply(Shipped(T("gh cli", "GitHub CLI"), T("get hub", "GitHub")), document);
        Assert.Equal(["gh cli", "get hub"], same.Select(row => row.Key.Value));
        Assert.Equal(new LibraryRow(K("gh cli"), added, TermOrigin.Pinned, added, document.Terms[0]), same[0]);

        // Shipped with other values: still the user's row, marked as added, with the shipped values beside it.
        var other = T("gh cli", "GH CLI");
        var different = BuiltInOverlay.Apply(Shipped(other), document);
        Assert.Equal(new LibraryRow(K("gh cli"), added, TermOrigin.Added, other, document.Terms[0]), Assert.Single(different));
    }

    [Fact]
    public void A_row_a_version_no_longer_ships_keeps_an_edit_and_an_addition_and_leaves_an_off_intent_inert()
    {
        // O-3.
        var v1 = Shipped(T("get hub", "GitHub"), T("copilot", "Copilot"), T("octo cat", "Octocat"));
        var document = Document(
            Edited("get hub", T("get hub", "GitHub"), T("get hub", "GitHub Enterprise")),
            Pinned("copilot", T("copilot", "Copilot"), T("copilot", "Copilot")),
            Off("octo cat", T("octo cat", "Octocat")),
            Added("gh cli", T("gh cli", "GitHub CLI")));
        var v2 = Shipped(T("git hub actions", "GitHub Actions"));

        var rows = BuiltInOverlay.Apply(v2, document);

        Assert.Equal(["git hub actions", "get hub", "copilot", "gh cli"], rows.Select(row => row.Key.Value));
        Assert.Equal(new LibraryRow(K("get hub"), T("get hub", "GitHub Enterprise"), TermOrigin.NoLongerShipped, null, document.Terms[0]), rows[1]);
        Assert.Equal(new LibraryRow(K("copilot"), T("copilot", "Copilot"), TermOrigin.NoLongerShipped, null, document.Terms[1]), rows[2]);
        Assert.Equal(new LibraryRow(K("gh cli"), T("gh cli", "GitHub CLI"), TermOrigin.Added, null, document.Terms[3]), rows[3]);
        Assert.DoesNotContain(rows, row => row.Key == K("octo cat"));

        // Nothing the user did is lost by a Save in this version, and a version that ships the rows again restores them.
        var (stored, reloaded) = SaveAndReload(v2, document, rows);
        AssertSameRows(rows, reloaded);
        var back = BuiltInOverlay.Apply(v1, stored);
        Assert.Equal(TermOrigin.Edited, Row(back, "get hub").Origin);
        Assert.Equal(TermOrigin.Pinned, Row(back, "copilot").Origin);
        Assert.Equal(TermOrigin.Off, Row(back, "octo cat").Origin);
        Assert.Equal(TermOrigin.Added, Row(back, "gh cli").Origin);
    }

    [Fact]
    public void Rows_come_in_shipped_order_with_entries_in_place_then_added_and_no_longer_shipped_rows_in_document_order()
    {
        var shipped = Shipped(T("alpha", "Alpha"), T("bravo", "Bravo"), T("charlie", "Charlie"));
        var document = Document(
            Added("zulu", T("zulu", "Zulu")),
            Edited("x ray", T("x ray", "X-ray"), T("x ray", "Xray")),
            Off("charlie", T("charlie", "Charlie")),
            Added("yankee", T("yankee", "Yankee")),
            Off("whiskey", T("whiskey", "Whiskey")),
            Edited("alpha", T("alpha", "Alpha"), T("alpha", "ALPHA")));

        var rows = BuiltInOverlay.Apply(shipped, document);

        Assert.Equal(["alpha", "bravo", "charlie", "zulu", "x ray", "yankee"], rows.Select(row => row.Key.Value));
        Assert.Equal(
            [TermOrigin.Edited, TermOrigin.Shipped, TermOrigin.Off, TermOrigin.Added, TermOrigin.NoLongerShipped, TermOrigin.Added],
            rows.Select(row => row.Origin));
    }

    [Fact]
    public void An_entry_finds_its_shipped_row_by_key_whatever_the_case_and_edge_spaces_of_either()
    {
        var document = Document(Edited(" Get Hub ", T("get hub", "GitHub"), T("get hub", "GitHub Enterprise")));

        var row = Assert.Single(BuiltInOverlay.Apply(Shipped(T("GET HUB", "GitHub")), document));

        Assert.Equal(TermOrigin.Edited, row.Origin);
        Assert.Equal("Get Hub", row.Key.Value);
        Assert.Equal(T("GET HUB", "GitHub Enterprise"), row.Values);
    }

    [Fact]
    public void A_renamed_built_in_row_is_found_by_its_key_and_a_later_shipped_row_with_its_new_spoken_form_blocks_nothing()
    {
        // O-9 (Astra's question 2): the key identifies the row, the spoken form it writes competes. Which of the two rows
        // with the spoken form "git hub" wins, and that the shipped one shows Not used, is composition's (C-18).
        var v1 = T("get hub", "GitHub");
        var renamed = BuiltInOverlay.Edit(Row(BuiltInOverlay.Apply(Shipped(v1), null), "get hub"), T("git hub", "GitHub"));
        Assert.Equal(K("get hub"), renamed.Key);
        Assert.Equal(K("git hub"), K(renamed.Values.Spoken));
        var document = BuiltInOverlay.Collect(Shipped(v1), null, [renamed]);

        var v2 = Shipped(T("get hub", "GitHub, Inc."), T("git hub", "Git Hub"));
        var rows = BuiltInOverlay.Apply(v2, document);

        Assert.Equal(2, rows.Count);
        var edited = rows[0];
        Assert.Equal(K("get hub"), edited.Key);
        Assert.Equal(TermOrigin.Edited, edited.Origin);
        Assert.Equal(T("git hub", "GitHub, Inc."), edited.Values);
        Assert.Null(edited.Review);
        Assert.Equal(new LibraryRow(K("git hub"), T("git hub", "Git Hub"), TermOrigin.Shipped, T("git hub", "Git Hub")), rows[1]);
        Assert.Equal(2, rows.Count(row => K(row.Values.Spoken) == K("git hub")));

        // Two rows now write the spoken form "git hub", which is the shipped data's doing: collecting and saving go on.
        var (stored, reloaded) = SaveAndReload(v2, document, rows);
        AssertSameDocument(document, stored);
        AssertSameRows(rows, reloaded);
    }

    [Fact]
    public void Apply_refuses_a_custom_library_and_another_librarys_document()
    {
        var custom = new DictionaryLibrary("team-terms", "Team terms", "Custom", null, BuiltIn: false, []);

        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Apply(custom, null));
        Assert.Throws<ArgumentNullException>(() => BuiltInOverlay.Apply(null!, null));
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Apply(Shipped("github"), new BuiltInLibraryEdits("microsoft-azure", [])));
        Assert.Throws<ArgumentException>(() => BuiltInOverlay.Apply(
            Shipped("github"),
            Document(Added("gh cli", T("gh cli", "A")), Added("GH CLI", T("gh cli", "B")))));
        Assert.Empty(BuiltInOverlay.Apply(Shipped("GitHub"), new BuiltInLibraryEdits("github", [])));
    }

    private static void CheckEditedMerge(FieldState[] states, bool withAcknowledgment)
    {
        var spoken = StringField(states[0], @base: "get hub", shipped: "Get Hub", user: "git hub", older: "GET HUB");
        var written = StringField(states[1], @base: "GitHub", shipped: "GitHub, Inc.", user: "GitHub Enterprise", older: "GitHub.com");
        var wholeWord = FlagField(states[2]);
        var enabled = FlagField(states[3]);
        var @base = new TermValues(spoken.Base, written.Base, wholeWord.Base, enabled.Base);
        var user = new TermValues(spoken.User, written.User, wholeWord.User, enabled.User);
        var shipped = new TermValues(spoken.Shipped, written.Shipped, wholeWord.Shipped, enabled.Shipped);
        var acknowledged = withAcknowledgment
            ? new TermValues(spoken.Acknowledged, written.Acknowledged, wholeWord.Shipped, enabled.Shipped)
            : null;

        var expectedValues = new TermValues(spoken.Merged, written.Merged, wholeWord.Merged, enabled.Merged);
        var questions = (Asks(states[0], withAcknowledgment) ? TermFields.Spoken : TermFields.None) |
            (Asks(states[1], withAcknowledgment) ? TermFields.Written : TermFields.None);

        // A review, once a field asks, lists every field the user's version and the updated one differ in (round 2, A4):
        // the fields the user changed alone, and ones already kept, as well as the ones that ask.
        var differing = (Differs(states[0]) ? TermFields.Spoken : TermFields.None) |
            (Differs(states[1]) ? TermFields.Written : TermFields.None) |
            (states[2] == FieldState.UserChanged ? TermFields.WholeWord : TermFields.None) |
            (states[3] == FieldState.UserChanged ? TermFields.Enabled : TermFields.None);
        var expectedOrigin = expectedValues == shipped ? TermOrigin.Pinned : TermOrigin.Edited;
        var entry = Edited("get hub", @base, user, acknowledged);

        var row = Assert.Single(BuiltInOverlay.Apply(Shipped(shipped), Document(entry)));

        var because = $"{string.Join(", ", states)}, acknowledged: {withAcknowledgment}";
        Assert.True(expectedValues == row.Values, $"{because}: values {Describe(row.Values)}");
        Assert.True(expectedOrigin == row.Origin, $"{because}: origin {row.Origin}");
        Assert.True(shipped == row.Shipped, because);
        Assert.Same(entry, row.Edit);
        Assert.True(
            (questions == TermFields.None ? null : new TermReview(expectedValues, shipped, differing)) == row.Review,
            $"{because}: review {row.Review?.Differing}");
    }

    // The states in which the user's value stands against a shipped value it differs from.
    private static bool Differs(FieldState state) =>
        state is FieldState.UserChanged or FieldState.BothDiffer or FieldState.BothDifferKept;

    // An older acknowledgment (for a shipped value since replaced) never stops a question; one for this very value does.
    private static bool Asks(FieldState state, bool withAcknowledgment) =>
        state == FieldState.BothDiffer || (state == FieldState.BothDifferKept && !withAcknowledgment);

    private static (string Base, string User, string Shipped, string Merged, string Acknowledged) StringField(
        FieldState state, string @base, string shipped, string user, string older) => state switch
    {
        FieldState.Untouched => (@base, @base, @base, @base, older),
        FieldState.ShippedChanged => (@base, @base, shipped, shipped, older),
        FieldState.UserChanged => (@base, user, @base, user, older),
        FieldState.BothSame => (@base, shipped, shipped, shipped, older),
        FieldState.BothDiffer => (@base, user, shipped, user, older),
        FieldState.BothDifferKept => (@base, user, shipped, user, shipped),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static (bool Base, bool User, bool Shipped, bool Merged) FlagField(FieldState state) => state switch
    {
        FieldState.Untouched => (true, true, true, true),
        FieldState.ShippedChanged => (true, true, false, false),
        FieldState.UserChanged => (true, false, true, false),
        FieldState.BothSame => (true, false, false, false),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static void CheckPinned(string[] fields, bool withAcknowledgment)
    {
        // The user's values, and per field the shipped value: equal, different, or different and already kept (A = S).
        var user = T("git hub", "GitHub Enterprise", wholeWord: true, enabled: true);
        var otherSpoken = "Git Hub";
        var shipped = new TermValues(
            fields[0] == "equal" ? user.Spoken : otherSpoken,
            fields[1] == "equal" ? user.Written : "GitHub, Inc.",
            fields[2] == "equal" ? user.WholeWord : !user.WholeWord,
            fields[3] == "equal" ? user.Enabled : !user.Enabled);
        var acknowledged = withAcknowledgment
            ? new TermValues(
                fields[0] == "kept" ? shipped.Spoken : "GIT HUB",
                fields[1] == "kept" ? shipped.Written : "GitHub.com",
                fields[2] == "kept" ? shipped.WholeWord : user.WholeWord,
                fields[3] == "kept" ? shipped.Enabled : user.Enabled)
            : null;
        var questions = TermFields.None;
        var differing = TermFields.None;
        TermFields[] flags = [TermFields.Spoken, TermFields.Written, TermFields.WholeWord, TermFields.Enabled];
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i] == "differs")
            {
                questions |= flags[i];
            }

            // Once a field asks, the review lists every field that differs, the ones already kept too (round 2, A4).
            if (fields[i] != "equal")
            {
                differing |= flags[i];
            }
        }

        // The base plays no part in a pinned entry: vary it and nothing changes.
        foreach (var @base in new[] { user, T("get hub", "GitHub"), shipped })
        {
            var entry = Pinned("git hub", @base, user, acknowledged);
            var row = Assert.Single(BuiltInOverlay.Apply(Shipped(shipped), Document(entry)));

            var because = $"{string.Join(", ", fields)}, acknowledged: {withAcknowledgment}";
            Assert.True(user == row.Values, because);
            Assert.True((fields.All(field => field == "equal") ? TermOrigin.Pinned : TermOrigin.Edited) == row.Origin, because);
            Assert.True((questions == TermFields.None ? null : new TermReview(user, shipped, differing)) == row.Review, because);
        }
    }
}
