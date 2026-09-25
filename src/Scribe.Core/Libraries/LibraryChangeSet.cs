namespace Scribe.Core.Libraries;

/// <summary>
/// One library file to write in a Save: a custom library's whole new content, or a built-in's whole new edits document.
/// </summary>
/// <remarks>
/// Exactly one shape per write:
/// <list type="bullet">
/// <item>Custom (<see cref="BuiltIn"/> false): <see cref="Content"/> is set and has <see cref="LibraryContent.BuiltIn"/>
/// false and the same id; <see cref="Edits"/> is null and <see cref="Recovery"/> is <see cref="BuiltInEditsRecovery.None"/>.
/// The journal encodes it as a managed CSV.</item>
/// <item>Built-in with <see cref="Recovery"/> <see cref="BuiltInEditsRecovery.None"/>: <see cref="Edits"/> is the new
/// document, or null to remove the document (every row back to its shipped values); <see cref="Content"/> is null.</item>
/// <item>Built-in with another <see cref="Recovery"/>: <see cref="Edits"/> and <see cref="Content"/> are null.</item>
/// </list>
/// </remarks>
/// <param name="LibraryId">The library's id.</param>
/// <param name="BuiltIn">Whether it is a built-in.</param>
/// <param name="Origin">How the library came to be in the draft; <see cref="LibraryOrigin.Existing"/> for a change to a committed one.</param>
/// <param name="ExpectedPreImage">
/// The hash of the file as the draft's catalog read it; null for a file that did not exist (a new custom library, a
/// built-in with no document yet). A different file on disk is an outside edit, and nothing is written over it.
/// </param>
/// <param name="Content">A custom library's whole content, in saved order.</param>
/// <param name="Edits">A built-in's whole new edits document, or null to remove it.</param>
/// <param name="Recovery">A recovery for a paused built-in's document.</param>
public sealed record LibraryWrite(
    string LibraryId,
    bool BuiltIn,
    LibraryOrigin Origin,
    LibraryContentHash? ExpectedPreImage,
    LibraryContent? Content = null,
    BuiltInLibraryEdits? Edits = null,
    BuiltInEditsRecovery Recovery = BuiltInEditsRecovery.None);

/// <summary>A committed custom library to move into Recently deleted.</summary>
/// <param name="LibraryId">Its id.</param>
/// <param name="FileName">Its file name in the libraries folder.</param>
/// <param name="ExpectedPreImage">The hash of the file as the catalog read it.</param>
public sealed record LibraryDeletion(string LibraryId, string FileName, LibraryContentHash ExpectedPreImage);

/// <summary>What a Save does to one Recently deleted entry.</summary>
public enum RecentlyDeletedActionKind
{
    /// <summary>Move the entry back as a custom library with <see cref="RecentlyDeletedAction.RestoreAsId"/>, both check boxes off.</summary>
    Restore,

    /// <summary>Delete the entry for good.</summary>
    DeletePermanently,
}

/// <summary>A staged action on a Recently deleted entry.</summary>
/// <param name="Kind">Restore or delete permanently.</param>
/// <param name="EntryName">The entry (<see cref="RecentlyDeletedLibrary.EntryName"/>).</param>
/// <param name="ExpectedHash">
/// The entry's hash as the workspace read it: for <see cref="RecentlyDeletedActionKind.Restore"/>, the hash of the
/// <see cref="RecentlyDeletedContent"/> it restored from; for <see cref="RecentlyDeletedActionKind.DeletePermanently"/>,
/// the entry's <see cref="RecentlyDeletedLibrary.ContentHash"/> (null for an entry that could not be read). Required for
/// a restore: the journal stages exactly those bytes at prepare, and refuses the Save as an outside edit when the entry
/// no longer matches them.
/// </param>
/// <param name="RestoreAsId">
/// For <see cref="RecentlyDeletedActionKind.Restore"/>: the id the library comes back with, its original id unless that
/// is taken now; required. Null otherwise.
/// </param>
public sealed record RecentlyDeletedAction(
    RecentlyDeletedActionKind Kind,
    string EntryName,
    LibraryContentHash? ExpectedHash = null,
    string? RestoreAsId = null);

/// <summary>
/// Everything one Save changes in the libraries, captured from a draft at one revision after validation: the files to
/// write, the libraries to delete, the Recently deleted actions and the complete new local state.
/// </summary>
/// <remarks>
/// <para>
/// Immutable and self-contained: the journal writes it without consulting the draft again, so edits the user makes
/// while it is being saved stay unsaved (<see cref="DraftRevision"/> is what the workspace marks saved). Only the
/// workspace builds one (the constructor is internal to Core), always from a validated draft; the journal refuses one
/// whose <see cref="BaseGeneration"/> is not the committed generation.
/// </para>
/// <para>
/// Ordering the journal relies on: no id is both written and deleted, and a restored library the draft also edited is a
/// <see cref="RecentlyDeletedActionKind.Restore"/> action plus a <see cref="LibraryWrite"/> for its
/// <see cref="RecentlyDeletedAction.RestoreAsId"/> whose pre-image is the entry's hash; the journal merges the two into
/// one restore of the edited content, so no file is the target of two operations in one manifest. Enabled state and AI
/// permission travel only in <see cref="LocalState"/>, never in a file. The journal adds to the local state it commits
/// the hash of every file it writes, creates or restores, custom CSVs and edits documents alike
/// (<see cref="LibraryLocalState.AcceptedContent"/>), and drops the entry of every edits document it removes (Restore all
/// built-in values, Back up and reset), so Scribe's own writes never read as an outside replacement or disappearance. A
/// change set whose local state is <see cref="LocalStateHealth.Newer"/> is refused
/// (<see cref="LibraryPrepareStatus.ReadOnly"/>): a newer version's library state makes the libraries read-only.
/// </para>
/// </remarks>
public sealed class LibraryChangeSet
{
    internal LibraryChangeSet(
        long baseGeneration,
        long draftRevision,
        IReadOnlyList<LibraryWrite> writes,
        IReadOnlyList<LibraryDeletion> deletions,
        IReadOnlyList<RecentlyDeletedAction> recentlyDeletedActions,
        LibraryLocalState localState,
        bool localStateChanged)
    {
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(deletions);
        ArgumentNullException.ThrowIfNull(recentlyDeletedActions);
        ArgumentNullException.ThrowIfNull(localState);
        ArgumentOutOfRangeException.ThrowIfNegative(baseGeneration);
        ArgumentOutOfRangeException.ThrowIfNegative(draftRevision);

        BaseGeneration = baseGeneration;
        DraftRevision = draftRevision;
        Writes = [.. writes];
        Deletions = [.. deletions];
        RecentlyDeletedActions = [.. recentlyDeletedActions];
        LocalState = localState;
        LocalStateChanged = localStateChanged;
    }

    /// <summary>The committed generation the draft was based on; the Save commits <c>BaseGeneration + 1</c>.</summary>
    public long BaseGeneration { get; }

    /// <summary>The draft revision captured, which the workspace marks saved once the Save commits.</summary>
    public long DraftRevision { get; }

    /// <summary>Files to write.</summary>
    public IReadOnlyList<LibraryWrite> Writes { get; }

    /// <summary>Custom libraries to move into Recently deleted.</summary>
    public IReadOnlyList<LibraryDeletion> Deletions { get; }

    /// <summary>Restores and permanent deletions of Recently deleted entries.</summary>
    public IReadOnlyList<RecentlyDeletedAction> RecentlyDeletedActions { get; }

    /// <summary>The complete local state to commit: enabled lists, AI permissions, legacy markers.</summary>
    public LibraryLocalState LocalState { get; }

    /// <summary>Whether <see cref="LocalState"/> differs from the committed one.</summary>
    public bool LocalStateChanged { get; }

    /// <summary>Nothing to save: no file, deletion, Recently deleted action or local state change.</summary>
    public bool IsEmpty =>
        Writes.Count == 0 && Deletions.Count == 0 && RecentlyDeletedActions.Count == 0 && !LocalStateChanged;
}
