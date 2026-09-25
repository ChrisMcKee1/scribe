using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// What blocks a library Save (plan 3.6): removal intent (D-1), repeated spoken forms (D-2), metadata older versions
/// misread (D-13), content that cannot be saved (D-15), and the field and row limits. Validation applies to the libraries
/// the change set writes, never to an untouched legacy library.
/// </summary>
public sealed class LibraryWorkspaceValidationTests
{
    [Fact]
    public void D1_a_new_or_cleared_written_form_needs_a_removal_intent_before_it_saves()
    {
        var workspace = Workspace(Standard());

        var added = workspace.AddTerm("team-terms", new TermValues("  gh  cli ", "   "));
        Assert.True(added.Applied);
        Assert.Equal(LibraryValidationKind.EmptyWrittenWithoutIntent, added.Issue!.Kind);
        Assert.Equal(TermFields.Written, added.Issue.Field);
        Assert.Equal("Type how \"gh cli\" should be written.", LibraryEditor.Message(added.Issue, "gh cli"));
        var newRow = RowIdOf(workspace, "team-terms", "gh cli");

        var cleared = workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", ""));
        Assert.Equal(LibraryValidationKind.EmptyWrittenWithoutIntent, cleared.Issue!.Kind);

        var blocked = workspace.CaptureChangeSet();
        Assert.Null(blocked.ChangeSet);
        Assert.Equal(2, blocked.Issues.Count(issue => issue.Kind == LibraryValidationKind.EmptyWrittenWithoutIntent));
        Assert.Contains(blocked.Issues, issue => issue.RowId == newRow);

        Assert.Null(workspace.EditTerm("team-terms", newRow, new TermValues("gh cli", ""), removalIntent: true).Issue);
        Assert.Null(workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", ""), removalIntent: true).Issue);
        var changes = Capture(workspace);
        var written = Assert.Single(changes.Writes).Content!.Rows.Select(row => row.Values).ToList();
        Assert.Contains(new TermValues("gh cli", ""), written);
        Assert.Contains(new TermValues("kube", ""), written);
    }

    [Fact]
    public void D1_legacy_and_imported_empty_rows_stay_valid_and_a_blank_placeholder_is_dropped()
    {
        var catalog = Catalog(
            [Custom("legacy", "Legacy", [new TermValues("um", ""), new TermValues("kube", "Kubernetes")])],
            ["legacy"],
            ai: [new("legacy", true)]);
        var workspace = Workspace(catalog);

        // The legacy removal rule is kept as it is when another row of its library is edited.
        workspace.EditTerm("legacy", RowIdOf(workspace, "legacy", "kube"), new TermValues("kube", "K8s"));
        var plan = LibraryImportPlanner.Plan(
            Document("Imported", new TermValues("uh", ""), new TermValues("vm", "VM")),
            new LibraryImportTarget.ExistingLibrary("legacy"),
            workspace.Draft);
        Assert.Equal(1, plan.RemovalRules);
        Assert.True(workspace.ApplyImport(plan, ImportConflictChoice.KeepMine).Applied);

        // A row added and never typed into is a placeholder: dropped, and no change by itself.
        Assert.Null(workspace.AddTerm("legacy", new TermValues("", "")).Issue);

        var content = Assert.Single(Capture(workspace).Writes).Content!;
        Assert.Equal(
            [new TermValues("um", ""), new TermValues("kube", "K8s"), new TermValues("uh", ""), new TermValues("vm", "VM")],
            content.Rows.Select(row => row.Values));

        var untouched = Workspace(catalog);
        untouched.AddTerm("legacy", new TermValues("", ""));
        Assert.False(untouched.HasUnsavedChanges);
        Assert.True(Capture(untouched).IsEmpty);
    }

    [Fact]
    public void D2_a_repeated_spoken_form_blocks_off_rows_included_and_a_legacy_repeat_blocks_only_its_own_library()
    {
        var catalog = Catalog(
            [
                Custom("repeats", "Repeats", [new TermValues("kube", "Kubernetes"), new TermValues("KUBE ", "K8s")]),
                Custom("other", "Other", [new TermValues("vm", "VM")]),
            ],
            ["repeats", "other"],
            ai: [new("repeats", true), new("other", true)]);
        var workspace = Workspace(catalog);

        // An unrelated library saves: the legacy repeat is not validated while its library is untouched.
        workspace.AddTerm("other", new TermValues("aks", "AKS", Enabled: false));
        Assert.NotNull(workspace.CaptureChangeSet().ChangeSet);

        var repeated = workspace.AddTerm("other", new TermValues("A K S", "AKS"));
        Assert.Null(repeated.Issue);
        var offRepeat = workspace.AddTerm("other", new TermValues("aks", "Azure Kubernetes Service"));
        Assert.Equal(LibraryValidationKind.DuplicateSpoken, offRepeat.Issue!.Kind);

        workspace.AddTerm("repeats", new TermValues("vm", "VM"));
        var blocked = workspace.CaptureChangeSet();
        Assert.Null(blocked.ChangeSet);
        Assert.Contains(blocked.Issues, issue => issue.LibraryId == "other" && issue.Kind == LibraryValidationKind.DuplicateSpoken);
        Assert.Contains(blocked.Issues, issue => issue.LibraryId == "repeats" && issue.Kind == LibraryValidationKind.DuplicateSpoken);
    }

    [Fact]
    public void D2_an_untouched_irregularly_spaced_row_is_its_own_term_until_it_is_edited()
    {
        var catalog = Catalog(
            [Custom("legacy", "Legacy", [new TermValues("get  hub", "Git Hub")])],
            ["legacy"],
            ai: [new("legacy", true)]);
        var workspace = Workspace(catalog);
        var legacyRow = RowIdOf(workspace, "legacy", "get  hub");

        // 0.4.3 keeps both: the legacy row never matches, the new one does (review finding A10).
        Assert.Null(workspace.AddTerm("legacy", new TermValues("get hub", "GitHub")).Issue);
        Assert.NotNull(workspace.CaptureChangeSet().ChangeSet);

        // Editing the legacy row commits it normalized, and then it is a repeat like any other.
        var edited = workspace.EditTerm("legacy", legacyRow, new TermValues("get  hub", "GitHub!"));
        Assert.Equal(LibraryValidationKind.DuplicateSpoken, edited.Issue!.Kind);
        Assert.Equal("get hub", workspace.RowsOf("legacy").Single(row => row.RowId == legacyRow).Row.Values.Spoken);
        Assert.Null(workspace.CaptureChangeSet().ChangeSet);
    }

    [Fact]
    public void D2_a_built_in_blocks_a_repeat_the_user_makes_but_not_one_the_shipped_data_already_has()
    {
        var workspace = Workspace(Standard());

        // "copilot" is shipped once in GitHub; adding it again is the user's repeat.
        var added = workspace.AddTerm(GitHubId, new TermValues("copilot", "GitHub Copilot"));
        Assert.Equal(LibraryValidationKind.DuplicateSpoken, added.Issue!.Kind);
        workspace.DeleteTerm(GitHubId, workspace.RowsOf(GitHubId).Last().RowId);

        // An edit that makes another row speak a shipped form is the user's repeat too.
        var renamed = workspace.EditTerm(GitHubId, RowIdOf(workspace, GitHubId, "octo cat"), new TermValues("get hub", "Octocat"));
        Assert.Equal(LibraryValidationKind.DuplicateSpoken, renamed.Issue!.Kind);
        Assert.Null(workspace.CaptureChangeSet().ChangeSet);

        // A committed document that already speaks a shipped form twice (a later version shipped the form an edit
        // wrote) saves when the user edits only another value of it.
        var edits = new BuiltInLibraryEdits(GitHubId,
        [
            new BuiltInTermEdit(LibraryTermKey.From("octo cat"), BuiltInTermIntent.Edited,
                new TermValues("octo cat", "Octocat"), new TermValues("copilot", "Octocat")),
        ]);
        var collided = Workspace(Catalog([BuiltIn(GitHubId, edits)], [GitHubId]));
        var octocat = collided.RowsOf(GitHubId).Single(row => row.Row.Key == LibraryTermKey.From("octo cat")).RowId;
        Assert.Null(collided.EditTerm(GitHubId, octocat, new TermValues("copilot", "The Octocat")).Issue);
        Assert.NotNull(collided.CaptureChangeSet().ChangeSet);
    }

    [Fact]
    public void D13_a_typed_double_quote_is_refused_with_its_message_and_a_trailing_comma_is_not()
    {
        var workspace = Workspace(Standard());

        var name = workspace.Rename("team-terms", "Bob\"s terms");
        Assert.False(name.Applied);
        Assert.Equal(LibraryValidationKind.MetadataDoubleQuote, name.Issue!.Kind);
        Assert.Equal(LibraryMetadataField.Name, name.Issue.Metadata);
        Assert.Equal("Names can't contain a double quote (\"), which older versions of Scribe misread.", LibraryEditor.Message(name.Issue));
        Assert.Equal("Team terms", workspace.Draft.Find("team-terms")!.Content.Name);

        var category = workspace.SetDetails("team-terms", "Work \"stuff\"", null);
        Assert.Equal(LibraryMetadataField.Category, category.Issue!.Metadata);
        var description = workspace.SetDetails("team-terms", "Custom", "Say \"hi\"");
        Assert.Equal(LibraryMetadataField.Description, description.Issue!.Metadata);
        Assert.False(workspace.HasUnsavedChanges);

        Assert.True(workspace.Rename("team-terms", "Team terms,").Applied);
        Assert.True(workspace.SetDetails("team-terms", "Work,", "Terms, mostly,").Applied);
        var content = Assert.Single(Capture(workspace).Writes).Content!;
        Assert.Equal("Team terms,", content.Name);
        Assert.Equal("Work,", content.Category);
        Assert.Equal("Terms, mostly,", content.Description);
    }

    [Fact]
    public void D13_an_untouched_legacy_name_with_an_unpaired_quote_blocks_only_a_save_that_rewrites_its_library()
    {
        var catalog = Catalog(
            [
                Custom("bobs", "Bob\"s terms", [new TermValues("kube", "Kubernetes")]),
                Custom("other", "Other", [new TermValues("vm", "VM")]),
            ],
            ["bobs", "other"],
            ai: [new("bobs", true), new("other", true)]);
        var workspace = Workspace(catalog);

        workspace.AddTerm("other", new TermValues("aks", "AKS"));
        workspace.SetEnabled("bobs", false);
        Assert.NotNull(workspace.CaptureChangeSet().ChangeSet);

        workspace.AddTerm("bobs", new TermValues("aks", "AKS"));
        var blocked = workspace.CaptureChangeSet();
        var issue = Assert.Single(blocked.Issues);
        Assert.Equal(LibraryValidationKind.MetadataUnreadableInOlder, issue.Kind);
        Assert.Equal("bobs", issue.LibraryId);
        Assert.Equal(LibraryMetadataField.Name, issue.Metadata);
        Assert.Equal(LibraryEditor.MetadataDoubleQuoteMessage, LibraryEditor.Message(issue));

        // Renaming it, which refuses a typed quote, is the way out.
        Assert.True(workspace.Rename("bobs", "Bobs terms").Applied);
        Assert.NotNull(workspace.CaptureChangeSet().ChangeSet);
    }

    [Fact]
    public void D13_changing_one_detail_keeps_an_untouched_legacy_other_as_it_is()
    {
        var catalog = Catalog(
            [
                Custom("paired", "Paired", [new TermValues("a", "A")], description: "Say \"hi\" twice"),
                Custom("odd", "Odd", [new TermValues("b", "B")], description: "It\"s odd"),
            ],
            ["paired", "odd"],
            ai: [new("paired", true), new("odd", true)]);
        var workspace = Workspace(catalog);

        // The description is passed back as it is, so only the category is a typed value; two quotes read back in 0.4.3.
        Assert.True(workspace.SetDetails("paired", "Work", "Say \"hi\" twice").Applied);
        Assert.Equal(("Work", "Say \"hi\" twice"), (workspace.Draft.Find("paired")!.Content.Category, workspace.Draft.Find("paired")!.Content.Description));
        Assert.True(workspace.Rename("paired", "Paired").Applied);
        Assert.NotNull(workspace.CaptureChangeSet().ChangeSet);

        // One quote does not, and the Save that rewrites the library says so on the description.
        Assert.True(workspace.SetDetails("odd", "Work", "It\"s odd").Applied);
        var issue = Assert.Single(workspace.CaptureChangeSet().Issues);
        Assert.Equal((LibraryValidationKind.MetadataUnreadableInOlder, LibraryMetadataField.Description), (issue.Kind, issue.Metadata));
    }

    [Fact]
    public void D15_a_partly_readable_library_can_be_toggled_and_its_permission_changed_but_its_content_cannot_be_edited()
    {
        var catalog = Catalog(
            [Custom("partial", "Partial", [new TermValues("kube", "Kubernetes")], state: LibraryFileState.PartlyReadable)],
            ["partial"],
            ai: [new("partial", true)]);
        var workspace = Workspace(catalog);
        var row = RowIdOf(workspace, "partial", "kube");

        Assert.False(workspace.CanEditContent("partial"));
        Assert.Equal(LibraryValidationKind.ContentNotSaveable, workspace.AddTerm("partial", new TermValues("a", "A")).Issue!.Kind);
        Assert.Equal(LibraryValidationKind.ContentNotSaveable, workspace.EditTerm("partial", row, new TermValues("kube", "K8s")).Issue!.Kind);
        Assert.Equal(LibraryValidationKind.ContentNotSaveable, workspace.Rename("partial", "Whole").Issue!.Kind);
        Assert.Throws<InvalidOperationException>(() => workspace.DeleteTerm("partial", row));
        Assert.Throws<InvalidOperationException>(() => workspace.SetTermEnabled("partial", row, false));
        Assert.False(workspace.HasUnsavedChanges);

        workspace.SetEnabled("partial", false);
        Assert.True(workspace.SetAiPermission("partial", false).Applied);
        var changes = Capture(workspace);
        Assert.Empty(changes.Writes);
        Assert.True(changes.LocalStateChanged);
        Assert.DoesNotContain("partial", changes.LocalState.EnabledIds);
        Assert.False(changes.LocalState.AiPermissions["partial"]);
    }

    [Fact]
    public void D15_a_newer_state_makes_the_workspace_read_only()
    {
        var catalog = Catalog([BuiltIn(GitHubId), Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes")])],
            ["team-terms"], health: LocalStateHealth.Newer);
        var workspace = Workspace(catalog);
        var row = RowIdOf(workspace, "team-terms", "kube");

        Assert.True(workspace.IsReadOnly);
        Assert.Equal(LocalStateHealth.Newer, workspace.Draft.LocalState.Health);
        Assert.False(workspace.AddTerm("team-terms", new TermValues("a", "A")).Applied);
        Assert.False(workspace.EditTerm("team-terms", row, new TermValues("kube", "K8s")).Applied);
        Assert.False(workspace.Rename("team-terms", "Renamed").Applied);
        Assert.False(workspace.SetAiPermission("team-terms", false).Applied);
        workspace.SetEnabled("team-terms", false);
        workspace.DeleteTerm("team-terms", row);
        workspace.DeleteLibrary("team-terms");
        workspace.SetTermEnabled(GitHubId, workspace.RowsOf(GitHubId)[0].RowId, false);
        Assert.Throws<InvalidOperationException>(() => workspace.CreateLibrary());
        Assert.Throws<InvalidOperationException>(() => workspace.Duplicate(GitHubId));

        Assert.False(workspace.HasUnsavedChanges);
        Assert.False(workspace.CanEditContent("team-terms"));
        var changes = Capture(workspace);
        Assert.True(changes.IsEmpty);
        Assert.Equal(LocalStateHealth.Newer, changes.LocalState.Health);
    }

    [Fact]
    public void A_new_or_changed_value_past_the_field_limit_blocks_and_an_unchanged_legacy_one_is_kept()
    {
        var longValue = new string('x', LibraryLimits.MaxFieldLength + 1);
        var catalog = Catalog(
            [Custom("long", "Long", [new TermValues("sig", longValue), new TermValues("kube", "Kubernetes")])],
            ["long"],
            ai: [new("long", true)]);
        var workspace = Workspace(catalog);

        workspace.EditTerm("long", RowIdOf(workspace, "long", "kube"), new TermValues("kube", "K8s"));
        Assert.NotNull(workspace.CaptureChangeSet().ChangeSet);

        var added = workspace.AddTerm("long", new TermValues("note", longValue));
        Assert.Equal(LibraryValidationKind.FieldTooLong, added.Issue!.Kind);
        Assert.Equal(TermFields.Written, added.Issue.Field);
        var blocked = workspace.CaptureChangeSet();
        Assert.Equal(LibraryValidationKind.FieldTooLong, Assert.Single(blocked.Issues).Kind);
    }

    [Fact]
    public void A_library_at_the_term_limit_does_not_grow()
    {
        var rows = Enumerable.Range(0, LibraryLimits.MaxTermsPerLibrary).Select(i => new TermValues($"term {i}", $"Term {i}"));
        var workspace = Workspace(Catalog([Custom("full", "Full", rows)], ["full"], ai: [new("full", true)]));

        var refused = workspace.AddTerm("full", new TermValues("one more", "One more"));
        Assert.False(refused.Applied);
        Assert.Equal(LibraryValidationKind.TooManyTerms, refused.Issue!.Kind);

        // A placeholder is not a term, so it can still be added, and editing within the limit still saves.
        Assert.True(workspace.AddTerm("full", new TermValues("", "")).Applied);
        workspace.EditTerm("full", RowIdOf(workspace, "full", "term 0"), new TermValues("term 0", "Term zero"));
        Assert.NotNull(workspace.CaptureChangeSet().ChangeSet);
    }

    [Fact]
    public void A_written_form_without_a_spoken_one_blocks()
    {
        var workspace = Workspace(Standard());

        var issue = workspace.AddTerm("team-terms", new TermValues("", "Orphan")).Issue!;

        Assert.Equal(LibraryValidationKind.WrittenWithoutSpoken, issue.Kind);
        Assert.Equal(TermFields.Spoken, issue.Field);
        Assert.Equal(LibraryValidationKind.WrittenWithoutSpoken, Assert.Single(workspace.CaptureChangeSet().Issues).Kind);
    }
}
