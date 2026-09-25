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

    /// <summary>The version 1 document for <paramref name="edits"/>, which <see cref="ReadEdits"/> reads back unchanged.</summary>
    byte[] WriteEdits(BuiltInLibraryEdits edits);

    /// <summary>
    /// The effective rows of <paramref name="shipped"/> with <paramref name="edits"/> applied, in saved order: shipped
    /// rows in shipped order, then added and no-longer-shipped rows in document order. Applies the per-field upgrade
    /// merge and sets <see cref="LibraryRow.Review"/> where the user owes a decision. With no document, every row is
    /// <see cref="TermOrigin.Shipped"/>. An edit whose shipped row is gone keeps its authored values as
    /// <see cref="TermOrigin.NoLongerShipped"/>; an off entry whose row is gone stays in the document and shows no row,
    /// and <see cref="Collect"/> carries it over from the committed document.
    /// </summary>
    IReadOnlyList<LibraryRow> Apply(DictionaryLibrary shipped, BuiltInLibraryEdits? edits);

    /// <summary>The row with <paramref name="values"/> as the user's values, authored from now on; the key never changes.</summary>
    LibraryRow Edit(LibraryRow row, TermValues values);

    /// <summary>Turn off term or Turn on term.</summary>
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
    /// inert entries included, is not a <see cref="Collect"/>: Restore all built-in values writes no document.
    /// </summary>
    BuiltInLibraryEdits? Collect(DictionaryLibrary shipped, BuiltInLibraryEdits? committed, IReadOnlyList<LibraryRow> rows);

    /// <summary>The values the user authored in a document (edited, pinned and added rows), for keeping a retired built-in's work.</summary>
    IReadOnlyList<TermValues> AuthoredTerms(BuiltInLibraryEdits edits);
}
