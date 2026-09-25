namespace Scribe.Core.Libraries;

/// <summary>
/// One committed library, as the catalog loaded it.
/// </summary>
/// <param name="Content">
/// Its content. A paused library (<see cref="LibraryFileState.Unreadable"/> or <see cref="LibraryFileState.Newer"/>)
/// has no rows here; a <see cref="LibraryFileState.PartlyReadable"/> one has the rows that could be read; an
/// <see cref="LibraryFileState.AwaitingRelease"/> one has its committed content (from the journal's redo image, or the
/// content this process last read when another app holds the file open), or no rows at a start that has read neither,
/// or while its committed content cannot be read right now and nothing vouches for it (held back; review findings A13
/// and A15 on the storage stream).
/// </param>
/// <param name="State">Whether its file could be used.</param>
/// <param name="FileName">
/// Custom: the CSV's file name in the libraries folder, <c>Id + ".csv"</c> except for a hand-placed file whose id is not
/// its stem (remapped because its stem is a built-in id, or suffixed because its stem is an id recorded for another file
/// that still exists), which keeps its own name (no physical rename). It is the library's precedence
/// identity (review finding A16) and, without <c>.csv</c>, the id older builds load it as (A15). Built-in: null.
/// </param>
/// <param name="ContentHash">
/// The pre-image the journal checks before replacing the file, and the content identity compared with
/// <see cref="LibraryLocalState.AcceptedContent"/>: the custom CSV's bytes, or the built-in's edits document (null when
/// it has none). While a committed manifest is unresolved, the hash of the committed content it names; null while the
/// library is held back because that content cannot be read right now (review finding A13 on the storage stream). It is
/// also the hash a request scope pairs the library with while it is permitted
/// (<see cref="AiVocabularyScope.PermittedContent"/>), whatever the state accepted.
/// </param>
/// <param name="Edits">A built-in's parsed edits document, or null when it has none or it could not be used.</param>
/// <param name="PreviousEditsAvailable">A built-in's last good edits document is kept beside it (Restore the previous copy).</param>
/// <param name="ReadErrorCount">Rows of the file the codec could not use; nonzero exactly for a partly readable file.</param>
public sealed record CatalogLibrary(
    LibraryContent Content,
    LibraryFileState State,
    string? FileName,
    LibraryContentHash? ContentHash,
    BuiltInLibraryEdits? Edits = null,
    bool PreviousEditsAvailable = false,
    int ReadErrorCount = 0);

/// <summary>A custom library in Recently deleted, restorable until the janitor removes it 30 days after its deletion.</summary>
/// <param name="EntryName">The file's name in <c>LibrariesDir\deleted</c>: the handle Restore and Delete permanently use.</param>
/// <param name="OriginalId">The id it had.</param>
/// <param name="Name">Its display name, read from its header, or the humanized id when it has none.</param>
/// <param name="TermCount">How many rows it holds.</param>
/// <param name="DeletedUtc">When the Save that deleted it committed, from the entry's name.</param>
/// <param name="State">Whether the entry could be read; an unreadable one can still be deleted permanently.</param>
/// <param name="ContentHash">
/// The entry's bytes; set whenever the bytes could be read. A restore names it, and the journal restores only a file
/// that still matches it.
/// </param>
public sealed record RecentlyDeletedLibrary(
    string EntryName,
    string OriginalId,
    string Name,
    int TermCount,
    DateTimeOffset DeletedUtc,
    LibraryFileState State,
    LibraryContentHash? ContentHash = null);

/// <summary>
/// A Recently deleted entry's content, read by the store (<c>ILibraryCatalogStore.ReadRecentlyDeleted</c>) and checked
/// against the entry's hash, so the workspace can restore it into a draft, and preview, export or edit it before Save,
/// without any file I/O of its own (review finding A8). Immutable: the restore the Save commits names the same hash,
/// and the journal restores these bytes (edited, when the draft edited them), never whatever the entry holds by then.
/// </summary>
/// <param name="Entry">The entry, whose <see cref="RecentlyDeletedLibrary.ContentHash"/> the content matched.</param>
/// <param name="Content">The content, as a custom library with the entry's original id and every readable row.</param>
/// <param name="State">
/// <see cref="LibraryFileState.Available"/>, or <see cref="LibraryFileState.PartlyReadable"/> when some rows could not
/// be read; such a restore brings the file back byte for byte and cannot be edited in the same Save.
/// </param>
public sealed record RecentlyDeletedContent(
    RecentlyDeletedLibrary Entry,
    LibraryContent Content,
    LibraryFileState State = LibraryFileState.Available);

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
        int filesAwaitingRelease,
        IReadOnlyList<LibraryKeptVersion>? keptVersions = null,
        LibraryIoFailure pendingFailure = LibraryIoFailure.None)
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
        KeptVersions = keptVersions is null ? [] : [.. keptVersions];
        PendingFailure = pendingFailure;

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

    /// <summary>
    /// Files of the committed generation not in place yet (another app holds one open, access is denied, the disk is
    /// full). While it is above zero the journal reads the committed content from its redo images, so dictation is
    /// unaffected, but no library change can be saved until recovery has finished them. A redo image that cannot be read
    /// right now is taken from this process's last complete read of it, or from its file when that already holds the
    /// committed bytes; otherwise its library is held back (<see cref="LibraryFileState.AwaitingRelease"/> with no rows;
    /// review finding A13 on the storage stream).
    /// </summary>
    public int FilesAwaitingRelease { get; }

    /// <summary>Why files of the committed generation are not in place yet; <see cref="LibraryIoFailure.None"/> when all are.</summary>
    public LibraryIoFailure PendingFailure { get; }

    /// <summary>
    /// Versions this process's completions and recoveries kept rather than overwrote, oldest first, for the Libraries
    /// page's notices. In memory only: after a restart each kept version is simply a custom library that is off.
    /// </summary>
    public IReadOnlyList<LibraryKeptVersion> KeptVersions { get; }

    /// <summary>The library with <paramref name="id"/> (case-insensitive), or null.</summary>
    public CatalogLibrary? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out var library) ? library : null;
}
