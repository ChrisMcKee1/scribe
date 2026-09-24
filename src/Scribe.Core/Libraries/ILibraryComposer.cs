namespace Scribe.Core.Libraries;

/// <summary>
/// What the library service needs from composition and policy: the local state's persistence, adoption at the first
/// start and when a file appears from elsewhere, the one composition runtime consumers read, and the revocation check.
/// Implemented once, by the composition stream, over the two policy points of Decision 1 (authored library terms beat
/// shipped terms, with legacy markers) and Decision 2 (which libraries AI cleanup receives by default).
/// </summary>
/// <remarks>
/// Pure and thread-safe: no I/O, no clock, no logging. Never throws on stored content: an unreadable or newer state is
/// reported through <see cref="LibraryLocalState.Health"/>, and permission then fails closed.
/// </remarks>
public interface ILibraryComposer
{
    /// <summary>
    /// The local state stored as the settings document's enabled list and the value of
    /// <see cref="LibrarySettingKeys.State"/> (null when the row is absent). An absent row reads as
    /// <see cref="LocalStateHealth.Absent"/> only when <paramref name="context"/> gives no sign it was lost; otherwise it
    /// reads as <see cref="LocalStateHealth.Unreadable"/>, so a lost row never re-grants AI permission.
    /// </summary>
    LibraryLocalState ReadLocalState(IReadOnlyList<string>? enabledLibraryIds, string? storedState, LibraryStateContext context);

    /// <summary>
    /// <paramref name="state"/> encoded for the settings store. An enabled library whose AI permission is off goes to the
    /// auxiliary list, not the document's, so an older build never sends it; <paramref name="isBuiltIn"/> answers a
    /// library's kind, or null for an id no library has right now (an unreadable file still counts as a library), which
    /// stays in the document's list if it was there and is dropped from the stored library state. A
    /// <see cref="LocalStateHealth.Newer"/> state encodes with a null <see cref="LibraryStateEncoding.StateValue"/> and
    /// the document's list as it was read.
    /// </summary>
    LibraryStateEncoding EncodeLocalState(LibraryLocalState state, Func<string, bool?> isBuiltIn);

    /// <summary>
    /// The state to commit for libraries the stored state has never recorded, or null when there are none or nothing
    /// may be adopted now (a session on defaults, an unreadable or newer state).
    /// </summary>
    LibraryAdoption? PlanAdoption(LibraryCatalog catalog, LibraryStateContext context);

    /// <summary>The runtime vocabulary of <paramref name="catalog"/>: winners under both decisions, and the AI subset.</summary>
    LibraryVocabulary ComposeVocabulary(LibraryCatalog catalog);

    /// <summary>
    /// Whether AI permission has narrowed since <paramref name="admitted"/>: a library it permitted that
    /// <paramref name="current"/> does not. A cleanup request carrying the admitted vocabulary must not be sent then.
    /// </summary>
    bool HasNarrowed(AiVocabularyScope admitted, AiVocabularyScope current);

    /// <summary>
    /// The scope that holds from the moment <paramref name="changes"/> is prepared until its Save completes: the scope of
    /// <paramref name="committed"/> minus every library the change set turns off, deletes or takes AI permission from.
    /// Nothing the change set grants is in it; a widening waits for the commit, so revocation is immediate and fails
    /// closed (review finding Astra N5).
    /// </summary>
    AiVocabularyScope ScopeWhileSaving(LibraryCatalog committed, LibraryChangeSet changes);
}
