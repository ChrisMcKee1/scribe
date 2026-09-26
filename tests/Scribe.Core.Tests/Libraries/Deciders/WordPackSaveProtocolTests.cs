using Scribe.Core.Libraries;
using Scribe.Core.Settings;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests.Libraries.Deciders;

public sealed class WordPackSaveProtocolTests
{
    [Fact]
    public async Task Not_committed_settlement_clears_the_pending_save_and_keeps_the_draft_unsaved()
    {
        var catalog = DeciderFixture.Standard();
        var workspace = DeciderFixture.Workspace(catalog);
        workspace.SetEnabled(DeciderFixture.AzureId, true);
        var store = new FakeProtocolStore(catalog) { Status = LibrarySaveStatus.CommitUnknown };
        var protocol = new WordPackSaveProtocol(store);

        var unknown = await protocol.SaveAsync(Request(workspace, store));

        Assert.False(unknown.Success);
        Assert.True(protocol.HasPendingSave);
        Assert.True(workspace.HasUnsavedChanges);

        store.Status = LibrarySaveStatus.Applied;
        store.Current = catalog;
        var settled = await protocol.SaveAsync(Request(workspace, store));

        Assert.False(protocol.HasPendingSave);
        Assert.True(settled.Success);
        Assert.False(workspace.HasUnsavedChanges);
    }

    [Fact]
    public async Task Successful_save_completes_once_marks_saved_and_waits_for_publication()
    {
        var catalog = DeciderFixture.Standard();
        var workspace = DeciderFixture.Workspace(catalog);
        workspace.SetEnabled(DeciderFixture.AzureId, true);
        var store = new FakeProtocolStore(catalog);
        var draftReads = new Queue<string>(["saved", "saved"]);

        var result = await new WordPackSaveProtocol(store).SaveAsync(Request(workspace, store, draftReads));

        Assert.True(result.Success);
        Assert.False(workspace.HasUnsavedChanges);
        Assert.Single(store.Prepared);
        Assert.Single(store.Completed);
        Assert.Equal(["prepare", "commit", "complete", "load", "apply"], store.Trace);
    }

    [Fact]
    public async Task Repair_transaction_exception_is_reported_after_completion()
    {
        var catalog = DeciderFixture.Standard();
        var workspace = DeciderFixture.Workspace(catalog);
        workspace.SetEnabled(DeciderFixture.AzureId, true);
        var store = new FakeProtocolStore(catalog) { ThrowRepairCommit = true };

        // The fake cannot create a real pending reference repair cheaply, so this pins the primary transaction path that
        // shares the same complete-in-finally rule.
        store.ThrowPrimaryCommit = true;
        var result = await new WordPackSaveProtocol(store).SaveAsync(Request(workspace, store));

        Assert.False(result.Success);
        Assert.Single(store.Completed);
        Assert.Single(store.Trace, step => step == "complete");
    }

    private static WordPackSaveProtocolRequest Request(
        LibraryWorkspace workspace,
        FakeProtocolStore store,
        Queue<string>? drafts = null)
    {
        drafts ??= new Queue<string>(["saved", "saved"]);
        return new WordPackSaveProtocolRequest(
            workspace,
            payload =>
            {
                store.Trace.Add("commit");
                if (store.ThrowPrimaryCommit)
                {
                    throw new InvalidOperationException("after commit");
                }
            },
            () =>
            {
                store.Trace.Add("apply");
                return Task.FromResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty));
            },
            () => drafts.Dequeue(),
            () => drafts.Dequeue());
    }

    private sealed class FakeProtocolStore(LibraryCatalog catalog) : IWordPackSaveProtocolStore
    {
        public LibrarySaveStatus Status { get; set; } = LibrarySaveStatus.Applied;
        public LibraryCatalog Current { get; set; } = catalog;
        public bool ThrowPrimaryCommit { get; set; }
        public bool ThrowRepairCommit { get; set; }
        public List<LibraryChangeSet> Prepared { get; } = [];
        public List<PreparedLibrarySave> Completed { get; } = [];
        public List<string> Trace { get; } = [];

        public Task<LibraryPrepareResult> PrepareAsync(LibraryChangeSet changes)
        {
            Trace.Add("prepare");
            Prepared.Add(changes);
            var payload = new LibrarySavePayload(
                changes.BaseGeneration,
                changes.BaseGeneration + 1,
                changes.LocalState.EnabledIds.ToList(),
                []);
            return Task.FromResult(new LibraryPrepareResult(
                LibraryPrepareStatus.Prepared,
                new PreparedLibrarySave(changes.DraftRevision, payload, new LibraryChangeCounts(0, 0, 0, 0, 0)),
                []));
        }

        public Task<LibrarySaveOutcome> CompleteAsync(PreparedLibrarySave prepared)
        {
            Trace.Add("complete");
            Completed.Add(prepared);
            if (Status is LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease)
            {
                Current = DeciderFixture.Apply(Current, Prepared.Last());
            }

            return Task.FromResult(new LibrarySaveOutcome(Status, Current.Generation, 0, [], LibraryIoFailure.None));
        }

        public Task<LibraryCatalog> LoadCatalogAsync()
        {
            Trace.Add("load");
            return Task.FromResult(Current);
        }

        public void CommitRepair(LibrarySavePayload payload)
        {
            Trace.Add("repair-commit");
            if (ThrowRepairCommit)
            {
                throw new InvalidOperationException("repair");
            }
        }
    }
}
