using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class QuickAddSourcesTests
{
    [Fact]
    public void Forget_keeps_unsaved_correction_when_corrected_current_source_is_deleted()
    {
        var state = QuickAddSources.Forget(
            ["cloud pilot"],
            currentOriginal: "cloud pilot",
            deletedOriginal: "cloud pilot",
            hasSavableCorrection: true);

        Assert.Empty(state.Sources);
        Assert.True(state.CurrentRemoved);
        Assert.True(state.KeepCorrection);
        Assert.Equal(QuickAddSources.RemovedMessage, state.Message);
    }

    [Fact]
    public void Clear_history_keeps_savable_correction_but_removes_sources()
    {
        var state = QuickAddSources.Clear(hasSavableCorrection: true);

        Assert.Empty(state.Sources);
        Assert.True(state.CurrentRemoved);
        Assert.True(state.KeepCorrection);
        Assert.Equal(QuickAddSources.RemovedMessage, state.Message);
    }
    [Fact]
    public void Apply_deletion_forgets_source_after_it_left_the_ring()
    {
        var source = new Source("deleted", DateTimeOffset.UtcNow, AddedAtRevision: 0);
        var state = QuickAddSources.ApplyDeletion(
            [source, new Source("newer", DateTimeOffset.UtcNow, AddedAtRevision: 2)],
            source,
            new HistoryDeletion(
                HistoryDeletionKind.Entry,
                new HistoryEntry(1, DateTimeOffset.UtcNow, "deleted", 1, 1),
                Revision: 1),
            item => item.HistoryText,
            item => item.TimestampUtc,
            item => item.AddedAtRevision,
            hasSavableCorrection: true);

        Assert.Equal(["newer"], state.Sources.Select(item => item.HistoryText));
        Assert.True(state.CurrentRemoved);
        Assert.True(state.KeepCorrection);
        Assert.Equal(QuickAddSources.RemovedMessage, state.Message);
    }

    [Fact]
    public void Apply_deletion_forgets_retained_source_by_history_time()
    {
        var cutoff = DateTimeOffset.UtcNow;
        var old = new Source("old", cutoff.AddMinutes(-1), AddedAtRevision: 5);
        var state = QuickAddSources.ApplyDeletion(
            [old, new Source("new", cutoff.AddMinutes(1), AddedAtRevision: 0)],
            old,
            new HistoryDeletion(HistoryDeletionKind.OlderThan, CutoffUtc: cutoff, Revision: 6),
            item => item.HistoryText,
            item => item.TimestampUtc,
            item => item.AddedAtRevision,
            hasSavableCorrection: false);

        Assert.Equal(["new"], state.Sources.Select(item => item.HistoryText));
        Assert.True(state.CurrentRemoved);
        Assert.False(state.KeepCorrection);
    }

    [Fact]
    public void Apply_clear_keeps_source_added_after_clear_commit()
    {
        var source = new Source("after", DateTimeOffset.UtcNow, AddedAtRevision: 2);
        var state = QuickAddSources.ApplyDeletion(
            [source],
            source,
            new HistoryDeletion(HistoryDeletionKind.Clear, Revision: 2),
            item => item.HistoryText,
            item => item.TimestampUtc,
            item => item.AddedAtRevision,
            hasSavableCorrection: true);

        Assert.Same(source, Assert.Single(state.Sources));
        Assert.False(state.CurrentRemoved);
    }

    private sealed record Source(string HistoryText, DateTimeOffset TimestampUtc, long AddedAtRevision);
}
