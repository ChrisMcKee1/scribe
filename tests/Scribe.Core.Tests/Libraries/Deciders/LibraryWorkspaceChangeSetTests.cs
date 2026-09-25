using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-4, D-12, D-16 and D-17: what each workspace operation puts in the change set, what a Save leaves unsaved, and what
/// Reload and Discard do.
/// </summary>
public sealed class LibraryWorkspaceChangeSetTests
{
    [Fact]
    public void D4_a_fresh_workspace_and_an_untouched_new_library_are_no_change()
    {
        var workspace = Workspace(Standard());
        Assert.False(workspace.HasUnsavedChanges);
        Assert.True(Capture(workspace).IsEmpty);

        var created = workspace.CreateLibrary();
        Assert.True(workspace.Draft.Find(created) is { Origin: LibraryOrigin.Created, Unsaved: false });
        Assert.Contains(created, workspace.Draft.LocalState.EnabledIds);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Empty(workspace.UnsavedLibraryIds);
        var changes = Capture(workspace);
        Assert.True(changes.IsEmpty);
        Assert.DoesNotContain(created, changes.LocalState.EnabledIds);
        Assert.False(changes.LocalState.AiPermissions.ContainsKey(created));

        // Adding a term and deleting it again leaves it untouched; a rename touches it.
        workspace.AddTerm(created, new TermValues("kube", "Kubernetes"));
        Assert.True(workspace.HasUnsavedChanges);
        workspace.DeleteTerm(created, workspace.RowsOf(created)[0].RowId);
        Assert.False(workspace.HasUnsavedChanges);
        workspace.Rename(created, "Release notes");
        Assert.Equal([created], workspace.UnsavedLibraryIds);
        var write = Assert.Single(Capture(workspace).Writes);
        Assert.Equal(new LibraryWrite(created, false, LibraryOrigin.Created, null, write.Content), write);
        Assert.Equal("Release notes", write.Content!.Name);
        Assert.Empty(write.Content.Rows);
    }

    [Fact]
    public void D4_each_operation_puts_exactly_its_change_in_the_change_set()
    {
        var workspace = Workspace(Standard());
        var catalog = Standard();

        workspace.SetEnabled(AzureId, true);
        workspace.SetAiPermission(GitHubId, false);
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues(" Kube ", " K8s "));
        workspace.SetTermEnabled(GitHubId, RowIdOf(workspace, GitHubId, "octo cat"), false);
        var copy = workspace.Duplicate("team-terms");

        Assert.Equal([GitHubId, AzureId, copy, "team-terms"], workspace.UnsavedLibraryIds);
        var changes = Capture(workspace);
        Assert.Equal(catalog.Generation, changes.BaseGeneration);
        Assert.Equal(workspace.Revision, changes.DraftRevision);

        var github = changes.Writes.Single(write => write.LibraryId == GitHubId);
        Assert.True(github.BuiltIn);
        Assert.Null(github.ExpectedPreImage);
        var entry = Assert.Single(github.Edits!.Terms);
        Assert.Equal((LibraryTermKey.From("octo cat"), BuiltInTermIntent.Off), (entry.Key, entry.Intent));

        var team = changes.Writes.Single(write => write.LibraryId == "team-terms");
        Assert.Equal(catalog.Find("team-terms")!.ContentHash, team.ExpectedPreImage);
        Assert.Equal(new TermValues("Kube", "K8s"), team.Content!.Rows[0].Values);
        Assert.Equal(LibraryTermKey.From("kube"), team.Content.Rows[0].Key);

        var duplicate = changes.Writes.Single(write => write.LibraryId == copy);
        Assert.Equal((LibraryOrigin.Duplicated, "team-terms", "Team terms - Copy"), (duplicate.Origin, duplicate.Content!.BasedOn, duplicate.Content.Name));
        Assert.Null(duplicate.ExpectedPreImage);

        Assert.True(changes.LocalStateChanged);
        Assert.Equal(new[] { GitHubId, AzureId, "team-terms" }.ToHashSet(StringComparer.OrdinalIgnoreCase), changes.LocalState.EnabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.False(changes.LocalState.AiPermissions[GitHubId]);
        Assert.True(changes.LocalState.AiPermissions[copy]);
        Assert.Equal(catalog.LocalState.AcceptedContent.Count, changes.LocalState.AcceptedContent.Count);
        Assert.Empty(changes.Deletions);
        Assert.Empty(changes.RecentlyDeletedActions);
    }

    [Fact]
    public void D4_deleting_restoring_and_purging_are_their_own_change_set_entries()
    {
        var restorable = Deleted("20260901T100000Z.old-notes.csv", "old-notes", "Old notes", new TermValues("x", "X"));
        var purgeable = Deleted("20260902T100000Z.scratch.csv", "scratch", "Scratch", new TermValues("y", "Y"));
        var catalog = Catalog(
            [BuiltIn(GitHubId), Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes")], fileName: "team-terms.csv")],
            ["team-terms"],
            ai: [new("team-terms", true)],
            recentlyDeleted: [restorable.Entry, purgeable.Entry]);
        var workspace = Workspace(catalog);

        workspace.DeleteLibrary("team-terms");
        var restored = workspace.RestoreDeleted(restorable);
        workspace.DeletePermanently(purgeable.Entry);

        Assert.Empty(workspace.Draft.RecentlyDeleted);
        Assert.True(workspace.Draft.Find("team-terms")!.PendingDelete);
        Assert.Throws<InvalidOperationException>(() => workspace.DeleteLibrary(GitHubId));
        Assert.Throws<InvalidOperationException>(() => workspace.AddTerm("team-terms", new TermValues("a", "A")));

        var changes = Capture(workspace);
        Assert.Equal(new LibraryDeletion("team-terms", "team-terms.csv", catalog.Find("team-terms")!.ContentHash!.Value), Assert.Single(changes.Deletions));
        Assert.Empty(changes.Writes);
        Assert.Equal(
            [
                new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, restorable.Entry.EntryName, restorable.Entry.ContentHash, restored),
                new RecentlyDeletedAction(RecentlyDeletedActionKind.DeletePermanently, purgeable.Entry.EntryName, purgeable.Entry.ContentHash),
            ],
            changes.RecentlyDeletedActions);
        Assert.DoesNotContain(restored, changes.LocalState.EnabledIds);
        Assert.False(changes.LocalState.AiPermissions[restored]);
    }

    [Fact]
    public void D4_mark_saved_keeps_the_edits_made_after_the_captured_revision_unsaved()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "K8s"));
        var created = workspace.CreateLibrary();
        workspace.Rename(created, "Release notes");
        var changes = Capture(workspace);

        // While the Save runs, the user keeps editing.
        var kubeRow = RowIdOf(workspace, "team-terms", "kube");
        workspace.AddTerm(created, new TermValues("ga", "general availability"));
        workspace.SetEnabled(AzureId, true);
        var later = workspace.Revision;

        var saved = Apply(catalog, changes);
        workspace.MarkSaved(changes.DraftRevision, saved);

        Assert.True(workspace.Revision > later);
        Assert.Equal(saved.Generation, workspace.Draft.BaseGeneration);
        Assert.Equal([AzureId, created], workspace.UnsavedLibraryIds);
        Assert.Equal(LibraryOrigin.Existing, workspace.Draft.Find(created)!.Origin);
        Assert.Equal(new TermValues("ga", "general availability"), Assert.Single(ValuesOf(workspace, created)));
        Assert.Equal(kubeRow, RowIdOf(workspace, "team-terms", "kube"));
        Assert.False(workspace.CanUndo);

        var next = Capture(workspace);
        var write = Assert.Single(next.Writes);
        Assert.Equal((created, LibraryOrigin.Existing, saved.Find(created)!.ContentHash), (write.LibraryId, write.Origin, write.ExpectedPreImage));
        Assert.Contains(AzureId, next.LocalState.EnabledIds);
        Assert.Throws<InvalidOperationException>(() => workspace.MarkSaved(changes.DraftRevision, saved));
    }

    [Fact]
    public void D4_mark_saved_with_nothing_edited_since_leaves_nothing_unsaved()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.DeleteTerm("team-terms", RowIdOf(workspace, "team-terms", "get hub"));
        workspace.SetTermEnabled(GitHubId, RowIdOf(workspace, GitHubId, "copilot"), false);
        workspace.SetEnabled("team-terms", false);
        var changes = Capture(workspace);

        workspace.MarkSaved(changes.DraftRevision, Apply(catalog, changes));

        Assert.False(workspace.HasUnsavedChanges);
        Assert.True(Capture(workspace).IsEmpty);
        Assert.DoesNotContain("team-terms", workspace.Draft.LocalState.EnabledIds);
        Assert.Equal(TermOrigin.Off, workspace.Draft.Find(GitHubId)!.Content.Rows.Single(row => row.Key == LibraryTermKey.From("copilot")).Origin);
    }

    [Fact]
    public void D4_a_library_saved_under_another_id_is_the_kept_library_from_then_on()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var created = workspace.CreateLibrary();
        workspace.Rename(created, "Release notes");
        workspace.AddTerm(created, new TermValues("ga", "GA"));
        var changes = Capture(workspace);
        workspace.AddTerm(created, new TermValues("rc", "RC"));

        // Another app took the planned name: the user's content went to another id, and the file there is someone else's.
        var saved = Apply(catalog, changes);
        var keptAs = created + "-2";
        var mine = saved.Find(created)!;
        var theirs = Custom(created, "Their notes", [new TermValues("zz", "ZZ")]);
        var libraries = saved.Libraries.Where(library => library.Content.Id != created)
            .Append(theirs)
            .Append(new CatalogLibrary(mine.Content with { Id = keptAs }, LibraryFileState.Available, keptAs + ".csv", HashOf(mine.Content with { Id = keptAs })))
            .ToList();
        var committed = Catalog(libraries, [GitHubId, "team-terms"], ai: [new("team-terms", true)], generation: saved.Generation,
            kept: [new LibraryKeptVersion(created, LibraryKeptVersionKind.SavedUnderNewId, keptAs)]);

        workspace.MarkSaved(changes.DraftRevision, committed);

        Assert.Equal("Their notes", workspace.Draft.Find(created)!.Content.Name);
        Assert.Equal([new TermValues("zz", "ZZ")], ValuesOf(workspace, created));
        Assert.Equal([new TermValues("ga", "GA"), new TermValues("rc", "RC")], ValuesOf(workspace, keptAs));
        Assert.Equal([keptAs], workspace.UnsavedLibraryIds);
        var write = Assert.Single(Capture(workspace).Writes);
        Assert.Equal(keptAs, write.LibraryId);
        Assert.Equal(committed.Find(keptAs)!.ContentHash, write.ExpectedPreImage);
    }

    [Fact]
    public void D4_reload_discards_the_draft_and_discard_reverts_one_library()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "K8s"));
        workspace.SetEnabled("team-terms", false);
        workspace.SetEnabled(AzureId, true);
        var created = workspace.CreateLibrary();
        workspace.Rename(created, "Scratch");
        var restorable = Deleted("20260901T100000Z.old.csv", "old", "Old", new TermValues("x", "X"));

        workspace.DiscardLibrary("team-terms");
        Assert.Equal([AzureId, created], workspace.UnsavedLibraryIds);
        Assert.Contains("team-terms", workspace.Draft.LocalState.EnabledIds);
        Assert.Equal(new TermValues("kube", "Kubernetes"), ValuesOf(workspace, "team-terms")[0]);

        workspace.DiscardLibrary(created);
        Assert.Null(workspace.Draft.Find(created));
        Assert.Equal([AzureId], workspace.UnsavedLibraryIds);

        var withEntry = Catalog(catalog.Libraries, [GitHubId, "team-terms"], ai: [new("team-terms", true)], recentlyDeleted: [restorable.Entry]);
        workspace.Reload(withEntry);
        Assert.False(workspace.HasUnsavedChanges);
        var restored = workspace.RestoreDeleted(restorable);
        Assert.Empty(workspace.Draft.RecentlyDeleted);
        workspace.DiscardLibrary(restored);
        Assert.Single(workspace.Draft.RecentlyDeleted);
        Assert.False(workspace.HasUnsavedChanges);
    }

    [Fact]
    public void D4_rebase_takes_a_newer_catalog_and_keeps_the_draft_edits()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "K8s"));

        // Another commit added a library and changed GitHub's enabled state meanwhile.
        var newer = Catalog(
            catalog.Libraries.Append(Custom("discovered", "Discovered", [new TermValues("vm", "VM")])),
            ["team-terms"],
            ai: [new("team-terms", true), new("discovered", false)],
            generation: catalog.Generation + 1);
        workspace.Rebase(newer);

        Assert.Equal(newer.Generation, workspace.Draft.BaseGeneration);
        Assert.NotNull(workspace.Draft.Find("discovered"));
        Assert.DoesNotContain(GitHubId, workspace.Draft.LocalState.EnabledIds);
        Assert.Equal(["team-terms"], workspace.UnsavedLibraryIds);
        Assert.Equal(new TermValues("kube", "K8s"), ValuesOf(workspace, "team-terms")[0]);
        Assert.Equal(newer.Find("team-terms")!.ContentHash, Assert.Single(Capture(workspace).Writes).ExpectedPreImage);
    }

    [Fact]
    public void Restore_all_built_in_values_writes_no_document_and_later_edits_start_a_new_one()
    {
        var edits = new BuiltInLibraryEdits(GitHubId,
        [
            new BuiltInTermEdit(LibraryTermKey.From("copilot"), BuiltInTermIntent.Edited,
                new TermValues("copilot", "Copilot"), new TermValues("copilot", "GitHub Copilot")),
            new BuiltInTermEdit(LibraryTermKey.From("retired row"), BuiltInTermIntent.Off, new TermValues("retired row", "Retired"), null),
        ]);
        var catalog = Catalog([BuiltIn(GitHubId, edits)], [GitHubId]);
        var workspace = Workspace(catalog);

        workspace.RestoreAllBuiltInValues(GitHubId);
        var write = Assert.Single(Capture(workspace).Writes);
        Assert.Null(write.Edits);
        Assert.Equal(BuiltInEditsRecovery.None, write.Recovery);
        Assert.Equal(catalog.Find(GitHubId)!.ContentHash, write.ExpectedPreImage);
        Assert.All(workspace.Draft.Find(GitHubId)!.Content.Rows, row => Assert.Equal(TermOrigin.Shipped, row.Origin));

        workspace.SetTermEnabled(GitHubId, RowIdOf(workspace, GitHubId, "octo cat"), false);
        var entry = Assert.Single(Assert.Single(Capture(workspace).Writes).Edits!.Terms);
        Assert.Equal(LibraryTermKey.From("octo cat"), entry.Key);

        // An ordinary Save of the same document carries the inert off entry over (review finding A7).
        var carried = Workspace(catalog);
        carried.SetTermEnabled(GitHubId, RowIdOf(carried, GitHubId, "get hub"), false);
        var terms = Assert.Single(Capture(carried).Writes).Edits!.Terms;
        Assert.Contains(terms, term => term.Key == LibraryTermKey.From("retired row"));
    }

    [Fact]
    public void A_paused_built_in_takes_a_recovery_and_nothing_else()
    {
        var catalog = Catalog([BuiltIn(GitHubId, state: LibraryFileState.Unreadable, previousEdits: true), BuiltIn(AzureId, state: LibraryFileState.Newer)], [GitHubId]);
        var workspace = Workspace(catalog);

        Assert.False(workspace.CanEditContent(GitHubId));
        Assert.Equal(LibraryValidationKind.ContentNotSaveable, workspace.AddTerm(GitHubId, new TermValues("a", "A")).Issue!.Kind);
        Assert.Throws<InvalidOperationException>(() => workspace.RecoverBuiltIn(AzureId, BuiltInEditsRecovery.RestorePrevious));

        workspace.RecoverBuiltIn(GitHubId, BuiltInEditsRecovery.RestorePrevious);
        workspace.RecoverBuiltIn(AzureId, BuiltInEditsRecovery.BackUpAndReset);
        var writes = Capture(workspace).Writes;
        Assert.Equal(
            [
                new LibraryWrite(GitHubId, true, LibraryOrigin.Existing, catalog.Find(GitHubId)!.ContentHash, Recovery: BuiltInEditsRecovery.RestorePrevious),
                new LibraryWrite(AzureId, true, LibraryOrigin.Existing, catalog.Find(AzureId)!.ContentHash, Recovery: BuiltInEditsRecovery.BackUpAndReset),
            ],
            writes);

        workspace.RecoverBuiltIn(GitHubId, BuiltInEditsRecovery.None);
        Assert.Single(Capture(workspace).Writes);
        Assert.Throws<InvalidOperationException>(() =>
            Workspace(Standard()).RecoverBuiltIn(GitHubId, BuiltInEditsRecovery.BackUpAndReset));
    }

    [Fact]
    public void D12_a_restore_the_draft_edited_is_one_restore_action_with_the_entry_hash_and_one_write()
    {
        var entry = Deleted("20260901T100000Z.team-notes.csv", "team-notes", "Team notes", new TermValues("kube", "Kubernetes"), new TermValues("vm", "VM"));
        var catalog = Catalog([BuiltIn(GitHubId)], [GitHubId], recentlyDeleted: [entry.Entry]);
        var workspace = Workspace(catalog);

        var id = workspace.RestoreDeleted(entry);
        Assert.Equal("team-notes", id);
        Assert.Equal(LibraryOrigin.Restored, workspace.Draft.Find(id)!.Origin);
        Assert.Equal("team-notes.csv", workspace.Draft.Find(id)!.FileName);

        var unedited = Capture(workspace);
        Assert.Empty(unedited.Writes);
        Assert.Single(unedited.RecentlyDeletedActions);

        workspace.EditTerm(id, RowIdOf(workspace, id, "vm"), new TermValues("vm", "virtual machine"));

        // The preview and the export see the draft's content.
        Assert.Equal([new TermValues("kube", "Kubernetes"), new TermValues("vm", "virtual machine")], ValuesOf(workspace, id));

        var changes = Capture(workspace);
        var action = Assert.Single(changes.RecentlyDeletedActions);
        Assert.Equal(new RecentlyDeletedAction(RecentlyDeletedActionKind.Restore, entry.Entry.EntryName, entry.Entry.ContentHash, id), action);
        var write = Assert.Single(changes.Writes);
        Assert.Equal((id, LibraryOrigin.Restored, entry.Entry.ContentHash), (write.LibraryId, write.Origin, write.ExpectedPreImage));
        Assert.Equal([new TermValues("kube", "Kubernetes"), new TermValues("vm", "virtual machine")], write.Content!.Rows.Select(row => row.Values));

        Assert.Throws<InvalidOperationException>(() => workspace.RestoreDeleted(entry));
        Assert.Throws<InvalidOperationException>(() => workspace.DeletePermanently(entry.Entry));
    }

    [Fact]
    public void D16_every_custom_library_carries_its_file_name_and_a_twin_ranks_by_its_own()
    {
        var entry = Deleted("20260901T100000Z.zeta.csv", "zeta", "Zeta", new TermValues("z", "Z"));
        var catalog = Catalog(
            [
                BuiltIn(GitHubId),
                Custom("epsilon", "Epsilon", [new TermValues("project token", "Epsilon")]),
                Custom("custom-github", "Twin", [new TermValues("project token", "Twin")], fileName: "github.csv"),
            ],
            [GitHubId, "epsilon", "custom-github"],
            recentlyDeleted: [entry.Entry],
            retired: [new RetiredBuiltInEdits("data-and-ai", [new TermValues("llm", "LLM")], Hash("retired"))]);
        var workspace = Workspace(catalog);

        var created = workspace.CreateLibrary();
        var copy = workspace.Duplicate(GitHubId);
        var restored = workspace.RestoreDeleted(entry);
        var kept = workspace.KeepRetiredBuiltIn("data-and-ai");
        var plan = LibraryImportPlanner.Plan(Document("Imported"), new LibraryImportTarget.NewLibrary("imported.csv"), workspace.Draft);
        workspace.ApplyImport(plan, ImportConflictChoice.KeepMine);
        var imported = workspace.Draft.Libraries.Single(library => library.Origin == LibraryOrigin.Imported).Content.Id;
        workspace.Rename("custom-github", "Renamed twin");

        var draft = workspace.Draft;
        Assert.Null(draft.Find(GitHubId)!.FileName);
        Assert.Equal("github.csv", draft.Find("custom-github")!.FileName);
        Assert.Equal("custom-github", draft.Find("custom-github")!.Content.Id);
        Assert.Equal("epsilon.csv", draft.Find("epsilon")!.FileName);
        foreach (var id in new[] { created, copy, restored, kept, imported })
        {
            Assert.Equal(id + ".csv", draft.Find(id)!.FileName);
        }

        // Ranking the draft by file name puts the twin where the committed catalog has it, by github.csv: after
        // epsilon.csv, where 0.4.3 ranked it, not before it as custom-github.csv would (review finding A16).
        var order = draft.Libraries.Select(library => library.Content.Id).ToList();
        Assert.True(order.IndexOf("epsilon") < order.IndexOf("custom-github"));
        Assert.Equal(
            catalog.Libraries.Select(library => library.Content.Id).ToList(),
            order.Where(id => catalog.Find(id) is not null).ToList());
        var winner = LibraryPrecedence
            .Order(draft.Libraries, library => library.Content.Id, library => library.Content.BuiltIn, library => library.FileName)
            .First(library => library.Content.Rows.Any(row => row.Values.Spoken == "project token"));
        Assert.Equal("epsilon", winner.Content.Id);
    }

    [Fact]
    public void D17_a_cleanup_plan_switches_off_exactly_what_it_switches_off_and_undo_brings_them_back()
    {
        var catalog = Catalog(
            [BuiltIn(GitHubId), BuiltIn(AzureId), Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes")])],
            [GitHubId, AzureId, "team-terms"],
            ai: [new("team-terms", true)]);
        var workspace = Workspace(catalog);
        LibraryUsage Usage(string id, bool builtIn) => new(id, id, [], UnusedCount: 1, builtIn);
        var switchingOff = new[] { Usage(GitHubId, true), Usage(AzureId, true), Usage("team-terms", false) };
        var plan = new LibrarySwitchOffCopy.Result([], 0, [new LibrarySwitchOffCopy.KeptOnLibrary(AzureId, true, "Microsoft Azure", 2)]);

        var switchedOff = workspace.ApplyDictionaryCleanup(switchingOff, plan);

        Assert.Equal([GitHubId, "team-terms"], switchedOff);
        Assert.Equal([AzureId], workspace.Draft.LocalState.EnabledIds);
        var changes = Capture(workspace);
        Assert.Empty(changes.Writes);
        Assert.True(changes.LocalStateChanged);
        Assert.Equal([AzureId], changes.LocalState.EnabledIds);
        Assert.Equal("Turn off unused libraries", workspace.UndoLabel);

        workspace.Undo();
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Equal(3, workspace.Draft.LocalState.EnabledIds.Count);

        var keepsAll = new LibrarySwitchOffCopy.Result([], 0,
            switchingOff.Select(usage => new LibrarySwitchOffCopy.KeptOnLibrary(usage.Id, usage.BuiltIn, usage.Name, 1)).ToList());
        var revision = workspace.Revision;
        Assert.Empty(workspace.ApplyDictionaryCleanup(switchingOff, keepsAll));
        Assert.Equal(revision, workspace.Revision);
        Assert.False(workspace.HasUnsavedChanges);

        // A twin goes off by its own logical id; the built-in with its stem stays on.
        Assert.Throws<ArgumentException>(() => workspace.ApplyDictionaryCleanup([Usage("custom-github", false)], plan));
    }
}
