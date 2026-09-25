using System.Runtime.CompilerServices;
using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-4, GPT-6 Astra's A12 and A13: how long a capture lasts, and how long the ids it holds stay reserved. MarkSaved finds a
/// Save's capture by its revision, and while a Save is unresolved (CommitUnknown) the page stays usable and every further
/// Save the user presses is captured and then fenced by the store (PreviousSaveUnfinished, contract 9.7), so more captures
/// can come after it than the workspace keeps of recent ones before a later load settles it as committed. A capture lives
/// as long as its change set does: W2 holds the change set of a Save until the Save settles, and a capture whose change
/// set nobody holds is released once it is no longer among the recent ones. The ids a capture holds are reserved apart
/// from that, from the capture until the draft's history ends (MarkSaved, Rebase, Reload), whoever holds the change set
/// and whenever a collection runs: the Save may still commit a library at any of them, so a library made meanwhile never
/// takes one, and the settlement never mistakes it for the library the Save wrote there.
/// </summary>
public sealed class LibraryWorkspaceCaptureLifetimeTests
{
    [Fact]
    public void D4_an_unresolved_follow_up_is_marked_saved_after_more_fenced_saves_than_the_workspace_keeps()
    {
        // Astra's sequence: the repairs-only follow-up comes back CommitUnknown and W2 holds its change set; the user edits
        // and presses Save more times than the workspace keeps recent captures, each Save fenced and dropped; a later load
        // settles the follow-up as committed.
        var catalog = Standard();
        var workspace = PendingRepair(catalog, out var copy, out var keptAs, out var saved);
        var followUp = RepairsOf(workspace);
        var later = FencedSaves(workspace, LibraryWorkspace.RecentCaptures + 4);
        Collect();

        var committed = Apply(saved, followUp);
        workspace.MarkSaved(followUp.DraftRevision, committed);
        GC.KeepAlive(followUp);

        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.Equal(keptAs, committed.Find(copy)!.Content.BasedOn);
        Assert.Equal(keptAs, workspace.Draft.Find(copy)!.Content.BasedOn);
        AssertStillUnsaved(workspace, committed, later);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void D4_an_unresolved_save_of_the_users_edits_is_marked_saved_after_more_fenced_saves_than_the_workspace_keeps(bool byChangeSet)
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "K8s"));
        var planned = workspace.CreateLibrary();
        workspace.AddTerm(planned, new TermValues("ga", "general availability"));
        var changes = Capture(workspace);

        // While it is unresolved the user removes the library it creates, then edits and presses Save again and again.
        workspace.DeleteLibrary(planned);
        var later = FencedSaves(workspace, LibraryWorkspace.RecentCaptures + 4);
        Collect();

        var committed = Apply(catalog, changes);
        if (byChangeSet)
        {
            workspace.MarkSaved(changes, committed);

            // Marking saved ended the draft's history, and the capture with it.
            Assert.Throws<InvalidOperationException>(() => workspace.MarkSaved(changes, committed));
        }
        else
        {
            workspace.MarkSaved(changes.DraftRevision, committed);
            GC.KeepAlive(changes);
        }

        // The Save's writes are committed; the removal made meanwhile is the user's, a pending deletion of the saved library
        // (review finding A2); every later edit is still unsaved.
        Assert.Equal(new TermValues("kube", "K8s"), committed.Find("team-terms")!.Content.Rows[0].Values);
        Assert.NotNull(committed.Find(planned));
        Assert.True(workspace.Draft.Find(planned)!.PendingDelete);
        AssertStillUnsaved(workspace, committed, later);
    }

    [Fact]
    public void D4_captures_whose_change_sets_nobody_holds_are_released_and_their_number_stays_bounded()
    {
        var catalog = Standard();
        var workspace = Workspace(catalog);
        workspace.EditTerm("team-terms", RowIdOf(workspace, "team-terms", "kube"), new TermValues("kube", "K8s"));
        var held = Capture(workspace);
        var dropped = DroppedCaptures(workspace, 3 * LibraryWorkspace.RecentCaptures);
        Collect();
        DroppedCaptures(workspace, 1);

        // Kept: the recent captures, whoever holds their change sets, and the held one; nothing else, and the references to
        // collected change sets are gone (the held one and the latest capture's are left).
        Assert.Equal(LibraryWorkspace.RecentCaptures + 1, workspace.CapturesKept);
        Assert.Equal(2, workspace.HeldReferences);
        DroppedCaptures(workspace, 20 * LibraryWorkspace.RecentCaptures);
        Collect();
        DroppedCaptures(workspace, 1);
        Assert.Equal(LibraryWorkspace.RecentCaptures + 1, workspace.CapturesKept);
        Assert.Equal(2, workspace.HeldReferences);

        // A capture older than the recent ones whose change set was dropped is gone; the held one is found.
        var committed = Apply(catalog, held);
        Assert.Throws<InvalidOperationException>(() => workspace.MarkSaved(dropped[0], committed));
        workspace.MarkSaved(held.DraftRevision, committed);
        GC.KeepAlive(held);
        Assert.Equal(new TermValues("kube", "K8s"), workspace.Draft.Find("team-terms")!.Content.Rows[0].Values);
        Assert.Contains("team-terms", workspace.UnsavedLibraryIds);

        // Marking saved ends the draft's history, and every capture with it.
        Assert.Equal(0, workspace.CapturesKept);
    }

    [Fact]
    public void D4_a_recent_capture_is_found_by_its_revision_after_its_change_set_was_collected()
    {
        // The ordinary Save does not depend on when a collection runs: a recent capture is kept whoever holds its change
        // set. The committed catalog comes from a twin workspace given the same edit, so nothing here holds the change set.
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var twin = Workspace(catalog);
        foreach (var draft in new[] { workspace, twin })
        {
            draft.EditTerm("team-terms", RowIdOf(draft, "team-terms", "kube"), new TermValues("kube", "K8s"));
        }

        var revision = DroppedCapture(workspace);
        var twins = Capture(twin);
        Assert.Equal(twins.DraftRevision, revision);
        Collect();

        var committed = Apply(catalog, twins);
        workspace.MarkSaved(revision, committed);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Equal(new TermValues("kube", "K8s"), workspace.Draft.Find("team-terms")!.Content.Rows[0].Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void D4_a_library_made_while_a_retained_save_is_unresolved_never_takes_an_id_that_save_captured(bool byChangeSet)
    {
        // GPT-6 Astra's A13: a library and its copy are saved, and the Save comes back CommitUnknown, W2 holding its change
        // set. While it is unresolved the user deletes the library, edits and presses Save more times than the workspace
        // keeps recent captures, each Save fenced and dropped, and makes a new library, which must not take the deleted
        // library's id: the Save may still commit the library there.
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var original = workspace.CreateLibrary();
        workspace.AddTerm(original, new TermValues("ga", "general availability"));
        var copy = workspace.Duplicate(original);
        var changes = Capture(workspace);

        workspace.DeleteLibrary(original);
        var later = FencedSaves(workspace, LibraryWorkspace.RecentCaptures + 4);
        Collect();
        var created = workspace.CreateLibrary();
        Assert.NotEqual(original, created, StringComparer.OrdinalIgnoreCase);
        workspace.AddTerm(created, new TermValues("ship it", "Ship it"));
        Assert.Null(workspace.CopyOriginal(copy));

        // A later load settles the Save as committed.
        var committed = Apply(catalog, changes);
        MarkSaved(workspace, changes, committed, byChangeSet);

        // The library the Save wrote is still the user's pending deletion (review finding A2), and the one made meanwhile
        // is a new library of its own; the copy's original is deleted, so Use this copy instead acts on nothing, and never
        // on the new library.
        Assert.True(workspace.Draft.Find(original)!.PendingDelete);
        var made = workspace.Draft.Find(created)!;
        Assert.Equal((LibraryOrigin.Created, false, true), (made.Origin, made.PendingDelete, made.Unsaved));
        Assert.Null(committed.Find(created));
        Assert.Equal([new TermValues("ship it", "Ship it")], ValuesOf(workspace, created));
        Assert.Equal(original, workspace.Draft.Find(copy)!.Content.BasedOn);
        Assert.Null(workspace.CopyOriginal(copy));

        // The next Save moves the library to Recently deleted and creates the new one beside it, over no file.
        var next = Capture(workspace);
        AssertPreImages(committed, next);
        var deletion = Assert.Single(next.Deletions);
        Assert.Equal((original, committed.Find(original)!.ContentHash!.Value), (deletion.LibraryId, deletion.ExpectedPreImage));
        Assert.DoesNotContain(next.Writes, write => string.Equals(write.LibraryId, original, StringComparison.OrdinalIgnoreCase));
        var creation = next.Writes.Single(write => string.Equals(write.LibraryId, created, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LibraryOrigin.Created, creation.Origin);
        Assert.Null(creation.ExpectedPreImage);
        Assert.Equal([new TermValues("ship it", "Ship it")], creation.Content!.Rows.Select(row => row.Values));
        var written = next.Writes.Single(write => write.LibraryId == "team-terms").Content!.Rows.Select(row => row.Values).ToList();
        Assert.All(later, values => Assert.Contains(values, written));

        var store = new Dictionary<string, RecentlyDeletedContent>(StringComparer.OrdinalIgnoreCase);
        var after = Apply(committed, next, store);
        workspace.MarkSaved(next, after);
        Assert.False(workspace.HasUnsavedChanges);
        var entry = Assert.Single(after.RecentlyDeleted);
        Assert.Equal(original, entry.OriginalId);
        Assert.Equal([new TermValues("ga", "general availability")], store[entry.EntryName].Content.Rows.Select(row => row.Values));
        Assert.Null(after.Find(original));
        Assert.Equal([new TermValues("ship it", "Ship it")], after.Find(created)!.Content.Rows.Select(row => row.Values));
        Assert.Null(workspace.CopyOriginal(copy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void D4_the_follow_up_of_a_retained_save_repairs_the_reference_to_where_the_original_went_never_to_a_library_made_meanwhile(bool byChangeSet)
    {
        // A13 through the repairs-only follow-up: the same sequence, but the store kept the original under another id,
        // because another app created its file, so the copy saved with it is repaired and the follow-up writes the repair.
        // A library made meanwhile at the original's planned id would be taken for the original, moved to the kept id,
        // and named by the repair.
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var original = workspace.CreateLibrary();
        workspace.AddTerm(original, new TermValues("ga", "general availability"));
        var copy = workspace.Duplicate(original);
        var changes = Capture(workspace);

        workspace.DeleteLibrary(original);
        FencedSaves(workspace, LibraryWorkspace.RecentCaptures + 4);
        Collect();
        var created = workspace.CreateLibrary();
        Assert.NotEqual(original, created, StringComparer.OrdinalIgnoreCase);
        workspace.AddTerm(created, new TermValues("ship it", "Ship it"));

        var keptAs = original + "-7";
        var committed = Apply(
            catalog, changes, outcomes: [new StoreOutcome.SavedUnderNewId(original, keptAs, [new TermValues("theirs", "Theirs")])]);
        MarkSaved(workspace, changes, committed, byChangeSet);

        // Where the store kept the original, it is the user's pending deletion; the planned id holds the other app's
        // library; the library made meanwhile is its own; and the copy's reference names the kept original, a repair.
        Assert.True(workspace.Draft.Find(keptAs)!.PendingDelete);
        var theirs = workspace.Draft.Find(original)!;
        Assert.Equal(("Their notes", false, false), (theirs.Content.Name, theirs.PendingDelete, theirs.Unsaved));
        var made = workspace.Draft.Find(created)!;
        Assert.Equal((LibraryOrigin.Created, false, true), (made.Origin, made.PendingDelete, made.Unsaved));
        Assert.Equal([new TermValues("ship it", "Ship it")], ValuesOf(workspace, created));
        Assert.Equal(keptAs, workspace.Draft.Find(copy)!.Content.BasedOn);
        Assert.Equal([copy], workspace.PendingReferenceRepairs);
        Assert.Null(workspace.CopyOriginal(copy));

        // The follow-up writes the copy's reference to the kept original, and nothing else.
        var followUp = RepairsOf(workspace);
        var repair = Assert.Single(followUp.Writes);
        Assert.Equal((copy, keptAs), (repair.LibraryId, repair.Content!.BasedOn));
        Assert.Empty(followUp.Deletions);
        Assert.Empty(followUp.RecentlyDeletedActions);
        var repaired = Apply(committed, followUp);
        workspace.MarkSaved(followUp, repaired);
        Assert.False(workspace.HasPendingReferenceRepairs);

        // The user's Save then deletes the kept original and creates the new library beside it.
        var next = Capture(workspace);
        AssertPreImages(repaired, next);
        Assert.Equal(keptAs, Assert.Single(next.Deletions).LibraryId);
        Assert.DoesNotContain(next.Writes, write =>
            string.Equals(write.LibraryId, keptAs, StringComparison.OrdinalIgnoreCase)
            || string.Equals(write.LibraryId, original, StringComparison.OrdinalIgnoreCase));
        Assert.Null(next.Writes.Single(write => string.Equals(write.LibraryId, created, StringComparison.OrdinalIgnoreCase)).ExpectedPreImage);
        var after = Apply(repaired, next);
        workspace.MarkSaved(next, after);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Null(after.Find(keptAs));
        Assert.Equal("Their notes", after.Find(original)!.Content.Name);
        Assert.Equal([new TermValues("ship it", "Ship it")], after.Find(created)!.Content.Rows.Select(row => row.Values));
        Assert.Equal(keptAs, after.Find(copy)!.Content.BasedOn);
        Assert.Null(workspace.CopyOriginal(copy));
    }

    [Fact]
    public void D4_a_library_made_while_a_retained_follow_up_is_unresolved_stays_its_own_when_the_follow_up_settles()
    {
        // The retained Save is the repairs-only follow-up itself. Every id such a capture holds is one the committed
        // catalog holds, which new ids avoid through the catalog anyway, so this pins the settlement rather than the
        // reservation: the kept original deleted while the follow-up was unresolved stays a pending deletion, and a
        // library made meanwhile stays its own.
        var catalog = Standard();
        var workspace = PendingRepair(catalog, out var copy, out var keptAs, out var saved);
        var followUp = RepairsOf(workspace);
        workspace.DeleteLibrary(keptAs);
        FencedSaves(workspace, LibraryWorkspace.RecentCaptures + 4);
        Collect();
        var created = workspace.CreateLibrary();
        workspace.AddTerm(created, new TermValues("ship it", "Ship it"));
        Assert.DoesNotContain(created, saved.Libraries.Select(library => library.Content.Id), StringComparer.OrdinalIgnoreCase);

        var committed = Apply(saved, followUp);
        workspace.MarkSaved(followUp, committed);

        Assert.False(workspace.HasPendingReferenceRepairs);
        Assert.Equal(keptAs, committed.Find(copy)!.Content.BasedOn);
        Assert.True(workspace.Draft.Find(keptAs)!.PendingDelete);
        var made = workspace.Draft.Find(created)!;
        Assert.Equal((LibraryOrigin.Created, false, true), (made.Origin, made.PendingDelete, made.Unsaved));
        Assert.Null(workspace.CopyOriginal(copy));
        var next = Capture(workspace);
        AssertPreImages(committed, next);
        Assert.Equal(keptAs, Assert.Single(next.Deletions).LibraryId);
        Assert.Null(next.Writes.Single(write => string.Equals(write.LibraryId, created, StringComparison.OrdinalIgnoreCase)).ExpectedPreImage);
    }

    [Fact]
    public void D4_a_restore_asking_for_its_own_old_id_while_a_retained_save_restores_it_there_takes_another()
    {
        // A8 beyond the recent captures: the retained Save restores an entry under its original id; while it is
        // unresolved the user cancels that restore, which offers the entry again, and restores it again after more fenced
        // Saves than the workspace keeps recent captures. A restore takes its original id unless that is taken, and an
        // id the Save captured is taken: the Save may still commit the first restore there.
        var gone = Deleted("20260901T100000Z.gone.csv", "gone", "Gone", new TermValues("gone term", "Gone term"));
        var store = new Dictionary<string, RecentlyDeletedContent>(StringComparer.OrdinalIgnoreCase) { [gone.Entry.EntryName] = gone };
        var catalog = Catalog(
            [BuiltIn(GitHubId), BuiltIn(AzureId), Custom("team-terms", "Team terms", [new TermValues("kube", "Kubernetes")])],
            [GitHubId, "team-terms"],
            recentlyDeleted: [gone.Entry]);
        var workspace = Workspace(catalog);
        Assert.Equal("gone", workspace.RestoreDeleted(gone));
        var changes = Capture(workspace);

        workspace.DeleteLibrary("gone");
        Assert.Contains(workspace.Draft.RecentlyDeleted, entry => entry.EntryName == gone.Entry.EntryName);
        FencedSaves(workspace, LibraryWorkspace.RecentCaptures + 4);
        Collect();
        var again = workspace.RestoreDeleted(gone);
        Assert.NotEqual("gone", again, StringComparer.OrdinalIgnoreCase);

        // Settled as committed: the first restore is the user's pending deletion, and the second, whose entry that Save
        // consumed, is a new library with the entry's content (A8).
        var committed = Apply(catalog, changes, store);
        workspace.MarkSaved(changes, committed);
        Assert.True(workspace.Draft.Find("gone")!.PendingDelete);
        var restored = workspace.Draft.Find(again)!;
        Assert.Equal((LibraryOrigin.Created, false), (restored.Origin, restored.PendingDelete));
        Assert.Equal([new TermValues("gone term", "Gone term")], ValuesOf(workspace, again));
        var next = Capture(workspace);
        AssertPreImages(committed, next);
        Assert.Equal("gone", Assert.Single(next.Deletions).LibraryId);
        Assert.Empty(next.RecentlyDeletedActions);
        Assert.Null(next.Writes.Single(write => string.Equals(write.LibraryId, again, StringComparison.OrdinalIgnoreCase)).ExpectedPreImage);
    }

    [Theory]
    [InlineData("MarkSaved")]
    [InlineData("Rebase")]
    [InlineData("Reload")]
    public void D4_an_id_a_capture_held_stays_reserved_whoever_holds_its_change_set_until_the_history_ends(string ending)
    {
        // The reservation is deterministic: nothing holds the change set of the capture that held the id, many captures
        // came after it and a collection ran, and a new library still takes another id, so which id it takes never
        // depends on when a collection runs. Only the end of the draft's history releases it, since no capture taken
        // before it can be marked saved any more.
        var catalog = Standard();
        var workspace = Workspace(catalog);
        var first = workspace.CreateLibrary();
        workspace.AddTerm(first, new TermValues("ga", "general availability"));
        DroppedCapture(workspace);
        workspace.DeleteLibrary(first);
        DroppedCaptures(workspace, 3 * LibraryWorkspace.RecentCaptures);
        Collect();

        var meanwhile = workspace.CreateLibrary();
        Assert.NotEqual(first, meanwhile, StringComparer.OrdinalIgnoreCase);
        workspace.DeleteLibrary(meanwhile);

        switch (ending)
        {
            case "MarkSaved":
                var changes = Capture(workspace);
                workspace.MarkSaved(changes, Apply(catalog, changes));
                break;
            case "Rebase":
                workspace.Rebase(SavedElsewhere(catalog));
                break;
            default:
                workspace.Reload(SavedElsewhere(catalog));
                break;
        }

        Assert.Equal(first, workspace.CreateLibrary(), StringComparer.OrdinalIgnoreCase);
    }

    // A catalog newer than `catalog`, as a Save made elsewhere leaves it: team-terms' "kube" written "K8s".
    private static LibraryCatalog SavedElsewhere(LibraryCatalog catalog)
    {
        var elsewhere = Workspace(catalog);
        elsewhere.EditTerm("team-terms", RowIdOf(elsewhere, "team-terms", "kube"), new TermValues("kube", "K8s"));
        return Apply(catalog, Capture(elsewhere));
    }

    // Marks the Save of `changes` saved against `committed`, by the change set or by its revision, keeping the change set
    // referenced until the call returns, as W2 must with the revision form.
    private static void MarkSaved(LibraryWorkspace workspace, LibraryChangeSet changes, LibraryCatalog committed, bool byChangeSet)
    {
        if (byChangeSet)
        {
            workspace.MarkSaved(changes, committed);
        }
        else
        {
            workspace.MarkSaved(changes.DraftRevision, committed);
            GC.KeepAlive(changes);
        }
    }

    // `count` edits of team-terms, each followed by a Save the store fences because an earlier Save is unresolved: the
    // capture is taken and its change set dropped, as W2 drops a Save the store refused. Kept out of the calling test's
    // frame, so nothing there holds the dropped change sets. Returns the terms added.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<TermValues> FencedSaves(LibraryWorkspace workspace, int count)
    {
        var added = new List<TermValues>();
        for (var attempt = 1; attempt <= count; attempt++)
        {
            var values = new TermValues($"fenced {attempt}", $"Fenced {attempt}");
            Assert.True(workspace.AddTerm("team-terms", values).Applied);
            added.Add(values);
            Assert.NotNull(workspace.CaptureChangeSet().ChangeSet);
        }

        return added;
    }

    // `count` edits, each captured and its change set dropped at once. Returns the revisions captured.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long[] DroppedCaptures(LibraryWorkspace workspace, int count)
    {
        var revisions = new long[count];
        for (var i = 0; i < count; i++)
        {
            Assert.True(workspace.AddTerm("team-terms", new TermValues($"dropped {workspace.Revision}", "Dropped")).Applied);
            revisions[i] = workspace.CaptureChangeSet().ChangeSet!.DraftRevision;
        }

        return revisions;
    }

    // A capture of the draft as it is, its change set dropped at once. Returns the revision captured.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DroppedCapture(LibraryWorkspace workspace) => workspace.CaptureChangeSet().ChangeSet!.DraftRevision;

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // Every edit made while the Save was unresolved is the draft's and unsaved, and the next Save writes it.
    private static void AssertStillUnsaved(LibraryWorkspace workspace, LibraryCatalog committed, IReadOnlyList<TermValues> later)
    {
        var rows = ValuesOf(workspace, "team-terms");
        Assert.All(later, values => Assert.Contains(values, rows));
        Assert.DoesNotContain(committed.Find("team-terms")!.Content.Rows, row => later.Contains(row.Values));
        Assert.Contains("team-terms", workspace.UnsavedLibraryIds);
        var next = Capture(workspace);
        AssertPreImages(committed, next);
        var written = next.Writes.Single(write => write.LibraryId == "team-terms").Content!.Rows.Select(row => row.Values).ToList();
        Assert.All(later, values => Assert.Contains(values, written));
        workspace.MarkSaved(next.DraftRevision, Apply(committed, next));
        Assert.False(workspace.HasUnsavedChanges);
    }
}
