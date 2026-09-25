namespace Scribe.Core.Libraries;

/// <summary>One library as a draft holds it.</summary>
/// <param name="Content">Its draft content, in saved order.</param>
/// <param name="Origin">How it came to be in the draft.</param>
/// <param name="State">The file state the committed catalog reported (<see cref="LibraryFileState.Available"/> for a new library).</param>
/// <param name="PendingDelete">Deleted in the draft; moves to Recently deleted when the draft is saved.</param>
/// <param name="Unsaved">Differs from the committed catalog in content, metadata, enabled state or AI permission.</param>
/// <param name="FileName">
/// A custom library's physical file name, which is what it ranks by (review finding A16): the committed file's name
/// (<see cref="CatalogLibrary.FileName"/>, <c>github.csv</c> for a remapped <c>custom-github</c>), or for a library not
/// written yet the name it will be written as, <c>Id + ".csv"</c>. Null for a built-in, and null for a custom library
/// means <c>Id + ".csv"</c>.
/// </param>
/// <param name="WritesContent">
/// The Save of this draft revision writes this library's content: a custom file written; a built-in's edits document
/// written or removed, a Restore all built-in values that clears intents no row shows included; or a library created,
/// imported, duplicated or restored. False for a change of enabled state or AI permission alone, and for a pending
/// deletion. The workspace sets it at every revision to exactly what its capture would write, and a preview never infers
/// it (review finding A6 on the composition stream): the preview judges a library that writes its content by the draft's
/// choices, since that Save records the hash of what it writes, and composes every other library exactly as the
/// committed composition after the Save will.
/// </param>
public sealed record DraftLibrary(
    LibraryContent Content,
    LibraryOrigin Origin,
    LibraryFileState State,
    bool PendingDelete = false,
    bool Unsaved = false,
    string? FileName = null,
    bool WritesContent = false);

/// <summary>
/// The editor's view of every library at one revision: what the Libraries page shows and previews against, never what
/// dictation uses. Produced by the workspace (<c>Scribe.Core.Settings.LibraryWorkspace</c>) from a committed
/// <see cref="LibraryCatalog"/> plus the user's operations.
/// </summary>
/// <remarks>
/// A distinct type from <see cref="LibraryCatalog"/> on purpose (review finding R12): quick add, dictation and the
/// usage report accept committed state only, so a draft cannot be handed to them. Previews (statuses, badges, the
/// glossary hint, the overlap prompt) compose over a draft together with the committed catalog it was built from, and
/// each library's <see cref="DraftLibrary.WritesContent"/> tells them which content the Save writes; a preview computed
/// for one revision is discarded once the revision moves on. Immutable; the workspace hands out a new one per revision.
/// </remarks>
public sealed class LibraryDraft
{
    private readonly Dictionary<string, DraftLibrary> _byId;

    internal LibraryDraft(
        long revision,
        long baseGeneration,
        IReadOnlyList<DraftLibrary> libraries,
        LibraryLocalState localState,
        IReadOnlyList<RecentlyDeletedLibrary> recentlyDeleted)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(localState);
        ArgumentNullException.ThrowIfNull(recentlyDeleted);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentOutOfRangeException.ThrowIfNegative(baseGeneration);

        Revision = revision;
        BaseGeneration = baseGeneration;
        Libraries = [.. libraries];
        LocalState = localState;
        RecentlyDeleted = [.. recentlyDeleted];

        _byId = new Dictionary<string, DraftLibrary>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in Libraries)
        {
            ArgumentNullException.ThrowIfNull(library);
            _byId.TryAdd(library.Content.Id, library);
        }
    }

    /// <summary>The workspace revision this draft reflects; every operation, undo and redo moves it forward.</summary>
    public long Revision { get; }

    /// <summary>The committed generation the draft started from.</summary>
    public long BaseGeneration { get; }

    /// <summary>Every library in the draft, pending deletions included, in precedence order.</summary>
    public IReadOnlyList<DraftLibrary> Libraries { get; }

    /// <summary>The draft's enabled lists, AI permissions and legacy markers.</summary>
    public LibraryLocalState LocalState { get; }

    /// <summary>Recently deleted as the draft sees it: entries it restores or deletes permanently are left out.</summary>
    public IReadOnlyList<RecentlyDeletedLibrary> RecentlyDeleted { get; }

    /// <summary>The library with <paramref name="id"/> (case-insensitive), or null.</summary>
    public DraftLibrary? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out var library) ? library : null;
}
