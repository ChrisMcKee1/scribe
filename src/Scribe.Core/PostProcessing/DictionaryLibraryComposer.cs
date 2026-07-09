using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Pure composition of the effective dictionary from the user's base entries plus any enabled
/// libraries. De-duplicates by spoken form (trimmed, case-insensitive) so a term defined in more
/// than one place resolves to a single rule — mirroring the unique-pattern rule the base dictionary
/// enforces in the database. The first occurrence wins, and callers pass the base dictionary first
/// so the user's own entries always take precedence over a library's.
/// </summary>
public static class DictionaryLibraryComposer
{
    /// <summary>
    /// Flattens the enabled entries of the supplied libraries into one de-duplicated list, in
    /// library order then entry order. Only entries whose <see cref="DictionaryEntry.Enabled"/> flag
    /// is set contribute.
    /// </summary>
    public static IReadOnlyList<DictionaryEntry> ComposeLibraries(IEnumerable<DictionaryLibrary> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        return Deduplicate(libraries.Where(l => l is not null).SelectMany(l => l.EnabledEntries));
    }

    /// <summary>
    /// Merges the base dictionary with library entries into the effective rule set. Base entries come
    /// first and win on conflict; a library entry is appended only when its spoken form is not already
    /// present. Used by both the deterministic post-processor and the AI glossary builder so the two
    /// stay consistent.
    /// </summary>
    public static IReadOnlyList<DictionaryEntry> Merge(
        IEnumerable<DictionaryEntry> baseEntries, IEnumerable<DictionaryEntry> libraryEntries)
    {
        ArgumentNullException.ThrowIfNull(baseEntries);
        ArgumentNullException.ThrowIfNull(libraryEntries);
        return Deduplicate(baseEntries.Concat(libraryEntries));
    }

    private static List<DictionaryEntry> Deduplicate(IEnumerable<DictionaryEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DictionaryEntry>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var key = entry.Pattern?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (seen.Add(key))
            {
                result.Add(entry);
            }
        }

        return result;
    }
}
