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
            retired: [new RetiredBuiltInEdits("data-and-ai", [new TermValues("llm", "LLM")], Hash("retired"))],
            accepted: [new("data-and-ai", Hash("retired"))]);
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

    [Theory]
    [InlineData("matching", null, true)]
    [InlineData("missing", null, false)]
    [InlineData("mismatched", null, false)]
    [InlineData("mismatched", true, false)]
    [InlineData("matching", false, false)]
    public void A_retired_built_in_passes_its_permission_on_only_for_the_document_its_state_accepted(
        string accepted, bool? chosen, bool inherited)
    {
        // The retired built-in's edits document is present, so its content is that document's, never a built-in's
        // shipped rows: a document the state never accepted permits nothing, whatever was chosen for the id (A4).
        var document = Hash("retired edits");
        var asked = new List<bool?>();
        bool Decision(LibraryOrigin origin, bool builtIn, bool? source)
        {
            if (origin == LibraryOrigin.RetiredBuiltIn)
            {
                asked.Add(source);
            }

            return DefaultAi(origin, builtIn, source);
        }

        KeyValuePair<string, LibraryContentHash>[] acceptedContent = accepted switch
        {
            "matching" => [new("data-and-ai", document)],
            "mismatched" => [new("data-and-ai", Hash("an earlier document"))],
            _ => [],
        };
        KeyValuePair<string, bool>[] choices = chosen is { } choice ? [new("data-and-ai", choice)] : [];
        var catalog = Catalog(
            [BuiltIn(GitHubId)],
            [GitHubId, "data-and-ai"],
            ai: choices,
            retired: [new RetiredBuiltInEdits("data-and-ai", [new TermValues("llm", "LLM")], document)],
            accepted: acceptedContent);
        var workspace = new LibraryWorkspace(catalog, TestOverlay, Decision, ShippedLibrary);

        var kept = workspace.KeepRetiredBuiltIn("data-and-ai");

        Assert.Equal([inherited], asked);
        Assert.Equal(inherited, workspace.Draft.LocalState.AiPermissions[kept]);
        Assert.Equal(inherited, Capture(workspace).LocalState.AiPermissions[kept]);
    }

    [Fact]
    public void A_custom_library_with_no_recorded_choice_shows_what_decision_2_gives_a_discovered_file()
    {
        // A Decision 2 that permits discovered files (a veto of the plan's default): the workspace follows the delegate,
        // as the policy does for an unrecorded custom file, and a copy inherits what it gives.
        var asked = new List<(LibraryOrigin Origin, bool BuiltIn, bool? Source)>();
        bool Permissive(LibraryOrigin origin, bool builtIn, bool? source)
        {
            asked.Add((origin, builtIn, source));
            return origin == LibraryOrigin.Discovered || DefaultAi(origin, builtIn, source);
        }

        var catalog = Catalog(
            [BuiltIn(GitHubId), Custom("unrecorded", "Unrecorded", [new TermValues("kube", "Kubernetes")])],
            [GitHubId, "unrecorded"]);
        var permissive = new LibraryWorkspace(catalog, TestOverlay, Permissive, ShippedLibrary);
        var copy = permissive.Duplicate("unrecorded");
        Assert.Contains((LibraryOrigin.Discovered, false, (bool?)null), asked);
        Assert.Contains((LibraryOrigin.Duplicated, false, (bool?)true), asked);
        Assert.True(permissive.Draft.LocalState.AiPermissions[copy]);

        // One that keeps even the built-ins off: nothing is shown on, so nothing is inherited on.
        static bool Strict(LibraryOrigin origin, bool builtIn, bool? source) => origin == LibraryOrigin.Duplicated && (source ?? false);
        var strict = new LibraryWorkspace(catalog, TestOverlay, Strict, ShippedLibrary);
        var builtInCopy = strict.Duplicate(GitHubId);
        var customCopy = strict.Duplicate("unrecorded");
        Assert.False(strict.Draft.LocalState.AiPermissions[builtInCopy]);
        Assert.False(strict.Draft.LocalState.AiPermissions[customCopy]);
    }

    [Fact]
    public void Each_box_shows_the_workspaces_mirror_of_the_policy_for_the_draft()
    {
        var catalog = Catalog(
            [
                BuiltIn(GitHubId), BuiltIn(AzureId),
                Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes")]),
                Custom("unrecorded", "Unrecorded", [new TermValues("vm", "VM")]),
                Custom("replaced", "Replaced", [new TermValues("x", "X")]),
            ],
            [GitHubId, "team-terms"],
            ai: [new("team-terms", true), new(AzureId, false), new("replaced", true)],
            accepted: [new("replaced", Hash("the bytes this version wrote"))]);
        var workspace = Workspace(catalog);

        Assert.True(workspace.ShowsAiPermission(GitHubId));
        Assert.False(workspace.ShowsAiPermission(AzureId));
        Assert.True(workspace.ShowsAiPermission("team-terms"));
        Assert.False(workspace.ShowsAiPermission("unrecorded"));

        // Replaced outside Scribe, the file's choice no longer applies to it; once the draft rewrites it, its Save
        // records the new content and the choice shows again.
        Assert.False(workspace.ShowsAiPermission("replaced"));
        workspace.EditTerm("replaced", RowIdOf(workspace, "replaced", "x"), new TermValues("x", "Ex"));
        Assert.True(workspace.ShowsAiPermission("replaced"));

        workspace.SetAiPermission("unrecorded", true);
        Assert.True(workspace.ShowsAiPermission("unrecorded"));

        // A lost state shows unchecked whatever nothing chose again.
        var lost = Workspace(Catalog([BuiltIn(GitHubId), Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes")])],
            [GitHubId], ai: [new("team-terms", true)], lost: true));
        Assert.False(lost.ShowsAiPermission(GitHubId));
        Assert.True(lost.ShowsAiPermission("team-terms"));
    }

    [Fact]
    public void A_built_in_whose_edits_document_vanished_outside_scribe_shows_no_permission_until_the_draft_writes_it()
    {
        // The catalog holds no document while the state still accepts one: it disappeared outside Scribe (review finding A5
        // on the composition stream), so nothing permits the built-in, not even the default, and a copy inherits nothing.
        var catalog = Catalog([BuiltIn(GitHubId), BuiltIn(AzureId)], [GitHubId, AzureId],
            accepted: [new(GitHubId, Hash("the document that vanished"))]);
        var workspace = Workspace(catalog);

        Assert.False(workspace.ShowsAiPermission(GitHubId));
        Assert.True(workspace.ShowsAiPermission(AzureId));
        var copy = workspace.Duplicate(GitHubId);
        Assert.False(workspace.Draft.LocalState.AiPermissions[copy]);

        // Once the draft writes the built-in's document, that Save records its hash, and the kind default shows again.
        workspace.EditTerm(GitHubId, RowIdOf(workspace, GitHubId, "copilot"), new TermValues("copilot", "GitHub Copilot"));
        Assert.True(WritesContent(workspace, GitHubId));
        Assert.True(workspace.ShowsAiPermission(GitHubId));
    }

    [Fact]
    public void Edited_content_the_save_cannot_write_keeps_the_committed_content_check()
    {
        // The draft edited "Team terms"; then a newer catalog found its file partly readable, holding bytes the state never
        // accepted. The Save cannot write the edit, so the box is judged as the composition after that Save will judge it.
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "K8s"));
        Assert.True(workspace.ShowsAiPermission("team-terms"));

        var partial = Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes")], state: LibraryFileState.PartlyReadable);
        workspace.Rebase(Catalog(
            [BuiltIn(GitHubId), BuiltIn(AzureId), partial], [GitHubId, "team-terms"], ai: [new("team-terms", true)],
            generation: catalog.Generation + 1, accepted: [new("team-terms", Hash("the bytes this version wrote"))]));

        Assert.False(WritesContent(workspace, "team-terms"));
        Assert.False(workspace.ShowsAiPermission("team-terms"));
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
