using Scribe.Core.Cleanup;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Storage;

namespace Scribe.Core.Tests.Libraries.Integration;

/// <summary>
/// The checks the reviews of C and D left for integration (contract 9.3), through the real D, O, C and J parts with
/// nothing faked: a preview is the committed composition its Save leaves, D's shown AI permission is C's policy on
/// committed inputs, a kept library carries the draft's choices to the id it is kept under, and held-back content never
/// moves the committed local state.
/// </summary>
public sealed class LibraryPreviewAfterRealSaveTests : IDisposable
{
    private static readonly GlossaryBudget Budget = new(CleanupPrompt.MaxGlossaryTermsCloud);

    private readonly RealLibraries _libraries = new();

    public void Dispose() => _libraries.Dispose();

    private LibraryStorageFixture Fixture => _libraries.Fixture;

    [Fact]
    public void Across_random_workspace_Saves_a_preview_equals_the_committed_composition_the_real_Save_leaves()
    {
        // The W1a fixture plus a built-in whose edits document holds only an Off intent for a term this version does not
        // ship: nothing on the page shows it, and Restore all built-in values clears it, which writes (removes) the
        // document; the Save drops its accepted hash in the same commit, so the preview must show the built-in permitted.
        foreach (var (fileName, csv) in LibraryFixture.CustomFiles)
        {
            Fixture.Write(fileName, csv);
        }

        var inert = new BuiltInLibraryEdits("github",
            [new BuiltInTermEdit(LibraryTermKey.From("retired hub term"), BuiltInTermIntent.Off, new TermValues("retired hub term", "Retired Hub"), null)]);
        Fixture.WriteBytes("edits/github.json", BuiltInLibraryOverlay.Instance.WriteEdits(inert));
        Fixture.SaveEnabled(LibraryFixture.Scenarios[0].EnabledIds);
        var dictionary = new DictionaryRepository(Fixture.Database);
        dictionary.AddRange(LibraryFixture.Personal);
        var service = _libraries.Service();
        var catalog = service.LoadCatalog();
        Assert.True(catalog.LocalState.AcceptedContent.ContainsKey("github"));
        Assert.DoesNotContain(catalog.Find("github")!.Content.Rows, row => row.Key == LibraryTermKey.From("retired hub term"));

        var random = new Random(20260926);
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var saves = 0;
        for (var round = 0; round < 48; round++)
        {
            var workspace = RealLibraries.Workspace(catalog);
            var actions = round == 0 ? InvisibleReset(workspace) : RandomActions(workspace, random, round);
            foreach (var action in actions)
            {
                kinds[action] = kinds.GetValueOrDefault(action) + 1;
            }

            var personal = dictionary.GetEnabled();
            var draft = workspace.Draft;
            var preview = LibraryComposition.Preview(draft, catalog, personal, Budget);
            AssertShownPermissionIsThePolicy(workspace, draft, catalog, preview, $"round {round} ({string.Join(", ", actions)})");

            var capture = workspace.CaptureChangeSet();
            Assert.Empty(capture.Issues);
            if (capture.ChangeSet is not { IsEmpty: false })
            {
                continue;
            }

            var saved = _libraries.Save(service, workspace);
            Assert.Equal(LibrarySaveStatus.Applied, saved.Outcome.Status);
            saves++;
            var after = LibraryComposition.Committed(saved.Committed, personal, Budget);
            var differences = Differences(preview, after, saved.Committed);
            Assert.True(differences.Count == 0, $"Round {round} ({string.Join(", ", actions)}):\n{string.Join("\n", differences)}");

            // What the service publishes for dictation and AI cleanup is that committed composition.
            Assert.Equal(after.LibraryEntries.Select(Entry), service.Current.Entries.Select(Entry));
            Assert.Equal(after.AiLibraryEntries.Select(Entry), service.Current.AiEntries.Select(Entry));
            if (round == 0)
            {
                Assert.False(Fixture.Exists("edits/github.json"));
                Assert.False(saved.Committed.LocalState.AcceptedContent.ContainsKey("github"));
                Assert.DoesNotContain("github", after.AiExcludedLibraryIds);
            }

            catalog = saved.Committed;
        }

        Assert.True(saves >= 30, $"only {saves} rounds saved anything");
        Assert.All(
            new[] { "invisible reset", "toggle", "ai", "edit custom", "add custom", "edit built-in", "turn off built-in row", "create", "delete", "rename" },
            kind => Assert.True(kinds.GetValueOrDefault(kind) >= 1, $"{kind}: {kinds.GetValueOrDefault(kind)} rounds"));
    }

    [Fact]
    public void A_library_saved_under_another_id_carries_the_drafts_choices_to_that_id_and_the_other_apps_file_starts_off()
    {
        Fixture.SaveEnabled("github");
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        var created = workspace.CreateLibrary();
        Assert.True(workspace.Rename(created, "Release notes").Applied);
        Assert.True(workspace.AddTerm(created, new TermValues("sprint", "Sprint")).Applied);
        Assert.True(workspace.SetAiPermission(created, true).Applied);
        workspace.SetEnabled(created, true);

        // Another app creates the planned file between the prepare and the completion (case C3, G3).
        var changes = workspace.CaptureChangeSet().ChangeSet!;
        var prepared = service.PrepareSave(changes);
        Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);
        Fixture.Write(created + ".csv", "# name: Theirs\npattern,replacement\ntheir term,Theirs\n");
        Fixture.Settings.SaveBundle(Fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        var outcome = service.CompleteSave(prepared.Save);
        var committed = service.LoadCatalog();
        workspace.MarkSaved(changes, committed);

        var kept = Assert.Single(outcome.KeptVersions);
        Assert.Equal(LibraryKeptVersionKind.SavedUnderNewId, kept.Kind);
        var keptId = kept.KeptAsId!;
        Assert.NotEqual(created, keptId);

        // J's journal and C's encoding: the draft's enabled state and AI choice are the kept id's, with its content's hash.
        var reloaded = _libraries.Restart();
        var catalog = reloaded.LoadCatalog();
        Assert.Contains(keptId, catalog.LocalState.EnabledIds);
        Assert.True(catalog.LocalState.AiPermissions[keptId]);
        Assert.Equal(catalog.Find(keptId)!.ContentHash, catalog.LocalState.AcceptedContent[keptId]);
        Assert.Contains(reloaded.Current.AiEntries, entry => entry.Replacement == "Sprint");

        // The other app's file at the planned id is discovered: off, and not sent.
        Assert.DoesNotContain(created, catalog.LocalState.EnabledIds);
        Assert.False(AiVocabularyPolicy.IsPermitted(catalog.LocalState, created, false, catalog.Find(created)!.ContentHash));
        Assert.DoesNotContain(reloaded.Current.Entries, entry => entry.Replacement == "Theirs");

        // D's MarkSaved followed the kept id: the draft holds the library there, with the choices it was saved with.
        Assert.True(workspace.ShowsAiPermission(keptId));
        Assert.Equal("Sprint", RealLibraries.Row(workspace, keptId, "sprint").Row.Values.Written);
        Assert.Contains(keptId, workspace.Draft.LocalState.EnabledIds);
        Assert.False(workspace.HasUnsavedChanges);
    }

    [Fact]
    public void An_outside_version_is_kept_off_and_not_sent_while_the_edited_library_keeps_the_drafts_choices()
    {
        Fixture.Write("team-terms.csv", "# name: Team terms\npattern,replacement\nkube,Kubernetes\n");
        Fixture.SaveEnabled("team-terms");
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        workspace.EditTerm("team-terms", RealLibraries.Row(workspace, "team-terms", "kube").RowId, new TermValues("kube", "K8s"));
        Assert.True(workspace.SetAiPermission("team-terms", true).Applied);

        // Another app saves the file between the prepare and the completion (case W4, G4).
        var changes = workspace.CaptureChangeSet().ChangeSet!;
        var prepared = service.PrepareSave(changes);
        Fixture.Write("team-terms.csv", "# name: Team terms\npattern,replacement\nkube,Kube from another app\nsecret,Other App Secret\n");
        Fixture.Settings.SaveBundle(Fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        var outcome = service.CompleteSave(prepared.Save);
        workspace.MarkSaved(changes, service.LoadCatalog());

        var kept = Assert.Single(outcome.KeptVersions);
        Assert.Equal(LibraryKeptVersionKind.OutsideVersion, kept.Kind);
        var reloaded = _libraries.Restart();
        var catalog = reloaded.LoadCatalog();
        Assert.Equal("K8s", catalog.Find("team-terms")!.Content.Rows.Single().Values.Written);
        Assert.Contains("team-terms", catalog.LocalState.EnabledIds);
        Assert.True(catalog.LocalState.AiPermissions["team-terms"]);
        var outside = catalog.Find(kept.KeptAsId!)!;
        Assert.Contains(outside.Content.Rows, row => row.Values.Written == "Other App Secret");
        Assert.DoesNotContain(kept.KeptAsId!, catalog.LocalState.EnabledIds);
        Assert.False(AiVocabularyPolicy.IsPermitted(catalog.LocalState, kept.KeptAsId!, false, outside.ContentHash));
        Assert.DoesNotContain(reloaded.Current.Entries, entry => entry.Replacement == "Other App Secret");
        Assert.False(workspace.HasUnsavedChanges);
    }

    [Fact]
    public void Choices_never_follow_a_kept_file_whose_content_changed_before_they_were_carried()
    {
        // The carry is bound to content (A4): while it waits (here its adoption cannot commit), another app changes the
        // kept file, and the choices made for Scribe's bytes must not reach bytes nobody chose: the kept file starts off.
        Fixture.SaveEnabled("github");
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        var created = workspace.CreateLibrary();
        Assert.True(workspace.Rename(created, "Release notes").Applied);
        Assert.True(workspace.AddTerm(created, new TermValues("sprint", "Sprint")).Applied);
        Assert.True(workspace.SetAiPermission(created, true).Applied);
        var prepared = service.PrepareSave(workspace.CaptureChangeSet().ChangeSet!);
        Fixture.Write(created + ".csv", "# name: Theirs\npattern,replacement\ntheir term,Theirs\n");
        Fixture.Settings.SaveBundle(Fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);
        var adoptions = true;
        Fixture.Settings.WriteStep = (name, _, _) =>
        {
            if (adoptions && name == "library state committing")
            {
                throw FaultingFileSystem.Injected();
            }
        };
        var outcome = service.CompleteSave(prepared.Save);
        var keptId = Assert.Single(outcome.KeptVersions).KeptAsId!;
        Assert.DoesNotContain(keptId, Fixture.Settings.Load().EnabledDictionaryLibraryIds, StringComparer.OrdinalIgnoreCase);

        Fixture.Write(keptId + ".csv", "# name: Changed\npattern,replacement\nprivate term,Changed Outside\n");
        adoptions = false;
        var catalog = service.LoadCatalog();

        Assert.DoesNotContain(keptId, catalog.LocalState.EnabledIds);
        Assert.False(AiVocabularyPolicy.IsPermitted(catalog.LocalState, keptId, false, catalog.Find(keptId)!.ContentHash));
        Assert.DoesNotContain(service.Current.Entries, entry => entry.Replacement == "Changed Outside");
        Assert.DoesNotContain(service.Current.AiEntries, entry => entry.Replacement is "Changed Outside" or "Theirs");
        Fixture.Settings.WriteStep = null;
    }

    [Theory]
    [InlineData("state lost")]
    [InlineData("state newer")]
    [InlineData("document vanished on defaults")]
    public void Ds_shown_permission_is_Cs_policy_on_the_edge_inputs_of_Cs_report(string edge)
    {
        // The inputs C's report lists as places D's box and C's policy could disagree (w1b-c.md, "revised for round 3"):
        // a lost state, a newer one (read-only), and a built-in with no document while an accepted entry remains, which
        // only a session on defaults, where no adoption runs, can hold.
        Fixture.Write("team-terms.csv", "# name: Team terms\npattern,replacement\nkube,Kubernetes\n");
        Fixture.SaveEnabled("team-terms", "github");
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        workspace.EditTerm("github", RealLibraries.Row(workspace, "github", "get hub").RowId, new TermValues("get hub", "GitHub Enterprise"));
        Assert.True(workspace.SetAiPermission("team-terms", true).Applied);
        _libraries.Save(service, workspace);

        switch (edge)
        {
            case "state lost":
                Fixture.Settings.Set(LibrarySettingKeys.State, "{ damaged");
                break;
            case "state newer":
                Fixture.Settings.Set(LibrarySettingKeys.State, "{\"version\":2}");
                break;
            default:
                File.Delete(Fixture.PathOf("edits/github.json"));
                Fixture.Context = new LibraryStateContext(RunningOnDefaults: true, DatabaseRepaired: false, GenerationStored: true);
                break;
        }

        var catalog = _libraries.Restart().LoadCatalog();
        var edgeWorkspace = RealLibraries.Workspace(catalog);
        var draft = edgeWorkspace.Draft;
        var preview = LibraryComposition.Preview(draft, catalog, [], Budget);
        AssertShownPermissionIsThePolicy(edgeWorkspace, draft, catalog, preview, edge);
        Assert.False(edgeWorkspace.ShowsAiPermission("github"), edge);
        Assert.Equal(edge == "document vanished on defaults", edgeWorkspace.ShowsAiPermission("team-terms"));

        switch (edge)
        {
            case "state lost":
                Assert.True(catalog.LocalState.AiPermissionsLost);
                break;
            case "state newer":
                Assert.True(edgeWorkspace.IsReadOnly);
                break;
            default:
                Assert.True(catalog.LocalState.AcceptedContent.ContainsKey("github"));
                Assert.Null(catalog.Find("github")!.ContentHash);
                Assert.Contains("github", preview.AiExcludedLibraryIds);
                break;
        }
    }

    [Theory]
    [InlineData(".redo")]
    [InlineData(".manifest.json")]
    public void Content_held_back_because_the_committed_Save_cannot_be_read_never_moves_the_committed_local_state(string unreadable)
    {
        // From J's round 3 (9.3): a Save committed and not yet installed, then a fresh process that cannot read its redo
        // image, or its manifest, right now: the libraries it cannot vouch for are held back, never taken for replaced or
        // discovered content, and nothing is committed; once the read succeeds they serve the committed content with
        // their choices intact.
        Fixture.Write("team-terms.csv", "# name: Team terms\npattern,replacement\nkube,Kubernetes\n");
        Fixture.Write("other.csv", "# name: Other\npattern,replacement\nnorth star,North Star\n");
        Fixture.SaveEnabled("team-terms", "other");
        var service = _libraries.Service();
        var workspace = RealLibraries.Workspace(service.LoadCatalog());
        workspace.EditTerm("team-terms", RealLibraries.Row(workspace, "team-terms", "kube").RowId, new TermValues("kube", "K8s"));
        Assert.True(workspace.SetAiPermission("other", false).Applied);
        var changes = workspace.CaptureChangeSet().ChangeSet!;
        var prepared = service.PrepareSave(changes);
        Fixture.Settings.SaveBundle(Fixture.Settings.Load(), null, null, default, prepared.Save!.Payload);

        // The process ends before completion. The next one cannot read that part of the journal.
        var stateBefore = Fixture.Row(LibrarySettingKeys.State);
        var generation = Fixture.StoredGeneration;
        var files = new FaultingFileSystem
        {
            ReadFault = path => path.Contains(Path.DirectorySeparatorChar + "journal" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                                path.EndsWith(unreadable, StringComparison.OrdinalIgnoreCase)
                ? FaultingFileSystem.SharingViolation()
                : null,
        };
        var fresh = _libraries.Restart(files);
        var held = fresh.LoadCatalog();
        var team = held.Find("team-terms")!;
        Assert.Equal(LibraryFileState.AwaitingRelease, team.State);
        Assert.Empty(team.Content.Rows);
        Assert.Null(team.ContentHash);
        Assert.DoesNotContain(fresh.Current.Entries, entry => entry.Pattern == "kube");

        // Held back, no library is read as replaced or discovered, in memory either: the choices are the committed ones.
        Assert.Equal(changes.LocalState.EnabledIds.Order(StringComparer.OrdinalIgnoreCase), held.LocalState.EnabledIds.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            changes.LocalState.AiPermissions.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase),
            held.LocalState.AiPermissions.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase));
        Assert.True(held.LocalState.AcceptedContent.ContainsKey("team-terms"));
        Assert.True(held.LocalState.AcceptedContent.ContainsKey("other"));
        fresh.Recover();
        fresh.LoadCatalog();
        Assert.Equal(stateBefore, Fixture.Row(LibrarySettingKeys.State));
        Assert.Equal(generation, Fixture.StoredGeneration);

        files.ReadFault = null;
        var settled = fresh.LoadCatalog();
        Assert.Equal(LibraryFileState.Available, settled.Find("team-terms")!.State);
        Assert.Equal("K8s", settled.Find("team-terms")!.Content.Rows.Single().Values.Written);
        Assert.Contains("team-terms", settled.LocalState.EnabledIds);
        Assert.False(settled.LocalState.AiPermissions["other"]);
        Assert.Equal(settled.Find("team-terms")!.ContentHash, settled.LocalState.AcceptedContent["team-terms"]);
        Assert.Contains(fresh.Current.Entries, entry => entry.Pattern == "kube" && entry.Replacement == "K8s");
        Assert.DoesNotContain("other", fresh.Current.AiScope.PermittedLibraryIds);
    }

    // Round 0: Restore all built-in values on the built-in whose document holds only the inert Off intent.
    private static List<string> InvisibleReset(LibraryWorkspace workspace)
    {
        workspace.RestoreAllBuiltInValues("github");
        Assert.True(workspace.Draft.Find("github")!.WritesContent);
        return ["invisible reset"];
    }

    private static List<string> RandomActions(LibraryWorkspace workspace, Random random, int round)
    {
        var actions = new List<string>();
        var count = random.Next(1, 4);
        for (var i = 0; i < count; i++)
        {
            var libraries = workspace.Draft.Libraries.Where(library => !library.PendingDelete).ToList();
            var customs = libraries.Where(library => !library.Content.BuiltIn && workspace.CanEditContent(library.Content.Id)).ToList();
            var builtIns = libraries.Where(library => library.Content.BuiltIn && workspace.CanEditContent(library.Content.Id)).ToList();
            switch (random.Next(9))
            {
                case 0:
                {
                    var library = libraries[random.Next(libraries.Count)];
                    workspace.SetEnabled(library.Content.Id, !workspace.Draft.LocalState.EnabledIds.Contains(library.Content.Id));
                    actions.Add("toggle");
                    break;
                }

                case 1:
                {
                    var library = libraries[random.Next(libraries.Count)];
                    if (workspace.SetAiPermission(library.Content.Id, random.Next(2) == 0).Applied)
                    {
                        actions.Add("ai");
                    }

                    break;
                }

                case 2 when customs.Count > 0:
                {
                    var library = customs[random.Next(customs.Count)].Content.Id;
                    var rows = workspace.RowsOf(library);
                    if (rows.Count > 0)
                    {
                        var row = rows[random.Next(rows.Count)];
                        if (workspace.EditTerm(library, row.RowId, row.Row.Values with { Written = row.Row.Values.Written + " r" + round }).Applied)
                        {
                            actions.Add("edit custom");
                        }
                    }

                    break;
                }

                case 3 when customs.Count > 0:
                {
                    var library = customs[random.Next(customs.Count)].Content.Id;
                    if (workspace.AddTerm(library, new TermValues($"added term {round} {i}", $"Added {round}")).Applied)
                    {
                        actions.Add("add custom");
                    }

                    break;
                }

                case 4 when builtIns.Count > 0:
                {
                    var library = builtIns[random.Next(builtIns.Count)].Content.Id;
                    var rows = workspace.RowsOf(library);
                    var row = rows[random.Next(rows.Count)];
                    if (workspace.EditTerm(library, row.RowId, row.Row.Values with { Written = row.Row.Values.Written + " e" + round }).Applied)
                    {
                        actions.Add("edit built-in");
                    }

                    break;
                }

                case 5 when builtIns.Count > 0:
                {
                    var library = builtIns[random.Next(builtIns.Count)].Content.Id;
                    var rows = workspace.RowsOf(library).Where(row => row.Row.Values.Enabled).ToList();
                    if (rows.Count > 0)
                    {
                        workspace.SetTermEnabled(library, rows[random.Next(rows.Count)].RowId, enabled: false);
                        actions.Add("turn off built-in row");
                    }

                    break;
                }

                case 6:
                {
                    var created = workspace.CreateLibrary();
                    workspace.AddTerm(created, new TermValues($"new library term {round}", $"New {round}"));
                    actions.Add("create");
                    break;
                }

                case 7 when customs.Count > 2:
                {
                    workspace.DeleteLibrary(customs[random.Next(customs.Count)].Content.Id);
                    actions.Add("delete");
                    break;
                }

                case 8 when customs.Count > 0:
                {
                    if (workspace.Rename(customs[random.Next(customs.Count)].Content.Id, $"Renamed {round} {i}").Applied)
                    {
                        actions.Add("rename");
                    }

                    break;
                }
            }
        }

        return actions;
    }

    // D's shown permission is C's policy on committed inputs: for every library whose Save writes nothing, the box shows
    // what IsPermitted says of the draft's choices over the committed content, and a library in use is excluded from the
    // preview's AI vocabulary exactly when it is not permitted.
    private static void AssertShownPermissionIsThePolicy(
        LibraryWorkspace workspace, LibraryDraft draft, LibraryCatalog committed, LibraryComposition preview, string context)
    {
        foreach (var library in draft.Libraries)
        {
            if (library.WritesContent || library.PendingDelete || committed.Find(library.Content.Id) is not { } file)
            {
                continue;
            }

            var id = library.Content.Id;
            var permitted = AiVocabularyPolicy.IsPermitted(draft.LocalState, id, library.Content.BuiltIn, file.ContentHash);
            Assert.True(permitted == workspace.ShowsAiPermission(id), $"{context}: {id} shows {workspace.ShowsAiPermission(id)}, the policy says {permitted}");
            if (preview.EnabledLibraries.Any(enabled => string.Equals(enabled.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                Assert.True(permitted != preview.AiExcludedLibraryIds.Contains(id), $"{context}: {id} in use, permitted {permitted}");
            }
        }
    }

    private static List<string> Differences(LibraryComposition preview, LibraryComposition after, LibraryCatalog saved)
    {
        var differences = new List<string>();
        void Compare<T>(string what, IEnumerable<T> previewed, IEnumerable<T> committed)
        {
            var left = previewed.ToList();
            var right = committed.ToList();
            if (!left.SequenceEqual(right))
            {
                differences.Add($"{what}: preview [{string.Join("; ", left)}], after the Save [{string.Join("; ", right)}]");
            }
        }

        Compare("rules", preview.Rules.Select(Rule), after.Rules.Select(Rule));
        Compare("AI entries", preview.AiLibraryEntries.Select(Entry), after.AiLibraryEntries.Select(Entry));
        Compare("AI excluded", preview.AiExcludedLibraryIds.Order(StringComparer.Ordinal), after.AiExcludedLibraryIds.Order(StringComparer.Ordinal));
        Compare("enabled", preview.EnabledLibraries.Select(l => l.Id), after.EnabledLibraries.Select(l => l.Id));
        Compare("any marker active", new[] { preview.AnyLegacyMarkerActive }, new[] { after.AnyLegacyMarkerActive });
        foreach (var library in saved.Libraries)
        {
            foreach (var row in library.Content.Rows)
            {
                Compare(
                    $"status of {library.Content.Id} {row.Key.Value}",
                    new[] { Status(preview.StatusOf(library.Content.Id, row.Key)) },
                    new[] { Status(after.StatusOf(library.Content.Id, row.Key)) });
            }
        }

        return differences;
    }

    private static string Rule(ComposedRule rule) =>
        $"{rule.Key.Value}|{rule.LibraryId}|{rule.Tier}|{rule.LegacyMarkerActive}|{Entry(rule.Entry)}";

    private static string Entry(DictionaryEntry entry) => $"{entry.Pattern}={entry.Replacement}{(entry.WholeWord ? string.Empty : " (in words)")}";

    private static string Status(TermStatus status) =>
        $"{status.Marker}|{status.Winner}|{status.WinningLibraryId}|{(status.WinningEntry is { } winning ? Entry(winning) : "-")}|" +
        $"same:{string.Join(",", status.SameResultIn)}|different:{string.Join(",", status.DifferentResultIn)}|" +
        $"{status.LegacyMarkerActive}|{status.Glossary}|{status.Review is not null}";
}
