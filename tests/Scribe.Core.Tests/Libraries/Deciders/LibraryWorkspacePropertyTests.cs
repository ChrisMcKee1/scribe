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

        // The sequences reach every kind of change a Save carries, and saves in the middle of them, some with the user
        // working on while they run.
        Assert.True(totals.CustomWrites > 100, $"custom writes {totals.CustomWrites}");
        Assert.True(totals.BuiltInWrites > 50, $"built-in writes {totals.BuiltInWrites}");
        Assert.True(totals.Deletions > 20, $"deletions {totals.Deletions}");
        Assert.True(totals.Restores > 20, $"restores {totals.Restores}");
        Assert.True(totals.Purges > 5, $"purges {totals.Purges}");
        Assert.True(totals.MidSaves > 50, $"saves in the middle {totals.MidSaves}");
        Assert.True(totals.InterleavedSaves > 25, $"saves with work while they ran {totals.InterleavedSaves}");
        Assert.True(totals.DeletedWhileSaving > 5, $"libraries deleted while their save ran {totals.DeletedWhileSaving}");
        Assert.True(totals.KeptWhileSaving > 5, $"deletions taken back while their save ran {totals.KeptWhileSaving}");
    }

    private sealed class Coverage
    {
        public int CustomWrites;
        public int BuiltInWrites;
        public int Deletions;
        public int Restores;
        public int Purges;
        public int MidSaves;
        public int InterleavedSaves;
        public int DeletedWhileSaving;
        public int KeptWhileSaving;

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
        var catalog = StartingCatalog(out var store);
        var workspace = Workspace(catalog);
        var counter = 0;
        string Fresh(string prefix) => $"{prefix} {seed} {++counter}";

        for (var step = 0; step < 40; step++)
        {
            var choice = random.Next(20);
            if (choice == 19)
            {
                // A Save in the middle of the sequence: the edits after it carry on over the new catalog.
                catalog = Save(workspace, catalog, store, random, seed, step, totals, Fresh);
            }
            else
            {
                Operate(workspace, choice, random, store, Fresh);
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

    [Fact]
    public void WritesContent_is_exactly_what_the_captured_change_set_writes_at_every_revision()
    {
        // The ruling on GPT-6 Astra's verification of sub-stream C: the draft tells a preview which libraries its Save
        // writes, and that is the capture's own answer at every revision, never an inference from what the rows show.
        var checks = 0;
        var writing = 0;
        var invisible = 0;
        for (var seed = 1; seed <= 80; seed++)
        {
            var random = new Random(seed * 7919);
            var catalog = StartingCatalog(out var store);
            var workspace = Workspace(catalog);
            var counter = 0;
            string Fresh(string prefix) => $"{prefix} {seed} {++counter}";

            for (var step = 0; step < 40; step++)
            {
                var choice = random.Next(24);
                if (choice == 19 && workspace.CaptureChangeSet().ChangeSet is { } saving)
                {
                    catalog = Apply(catalog, saving, store);
                    workspace.MarkSaved(saving.DraftRevision, catalog);
                }
                else if (choice >= 20 && workspace.CanEditContent(GitHubId))
                {
                    // Every row of GitHub back to its shipped values, saved, then Restore all: the document the Save
                    // removes may then hold only an off intent for a term no row shows, a write nothing visible reveals.
                    foreach (var row in workspace.RowsOf(GitHubId).ToList())
                    {
                        if (row.Row.Shipped is null)
                        {
                            workspace.DeleteTerm(GitHubId, row.RowId);
                        }
                        else if (row.Row.Origin != TermOrigin.Shipped)
                        {
                            workspace.RestoreBuiltInValues(GitHubId, row.RowId);
                        }
                    }

                    if (workspace.CaptureChangeSet().ChangeSet is { } cleaned)
                    {
                        catalog = Apply(catalog, cleaned, store);
                        workspace.MarkSaved(cleaned.DraftRevision, catalog);
                    }

                    workspace.RestoreAllBuiltInValues(GitHubId);
                }
                else
                {
                    Operate(workspace, Math.Min(choice, 18), random, store, Fresh);
                }

                if (workspace.CaptureChangeSet().ChangeSet is not { } changes)
                {
                    continue;
                }

                foreach (var library in workspace.Draft.Libraries)
                {
                    var id = library.Content.Id;
                    var writes = Writes(changes, id);
                    Assert.True(writes == WritesContent(workspace, id), $"seed {seed} step {step}: {id} writes={writes}");
                    checks++;
                    if (writes)
                    {
                        writing++;
                        if (library.Content.BuiltIn
                            && catalog.Find(id) is { } committed
                            && committed.Content.Rows.SequenceEqual(library.Content.Rows))
                        {
                            invisible++;
                        }
                    }
                }
            }
        }

        Assert.True(checks > 5000, $"libraries checked {checks}");
        Assert.True(writing > 1000, $"libraries written {writing}");
        Assert.True(invisible > 10, $"built-in documents written with no row changed {invisible}");
    }

    // The catalog every sequence starts from: both fake built-ins (GitHub with an edit, an addition and an off intent for a
    // term this version does not ship), two custom libraries, two Recently deleted entries and a retired built-in.
    private static LibraryCatalog StartingCatalog(out Dictionary<string, RecentlyDeletedContent> store)
    {
        var gone = Deleted("20260901T100000Z.gone.csv", "gone", "Gone", new TermValues("gone term", "Gone term"));
        var scratch = Deleted("20260902T100000Z.scratch.csv", "scratch", "Scratch", new TermValues("scratch term", "Scratch term"));
        store = new Dictionary<string, RecentlyDeletedContent>(StringComparer.OrdinalIgnoreCase)
        {
            [gone.Entry.EntryName] = gone,
            [scratch.Entry.EntryName] = scratch,
        };
        var edits = new BuiltInLibraryEdits(GitHubId,
        [
            new BuiltInTermEdit(LibraryTermKey.From("copilot"), BuiltInTermIntent.Edited,
                new TermValues("copilot", "Copilot"), new TermValues("copilot", "GitHub Copilot")),
            new BuiltInTermEdit(LibraryTermKey.From("gh cli"), BuiltInTermIntent.Added, null, new TermValues("gh cli", "GitHub CLI")),
            new BuiltInTermEdit(LibraryTermKey.From("retired row"), BuiltInTermIntent.Off, new TermValues("retired row", "Retired"), null),
        ]);
        return Catalog(
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
    }

    // A Save: capture, commit through the fake store, mark saved. Half of them let the user go on working while the Save
    // runs (review finding A2): deleting a library the Save creates or restores, taking back a deletion it makes, undoing,
    // or any other operation. Marking saved never changes what the page shows, and what it shows reads back from a Save.
    private static LibraryCatalog Save(
        LibraryWorkspace workspace,
        LibraryCatalog catalog,
        Dictionary<string, RecentlyDeletedContent> store,
        Random random,
        int seed,
        int step,
        Coverage totals,
        Func<string, string> fresh)
    {
        var changes = CaptureOrFail(workspace, seed, step);
        totals.Count(changes);
        totals.MidSaves++;
        var saved = Apply(catalog, changes, store);
        if (random.Next(2) == 0)
        {
            workspace.MarkSaved(changes.DraftRevision, saved);
            Assert.False(workspace.HasUnsavedChanges, $"seed {seed} step {step}: unsaved after a save");
            Assert.Equal(Content(Workspace(saved)), Content(workspace));
            return saved;
        }

        totals.InterleavedSaves++;
        var added = changes.Writes
            .Where(write => !write.BuiltIn && write.Origin != LibraryOrigin.Existing)
            .Select(write => write.LibraryId)
            .Concat(changes.RecentlyDeletedActions.Where(action => action.RestoreAsId is not null).Select(action => action.RestoreAsId!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deleted = changes.Deletions.Select(deletion => deletion.LibraryId).ToList();
        for (var operations = random.Next(1, 4); operations > 0; operations--)
        {
            switch (random.Next(4))
            {
                case 0 when added.Count > 0:
                    var created = added[random.Next(added.Count)];
                    if (workspace.Draft.Find(created) is { PendingDelete: false })
                    {
                        workspace.DeleteLibrary(created);
                        totals.DeletedWhileSaving++;
                    }

                    break;

                case 1 when deleted.Count > 0:
                    var gone = deleted[random.Next(deleted.Count)];
                    if (workspace.Draft.Find(gone) is { PendingDelete: true })
                    {
                        workspace.DiscardLibrary(gone);
                        totals.KeptWhileSaving++;
                    }

                    break;

                case 2:
                    workspace.Undo();
                    break;

                default:
                    Operate(workspace, random.Next(19), random, store, fresh);
                    break;
            }
        }

        var shown = Shown(workspace);
        workspace.MarkSaved(changes.DraftRevision, saved);
        Assert.True(shown == Shown(workspace), $"seed {seed} step {step}: marking saved changed the page\n{shown}\n---\n{Shown(workspace)}");
        var again = workspace.CaptureChangeSet();
        if (again.ChangeSet is { } next)
        {
            Assert.Equal(Content(workspace), Content(Workspace(Apply(saved, next, store))));
        }

        return saved;
    }

    // One operation of the sequence, chosen by 0 to 18.
    private static void Operate(
        LibraryWorkspace workspace,
        int choice,
        Random random,
        IReadOnlyDictionary<string, RecentlyDeletedContent> store,
        Func<string, string> fresh)
    {
        var live = workspace.Draft.Libraries.Where(library => !library.PendingDelete).Select(library => library.Content.Id).ToList();
        var editable = live.Where(workspace.CanEditContent).ToList();
        var custom = live.Where(id => !workspace.Draft.Find(id)!.Content.BuiltIn).ToList();
        string Pick(IReadOnlyList<string> ids) => ids[random.Next(ids.Count)];

        switch (choice)
        {
            case 0 or 1 when editable.Count > 0:
                var target = Pick(editable);
                if (random.Next(4) == 0)
                {
                    workspace.AddTerm(target, new TermValues(fresh("removes"), ""), removalIntent: true);
                }
                else
                {
                    workspace.AddTerm(target, new TermValues(fresh("word"), fresh("Word"), WholeWord: random.Next(2) == 0));
                }

                break;

            case 2 or 3 when editable.Count > 0:
                var edited = Pick(editable);
                var rows = workspace.RowsOf(edited).Where(row => row.Row.Values.Spoken.Length > 0).ToList();
                if (rows.Count > 0)
                {
                    var row = rows[random.Next(rows.Count)];
                    var values = random.Next(2) == 0
                        ? row.Row.Values with { Written = fresh("Edited") }
                        : row.Row.Values with { Spoken = fresh("spoken"), Written = row.Row.Values.Written.Length == 0 ? fresh("Now") : row.Row.Values.Written };
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
                    workspace.Rename(created, fresh("Library"));
                }

                break;

            case 9 when custom.Count > 0:
                var renamed = Pick(custom);
                if (workspace.CanEditContent(renamed))
                {
                    if (random.Next(2) == 0)
                    {
                        workspace.Rename(renamed, fresh("Renamed"));
                    }
                    else
                    {
                        workspace.SetDetails(renamed, fresh("Category"), random.Next(2) == 0 ? null : fresh("Description"));
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
                var fileRows = new List<TermValues> { new(fresh("imported"), fresh("Imported")) };
                if (existing is not null)
                {
                    fileRows.Add(existing.Row.Values with { Written = fresh("From file") });
                }

                var document = Document(fresh("Import"), [.. fileRows]);
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
        }
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

    // What the page shows of the libraries the user keeps: the content without the deleted list, where a library the user
    // removed while its Save ran moves from nowhere to a pending deletion of the saved library.
    private static string Shown(LibraryWorkspace workspace) =>
        string.Join("\n", Content(workspace).Split('\n').Where(line => !line.StartsWith("deleted=", StringComparison.Ordinal)));
}
