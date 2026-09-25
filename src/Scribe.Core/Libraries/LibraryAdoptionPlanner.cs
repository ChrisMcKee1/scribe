namespace Scribe.Core.Libraries;

/// <summary>
/// The library state the service commits on its own (W1b contracts 3.3.4): what the stored state has not recorded about
/// the catalog, for each reason that applies. Deterministic from the catalog, its local state and the context, so a
/// start whose commit failed or had to wait plans the same again and uses the plan in memory meanwhile.
/// </summary>
internal static class LibraryAdoptionPlanner
{
    /// <summary>
    /// The adoption for <paramref name="catalog"/>, or null when there is nothing to record, or nothing may be written
    /// now: a session on defaults, or a state from a newer version.
    /// </summary>
    /// <param name="catalog">The catalog, with the local state as <see cref="ILibraryComposer.ReadLocalState"/> read it.</param>
    /// <param name="context">What the service knows about this start.</param>
    /// <param name="defaultAiPermission">Decision 2 (<see cref="LibraryDecisions.DefaultAiPermission"/>).</param>
    public static LibraryAdoption? Plan(
        LibraryCatalog catalog, LibraryStateContext context, Func<LibraryOrigin, bool, bool?, bool> defaultAiPermission)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(defaultAiPermission);
        var state = catalog.LocalState;
        if (context.RunningOnDefaults || state.Health == LocalStateHealth.Newer)
        {
            return null;
        }

        var shipped = LibraryTiers.ShippedValues(catalog.Libraries.Where(l => l.Content.BuiltIn).Select(l => l.Content));

        // An absent row reads as a first start only when nothing says a row was lost; a state that says otherwise is
        // treated as lost here too, so a caller that skipped ReadLocalState cannot re-grant anything.
        var lostAtThisStart = context.DatabaseRepaired || context.GenerationStored || context.CommitWitnessed;
        return state.Health switch
        {
            LocalStateHealth.Unreadable => StateLost(catalog, state, shipped),
            LocalStateHealth.Absent when lostAtThisStart => StateLost(catalog, state, shipped),
            LocalStateHealth.Absent => FirstStart(catalog, state, shipped, defaultAiPermission),
            _ => Update(catalog, state, shipped, defaultAiPermission),
        };
    }

    // A3: a fresh state that denies every library until the user chooses again, with the libraries the document's list
    // stands for on (ReadLocalState already read an unhealthy state that way), markers as at the upgrade and the content
    // that is there now accepted, which is safe because nothing is permitted.
    private static LibraryAdoption StateLost(
        LibraryCatalog catalog, LibraryLocalState state, IReadOnlyDictionary<LibraryTermKey, List<TermValues>> shipped)
    {
        var markers = catalog.Libraries
            .Where(library => !library.Content.BuiltIn)
            .SelectMany(library => LibraryTiers.UpgradeMarkers(library.Content, shipped))
            .ToList();
        var accepted = catalog.Libraries
            .Where(library => library.ContentHash is not null)
            .Select(library => new KeyValuePair<string, LibraryContentHash>(library.Content.Id, library.ContentHash!.Value))
            .ToList();
        var adopted = LibraryLocalState.Create(
            state.EnabledIds,
            state.LegacyEnabledIds,
            aiPermissions: null,
            markers,
            aiUpgradeNotice: null,
            LocalStateHealth.Ok,
            accepted,
            aiPermissionsLost: true);
        return new LibraryAdoption(adopted, LibraryAdoptionReasons.StateLost, catalog.Libraries.Count, adopted.LegacyMarkers.Count);
    }

    // The upgrade: every custom library that is there is Existing (Decision 2 keeps it on), its content accepted, its
    // legacy markers added (Decision 1) and listed in the one-time notice. Built-ins take the kind default and need no
    // entry; an edits document already there is accepted as it stands. A file whose bytes cannot be read yet has no
    // content to bind a choice to, so it is left unrecorded and is discovered once it can be read, with AI off.
    private static LibraryAdoption FirstStart(
        LibraryCatalog catalog,
        LibraryLocalState state,
        IReadOnlyDictionary<LibraryTermKey, List<TermValues>> shipped,
        Func<LibraryOrigin, bool, bool?, bool> defaultAiPermission)
    {
        var permissions = new List<KeyValuePair<string, bool>>();
        var markers = new List<LegacyMarker>();
        var accepted = new List<KeyValuePair<string, LibraryContentHash>>();
        var notice = new List<string>();
        var recorded = 0;
        foreach (var library in catalog.Libraries)
        {
            if (library.ContentHash is not { } hash)
            {
                continue;
            }

            var id = library.Content.Id;
            accepted.Add(new(id, hash));
            recorded++;
            if (!library.Content.BuiltIn)
            {
                permissions.Add(new(id, defaultAiPermission(LibraryOrigin.Existing, false, null)));
                markers.AddRange(LibraryTiers.UpgradeMarkers(library.Content, shipped));
                notice.Add(id);
            }
        }

        var adopted = LibraryLocalState.Create(
            state.EnabledIds,
            state.LegacyEnabledIds,
            permissions,
            markers,
            notice,
            LocalStateHealth.Ok,
            accepted,
            aiPermissionsLost: false);
        return new LibraryAdoption(adopted, LibraryAdoptionReasons.FirstStart, recorded, adopted.LegacyMarkers.Count);
    }

    // A healthy state: record custom files it never saw (Discovered) and re-record content that changed outside Scribe
    // (ContentReplaced, A4). The enabled state of a discovered file is what reading gave it; a replaced custom file is
    // turned off, because this version cannot tell whose choice its enabled entry was; a built-in whose edits document
    // was replaced keeps its enabled state, since its shipped rows are Decision 2's, and loses its AI permission.
    private static LibraryAdoption? Update(
        LibraryCatalog catalog,
        LibraryLocalState state,
        IReadOnlyDictionary<LibraryTermKey, List<TermValues>> shipped,
        Func<LibraryOrigin, bool, bool?, bool> defaultAiPermission)
    {
        var permissions = new Dictionary<string, bool>(state.AiPermissions, StringComparer.OrdinalIgnoreCase);
        var enabled = new HashSet<string>(state.EnabledIds, StringComparer.OrdinalIgnoreCase);
        var markers = state.LegacyMarkers.ToList();
        var accepted = new Dictionary<string, LibraryContentHash>(state.AcceptedContent, StringComparer.OrdinalIgnoreCase);
        var notice = new HashSet<string>(state.AiUpgradeNotice, StringComparer.OrdinalIgnoreCase);
        var reasons = LibraryAdoptionReasons.None;
        var recorded = 0;

        foreach (var library in catalog.Libraries)
        {
            if (library.ContentHash is not { } hash)
            {
                continue;
            }

            var id = library.Content.Id;
            var known = accepted.TryGetValue(id, out var previous);
            if (known && previous == hash)
            {
                continue;
            }

            recorded++;
            accepted[id] = hash;
            if (library.Content.BuiltIn)
            {
                reasons |= LibraryAdoptionReasons.ContentReplaced;
                permissions[id] = false;
                continue;
            }

            markers.RemoveAll(marker => string.Equals(marker.LibraryId, id, StringComparison.OrdinalIgnoreCase));
            markers.AddRange(LibraryTiers.UpgradeMarkers(library.Content, shipped));
            if (known)
            {
                reasons |= LibraryAdoptionReasons.ContentReplaced;
                permissions[id] = false;
                enabled.Remove(id);
                notice.Remove(id);
            }
            else
            {
                reasons |= LibraryAdoptionReasons.Discovered;
                permissions[id] = defaultAiPermission(LibraryOrigin.Discovered, false, null);
            }
        }

        if (reasons == LibraryAdoptionReasons.None)
        {
            return null;
        }

        var before = state.LegacyMarkers.ToHashSet();
        var adopted = LibraryLocalState.Create(
            enabled,
            state.LegacyEnabledIds,
            permissions,
            markers,
            notice,
            LocalStateHealth.Ok,
            accepted,
            state.AiPermissionsLost);
        return new LibraryAdoption(adopted, reasons, recorded, adopted.LegacyMarkers.Count(marker => !before.Contains(marker)));
    }
}
