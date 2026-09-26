using Scribe.Core.Libraries;

namespace Scribe.Core.Settings;

/// <summary>
/// Owns the Word packs Save sequence so the window only supplies the settings transaction.
/// </summary>
/// <remarks>
/// The important bit is the captured <see cref="LibraryChangeSet"/>. It is held across prepare, the settings commit,
/// completion and any unknown outcome, then passed back to <see cref="LibraryWorkspace.MarkSaved(LibraryChangeSet, LibraryCatalog)"/>
/// only when the library Save stands.
/// </remarks>
public sealed class WordPackSaveSession
{
    private readonly LibraryWorkspace _workspace;
    private readonly ILibraryCatalogStore _store;
    private readonly LibraryChangeSet _changes;
    private readonly PreparedLibrarySave _prepared;
    private bool _completed;

    private WordPackSaveSession(
        LibraryWorkspace workspace,
        ILibraryCatalogStore store,
        LibraryChangeSet changes,
        PreparedLibrarySave prepared)
    {
        _workspace = workspace;
        _store = store;
        _changes = changes;
        _prepared = prepared;
    }

    /// <summary>The payload to commit with the settings document.</summary>
    public LibrarySavePayload Payload => _prepared.Payload;

    /// <summary>The change set this session is holding until the Save settles.</summary>
    public LibraryChangeSet Changes => _changes;

    /// <summary>Capture and prepare a library Save, or return the reason a Save cannot start.</summary>
    public static WordPackSaveBeginResult Begin(LibraryWorkspace workspace, ILibraryCatalogStore store)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(store);

        if (!workspace.HasUnsavedChanges)
        {
            return WordPackSaveBeginResult.NoChanges();
        }

        var capture = workspace.CaptureChangeSet();
        if (capture.Issues.Count > 0 || capture.ChangeSet is null)
        {
            return WordPackSaveBeginResult.ValidationFailed(capture.Issues);
        }

        var prepare = store.PrepareSave(capture.ChangeSet);
        return prepare.Status switch
        {
            LibraryPrepareStatus.Prepared when prepare.Save is not null =>
                WordPackSaveBeginResult.Prepared(new WordPackSaveSession(workspace, store, capture.ChangeSet, prepare.Save)),
            LibraryPrepareStatus.NothingToSave => WordPackSaveBeginResult.NoChanges(),
            LibraryPrepareStatus.Stale => RebaseAndReport(workspace, store, prepare),
            _ => WordPackSaveBeginResult.NotPrepared(prepare),
        };
    }

    /// <summary>
    /// Complete the prepared library Save after the settings transaction has either committed the payload or failed.
    /// </summary>
    /// <param name="settingsCommitted">Whether the settings transaction returned successfully.</param>
    /// <param name="commitFollowUp">
    /// Commits a reference-repair payload after a standing Save. It must use the same settings transaction path as the
    /// user's Save, but without recapturing the user's later edits.
    /// </param>
    public WordPackSaveCompletion Complete(bool settingsCommitted, Action<LibrarySavePayload> commitFollowUp)
    {
        ArgumentNullException.ThrowIfNull(commitFollowUp);
        if (_completed)
        {
            throw new InvalidOperationException("This word pack Save session was already completed.");
        }

        _completed = true;
        var outcome = _store.CompleteSave(_prepared);
        if (!settingsCommitted)
        {
            return WordPackSaveCompletion.FromOutcome(outcome);
        }

        switch (outcome.Status)
        {
            case LibrarySaveStatus.Applied:
            case LibrarySaveStatus.AppliedAwaitingRelease:
                var catalog = _store.LoadCatalog();
                _workspace.MarkSaved(_changes, catalog);
                return CompleteReferenceRepairs(commitFollowUp, outcome);

            case LibrarySaveStatus.Superseded:
                _workspace.Rebase(_store.LoadCatalog());
                return WordPackSaveCompletion.FromOutcome(outcome);

            case LibrarySaveStatus.NotCommitted:
            case LibrarySaveStatus.CommitUnknown:
            default:
                return WordPackSaveCompletion.FromOutcome(outcome);
        }
    }

    private static WordPackSaveBeginResult RebaseAndReport(
        LibraryWorkspace workspace,
        ILibraryCatalogStore store,
        LibraryPrepareResult prepare)
    {
        workspace.Rebase(store.LoadCatalog());
        return WordPackSaveBeginResult.NotPrepared(prepare);
    }

    private WordPackSaveCompletion CompleteReferenceRepairs(
        Action<LibrarySavePayload> commitFollowUp,
        LibrarySaveOutcome original)
    {
        while (_workspace.HasPendingReferenceRepairs)
        {
            var capture = _workspace.CaptureReferenceRepairs();
            if (capture.Issues.Count > 0 || capture.ChangeSet is null)
            {
                return WordPackSaveCompletion.RepairPending(original, capture.Issues);
            }

            var prepare = _store.PrepareSave(capture.ChangeSet);
            if (prepare.Status != LibraryPrepareStatus.Prepared || prepare.Save is null)
            {
                return WordPackSaveCompletion.RepairPending(original, prepare);
            }

            var committed = false;
            LibrarySaveOutcome? repairOutcome = null;
            try
            {
                commitFollowUp(prepare.Save.Payload);
                committed = true;
            }
            finally
            {
                repairOutcome = _store.CompleteSave(prepare.Save);
            }

            if (committed && repairOutcome.Status is LibrarySaveStatus.Applied or LibrarySaveStatus.AppliedAwaitingRelease)
            {
                _workspace.MarkSaved(capture.ChangeSet, _store.LoadCatalog());
            }
            else
            {
                return WordPackSaveCompletion.RepairPending(original, repairOutcome);
            }
        }

        return WordPackSaveCompletion.FromOutcome(original);
    }
}

public sealed record WordPackSaveBeginResult(
    WordPackSaveBeginStatus Status,
    WordPackSaveSession? Session,
    LibraryPrepareResult? PrepareResult,
    IReadOnlyList<LibraryValidationIssue> Issues)
{
    public static WordPackSaveBeginResult Prepared(WordPackSaveSession session) =>
        new(WordPackSaveBeginStatus.Prepared, session, null, []);

    public static WordPackSaveBeginResult NoChanges() =>
        new(WordPackSaveBeginStatus.NoChanges, null, null, []);

    public static WordPackSaveBeginResult ValidationFailed(IReadOnlyList<LibraryValidationIssue> issues) =>
        new(WordPackSaveBeginStatus.ValidationFailed, null, null, issues);

    public static WordPackSaveBeginResult NotPrepared(LibraryPrepareResult result) =>
        new(WordPackSaveBeginStatus.NotPrepared, null, result, []);
}

public enum WordPackSaveBeginStatus
{
    Prepared,
    NoChanges,
    ValidationFailed,
    NotPrepared,
}

public sealed record WordPackSaveCompletion(
    LibrarySaveOutcome Outcome,
    bool ReferenceRepairPending,
    LibraryPrepareResult? RepairPrepareResult = null,
    LibrarySaveOutcome? RepairOutcome = null,
    IReadOnlyList<LibraryValidationIssue>? RepairIssues = null)
{
    public static WordPackSaveCompletion FromOutcome(LibrarySaveOutcome outcome) =>
        new(outcome, ReferenceRepairPending: false);

    public static WordPackSaveCompletion RepairPending(LibrarySaveOutcome original, LibraryPrepareResult prepare) =>
        new(original, ReferenceRepairPending: true, RepairPrepareResult: prepare);

    public static WordPackSaveCompletion RepairPending(LibrarySaveOutcome original, LibrarySaveOutcome repairOutcome) =>
        new(original, ReferenceRepairPending: true, RepairOutcome: repairOutcome);

    public static WordPackSaveCompletion RepairPending(
        LibrarySaveOutcome original,
        IReadOnlyList<LibraryValidationIssue> issues) =>
        new(original, ReferenceRepairPending: true, RepairIssues: issues);
}
