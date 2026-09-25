using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-11: any sequence of workspace operations, captured and applied by a fake store to a new catalog, gives a workspace
/// showing the same content; and the workspace marked saved against that catalog shows it too, with nothing unsaved.
/// Seeded, so a failure names the sequence that produced it.
/// </summary>
public sealed class LibraryWorkspacePropertyTests
{
    [Fact]
    public void D11_captured_and_applied_operations_reload_as_the_same_content()
    {
        var totals = new Coverage();
        for (var seed = 1; seed <= 120; seed++)
        {
            Run(seed, totals);
        }

        // The sequences reach every kind of change a Save carries, and saves in the middle of them.
        Assert.True(totals.CustomWrites > 100, $"custom writes {totals.CustomWrites}");
        Assert.True(totals.BuiltInWrites > 50, $"built-in writes {totals.BuiltInWrites}");
        Assert.True(totals.Deletions > 20, $"deletions {totals.Deletions}");
        Assert.True(totals.Restores > 20, $"restores {totals.Restores}");
        Assert.True(totals.Purges > 5, $"purges {totals.Purges}");
        Assert.True(totals.MidSaves > 50, $"saves in the middle {totals.MidSaves}");
    }

    private sealed class Coverage
    {
        public int CustomWrites;
        public int BuiltInWrites;
        public int Deletions;
        public int Restores;
        public int Purges;
        public int MidSaves;

        public void Count(LibraryChangeSet changes)
        {
            CustomWrites += changes.Writes.Count(write => !write.BuiltIn);
            BuiltInWrites += changes.Writes.Count(write => write.BuiltIn);
            Deletions += changes.Deletions.Count;
            Restores += changes.RecentlyDeletedActions.Count(action => action.Kind == RecentlyDeletedActionKind.Restore);
            Purges += changes.RecentlyDeletedActions.Count(action => action.Kind == RecentlyDeletedActionKind.DeletePermanently);
        }
    }

    private static void Run(int seed, Coverage totals)
    {
        var random = new Random(seed);
        var gone = Deleted("20260901T100000Z.gone.csv", "gone", "Gone", new TermValues("gone term", "Gone term"));
        var scratch = Deleted("20260902T100000Z.scratch.csv", "scratch", "Scratch", new TermValues("scratch term", "Scratch term"));
        var store = new Dictionary<string, RecentlyDeletedContent>(StringComparer.OrdinalIgnoreCase)
        {
            [gone.Entry.EntryName] = gone,
            [scratch.Entry.EntryName] = scratch,
        };
        var edits = new BuiltInLibraryEdits(GitHubId,
        [
            new BuiltInTermEdit(LibraryTermKey.From("copilot"), BuiltInTermIntent.Edited,
                new TermValues("copilot", "Copilot"), new TermValues("copilot", "GitHub Copilot")),
            new BuiltInTermEdit(LibraryTermKey.From("gh cli"), BuiltInTermIntent.Added, null, new TermValues("gh cli", "GitHub CLI")),
        ]);
        var catalog = Catalog(
            [
                BuiltIn(GitHubId, edits),
                BuiltIn(AzureId),
                Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes"), new TermValues("um", "")]),
                Custom("notes", "Notes", [new TermValues("vm", "VM")], category: "Work", description: "Mine"),
            ],
            [GitHubId, "team-terms"],
            ai: [new("team-terms", true), new("notes", false)],
            recentlyDeleted: [gone.Entry, scratch.Entry],
            retired: [new RetiredBuiltInEdits("data-and-ai", [new TermValues("llm", "LLM")], Hash("retired"))]);
        var workspace = Workspace(catalog);
        var counter = 0;
        string Fresh(string prefix) => $"{prefix} {seed} {++counter}";

        for (var step = 0; step < 40; step++)
        {
            var live = workspace.Draft.Libraries.Where(library => !library.PendingDelete).Select(library => library.Content.Id).ToList();
            var editable = live.Where(workspace.CanEditContent).ToList();
            var custom = live.Where(id => !workspace.Draft.Find(id)!.Content.BuiltIn).ToList();
            string Pick(IReadOnlyList<string> ids) => ids[random.Next(ids.Count)];

            switch (random.Next(20))
            {
                case 0 or 1 when editable.Count > 0:
                    var target = Pick(editable);
                    if (random.Next(4) == 0)
                    {
                        workspace.AddTerm(target, new TermValues(Fresh("removes"), ""), removalIntent: true);
                    }
                    else
                    {
                        workspace.AddTerm(target, new TermValues(Fresh("word"), Fresh("Word"), WholeWord: random.Next(2) == 0));
                    }

                    break;

                case 2 or 3 when editable.Count > 0:
                    var edited = Pick(editable);
                    var rows = workspace.RowsOf(edited).Where(row => row.Row.Values.Spoken.Length > 0).ToList();
                    if (rows.Count > 0)
                    {
                        var row = rows[random.Next(rows.Count)];
                        var values = random.Next(2) == 0
                            ? row.Row.Values with { Written = Fresh("Edited") }
                            : row.Row.Values with { Spoken = Fresh("spoken"), Written = row.Row.Values.Written.Length == 0 ? Fresh("Now") : row.Row.Values.Written };
                        workspace.EditTerm(edited, row.RowId, values);
                    }

                    break;

                case 4 when editable.Count > 0:
                    var owner = Pick(editable);
                    var deletable = workspace.RowsOf(owner)
                        .Where(row => row.Row.Origin is TermOrigin.Custom or TermOrigin.Added or TermOrigin.NoLongerShipped)
                        .ToList();
                    if (deletable.Count > 0)
                    {
                        workspace.DeleteTerm(owner, deletable[random.Next(deletable.Count)].RowId);
                    }

                    break;

                case 5 when editable.Count > 0:
                    var toggled = Pick(editable);
                    var all = workspace.RowsOf(toggled);
                    if (all.Count > 0)
                    {
                        var row = all[random.Next(all.Count)];
                        workspace.SetTermEnabled(toggled, row.RowId, !row.Row.Values.Enabled);
                    }

                    break;

                case 6:
                    var library = Pick(live);
                    workspace.SetEnabled(library, !workspace.Draft.LocalState.EnabledIds.Contains(library));
                    break;

                case 7:
                    workspace.SetAiPermission(Pick(live), random.Next(2) == 0);
                    break;

                case 8:
                    var created = workspace.CreateLibrary();
                    if (random.Next(2) == 0)
                    {
                        workspace.Rename(created, Fresh("Library"));
                    }

                    break;

                case 9 when custom.Count > 0:
                    var renamed = Pick(custom);
                    if (workspace.CanEditContent(renamed))
                    {
                        if (random.Next(2) == 0)
                        {
                            workspace.Rename(renamed, Fresh("Renamed"));
                        }
                        else
                        {
                            workspace.SetDetails(renamed, Fresh("Category"), random.Next(2) == 0 ? null : Fresh("Description"));
                        }
                    }

                    break;

                case 10:
                    workspace.Duplicate(Pick(live));
                    break;

                case 11 when custom.Count > 0:
                    workspace.DeleteLibrary(Pick(custom));
                    break;

                case 12:
                    var available = workspace.Draft.RecentlyDeleted.ToList();
                    if (available.Count > 0)
                    {
                        var entry = available[random.Next(available.Count)];
                        if (random.Next(3) == 0)
                        {
                            workspace.DeletePermanently(entry);
                        }
                        else
                        {
                            workspace.RestoreDeleted(store[entry.EntryName]);
                        }
                    }

                    break;

                case 13 when workspace.CanEditContent(GitHubId):
                    var shippedRows = workspace.RowsOf(GitHubId).Where(row => row.Row.Shipped is not null && row.Row.Origin != TermOrigin.Shipped).ToList();
                    if (shippedRows.Count > 0 && random.Next(3) > 0)
                    {
                        workspace.RestoreBuiltInValues(GitHubId, shippedRows[random.Next(shippedRows.Count)].RowId);
                    }
                    else
                    {
                        workspace.RestoreAllBuiltInValues(GitHubId);
                    }

                    break;

                case 14:
                    var copies = custom.Where(id => workspace.Draft.Find(id)!.Content.BasedOn is { } basedOn
                        && workspace.Draft.Find(basedOn) is { PendingDelete: false }).ToList();
                    if (copies.Count > 0)
                    {
                        workspace.UseCopyInstead(Pick(copies));
                    }

                    break;

                case 15:
                    workspace.Undo();
                    break;

                case 16:
                    workspace.Redo();
                    break;

                case 17 when editable.Count > 0:
                    var into = Pick(editable);
                    var existing = workspace.RowsOf(into).FirstOrDefault(row => row.Row.Values.Spoken.Length > 0);
                    var fileRows = new List<TermValues> { new(Fresh("imported"), Fresh("Imported")) };
                    if (existing is not null)
                    {
                        fileRows.Add(existing.Row.Values with { Written = Fresh("From file") });
                    }

                    var document = Document(Fresh("Import"), [.. fileRows]);
                    LibraryImportTarget importTarget = random.Next(2) == 0
                        ? new LibraryImportTarget.NewLibrary(null)
                        : new LibraryImportTarget.ExistingLibrary(into);
                    workspace.ApplyImport(
                        LibraryImportPlanner.Plan(document, importTarget, workspace.Draft),
                        random.Next(2) == 0 ? ImportConflictChoice.KeepMine : ImportConflictChoice.UseFilesVersion);
                    break;

                case 18:
                    if (random.Next(3) == 0)
                    {
                        workspace.KeepRetiredBuiltIn("data-and-ai");
                    }
                    else
                    {
                        workspace.DiscardLibrary(Pick(live));
                    }

                    break;

                case 19:
                    // A Save in the middle of the sequence: the edits after it carry on over the new catalog.
                    var changes = CaptureOrFail(workspace, seed, step);
                    totals.Count(changes);
                    totals.MidSaves++;
                    var saved = Apply(catalog, changes, store);
                    workspace.MarkSaved(changes.DraftRevision, saved);
                    Assert.False(workspace.HasUnsavedChanges, $"seed {seed} step {step}: unsaved after a save");
                    Assert.Equal(Content(Workspace(saved)), Content(workspace));
                    catalog = saved;
                    break;
            }
        }

        var final = CaptureOrFail(workspace, seed, -1);
        totals.Count(final);
        var reloaded = Apply(catalog, final, store);
        var expected = Content(workspace);
        Assert.Equal(expected, Content(Workspace(reloaded)));
        workspace.MarkSaved(final.DraftRevision, reloaded);
        Assert.False(workspace.HasUnsavedChanges, $"seed {seed}: unsaved after the final save");
        Assert.Equal(expected, Content(workspace));
    }

    // Undo can bring back a library whose generated name another library took meanwhile ("New library", say); that is a
    // real duplicate the user would rename, so the sequence renames it and captures again. Anything else fails the seed.
    private static LibraryChangeSet CaptureOrFail(LibraryWorkspace workspace, int seed, int step)
    {
        var result = workspace.CaptureChangeSet();
        var renames = 0;
        while (result.ChangeSet is null && result.Issues.All(issue => issue.Kind == LibraryValidationKind.DuplicateName) && renames++ < 10)
        {
            foreach (var issue in result.Issues)
            {
                workspace.Rename(issue.LibraryId, $"Resolved {seed} {step} {renames} {issue.LibraryId}");
            }

            result = workspace.CaptureChangeSet();
        }

        Assert.True(result.ChangeSet is not null, $"seed {seed} step {step}: {string.Join(", ", result.Issues.Select(issue => issue.Kind))}");
        return result.ChangeSet!;
    }

    // What the page shows, for every library a Save keeps: an untouched new library is not saved, so it is left out.
    private static string Content(LibraryWorkspace workspace)
    {
        var draft = workspace.Draft;
        var lines = draft.Libraries
            .Where(library => !library.PendingDelete && !(library.Origin == LibraryOrigin.Created && !library.Unsaved))
            .OrderBy(library => library.Content.Id, StringComparer.OrdinalIgnoreCase)
            .Select(library =>
            {
                var content = library.Content;
                var ai = draft.LocalState.AiPermissions.TryGetValue(content.Id, out var permitted) ? permitted.ToString() : "none";
                return $"{content.Id}|{content.BuiltIn}|{content.Name}|{content.Category}|{content.Description}|{content.BasedOn}|"
                    + $"on={draft.LocalState.EnabledIds.Contains(content.Id)}|ai={ai}|"
                    + string.Join(";", content.Rows.Select(row => row.Values.ToString()));
            });
        var deleted = draft.RecentlyDeleted.Select(entry => entry.OriginalId)
            .Concat(draft.Libraries.Where(library => library.PendingDelete).Select(library => library.Content.Id))
            .Order(StringComparer.OrdinalIgnoreCase);
        return string.Join("\n", lines) + "\ndeleted=" + string.Join(",", deleted)
            + $"\nlost={draft.LocalState.AiPermissionsLost}\nnotice={string.Join(",", draft.LocalState.AiUpgradeNotice.Order(StringComparer.OrdinalIgnoreCase))}";
    }
}
