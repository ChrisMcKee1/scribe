using Scribe.Core.Libraries;

namespace Scribe.Core.Settings;

/// <summary>
/// Holds the exact word-pack change set a Save captured and exposes separate dispatcher and worker steps.
/// </summary>
/// <remarks>
/// <see cref="LibraryWorkspace"/> is not thread-safe. This type touches it only in the dispatcher methods
/// (<see cref="Capture"/>, <see cref="MarkSaved"/>, <see cref="Rebase"/> and <see cref="CaptureReferenceRepairs"/>).
/// Store I/O is isolated in static worker helpers that use immutable change sets and preparations.
/// </remarks>
public sealed class WordPackSaveSession
{
    private WordPackSaveSession(LibraryWorkspace workspace, LibraryChangeSet changes)
    {
        Workspace = workspace;
        Changes = changes;
    }

    public LibraryWorkspace Workspace { get; }

    public LibraryChangeSet Changes { get; }

    public PreparedLibrarySave? Prepared { get; private set; }

    public long PreparedGeneration => Prepared?.Generation ?? Changes.BaseGeneration + 1;

    public LibrarySavePayload Payload =>
        Prepared?.Payload ?? throw new InvalidOperationException("The word pack Save was not prepared.");

    /// <summary>Dispatcher step: validate and capture the workspace without any file I/O.</summary>
    public static WordPackSaveBeginResult Capture(LibraryWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.HasUnsavedChanges)
        {
            return WordPackSaveBeginResult.NoChanges();
        }

        var capture = workspace.CaptureChangeSet();
        if (capture.Issues.Count > 0 || capture.ChangeSet is null)
        {
            return WordPackSaveBeginResult.ValidationFailed(capture.Issues);
        }

        return WordPackSaveBeginResult.Captured(new WordPackSaveSession(workspace, capture.ChangeSet));
    }

    /// <summary>Worker step: prepare immutable files and manifest.</summary>
    public static LibraryPrepareResult Prepare(ILibraryCatalogStore store, LibraryChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        return store.PrepareSave(changes);
    }

    /// <summary>Dispatcher step: attach a successful preparation to the captured change set.</summary>
    public void PreparedBy(LibraryPrepareResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status != LibraryPrepareStatus.Prepared || result.Save is null)
        {
            throw new InvalidOperationException("Only a prepared library Save can be attached.");
        }

        Prepared = result.Save;
    }

    /// <summary>Worker step: complete exactly one successful preparation.</summary>
    public static LibrarySaveOutcome Complete(ILibraryCatalogStore store, PreparedLibrarySave prepared)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(prepared);
        return store.CompleteSave(prepared);
    }

    /// <summary>Worker step: load the latest catalog.</summary>
    public static LibraryCatalog LoadCatalog(ILibraryCatalogStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.LoadCatalog();
    }

    /// <summary>Dispatcher step: mark this exact captured change set saved against a committed catalog.</summary>
    public void MarkSaved(LibraryCatalog catalog) => Workspace.MarkSaved(Changes, catalog);

    /// <summary>Dispatcher step: rebase the live draft onto a committed catalog.</summary>
    public void Rebase(LibraryCatalog catalog) => Workspace.Rebase(catalog);

    /// <summary>Dispatcher step: capture the follow-up reference repairs only.</summary>
    public WordPackSaveBeginResult CaptureReferenceRepairs()
    {
        if (!Workspace.HasPendingReferenceRepairs)
        {
            return WordPackSaveBeginResult.NoChanges();
        }

        var capture = Workspace.CaptureReferenceRepairs();
        if (capture.Issues.Count > 0 || capture.ChangeSet is null)
        {
            return WordPackSaveBeginResult.ValidationFailed(capture.Issues);
        }

        return WordPackSaveBeginResult.Captured(new WordPackSaveSession(Workspace, capture.ChangeSet));
    }
}

public sealed record WordPackSaveBeginResult(
    WordPackSaveBeginStatus Status,
    WordPackSaveSession? Session,
    IReadOnlyList<LibraryValidationIssue> Issues)
{
    public static WordPackSaveBeginResult Captured(WordPackSaveSession session) =>
        new(WordPackSaveBeginStatus.Captured, session, []);

    public static WordPackSaveBeginResult NoChanges() =>
        new(WordPackSaveBeginStatus.NoChanges, null, []);

    public static WordPackSaveBeginResult ValidationFailed(IReadOnlyList<LibraryValidationIssue> issues) =>
        new(WordPackSaveBeginStatus.ValidationFailed, null, issues);
}

public enum WordPackSaveBeginStatus
{
    Captured,
    NoChanges,
    ValidationFailed,
}
