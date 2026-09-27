using System.Runtime.CompilerServices;
using Scribe.Core.Models;

namespace Scribe.Core.Libraries;

/// <summary>
/// Composition and policy for the library service (<see cref="ILibraryComposer"/>): the local state's persistence, the
/// state the service adopts on its own, the runtime vocabulary and the revocation checks, over the two policy points of
/// <see cref="LibraryDecisions"/>.
/// </summary>
/// <remarks>Pure and thread-safe: no I/O, no clock, no logging, and nothing it reads makes it throw.</remarks>
public sealed class LibraryComposer : ILibraryComposer
{
    private LibraryComposer()
    {
    }

    /// <summary>The one instance; it holds no state.</summary>
    public static LibraryComposer Instance { get; } = new();

    /// <inheritdoc/>
    public LibraryLocalState ReadLocalState(
        IReadOnlyList<string>? documentEnabledIds,
        string? storedState,
        IReadOnlyList<LibraryIdentity> libraries,
        LibraryStateContext context)
    {
        ArgumentNullException.ThrowIfNull(libraries);

        LocalStateHealth health;
        LibraryLocalStateJson.Row? row = null;
        if (storedState is null)
        {
            // Any sign that a row existed turns its absence into a loss, so a lost row never re-grants permission (A3).
            health = context.RunningOnDefaults || context.DatabaseRepaired || context.GenerationStored || context.CommitWitnessed
                ? LocalStateHealth.Unreadable
                : LocalStateHealth.Absent;
        }
        else
        {
            (health, row) = LibraryLocalStateJson.Read(storedState);
        }

        var groups = LegacyGroups(libraries);
        IEnumerable<string> enabled;
        IEnumerable<string> legacyEnabled;
        if (documentEnabledIds is not null)
        {
            var document = IdSet(documentEnabledIds);
            if (row is not null)
            {
                // The row is the truth; a difference between the document's list and the projection this build last
                // wrote beside it is an older build's change, applied to every library the legacy id stands for (A17).
                var ids = IdSet(row.Enabled);
                var projection = IdSet(row.LegacyProjection);
                foreach (var legacyId in document.Where(id => !projection.Contains(id)))
                {
                    ids.UnionWith(groups.GetValueOrDefault(legacyId)?.Select(library => library.Id) ?? []);
                }

                foreach (var legacyId in projection.Where(id => !document.Contains(id)))
                {
                    if (groups.GetValueOrDefault(legacyId) is { } group && !OnlyUnlistableBuiltIns(group))
                    {
                        ids.ExceptWith(group.Select(library => library.Id));
                    }
                }

                enabled = ids;
            }
            else
            {
                // No readable row: each listed legacy id stands for every library an older build loads under it, which
                // is how a hand-placed twin takes the enabled state its stem had at the first start (decision 5).
                enabled = document.SelectMany(id => groups.GetValueOrDefault(id)?.Select(library => library.Id) ?? []);
            }

            legacyEnabled = documentEnabledIds;
        }
        else
        {
            // No readable document (a session on defaults): nothing to infer an older build's change from.
            enabled = row?.Enabled ?? [];
            legacyEnabled = row?.LegacyProjection ?? [];
        }

        return LibraryLocalState.Create(
            enabled,
            legacyEnabled,
            row?.AiPermissions,
            row?.LegacyMarkers,
            row?.UpgradeNotice,
            health,
            row?.Accepted,
            row?.AiPermissionsLost ?? false);
    }

    /// <inheritdoc/>
    public LibraryStateEncoding EncodeLocalState(
        LibraryLocalState state,
        LibraryLocalState? startingState,
        IReadOnlyList<LibraryIdentity> librariesBefore,
        IReadOnlyList<LibraryIdentity> librariesAfter)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(librariesBefore);
        ArgumentNullException.ThrowIfNull(librariesAfter);

        var both = LibraryPrecedence.Order(
            librariesBefore.Concat(librariesAfter).Distinct(), library => library.Id, library => library.BuiltIn,
            library => library.FileName);
        var legacyIdsOfLibraries = new HashSet<string>(both.Select(library => library.LegacyId), StringComparer.OrdinalIgnoreCase);
        var kept = state.LegacyEnabledIds
            .Where(id => !legacyIdsOfLibraries.Contains(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => id, StringComparer.Ordinal);

        if (state.Health == LocalStateHealth.Newer)
        {
            // A newer version's row is never written by this one, and the document keeps the list it was read with.
            var listed = both
                .Select(library => library.LegacyId)
                .Where(state.LegacyEnabledIds.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return new LibraryStateEncoding([.. listed, .. kept], StateValue: null);
        }

        var before = new HashSet<string>(librariesBefore.Select(library => library.Id), StringComparer.OrdinalIgnoreCase);
        var after = new HashSet<string>(librariesAfter.Select(library => library.Id), StringComparer.OrdinalIgnoreCase);
        var projection = new List<string>();
        foreach (var group in both.GroupBy(library => library.LegacyId, StringComparer.OrdinalIgnoreCase))
        {
            if (group.All(library => IsSafeForOlderBuilds(library, state, startingState, before, after)))
            {
                projection.Add(group.Key);
            }
        }

        projection.AddRange(kept);

        // Every id no library has after the commit leaves the stored state (an unreadable file still counts).
        var order = LibraryPrecedence.Order(librariesAfter, library => library.Id, library => library.BuiltIn, library => library.FileName)
            .Select((library, index) => (library.Id, index))
            .GroupBy(pair => pair.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        IEnumerable<T> Present<T>(IEnumerable<T> items, Func<T, string> id) =>
            items.Where(item => order.ContainsKey(id(item))).OrderBy(item => order[id(item)]);

        var row = new LibraryLocalStateJson.Row(
            [.. Present(state.EnabledIds, id => id)],
            projection,
            [.. Present(state.AiPermissions, pair => pair.Key)],
            // An unreadable state reaching a commit (the user's Save on defaults) carries the denial on (A3).
            state.AiPermissionsLost || state.Health == LocalStateHealth.Unreadable,
            [.. Present(state.LegacyMarkers, marker => marker.LibraryId)],
            [.. Present(state.AcceptedContent, pair => pair.Key)],
            [.. Present(state.AiUpgradeNotice, id => id)]);
        return new LibraryStateEncoding(projection, LibraryLocalStateJson.Write(row));
    }

    /// <inheritdoc/>
    public LibraryAdoption? PlanAdoption(LibraryCatalog catalog, LibraryStateContext context) =>
        LibraryAdoptionPlanner.Plan(catalog, context, LibraryDecisions.DefaultAiPermission);

    /// <inheritdoc/>
    public LibraryVocabulary ComposeVocabulary(LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var composition = LibraryComposition.Committed(catalog, [], new GlossaryBudget(0));
        var vocabulary = new LibraryVocabulary(
            catalog.Generation, composition.LibraryEntries, composition.AiLibraryEntries, AiVocabularyPolicy.ScopeOf(catalog));
        LibraryVocabularyOrigins.Record(vocabulary, composition);
        return vocabulary;
    }

    /// <inheritdoc/>
    public bool HasNarrowed(AiVocabularyScope admitted, AiVocabularyScope current) =>
        AiVocabularyPolicy.HasNarrowed(admitted, current);

    /// <inheritdoc/>
    public AiVocabularyScope ScopeWhileSaving(LibraryCatalog committed, LibraryChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(changes);

        // Narrow only: start from the committed scope with its committed content, and drop every library the Save turns
        // off, deletes or takes permission from. Nothing it grants or writes new content for gains anything before the
        // commit; completion publishes the new content, which no longer covers requests admitted for the old (N5, A12).
        var next = changes.LocalState;
        var deleted = new HashSet<string>(changes.Deletions.Select(deletion => deletion.LibraryId), StringComparer.OrdinalIgnoreCase);
        var scope = AiVocabularyPolicy.ScopeOf(committed);
        var kept = scope.PermittedContent.Where(pair =>
            committed.Find(pair.Key) is { } library &&
            next.EnabledIds.Contains(pair.Key) &&
            !deleted.Contains(pair.Key) &&
            AiVocabularyPolicy.IsPermittedByChoice(next, pair.Key, library.Content.BuiltIn));
        return new AiVocabularyScope(scope.Generation, kept);
    }

    // A legacy id enters the document's list only when every library an older build loads under it is, after the commit,
    // there, on and permitted with its accepted content, and, if it is a custom library whose bytes the commit rewrites,
    // was permitted by the starting state too, since an older build may still load the old bytes (A15, A18). An older
    // build loads a built-in's shipped rows whatever its edits hold, and a library the commit creates or restores has no
    // old bytes an older build could load.
    private static bool IsSafeForOlderBuilds(
        LibraryIdentity library,
        LibraryLocalState state,
        LibraryLocalState? startingState,
        IReadOnlySet<string> before,
        IReadOnlySet<string> after)
    {
        if (!after.Contains(library.Id) ||
            !state.EnabledIds.Contains(library.Id) ||
            !AiVocabularyPolicy.IsPermitted(state, library.Id, library.BuiltIn, AiVocabularyPolicy.AcceptedContentOf(state, library.Id)))
        {
            return false;
        }

        if (library.BuiltIn || startingState is null || !before.Contains(library.Id))
        {
            return true;
        }

        var previous = AiVocabularyPolicy.AcceptedContentOf(startingState, library.Id);
        return previous == AiVocabularyPolicy.AcceptedContentOf(state, library.Id) ||
            AiVocabularyPolicy.IsPermitted(startingState, library.Id, builtIn: false, previous);
    }

    // An older build cannot list a built-in it does not ship, so a scribe. id missing from its Save is not a choice.
    private static bool OnlyUnlistableBuiltIns(IReadOnlyList<LibraryIdentity> group) =>
        group.Count > 0 && group.All(library =>
            library.BuiltIn && library.Id.StartsWith(LibraryTiers.NewBuiltInPrefix, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, IReadOnlyList<LibraryIdentity>> LegacyGroups(IReadOnlyList<LibraryIdentity> libraries) =>
        libraries
            .Where(library => library is not null && !string.IsNullOrWhiteSpace(library.Id))
            .GroupBy(library => library.LegacyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<LibraryIdentity>)group.ToList(), StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> IdSet(IEnumerable<string?> ids) =>
        new(ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!.Trim()), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Which library supplied each entry of a vocabulary this composer built, so the usage report can bind its shareable
/// labels to exactly the libraries behind them (review finding A6). Held beside the vocabulary for as long as it lives,
/// because <see cref="LibraryVocabulary"/> itself carries only flat entries; a vocabulary built anywhere else has no
/// origins, and the report then binds to the whole AI scope, which only ever refuses more.
/// </summary>
internal static class LibraryVocabularyOrigins
{
    private static readonly ConditionalWeakTable<LibraryVocabulary, IReadOnlyDictionary<DictionaryEntry, string>> Origins = new();

    public static void Record(LibraryVocabulary vocabulary, LibraryComposition composition)
    {
        var origins = new Dictionary<DictionaryEntry, string>(ReferenceEqualityComparer.Instance);
        foreach (var entry in vocabulary.Entries)
        {
            if (composition.LibraryOf(entry) is { } id)
            {
                origins.TryAdd(entry, id);
            }
        }

        Origins.AddOrUpdate(vocabulary, origins);
    }

    public static bool TryGet(LibraryVocabulary vocabulary, out IReadOnlyDictionary<DictionaryEntry, string> origins) =>
        Origins.TryGetValue(vocabulary, out origins!);
}
