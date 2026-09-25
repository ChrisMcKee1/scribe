using System.Text.Json.Nodes;
using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// A test double of composition and policy (the C sub-stream's <c>LibraryComposer</c>) that follows the rules J's storage
/// relies on, as the contract states them: the <c>libraries.state</c> v1 format of 6.2, reading with the legacy-id
/// difference rule (3.3.4, A17), the downgrade encoding over the libraries before and after a commit (2.15, A15, A18),
/// adoption for a first start, a discovered file, replaced content and a lost state (3.3.4, A3, A4), AI permission bound
/// to content (3.3.3, A4, A12), and a first-wins composition in catalog order. Tiers and legacy markers are left out:
/// nothing in the storage stream depends on them. It lets J's tests prove that J hands composition the right inputs and
/// commits its answers, without building against C's unmerged code.
/// </summary>
internal sealed class ContractComposer : ILibraryComposer
{
    public static ContractComposer Instance { get; } = new();

    public LibraryLocalState ReadLocalState(
        IReadOnlyList<string>? documentEnabledIds, string? storedState, IReadOnlyList<LibraryIdentity> libraries, LibraryStateContext context)
    {
        var listed = new HashSet<string>(documentEnabledIds ?? [], StringComparer.OrdinalIgnoreCase);
        var fromList = documentEnabledIds is null
            ? []
            : libraries.Where(library => listed.Contains(library.LegacyId)).Select(library => library.Id).ToList();
        if (storedState is null)
        {
            var lost = context.GenerationStored || context.DatabaseRepaired || context.RunningOnDefaults || context.CommitWitnessed;
            return LibraryLocalState.Create(fromList, documentEnabledIds, null, null, null, lost ? LocalStateHealth.Unreadable : LocalStateHealth.Absent);
        }

        var row = ParseRow(storedState);
        if (row is null || row.Health != LocalStateHealth.Ok)
        {
            return LibraryLocalState.Create(fromList, documentEnabledIds, null, null, null, row?.Health ?? LocalStateHealth.Unreadable);
        }

        var enabled = new HashSet<string>(row.Enabled, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> legacy = row.Projection;
        if (documentEnabledIds is not null)
        {
            var projection = new HashSet<string>(row.Projection, StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries)
            {
                if (listed.Contains(library.LegacyId) && !projection.Contains(library.LegacyId))
                {
                    enabled.Add(library.Id);
                }

                if (projection.Contains(library.LegacyId) && !listed.Contains(library.LegacyId) &&
                    !library.LegacyId.StartsWith("scribe.", StringComparison.OrdinalIgnoreCase))
                {
                    enabled.Remove(library.Id);
                }
            }

            legacy = documentEnabledIds;
        }

        return LibraryLocalState.Create(
            enabled, legacy, row.Ai, null, row.Notice, LocalStateHealth.Ok, row.Accepted, row.Lost);
    }

    public LibraryStateEncoding EncodeLocalState(
        LibraryLocalState state, LibraryLocalState? startingState, IReadOnlyList<LibraryIdentity> librariesBefore, IReadOnlyList<LibraryIdentity> librariesAfter)
    {
        var after = librariesAfter.Select(library => library.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = librariesBefore.Concat(librariesAfter).DistinctBy(library => library.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var listed = new List<string>();
        foreach (var group in LibraryPrecedence.Order(all, library => library.Id, library => library.BuiltIn, library => library.FileName)
                     .GroupBy(library => library.LegacyId, StringComparer.OrdinalIgnoreCase))
        {
            var safe = group.All(library =>
            {
                var content = state.AcceptedContent.TryGetValue(library.Id, out var accepted) ? accepted : (LibraryContentHash?)null;
                if (!after.Contains(library.Id) || !state.EnabledIds.Contains(library.Id) || !IsPermitted(state, library.Id, library.BuiltIn, content))
                {
                    return false;
                }

                if (library.BuiltIn || startingState is null)
                {
                    return true;
                }

                var before = startingState.AcceptedContent.TryGetValue(library.Id, out var old) ? old : (LibraryContentHash?)null;
                return before == content || IsPermitted(startingState, library.Id, false, before);
            });
            if (safe)
            {
                listed.Add(group.Key);
            }
        }

        var legacyIds = all.Select(library => library.LegacyId).Concat(all.Select(library => library.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        listed.AddRange(state.LegacyEnabledIds.Where(id => !legacyIds.Contains(id)).Order(StringComparer.Ordinal));
        if (state.Health == LocalStateHealth.Newer)
        {
            return new LibraryStateEncoding([.. state.LegacyEnabledIds], null);
        }

        var ai = new JsonObject
        {
            ["on"] = Array(state.AiPermissions.Where(pair => pair.Value && after.Contains(pair.Key)).Select(pair => pair.Key)),
            ["off"] = Array(state.AiPermissions.Where(pair => !pair.Value && after.Contains(pair.Key)).Select(pair => pair.Key)),
        };
        var accepted = new JsonObject();
        foreach (var (id, hash) in state.AcceptedContent.Where(pair => after.Contains(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            accepted[id] = hash.Value;
        }

        var value = new JsonObject
        {
            ["version"] = 1,
            ["enabled"] = Array(state.EnabledIds.Where(after.Contains)),
            ["legacyProjection"] = new JsonArray([.. listed.Select(id => (JsonNode?)JsonValue.Create(id))]),
            ["ai"] = ai,
            ["aiPermissionsLost"] = state.AiPermissionsLost,
            ["accepted"] = accepted,
            ["upgradeNotice"] = Array(state.AiUpgradeNotice.Where(after.Contains)),
        };
        return new LibraryStateEncoding(listed, value.ToJsonString());
    }

    public LibraryAdoption? PlanAdoption(LibraryCatalog catalog, LibraryStateContext context)
    {
        var state = catalog.LocalState;
        if (context.RunningOnDefaults || state.Health == LocalStateHealth.Newer)
        {
            return null;
        }

        var hashes = catalog.Libraries.Where(library => library.ContentHash is not null)
            .ToDictionary(library => library.Content.Id, library => library.ContentHash!.Value, StringComparer.OrdinalIgnoreCase);
        if (state.Health == LocalStateHealth.Absent)
        {
            var customs = catalog.Libraries.Where(library => !library.Content.BuiltIn && library.ContentHash is not null).Select(library => library.Content.Id).ToList();
            var next = LibraryLocalState.Create(
                state.EnabledIds, state.LegacyEnabledIds, customs.Select(id => new KeyValuePair<string, bool>(id, true)), null, customs,
                LocalStateHealth.Ok, hashes);
            return new LibraryAdoption(next, LibraryAdoptionReasons.FirstStart, customs.Count, 0);
        }

        if (state.Health == LocalStateHealth.Unreadable)
        {
            var denial = LibraryLocalState.Create(
                state.EnabledIds, state.LegacyEnabledIds, null, null, null, LocalStateHealth.Ok, hashes, aiPermissionsLost: true);
            return new LibraryAdoption(denial, LibraryAdoptionReasons.StateLost, catalog.Libraries.Count, 0);
        }

        var reasons = LibraryAdoptionReasons.None;
        var ai = new Dictionary<string, bool>(state.AiPermissions, StringComparer.OrdinalIgnoreCase);
        var enabled = new HashSet<string>(state.EnabledIds, StringComparer.OrdinalIgnoreCase);
        var acceptedContent = new Dictionary<string, LibraryContentHash>(state.AcceptedContent, StringComparer.OrdinalIgnoreCase);
        var adopted = 0;
        foreach (var library in catalog.Libraries)
        {
            if (library.ContentHash is not { } hash || library.State is LibraryFileState.AwaitingRelease or LibraryFileState.Unreadable)
            {
                continue;
            }

            var known = acceptedContent.TryGetValue(library.Content.Id, out var accepted);
            if (!library.Content.BuiltIn && !known)
            {
                reasons |= LibraryAdoptionReasons.Discovered;
                ai[library.Content.Id] = false;
                acceptedContent[library.Content.Id] = hash;
                adopted++;
            }
            else if ((!known && library.Content.BuiltIn) || (known && accepted != hash))
            {
                reasons |= LibraryAdoptionReasons.ContentReplaced;
                ai[library.Content.Id] = false;
                if (!library.Content.BuiltIn)
                {
                    enabled.Remove(library.Content.Id);
                }

                acceptedContent[library.Content.Id] = hash;
                adopted++;
            }
        }

        return reasons == LibraryAdoptionReasons.None
            ? null
            : new LibraryAdoption(
                LibraryLocalState.Create(enabled, state.LegacyEnabledIds, ai, null, state.AiUpgradeNotice, LocalStateHealth.Ok, acceptedContent, state.AiPermissionsLost),
                reasons, adopted, 0);
    }

    public LibraryVocabulary ComposeVocabulary(LibraryCatalog catalog)
    {
        var state = catalog.LocalState;
        var seen = new HashSet<LibraryTermKey>();
        var entries = new List<DictionaryEntry>();
        var aiEntries = new List<DictionaryEntry>();
        var permitted = new List<KeyValuePair<string, LibraryContentHash?>>();
        foreach (var library in catalog.Libraries)
        {
            if (!state.EnabledIds.Contains(library.Content.Id) ||
                library.State is not (LibraryFileState.Available or LibraryFileState.PartlyReadable or LibraryFileState.AwaitingRelease))
            {
                continue;
            }

            var allowed = IsPermitted(state, library.Content.Id, library.Content.BuiltIn, library.ContentHash);
            if (allowed)
            {
                permitted.Add(new(library.Content.Id, library.ContentHash));
            }

            foreach (var row in library.Content.Rows.Where(row => row.Values.Enabled))
            {
                if (seen.Add(LibraryTermKey.From(row.Values.Spoken)))
                {
                    entries.Add(row.Values.ToEntry());
                    if (allowed)
                    {
                        aiEntries.Add(row.Values.ToEntry());
                    }
                }
            }
        }

        return new LibraryVocabulary(catalog.Generation, entries, aiEntries, new AiVocabularyScope(catalog.Generation, permitted));
    }

    public bool HasNarrowed(AiVocabularyScope admitted, AiVocabularyScope current) => !current.Covers(admitted);

    public AiVocabularyScope ScopeWhileSaving(LibraryCatalog committed, LibraryChangeSet changes)
    {
        var deleted = changes.Deletions.Select(deletion => deletion.LibraryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = ComposeVocabulary(committed).AiScope.PermittedContent.Where(pair =>
            changes.LocalState.EnabledIds.Contains(pair.Key) && !deleted.Contains(pair.Key) &&
            committed.Find(pair.Key) is { } library && IsPermitted(changes.LocalState, pair.Key, library.Content.BuiltIn, pair.Value));
        return new AiVocabularyScope(committed.Generation, kept);
    }

    /// <summary>Contract 3.3.3's order: an unhealthy state permits nothing, then content, then the choice, then the denial, then the kind.</summary>
    public static bool IsPermitted(LibraryLocalState state, string id, bool builtIn, LibraryContentHash? content)
    {
        if (state.Health is LocalStateHealth.Unreadable or LocalStateHealth.Newer)
        {
            return false;
        }

        var hasAccepted = state.AcceptedContent.TryGetValue(id, out var accepted);
        if (!builtIn && (!hasAccepted || content is null || accepted != content))
        {
            return false;
        }

        if (builtIn && content is { } document && (!hasAccepted || accepted != document))
        {
            return false;
        }

        if (state.AiPermissions.TryGetValue(id, out var choice))
        {
            return choice;
        }

        return !state.AiPermissionsLost && builtIn;
    }

    /// <summary>The stored row, parsed; null or unhealthy when it is not a v1 row this build reads (6.2).</summary>
    public static StoredRow? ParseRow(string value)
    {
        try
        {
            if (JsonNode.Parse(value) is not JsonObject root || root["version"] is not JsonValue version || !version.TryGetValue<int>(out var number))
            {
                return new StoredRow(LocalStateHealth.Unreadable);
            }

            if (number > 1)
            {
                return new StoredRow(LocalStateHealth.Newer);
            }

            if (root["enabled"] is not JsonArray enabled || root["legacyProjection"] is not JsonArray projection ||
                root["aiPermissionsLost"] is not JsonValue lostValue || !lostValue.TryGetValue<bool>(out var lost))
            {
                return new StoredRow(LocalStateHealth.Unreadable);
            }

            var ai = new List<KeyValuePair<string, bool>>();
            if (root["ai"] is JsonObject aiObject)
            {
                ai.AddRange(Strings(aiObject["on"] as JsonArray).Select(id => new KeyValuePair<string, bool>(id, true)));
                ai.AddRange(Strings(aiObject["off"] as JsonArray).Select(id => new KeyValuePair<string, bool>(id, false)));
            }

            var accepted = new List<KeyValuePair<string, LibraryContentHash>>();
            if (root["accepted"] is JsonObject acceptedObject)
            {
                foreach (var (id, hash) in acceptedObject)
                {
                    accepted.Add(new(id, new LibraryContentHash(hash!.GetValue<string>())));
                }
            }

            return new StoredRow(
                LocalStateHealth.Ok, Strings(enabled), Strings(projection), ai, accepted, Strings(root["upgradeNotice"] as JsonArray), lost);
        }
        catch (Exception)
        {
            return new StoredRow(LocalStateHealth.Unreadable);
        }
    }

    internal sealed record StoredRow(
        LocalStateHealth Health,
        IReadOnlyList<string>? EnabledList = null,
        IReadOnlyList<string>? ProjectionList = null,
        IReadOnlyList<KeyValuePair<string, bool>>? Ai = null,
        IReadOnlyList<KeyValuePair<string, LibraryContentHash>>? Accepted = null,
        IReadOnlyList<string>? Notice = null,
        bool Lost = false)
    {
        public IReadOnlyList<string> Enabled => EnabledList ?? [];

        public IReadOnlyList<string> Projection => ProjectionList ?? [];
    }

    private static List<string> Strings(JsonArray? array) =>
        array is null ? [] : [.. array.OfType<JsonValue>().Select(value => value.GetValue<string>())];

    private static JsonArray Array(IEnumerable<string> values) =>
        new([.. values.Order(StringComparer.Ordinal).Select(value => (JsonNode?)JsonValue.Create(value))]);
}
