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
    public void D4_a_library_kept_under_another_id_keeps_the_choices_of_the_draft_whatever_the_store_committed_for_it()
    {
        // GPT-6 Astra's note on round 3: by the contract the store carries the draft's enabled state and AI choice to the
        // kept id, but a store that committed the kept library without them (off, no AI choice, as a file it found) must
        // not have MarkSaved adopt that silently. The choices of the live draft stand, unsaved where the store did not
        // commit them, and the next Save writes them.
        var catalog = Standard();
        LibraryWorkspace Saving(out LibraryChangeSet changes, out LibraryCatalog saved, out string keptAs)
        {
            var workspace = Workspace(catalog);
            var created = workspace.CreateLibrary();
            workspace.AddTerm(created, new TermValues("ga", "GA"));
            Assert.True(workspace.SetAiPermission(created, true).Applied);
            changes = Capture(workspace);
            keptAs = created + "-2";
            saved = Apply(catalog, changes, outcomes: [new StoreOutcome.SavedUnderNewId(created, keptAs, [new TermValues("theirs", "Theirs")])]);
            return workspace;
        }

        // The store as the contract has it: the choices went with the content, so nothing is left unsaved.
        var workspace = Saving(out var changes, out var saved, out var keptAs);
        Assert.Contains(keptAs, saved.LocalState.EnabledIds);
        workspace.MarkSaved(changes.DraftRevision, saved);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Contains(keptAs, workspace.Draft.LocalState.EnabledIds);
        Assert.True(workspace.ShowsAiPermission(keptAs));

        // A store that committed the kept library off and with no AI choice.
        workspace = Saving(out changes, out saved, out keptAs);
        var id = keptAs;
        var local = saved.LocalState;
        var withoutChoices = new LibraryCatalog(
            saved.Generation,
            saved.Libraries.ToList(),
            LibraryLocalState.Create(
                local.EnabledIds.Where(enabled => !string.Equals(enabled, id, StringComparison.OrdinalIgnoreCase)),
                local.LegacyEnabledIds,
                local.AiPermissions.Where(pair => !string.Equals(pair.Key, id, StringComparison.OrdinalIgnoreCase)),
                local.LegacyMarkers,
                local.AiUpgradeNotice,
                LocalStateHealth.Ok,
                local.AcceptedContent,
                local.AiPermissionsLost),
            saved.RecentlyDeleted.ToList(),
            saved.RetiredBuiltInEdits.ToList(),
            filesAwaitingRelease: 0,
            saved.KeptVersions.ToList());
        workspace.MarkSaved(changes.DraftRevision, withoutChoices);

        Assert.Contains(keptAs, workspace.Draft.LocalState.EnabledIds);
        Assert.True(workspace.Draft.LocalState.AiPermissions[keptAs]);
        Assert.True(workspace.HasUnsavedChanges);
        Assert.Equal([keptAs], workspace.UnsavedLibraryIds);
        var next = Capture(workspace);
        AssertPreImages(withoutChoices, next);
        Assert.False(Writes(next, keptAs));
        Assert.Contains(keptAs, next.LocalState.EnabledIds);
        Assert.True(next.LocalState.AiPermissions[keptAs]);
        workspace.MarkSaved(next.DraftRevision, Apply(withoutChoices, next));
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Contains(keptAs, workspace.Draft.LocalState.EnabledIds);

        // A choice the user changed while the Save ran stands against the one the store committed too.
        workspace = Saving(out changes, out saved, out keptAs);
        var planned = keptAs[..^2];
        workspace.SetEnabled(planned, false);
        workspace.MarkSaved(changes.DraftRevision, saved);
        Assert.DoesNotContain(keptAs, workspace.Draft.LocalState.EnabledIds);
        Assert.Equal([keptAs], workspace.UnsavedLibraryIds);
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
    public void D4_a_new_library_deleted_while_its_save_runs_is_a_pending_deletion_of_the_saved_library()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var created = workspace.CreateLibrary();
        workspace.Rename(created, "Release notes");
        workspace.AddTerm(created, new TermValues("ga", "general availability"));
        var changes = Capture(workspace);

        // While the Save runs, the user deletes the library it is saving, and makes another: the Save may still bring
        // the first back under its id, so the new one never takes it.
        workspace.DeleteLibrary(created);
        var another = workspace.CreateLibrary();
        Assert.NotEqual(created, another, StringComparer.OrdinalIgnoreCase);
        workspace.AddTerm(another, new TermValues("rc", "release candidate"));
        var saved = Apply(catalog, changes);
        workspace.MarkSaved(changes.DraftRevision, saved);

        var committed = saved.Find(created)!;
        Assert.True(workspace.HasUnsavedChanges);
        Assert.Equal([created, another], workspace.UnsavedLibraryIds.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(workspace.Draft.Find(created)!.PendingDelete);
        Assert.Equal([new TermValues("rc", "release candidate")], ValuesOf(workspace, another));
        var next = Capture(workspace);
        var deletion = Assert.Single(next.Deletions);
        Assert.Equal((created, committed.FileName, committed.ContentHash!.Value), (deletion.LibraryId, deletion.FileName, deletion.ExpectedPreImage));
        Assert.Equal(another, Assert.Single(next.Writes).LibraryId);

        // Saving it leaves what the user saw: the first library gone, the second saved, and nothing unsaved.
        var deleted = Apply(saved, next);
        workspace.MarkSaved(next.DraftRevision, deleted);
        Assert.Null(deleted.Find(created));
        Assert.NotNull(deleted.Find(another));
        Assert.False(workspace.HasUnsavedChanges);
    }

    [Fact]
    public void D4_a_deletion_undone_while_its_save_runs_stays_an_unsaved_restore()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var rows = ValuesOf(workspace, "team-terms");
        workspace.DeleteLibrary("team-terms");
        var changes = Capture(workspace);
        Assert.Single(changes.Deletions);

        // While the Save runs, the user takes the deletion back.
        workspace.Undo();
        var store = new Dictionary<string, RecentlyDeletedContent>(StringComparer.OrdinalIgnoreCase);
        var saved = Apply(catalog, changes, store);
        workspace.MarkSaved(changes.DraftRevision, saved);

        var entry = Assert.Single(saved.RecentlyDeleted);
        var library = workspace.Draft.Find("team-terms");
        Assert.NotNull(library);
        Assert.Equal((LibraryOrigin.Restored, false, true), (library.Origin, library.PendingDelete, library.Unsaved));
        Assert.Equal(rows, ValuesOf(workspace, "team-terms"));
        Assert.Contains("team-terms", workspace.Draft.LocalState.EnabledIds);
        Assert.True(workspace.Draft.LocalState.AiPermissions["team-terms"]);
        Assert.Empty(workspace.Draft.RecentlyDeleted);
        Assert.Equal(["team-terms"], workspace.UnsavedLibraryIds);

        // The next Save restores the entry that Save made, and the library reads back as the user saw it.
        var next = Capture(workspace);
        var restore = Assert.Single(next.RecentlyDeletedActions);
        Assert.Equal(
            (RecentlyDeletedActionKind.Restore, entry.EntryName, entry.ContentHash, "team-terms"),
            (restore.Kind, restore.EntryName, restore.ExpectedHash, restore.RestoreAsId));
        Assert.Empty(next.Writes);
        var restored = Apply(saved, next, store);
        workspace.MarkSaved(next.DraftRevision, restored);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Equal(rows, ValuesOf(workspace, "team-terms"));
        Assert.Contains("team-terms", restored.LocalState.EnabledIds);
        Assert.True(restored.LocalState.AiPermissions["team-terms"]);
    }

    [Fact]
    public void D4_a_restore_redone_while_its_save_runs_becomes_a_new_library_and_names_no_consumed_entry()
    {
        // GPT-6 Astra's A8 sequence: the Save in flight restores the entry as "old"; meanwhile the draft's "old" is
        // deleted, which offers the entry again, and the entry is restored again, under another id.
        var entry = Deleted("20260901T100000Z.old.csv", "old", "Old", new TermValues("old term", "Old term"));
        var store = new Dictionary<string, RecentlyDeletedContent>(StringComparer.OrdinalIgnoreCase) { [entry.Entry.EntryName] = entry };
        var catalog = Catalog([BuiltIn(GitHubId), BuiltIn(AzureId)], [GitHubId], recentlyDeleted: [entry.Entry]);
        var workspace = Workspace(catalog);
        Assert.Equal("old", workspace.RestoreDeleted(entry));
        var changes = Capture(workspace);

        workspace.DeleteLibrary("old");
        Assert.Single(workspace.Draft.RecentlyDeleted);
        var again = workspace.RestoreDeleted(entry);
        Assert.Equal("custom-old", again);
        workspace.EditTerm(again, RowIdOf(workspace, again, "old term"), new TermValues("old term", "Older term"));
        var saved = Apply(catalog, changes, store);
        Assert.Empty(saved.RecentlyDeleted);
        workspace.MarkSaved(changes.DraftRevision, saved);

        // The entry is spent: "old" is committed and deleted in the draft, and the second restore is a new library with
        // the content the draft holds, so nothing names an entry the catalog no longer lists.
        Assert.True(workspace.Draft.Find("old")!.PendingDelete);
        var kept = workspace.Draft.Find(again)!;
        Assert.Equal((LibraryOrigin.Created, true, true), (kept.Origin, kept.Unsaved, kept.WritesContent));
        Assert.Equal([new TermValues("old term", "Older term")], ValuesOf(workspace, again));
        var next = Capture(workspace);
        AssertPreImages(saved, next);
        Assert.Empty(next.RecentlyDeletedActions);
        Assert.Equal("old", Assert.Single(next.Deletions).LibraryId);
        Assert.Equal((again, (LibraryContentHash?)null), (Assert.Single(next.Writes).LibraryId, Assert.Single(next.Writes).ExpectedPreImage));
        var after = Apply(saved, next, store);
        workspace.MarkSaved(next.DraftRevision, after);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Equal([new TermValues("old term", "Older term")], after.Find(again)!.Content.Rows.Select(row => row.Values));
    }

    [Fact]
    public void D4_a_permanent_deletion_of_an_entry_its_save_consumed_is_dropped()
    {
        // The same sequence with Delete permanently instead of the second restore.
        var entry = Deleted("20260901T100000Z.old.csv", "old", "Old", new TermValues("old term", "Old term"));
        var store = new Dictionary<string, RecentlyDeletedContent>(StringComparer.OrdinalIgnoreCase) { [entry.Entry.EntryName] = entry };
        var catalog = Catalog([BuiltIn(GitHubId), BuiltIn(AzureId)], [GitHubId], recentlyDeleted: [entry.Entry]);
        var workspace = Workspace(catalog);
        workspace.RestoreDeleted(entry);
        var changes = Capture(workspace);

        workspace.DeleteLibrary("old");
        workspace.DeletePermanently(entry.Entry);
        Assert.Empty(workspace.Draft.RecentlyDeleted);
        var saved = Apply(catalog, changes, store);
        workspace.MarkSaved(changes.DraftRevision, saved);

        var next = Capture(workspace);
        AssertPreImages(saved, next);
        Assert.Empty(next.RecentlyDeletedActions);
        Assert.Equal("old", Assert.Single(next.Deletions).LibraryId);
        workspace.MarkSaved(next.DraftRevision, Apply(saved, next, store));
        Assert.False(workspace.HasUnsavedChanges);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("imported")]
    [InlineData("restored")]
    public void D4_a_library_at_the_id_a_save_kept_another_under_keeps_its_content_and_its_choices(string how)
    {
        // Grok 4.7's G1 sequence: the Save in flight creates "custom-new-library"; another app creates that file meanwhile,
        // so the store keeps Scribe's content as "custom-new-library-2", an id it invents at commit. While the Save ran
        // the user made another library that holds exactly that id.
        var gone = Deleted("20260801T100000Z.custom-new-library-2.csv", "custom-new-library-2", "Archived notes", new TermValues("zz", "ZZ"));
        var store = new Dictionary<string, RecentlyDeletedContent>(StringComparer.OrdinalIgnoreCase) { [gone.Entry.EntryName] = gone };
        var catalog = Catalog([BuiltIn(GitHubId), BuiltIn(AzureId)], [GitHubId], recentlyDeleted: how == "restored" ? [gone.Entry] : []);
        var workspace = Workspace(catalog);
        var planned = workspace.CreateLibrary();
        workspace.AddTerm(planned, new TermValues("ga", "general availability"));
        workspace.SetAiPermission(planned, false);
        var changes = Capture(workspace);

        // An edit of the library the Save is writing follows it to the id the store keeps it under.
        workspace.AddTerm(planned, new TermValues("beta", "Beta"));
        var occupant = how switch
        {
            "created" => workspace.CreateLibrary(),
            "imported" => ImportNew(workspace, Document("New library 2", new TermValues("rc", "release candidate"))),
            _ => workspace.RestoreDeleted(gone),
        };
        Assert.Equal("custom-new-library-2", occupant);
        if (how == "created")
        {
            workspace.AddTerm(occupant, new TermValues("rc", "release candidate"));
        }

        workspace.SetEnabled(occupant, true);
        workspace.SetAiPermission(occupant, true);
        var occupantRows = ValuesOf(workspace, occupant);
        var saved = Apply(catalog, changes, store,
            [new StoreOutcome.SavedUnderNewId(planned, "custom-new-library-2", [new TermValues("theirs", "Theirs")])]);
        workspace.MarkSaved(changes.DraftRevision, saved);

        // Scribe's library is the kept one, with its rows and choices; the user's other library moved to a fresh id with
        // its own; the planned id is the other app's library.
        var draft = workspace.Draft;
        Assert.Equal(
            [new TermValues("ga", "general availability"), new TermValues("beta", "Beta")],
            ValuesOf(workspace, "custom-new-library-2"));
        Assert.Equal([new TermValues("theirs", "Theirs")], ValuesOf(workspace, planned));
        Assert.False(draft.LocalState.AiPermissions["custom-new-library-2"]);
        Assert.Contains("custom-new-library-2", draft.LocalState.EnabledIds);
        var moved = draft.Libraries.Single(library => library.Content.Rows.Select(row => row.Values).SequenceEqual(occupantRows)).Content.Id;
        Assert.NotEqual("custom-new-library-2", moved);
        Assert.True(draft.LocalState.AiPermissions[moved]);
        Assert.Contains(moved, draft.LocalState.EnabledIds);
        Assert.Equal("Their notes", draft.Find(planned)!.Content.Name);

        var next = Capture(workspace);
        AssertPreImages(saved, next);
        Assert.True(Writes(next, moved));
        Assert.Equal(saved.Find("custom-new-library-2")!.ContentHash, next.Writes.Single(write => write.LibraryId == "custom-new-library-2").ExpectedPreImage);
        Assert.False(Writes(next, planned));
        workspace.MarkSaved(next.DraftRevision, Apply(saved, next, store));
        Assert.False(workspace.HasUnsavedChanges);
    }

    [Fact]
    public void D4_a_copy_made_while_its_originals_save_ran_follows_the_original_to_the_id_the_store_kept_it_under()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var planned = workspace.CreateLibrary();
        workspace.AddTerm(planned, new TermValues("ga", "general availability"));
        var changes = Capture(workspace);
        var copy = workspace.Duplicate(planned);

        workspace.MarkSaved(changes.DraftRevision, Apply(catalog, changes, outcomes:
            [new StoreOutcome.SavedUnderNewId(planned, planned + "-7", [new TermValues("theirs", "Theirs")])]));

        Assert.Equal(planned + "-7", workspace.Draft.Find(copy)!.Content.BasedOn);
        workspace.UseCopyInstead(copy);
        Assert.DoesNotContain(planned + "-7", workspace.Draft.LocalState.EnabledIds);
        Assert.Contains(copy, workspace.Draft.LocalState.EnabledIds);
    }

    [Fact]
    public void D4_a_copy_the_save_wrote_of_a_library_kept_under_another_id_is_repaired_and_never_turns_off_the_other_apps_library()
    {
        // GPT-6 Astra's A10, first sequence: a library and its duplicate saved together; the store keeps the library under
        // another id because another app took its file, so the copy's file names the other app's library.
        var catalog = Standard();
        LibraryWorkspace SavedTogether(out string original, out string copy, out string keptAs, out LibraryCatalog saved)
        {
            var workspace = Workspace(catalog);
            original = workspace.CreateLibrary();
            workspace.AddTerm(original, new TermValues("ga", "general availability"));
            copy = workspace.Duplicate(original);
            var changes = Capture(workspace);
            keptAs = original + "-7";
            saved = Apply(catalog, changes, outcomes: [new StoreOutcome.SavedUnderNewId(original, keptAs, [new TermValues("theirs", "Theirs")])]);
            workspace.MarkSaved(changes.DraftRevision, saved);
            return workspace;
        }

        var workspace = SavedTogether(out var original, out var copy, out var keptAs, out var saved);
        Assert.Equal(original, saved.Find(copy)!.Content.BasedOn);

        // The draft repairs the reference, which is unsaved until the next Save writes it.
        Assert.Equal(keptAs, workspace.Draft.Find(copy)!.Content.BasedOn);
        Assert.True(WritesContent(workspace, copy));
        Assert.Equal(keptAs, workspace.CopyOriginal(copy));

        // A copy made now of the other app's library names it, and that reference is its own.
        var theirsCopy = workspace.Duplicate(original);
        Assert.Equal(original, workspace.CopyOriginal(theirsCopy));

        // Use this copy instead turns off the library the copy was made from, never the other app's, even while on.
        workspace.SetEnabled(original, true);
        workspace.UseCopyInstead(copy);
        var enabled = workspace.Draft.LocalState.EnabledIds;
        Assert.Contains(copy, enabled);
        Assert.DoesNotContain(keptAs, enabled);
        Assert.Contains(original, enabled);

        // Saved, the file names the kept id, and after a reload the command still acts on the right library.
        var next = Capture(workspace);
        Assert.Equal(keptAs, next.Writes.Single(write => write.LibraryId == copy).Content!.BasedOn);
        var repaired = Apply(saved, next);
        workspace.MarkSaved(next.DraftRevision, repaired);
        Assert.False(workspace.HasPendingReferenceRepairs);
        workspace.Reload(repaired);
        workspace.SetEnabled(keptAs, true);
        workspace.SetEnabled(copy, false);
        workspace.UseCopyInstead(copy);
        Assert.Equal((true, false, true), Enabled(workspace, copy, keptAs, original));

        // Reloaded before the repair was saved, the file still names the other app's library: the command refuses rather
        // than turn that one off.
        var reloaded = SavedTogether(out original, out copy, out keptAs, out saved);
        reloaded.Reload(saved);
        Assert.Equal(original, reloaded.Draft.Find(copy)!.Content.BasedOn);
        Assert.Null(reloaded.CopyOriginal(copy));
        reloaded.SetEnabled(original, true);
        Assert.Throws<InvalidOperationException>(() => reloaded.UseCopyInstead(copy));
        Assert.Equal((false, true, true), Enabled(reloaded, copy, keptAs, original));
    }

    [Fact]
    public void D4_a_copy_made_while_the_save_ran_of_an_original_removed_meanwhile_names_where_the_store_kept_it()
    {
        // A10, second sequence: the copy is made while the Save runs and its original then leaves the draft, so only the
        // base knows where the store kept the original.
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var original = workspace.CreateLibrary();
        workspace.AddTerm(original, new TermValues("ga", "general availability"));
        var changes = Capture(workspace);
        var copy = workspace.Duplicate(original);
        workspace.DeleteLibrary(original);
        var keptAs = original + "-7";
        var saved = Apply(catalog, changes, outcomes: [new StoreOutcome.SavedUnderNewId(original, keptAs, [new TermValues("theirs", "Theirs")])]);
        workspace.MarkSaved(changes.DraftRevision, saved);

        Assert.True(workspace.Draft.Find(keptAs)!.PendingDelete);
        Assert.Equal(keptAs, workspace.Draft.Find(copy)!.Content.BasedOn);
        Assert.Null(workspace.CopyOriginal(copy));
        workspace.SetEnabled(original, true);
        Assert.Throws<InvalidOperationException>(() => workspace.UseCopyInstead(copy));
        Assert.Equal((false, true), Enabled(workspace, copy, original));

        // Saved and reloaded, the copy names its deleted original, and the command still changes no other library.
        var next = Capture(workspace);
        var after = Apply(saved, next);
        workspace.MarkSaved(next.DraftRevision, after);
        workspace.Reload(after);
        Assert.Equal(keptAs, workspace.Draft.Find(copy)!.Content.BasedOn);
        Assert.Throws<InvalidOperationException>(() => workspace.UseCopyInstead(copy));
        Assert.Equal((false, true), Enabled(workspace, copy, original));
    }

    private static (bool, bool) Enabled(LibraryWorkspace workspace, string first, string second) =>
        (workspace.Draft.LocalState.EnabledIds.Contains(first), workspace.Draft.LocalState.EnabledIds.Contains(second));

    // The rule for W2 after round 4: a Save whose outcome kept a library under another id is completed by a follow-up Save
    // while reference repairs are pending, so a copy's file names the other app's library only until then; and Use this
    // copy instead is confirmed with the name of the library CopyOriginal gives.
    [Fact]
    public void D4_a_reference_repair_is_pending_from_the_save_that_kept_the_original_elsewhere_until_a_save_writes_it()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var original = workspace.CreateLibrary();
        workspace.AddTerm(original, new TermValues("ga", "general availability"));
        var copy = workspace.Duplicate(original);
        Assert.False(workspace.HasPendingReferenceRepairs);
        var changes = Capture(workspace);
        var keptAs = original + "-7";
        var saved = Apply(catalog, changes, outcomes: [new StoreOutcome.SavedUnderNewId(original, keptAs, [new TermValues("theirs", "Theirs")])]);
        Assert.False(workspace.HasPendingReferenceRepairs);
        workspace.MarkSaved(changes.DraftRevision, saved);

        Assert.True(workspace.HasPendingReferenceRepairs);
        Assert.Equal([copy], workspace.PendingReferenceRepairs);
        Assert.Equal(keptAs, workspace.CopyOriginal(copy));

        // The follow-up Save writes the repair, which is pending until that Save is marked saved.
        var followUp = RepairsOf(workspace);
        Assert.Equal([copy], followUp.Writes.Select(write => write.LibraryId));
        Assert.Equal(keptAs, followUp.Writes.Single(write => write.LibraryId == copy).Content!.BasedOn);
        Assert.True(workspace.HasPendingReferenceRepairs);
        var repaired = Apply(saved, followUp);
        workspace.MarkSaved(followUp.DraftRevision, repaired);
        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.Empty(workspace.PendingReferenceRepairs);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Equal(keptAs, repaired.Find(copy)!.Content.BasedOn);
        Assert.Equal(keptAs, workspace.CopyOriginal(copy));

        workspace.Reload(repaired);
        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.Equal(keptAs, workspace.CopyOriginal(copy));
    }

    [Fact]
    public void D4_no_reference_repair_is_pending_when_no_saved_copy_names_a_library_kept_elsewhere()
    {
        var catalog = Standard();
        TermValues[] theirs = [new("theirs", "Theirs")];

        // A library and its copy saved as planned.
        var workspace = Workspace(catalog);
        var original = workspace.CreateLibrary();
        workspace.AddTerm(original, new TermValues("ga", "general availability"));
        var copy = workspace.Duplicate(original);
        var changes = Capture(workspace);
        workspace.MarkSaved(changes.DraftRevision, Apply(catalog, changes));
        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.Equal(original, workspace.CopyOriginal(copy));

        // A library with no copy kept under another id.
        workspace = Workspace(catalog);
        var alone = workspace.CreateLibrary();
        workspace.AddTerm(alone, new TermValues("ga", "general availability"));
        changes = Capture(workspace);
        workspace.MarkSaved(changes.DraftRevision, Apply(catalog, changes, outcomes: [new StoreOutcome.SavedUnderNewId(alone, alone + "-7", theirs)]));
        Assert.False(workspace.HasPendingReferenceRepairs);

        // A copy made while the Save ran is corrected before any file holds it, and then saved with the right reference.
        workspace = Workspace(catalog);
        var planned = workspace.CreateLibrary();
        workspace.AddTerm(planned, new TermValues("ga", "general availability"));
        changes = Capture(workspace);
        var later = workspace.Duplicate(planned);
        var saved = Apply(catalog, changes, outcomes: [new StoreOutcome.SavedUnderNewId(planned, planned + "-7", theirs)]);
        workspace.MarkSaved(changes.DraftRevision, saved);
        Assert.Equal(planned + "-7", workspace.Draft.Find(later)!.Content.BasedOn);
        Assert.False(workspace.HasPendingReferenceRepairs);
        var next = Capture(workspace);
        workspace.MarkSaved(next.DraftRevision, Apply(saved, next));
        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.Equal(planned + "-7", workspace.CopyOriginal(later));
    }

    [Fact]
    public void D4_a_reference_repair_discarded_deleted_or_reloaded_is_no_longer_pending()
    {
        var catalog = Standard();
        LibraryWorkspace Repairing(out string original, out string copy, out LibraryCatalog saved)
        {
            var workspace = Workspace(catalog);
            original = workspace.CreateLibrary();
            workspace.AddTerm(original, new TermValues("ga", "general availability"));
            copy = workspace.Duplicate(original);
            var changes = Capture(workspace);
            saved = Apply(catalog, changes, outcomes: [new StoreOutcome.SavedUnderNewId(original, original + "-7", [new TermValues("theirs", "Theirs")])]);
            workspace.MarkSaved(changes.DraftRevision, saved);
            Assert.True(workspace.HasPendingReferenceRepairs);
            return workspace;
        }

        // Discarded: the copy is its file again, whose reference this session knows to be stale.
        var workspace = Repairing(out var original, out var copy, out _);
        workspace.DiscardLibrary(copy);
        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.Equal(original, workspace.Draft.Find(copy)!.Content.BasedOn);
        Assert.Null(workspace.CopyOriginal(copy));

        // Deleted: the Save moves the file to Recently deleted, and there is nothing to repair.
        workspace = Repairing(out _, out copy, out _);
        workspace.DeleteLibrary(copy);
        Assert.False(workspace.HasPendingReferenceRepairs);

        // Reloaded before the follow-up Save: the same as a discard.
        workspace = Repairing(out _, out copy, out var saved);
        workspace.Reload(saved);
        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.Null(workspace.CopyOriginal(copy));
    }

    [Fact]
    public void D4_a_reference_repair_stays_pending_across_a_rebase_until_the_file_holds_it()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var original = workspace.CreateLibrary();
        workspace.AddTerm(original, new TermValues("ga", "general availability"));
        var copy = workspace.Duplicate(original);
        var changes = Capture(workspace);
        var keptAs = original + "-7";
        var saved = Apply(catalog, changes, outcomes: [new StoreOutcome.SavedUnderNewId(original, keptAs, [new TermValues("theirs", "Theirs")])]);
        workspace.MarkSaved(changes.DraftRevision, saved);
        Assert.True(workspace.HasPendingReferenceRepairs);

        LibraryCatalog WithCopy(LibraryCatalog from, Func<LibraryContent, LibraryContent> change) =>
            new(
                from.Generation + 1,
                from.Libraries
                    .Select(library => library.Content.Id == copy
                        ? new CatalogLibrary(change(library.Content), library.State, library.FileName, HashOf(change(library.Content)))
                        : library)
                    .ToList(),
                from.LocalState,
                from.RecentlyDeleted.ToList(),
                from.RetiredBuiltInEdits.ToList(),
                filesAwaitingRelease: 0,
                from.KeptVersions.ToList());

        // Another app renamed the copy's file, which still names the planned id: the repair stays pending, and trusted.
        var renamedOutside = WithCopy(saved, content => content with { Name = "Renamed outside" });
        workspace.Rebase(renamedOutside);
        Assert.Equal("Renamed outside", workspace.Draft.Find(copy)!.Content.Name);
        Assert.Equal(keptAs, workspace.Draft.Find(copy)!.Content.BasedOn);
        Assert.Equal([copy], workspace.PendingReferenceRepairs);
        Assert.Equal(keptAs, workspace.CopyOriginal(copy));

        // Another Scribe sharing the data folder wrote the repair: the file holds it, so nothing is pending or unsaved.
        var written = WithCopy(renamedOutside, content => content with { BasedOn = keptAs });
        workspace.Rebase(written);
        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Equal(keptAs, workspace.CopyOriginal(copy));
    }

    [Fact]
    public void D4_a_repair_discarded_while_the_follow_up_save_ran_is_not_pending_and_use_this_copy_instead_stays_refused()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var original = workspace.CreateLibrary();
        workspace.AddTerm(original, new TermValues("ga", "general availability"));
        var copy = workspace.Duplicate(original);
        var changes = Capture(workspace);
        var keptAs = original + "-7";
        var saved = Apply(catalog, changes, outcomes: [new StoreOutcome.SavedUnderNewId(original, keptAs, [new TermValues("theirs", "Theirs")])]);
        workspace.MarkSaved(changes.DraftRevision, saved);

        var followUp = RepairsOf(workspace);
        workspace.DiscardLibrary(copy);
        var repaired = Apply(saved, followUp);
        workspace.MarkSaved(followUp.DraftRevision, repaired);

        // The Save wrote the repair; the discard made meanwhile is kept like any change made while a Save runs, so the copy
        // shows the reference its file held before, as an unsaved change, and no repair is pending.
        Assert.Equal(keptAs, repaired.Find(copy)!.Content.BasedOn);
        Assert.Equal(original, workspace.Draft.Find(copy)!.Content.BasedOn);
        Assert.Contains(copy, workspace.UnsavedLibraryIds);
        Assert.False(workspace.HasPendingReferenceRepairs);

        // That reference is neither its file's nor Scribe's repair, so Use this copy instead refuses rather than turn off the
        // other app's library.
        Assert.Null(workspace.CopyOriginal(copy));
        workspace.SetEnabled(original, true);
        Assert.Throws<InvalidOperationException>(() => workspace.UseCopyInstead(copy));
        Assert.Contains(original, workspace.Draft.LocalState.EnabledIds);

        // Discarded again, the copy is its file, which names where the original went.
        workspace.DiscardLibrary(copy);
        Assert.Equal(keptAs, workspace.Draft.Find(copy)!.Content.BasedOn);
        Assert.Equal(keptAs, workspace.CopyOriginal(copy));
    }

    // A library and its copy saved together, the store keeping the library under another id, so the copy's reference is
    // repaired in the draft and pending; `whileSaving` is what the user does, to the copy or elsewhere, while that Save runs.
    private static LibraryWorkspace PendingRepair(
        LibraryCatalog catalog, out string copy, out string keptAs, out LibraryCatalog saved,
        Action<LibraryWorkspace, string>? whileSaving = null)
    {
        var workspace = Workspace(catalog);
        var original = workspace.CreateLibrary();
        workspace.AddTerm(original, new TermValues("ga", "general availability"));
        copy = workspace.Duplicate(original);
        var changes = Capture(workspace);
        whileSaving?.Invoke(workspace, copy);
        keptAs = original + "-7";
        saved = Apply(catalog, changes, outcomes: [new StoreOutcome.SavedUnderNewId(original, keptAs, [new TermValues("theirs", "Theirs")])]);
        workspace.MarkSaved(changes.DraftRevision, saved);
        Assert.Equal([copy], workspace.PendingReferenceRepairs);
        return workspace;
    }

    private static bool? AiOf(LibraryLocalState state, string id) =>
        state.AiPermissions.TryGetValue(id, out var permitted) ? permitted : null;

    private static string[] Sorted(IEnumerable<string> ids) => [.. ids.Order(StringComparer.Ordinal)];

    // The ruling on the follow-up Save: W2 runs it without the user pressing Save, so it writes the reference repairs and
    // nothing else, and every other change waits for the user's own Save (plan 3.9).
    [Fact]
    public void D4_the_follow_up_save_writes_only_the_reference_repairs_and_an_edit_made_while_the_first_save_ran_stays_unsaved()
    {
        var catalog = Standard();
        var workspace = PendingRepair(catalog, out var copy, out var keptAs, out var saved, (w, _) =>
        {
            w.EditTerm("team-terms", RowIdOf(w, "team-terms", "kube"), new TermValues("kube", "K8s"));
            w.SetEnabled(AzureId, true);
        });
        Assert.Equal(Sorted([AzureId, "team-terms", copy]), Sorted(workspace.UnsavedLibraryIds));

        // The repair alone: the copy's file with its based-on line corrected, the rest of the draft left out.
        var followUp = RepairsOf(workspace);
        var file = saved.Find(copy)!;
        var write = Assert.Single(followUp.Writes);
        Assert.Equal(copy, write.LibraryId);
        Assert.False(write.BuiltIn);
        Assert.Equal(file.ContentHash, write.ExpectedPreImage);
        Assert.Equal(Describe(file.Content with { BasedOn = keptAs }), Describe(write.Content!));
        Assert.Empty(followUp.Deletions);
        Assert.Empty(followUp.RecentlyDeletedActions);
        Assert.False(followUp.LocalStateChanged);
        Assert.Equal(Sorted(saved.LocalState.EnabledIds), Sorted(followUp.LocalState.EnabledIds));
        Assert.Equal(
            saved.LocalState.AiPermissions.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            followUp.LocalState.AiPermissions.OrderBy(pair => pair.Key, StringComparer.Ordinal));

        var repaired = Apply(saved, followUp);
        workspace.MarkSaved(followUp.DraftRevision, repaired);

        // The repair is committed, and nothing else is.
        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.Equal(keptAs, repaired.Find(copy)!.Content.BasedOn);
        Assert.Equal(new TermValues("kube", "Kubernetes"), repaired.Find("team-terms")!.Content.Rows[0].Values);
        Assert.DoesNotContain(AzureId, repaired.LocalState.EnabledIds);

        // The edit and the switch are still the draft's and unsaved, and the user's own Save writes them and nothing of the
        // copy.
        Assert.Equal(new TermValues("kube", "K8s"), ValuesOf(workspace, "team-terms")[0]);
        Assert.Contains(AzureId, workspace.Draft.LocalState.EnabledIds);
        Assert.Equal(Sorted([AzureId, "team-terms"]), Sorted(workspace.UnsavedLibraryIds));
        var next = Capture(workspace);
        AssertPreImages(repaired, next);
        Assert.Equal(["team-terms"], next.Writes.Select(change => change.LibraryId));
        Assert.True(next.LocalStateChanged);
        Assert.Contains(AzureId, next.LocalState.EnabledIds);
        workspace.MarkSaved(next.DraftRevision, Apply(repaired, next));
        Assert.False(workspace.HasUnsavedChanges);
    }

    [Fact]
    public void D4_the_follow_up_save_writes_only_a_copys_based_on_line_whatever_else_the_user_changed_in_it()
    {
        // While the first Save runs, the user renames the copy and adds a term to it.
        var catalog = Standard();
        var workspace = PendingRepair(catalog, out var copy, out var keptAs, out var saved, (w, c) =>
        {
            Assert.True(w.Rename(c, "My copy").Applied);
            Assert.True(w.AddTerm(c, new TermValues("rc", "release candidate")).Applied);
        });

        // And after it, before the follow-up: its details, a row, its switches.
        Assert.True(workspace.SetDetails(copy, "Release", "Notes for releases").Applied);
        Assert.True(workspace.EditTerm(copy, RowIdOf(workspace, copy, "ga"), new TermValues("ga", "GA")).Applied);
        workspace.SetEnabled(copy, true);
        var ai = !workspace.ShowsAiPermission(copy);
        Assert.True(workspace.SetAiPermission(copy, ai).Applied);
        var mine = workspace.Draft.Find(copy)!.Content;
        Assert.Equal(keptAs, mine.BasedOn);

        // Only its based-on line is written: the rest of the file stays what the first Save wrote, and so do its switches.
        var followUp = RepairsOf(workspace);
        var file = saved.Find(copy)!;
        var write = Assert.Single(followUp.Writes);
        Assert.Equal(Describe(file.Content with { BasedOn = keptAs }), Describe(write.Content!));
        Assert.NotEqual("My copy", write.Content!.Name);
        Assert.Equal([new TermValues("ga", "general availability")], write.Content!.Rows.Select(row => row.Values));
        Assert.False(followUp.LocalStateChanged);
        Assert.DoesNotContain(copy, followUp.LocalState.EnabledIds);
        Assert.Equal(AiOf(saved.LocalState, copy), AiOf(followUp.LocalState, copy));

        var repaired = Apply(saved, followUp);
        workspace.MarkSaved(followUp.DraftRevision, repaired);

        // Everything else the user changed in the copy is still theirs, and unsaved.
        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.Equal(Describe(mine), Describe(workspace.Draft.Find(copy)!.Content));
        Assert.Contains(copy, workspace.Draft.LocalState.EnabledIds);
        Assert.Equal(ai, workspace.Draft.LocalState.AiPermissions[copy]);
        Assert.Equal([copy], workspace.UnsavedLibraryIds);
        var next = Capture(workspace);
        AssertPreImages(repaired, next);
        var userWrite = Assert.Single(next.Writes);
        Assert.Equal(repaired.Find(copy)!.ContentHash, userWrite.ExpectedPreImage);
        Assert.Equal(Describe(mine), Describe(userWrite.Content!));
        Assert.Contains(copy, next.LocalState.EnabledIds);
        Assert.Equal(ai, next.LocalState.AiPermissions[copy]);
    }

    [Fact]
    public void D4_a_repairs_capture_and_a_full_capture_taken_at_one_revision_each_mark_their_own_save_saved()
    {
        // MarkSaved finds a capture by its revision, so the two kinds never share one: whichever is taken second moves the
        // revision on, so marking the follow-up saved never counts the whole draft as saved, nor the other way round.
        foreach (var (repairsFirst, saveRepairs) in new[] { (true, true), (true, false), (false, true), (false, false) })
        {
            var catalog = Standard();
            var workspace = PendingRepair(catalog, out var copy, out _, out var saved, (w, _) =>
                w.EditTerm("team-terms", RowIdOf(w, "team-terms", "kube"), new TermValues("kube", "K8s")));
            LibraryChangeSet repairs;
            LibraryChangeSet full;
            if (repairsFirst)
            {
                repairs = RepairsOf(workspace);
                full = Capture(workspace);
            }
            else
            {
                full = Capture(workspace);
                repairs = RepairsOf(workspace);
            }

            Assert.NotEqual(repairs.DraftRevision, full.DraftRevision);
            Assert.Equal([copy], repairs.Writes.Select(write => write.LibraryId));
            Assert.Equal(Sorted([copy, "team-terms"]), Sorted(full.Writes.Select(write => write.LibraryId)));

            var chosen = saveRepairs ? repairs : full;
            var committed = Apply(saved, chosen);
            workspace.MarkSaved(chosen.DraftRevision, committed);
            Assert.False(workspace.HasPendingReferenceRepairs);
            Assert.Equal(new TermValues("kube", "K8s"), ValuesOf(workspace, "team-terms")[0]);
            Assert.Equal(saveRepairs ? new[] { "team-terms" } : Array.Empty<string>(), workspace.UnsavedLibraryIds);
            Assert.Equal(saveRepairs ? "Kubernetes" : "K8s", committed.Find("team-terms")!.Content.Rows[0].Values.Written);
        }
    }

    [Fact]
    public void D4_a_repairs_capture_is_empty_with_nothing_pending_and_refused_while_a_copys_file_cannot_be_written()
    {
        // Nothing pending: an empty change set, whatever else is unsaved.
        var catalog = Standard();
        var plain = Workspace(catalog);
        plain.EditTerm("team-terms", RowIdOf(plain, "team-terms", "kube"), new TermValues("kube", "K8s"));
        Assert.True(RepairsOf(plain).IsEmpty);
        Assert.True(plain.HasUnsavedChanges);

        // Read-only (libraries changed by a newer Scribe): empty too, as every capture is.
        var readOnly = Workspace(Catalog(catalog.Libraries, [GitHubId], health: LocalStateHealth.Newer));
        Assert.True(readOnly.IsReadOnly);
        Assert.True(RepairsOf(readOnly).IsEmpty);

        // A repair pending on a copy whose file another app holds: the repairs capture refuses as the full one does, nothing
        // is written, and the repair stays pending.
        var workspace = PendingRepair(catalog, out var copy, out var keptAs, out var saved);
        var held = WithFileState(saved, copy, LibraryFileState.AwaitingRelease);
        workspace.Rebase(held);
        Assert.Equal([copy], workspace.PendingReferenceRepairs);
        var refused = workspace.CaptureReferenceRepairs();
        Assert.Null(refused.ChangeSet);
        var issue = Assert.Single(refused.Issues);
        Assert.Equal((copy, LibraryValidationKind.ContentNotSaveable), (issue.LibraryId, issue.Kind));
        Assert.Contains(workspace.CaptureChangeSet().Issues, full => full.LibraryId == copy && full.Kind == LibraryValidationKind.ContentNotSaveable);
        Assert.True(workspace.HasPendingReferenceRepairs);

        // Once the file is back, the follow-up writes the repair.
        var back = WithFileState(held, copy, LibraryFileState.Available);
        workspace.Rebase(back);
        var followUp = RepairsOf(workspace);
        Assert.Equal(keptAs, Assert.Single(followUp.Writes).Content!.BasedOn);
        workspace.MarkSaved(followUp.DraftRevision, Apply(back, followUp));
        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.False(workspace.HasUnsavedChanges);
    }

    [Fact]
    public void D4_a_repair_with_no_kept_original_is_pending_and_the_follow_up_writes_only_it()
    {
        // The rarer source of a repair, which keying the follow-up on HasPendingReferenceRepairs covers: an original
        // restored while a Save runs takes its id back, the store keeps an outside version under that same id, so the
        // restored original moves to a fresh id and the saved copy's reference follows it there.
        var entry = Deleted("20260901T100000Z.orig.csv", "orig", "Original", new TermValues("ga", "general availability"));
        var catalog = Catalog(
            [
                BuiltIn(GitHubId),
                Custom("orig-copy", "Original - Copy", [new TermValues("ga", "general availability")], basedOn: "orig"),
                Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes")]),
            ],
            [GitHubId, "team-terms"],
            recentlyDeleted: [entry.Entry]);
        var store = new Dictionary<string, RecentlyDeletedContent>(StringComparer.OrdinalIgnoreCase) { [entry.Entry.EntryName] = entry };
        var workspace = Workspace(catalog);
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "K8s"));
        var changes = Capture(workspace);
        Assert.Equal("orig", workspace.RestoreDeleted(entry));
        var saved = Apply(catalog, changes, store, [new StoreOutcome.OutsideVersion("team-terms", "orig", [new TermValues("outside", "Outside")])]);
        workspace.MarkSaved(changes.DraftRevision, saved);

        var movedTo = workspace.Draft.Libraries.Single(library => library.Content.Name == "Original").Content.Id;
        Assert.NotEqual("orig", movedTo);
        Assert.Equal(movedTo, workspace.Draft.Find("orig-copy")!.Content.BasedOn);
        Assert.Equal(["orig-copy"], workspace.PendingReferenceRepairs);
        Assert.Equal(movedTo, workspace.CopyOriginal("orig-copy"));

        // The follow-up writes the copy's line alone; the restore stays the user's to save.
        var followUp = RepairsOf(workspace);
        var write = Assert.Single(followUp.Writes);
        Assert.Equal(Describe(saved.Find("orig-copy")!.Content with { BasedOn = movedTo }), Describe(write.Content!));
        Assert.Empty(followUp.RecentlyDeletedActions);
        var repaired = Apply(saved, followUp, store);
        workspace.MarkSaved(followUp.DraftRevision, repaired);
        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.Equal([movedTo], workspace.UnsavedLibraryIds);

        // The user's Save restores the original under the id the copy now names.
        var next = Capture(workspace);
        Assert.Contains(next.RecentlyDeletedActions, action => action.Kind == RecentlyDeletedActionKind.Restore && action.RestoreAsId == movedTo);
        workspace.MarkSaved(next.DraftRevision, Apply(repaired, next, store));
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Equal(movedTo, workspace.CopyOriginal("orig-copy"));
    }

    // The catalog with one library's file in another state, one generation on.
    private static LibraryCatalog WithFileState(LibraryCatalog catalog, string id, LibraryFileState state) =>
        new(
            catalog.Generation + 1,
            catalog.Libraries.Select(library => library.Content.Id == id ? library with { State = state } : library).ToList(),
            catalog.LocalState,
            catalog.RecentlyDeleted.ToList(),
            catalog.RetiredBuiltInEdits.ToList(),
            filesAwaitingRelease: state == LibraryFileState.AwaitingRelease ? 1 : 0,
            catalog.KeptVersions.ToList());

    private static (bool, bool, bool) Enabled(LibraryWorkspace workspace, string first, string second, string third) =>
        (workspace.Draft.LocalState.EnabledIds.Contains(first),
         workspace.Draft.LocalState.EnabledIds.Contains(second),
         workspace.Draft.LocalState.EnabledIds.Contains(third));

    [Fact]
    public void Rebase_settles_a_restore_and_a_permanent_deletion_of_an_entry_whose_bytes_changed_outside()
    {
        var entry = Deleted("20260901T100000Z.old.csv", "old", "Old", new TermValues("old term", "Old term"));
        var other = Deleted("20260902T100000Z.other.csv", "other", "Other", new TermValues("other term", "Other term"));
        var catalog = Catalog([BuiltIn(GitHubId)], [GitHubId], recentlyDeleted: [entry.Entry, other.Entry]);
        var workspace = Workspace(catalog);
        var restored = workspace.RestoreDeleted(entry);
        workspace.DeletePermanently(other.Entry);

        // Both entries were rewritten outside Scribe meanwhile: the same names, other bytes.
        var newer = Catalog([BuiltIn(GitHubId)], [GitHubId], generation: catalog.Generation + 1,
            recentlyDeleted: [entry.Entry with { ContentHash = Hash("rewritten old") }, other.Entry with { ContentHash = Hash("rewritten other") }]);
        workspace.Rebase(newer);

        // The draft restores and deletes for good only the bytes it read: the restore becomes a new library with the
        // content the draft holds, and the permanent deletion is dropped.
        Assert.Equal(LibraryOrigin.Created, workspace.Draft.Find(restored)!.Origin);
        Assert.Equal([new TermValues("old term", "Old term")], ValuesOf(workspace, restored));
        var next = Capture(workspace);
        AssertPreImages(newer, next);
        Assert.Empty(next.RecentlyDeletedActions);
        Assert.Equal(2, workspace.Draft.RecentlyDeleted.Count);
    }

    private static string ImportNew(LibraryWorkspace workspace, LibraryCsvDocument document)
    {
        var before = workspace.Draft.Libraries.Select(library => library.Content.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(workspace.ApplyImport(
            LibraryImportPlanner.Plan(document, new LibraryImportTarget.NewLibrary(null), workspace.Draft), ImportConflictChoice.KeepMine).Applied);
        return workspace.Draft.Libraries.Single(library => !before.Contains(library.Content.Id)).Content.Id;
    }

    [Fact]
    public void Rebase_keeps_a_library_the_user_edited_that_the_newer_catalog_no_longer_has_as_an_unsaved_recreation()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "K8s"));

        // Its file was deleted outside Scribe meanwhile.
        var newer = Catalog([BuiltIn(GitHubId), BuiltIn(AzureId)], [GitHubId], generation: catalog.Generation + 1);
        workspace.Rebase(newer);

        var library = workspace.Draft.Find("team-terms");
        Assert.NotNull(library);
        Assert.Equal((LibraryOrigin.Created, true), (library.Origin, library.Unsaved));
        Assert.Equal([new TermValues("kube", "K8s"), new TermValues("get hub", "GitHub Enterprise")], ValuesOf(workspace, "team-terms"));
        var write = Assert.Single(Capture(workspace).Writes);
        Assert.Equal(("team-terms", (LibraryContentHash?)null), (write.LibraryId, write.ExpectedPreImage));

        // A library the user did not edit simply goes with its file.
        var untouched = Workspace(catalog);
        untouched.Rebase(newer);
        Assert.Null(untouched.Draft.Find("team-terms"));
        Assert.False(untouched.HasUnsavedChanges);
    }

    [Fact]
    public void Rebase_keeps_each_custom_row_its_identity_so_disjoint_edits_merge_without_a_second_copy()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var kube = RowIdOf(workspace, "team-terms", "kube");
        var getHub = RowIdOf(workspace, "team-terms", "get hub");
        workspace.EditTerm("team-terms", kube, new TermValues("kube", "K8s"));

        // Another commit changed only "get hub" and added "helm" after it.
        var newer = Catalog(
            [
                BuiltIn(GitHubId), BuiltIn(AzureId),
                Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes"), new TermValues("get hub", "GH"), new TermValues("helm", "Helm")]),
            ],
            [GitHubId, "team-terms"], ai: [new("team-terms", true)], generation: catalog.Generation + 1);
        workspace.Rebase(newer);

        Assert.Equal([new TermValues("kube", "K8s"), new TermValues("get hub", "GH"), new TermValues("helm", "Helm")], ValuesOf(workspace, "team-terms"));
        Assert.Equal((kube, getHub), (RowIdOf(workspace, "team-terms", "kube"), RowIdOf(workspace, "team-terms", "get hub")));
        var write = Assert.Single(Capture(workspace).Writes);
        Assert.Equal(ValuesOf(workspace, "team-terms"), write.Content!.Rows.Select(row => row.Values));
    }

    [Fact]
    public void Rebase_takes_the_rows_a_newer_catalog_deleted_unless_the_user_edited_them()
    {
        var catalog = Catalog(
            [Custom("notes", "Notes", [new TermValues("vm", "VM"), new TermValues("kube", "Kubernetes"), new TermValues("helm", "Helm")])],
            ["notes"], ai: [new("notes", true)]);
        var workspace = Workspace(catalog);
        workspace.EditTerm("notes", RowIdOf(workspace, "notes", "vm"), new TermValues("vm", "virtual machine"));
        workspace.EditTerm("notes", RowIdOf(workspace, "notes", "helm"), new TermValues("helm", "Helm charts"));

        // Another commit deleted "kube" and "helm".
        var newer = Catalog([Custom("notes", "Notes", [new TermValues("vm", "VM")])], ["notes"], ai: [new("notes", true)], generation: catalog.Generation + 1);
        workspace.Rebase(newer);

        Assert.Equal([new TermValues("vm", "virtual machine"), new TermValues("helm", "Helm charts")], ValuesOf(workspace, "notes"));
        Assert.Empty(workspace.CaptureChangeSet().Issues);
    }

    [Fact]
    public void Rebase_treats_a_spoken_form_several_rows_could_be_as_a_conflict_the_save_names()
    {
        var catalog = Catalog([Custom("notes", "Notes", [new TermValues("kube", "Kubernetes")])], ["notes"], ai: [new("notes", true)]);
        var workspace = Workspace(catalog);
        var mine = RowIdOf(workspace, "notes", "kube");
        workspace.EditTerm("notes", mine, new TermValues("kube", "K8s"));

        // Another commit made the one "kube" row two: which of them is the user's row cannot be told.
        var newer = Catalog(
            [Custom("notes", "Notes", [new TermValues("kube", "Kube"), new TermValues("kube", "Kubernetes cluster")])],
            ["notes"], ai: [new("notes", true)], generation: catalog.Generation + 1);
        workspace.Rebase(newer);

        // Nothing is dropped or guessed: the user's row keeps its identity beside the newer rows, and the Save names the
        // repeat until the user settles it.
        Assert.Equal(
            [new TermValues("kube", "Kube"), new TermValues("kube", "Kubernetes cluster"), new TermValues("kube", "K8s")],
            ValuesOf(workspace, "notes"));
        Assert.Equal(mine, workspace.RowsOf("notes").Single(row => row.Row.Values.Written == "K8s").RowId);
        var issues = workspace.CaptureChangeSet().Issues;
        Assert.Contains(issues, issue => issue.Kind == LibraryValidationKind.DuplicateSpoken && issue.RowId == mine);
    }

    [Fact]
    public void Rebase_takes_a_newer_order_of_rows_the_user_left_alone_and_nothing_reads_as_unsaved()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.SetEnabled(AzureId, true);

        // Another commit only reordered "Team terms".
        var reordered = Custom("team-terms", "Team terms", [new TermValues("get hub", "GitHub Enterprise"), new TermValues("kube", "Kubernetes")]);
        var newer = Catalog([BuiltIn(GitHubId), BuiltIn(AzureId), reordered], [GitHubId, "team-terms"], ai: [new("team-terms", true)], generation: catalog.Generation + 1);
        workspace.Rebase(newer);

        Assert.Equal([new TermValues("get hub", "GitHub Enterprise"), new TermValues("kube", "Kubernetes")], ValuesOf(workspace, "team-terms"));
        Assert.Equal([AzureId], workspace.UnsavedLibraryIds);
    }

    [Fact]
    public void Rebase_merges_each_metadata_field_on_its_own()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        Assert.True(workspace.SetDetails("team-terms", "Work", null).Applied);

        // Another commit renamed the library meanwhile.
        var renamed = Custom("team-terms", "Platform terms", [new TermValues("kube", "Kubernetes"), new TermValues("get hub", "GitHub Enterprise")]);
        var newer = Catalog([BuiltIn(GitHubId), BuiltIn(AzureId), renamed], [GitHubId, "team-terms"], ai: [new("team-terms", true)], generation: catalog.Generation + 1);
        workspace.Rebase(newer);

        var content = workspace.Draft.Find("team-terms")!.Content;
        Assert.Equal(("Platform terms", "Work"), (content.Name, content.Category));
        var write = Assert.Single(Capture(workspace).Writes);
        Assert.Equal(("Platform terms", "Work"), (write.Content!.Name, write.Content.Category));
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
