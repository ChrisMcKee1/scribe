using System.Runtime.CompilerServices;
using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using static Scribe.Core.Tests.Libraries.Deciders.DeciderFixture;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-4, GPT-6 Astra's A12: how long a capture lasts. MarkSaved finds a Save's capture by its revision, and while a Save is
/// unresolved (CommitUnknown) the page stays usable and every further Save the user presses is captured and then fenced by
/// the store (PreviousSaveUnfinished, contract 9.7), so more captures can come after it than the workspace keeps of recent
/// ones before a later load settles it as committed. A capture lives as long as its change set does: W2 holds the change
/// set of a Save until the Save settles, and a capture whose change set nobody holds is released once it is no longer
/// among the recent ones.
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
