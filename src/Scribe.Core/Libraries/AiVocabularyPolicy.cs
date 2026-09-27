namespace Scribe.Core.Libraries;

/// <summary>
/// Which libraries AI cleanup may carry as vocabulary (plan 3.7): one filter every outbound path shares, so the
/// glossary, the usage insight's labels and the revocation check can never disagree about a library.
/// </summary>
/// <remarks>
/// Permission fails closed at every step: an unhealthy state permits nothing, content the state never accepted is not
/// permitted, a lost state denies everything nobody chose again, and an unrecorded custom library takes Decision 2's
/// default for a discovered file. Pure and thread-safe.
/// </remarks>
public static class AiVocabularyPolicy
{
    /// <summary>
    /// Whether <paramref name="libraryId"/>, holding <paramref name="content"/>, may be sent to AI cleanup, in order: a
    /// state that is <see cref="LocalStateHealth.Unreadable"/> or <see cref="LocalStateHealth.Newer"/> permits nothing;
    /// content that does not match <see cref="LibraryLocalState.AcceptedContent"/> for that id (a custom library with no
    /// accepted entry or another hash, a built-in whose edits document is present and not the accepted one, a built-in
    /// with no document while an accepted entry remains) is not permitted (review findings A4 and, round 3, A5); an
    /// explicit <see cref="LibraryLocalState.AiPermissions"/> entry decides; with
    /// <see cref="LibraryLocalState.AiPermissionsLost"/> set, a library with no explicit entry is denied (A3); otherwise
    /// Decision 2's default for its kind: a built-in on, a custom library off, since every custom library is recorded when
    /// it is adopted or created and only an unrecorded file meets this default.
    /// </summary>
    /// <param name="state">The local state the choice is read from.</param>
    /// <param name="libraryId">The library's logical id.</param>
    /// <param name="builtIn">Whether it ships with Scribe.</param>
    /// <param name="content">
    /// The hash of the content it holds: a custom library's CSV, a built-in's edits document, or null for a built-in
    /// without one (whose shipped rows are Decision 2's, permitted only while no accepted entry names a document) and for
    /// a custom file whose bytes could not be read (never permitted).
    /// </param>
    public static bool IsPermitted(LibraryLocalState state, string libraryId, bool builtIn, LibraryContentHash? content)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(libraryId);
        return IsHealthy(state) && ContentIsAccepted(state, libraryId, builtIn, content) && IsChosen(state, libraryId, builtIn);
    }

    /// <summary>
    /// The scope of <paramref name="catalog"/>: every library that is enabled, usable (available, partly readable, or
    /// awaiting release with its committed content) and permitted by <see cref="IsPermitted"/>, each paired with the
    /// content its permission covers (review finding A12): the content the catalog holds (round 2, review finding A1).
    /// Since round 3 (A5) that is always the accepted hash of a permitted library, or none for a built-in with neither a
    /// document nor an accepted entry, so a built-in whose accepted document is gone is not in the scope at all; the
    /// pairing is kept as a second guard. What a request admitted now may carry.
    /// </summary>
    public static AiVocabularyScope ScopeOf(LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var state = catalog.LocalState;
        var permitted = new List<KeyValuePair<string, LibraryContentHash?>>();
        foreach (var library in catalog.Libraries)
        {
            var id = library.Content.Id;
            if (state.EnabledIds.Contains(id) &&
                LibraryTiers.IsUsable(library.State) &&
                IsPermitted(state, id, library.Content.BuiltIn, library.ContentHash))
            {
                permitted.Add(new(id, library.ContentHash));
            }
        }

        return new AiVocabularyScope(catalog.Generation, permitted);
    }

    /// <summary>
    /// Whether AI permission narrowed since <paramref name="admitted"/>: exactly <c>!current.Covers(admitted)</c>, so a
    /// library the admitted scope permitted that <paramref name="current"/> does not, or permits for other content, has
    /// narrowed it even when the library is still permitted (review finding A12).
    /// </summary>
    public static bool HasNarrowed(AiVocabularyScope admitted, AiVocabularyScope current)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentNullException.ThrowIfNull(current);
        return !current.Covers(admitted);
    }

    // The choice alone, without the content binding: a preview judges by it only the libraries whose content the draft's
    // Save writes, since that Save records the hash of every file it writes (round 2, part 3).
    internal static bool IsPermittedByChoice(LibraryLocalState state, string libraryId, bool builtIn) =>
        IsHealthy(state) && IsChosen(state, libraryId, builtIn);

    internal static LibraryContentHash? AcceptedContentOf(LibraryLocalState state, string libraryId) =>
        state.AcceptedContent.TryGetValue(libraryId, out var accepted) ? accepted : null;

    // Whether the content a library holds is the content the state's choices were made for (A4).
    internal static bool ContentIsAccepted(LibraryLocalState state, string libraryId, bool builtIn, LibraryContentHash? content)
    {
        var hasAccepted = state.AcceptedContent.TryGetValue(libraryId, out var accepted);
        if (builtIn)
        {
            // A built-in without an edits document supplies its shipped rows, which cannot change while Scribe runs, but an
            // accepted entry says the choices were made for a document it no longer has (deleted outside Scribe, or not
            // readable yet): fail closed until an adoption drops the entry or the user chooses again (round 3, A5).
            return content is { } document ? hasAccepted && accepted == document : !hasAccepted;
        }

        return hasAccepted && content is { } held && held == accepted;
    }

    private static bool IsHealthy(LibraryLocalState state) =>
        state.Health is LocalStateHealth.Absent or LocalStateHealth.Ok;

    private static bool IsChosen(LibraryLocalState state, string libraryId, bool builtIn)
    {
        if (state.AiPermissions.TryGetValue(libraryId, out var chosen))
        {
            return chosen;
        }

        if (state.AiPermissionsLost)
        {
            return false;
        }

        // Only an unrecorded file meets the custom default, which is Decision 2's for a discovered one.
        return LibraryDecisions.DefaultAiPermission(
            builtIn ? LibraryOrigin.Existing : LibraryOrigin.Discovered, builtIn, sourcePermission: null);
    }
}
