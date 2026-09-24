namespace Scribe.Core.Libraries;

/// <summary>
/// One library's content: its identity, metadata and effective rows in saved order. The same value describes a
/// committed library (<see cref="CatalogLibrary"/>), a draft library (<see cref="DraftLibrary"/>) and a custom library to
/// write (<see cref="LibraryWrite"/>); which of those it is comes from the container, never from the content.
/// </summary>
/// <remarks>
/// A built-in's name, category and description are the shipped ones and read-only; Duplicate is the fork. A custom
/// library's metadata is the user's. Record equality compares <see cref="Rows"/> by reference, so compare rows
/// element by element when content matters. A producer hands the rows list over and never changes it afterwards; the
/// containers (<see cref="LibraryCatalog"/>, <see cref="LibraryDraft"/>, <see cref="LibraryChangeSet"/>) keep copies of
/// their own lists.
/// </remarks>
/// <param name="Id">
/// The stable id the enabled lists, AI permissions and legacy markers refer to. Built-ins: the eleven 0.4.3 ids, or a
/// dotted <c>scribe.&lt;name&gt;</c> for any built-in added later. Custom: the file stem for libraries that existed
/// before this version, <c>custom-&lt;slug&gt;</c> for libraries created from now on, and <c>custom-&lt;stem&gt;</c>
/// for a hand-placed file whose stem is a built-in id. Compared case-insensitively.
/// </param>
/// <param name="BuiltIn">Whether the library ships with Scribe.</param>
/// <param name="Name">The display name; unique names are suggested, never forced on an untouched legacy name.</param>
/// <param name="Category">The category, "Custom" by default for custom libraries.</param>
/// <param name="Description">An optional description.</param>
/// <param name="Rows">The effective rows in saved order.</param>
/// <param name="BasedOn">For a custom library duplicated from another: the original's id (<c># based-on:</c>).</param>
public sealed record LibraryContent(
    string Id,
    bool BuiltIn,
    string Name,
    string Category,
    string? Description,
    IReadOnlyList<LibraryRow> Rows,
    string? BasedOn = null);

/// <summary>
/// The SHA-256 of a library file's bytes, as 64 lowercase hexadecimal digits: the pre-image the journal checks before
/// it replaces a file, and how an edit made outside Scribe is noticed.
/// </summary>
/// <param name="Value">The digest in lowercase hexadecimal.</param>
public readonly record struct LibraryContentHash(string Value);

/// <summary>Whether a library's file could be used, as the catalog found it.</summary>
public enum LibraryFileState
{
    /// <summary>Read and in use. A built-in with no edits document is available too.</summary>
    Available,

    /// <summary>
    /// The file could not be read or parsed. The library pauses: it supplies no rules and no AI vocabulary, keeps its
    /// enabled state, and its file is never rewritten until the user chooses a recovery.
    /// </summary>
    Unreadable,

    /// <summary>
    /// A built-in's edits document from a newer version of Scribe. The library pauses as for
    /// <see cref="Unreadable"/>, and the document is never rewritten by this version.
    /// </summary>
    Newer,

    /// <summary>
    /// The committed version of the file waits in a staged copy because another app keeps the target open; readers use
    /// the staged copy until the journal can finish.
    /// </summary>
    AwaitingRelease,
}

/// <summary>How a library came to be in a draft, which decides its defaults (Decision 2) and how it is written.</summary>
public enum LibraryOrigin
{
    /// <summary>Committed before this draft began.</summary>
    Existing,

    /// <summary>New library.</summary>
    Created,

    /// <summary>Import, as a new library.</summary>
    Imported,

    /// <summary>Duplicate of another library, built-in or custom.</summary>
    Duplicated,

    /// <summary>Restored from Recently deleted.</summary>
    Restored,

    /// <summary>A built-in that stopped shipping, whose authored rows the user kept as a new custom library.</summary>
    RetiredBuiltIn,

    /// <summary>The version of a file someone changed outside Scribe, kept as a new custom library so nothing is lost.</summary>
    ChangedOutside,

    /// <summary>
    /// A custom library file that appeared after the upgrade without this version creating it: placed by hand, or
    /// imported by an older build sharing the data folder.
    /// </summary>
    Discovered,
}
