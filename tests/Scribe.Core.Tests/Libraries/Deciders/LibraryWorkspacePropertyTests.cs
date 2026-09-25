using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-11: any sequence of workspace operations, captured and applied by a fake store to a new catalog, gives a workspace
/// showing the same content; and the workspace marked saved against that catalog shows it too, with nothing unsaved. Saves
/// happen in the middle of sequences, half of them with arbitrary operations while they run and some with the store doing
/// more than the plan (a library kept under an id it invents, an outside version kept), and after every MarkSaved nothing
/// the page showed is lost or overwritten, every operation of the next change set names what the committed catalog holds,
/// and something is unsaved exactly when the next Save would change something. A Save that leaves a reference repair
/// pending is followed by W2's follow-up Save of the repairs alone, which writes exactly them and leaves every other change
/// unsaved. Seeded, so a failure names the sequence.
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
        // working on while they run and some where the store did more than the plan.
        Assert.True(totals.CustomWrites > 100, $"custom writes {totals.CustomWrites}");
        Assert.True(totals.BuiltInWrites > 50, $"built-in writes {totals.BuiltInWrites}");
        Assert.True(totals.Deletions > 20, $"deletions {totals.Deletions}");
        Assert.True(totals.Restores > 20, $"restores {totals.Restores}");
        Assert.True(totals.Purges > 5, $"purges {totals.Purges}");
        Assert.True(totals.MidSaves > 50, $"saves in the middle {totals.MidSaves}");
        Assert.True(totals.InterleavedSaves > 25, $"saves with work while they ran {totals.InterleavedSaves}");
        Assert.True(totals.DeletedWhileSaving > 5, $"libraries deleted while their save ran {totals.DeletedWhileSaving}");
        Assert.True(totals.KeptWhileSaving > 5, $"deletions taken back while their save ran {totals.KeptWhileSaving}");
        Assert.True(totals.RestoredAgainWhileSaving > 5, $"entries restored again while their save ran {totals.RestoredAgainWhileSaving}");
        Assert.True(totals.PurgedWhileSaving > 5, $"entries deleted for good while their save ran {totals.PurgedWhileSaving}");
        Assert.True(totals.SavedUnderNewIds > 10, $"libraries kept under another id {totals.SavedUnderNewIds}");
        Assert.True(totals.KeptAtAnOccupiedId > 5, $"kept under an id a library made meanwhile held {totals.KeptAtAnOccupiedId}");
        Assert.True(totals.OutsideVersions > 10, $"outside versions kept {totals.OutsideVersions}");
        Assert.True(totals.Probes > 100, $"change sets checked after marking saved {totals.Probes}");
        Assert.True(totals.KeptWithACopy > 5, $"libraries kept under another id with a copy {totals.KeptWithACopy}");
        Assert.True(totals.RepairsPending > 15, $"saves that left a reference repair pending {totals.RepairsPending}");
        Assert.True(totals.FollowUpSaves == totals.RepairsPending, $"follow-up saves {totals.FollowUpSaves}");
        Assert.True(totals.InterleavedFollowUps > 5, $"follow-up saves with work while they ran {totals.InterleavedFollowUps}");
        Assert.True(totals.ExactFollowUps > 8, $"follow-up saves compared with the change set before them {totals.ExactFollowUps}");
        Assert.True(totals.FollowUpsLeavingChangesUnsaved > 3, $"follow-up saves that left other changes unsaved {totals.FollowUpsLeavingChangesUnsaved}");
        Assert.True(totals.RepairedCopiesRenamed > 8, $"repaired copies renamed before their follow-up {totals.RepairedCopiesRenamed}");
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
        public int RestoredAgainWhileSaving;
        public int PurgedWhileSaving;
        public int SavedUnderNewIds;
        public int KeptAtAnOccupiedId;
        public int OutsideVersions;
        public int Probes;
        public int KeptWithACopy;
        public int RepairsPending;
        public int RepairsWithoutAKeptOriginal;
        public int FollowUpSaves;
        public int CopiedBeforeSaving;
        public int InterleavedFollowUps;
        public int ExactFollowUps;
        public int FollowUpsLeavingChangesUnsaved;
        public int RepairedCopiesRenamed;

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
    public void D19_WritesContent_is_exactly_what_the_captured_change_set_writes_at_every_revision()
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
    // runs (review findings A2 and A8): deleting a library the Save creates or restores, taking back a deletion it makes,
    // restoring again or deleting for good an entry the Save restores, undoing, or any other operation. And the store may
    // do more than the plan: keep a new library under an id it invents, often one a library made meanwhile holds (G1), or
    // keep the outside version of a library it wrote. After every MarkSaved: nothing the page showed is lost or
    // overwritten, the store's additions are all that is new, every operation of the next change set names what the
    // committed catalog holds, and something is unsaved exactly when the next Save would change something.
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
        var context = $"seed {seed} step {step}: ";

        // Sometimes a library the Save creates is duplicated first, so the Save writes a copy with its original and the
        // store may keep the original under another id: what leaves a reference repair pending (A10). Drawn from a stream
        // of its own, so a sequence that does not take this path is the one it was.
        var aim = new Random(seed * 1009 + step);
        var planned = workspace.Draft.Libraries
            .Where(library => library.Unsaved
                && !library.PendingDelete
                && library.Origin is LibraryOrigin.Created or LibraryOrigin.Imported or LibraryOrigin.Duplicated)
            .Select(library => library.Content.Id)
            .ToList();
        if (planned.Count > 0 && aim.Next(3) == 0)
        {
            workspace.Duplicate(planned[aim.Next(planned.Count)]);
            totals.CopiedBeforeSaving++;
        }

        var changes = CaptureOrFail(workspace, seed, step);
        totals.Count(changes);
        totals.MidSaves++;
        if (random.Next(2) == 0)
        {
            totals.InterleavedSaves++;
            WorkWhileSaving(workspace, changes, store, random, totals, fresh);
        }

        var outcomes = Outcomes(workspace, catalog, changes, random, totals);
        var saved = Apply(catalog, changes, store, outcomes);
        var shown = Kept(workspace.Draft);
        workspace.MarkSaved(changes.DraftRevision, saved);
        var probe = AfterMarkSaved(workspace, shown, outcomes, saved, context, totals);

        // W2's rule after round 4: a Save that left reference repairs pending is completed by a follow-up Save of the
        // repairs alone, which the user may work through as well.
        if (workspace.HasPendingReferenceRepairs)
        {
            totals.RepairsPending++;
            if (!outcomes.Any(outcome => outcome is StoreOutcome.SavedUnderNewId))
            {
                totals.RepairsWithoutAKeptOriginal++;
            }

            saved = FollowUp(workspace, saved, store, random, context, totals, fresh, probe);
        }

        return saved;
    }

    // What holds after every MarkSaved: nothing the page showed is lost or overwritten and the store's additions are all
    // that is new, every operation of the next change set names what the committed catalog holds, something is unsaved
    // exactly when that change set is not empty, and every reference Use this copy instead would follow is one it can
    // trust. Returns that change set, or null when a problem blocks it.
    private static LibraryChangeSet? AfterMarkSaved(
        LibraryWorkspace workspace, List<string> shown, List<StoreOutcome> outcomes, LibraryCatalog saved, string context, Coverage totals)
    {
        var fromStore = Workspace(saved).Draft;
        var added = outcomes
            .Select(outcome => outcome switch
            {
                StoreOutcome.SavedUnderNewId kept => kept.PlannedId,
                StoreOutcome.OutsideVersion kept => kept.KeptAsId,
                _ => throw new InvalidOperationException(),
            })
            .Select(id => Signature(fromStore, fromStore.Find(id)!));
        Assert.True(
            shown.Concat(added).Order(StringComparer.Ordinal).SequenceEqual(Kept(workspace.Draft)),
            $"{context}marking saved lost, overwrote or invented a library\n{string.Join("\n", shown)}\n---\n{string.Join("\n", Kept(workspace.Draft))}");

        var next = workspace.CaptureChangeSet();
        if (next.ChangeSet is { } probe)
        {
            AssertPreImages(saved, probe, context);
            Assert.True(workspace.HasUnsavedChanges == !probe.IsEmpty, $"{context}unsaved={workspace.HasUnsavedChanges} but the next Save is empty={probe.IsEmpty}");
            totals.Probes++;
        }

        if (!workspace.HasUnsavedChanges)
        {
            Assert.Equal(Content(Workspace(saved)), Content(workspace));
        }

        AssertReferences(workspace, saved, context);
        return next.ChangeSet;
    }

    // W2's follow-up Save (the ruling after round 4): the reference repairs alone, committed by the store like any Save (it
    // may keep an outside version of a copy it rewrites) while the user may go on working. Its change set holds exactly the
    // repairs; after MarkSaved everything a Save leaves holds and no repair is pending; and when nothing was done while it
    // ran and the store moved no library off its id, the next full change set is the one taken before it less the repairs,
    // so every other change is still unsaved.
    private static LibraryCatalog FollowUp(
        LibraryWorkspace workspace,
        LibraryCatalog catalog,
        Dictionary<string, RecentlyDeletedContent> store,
        Random random,
        string context,
        Coverage totals,
        Func<string, string> fresh,
        LibraryChangeSet? before)
    {
        context += "the follow-up Save: ";
        totals.FollowUpSaves++;
        var repairs = workspace.PendingReferenceRepairs.ToList();

        // Sometimes the user renames a repaired copy after the first Save and before the follow-up is captured, which the
        // follow-up must not write. The change set to compare with is then taken again, at the revision the follow-up is
        // captured at, which the two captures must not share.
        if (random.Next(2) == 0)
        {
            var renamed = repairs[random.Next(repairs.Count)];
            if (workspace.CanEditContent(renamed) && workspace.Rename(renamed, fresh("Renamed copy")).Applied)
            {
                totals.RepairedCopiesRenamed++;
                before = workspace.CaptureChangeSet().ChangeSet;
            }
        }

        var draft = workspace.Draft;
        var result = workspace.CaptureReferenceRepairs();
        Assert.True(result.ChangeSet is not null, $"{context}{string.Join(", ", result.Issues.Select(issue => issue.Kind))}");
        var changes = result.ChangeSet!;
        AssertRepairsOnly(changes, repairs, draft, catalog, context);

        var interleaved = random.Next(3) == 0;
        if (interleaved)
        {
            totals.InterleavedFollowUps++;
            WorkWhileSaving(workspace, changes, store, random, totals, fresh);
        }

        var outcomes = Outcomes(workspace, catalog, changes, random, totals);
        var saved = Apply(catalog, changes, store, outcomes);
        var shownDraft = workspace.Draft;
        var shown = Kept(shownDraft);
        workspace.MarkSaved(changes.DraftRevision, saved);
        var after = AfterMarkSaved(workspace, shown, outcomes, saved, context, totals);
        Assert.False(workspace.HasPendingReferenceRepairs, $"{context}a reference repair is still pending after it");

        // The exact comparison needs the ids to stay put: an outside version the store kept under an id no library of the
        // draft held moves nothing.
        var nothingMoved = outcomes.All(outcome => outcome is StoreOutcome.OutsideVersion kept && shownDraft.Find(kept.KeptAsId) is null);
        if (!interleaved && nothingMoved && before is not null)
        {
            Assert.True(after is not null, $"{context}the next change set is refused");
            AssertOnlyTheRepairsWereSaved(before, after!, repairs, saved, context);
            totals.ExactFollowUps++;
            if (!after!.IsEmpty)
            {
                totals.FollowUpsLeavingChangesUnsaved++;
            }
        }

        return saved;
    }

    // The follow-up's change set is exactly the pending repairs: one write per listed copy, of its committed file with the
    // based-on reference the draft shows and nothing else of it; no other write, deletion or Recently deleted action; and
    // the committed local state, unchanged.
    private static void AssertRepairsOnly(
        LibraryChangeSet changes, IReadOnlyList<string> repairs, LibraryDraft draft, LibraryCatalog committed, string context)
    {
        Assert.True(changes.Deletions.Count == 0 && changes.RecentlyDeletedActions.Count == 0, $"{context}it deletes, restores or purges");
        Assert.False(changes.LocalStateChanged, $"{context}it changes the local state");
        Assert.True(
            repairs.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(changes.Writes.Select(write => write.LibraryId).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            $"{context}it writes {string.Join(", ", changes.Writes.Select(write => write.LibraryId))}, not the repairs {string.Join(", ", repairs)}");
        foreach (var write in changes.Writes)
        {
            var file = committed.Find(write.LibraryId)!;
            var repaired = file.Content with { BasedOn = draft.Find(write.LibraryId)!.Content.BasedOn };
            Assert.True(
                !write.BuiltIn && write.Edits is null && write.Recovery == BuiltInEditsRecovery.None && write.ExpectedPreImage == file.ContentHash,
                $"{context}{write.LibraryId} is not written as an ordinary write over its file");
            Assert.True(
                Describe(repaired) == Describe(write.Content!) && !string.Equals(file.Content.BasedOn, repaired.BasedOn, StringComparison.Ordinal),
                $"{context}{write.LibraryId} is written as {Describe(write.Content!)}, not as its file with the repaired reference {Describe(repaired)}");
        }

        Assert.True(LocalOf(changes.LocalState) == LocalOf(committed.LocalState), $"{context}it carries another local state than the committed one");
    }

    // With nothing done while the follow-up ran and no library moved off its id, the next change set is the one taken
    // before the follow-up less the repairs: a repaired copy is written only if the user changed more than its reference,
    // with the same content over the file the follow-up wrote; every other write, every deletion and Recently deleted
    // action and the local state are exactly as they were. So nothing but the repairs was counted as saved.
    private static void AssertOnlyTheRepairsWereSaved(
        LibraryChangeSet before, LibraryChangeSet after, IReadOnlyList<string> repairs, LibraryCatalog committed, string context)
    {
        var repaired = repairs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = new List<string>();
        foreach (var write in before.Writes)
        {
            var preImage = write.ExpectedPreImage;
            if (repaired.Contains(write.LibraryId))
            {
                var file = committed.Find(write.LibraryId)!;
                if (Describe(write.Content!) == Describe(file.Content))
                {
                    continue;
                }

                preImage = file.ContentHash;
            }

            expected.Add(WriteOf(write, preImage));
        }

        var actual = after.Writes.Select(write => WriteOf(write, write.ExpectedPreImage)).ToList();
        Assert.True(
            expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal)),
            $"{context}the next writes are not the earlier ones less the repairs\n{string.Join("\n", expected)}\n---\n{string.Join("\n", actual)}");
        Assert.True(
            before.Deletions.Select(deletion => $"{deletion.LibraryId}|{deletion.FileName}|{deletion.ExpectedPreImage}").Order(StringComparer.Ordinal)
                .SequenceEqual(after.Deletions.Select(deletion => $"{deletion.LibraryId}|{deletion.FileName}|{deletion.ExpectedPreImage}").Order(StringComparer.Ordinal)),
            $"{context}the next deletions changed");
        Assert.True(
            before.RecentlyDeletedActions.Select(action => $"{action.Kind}|{action.EntryName}|{action.ExpectedHash}|{action.RestoreAsId}").Order(StringComparer.Ordinal)
                .SequenceEqual(after.RecentlyDeletedActions.Select(action => $"{action.Kind}|{action.EntryName}|{action.ExpectedHash}|{action.RestoreAsId}").Order(StringComparer.Ordinal)),
            $"{context}the next Recently deleted actions changed");
        Assert.True(
            LocalOf(before.LocalState) == LocalOf(after.LocalState) && before.LocalStateChanged == after.LocalStateChanged,
            $"{context}the next local state changed");
    }

    private static string WriteOf(LibraryWrite write, LibraryContentHash? preImage) =>
        $"{write.LibraryId}|{write.BuiltIn}|{write.Origin}|{write.Recovery}|{preImage}|"
        + (write.Content is { } content ? Describe(content) : "")
        + "|" + (write.Edits is { } edits ? string.Join(";", edits.Terms.Select(term => term.ToString())) : "");

    // A local state as one string, the accepted content aside (a Save changes that for every file it writes).
    private static string LocalOf(LibraryLocalState state) =>
        string.Join(",", state.EnabledIds.Order(StringComparer.Ordinal)) + "|"
        + string.Join(",", state.AiPermissions.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")) + "|"
        + string.Join(",", state.LegacyMarkers.Select(marker => $"{marker.LibraryId}:{marker.Key.Value}").Order(StringComparer.Ordinal)) + "|"
        + string.Join(",", state.AiUpgradeNotice.Order(StringComparer.Ordinal)) + "|"
        + state.AiPermissionsLost;

    // Every pending reference repair is a saved copy whose draft reference is not its file's, and Use this copy instead
    // acts on the library that reference names; a saved copy whose draft reference is neither its file's nor a repair
    // (a discard made while a Save wrote its repair) is refused.
    private static void AssertReferences(LibraryWorkspace workspace, LibraryCatalog committed, string context)
    {
        var draft = workspace.Draft;
        var repairs = workspace.PendingReferenceRepairs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(workspace.HasPendingReferenceRepairs == repairs.Count > 0, $"{context}the repair flag disagrees with the list");
        foreach (var id in repairs)
        {
            Assert.True(draft.Find(id) is { PendingDelete: false, Content.BuiltIn: false }, $"{context}{id} is listed as a repair but is no live custom library");
        }

        foreach (var library in draft.Libraries.Where(library => !library.Content.BuiltIn && !library.PendingDelete))
        {
            var id = library.Content.Id;
            var file = committed.Find(id);
            var differs = file is not null && !string.Equals(file.Content.BasedOn, library.Content.BasedOn, StringComparison.Ordinal);
            if (repairs.Contains(id))
            {
                Assert.True(differs, $"{context}{id} is listed as a repair, but its file holds its reference");
                var referent = draft.Find(library.Content.BasedOn!) is { PendingDelete: false } original ? original.Content.Id : null;
                Assert.True(
                    string.Equals(workspace.CopyOriginal(id), referent, StringComparison.OrdinalIgnoreCase),
                    $"{context}Use this copy instead for the repaired {id} would act on {workspace.CopyOriginal(id)}, not {referent}");
            }
            else if (differs)
            {
                Assert.True(
                    workspace.CopyOriginal(id) is null,
                    $"{context}{id}'s reference is neither its file's nor a repair, yet Use this copy instead would act on {workspace.CopyOriginal(id)}");
            }
        }
    }

    // What the user does while a Save runs: one to three operations, some aimed at what the Save is doing.
    private static void WorkWhileSaving(
        LibraryWorkspace workspace,
        LibraryChangeSet changes,
        Dictionary<string, RecentlyDeletedContent> store,
        Random random,
        Coverage totals,
        Func<string, string> fresh)
    {
        var added = changes.Writes
            .Where(write => !write.BuiltIn && write.Origin != LibraryOrigin.Existing)
            .Select(write => write.LibraryId)
            .Concat(changes.RecentlyDeletedActions.Where(action => action.RestoreAsId is not null).Select(action => action.RestoreAsId!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deleted = changes.Deletions.Select(deletion => deletion.LibraryId).ToList();
        var restores = changes.RecentlyDeletedActions
            .Where(action => action.Kind == RecentlyDeletedActionKind.Restore)
            .Select(action => (Id: action.RestoreAsId!, Entry: action.EntryName))
            .ToList();
        var first = true;
        for (var operations = random.Next(1, 4); operations > 0; operations--)
        {
            // Half the Saves that restore an entry see it restored again or deleted for good first (A8).
            var choice = first && restores.Count > 0 && random.Next(2) == 0 ? 2 : random.Next(7);
            first = false;
            switch (choice)
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

                case 2 when restores.Count > 0:
                    // The library the Save restores leaves the draft, which offers its entry again: restored again, under
                    // another id, or deleted for good (A8).
                    var (restoredId, entryName) = restores[random.Next(restores.Count)];
                    if (workspace.Draft.Find(restoredId) is { PendingDelete: false, Origin: LibraryOrigin.Restored })
                    {
                        workspace.DeleteLibrary(restoredId);
                    }

                    if (workspace.Draft.RecentlyDeleted.FirstOrDefault(entry => entry.EntryName == entryName) is { } offered)
                    {
                        if (random.Next(2) == 0)
                        {
                            workspace.RestoreDeleted(store[entryName]);
                            totals.RestoredAgainWhileSaving++;
                        }
                        else
                        {
                            workspace.DeletePermanently(offered);
                            totals.PurgedWhileSaving++;
                        }
                    }

                    break;

                case 3:
                    workspace.Undo();
                    break;

                case 4:
                    workspace.Redo();
                    break;

                case 5 when added.Count > 0:
                    // A copy of a library the Save is creating, made while it runs, whose reference must follow it (A10).
                    var source = added[random.Next(added.Count)];
                    if (workspace.Draft.Find(source) is { PendingDelete: false })
                    {
                        workspace.Duplicate(source);
                    }

                    break;

                default:
                    Operate(workspace, random.Next(19), random, store, fresh);
                    break;
            }
        }
    }

    // What the store does beyond the plan, sometimes: keeps a new library under an id it invents at commit, which may be
    // one a library the user made while the Save ran already holds (TakenIds cannot know it, Grok 4.7's G1), or keeps the
    // outside version of a library it wrote as a new library.
    private static List<StoreOutcome> Outcomes(
        LibraryWorkspace workspace, LibraryCatalog catalog, LibraryChangeSet changes, Random random, Coverage totals)
    {
        var restoredAs = changes.RecentlyDeletedActions
            .Where(action => action.Kind == RecentlyDeletedActionKind.Restore)
            .Select(action => action.RestoreAsId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = catalog.Libraries.Select(library => library.Content.Id)
            .Concat(changes.Writes.Select(write => write.LibraryId))
            .Concat(restoredAs)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var madeMeanwhile = workspace.Draft.Libraries.Select(library => library.Content.Id).Where(id => !used.Contains(id)).ToList();

        string Invent(string stem)
        {
            if (madeMeanwhile.Count > 0 && random.Next(2) == 0)
            {
                var taken = madeMeanwhile[random.Next(madeMeanwhile.Count)];
                madeMeanwhile.Remove(taken);
                used.Add(taken);
                totals.KeptAtAnOccupiedId++;
                return taken;
            }

            for (var n = 2; ; n++)
            {
                var id = $"{stem}-{n}";
                if (used.Add(id) && workspace.Draft.Find(id) is null)
                {
                    return id;
                }
            }
        }

        var outcomes = new List<StoreOutcome>();
        var planned = changes.Writes
            .Where(write => !write.BuiltIn && write.ExpectedPreImage is null && !restoredAs.Contains(write.LibraryId))
            .Select(write => write.LibraryId)
            .ToList();

        // A new library that has a copy, saved with it or made meanwhile, is kept under another id more often, since its
        // copy's reference must follow it (A10).
        bool HasCopy(string id) =>
            changes.Writes.Any(write => string.Equals(write.Content?.BasedOn, id, StringComparison.OrdinalIgnoreCase))
            || workspace.Draft.Libraries.Any(library => string.Equals(library.Content.BasedOn, id, StringComparison.OrdinalIgnoreCase));
        var copied = planned.Where(HasCopy).ToList();
        if (planned.Count > 0 && random.Next(copied.Count > 0 ? 2 : 3) == 0)
        {
            var id = copied.Count > 0 && random.Next(4) != 0 ? copied[random.Next(copied.Count)] : planned[random.Next(planned.Count)];
            if (HasCopy(id))
            {
                totals.KeptWithACopy++;
            }

            outcomes.Add(new StoreOutcome.SavedUnderNewId(id, Invent(id), [new TermValues("theirs " + id, "Theirs")]));
            totals.SavedUnderNewIds++;
        }

        var existing = changes.Writes
            .Where(write => !write.BuiltIn && write.ExpectedPreImage is not null && !restoredAs.Contains(write.LibraryId))
            .Select(write => write.LibraryId)
            .ToList();
        if (existing.Count > 0 && random.Next(4) == 0)
        {
            var id = existing[random.Next(existing.Count)];
            outcomes.Add(new StoreOutcome.OutsideVersion(id, Invent("custom-outside"), [new TermValues("outside " + id, "Outside")]));
            totals.OutsideVersions++;
        }

        return outcomes;
    }

    // What the page shows of every library it lists (an untouched new library included, since it is shown), without ids,
    // which a Save can change (a library kept under another id, a library moved off an id the store gave another), in one
    // order.
    private static List<string> Kept(LibraryDraft draft) =>
        draft.Libraries
            .Where(library => !library.PendingDelete)
            .Select(library => Signature(draft, library))
            .Order(StringComparer.Ordinal)
            .ToList();

    // A library as the page shows it, without its id: its content, its switches, and the library its based-on reference
    // names, compared by that library's content, so a reference left on an id another library now holds shows as a change.
    private static string Signature(LibraryDraft draft, DraftLibrary library)
    {
        var content = library.Content;
        var ai = draft.LocalState.AiPermissions.TryGetValue(content.Id, out var permitted) ? permitted.ToString() : "none";
        return $"{Identity(content)}|on={draft.LocalState.EnabledIds.Contains(content.Id)}|ai={ai}|basedOn={Referent(draft, content.BasedOn)}";
    }

    private static string Identity(LibraryContent content) =>
        $"{content.BuiltIn}|{content.Name}|{content.Category}|{content.Description}|"
        + string.Join(";", content.Rows.Select(row => row.Values.ToString()));

    // The library a based-on reference names: the live library's content, or that it names no live library.
    private static string Referent(LibraryDraft draft, string? basedOn) =>
        basedOn is null ? "none"
        : draft.Find(basedOn) is { PendingDelete: false } original ? Identity(original.Content)
        : "gone";

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
                // The page offers Use this copy instead from CopyOriginal (request 3h), so a copy whose original can't be
                // told is not offered.
                var copies = custom.Where(id => workspace.CopyOriginal(id) is not null).ToList();
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
}
