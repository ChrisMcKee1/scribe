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
    private readonly SettingsApplicationDebt _debt;
    private WordPackSaveSession? _pending;

    // Whether the operation in progress has already tried to put the stored settings into effect, so the debt is not
    // retried a second time on its way out. One operation runs at a time, under the gate.
    private bool _attemptedApply;

    /// <param name="store">The word pack store the Save completes through.</param>
    /// <param name="debt">
    /// Who owns what a committed Save still owes dictation when its stored settings could not be put into effect
    /// (<see cref="SettingsApplicationDebt"/>). The host passes the one for the whole process, so a closed Settings window
    /// cannot take the debt with it. Without one the protocol keeps its own, for its lifetime only, with no background
    /// retry.
    /// </param>
    public WordPackSaveProtocol(IWordPackSaveProtocolStore store, SettingsApplicationDebt? debt = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _debt = debt ?? new SettingsApplicationDebt();
    }

    /// <summary>True while an earlier word pack Save's outcome is not known yet, which <see cref="TrySettlePendingAsync"/> settles.</summary>
    public bool HasPendingSave => _pending is not null;

    /// <summary>
    /// True while a Save that committed has not had its stored settings put into effect, because reading or applying them
    /// failed: the old settings keep running meanwhile. Every settlement and Save outcome tries again, whatever the
    /// outcome, and so does the background retry of the process-wide owner, with no window and no further Save; it ends
    /// with the first application that goes through. It never keeps a new Save from running, as
    /// <see cref="HasPendingSave"/> does.
    /// </summary>
    public bool ApplicationOwed => _debt.IsOwed;

    public bool IsBusy => _operationGate.CurrentCount == 0;

    /// <summary>
    /// Stores the Save: captures the word pack draft, prepares the journal, commits the settings, completes the journal and
    /// puts the stored settings into effect. Whatever it ends in, an application still owed from an earlier Save is tried
    /// again on the way out.
    /// </summary>
    public Task<WordPackSaveProtocolResult> SaveAsync(WordPackSaveProtocolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(request, () => SaveCoreAsync(request));
    }

    private async Task<WordPackSaveProtocolResult> SaveCoreAsync(WordPackSaveProtocolRequest request)
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
                    Rebase(session, catalog, request);
                }

                return FromNotice(WordPackNotices.FromPrepare(prepared), success: false, targetLibraryIds: prepared.OutsideEditIds);
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
                    // The shell's bookkeeping after the store failed, not the store: the document is stored, so what
                    // dictation runs on follows it before the failure goes up.
                    if (settingsCommitted)
                    {
                        await ApplyBeforeFailureAsync(request, savedDraft).ConfigureAwait(true);
                    }

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

        // The library payload commits in the document's own transaction, so a Save the library completed as committed
        // stored the document too, whatever the commit call did after its COMMIT (a failure there, such as a hook that
        // threw, says nothing of what is stored): its stored settings are put into effect like any committed Save's.
        if (!settingsCommitted && outcome is { Status: LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease })
        {
            settingsCommitted = true;
        }

        string? standingWarning = null;
        WordPackSaveProtocolResult? wordPackFailure = null;
        if (session is not null && outcome is not null)
        {
            WordPackSaveProtocolResult wordPackResult;
            try
            {
                wordPackResult = await ApplyOutcomeAsync(session, outcome, transactionError, request).ConfigureAwait(true);
            }
            catch when (settingsCommitted)
            {
                // A reference repair that could not even be prepared, a catalog that could not be read: whatever failed
                // after the store, the stored settings are what dictation runs on.
                await ApplyBeforeFailureAsync(request, savedDraft).ConfigureAwait(true);
                throw;
            }

            if (!wordPackResult.Success)
            {
                if (!settingsCommitted)
                {
                    return wordPackResult;
                }

                // A word pack problem after the settings were stored (a reference repair that could not be written, an
                // outcome not known yet) never keeps them from taking effect: they are applied below, and the word
                // pack result is what the Save reports.
                wordPackFailure = wordPackResult;
            }
            else
            {
                standingWarning = wordPackResult.Message;
            }
        }
        else if (transactionError is not null)
        {
            throw transactionError;
        }

        if (!settingsCommitted)
        {
            return WordPackSaveProtocolResult.Error("Could not save settings.");
        }

        var acknowledgement = await TryAcknowledgePublicationAsync(request, savedDraft).ConfigureAwait(true);
        if (acknowledgement is null)
        {
            // Committed, but the stored settings could not be read or applied: owed (see ApplicationOwed), and the Save is
            // reported by what is known of it, the word pack outcome when it has one, never as a failed commit.
            return wordPackFailure ?? NotApplied();
        }

        if (wordPackFailure is not null)
        {
            return acknowledgement.Success ? wordPackFailure : acknowledgement;
        }

        return acknowledgement.Success && standingWarning is not null
            ? WordPackSaveProtocolResult.Warning(standingWarning)
            : acknowledgement;
    }

    /// <summary>
    /// Finishes an earlier Save that is not finished: settles one whose outcome was not known (by that Save's own
    /// receipt from the library, <see cref="IWordPackSaveProtocolStore.SettleAsync"/>) and puts the stored settings of a
    /// Save that committed into effect when that is still owed (<see cref="ApplicationOwed"/>), whatever the settlement
    /// finds. Refused, with the usual notice, while another operation runs.
    /// </summary>
    public Task<WordPackSaveProtocolResult> TrySettlePendingAsync(WordPackSaveProtocolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(request, () => TrySettlePendingCoreAsync(request));
    }

    // One operation at a time. Whatever the operation ends in (a Save the library did not commit, a refusal, a validation
    // error, a settlement that kept nothing, an exception), an application still owed from an earlier Save is tried again on
    // the way out, unless the operation already tried it, and is never reported as a plain success while it is still owed.
    private async Task<WordPackSaveProtocolResult> RunAsync(
        WordPackSaveProtocolRequest request,
        Func<Task<WordPackSaveProtocolResult>> operation)
    {
        if (!_operationGate.Wait(0))
        {
            return Unfinished();
        }

        try
        {
            _attemptedApply = false;
            WordPackSaveProtocolResult result;
            try
            {
                result = await operation().ConfigureAwait(true);
            }
            catch
            {
                await PayOwedOnTheWayOutAsync(request).ConfigureAwait(true);
                throw;
            }

            await PayOwedOnTheWayOutAsync(request).ConfigureAwait(true);
            return _debt.IsOwed && result.Success ? NotApplied() : result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<WordPackSaveProtocolResult> TrySettlePendingCoreAsync(WordPackSaveProtocolRequest request)
    {
        var pending = _pending;
        if (pending is null)
        {
            // Nothing is unsettled; a Save that committed may still be waiting for its stored settings to take effect.
            if (!_debt.IsOwed)
            {
                return WordPackSaveProtocolResult.SuccessResult();
            }

            var owed = await TryAcknowledgePublicationAsync(request, request.CaptureDraft()).ConfigureAwait(true);
            return owed ?? NotApplied();
        }

        // Both places that wait for a settlement set a session that was prepared; one that was not has no Save to ask about.
        if (pending.Prepared is not { } preparedSave)
        {
            return Unfinished();
        }

        // Where the Save stands is read by that Save's own receipt, never by the generation now stored. Recovery can settle
        // it at its generation and an adoption commit the next before this asks, so a committed Save reads as one that
        // another commit superseded; and an adoption can reach the generation a discarded Save would have committed, so one
        // that never committed reads as saved. Nor does the generation say whether the Save was installed: recovery sets a
        // manifest aside unapplied when a redo image it needs is gone, whatever the generation, and the receipt says so.
        LibrarySaveOutcome receipt;
        LibraryCatalog catalog;
        try
        {
            receipt = await _store.SettleAsync(preparedSave).ConfigureAwait(true);
            if (receipt.Status == LibrarySaveStatus.CommitUnknown)
            {
                return Unfinished();
            }

            catalog = await _store.LoadCatalogAsync().ConfigureAwait(true);

            // The catalog read ran a recovery attempt, which can decide a manifest that was still unresolved when the receipt
            // was read: it is the answer after the read that says where the Save stands.
            (receipt, catalog) = await ConfirmAfterCatalogAsync(preparedSave, receipt, catalog).ConfigureAwait(true);
            if (receipt.Status == LibrarySaveStatus.CommitUnknown)
            {
                return Unfinished();
            }
        }
        catch (Exception ex)
        {
            _debt.Report(SettingsApplicationStep.Settlement, ex);
            return Unfinished();
        }

        if (!ReferenceEquals(_pending, pending))
        {
            return Unfinished();
        }

        if (receipt.Status is LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease)
        {
            pending.MarkSaved(catalog);
            ClearPending();
            request.OnWordPacksChanged?.Invoke(catalog);
            WordPackSaveProtocolResult repairs;
            try
            {
                repairs = await SavePendingReferenceRepairsAsync(pending, request).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // The repairs are a follow-up the draft still carries: a Save known to have committed is put into effect
                // whatever they run into, and the next Save writes them again. The failure is the host's to log by its shape.
                _debt.Report(SettingsApplicationStep.ReferenceRepair, ex);
                repairs = ReferenceStillNeedsUpdate();
            }

            // The Save is known to have committed, so it is put into effect whatever became of the follow-up reference
            // repairs, as a Save's own is.
            var acknowledgement = await TryAcknowledgePublicationAsync(request, request.CaptureDraft()).ConfigureAwait(true);
            if (acknowledgement is null)
            {
                return repairs.Success ? NotApplied() : repairs;
            }

            return repairs.Success || !acknowledgement.Success ? acknowledgement : repairs;
        }

        // Not saved: the draft keeps its edits on the catalog that is really stored now, so the workspace and the catalog the
        // shell composes the glossary against stay the same generation. An application still owed from an earlier Save is
        // tried on the way out of the operation, not here: nothing about this outcome says it was paid.
        Rebase(pending, catalog, request);
        ClearPending();
        return FromNotice(WordPackNotices.FromSettlement(WordPackSettlement.NotSaved));
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
                var status = outcome.Status;
                if (status == LibrarySaveStatus.AppliedAwaitingRelease && session.Prepared is { } prepared)
                {
                    // Revisable: the catalog read ran a recovery attempt that can have set this very manifest aside, so the
                    // draft is marked saved only on what the Save is asked after it, and keeps its edits when recovery
                    // decided otherwise.
                    var confirmed = await ConfirmAfterCatalogAsync(prepared, outcome, catalog).ConfigureAwait(true);
                    if (confirmed.Receipt.Status is not (LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease))
                    {
                        return await ApplyOutcomeAsync(session, confirmed.Receipt, transactionError, request).ConfigureAwait(true);
                    }

                    status = confirmed.Receipt.Status;
                    catalog = confirmed.Catalog;
                }

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

                if (status == LibrarySaveStatus.AppliedAwaitingRelease)
                {
                    return WordPackSaveProtocolResult.SuccessResult(WordPackNotices.FromSaveStatus(status).Text);
                }

                return WordPackSaveProtocolResult.SuccessResult();

            case LibrarySaveStatus.CommitUnknown:
                _pending = session;
                return FromNotice(WordPackNotices.FromSaveStatus(outcome.Status), success: false);

            case LibrarySaveStatus.Superseded:
            {
                var supersededCatalog = await _store.LoadCatalogAsync().ConfigureAwait(true);
                Rebase(session, supersededCatalog, request);
                return FromNotice(WordPackNotices.FromSaveStatus(outcome.Status));
            }

            default:
                return FromNotice(WordPackNotices.FromSaveStatus(LibrarySaveStatus.NotCommitted));
        }
    }

    private async Task<WordPackSaveProtocolResult> SavePendingReferenceRepairsAsync(WordPackSaveSession completed, WordPackSaveProtocolRequest request)
    {
        while (completed.Workspace.HasPendingReferenceRepairs)
        {
            var begin = completed.CaptureReferenceRepairs();
            if (begin.Status != WordPackSaveBeginStatus.Captured || begin.Session is null)
            {
                return ReferenceStillNeedsUpdate();
            }

            var repair = begin.Session;
            var prepare = await _store.PrepareAsync(repair.Changes).ConfigureAwait(true);
            if (prepare.Status != LibraryPrepareStatus.Prepared || prepare.Save is null)
            {
                return ReferenceStillNeedsUpdate();
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
                if (repairOutcome.Status == LibrarySaveStatus.AppliedAwaitingRelease)
                {
                    // The same revisable receipt as the Save's own: the repair is marked saved only on what it is asked
                    // after the catalog read, and a reference that is not repaired stays to be written again.
                    var confirmed = await ConfirmAfterCatalogAsync(prepare.Save, repairOutcome, catalog).ConfigureAwait(true);
                    if (confirmed.Receipt.Status is not (LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease))
                    {
                        if (confirmed.Receipt.Status == LibrarySaveStatus.CommitUnknown)
                        {
                            _pending = repair;
                        }
                        else if (confirmed.Receipt.Status == LibrarySaveStatus.Superseded)
                        {
                            Rebase(repair, confirmed.Catalog, request);
                        }

                        return ReferenceStillNeedsUpdate();
                    }

                    catalog = confirmed.Catalog;
                }

                repair.MarkSaved(catalog);
                request.OnWordPacksChanged?.Invoke(catalog);
                if (transactionError is not null)
                {
                    return ReferenceStillNeedsUpdate();
                }

                continue;
            }

            if (repairOutcome.Status == LibrarySaveStatus.CommitUnknown)
            {
                _pending = repair;
            }
            else if (repairOutcome.Status == LibrarySaveStatus.Superseded)
            {
                Rebase(repair, await _store.LoadCatalogAsync().ConfigureAwait(true), request);
            }

            return ReferenceStillNeedsUpdate();
        }

        return WordPackSaveProtocolResult.SuccessResult();
    }

    private static void Rebase(WordPackSaveSession session, LibraryCatalog catalog, WordPackSaveProtocolRequest request)
    {
        session.Rebase(catalog);
        request.OnWordPacksChanged?.Invoke(catalog);
    }

    // A receipt that is only AppliedAwaitingRelease is revisable (ILibraryCatalogStore.CompleteSave): the catalog read that
    // follows it runs a recovery attempt, which can finish that very manifest (Applied) or set it aside (Superseded), so the
    // receipt read before the catalog is stale evidence and never what marks a draft saved. Once the catalog is in hand the
    // Save is asked about again and the answer decides. A genuine fence, a manifest still waiting for a file to be released,
    // answers AppliedAwaitingRelease again and stays what it was. An answer that moved is followed by a catalog read made
    // after it, so what the draft is marked saved on, or rebased onto, is never older than the verdict; one that cannot be
    // told (CommitUnknown) is returned as it is, for the caller to keep the Save unsettled. A receipt that is final is not
    // asked about again.
    private async Task<(LibrarySaveOutcome Receipt, LibraryCatalog Catalog)> ConfirmAfterCatalogAsync(
        PreparedLibrarySave prepared,
        LibrarySaveOutcome receipt,
        LibraryCatalog catalog)
    {
        if (receipt.Status != LibrarySaveStatus.AppliedAwaitingRelease)
        {
            return (receipt, catalog);
        }

        var asked = await _store.SettleAsync(prepared).ConfigureAwait(true);
        if (asked.Status is LibrarySaveStatus.AppliedAwaitingRelease or LibrarySaveStatus.CommitUnknown)
        {
            return (asked, catalog);
        }

        return (asked, await _store.LoadCatalogAsync().ConfigureAwait(true));
    }

    // Puts the stored settings into effect on the way to a failure the caller is about to see: it is the failure that is
    // reported, so what this itself runs into is not (it stays owed, as for any committed Save).
    private async Task ApplyBeforeFailureAsync(WordPackSaveProtocolRequest request, string savedDraft)
    {
        await TryAcknowledgePublicationAsync(request, savedDraft).ConfigureAwait(true);
    }

    // Puts the stored settings of a Save that committed into effect and waits for the answer, once. The commit stands
    // whatever this finds, so a failure of the application itself (the stored settings cannot be read, applying them
    // throws, the answer faults) is not a failure of the Save: it returns null, the failure is the host's to log by its shape,
    // and the application stays owed to the process-wide owner, which also retries it in the background. Once the full
    // stored settings were handed over, an answer ends the scalar debt; their vocabulary generation has its own retries.
    private async Task<WordPackSaveProtocolResult?> TryAcknowledgePublicationAsync(WordPackSaveProtocolRequest request, string savedDraft)
    {
        _attemptedApply = true;
        var epoch = _debt.Begin();
        try
        {
            var acknowledgement = await AcknowledgePublicationAsync(request, savedDraft).ConfigureAwait(true);
            if (acknowledgement is not null)
            {
                _debt.Settle(epoch);
            }

            return acknowledgement;
        }
        catch (Exception ex)
        {
            _debt.Report(SettingsApplicationStep.Apply, ex);
            _debt.Owe();
            return null;
        }
    }

    private async Task<WordPackSaveProtocolResult?> AcknowledgePublicationAsync(
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

        var application = ApplyForAcknowledgement(request);
        var acknowledgement = StoredChangeAcknowledgement.Watch(application.Vocabulary, Draft);
        var outcome = await acknowledgement.CompleteAsync().ConfigureAwait(true);
        if (application.Reapplied != StoredSettingsReapplied.StoredSettings)
        {
            _debt.Report(SettingsApplicationStep.Apply, new InvalidOperationException("The stored settings cannot be used."));
            return null;
        }

        return outcome switch
        {
            StoredChangeOutcome.InEffect => WordPackSaveProtocolResult.SuccessResult(),
            StoredChangeOutcome.NotInUseYet => WordPackSaveProtocolResult.Warning(
                VocabularyNotice.SavedButNotApplied("Changes saved")),
            _ => WordPackSaveProtocolResult.Warning(VocabularyNotice.SettingsChangedWhileSaving),
        };
    }

    // The way out of every operation: an application still owed from an earlier Save is tried once more, by the application
    // alone, unless this operation already tried it. No draft is compared and nothing but the debt is reported: this
    // operation stored nothing the window could close over. On a failure it stays owed, and the process-wide owner keeps
    // retrying it.
    private async Task PayOwedOnTheWayOutAsync(WordPackSaveProtocolRequest request)
    {
        if (_attemptedApply || !_debt.IsOwed)
        {
            return;
        }

        _attemptedApply = true;
        var epoch = _debt.Begin();
        try
        {
            var application = ApplyForAcknowledgement(request);
            _ = await application.Vocabulary.ConfigureAwait(true);
            if (application.Reapplied == StoredSettingsReapplied.StoredSettings)
            {
                _debt.Settle(epoch);
            }
            else
            {
                _debt.Report(SettingsApplicationStep.Apply, new InvalidOperationException("The stored settings cannot be used."));
            }
        }
        catch (Exception ex)
        {
            _debt.Report(SettingsApplicationStep.Apply, ex);
            _debt.Owe();
        }
    }

    private StoredSettingsReapplyResult ApplyForAcknowledgement(WordPackSaveProtocolRequest request)
    {
        var application = request.ApplySettings();
        if (application.Reapplied != StoredSettingsReapplied.StoredSettings)
        {
            // Vocabulary recovery still runs, but it pays no scalar debt. Raising the epoch before awaiting it also
            // prevents the caller from settling that debt with the application epoch it took before this read.
            _debt.Owe();
        }

        return application;
    }

    private void ClearPending()
    {
        _pending = null;
    }

    private static WordPackSaveProtocolResult FromNotice(
        WordPackNotice notice,
        bool success = true,
        IReadOnlyList<string>? targetLibraryIds = null) =>
        notice.Severity switch
        {
            WordPackNoticeSeverity.Error => WordPackSaveProtocolResult.Error(notice.Text, notice.Actions, targetLibraryIds),
            WordPackNoticeSeverity.Warning => WordPackSaveProtocolResult.Warning(notice.Text, notice.Actions, targetLibraryIds),
            _ => success
                ? WordPackSaveProtocolResult.SuccessResult(notice.Text, notice.Actions, targetLibraryIds)
                : new WordPackSaveProtocolResult(false, notice.Text, WordPackSaveProtocolSeverity.Info, notice.Actions, targetLibraryIds),
        };

    private static WordPackSaveProtocolResult Unfinished() =>
        WordPackSaveProtocolResult.Warning("Scribe is still finishing an earlier save. Try again in a moment.");

    // What a Save that committed reports when its stored settings could not be put into effect: stored, and dictation is
    // still running on the previous settings (not only the previous vocabulary).
    private WordPackSaveProtocolResult NotApplied() =>
        WordPackSaveProtocolResult.Warning(VocabularyNotice.SettingsSavedButNotInUse(_debt.IsRetrying));

    private static WordPackSaveProtocolResult ReferenceStillNeedsUpdate() =>
        WordPackSaveProtocolResult.Warning("Saved, but a word pack reference still needs to be updated.");
}

public interface IWordPackSaveProtocolStore
{
    Task<LibraryPrepareResult> PrepareAsync(LibraryChangeSet changes);

    Task<LibrarySaveOutcome> CompleteAsync(PreparedLibrarySave prepared);

    /// <summary>
    /// Where a Save whose completion could not settle it (<see cref="LibrarySaveStatus.CommitUnknown"/>) stands now, by what
    /// happened to that Save itself: <see cref="LibrarySaveStatus.CommitUnknown"/> while that still cannot be told,
    /// otherwise whether it committed, however many commits an adoption has made since, which move the stored generation
    /// on and so say nothing of it. The library service reports a second completion of the same preparation this way
    /// (<see cref="ILibraryCatalogStore.CompleteSave"/>), which is what this does unless a store answers it otherwise.
    /// </summary>
    Task<LibrarySaveOutcome> SettleAsync(PreparedLibrarySave prepared) => CompleteAsync(prepared);

    Task<LibraryCatalog> LoadCatalogAsync();

    void CommitRepair(LibrarySavePayload payload);
}

public sealed record WordPackSaveProtocolRequest(
    LibraryWorkspace? Workspace,
    Action<LibrarySavePayload?> CommitSettings,
    Func<StoredSettingsReapplyResult> ApplySettings,
    Func<string> CaptureDraft,
    Func<string> CurrentDraft,
    Func<IReadOnlyList<string>>? Validate = null,
    Action? OnSettingsCommitted = null,
    Action<LibraryCatalog>? OnWordPacksChanged = null);

public sealed record WordPackSaveProtocolResult(
    bool Success,
    string? Message,
    WordPackSaveProtocolSeverity Severity,
    IReadOnlyList<WordPackNoticeAction>? Actions = null,
    IReadOnlyList<string>? TargetLibraryIds = null)
{
    public static WordPackSaveProtocolResult SuccessResult(
        string? message = null,
        IReadOnlyList<WordPackNoticeAction>? actions = null,
        IReadOnlyList<string>? targetLibraryIds = null) =>
        new(true, message, WordPackSaveProtocolSeverity.Info, actions, targetLibraryIds);

    public static WordPackSaveProtocolResult Warning(
        string message,
        IReadOnlyList<WordPackNoticeAction>? actions = null,
        IReadOnlyList<string>? targetLibraryIds = null) =>
        new(false, message, WordPackSaveProtocolSeverity.Warning, actions, targetLibraryIds);

    public static WordPackSaveProtocolResult Error(
        string message,
        IReadOnlyList<WordPackNoticeAction>? actions = null,
        IReadOnlyList<string>? targetLibraryIds = null) =>
        new(false, message, WordPackSaveProtocolSeverity.Error, actions, targetLibraryIds);
}

public enum WordPackSaveProtocolSeverity
{
    Info,
    Warning,
    Error,
}
