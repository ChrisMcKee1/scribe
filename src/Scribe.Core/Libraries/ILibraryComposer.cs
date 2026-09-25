namespace Scribe.Core.Libraries;

/// <summary>
/// What the library service needs from composition and policy: the local state's persistence, the state it commits on
/// its own (adoption at the first start, files that appear from elsewhere or change outside Scribe, a lost state's
/// denial), the one composition runtime consumers read, and the revocation check. Implemented once, by the composition
/// stream, over the two policy points of Decision 1 (authored library terms beat shipped terms, with legacy markers)
/// and Decision 2 (which libraries AI cleanup receives by default).
/// </summary>
/// <remarks>
/// Pure and thread-safe: no I/O, no clock, no logging. Never throws on stored content: an unreadable or newer state is
/// reported through <see cref="LibraryLocalState.Health"/>, and permission then fails closed.
/// </remarks>
public interface ILibraryComposer
{
    /// <summary>
    /// The local state stored as the settings document's enabled list (<paramref name="documentEnabledIds"/>, null when
    /// no readable document holds it: a session on defaults) and the value of <see cref="LibrarySettingKeys.State"/>
    /// (null when the row is absent), for the libraries <paramref name="libraries"/> names (every library of the catalog;
    /// an unreadable file counts). An absent row reads as <see cref="LocalStateHealth.Absent"/> only when
    /// <paramref name="context"/> gives no sign it was lost (no stored generation, no repair record, no session on
    /// defaults, no witness file); otherwise it reads as <see cref="LocalStateHealth.Unreadable"/>, so a lost row never
    /// re-grants AI permission, on this start or the next.
    /// </summary>
    /// <remarks>
    /// The auxiliary row is this build's source of truth for which libraries are on, by logical id (review finding A17);
    /// the settings document's list is only the projection older builds read, and the row stores the projection this
    /// build last wrote beside it, in the same transaction as the list. Reading compares the two to apply what an older
    /// build changed since, conservatively, through <see cref="LibraryIdentity.LegacyId"/>: a legacy id an older build
    /// turned off turns off every library it stands for, and one it turned on turns them all on, their AI permission
    /// unchanged (a twin that was not permitted stays off for AI). An older build's Save writes only the ids its window
    /// lists, so a built-in id of the <c>scribe.</c> form, which is reserved for built-ins older builds do not ship, is
    /// not read as turned off by its absence. With no readable row (a first start, a lost or newer state), the document's
    /// list is read the same way, each legacy id standing for every library an older build loads under it, which is how a
    /// hand-placed twin takes the enabled state its stem had at the first start (decision 5). With no document list, no
    /// change is inferred: a readable row is used as stored, and otherwise nothing is on.
    /// </remarks>
    LibraryLocalState ReadLocalState(
        IReadOnlyList<string>? documentEnabledIds,
        string? storedState,
        IReadOnlyList<LibraryIdentity> libraries,
        LibraryStateContext context);

    /// <summary>
    /// <paramref name="state"/> encoded for the settings store by a commit whose physical changes take the libraries
    /// from <paramref name="librariesBefore"/> (the catalog's, before any file of the commit is installed) to
    /// <paramref name="librariesAfter"/> (minus deletions, plus creations and restores; an unreadable file counts in
    /// both), starting from <paramref name="startingState"/> (the catalog's <see cref="LibraryCatalog.LocalState"/>, which
    /// the Save's draft began from; null for a commit that moves no file, an adoption, whose two lists are the same).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The auxiliary row holds every enabled library by logical id, the source of truth (review finding A17), and the
    /// projection this commit writes into the settings document's list, which is what 0.4.3 and 0.4.2 read. The
    /// projection is downgrade-safe (A15 and A18): an older build turns on every library it loads under a listed id and
    /// sends all of them, and until the commit's files are all in place it may meet any mix of the files before and
    /// after. So libraries are grouped by <see cref="LibraryIdentity.LegacyId"/> over both lists, and a group's legacy id
    /// enters the projection only when every library in it (1) is still there after the commit (one the commit deletes
    /// makes the group unsafe), (2) is enabled in <paramref name="state"/> and permitted by it for AI with its accepted
    /// content, and (3), for a custom library whose content the commit rewrites (its accepted content differs from
    /// <paramref name="startingState"/>'s), was also permitted by <paramref name="startingState"/> with the content it
    /// held, because an older build may still load those bytes. An older build loads a built-in's shipped rows whatever
    /// its edits document holds, so (3) never applies to a built-in.
    /// </para>
    /// <para>
    /// Every other enabled library stays out of the projection, so an older build runs with it off, which loses nothing
    /// private. Completion writes no settings (decision 12), so a group kept out only because of this commit's physical
    /// changes (a deleted twin, bytes rewritten under a permission granted in the same Save) joins the projection at the
    /// next commit. An id no library has, before or after, stays in the document's list if it was there (it is in
    /// <see cref="LibraryLocalState.LegacyEnabledIds"/>) and is dropped from the stored library state. A
    /// <see cref="LocalStateHealth.Newer"/> state encodes with a null <see cref="LibraryStateEncoding.StateValue"/> and
    /// the document's list as it was read. <see cref="LibraryLocalState.AiPermissionsLost"/> is always encoded, so every
    /// commit carries a denial until the user confirms the choices.
    /// </para>
    /// </remarks>
    LibraryStateEncoding EncodeLocalState(
        LibraryLocalState state,
        LibraryLocalState? startingState,
        IReadOnlyList<LibraryIdentity> librariesBefore,
        IReadOnlyList<LibraryIdentity> librariesAfter);

    /// <summary>
    /// The state to commit because the stored state has not recorded what the catalog shows
    /// (<see cref="LibraryAdoptionReasons"/>: the first start, a discovered file, content that no longer matches
    /// <see cref="LibraryLocalState.AcceptedContent"/>, a lost or unreadable state), or null when there is nothing to
    /// record or nothing may be written now: a session on defaults, or a state from a newer version.
    /// </summary>
    LibraryAdoption? PlanAdoption(LibraryCatalog catalog, LibraryStateContext context);

    /// <summary>
    /// The runtime vocabulary of <paramref name="catalog"/>: winners under both decisions, and the AI subset. A library
    /// whose content does not match <see cref="LibraryLocalState.AcceptedContent"/> takes the defaults of a discovered
    /// one (AI off, legacy markers as at the upgrade) until an adoption records it.
    /// </summary>
    LibraryVocabulary ComposeVocabulary(LibraryCatalog catalog);

    /// <summary>
    /// Whether AI permission has narrowed since <paramref name="admitted"/>: exactly
    /// <c>!current.Covers(admitted)</c> (<see cref="AiVocabularyScope.Covers"/>), so a library it permitted that
    /// <paramref name="current"/> does not, or permits for other content, has narrowed it (review finding A12). A cleanup
    /// request carrying the admitted vocabulary must not be sent then.
    /// </summary>
    bool HasNarrowed(AiVocabularyScope admitted, AiVocabularyScope current);

    /// <summary>
    /// The scope that holds from the moment <paramref name="changes"/> is prepared until its Save completes: the scope of
    /// <paramref name="committed"/>, with the committed content of each library, minus every library the change set
    /// turns off, deletes or takes AI permission from. Nothing the change set grants is in it, and a library the change
    /// set writes new content for keeps its committed content here; completion then publishes the new content, which no
    /// longer covers requests admitted for the old. A widening waits for the commit, so revocation is immediate and fails
    /// closed (review finding Astra N5).
    /// </summary>
    AiVocabularyScope ScopeWhileSaving(LibraryCatalog committed, LibraryChangeSet changes);
}
