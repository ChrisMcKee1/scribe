using Scribe.Core.Libraries;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Deciders;

public sealed class WordPackSaveSessionTests
{
    [Fact]
    public void Applied_save_marks_the_held_change_set_saved()
    {
        var catalog = DeciderFixture.Standard();
        var workspace = DeciderFixture.Workspace(catalog);
        workspace.SetEnabled(DeciderFixture.AzureId, true);
        var store = new FakeStore(catalog, LibrarySaveStatus.Applied);

        var begin = WordPackSaveSession.Begin(workspace, store);
        Assert.Equal(WordPackSaveBeginStatus.Prepared, begin.Status);
        Assert.True(workspace.HasUnsavedChanges);

        var completion = begin.Session!.Complete(settingsCommitted: true, _ => { });

        Assert.Equal(LibrarySaveStatus.Applied, completion.Outcome.Status);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Single(store.Prepared);
        Assert.Single(store.Completed);
    }

    [Theory]
    [InlineData(LibrarySaveStatus.NotCommitted)]
    [InlineData(LibrarySaveStatus.CommitUnknown)]
    public void Non_standing_save_keeps_the_draft_unsaved(LibrarySaveStatus status)
    {
        var catalog = DeciderFixture.Standard();
        var workspace = DeciderFixture.Workspace(catalog);
        workspace.SetEnabled(DeciderFixture.AzureId, true);
        var store = new FakeStore(catalog, status);

        var begin = WordPackSaveSession.Begin(workspace, store);
        var completion = begin.Session!.Complete(settingsCommitted: true, _ => { });

        Assert.Equal(status, completion.Outcome.Status);
        Assert.True(workspace.HasUnsavedChanges);
    }

    [Fact]
    public void Stale_prepare_rebases_without_marking_saved()
    {
        var catalog = DeciderFixture.Standard();
        var workspace = DeciderFixture.Workspace(catalog);
        workspace.SetEnabled(DeciderFixture.AzureId, true);
        var reloaded = DeciderFixture.Catalog(catalog.Libraries, enabled: [DeciderFixture.GitHubId, DeciderFixture.AzureId], generation: 4);
        var store = new FakeStore(reloaded, LibrarySaveStatus.Applied)
        {
            PrepareStatus = LibraryPrepareStatus.Stale,
        };

        var begin = WordPackSaveSession.Begin(workspace, store);

        Assert.Equal(WordPackSaveBeginStatus.NotPrepared, begin.Status);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Equal(4, workspace.Draft.BaseGeneration);
    }

    private sealed class FakeStore(LibraryCatalog committed, LibrarySaveStatus status) : ILibraryCatalogStore
    {
        public LibraryPrepareStatus PrepareStatus { get; init; } = LibraryPrepareStatus.Prepared;

        public List<LibraryChangeSet> Prepared { get; } = [];

        public List<PreparedLibrarySave> Completed { get; } = [];

        public LibraryCatalog Current { get; private set; } = committed;

        public LibraryCatalog LoadCatalog() => Current;

        public LibraryPrepareResult PrepareSave(LibraryChangeSet changes)
        {
            if (PrepareStatus != LibraryPrepareStatus.Prepared)
            {
                return new LibraryPrepareResult(PrepareStatus, null, []);
            }

            Prepared.Add(changes);
            var enabled = changes.LocalState.LegacyEnabledIds.Count == 0
                ? changes.LocalState.EnabledIds
                : changes.LocalState.LegacyEnabledIds;
            var payload = new LibrarySavePayload(
                changes.BaseGeneration,
                changes.BaseGeneration + 1,
                enabled.ToList(),
                []);
            return new LibraryPrepareResult(
                LibraryPrepareStatus.Prepared,
                new PreparedLibrarySave(changes.DraftRevision, payload, new LibraryChangeCounts(0, 0, 0, 0, 0)),
                []);
        }

        public LibrarySaveOutcome CompleteSave(PreparedLibrarySave prepared)
        {
            Completed.Add(prepared);
            if (status is LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease)
            {
                Current = DeciderFixture.Apply(Current, Prepared.Last());
            }

            return new LibrarySaveOutcome(status, Current.Generation, 0, [], LibraryIoFailure.None);
        }

        public LibraryRecoveryResult Recover() => throw new NotSupportedException();

        public IReadOnlyList<string> FindOutsideEdits(LibraryCatalog catalog) => [];

        public RecentlyDeletedContent? ReadRecentlyDeleted(RecentlyDeletedLibrary entry) => null;
    }
}
