using Scribe.Core.PostProcessing;

namespace Scribe.Core.Libraries;

/// <summary>
/// The built-in overlay: how a built-in's edits document is read and written, how it applies to the shipped library,
/// and how each editing command changes a built-in row. Implemented once, by the overlay stream; the storage stream
/// uses it to load and save documents, and the editor uses it for every change to a built-in row, so the rules live in
/// one place.
/// </summary>
/// <remarks>
/// Pure and thread-safe: no I/O, no clock, no logging. Row methods take and return rows of built-in libraries only
/// (a <see cref="TermOrigin.Custom"/> row is an <see cref="ArgumentException"/>), and never change the row passed in.
/// </remarks>
public interface IBuiltInLibraryOverlay
{
    /// <summary>
    /// Parses the edits document of <paramref name="libraryId"/>. Never throws on content: malformed bytes, a broken
    /// entry or another library's id are <see cref="LibraryFileState.Unreadable"/>; a version or intent this build does
    /// not know is <see cref="LibraryFileState.Newer"/>.
    /// </summary>
    BuiltInEditsReadResult ReadEdits(string libraryId, ReadOnlySpan<byte> bytes);

    /// <summary>
    /// The version 1 document for <paramref name="edits"/>, which <see cref="ReadEdits"/> reads back unchanged. Refuses,
    /// with <see cref="ArgumentException"/>, any document that would not: a blank library id, an empty or repeated key, an
    /// entry without the values its intent needs (<see cref="BuiltInTermEdit"/>), a null value string, or a library id,
    /// key or value string that is not well-formed UTF-16 (an unpaired surrogate), which the editor refuses first. It
    /// refuses rather than replacing a character, so nothing is written that cannot be read back; each case is a bug
    /// upstream, and writing it would pause the library at the next start.
    /// </summary>
    byte[] WriteEdits(BuiltInLibraryEdits edits);

    /// <summary>
    /// The effective rows of <paramref name="shipped"/> with <paramref name="edits"/> applied, in saved order: shipped
    /// rows in shipped order, then added and no-longer-shipped rows in document order. Applies the per-field upgrade
    /// merge and sets <see cref="LibraryRow.Review"/> where the user owes a decision. With no document, every row is
    /// <see cref="TermOrigin.Shipped"/>. An edit whose shipped row is gone keeps its authored values as
    /// <see cref="TermOrigin.NoLongerShipped"/>; an off entry whose row is gone stays in the document and shows no row,
    /// and <see cref="Collect"/> carries it over from the committed document. Null means the built-in has no document: a
    /// caller whose document exists but is unreadable, newer or awaiting release never passes null for it, which would
    /// bring back every row the user turned off; that built-in pauses or keeps its last content instead.
    /// </summary>
    IReadOnlyList<LibraryRow> Apply(DictionaryLibrary shipped, BuiltInLibraryEdits? edits);

    /// <summary>
    /// The row after the user's edit, authored from now on; the key never changes. An edit that changes nothing returns
    /// the row as it is (a shipped row does not become authored), and one that changes only
    /// <see cref="TermValues.Enabled"/> is <see cref="SetEnabled"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Authorship is per field (plan 3.3): the user's values (<see cref="BuiltInTermEdit.Value"/>, U) change only in the
    /// fields where <paramref name="values"/> differs from the row shown (<see cref="LibraryRow.Values"/>), and every other
    /// field keeps the U and the base (<see cref="BuiltInTermEdit.Base"/>, B) it had, so an inherited field stays
    /// inherited, U equal to B, and the shipped value keeps applying to it, upgrades included. The whole of U is never
    /// replaced by the values shown, which would pin every inherited field to today's shipped value.
    /// </para>
    /// <para>
    /// A field the edit changes takes the typed value as U and, in an edited entry, the shipped value in use now as B: the
    /// row returned shows exactly <paramref name="values"/>, nothing asks about that field until a later version changes
    /// it again, and a field typed back to the shipped value inherits again. A pinned entry keeps its B, which only Use
    /// updated values moves, and acknowledges the shipped value in use for the changed fields instead
    /// (<see cref="BuiltInTermEdit.Acknowledged"/>). Editing a shipped row creates the entry with B the shipped values and
    /// U equal to B except in the changed fields.
    /// </para>
    /// <para>
    /// Editing a turned-off row keeps the off authored: the edited entry it becomes has Enabled false against a base that
    /// is on, the value the row was turned off from, never the shipped value in use when that is off too, unless the edit
    /// itself turns the row on (its base is then the shipped values in use). So a later version that ships the row on
    /// does not turn it back on.
    /// </para>
    /// </remarks>
    LibraryRow Edit(LibraryRow row, TermValues values);

    /// <summary>
    /// Turn off term or Turn on term. Turning a shipped row off records an <see cref="BuiltInTermIntent.Off"/> entry,
    /// based on the shipped values. Turning an off row on removes its entry only when the shipped row is enabled, so the
    /// row is shipped again; when the shipped row is itself disabled at that moment, Turn on is the user's own choice
    /// against it and records an authored <see cref="BuiltInTermIntent.Edited"/> entry, based on the shipped values in
    /// use, whose user value turns <see cref="TermValues.Enabled"/> on and inherits every other field, as it does for a
    /// shipped row that ships disabled. On any other row the flag is changed like the other three values, per field
    /// (<see cref="Edit"/>).
    /// </summary>
    LibraryRow SetEnabled(LibraryRow row, bool enabled);

    /// <summary>
    /// Restore built-in values: the row back to the shipped values, <see cref="TermOrigin.Shipped"/>, its intent
    /// removed. Null for a row with no shipped counterpart (added, no longer shipped), which Delete term removes instead.
    /// </summary>
    LibraryRow? RestoreShipped(LibraryRow row);

    /// <summary>A row the user adds to a built-in, keyed by its spoken form.</summary>
    LibraryRow Add(TermValues values);

    /// <summary>Resolves the row's <see cref="LibraryRow.Review"/>; a row with none is returned unchanged.</summary>
    LibraryRow ResolveReview(LibraryRow row, TermReviewChoice choice);

    /// <summary>
    /// The edits document for a built-in whose effective rows are <paramref name="rows"/>, or null when nothing is
    /// authored (the document is removed). <paramref name="committed"/> is the document the draft started from
    /// (<see cref="CatalogLibrary.Edits"/>), because not every entry has a row (review finding A7): an entry of
    /// <paramref name="committed"/> whose key has no row in <paramref name="rows"/> is kept unchanged, which covers an
    /// off entry whose shipped row this version does not ship (it shows no row and applies again if the row returns)
    /// and a shipped row the caller left out (shipped rows are turned off, never deleted); the one exception is an
    /// added or no-longer-shipped entry, a row the user can delete, whose absence deletes it. Removing everything,
    /// inert entries included, is not a <see cref="Collect"/>: Restore all built-in values writes no document. Entries
    /// come in one order whatever the document held: the entries of shipped rows in shipped order, then the entries of
    /// rows this version does not ship in the order of <paramref name="rows"/>, then kept off entries whose shipped row
    /// is not in this version, in their committed order; so a document ordered by hand is reordered by the next Save.
    /// </summary>
    BuiltInLibraryEdits? Collect(DictionaryLibrary shipped, BuiltInLibraryEdits? committed, IReadOnlyList<LibraryRow> rows);

    /// <summary>The values the user authored in a document (edited, pinned and added rows), for keeping a retired built-in's work.</summary>
    IReadOnlyList<TermValues> AuthoredTerms(BuiltInLibraryEdits edits);
}
