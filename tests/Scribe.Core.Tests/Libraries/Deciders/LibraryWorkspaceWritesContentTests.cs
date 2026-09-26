using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-19: <see cref="DraftLibrary.WritesContent"/>, whether the Save of a draft revision writes each library's content,
/// which the draft carries for previews (the ruling on GPT-6 Astra's verification of sub-stream C): the capture's own
/// decision, never inferred from what the rows show. The seeded property over random sequences is in
/// <see cref="LibraryWorkspacePropertyTests"/>.
/// </summary>
public sealed class LibraryWorkspaceWritesContentTests
{
    [Fact]
    public void D19_Restoring_all_values_of_a_built_in_whose_document_holds_only_an_invisible_off_intent_writes_its_content()
    {
        // The document turns off a term this version does not ship, so no row shows it and the rows are the shipped ones.
        var edits = new BuiltInLibraryEdits(GitHubId,
        [
            new BuiltInTermEdit(LibraryTermKey.From("retired row"), BuiltInTermIntent.Off, new TermValues("retired row", "Retired"), null),
        ]);
        var workspace = Workspace(Catalog([BuiltIn(GitHubId, edits), BuiltIn(AzureId)], [GitHubId]));
        var shown = ValuesOf(workspace, GitHubId);
        Assert.False(WritesContent(workspace, GitHubId));

        workspace.RestoreAllBuiltInValues(GitHubId);

        // Nothing visible changed, and the Save removes the document.
        Assert.Equal(shown, ValuesOf(workspace, GitHubId));
        Assert.True(WritesContent(workspace, GitHubId));
        var write = Assert.Single(Capture(workspace).Writes);
        Assert.Equal((GitHubId, (BuiltInLibraryEdits?)null), (write.LibraryId, write.Edits));

        workspace.Undo();
        Assert.False(WritesContent(workspace, GitHubId));
    }

    [Fact]
    public void D19_Changing_only_the_enabled_state_or_the_ai_box_writes_no_content()
    {
        var workspace = Workspace(Standard());

        workspace.SetEnabled("team-terms", false);
        workspace.SetEnabled(AzureId, true);
        workspace.SetAiPermission("team-terms", false);
        workspace.SetAiPermission(GitHubId, false);

        Assert.Equal(
            new[] { AzureId, GitHubId, "team-terms" }.Order(StringComparer.OrdinalIgnoreCase),
            workspace.UnsavedLibraryIds.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(workspace.Draft.Libraries, library => Assert.False(WritesContent(workspace, library.Content.Id)));
        var changes = Capture(workspace);
        Assert.Empty(changes.Writes);
        Assert.True(changes.LocalStateChanged);
    }

    [Fact]
    public void D19_A_pending_deletion_an_untouched_new_library_and_an_edit_that_commits_to_what_is_shown_write_no_content()
    {
        var workspace = Workspace(Standard());
        var created = workspace.CreateLibrary();
        workspace.DeleteLibrary("team-terms");
        workspace.EditTerm(GitHubId, RowIdOf(workspace, GitHubId, "copilot"), new TermValues(" copilot ", "  Copilot "));

        Assert.False(WritesContent(workspace, created));
        Assert.False(WritesContent(workspace, "team-terms"));
        Assert.False(WritesContent(workspace, GitHubId));
        var changes = Capture(workspace);
        Assert.Empty(changes.Writes);
        Assert.Single(changes.Deletions);

        // A value typed back to the shipped one after an edit stays authored (Pinned, 3.2.1), so its document is written.
        var copilot = RowIdOf(workspace, GitHubId, "copilot");
        workspace.EditTerm(GitHubId, copilot, new TermValues("copilot", "GitHub Copilot"));
        workspace.EditTerm(GitHubId, copilot, new TermValues("copilot", "Copilot"));
        Assert.Equal(TermOrigin.Pinned, workspace.RowsOf(GitHubId).Single(row => row.RowId == copilot).Row.Origin);
        Assert.True(WritesContent(workspace, GitHubId));
    }

    [Fact]
    public void D19_Creating_importing_duplicating_restoring_keeping_and_editing_write_the_content_exactly_as_the_capture_does()
    {
        var entry = Deleted("20260901T100000Z.gone.csv", "gone", "Gone", new TermValues("gone term", "Gone term"));
        var catalog = Catalog(
            [
                BuiltIn(GitHubId), BuiltIn(AzureId),
                Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes"), new TermValues("get hub", "GitHub Enterprise")]),
                Custom("notes", "Notes", [new TermValues("vm", "VM")]),
                Custom("quiet", "Quiet", [new TermValues("um", "")]),
            ],
            [GitHubId, "team-terms", "notes"],
            ai: [new("team-terms", true), new("notes", false), new("quiet", false)],
            recentlyDeleted: [entry.Entry],
            retired: [new RetiredBuiltInEdits("data-and-ai", [new TermValues("llm", "LLM")], Hash("retired"))]);
        var workspace = Workspace(catalog);

        var created = workspace.CreateLibrary();
        workspace.Rename(created, "Release notes");
        var copy = workspace.Duplicate("team-terms");
        var restored = workspace.RestoreDeleted(entry);
        var kept = workspace.KeepRetiredBuiltIn("data-and-ai");
        workspace.ApplyImport(
            LibraryImportPlanner.Plan(Document("Imported", new TermValues("ga", "GA")), new LibraryImportTarget.NewLibrary(null), workspace.Draft),
            ImportConflictChoice.KeepMine);
        var imported = workspace.Draft.Libraries.Single(library => library.Origin == LibraryOrigin.Imported).Content.Id;
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "K8s"));
        workspace.SetDetails("notes", "Work", "Mine");
        workspace.EditTerm(GitHubId, RowIdOf(workspace, GitHubId, "copilot"), new TermValues("copilot", "GitHub Copilot"));
        workspace.SetEnabled("quiet", true);

        foreach (var id in new[] { created, copy, restored, kept, imported, "team-terms", "notes", GitHubId })
        {
            Assert.True(WritesContent(workspace, id), id);
        }

        Assert.False(WritesContent(workspace, AzureId));
        Assert.False(WritesContent(workspace, "quiet"));
        var changes = Capture(workspace);
        Assert.All(workspace.Draft.Libraries, library =>
            Assert.Equal(Writes(changes, library.Content.Id), WritesContent(workspace, library.Content.Id)));

        // Saved, nothing is left to write.
        workspace.MarkSaved(changes.DraftRevision, Apply(catalog, changes, new Dictionary<string, RecentlyDeletedContent>(StringComparer.OrdinalIgnoreCase)
        {
            [entry.Entry.EntryName] = entry,
        }));
        Assert.All(workspace.Draft.Libraries, library => Assert.False(WritesContent(workspace, library.Content.Id)));
    }

    [Fact]
    public void D19_A_restore_writes_its_content_whether_or_not_it_was_edited_and_a_recovery_writes_the_document()
    {
        var entry = Deleted("20260901T100000Z.gone.csv", "gone", "Gone", new TermValues("gone term", "Gone term"));
        var catalog = Catalog(
            [BuiltIn(GitHubId, state: LibraryFileState.Unreadable, previousEdits: true), BuiltIn(AzureId)],
            [GitHubId],
            recentlyDeleted: [entry.Entry]);
        var workspace = Workspace(catalog);

        var restored = workspace.RestoreDeleted(entry);
        Assert.True(WritesContent(workspace, restored));
        var unedited = Capture(workspace);
        Assert.Empty(unedited.Writes);
        Assert.True(Writes(unedited, restored));

        workspace.EditTerm(restored, RowIdOf(workspace, restored, "gone term"), new TermValues("gone term", "Back"));
        Assert.True(WritesContent(workspace, restored));

        Assert.False(WritesContent(workspace, GitHubId));
        workspace.RecoverBuiltIn(GitHubId, BuiltInEditsRecovery.RestorePrevious);
        Assert.True(WritesContent(workspace, GitHubId));
        Assert.Equal(BuiltInEditsRecovery.RestorePrevious, Capture(workspace).Writes.Single(write => write.BuiltIn).Recovery);

        workspace.DiscardLibrary(restored);
        Assert.Null(workspace.Draft.Find(restored));
    }

    [Theory]
    [InlineData(LibraryFileState.Newer)]
    [InlineData(LibraryFileState.Unreadable)]
    [InlineData(LibraryFileState.AwaitingRelease)]
    public void D19_An_edit_of_a_built_in_whose_document_a_newer_catalog_paused_is_not_written_and_blocks_the_save(LibraryFileState state)
    {
        // GPT-6 Astra's A9: the draft edited GitHub while its document was available; the newer catalog finds that document
        // from a newer version, unreadable, or held by another app. Writing the edit would replace it without the recovery
        // the user never chose, and the pre-image check cannot stop that, since the write would name its current hash.
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.EditTerm(GitHubId, RowIdOf(workspace, GitHubId, "copilot"), new TermValues("copilot", "GitHub Copilot"));
        Assert.True(WritesContent(workspace, GitHubId));

        workspace.Rebase(Paused(catalog, state));

        Assert.False(workspace.CanEditContent(GitHubId));
        Assert.False(WritesContent(workspace, GitHubId));
        var capture = workspace.CaptureChangeSet();
        Assert.Null(capture.ChangeSet);
        var issue = Assert.Single(capture.Issues);
        Assert.Equal((GitHubId, LibraryValidationKind.ContentNotSaveable), (issue.LibraryId, issue.Kind));
    }

    [Theory]
    [InlineData(LibraryFileState.Newer, BuiltInEditsRecovery.BackUpAndReset)]
    [InlineData(LibraryFileState.Unreadable, BuiltInEditsRecovery.RestorePrevious)]
    public void D19_A_recovery_the_user_chooses_for_that_built_in_still_writes_it(LibraryFileState state, BuiltInEditsRecovery recovery)
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.EditTerm(GitHubId, RowIdOf(workspace, GitHubId, "copilot"), new TermValues("copilot", "GitHub Copilot"));
        workspace.Rebase(Paused(catalog, state));

        workspace.RecoverBuiltIn(GitHubId, recovery);

        Assert.True(WritesContent(workspace, GitHubId));
        var write = Assert.Single(Capture(workspace).Writes);
        Assert.Equal((GitHubId, recovery, (BuiltInLibraryEdits?)null), (write.LibraryId, write.Recovery, write.Edits));
    }

    // The catalog after another commit: GitHub's edits document in `state`, everything else as it was.
    private static LibraryCatalog Paused(LibraryCatalog catalog, LibraryFileState state) =>
        Catalog(
            catalog.Libraries.Where(library => library.Content.Id != GitHubId).Append(BuiltIn(GitHubId, state: state, previousEdits: true)),
            [GitHubId, "team-terms"],
            ai: [new("team-terms", true)],
            generation: catalog.Generation + 1);

    [Fact]
    public void D19_A_read_only_state_writes_nothing()
    {
        var workspace = Workspace(Catalog([BuiltIn(GitHubId), Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes")])],
            [GitHubId], health: LocalStateHealth.Newer));

        Assert.True(workspace.IsReadOnly);
        Assert.All(workspace.Draft.Libraries, library => Assert.False(WritesContent(workspace, library.Content.Id)));
        Assert.True(Capture(workspace).IsEmpty);
    }

    [Fact]
    public void D19_Edited_content_a_newer_catalog_made_unsaveable_is_not_written_and_blocks_the_save()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "K8s"));
        Assert.True(WritesContent(workspace, "team-terms"));

        // The file now holds rows the codec could not read: the draft's edit cannot be written over it.
        var partial = Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes"), new TermValues("get hub", "GitHub Enterprise")],
            state: LibraryFileState.PartlyReadable);
        workspace.Rebase(Catalog([BuiltIn(GitHubId), BuiltIn(AzureId), partial], [GitHubId, "team-terms"], ai: [new("team-terms", true)],
            generation: catalog.Generation + 1));

        Assert.False(WritesContent(workspace, "team-terms"));
        var capture = workspace.CaptureChangeSet();
        Assert.Null(capture.ChangeSet);
        Assert.Equal(LibraryValidationKind.ContentNotSaveable, Assert.Single(capture.Issues).Kind);
    }
}
