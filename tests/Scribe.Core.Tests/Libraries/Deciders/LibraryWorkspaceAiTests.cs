using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-14 and Decision 2 through the workspace: a lost state's denial rides every capture until Use these choices records
/// an explicit choice for every library, and each way a library enters the draft takes its AI permission from the
/// Decision 2 delegate, never from a rule of the workspace's own.
/// </summary>
public sealed class LibraryWorkspaceAiTests
{
    [Fact]
    public void D14_with_lost_permissions_every_box_starts_unchecked_captures_keep_the_flag_and_only_confirming_clears_it()
    {
        var catalog = Catalog(
            [BuiltIn(GitHubId), BuiltIn(AzureId), Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes")])],
            [GitHubId, "team-terms"],
            lost: true);
        var workspace = Workspace(catalog);

        // Nothing is chosen, and the loss denies what nothing chose (AiVocabularyPolicy's rule for a lost state).
        Assert.True(workspace.Draft.LocalState.AiPermissionsLost);
        Assert.Empty(workspace.Draft.LocalState.AiPermissions);
        Assert.False(workspace.HasUnsavedChanges);

        // A copy made now inherits what its original shows: unchecked.
        var copy = workspace.Duplicate(GitHubId);
        Assert.False(workspace.Draft.LocalState.AiPermissions[copy]);

        workspace.SetAiPermission("team-terms", true);
        var changes = Capture(workspace);
        Assert.True(changes.LocalState.AiPermissionsLost);
        Assert.True(changes.LocalState.AiPermissions["team-terms"]);
        Assert.False(changes.LocalState.AiPermissions.ContainsKey(GitHubId));

        workspace.ConfirmAiPermissions();
        var confirmed = Capture(workspace);
        Assert.False(confirmed.LocalState.AiPermissionsLost);
        Assert.True(confirmed.LocalStateChanged);
        Assert.Equal(
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [GitHubId] = false,
                [AzureId] = false,
                ["team-terms"] = true,
                [copy] = false,
            },
            confirmed.LocalState.AiPermissions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

        // Confirming again does nothing: the loss is already cleared.
        var revision = workspace.Revision;
        workspace.ConfirmAiPermissions();
        Assert.Equal(revision, workspace.Revision);
    }

    [Fact]
    public void D14_an_unreadable_state_is_a_loss_the_next_save_commits_without_being_an_unsaved_change_itself()
    {
        var catalog = Catalog(
            [BuiltIn(GitHubId), Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes")])],
            [GitHubId],
            health: LocalStateHealth.Unreadable);
        var workspace = Workspace(catalog);

        Assert.True(workspace.Draft.LocalState.AiPermissionsLost);
        Assert.Equal(LocalStateHealth.Ok, workspace.Draft.LocalState.Health);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.True(Capture(workspace).IsEmpty);

        workspace.SetEnabled("team-terms", true);
        var changes = Capture(workspace);
        Assert.True(changes.LocalState.AiPermissionsLost);
        Assert.Equal(LocalStateHealth.Ok, changes.LocalState.Health);
    }

    [Fact]
    public void New_libraries_take_their_permission_from_decision_2()
    {
        var asked = new List<(LibraryOrigin Origin, bool BuiltIn, bool? Source)>();
        bool Decision(LibraryOrigin origin, bool builtIn, bool? source)
        {
            asked.Add((origin, builtIn, source));
            return DefaultAi(origin, builtIn, source);
        }

        var entry = Deleted("20260901T100000Z.gone.csv", "gone", "Gone", new TermValues("x", "X"));
        var catalog = Catalog(
            [BuiltIn(GitHubId), Custom("private", "Private", [new TermValues("kube", "Kubernetes")])],
            [GitHubId, "private"],
            ai: [new("private", false)],
            recentlyDeleted: [entry.Entry],
            retired: [new RetiredBuiltInEdits("data-and-ai", [new TermValues("llm", "LLM")], Hash("retired"))]);
        var workspace = new LibraryWorkspace(catalog, TestOverlay, Decision, ShippedLibrary);

        var created = workspace.CreateLibrary();
        var builtInCopy = workspace.Duplicate(GitHubId);
        var privateCopy = workspace.Duplicate("private");
        var restored = workspace.RestoreDeleted(entry);
        var kept = workspace.KeepRetiredBuiltIn("data-and-ai");
        workspace.ApplyImport(
            LibraryImportPlanner.Plan(Document("Imported", new TermValues("a", "A")), new LibraryImportTarget.NewLibrary(null), workspace.Draft),
            ImportConflictChoice.KeepMine);

        var ai = workspace.Draft.LocalState.AiPermissions;
        Assert.False(ai[created]);
        Assert.True(ai[builtInCopy]);
        Assert.False(ai[privateCopy]);
        Assert.False(ai[restored]);
        Assert.True(ai[kept]);
        Assert.Contains((LibraryOrigin.Created, false, (bool?)null), asked);
        Assert.Contains((LibraryOrigin.Duplicated, false, (bool?)true), asked);
        Assert.Contains((LibraryOrigin.Duplicated, false, (bool?)false), asked);
        Assert.Contains((LibraryOrigin.Restored, false, (bool?)null), asked);
        Assert.Contains((LibraryOrigin.RetiredBuiltIn, false, (bool?)true), asked);
        Assert.Contains((LibraryOrigin.Imported, false, (bool?)null), asked);

        // Enabled state: created on; duplicates, restores and imports off; the kept rows follow the retired built-in.
        var enabled = workspace.Draft.LocalState.EnabledIds;
        Assert.Contains(created, enabled);
        Assert.DoesNotContain(builtInCopy, enabled);
        Assert.DoesNotContain(restored, enabled);
        Assert.DoesNotContain(kept, enabled);
        Assert.Equal(kept, workspace.KeepRetiredBuiltIn("data-and-ai"));
    }

    [Fact]
    public void A_permission_choice_is_recorded_explicitly_and_toggling_back_to_the_committed_choice_is_no_change()
    {
        var workspace = Workspace(Standard());

        Assert.True(workspace.SetAiPermission("team-terms", false).Applied);
        Assert.Equal(["team-terms"], workspace.UnsavedLibraryIds);
        Assert.True(workspace.SetAiPermission("team-terms", true).Applied);
        Assert.False(workspace.HasUnsavedChanges);

        // Permission changes are not in the undo history; turning a library on or off is.
        Assert.False(workspace.CanUndo);
    }
}
