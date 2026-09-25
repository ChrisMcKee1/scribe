using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-5: undo and redo of the structural operations (delete, turn off or on, restore built-in values, removal intent,
/// import into a library, use this copy instead, the cleanup's switch-offs) until Save or Cancel; view state never
/// marks anything unsaved.
/// </summary>
public sealed class LibraryWorkspaceUndoTests
{
    [Fact]
    public void Undo_puts_a_deleted_term_back_in_its_place_and_redo_deletes_it_again()
    {
        var workspace = Workspace(Standard());
        var before = workspace.RowsOf("team-terms").ToList();

        workspace.DeleteTerm("team-terms", before[0].RowId);
        Assert.Equal("Delete term", workspace.UndoLabel);
        Assert.True(workspace.CanUndo);
        Assert.False(workspace.CanRedo);

        workspace.Undo();
        Assert.Equal(before, workspace.RowsOf("team-terms"));
        Assert.False(workspace.HasUnsavedChanges);
        Assert.False(workspace.CanUndo);
        Assert.True(workspace.CanRedo);

        workspace.Redo();
        Assert.Equal(before.Skip(1), workspace.RowsOf("team-terms"));
        Assert.True(workspace.CanUndo);
    }

    [Fact]
    public void Undo_restores_only_what_the_operation_changed_and_nothing_changed_since()
    {
        var workspace = Workspace(Standard());
        var kube = RowIdOf(workspace, "team-terms", "kube");
        var getHub = RowIdOf(workspace, "team-terms", "get hub");

        workspace.DeleteTerm("team-terms", kube);
        workspace.EditTerm("team-terms", getHub, new TermValues("get hub", "GH"));
        workspace.AddTerm("team-terms", new TermValues("vm", "VM"));
        workspace.Undo();

        Assert.Equal(
            [new TermValues("kube", "Kubernetes"), new TermValues("get hub", "GH"), new TermValues("vm", "VM")],
            ValuesOf(workspace, "team-terms"));

        // A term edited after it was turned off keeps its edit: the turn-off cannot be undone over it, so nothing is
        // offered for it any more.
        var fresh = Workspace(Standard());
        var row = RowIdOf(fresh, "team-terms", "kube");
        fresh.SetTermEnabled("team-terms", row, false);
        fresh.EditTerm("team-terms", row, new TermValues("kube", "K8s", Enabled: false));
        Assert.False(fresh.CanUndo);
        Assert.Null(fresh.UndoLabel);
        fresh.Undo();
        Assert.Equal(new TermValues("kube", "K8s", Enabled: false), ValuesOf(fresh, "team-terms")[0]);
    }

    [Fact]
    public void Each_structural_operation_undoes_back_to_the_committed_draft()
    {
        var entry = Deleted("20260901T100000Z.gone.csv", "gone", "Gone", new TermValues("x", "X"));
        var catalog = Catalog(
            [
                BuiltIn(GitHubId),
                BuiltIn(AzureId),
                Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes"), new TermValues("copilot", "Team Copilot")]),
                Custom("team-terms-copy", "Team terms - Copy", [new TermValues("kube", "K8s")], basedOn: "team-terms"),
            ],
            [GitHubId, AzureId, "team-terms"],
            ai: [new("team-terms", true), new("team-terms-copy", true)],
            recentlyDeleted: [entry.Entry]);
        var operations = new (string Label, Action<LibraryWorkspace> Run)[]
        {
            ("Delete term", workspace => workspace.DeleteTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"))),
            ("Delete library", workspace => workspace.DeleteLibrary("team-terms")),
            ("Turn off library", workspace => workspace.SetEnabled(GitHubId, false)),
            ("Turn on library", workspace => workspace.SetEnabled("team-terms-copy", true)),
            ("Turn off term", workspace => workspace.SetTermEnabled(GitHubId, RowIdOf(workspace, GitHubId, "copilot"), false)),
            ("Turn off term", workspace => workspace.SetTermEnabled("team-terms", RowIdOf(workspace, "team-terms", "kube"), false)),
            ("Use this copy instead", workspace => workspace.UseCopyInstead("team-terms-copy")),
            ("Turn off in other libraries", workspace =>
                workspace.TurnOffInOtherLibraries("team-terms", RowIdOf(workspace, "team-terms", "copilot"), [GitHubId, AzureId])),
            ("Import terms", workspace => workspace.ApplyImport(
                LibraryImportPlanner.Plan(
                    Document(null, new TermValues("kube", "K8s"), new TermValues("aks", "AKS")),
                    new LibraryImportTarget.ExistingLibrary("team-terms"),
                    workspace.Draft),
                ImportConflictChoice.UseFilesVersion)),
            ("Import terms", workspace => workspace.ApplyImport(
                LibraryImportPlanner.Plan(Document("Imported", new TermValues("a", "A")), new LibraryImportTarget.NewLibrary(null), workspace.Draft),
                ImportConflictChoice.KeepMine)),
            ("Use as a removal rule", workspace =>
            {
                workspace.AddTerm("team-terms", new TermValues("um", ""));
                workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "um"), new TermValues("um", ""), removalIntent: true);
            }),
        };

        foreach (var (label, run) in operations)
        {
            var workspace = Workspace(catalog);
            var draft = Snapshot(workspace);
            run(workspace);
            Assert.Equal(label, workspace.UndoLabel);
            Assert.NotEqual(draft, Snapshot(workspace));
            var after = Snapshot(workspace);

            workspace.Undo();
            if (label == "Use as a removal rule")
            {
                // The row it was set on is typed text, which undo leaves; only the intent goes.
                Assert.Contains(workspace.CaptureChangeSet().Issues, issue => issue.Kind == LibraryValidationKind.EmptyWrittenWithoutIntent);
                continue;
            }

            Assert.Equal(draft, Snapshot(workspace));
            Assert.False(workspace.HasUnsavedChanges);
            workspace.Redo();
            Assert.Equal(after, Snapshot(workspace));
        }
    }

    [Fact]
    public void Restore_built_in_values_and_restore_all_are_undoable()
    {
        var edits = new BuiltInLibraryEdits(GitHubId,
        [
            new BuiltInTermEdit(LibraryTermKey.From("copilot"), BuiltInTermIntent.Edited,
                new TermValues("copilot", "Copilot"), new TermValues("copilot", "GitHub Copilot")),
        ]);
        var catalog = Catalog([BuiltIn(GitHubId, edits)], [GitHubId]);
        var workspace = Workspace(catalog);
        var committed = Snapshot(workspace);

        workspace.RestoreBuiltInValues(GitHubId, RowIdOf(workspace, GitHubId, "copilot"));
        Assert.Equal("Restore built-in values", workspace.UndoLabel);
        Assert.Equal(TermOrigin.Shipped, workspace.Draft.Find(GitHubId)!.Content.Rows[1].Origin);
        workspace.Undo();
        Assert.Equal(committed, Snapshot(workspace));

        workspace.RestoreAllBuiltInValues(GitHubId);
        Assert.Equal("Restore all built-in values", workspace.UndoLabel);
        Assert.Null(Assert.Single(Capture(workspace).Writes).Edits);
        workspace.Undo();
        Assert.Equal(committed, Snapshot(workspace));
        Assert.True(Capture(workspace).IsEmpty);
    }

    [Fact]
    public void A_new_edit_ends_the_redo_history_and_a_save_or_reload_ends_both()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.SetEnabled(AzureId, true);
        workspace.Undo();
        Assert.True(workspace.CanRedo);

        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "K8s"));
        Assert.False(workspace.CanRedo);

        workspace.SetEnabled(AzureId, true);
        var changes = Capture(workspace);
        workspace.MarkSaved(changes.DraftRevision, Apply(catalog, changes));
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.CanRedo);

        workspace.SetEnabled(AzureId, false);
        workspace.Reload(catalog);
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void View_state_never_marks_anything_unsaved_or_moves_the_revision()
    {
        var workspace = Workspace(Standard());
        var revision = workspace.Revision;

        _ = workspace.Draft;
        _ = workspace.RowsOf("team-terms");
        _ = LibraryTermSort.For(System.Globalization.CultureInfo.InvariantCulture).Sort(workspace.RowsOf("team-terms"), LibraryTermSortOrder.WrittenDescending);
        _ = LibrarySearch.For(System.Globalization.CultureInfo.InvariantCulture).Search(workspace, "kube");
        _ = workspace.CanUndo;
        _ = workspace.UnsavedLibraryIds;
        workspace.SetEnabled("team-terms", true);
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "Kubernetes"));

        Assert.Equal(revision, workspace.Revision);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.False(workspace.CanUndo);
    }

    private static string Snapshot(LibraryWorkspace workspace)
    {
        var draft = workspace.Draft;
        var lines = draft.Libraries.Select(library =>
            $"{library.Content.Id}|{library.Content.Name}|{library.PendingDelete}|{library.Origin}|"
            + string.Join(";", library.Content.Rows.Select(row => $"{row.Values}:{row.Origin}")));
        return string.Join("\n", lines)
            + "\nenabled=" + string.Join(",", draft.LocalState.EnabledIds.Order(StringComparer.OrdinalIgnoreCase))
            + "\nai=" + string.Join(",", draft.LocalState.AiPermissions.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"))
            + "\ndeleted=" + string.Join(",", draft.RecentlyDeleted.Select(entry => entry.EntryName));
    }
}
