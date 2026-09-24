using System.Collections.Frozen;

namespace Scribe.Core.Libraries;

/// <summary>
/// The library state that lives in local settings rather than in library files: which libraries are on, which may be
/// sent to AI cleanup as vocabulary (Decision 2), and which legacy custom rows keep their pre-upgrade result (Decision
/// 1). None of it ever travels in a library file, so a shared library cannot switch itself on or grant itself AI
/// permission.
/// </summary>
/// <remarks>
/// <para>
/// Built only through <see cref="Create"/>, which gives every set and map case-insensitive ids, drops blank ids and
/// removes repeated markers, so no consumer meets a comparer it did not expect. Immutable (frozen collections); a
/// change is a new instance.
/// </para>
/// <para>
/// Stored as <see cref="Models.AppSettings.EnabledDictionaryLibraryIds"/> (the list 0.4.3 reads) plus the auxiliary
/// settings row <see cref="LibrarySettingKeys.State"/>, whose format belongs to composition. An enabled library whose
/// AI permission is off is kept out of the document's list, which older builds would send whole to AI cleanup, and
/// in the auxiliary row instead; this build reads both.
/// </para>
/// </remarks>
public sealed class LibraryLocalState
{
    private LibraryLocalState(
        FrozenSet<string> enabledIds,
        FrozenSet<string> legacyEnabledIds,
        FrozenDictionary<string, bool> aiPermissions,
        IReadOnlyList<LegacyMarker> legacyMarkers,
        FrozenSet<string> aiUpgradeNotice,
        LocalStateHealth health)
    {
        EnabledIds = enabledIds;
        LegacyEnabledIds = legacyEnabledIds;
        AiPermissions = aiPermissions;
        LegacyMarkers = legacyMarkers;
        AiUpgradeNotice = aiUpgradeNotice;
        Health = health;
    }

    /// <summary>Nothing stored yet: no library on, no choices made, <see cref="LocalStateHealth.Absent"/>.</summary>
    public static LibraryLocalState Absent { get; } =
        Create(enabledIds: null, legacyEnabledIds: null, aiPermissions: null, legacyMarkers: null, aiUpgradeNotice: null,
            LocalStateHealth.Absent);

    /// <summary>Every library this build treats as on: the document's list and the auxiliary list together.</summary>
    public IReadOnlySet<string> EnabledIds { get; }

    /// <summary>
    /// The ids the document's <see cref="Models.AppSettings.EnabledDictionaryLibraryIds"/> held when this state was
    /// read, so an id the encoder cannot place (a library that is not there right now) stays in the list it came from.
    /// </summary>
    public IReadOnlySet<string> LegacyEnabledIds { get; }

    /// <summary>
    /// The explicit AI permission choices, by library id. A library with no entry takes the policy default for its
    /// kind, and any state whose <see cref="Health"/> is <see cref="LocalStateHealth.Unreadable"/> or
    /// <see cref="LocalStateHealth.Newer"/> permits nothing: permission fails closed.
    /// </summary>
    public IReadOnlyDictionary<string, bool> AiPermissions { get; }

    /// <summary>The legacy rows of Decision 1, each keeping its pre-upgrade result until the user edits it or chooses Use my spelling.</summary>
    public IReadOnlyList<LegacyMarker> LegacyMarkers { get; }

    /// <summary>Custom libraries kept on for AI cleanup at the upgrade whose one-time notice the user has not dismissed.</summary>
    public IReadOnlySet<string> AiUpgradeNotice { get; }

    /// <summary>Whether the auxiliary row was found and understood.</summary>
    public LocalStateHealth Health { get; }

    /// <summary>
    /// A state from its parts. Null collections are empty; ids are trimmed, blank ones dropped, and compared
    /// case-insensitively (<see cref="StringComparer.OrdinalIgnoreCase"/>); a repeated marker is kept once, in first
    /// position; for a repeated permission id the last value wins.
    /// </summary>
    public static LibraryLocalState Create(
        IEnumerable<string?>? enabledIds,
        IEnumerable<string?>? legacyEnabledIds,
        IEnumerable<KeyValuePair<string, bool>>? aiPermissions,
        IEnumerable<LegacyMarker>? legacyMarkers,
        IEnumerable<string?>? aiUpgradeNotice,
        LocalStateHealth health)
    {
        var permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, permitted) in aiPermissions ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                permissions[id.Trim()] = permitted;
            }
        }

        var markers = new List<LegacyMarker>();
        var seen = new HashSet<LegacyMarker>();
        foreach (var marker in legacyMarkers ?? [])
        {
            if (!string.IsNullOrWhiteSpace(marker.LibraryId) && !marker.Key.IsEmpty)
            {
                var normalized = marker with { LibraryId = marker.LibraryId.Trim() };
                if (seen.Add(normalized))
                {
                    markers.Add(normalized);
                }
            }
        }

        return new LibraryLocalState(
            IdSet(enabledIds),
            IdSet(legacyEnabledIds),
            permissions.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            markers.AsReadOnly(),
            IdSet(aiUpgradeNotice),
            health);
    }

    private static FrozenSet<string> IdSet(IEnumerable<string?>? ids)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                set.Add(id.Trim());
            }
        }

        return set.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// A legacy custom row (Decision 1): a row of <see cref="LibraryId"/> whose spoken form a built-in also ships with a
/// different result. While a built-in with a different enabled result is on, the row keeps the pre-upgrade winner, the
/// built-in's; editing the row or choosing Use my spelling removes the marker.
/// </summary>
/// <param name="LibraryId">The custom library's id, compared case-insensitively.</param>
/// <param name="Key">The row's key.</param>
public readonly record struct LegacyMarker(string LibraryId, LibraryTermKey Key)
{
    public bool Equals(LegacyMarker other) =>
        string.Equals(LibraryId, other.LibraryId, StringComparison.OrdinalIgnoreCase) && Key.Equals(other.Key);

    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(LibraryId ?? string.Empty), Key);
}

/// <summary>Whether the library state's auxiliary settings row was found and understood.</summary>
public enum LocalStateHealth
{
    /// <summary>
    /// No row, and nothing says one was lost: the first start of this version. Libraries without an explicit choice
    /// take the policy default for their kind.
    /// </summary>
    Absent,

    /// <summary>Read and understood.</summary>
    Ok,

    /// <summary>
    /// A row this build could not parse, or a row that should exist and is gone (a stored generation, or a repaired
    /// database). AI permission fails closed and nothing is adopted; the next Save writes a fresh row.
    /// </summary>
    Unreadable,

    /// <summary>
    /// A row from a newer version. AI permission fails closed, enabled and permission choices cannot be changed, and
    /// this version never writes the row.
    /// </summary>
    Newer,
}

/// <summary>A library state encoded for the settings store.</summary>
/// <param name="EnabledLibraryIds">The list for <see cref="Models.AppSettings.EnabledDictionaryLibraryIds"/>, in precedence order.</param>
/// <param name="StateValue">
/// The value of the auxiliary row <see cref="LibrarySettingKeys.State"/>, or null to leave the stored row as it is:
/// a state from a newer version (<see cref="LocalStateHealth.Newer"/>) is never written by this one.
/// </param>
public sealed record LibraryStateEncoding(IReadOnlyList<string> EnabledLibraryIds, string? StateValue);

/// <summary>What the service knows about the session when it reads the library state or asks whether to adopt libraries into it.</summary>
/// <param name="RunningOnDefaults">
/// The session runs on defaults because the saved settings could not be read or were lost
/// (<see cref="Persistence.SettingsRepository.StartsWithoutSavedSettings"/>). Nothing is adopted or written then.
/// </param>
/// <param name="DatabaseRepaired">A repair rebuilt the database at this start, so an absent row may be a lost one.</param>
/// <param name="GenerationStored">
/// <see cref="LibrarySettingKeys.Generation"/> is stored. Every commit writes the state row with it, so a stored
/// generation beside an absent state row means the row was lost, not that this is the first start.
/// </param>
public readonly record struct LibraryStateContext(bool RunningOnDefaults, bool DatabaseRepaired, bool GenerationStored);

/// <summary>
/// Library state to commit because the service met libraries it had never recorded: at the first start of this
/// version, every custom library that exists (AI permission kept on, Decision 2, and legacy markers, Decision 1); later,
/// a custom file that appeared from elsewhere (<see cref="LibraryOrigin.Discovered"/>: AI off, markers as at the
/// upgrade). Deterministic from the catalog, so a start whose write failed plans the same again next time.
/// </summary>
/// <param name="State">The complete state to commit and use.</param>
/// <param name="LibrariesAdopted">How many libraries were recorded.</param>
/// <param name="MarkersAdded">How many legacy markers were added.</param>
/// <param name="FirstStart">Whether this is the first start of this version.</param>
public sealed record LibraryAdoption(LibraryLocalState State, int LibrariesAdopted, int MarkersAdded, bool FirstStart);
