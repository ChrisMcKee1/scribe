namespace Scribe.Core.Libraries;

/// <summary>
/// One committed library, as the catalog loaded it.
/// </summary>
/// <param name="Content">Its content. A paused library (<see cref="LibraryFileState.Unreadable"/> or
/// <see cref="LibraryFileState.Newer"/>) has no rows here.</param>
/// <param name="State">Whether its file could be used.</param>
/// <param name="FileName">
/// Custom: the CSV's file name in the libraries folder, <c>Id + ".csv"</c> except for a hand-placed file remapped
/// because its stem is a built-in id. Built-in: null.
/// </param>
/// <param name="ContentHash">
/// The pre-image the journal checks before replacing the file: the custom CSV's bytes, or the built-in's edits document
/// (null when it has none).
/// </param>
/// <param name="Edits">A built-in's parsed edits document, or null when it has none or it could not be used.</param>
/// <param name="PreviousEditsAvailable">A built-in's last good edits document is kept beside it (Restore the previous copy).</param>
public sealed record CatalogLibrary(
    LibraryContent Content,
    LibraryFileState State,
    string? FileName,
    LibraryContentHash? ContentHash,
    BuiltInLibraryEdits? Edits = null,
    bool PreviousEditsAvailable = false);

/// <summary>A custom library in Recently deleted, restorable until the janitor removes it 30 days after its deletion.</summary>
/// <param name="EntryName">The file's name in <c>LibrariesDir\deleted</c>: the handle Restore and Delete permanently use.</param>
/// <param name="OriginalId">The id it had.</param>
/// <param name="Name">Its display name, read from its header, or the humanized id when it has none.</param>
/// <param name="TermCount">How many rows it holds.</param>
/// <param name="DeletedUtc">When the Save that deleted it committed, from the entry's name.</param>
/// <param name="State">Whether the entry could be read; an unreadable one can still be deleted permanently.</param>
/// <param name="ContentHash">The entry's bytes, for a restore that is also edited before Save.</param>
public sealed record RecentlyDeletedLibrary(
    string EntryName,
    string OriginalId,
    string Name,
    int TermCount,
    DateTimeOffset DeletedUtc,
    LibraryFileState State,
    LibraryContentHash? ContentHash = null);

/// <summary>
/// The edits document of a built-in the running version no longer ships. Inert: it is never applied or rewritten; a
/// notice offers its authored rows as a new custom library.
/// </summary>
/// <param name="LibraryId">The retired built-in's id.</param>
/// <param name="AuthoredTerms">The rows the user authored in it (edited, pinned and added values).</param>
/// <param name="ContentHash">The document's bytes.</param>
public sealed record RetiredBuiltInEdits(string LibraryId, IReadOnlyList<TermValues> AuthoredTerms, LibraryContentHash ContentHash);

/// <summary>
/// The committed library state at one generation: every library with its provenance, bases, intents, reviews and file
/// state, the local state (enabled lists, AI permissions, legacy markers), Recently deleted and the documents of
/// retired built-ins. What runtime consumers read, and what a draft starts from.
/// </summary>
/// <remarks>
/// <para>
/// Only the library service's loader builds one (and tests), so no caller can turn a draft into something that looks
/// committed: quick add, dictation and the usage report take committed state, and the constructor is internal to
/// Core for that reason (review finding R12). Immutable and safe to share across threads.
/// </para>
/// <para>
/// <see cref="Libraries"/> are in precedence order (<see cref="LibraryPrecedence"/>: built-ins in the frozen order,
/// then custom libraries by file name), and ids are unique, compared case-insensitively. Display order is the view's
/// (<see cref="LibraryOrdering"/>).
/// </para>
/// </remarks>
public sealed class LibraryCatalog
{
    private readonly Dictionary<string, CatalogLibrary> _byId;

    internal LibraryCatalog(
        long generation,
        IReadOnlyList<CatalogLibrary> libraries,
        LibraryLocalState localState,
        IReadOnlyList<RecentlyDeletedLibrary> recentlyDeleted,
        IReadOnlyList<RetiredBuiltInEdits> retiredBuiltInEdits,
        int filesAwaitingRelease)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(localState);
        ArgumentNullException.ThrowIfNull(recentlyDeleted);
        ArgumentNullException.ThrowIfNull(retiredBuiltInEdits);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentOutOfRangeException.ThrowIfNegative(filesAwaitingRelease);

        Generation = generation;
        Libraries = [.. libraries];
        LocalState = localState;
        RecentlyDeleted = [.. recentlyDeleted];
        RetiredBuiltInEdits = [.. retiredBuiltInEdits];
        FilesAwaitingRelease = filesAwaitingRelease;

        _byId = new Dictionary<string, CatalogLibrary>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in Libraries)
        {
            ArgumentNullException.ThrowIfNull(library);
            _byId.TryAdd(library.Content.Id, library);
        }
    }

    /// <summary>
    /// The committed library generation: the value of <see cref="LibrarySettingKeys.Generation"/>, or 0 when this
    /// version has never committed a library change.
    /// </summary>
    public long Generation { get; }

    /// <summary>Every library, built-in and custom, paused ones included, in precedence order.</summary>
    public IReadOnlyList<CatalogLibrary> Libraries { get; }

    /// <summary>The enabled lists, AI permissions and legacy markers committed with <see cref="Generation"/>.</summary>
    public LibraryLocalState LocalState { get; }

    /// <summary>Recently deleted custom libraries, newest first.</summary>
    public IReadOnlyList<RecentlyDeletedLibrary> RecentlyDeleted { get; }

    /// <summary>Edits documents of built-ins the running version no longer ships.</summary>
    public IReadOnlyList<RetiredBuiltInEdits> RetiredBuiltInEdits { get; }

    /// <summary>Files of the committed generation still waiting for another app to release their target.</summary>
    public int FilesAwaitingRelease { get; }

    /// <summary>The library with <paramref name="id"/> (case-insensitive), or null.</summary>
    public CatalogLibrary? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out var library) ? library : null;
}
