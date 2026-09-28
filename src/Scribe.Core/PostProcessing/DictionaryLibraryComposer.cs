using Scribe.Core.Libraries;
using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>
/// Pure composition of the effective dictionary from the user's base entries plus any enabled
/// libraries. De-duplicates by spoken form (trimmed, case-insensitive) so a term defined in more
/// than one place resolves to a single rule, mirroring the unique-pattern rule the base dictionary
/// enforces in the database. The first occurrence wins, and callers pass the base dictionary first
/// so the user's own entries always take precedence over a library's.
/// </summary>
/// <remarks>
/// <see cref="ComposeLibraries"/> is 0.4.3's composition: first wins, in precedence order, over whatever libraries it is
/// given. The library model composes through <see cref="LibraryComposition"/>, with Decision 1's tiers and legacy
/// markers; this method stays for the library service's interim composer and for the tests that pin 0.4.3's behaviour.
/// <see cref="Merge"/> serves both: the dictionary wins, keyed by the same trimmed, case-insensitive spoken form as
/// <see cref="LibraryTermKey"/>.
/// </remarks>
public static class DictionaryLibraryComposer
{
    /// <summary>
    /// Flattens the enabled entries of the supplied libraries into one de-duplicated list, in
    /// precedence order (<see cref="LibraryPrecedence"/>) then entry order, whatever order the
    /// libraries arrive in. Only entries whose <see cref="DictionaryEntry.Enabled"/> flag is set
    /// contribute.
    /// </summary>
    /// <remarks>
    /// Ordering here rather than trusting the caller is what keeps the Libraries list's A to Z order
    /// out of dictation: a caller holding libraries in display order still gets the winners, and
    /// the glossary order, that dictation uses.
    /// </remarks>
    public static IReadOnlyList<DictionaryEntry> ComposeLibraries(IEnumerable<DictionaryLibrary> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        return Deduplicate(LibraryPrecedence.Order(libraries).SelectMany(l => l.EnabledEntries), capacity: 0);
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

        // Every entry kept is one of the inputs, so when both know their counts the set and the list are sized once rather
        // than grown from empty; the counts are read without enumerating either input.
        var capacity = baseEntries.TryGetNonEnumeratedCount(out var first) &&
                       libraryEntries.TryGetNonEnumeratedCount(out var second) &&
                       first <= int.MaxValue - second
            ? first + second
            : 0;
        return Deduplicate(baseEntries.Concat(libraryEntries), capacity);
    }

    private static List<DictionaryEntry> Deduplicate(IEnumerable<DictionaryEntry> entries, int capacity)
    {
        var seen = new HashSet<string>(capacity, StringComparer.OrdinalIgnoreCase);
        var result = new List<DictionaryEntry>(capacity);
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
