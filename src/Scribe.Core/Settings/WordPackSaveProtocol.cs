using Scribe.Core.Libraries;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Settings;

/// <summary>
/// Owns the word-pack part of Settings Save: capture on the owner thread, store I/O on workers, completion,
/// unknown-outcome settlement, repair follow-ups and publication acknowledgement.
/// </summary>
public sealed class WordPackSaveProtocol
{
    private readonly IWordPackSaveProtocolStore _store;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private WordPackSaveSession? _pending;
    private long _pendingGeneration;

    public WordPackSaveProtocol(IWordPackSaveProtocolStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public bool HasPendingSave => _pending is not null;

    public bool IsBusy => _operationGate.CurrentCount == 0;

    public async Task<WordPackSaveProtocolResult> SaveAsync(WordPackSaveProtocolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_operationGate.Wait(0))
        {
            return Unfinished();
        }

        try
        {
            if (_pending is not null)
            {
                var settlement = await TrySettlePendingCoreAsync(request).ConfigureAwait(true);
                if (_pending is not null)
                {
                    return Unfinished();
                }

                if (settlement.Message is not null && request.Workspace?.HasUnsavedChanges != true)
                {
                    return settlement;
                }
            }

            if (request.Validate?.Invoke() is { Count: > 0 } validationErrors)
            {
                return WordPackSaveProtocolResult.Error(validationErrors[0]);
            }

            WordPackSaveSession? session = null;
            if (request.Workspace?.HasUnsavedChanges == true)
            {
                var begin = WordPackSaveSession.Capture(request.Workspace);
                if (begin.Status == WordPackSaveBeginStatus.ValidationFailed)
                {
                    return WordPackSaveProtocolResult.Error("Fix the highlighted word pack problem, then save again.");
                }

                session = begin.Session;
            }

            var savedDraft = request.CaptureDraft();
            LibraryPrepareResult? prepared = null;
            if (session is not null)
            {
                prepared = await _store.PrepareAsync(session.Changes).ConfigureAwait(true);
                if (prepared.Status != LibraryPrepareStatus.Prepared || prepared.Save is null)
                {
                    if (prepared.Status == LibraryPrepareStatus.Stale)
                    {
                        var catalog = await _store.LoadCatalogAsync().ConfigureAwait(true);
                        session.Rebase(catalog);
                        request.OnWordPacksChanged?.Invoke(catalog);
                    }

                    return WordPackSaveProtocolResult.Error(PrepareMessage(prepared));
                }

                session.PreparedBy(prepared);
            }

            Exception? transactionError = null;
            LibrarySaveOutcome? outcome = null;
            var settingsCommitted = false;
            try
            {
                try
                {
                    request.CommitSettings(session?.Payload);
                    settingsCommitted = true;
                    request.OnSettingsCommitted?.Invoke();
                }
                catch (Exception ex)
                {
                    transactionError = ex;
                    if (session is null)
                    {
                        throw;
                    }
                }
            }
            finally
            {
                if (prepared?.Save is not null)
                {
                    outcome = await _store.CompleteAsync(prepared.Save).ConfigureAwait(true);
                }
            }

            string? standingWarning = null;
            if (session is not null && outcome is not null)
            {
                var wordPackResult = await ApplyOutcomeAsync(session, outcome, transactionError, request).ConfigureAwait(true);
                if (!wordPackResult.Success)
                {
                    return wordPackResult;
                }

                standingWarning = wordPackResult.Message;
            }
            else if (transactionError is not null)
            {
                throw transactionError;
            }

            if (!settingsCommitted)
            {
                return WordPackSaveProtocolResult.Error("Could not save settings.");
            }

            var acknowledgement = await AcknowledgePublicationAsync(request, savedDraft).ConfigureAwait(true);
            return acknowledgement.Success && standingWarning is not null
                ? WordPackSaveProtocolResult.Warning(standingWarning)
                : acknowledgement;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<WordPackSaveProtocolResult> TrySettlePendingAsync(WordPackSaveProtocolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_operationGate.Wait(0))
        {
            return Unfinished();
        }

        try
        {
            return await TrySettlePendingCoreAsync(request).ConfigureAwait(true);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<WordPackSaveProtocolResult> TrySettlePendingCoreAsync(WordPackSaveProtocolRequest request)
    {
        var pending = _pending;
        var pendingGeneration = _pendingGeneration;
        if (pending is null)
        {
            return WordPackSaveProtocolResult.SuccessResult();
        }

        LibraryCatalog catalog;
        try
        {
            catalog = await _store.LoadCatalogAsync().ConfigureAwait(true);
        }
        catch
        {
            return Unfinished();
        }

        if (!ReferenceEquals(_pending, pending) || _pendingGeneration != pendingGeneration)
        {
            return Unfinished();
        }

        if (catalog.Generation == pendingGeneration)
        {
            pending.MarkSaved(catalog);
            ClearPending();
            request.OnWordPacksChanged?.Invoke(catalog);
            var repairs = await SavePendingReferenceRepairsAsync(pending, request).ConfigureAwait(true);
            if (!repairs.Success)
            {
                return repairs;
            }

            return await AcknowledgePublicationAsync(request, request.CaptureDraft()).ConfigureAwait(true);
        }

        ClearPending();
        request.OnWordPacksChanged?.Invoke(catalog);
        return WordPackSaveProtocolResult.Warning("Your word pack changes weren't saved. Your edits are still here.");
    }

    private async Task<WordPackSaveProtocolResult> ApplyOutcomeAsync(
        WordPackSaveSession session,
        LibrarySaveOutcome outcome,
        Exception? transactionError,
        WordPackSaveProtocolRequest request)
    {
        switch (outcome.Status)
        {
            case LibrarySaveStatus.Applied:
            case LibrarySaveStatus.AppliedAwaitingRelease:
                var catalog = await _store.LoadCatalogAsync().ConfigureAwait(true);
                session.MarkSaved(catalog);
                request.OnWordPacksChanged?.Invoke(catalog);
                var repairs = await SavePendingReferenceRepairsAsync(session, request).ConfigureAwait(true);
                if (!repairs.Success)
                {
                    return repairs;
                }

                if (transactionError is not null)
                {
                    return WordPackSaveProtocolResult.Warning(
                        "Saved, but Settings hit an error after the word pack save. Your word pack changes are saved.");
                }

                if (outcome.Status == LibrarySaveStatus.AppliedAwaitingRelease)
                {
                    return WordPackSaveProtocolResult.SuccessResult(
                        "Saved. Close the word pack file in the other app so Scribe can finish.");
                }

                return WordPackSaveProtocolResult.SuccessResult();

            case LibrarySaveStatus.CommitUnknown:
                _pending = session;
                _pendingGeneration = session.PreparedGeneration;
                return WordPackSaveProtocolResult.Warning(
                    "Scribe couldn't confirm that your word pack changes were saved. It will finish saving them, and your edits stay here until it has.");

            case LibrarySaveStatus.Superseded:
            {
                var supersededCatalog = await _store.LoadCatalogAsync().ConfigureAwait(true);
                session.Rebase(supersededCatalog);
                request.OnWordPacksChanged?.Invoke(supersededCatalog);
                return WordPackSaveProtocolResult.Warning("Your word pack changes weren't saved. Your edits are still here.");
            }

            default:
                return WordPackSaveProtocolResult.Error("Couldn't save your changes. Your edits are still here.");
        }
    }

    private async Task<WordPackSaveProtocolResult> SavePendingReferenceRepairsAsync(WordPackSaveSession completed, WordPackSaveProtocolRequest request)
    {
        while (completed.Workspace.HasPendingReferenceRepairs)
        {
            var begin = completed.CaptureReferenceRepairs();
            if (begin.Status != WordPackSaveBeginStatus.Captured || begin.Session is null)
            {
                return WordPackSaveProtocolResult.Warning("Saved, but a word pack reference still needs to be updated.");
            }

            var repair = begin.Session;
            var prepare = await _store.PrepareAsync(repair.Changes).ConfigureAwait(true);
            if (prepare.Status != LibraryPrepareStatus.Prepared || prepare.Save is null)
            {
                return WordPackSaveProtocolResult.Warning("Saved, but a word pack reference still needs to be updated.");
            }

            repair.PreparedBy(prepare);
            Exception? transactionError = null;
            LibrarySaveOutcome repairOutcome;
            try
            {
                try
                {
                    _store.CommitRepair(repair.Payload);
                }
                catch (Exception ex)
                {
                    transactionError = ex;
                }
            }
            finally
            {
                repairOutcome = await _store.CompleteAsync(prepare.Save).ConfigureAwait(true);
            }

            if (repairOutcome.Status is LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease)
            {
                var catalog = await _store.LoadCatalogAsync().ConfigureAwait(true);
                repair.MarkSaved(catalog);
                request.OnWordPacksChanged?.Invoke(catalog);
                if (transactionError is not null)
                {
                    return WordPackSaveProtocolResult.Warning(
                        "Saved, but a word pack reference still needs to be updated.");
                }

                continue;
            }

            if (repairOutcome.Status == LibrarySaveStatus.CommitUnknown)
            {
                _pending = repair;
                _pendingGeneration = repair.PreparedGeneration;
            }

            return WordPackSaveProtocolResult.Warning("Saved, but a word pack reference still needs to be updated.");
        }

        return WordPackSaveProtocolResult.SuccessResult();
    }

    private static async Task<WordPackSaveProtocolResult> AcknowledgePublicationAsync(
        WordPackSaveProtocolRequest request,
        string savedDraft)
    {
        var firstRead = true;
        string Draft()
        {
            if (firstRead)
            {
                firstRead = false;
                return savedDraft;
            }

            return request.CurrentDraft();
        }

        var acknowledgement = StoredChangeAcknowledgement.Watch(request.ApplySettings(), Draft);
        var outcome = await acknowledgement.CompleteAsync().ConfigureAwait(true);
        return outcome switch
        {
            StoredChangeOutcome.InEffect => WordPackSaveProtocolResult.SuccessResult(),
            StoredChangeOutcome.NotInUseYet => WordPackSaveProtocolResult.Warning(
                VocabularyNotice.SavedButNotApplied("Settings saved")),
            _ => WordPackSaveProtocolResult.Warning(VocabularyNotice.SettingsChangedWhileSaving),
        };
    }

    private void ClearPending()
    {
        _pending = null;
        _pendingGeneration = 0;
    }

    private static string PrepareMessage(LibraryPrepareResult? result) =>
        result?.Status switch
        {
            LibraryPrepareStatus.PreviousSaveUnfinished when result.Failure == LibraryIoFailure.SharingViolation =>
                "A word pack file is open in another app, so Scribe can't finish saving. Close it there and try again.",
            LibraryPrepareStatus.PreviousSaveUnfinished =>
                "Scribe is still finishing an earlier save. Try again in a moment.",
            LibraryPrepareStatus.ReadOnly =>
                "Word packs were changed by a newer version of Scribe. Update Scribe to change them here.",
            LibraryPrepareStatus.OutsideEdit =>
                "A word pack changed outside Scribe. Reload saved version, or save your draft as a new word pack.",
            _ => "Couldn't save your changes. Your edits are still here.",
        };

    private static WordPackSaveProtocolResult Unfinished() =>
        WordPackSaveProtocolResult.Warning("Scribe is still finishing an earlier save. Try again in a moment.");
}

public interface IWordPackSaveProtocolStore
{
    Task<LibraryPrepareResult> PrepareAsync(LibraryChangeSet changes);

    Task<LibrarySaveOutcome> CompleteAsync(PreparedLibrarySave prepared);

    Task<LibraryCatalog> LoadCatalogAsync();

    void CommitRepair(LibrarySavePayload payload);
}

public sealed record WordPackSaveProtocolRequest(
    LibraryWorkspace? Workspace,
    Action<LibrarySavePayload?> CommitSettings,
    Func<Task<VocabularyRefresh>> ApplySettings,
    Func<string> CaptureDraft,
    Func<string> CurrentDraft,
    Func<IReadOnlyList<string>>? Validate = null,
    Action? OnSettingsCommitted = null,
    Action<LibraryCatalog>? OnWordPacksChanged = null);

public sealed record WordPackSaveProtocolResult(bool Success, string? Message, WordPackSaveProtocolSeverity Severity)
{
    public static WordPackSaveProtocolResult SuccessResult(string? message = null) =>
        new(true, message, WordPackSaveProtocolSeverity.Info);

    public static WordPackSaveProtocolResult Warning(string message) =>
        new(false, message, WordPackSaveProtocolSeverity.Warning);

    public static WordPackSaveProtocolResult Error(string message) =>
        new(false, message, WordPackSaveProtocolSeverity.Error);
}

public enum WordPackSaveProtocolSeverity
{
    Info,
    Warning,
    Error,
}
