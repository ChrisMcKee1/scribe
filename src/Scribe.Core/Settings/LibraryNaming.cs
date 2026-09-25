using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Settings;

/// <summary>
/// Names and ids for custom libraries: the names the editor suggests ("New word pack", "GitHub - Copy", a kept version's
/// "(changed outside Scribe)"), the uniqueness check a typed name gets, and the ids a new or remapped library is given.
/// </summary>
/// <remarks>
/// <para>
/// People know libraries as word packs (the maintainer's product decision), so every name suggested here says word pack,
/// in sentence case. The code, the ids and the file names keep "library": an id is derived from the name a library is
/// created with, by the unchanged rule, so a new word pack's id follows its default name (<c>custom-new-word-pack</c>),
/// and the slug's own fallback for a name with no letter or digit stays <c>library</c>, which the storage stream's copy
/// of the rule shares.
/// </para>
/// <para>
/// An id is fixed when the library is created and never follows a rename, because the local state, the legacy markers
/// and the file name all refer to it (review finding R11). New ids are <c>custom-&lt;slug&gt;</c>, which no built-in id can
/// be: the eleven 0.4.3 built-ins have their own ids, and any later built-in gets a dotted <c>scribe.&lt;name&gt;</c>, which a
/// slug cannot produce. A hand-placed file whose stem is a built-in id is remapped to <c>custom-&lt;stem&gt;</c>, the stem kept
/// as it is so the remap stays recognizable.
/// </para>
/// <para>
/// <see cref="Slug"/> is exactly the rule the library service has always derived a custom id from
/// (<c>DictionaryLibraryService.Slugify</c>), so an import keeps producing the ids it did before (review finding G9);
/// <c>tests/fixtures/libraries/slugs.json</c> pins its answers, and the storage stream's interim copy of the rule runs the
/// same vectors until the integration commit points it here.
/// </para>
/// <para>
/// Names compare in their committed form (<see cref="LibraryMetadata.Commit"/>) without case
/// (<see cref="StringComparison.OrdinalIgnoreCase"/>), across every library. Uniqueness is checked only for a name the
/// user types or the editor suggests: two untouched legacy libraries may keep sharing a name.
/// </para>
/// </remarks>
public static class LibraryNaming
{
    /// <summary>The prefix of every custom library id this version creates or remaps.</summary>
    public const string CustomIdPrefix = "custom-";

    /// <summary>The name a new library starts with, before it is made unique.</summary>
    public const string NewLibraryBaseName = "New word pack";

    /// <summary>The name an imported library gets when neither its header nor its file name gives one.</summary>
    public const string ImportedLibraryBaseName = "Imported word pack";

    /// <summary>"New word pack", or "New word pack 2", "New word pack 3" and so on while the name is taken.</summary>
    public static string NewLibraryName(IEnumerable<string?> takenNames) => UniqueName(NewLibraryBaseName, takenNames);

    /// <summary>
    /// The name of a duplicate of <paramref name="name"/>: "GitHub - Copy", or "GitHub - Copy 2" and so on while that is
    /// taken ("Word pack - Copy" for a blank name). The separator is an ASCII hyphen.
    /// </summary>
    public static string CopyName(string? name, IEnumerable<string?> takenNames)
    {
        var committed = LibraryMetadata.Commit(name);
        return UniqueName(committed.Length == 0 ? "Word pack - Copy" : committed + " - Copy", takenNames);
    }

    /// <summary>
    /// The display name the kept-version notice gives a version of a library someone changed outside Scribe. The kept
    /// file keeps its own bytes and header (decision 17), so only the notice uses this name.
    /// </summary>
    public static string ChangedOutsideName(string? name) => LibraryMetadata.Commit(name) + " (changed outside Scribe)";

    /// <summary>
    /// <paramref name="baseName"/> in committed form ("Word pack" when that is blank), or the first of "base 2", "base 3"
    /// and so on that no name in <paramref name="takenNames"/> holds.
    /// </summary>
    public static string UniqueName(string? baseName, IEnumerable<string?> takenNames)
    {
        ArgumentNullException.ThrowIfNull(takenNames);
        var committed = LibraryMetadata.Commit(baseName);
        if (committed.Length == 0)
        {
            committed = "Word pack";
        }

        var taken = NameSet(takenNames);
        if (!taken.Contains(committed))
        {
            return committed;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{committed} {n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/>, in committed form, equals one of <paramref name="names"/> without case. A blank
    /// name is never taken: that it is empty is its own problem.
    /// </summary>
    public static bool IsNameTaken(string? name, IEnumerable<string?> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var committed = LibraryMetadata.Commit(name);
        return committed.Length > 0 && NameSet(names).Contains(committed);
    }

    /// <summary>
    /// The slug of a library name: lowercased with the invariant culture, letters and digits of any script kept, every
    /// other run of characters collapsed to one hyphen, no hyphen at either end, and "library" when nothing is left. The
    /// rule <c>DictionaryLibraryService.Slugify</c> has always applied, character for character. That fallback is part of
    /// an id, never shown as a name, so it stays "library" whatever the page calls a library.
    /// </summary>
    public static string Slug(string? name)
    {
        var value = name ?? string.Empty;
        var sb = new StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingDash && sb.Length > 0)
                {
                    sb.Append('-');
                }

                sb.Append(ch);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        var slug = sb.ToString();
        return slug.Length == 0 ? "library" : slug;
    }

    /// <summary>
    /// The id a new custom library named <paramref name="name"/> gets: <c>custom-</c> and its <see cref="Slug"/>, then
    /// <c>-2</c>, <c>-3</c> and so on appended while the candidate is in <paramref name="takenIds"/>, compared without
    /// case. The caller passes every id a new library must not take: the built-in ids (shipped and retired), the ids of
    /// the catalog's and the draft's libraries, the stems of every file in the libraries folder, and the original ids of
    /// Recently deleted entries, so a restore can still bring its old id back.
    /// </summary>
    public static string NewCustomId(string? name, IEnumerable<string?> takenIds) => Unique(CustomIdPrefix + Slug(name), takenIds);

    /// <summary>
    /// The logical id of a hand-placed file whose stem <paramref name="fileStem"/> is a built-in id: <c>custom-</c> and the
    /// stem as it is (not slugged, so the remap is recognizable), then the same suffix rule as <see cref="NewCustomId"/>.
    /// </summary>
    public static string RemapId(string fileStem, IEnumerable<string?> takenIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileStem);
        return Unique(CustomIdPrefix + fileStem, takenIds);
    }

    private static string Unique(string candidate, IEnumerable<string?> takenIds)
    {
        ArgumentNullException.ThrowIfNull(takenIds);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in takenIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                taken.Add(id.Trim());
            }
        }

        if (!taken.Contains(candidate))
        {
            return candidate;
        }

        for (var n = 2; ; n++)
        {
            var suffixed = $"{candidate}-{n}";
            if (!taken.Contains(suffixed))
            {
                return suffixed;
            }
        }
    }

    private static HashSet<string> NameSet(IEnumerable<string?> names)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var committed = LibraryMetadata.Commit(name);
            if (committed.Length > 0)
            {
                set.Add(committed);
            }
        }

        return set;
    }
}
