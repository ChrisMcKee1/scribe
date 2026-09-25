namespace Scribe.Core.Libraries;

/// <summary>
/// What the user did to one row of a built-in library, persisted in that built-in's edits document. The intent is kept
/// until Restore built-in values or a reset removes it, whatever later versions ship (review finding R4).
/// </summary>
public enum BuiltInTermIntent
{
    /// <summary>The user changed at least one value of a shipped row.</summary>
    Edited,

    /// <summary>The user added a row the built-in did not ship.</summary>
    Added,

    /// <summary>
    /// Authored values that equal a shipped version's: kept authored, and never taking a later shipped change without
    /// asking.
    /// </summary>
    Pinned,

    /// <summary>The user turned a shipped row off. Survives the row's absence from a later version and applies again if it returns.</summary>
    Off,
}

/// <summary>
/// One entry of a built-in's edits document: the row it applies to, the user's intent, and the values the intent
/// needs. Field requirements by intent:
/// <list type="bullet">
/// <item><see cref="BuiltInTermIntent.Edited"/> and <see cref="BuiltInTermIntent.Pinned"/>: <see cref="Base"/> and
/// <see cref="Value"/> are both set.</item>
/// <item><see cref="BuiltInTermIntent.Off"/>: <see cref="Base"/> is set, <see cref="Value"/> is null (the row keeps the
/// shipped values, turned off).</item>
/// <item><see cref="BuiltInTermIntent.Added"/>: <see cref="Base"/> is null, <see cref="Value"/> is set.</item>
/// </list>
/// An entry that breaks these rules makes its document unreadable, which pauses that one library.
/// </summary>
/// <param name="Key">
/// The row's identity: the key of the original shipped spoken form (kept when the user changes Spoken), or for an added
/// row the key of the spoken form it was added with. Unique within a document.
/// </param>
/// <param name="Intent">What the user did.</param>
/// <param name="Base">
/// The shipped values the edit was made against. The per-field upgrade merge compares the new shipped values with it:
/// a field the user left equal to the base takes the new shipped value, a field the new version left equal to the base
/// keeps the user's, and a field both changed keeps the user's and asks (<see cref="TermReview"/>). In an edited entry
/// each field's base is the shipped value in use when the user last changed that field (a field never changed keeps the
/// base of the entry's first edit), so a later version asks about a field only when it changes that field again; an
/// edited entry made from an off one has a base whose Enabled is on, the value the row was turned off from, unless that
/// edit turned the row on, so the off stays authored. A pinned entry's base moves only with Use updated values.
/// </param>
/// <param name="Value">
/// The user's values (U). For an edited row, authorship is per field (plan 3.3): a field of U equal to <see cref="Base"/>
/// is inherited (the shipped value applies), and a field that differs is authored; an edit changes U only in the fields
/// the user changed, each against the shipped value in use as its base (<see cref="IBuiltInLibraryOverlay.Edit"/>).
/// </param>
/// <param name="Acknowledged">
/// The shipped values the user last reviewed with Keep my changes, so the same shipped change asks once; null until then.
/// An edit of a pinned entry also acknowledges the shipped value in use for each field it changes.
/// </param>
public sealed record BuiltInTermEdit(
    LibraryTermKey Key,
    BuiltInTermIntent Intent,
    TermValues? Base,
    TermValues? Value,
    TermValues? Acknowledged = null);

/// <summary>
/// The parsed edits document of one built-in library (<c>LibrariesDir\edits\&lt;id&gt;.json</c>): every authored row,
/// by key. The shipped library itself is never copied or changed; its effective rows are the shipped rows with these
/// entries applied. A built-in with no entries has no document.
/// </summary>
/// <remarks>
/// Entries keep their order: added rows are shown after the shipped rows in the order of their entries. Keys are
/// unique (<see cref="LibraryTermKey"/> equality). The JSON form, its version rules and what a newer or unreadable
/// document does are the overlay's, described in the W1b contracts.
/// </remarks>
/// <param name="LibraryId">The built-in's id. The document's file name is derived from it and must match it.</param>
/// <param name="Terms">The entries.</param>
public sealed record BuiltInLibraryEdits(string LibraryId, IReadOnlyList<BuiltInTermEdit> Terms)
{
    /// <summary>The document version this build writes and fully understands.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>What reading one edits document found.</summary>
/// <param name="State">
/// <see cref="LibraryFileState.Available"/> with <paramref name="Edits"/> set; <see cref="LibraryFileState.Unreadable"/>
/// for bytes that are not a valid version 1 document (malformed JSON, a broken entry, another library's id); or
/// <see cref="LibraryFileState.Newer"/> for a version this build does not know, or an intent it does not know. Either
/// of the last two pauses the library and the document is never rewritten.
/// </param>
/// <param name="Edits">The document, when <paramref name="State"/> is <see cref="LibraryFileState.Available"/>.</param>
/// <param name="Version">The version the document declared, when it declared one.</param>
public sealed record BuiltInEditsReadResult(LibraryFileState State, BuiltInLibraryEdits? Edits, int? Version);

/// <summary>A recovery a Save applies to a paused built-in's edits document.</summary>
public enum BuiltInEditsRecovery
{
    /// <summary>No recovery: the write carries the document to store, or none to remove it.</summary>
    None,

    /// <summary>Put the last good copy, kept beside the document, back in place.</summary>
    RestorePrevious,

    /// <summary>Rename the document aside with a time stamp, kept and never read again, and start with no edits.</summary>
    BackUpAndReset,
}
