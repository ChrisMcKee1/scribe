using System.Collections.Frozen;

namespace Scribe.Core.Libraries;

/// <summary>
/// The library state that lives in local settings rather than in library files: which libraries are on, which may be
/// sent to AI cleanup as vocabulary (Decision 2), which legacy custom rows keep their pre-upgrade result (Decision 1),
/// and which file content each of those choices was made for. None of it ever travels in a library file, so a shared
/// library cannot switch itself on or grant itself AI permission.
/// </summary>
/// <remarks>
/// <para>
/// Built only through <see cref="Create"/>, which gives every set and map case-insensitive ids, drops blank ids and
/// removes repeated markers, so no consumer meets a comparer it did not expect. Immutable (frozen collections); a
/// change is a new instance.
/// </para>
/// <para>
/// Stored as the auxiliary settings row <see cref="LibrarySettingKeys.State"/>, whose format belongs to composition and
/// which is this build's source of truth for which libraries are on, by logical id (review finding A17), plus
/// <see cref="Models.AppSettings.EnabledDictionaryLibraryIds"/>, the list 0.4.3 reads, which is only a downgrade-safe
/// projection of it by legacy id: an enabled library an older build would send or apply beyond what the user chose (its
/// AI permission is off, or it shares its legacy id with a library that is not both on and permitted, before or after the
/// commit's physical changes, review findings A15 and A18) is left out of it. The row also stores the projection this
/// build last wrote, so a change an older build made to the document's list since is noticed and applied, conservatively,
/// when the state is read.
/// </para>
/// <para>
/// Consent is bound to content (review finding A4): <see cref="AcceptedContent"/> holds, per library, the hash of the
/// file this version last wrote or adopted. A file whose bytes no longer match was replaced outside Scribe (an older
/// build deleted the library and imported another under the same file name, say), and composition treats it as newly
/// discovered: this version's earlier choices for that id no longer apply to it. The journal records the hash of
/// every file it writes in the same commit, so Scribe's own writes never read as a replacement.
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
        LocalStateHealth health,
        FrozenDictionary<string, LibraryContentHash> acceptedContent,
        bool aiPermissionsLost)
    {
        EnabledIds = enabledIds;
        LegacyEnabledIds = legacyEnabledIds;
        AiPermissions = aiPermissions;
        LegacyMarkers = legacyMarkers;
        AiUpgradeNotice = aiUpgradeNotice;
        Health = health;
        AcceptedContent = acceptedContent;
        AiPermissionsLost = aiPermissionsLost;
    }

    /// <summary>Nothing stored yet: no library on, no choices made, <see cref="LocalStateHealth.Absent"/>.</summary>
    public static LibraryLocalState Absent { get; } =
        Create(enabledIds: null, legacyEnabledIds: null, aiPermissions: null, legacyMarkers: null, aiUpgradeNotice: null,
            LocalStateHealth.Absent);

    /// <summary>
    /// Every library this build treats as on, by logical id: the auxiliary row's list, with any change an older build made
    /// to the document's list since this build last wrote it applied through the libraries' legacy ids (review finding
    /// A17). With no readable row, the document's list read the same way.
    /// </summary>
    public IReadOnlySet<string> EnabledIds { get; }

    /// <summary>
    /// The ids the document's <see cref="Models.AppSettings.EnabledDictionaryLibraryIds"/> held when this state was
    /// read (the projection, as an older build may have changed it), or, when no readable document held the list (a
    /// session on defaults), the projection the auxiliary row stored; so an id the encoder cannot place (a library that is
    /// not there right now) stays in the list it came from.
    /// </summary>
    public IReadOnlySet<string> LegacyEnabledIds { get; }

    /// <summary>
    /// The explicit AI permission choices, by library id. A library with no entry takes the policy default for its
    /// kind, unless <see cref="AiPermissionsLost"/> is set, which denies it. Any state whose <see cref="Health"/> is
    /// <see cref="LocalStateHealth.Unreadable"/> or <see cref="LocalStateHealth.Newer"/> permits nothing: permission
    /// fails closed.
    /// </summary>
    public IReadOnlyDictionary<string, bool> AiPermissions { get; }

    /// <summary>The legacy rows of Decision 1, each keeping its pre-upgrade result until the user edits it or chooses Use my spelling.</summary>
    public IReadOnlyList<LegacyMarker> LegacyMarkers { get; }

    /// <summary>Custom libraries kept on for AI cleanup at the upgrade whose one-time notice the user has not dismissed.</summary>
    public IReadOnlySet<string> AiUpgradeNotice { get; }

    /// <summary>Whether the auxiliary row was found and understood.</summary>
    public LocalStateHealth Health { get; }

    /// <summary>
    /// By library id, the hash of the content every choice above was made for: a custom library's CSV, or a built-in's
    /// edits document (no entry for a built-in with no document). A library whose file does not match is treated as
    /// newly discovered (review finding A4).
    /// </summary>
    public IReadOnlyDictionary<string, LibraryContentHash> AcceptedContent { get; }

    /// <summary>
    /// The AI permission choices were lost (a repair lost the auxiliary row, or it could not be read) and the user has
    /// not chosen again. While set, a library with no explicit entry in <see cref="AiPermissions"/> is denied whatever
    /// its kind, every commit keeps the flag, and only the user's explicit confirmation of the choices clears it, so a
    /// loss never re-grants permission on a later start or through an unrelated Save (review finding A3).
    /// </summary>
    public bool AiPermissionsLost { get; }

    /// <summary>
    /// A state from its parts. Null collections are empty; ids are trimmed, blank ones dropped, and compared
    /// case-insensitively (<see cref="StringComparer.OrdinalIgnoreCase"/>); a repeated marker is kept once, in first
    /// position; for a repeated permission or content id the last value wins.
    /// </summary>
    public static LibraryLocalState Create(
        IEnumerable<string?>? enabledIds,
        IEnumerable<string?>? legacyEnabledIds,
        IEnumerable<KeyValuePair<string, bool>>? aiPermissions,
        IEnumerable<LegacyMarker>? legacyMarkers,
        IEnumerable<string?>? aiUpgradeNotice,
        LocalStateHealth health,
        IEnumerable<KeyValuePair<string, LibraryContentHash>>? acceptedContent = null,
        bool aiPermissionsLost = false)
    {
        var permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, permitted) in aiPermissions ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                permissions[id.Trim()] = permitted;
            }
        }

        var accepted = new Dictionary<string, LibraryContentHash>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, hash) in acceptedContent ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                accepted[id.Trim()] = hash;
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
            health,
            accepted.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            aiPermissionsLost);
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
    /// No row, and nothing says one was lost: no stored generation, no repair at this start, no session on defaults, and
    /// no witness file of an earlier commit in the libraries folder. The first start of this version. Libraries without
    /// an explicit choice take the policy default for their kind.
    /// </summary>
    Absent,

    /// <summary>Read and understood.</summary>
    Ok,

    /// <summary>
    /// A row this build could not parse, or a row that should exist and is gone (see <see cref="LibraryStateContext"/>).
    /// AI permission fails closed, and the state committed next carries <see cref="LibraryLocalState.AiPermissionsLost"/>,
    /// so the denial outlives this start.
    /// </summary>
    Unreadable,

    /// <summary>
    /// A row from a newer version. AI permission fails closed, the libraries are read-only in this version (no library
    /// change is saved), and this version never writes the row.
    /// </summary>
    Newer,
}

/// <summary>A library state encoded for the settings store.</summary>
/// <param name="EnabledLibraryIds">
/// The list for <see cref="Models.AppSettings.EnabledDictionaryLibraryIds"/>, in precedence order: the downgrade-safe
/// projection of the enabled libraries, by the ids older builds load them as, safe both before and after the commit's
/// physical changes (review findings A15 and A18).
/// </param>
/// <param name="StateValue">
/// The value of the auxiliary row <see cref="LibrarySettingKeys.State"/>, which carries every enabled library by logical
/// id and this same projection (A17), or null to leave the stored row as it is: a state from a newer version
/// (<see cref="LocalStateHealth.Newer"/>) is never written by this one.
/// </param>
public sealed record LibraryStateEncoding(IReadOnlyList<string> EnabledLibraryIds, string? StateValue);

/// <summary>
/// What the service knows about the session when it reads the library state or asks whether to commit state on its own.
/// Any of the four flags turns an absent state row from a first start into a lost one.
/// </summary>
/// <param name="RunningOnDefaults">
/// The session runs on defaults because the saved settings could not be read or were lost
/// (<see cref="Persistence.SettingsRepository.StartsWithoutSavedSettings"/>). Nothing library-related is written
/// automatically then: no adoption, no marker, no denial, no Recently deleted or journal clean-up (the one exception is the
/// witness restored beside a stored library row, which can only make a missing row read as lost). The user's own Save,
/// which ends that state, commits the library changes with the settings.
/// </param>
/// <param name="DatabaseRepaired">
/// A repair rebuilt the database at this start (<see cref="Persistence.ScribeDatabase.RepairedAtStartup"/>), so an
/// absent row may be a lost one. A later start needs no such flag: no library row can exist without a commit the witness
/// preceded (<paramref name="CommitWitnessed"/>), so a repair that lost the rows leaves the witness to say so (round 4,
/// review finding A3).
/// </param>
/// <param name="GenerationStored">
/// <see cref="LibrarySettingKeys.Generation"/> is stored. Every commit writes the state row with it, so a stored
/// generation beside an absent state row means the row was lost, not that this is the first start.
/// </param>
/// <param name="CommitWitnessed">
/// The libraries folder holds the witness file (review finding A3). The journal writes it, flushed to disk, before any
/// library commit of this version can happen (the first adoption, the user's first Save, a wrapper) and at a start that
/// detects a loss, before anything else; it is monotonic: Scribe creates it and never deletes, truncates or replaces it.
/// Hence the invariant the reading rests on (round 4): no library row exists without a witness made durable first, so a
/// witness beside a missing row always means a loss, and no witness means no commit of this version ever happened here, a
/// genuine first start with nothing of this version to lose. Only an action outside Scribe (removing the witness, or
/// moving the database without the libraries folder) can break it. It lives outside the database, so it survives a repair
/// that loses both library rows, including one an older build makes. Written before the commit, it also turns an
/// interrupted first adoption into a lost state the user confirms next time, which is the fail-closed direction.
/// </param>
public readonly record struct LibraryStateContext(
    bool RunningOnDefaults, bool DatabaseRepaired, bool GenerationStored, bool CommitWitnessed = false);

/// <summary>Why the service commits library state it was not asked to save.</summary>
[Flags]
public enum LibraryAdoptionReasons
{
    None = 0,

    /// <summary>
    /// The first start of this version: every custom library that exists is recorded with AI permission on (Decision
    /// 2), its content accepted, legacy markers added (Decision 1), and the upgrade notice pending.
    /// </summary>
    FirstStart = 1,

    /// <summary>A custom file appeared that this version never recorded: recorded with AI off and markers as at the upgrade.</summary>
    Discovered = 2,

    /// <summary>
    /// A library's content no longer matches <see cref="LibraryLocalState.AcceptedContent"/>: this version's earlier
    /// choices for its id are dropped (AI off, out of the enabled lists, markers recomputed) and the new content accepted.
    /// </summary>
    ContentReplaced = 4,

    /// <summary>
    /// The stored state was lost or unreadable: a fresh state is committed with
    /// <see cref="LibraryLocalState.AiPermissionsLost"/> set, the libraries the document's enabled list stands for on
    /// (each legacy id for every library an older build loads under it), markers recomputed as at the upgrade and the
    /// current content accepted.
    /// </summary>
    StateLost = 8,
}

/// <summary>
/// Library state the service commits on its own because it met libraries or a loss it had never recorded. Deterministic
/// from the catalog and the stored state, so a start whose write failed, or had to wait, plans the same again and uses
/// the plan in memory meanwhile.
/// </summary>
/// <param name="State">The complete state to commit and use.</param>
/// <param name="Reasons">Why.</param>
/// <param name="LibrariesAdopted">How many libraries were recorded or re-recorded.</param>
/// <param name="MarkersAdded">How many legacy markers were added.</param>
public sealed record LibraryAdoption(LibraryLocalState State, LibraryAdoptionReasons Reasons, int LibrariesAdopted, int MarkersAdded);
