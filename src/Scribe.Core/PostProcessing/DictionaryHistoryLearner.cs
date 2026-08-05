using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>Builds high-confidence dictionary entries from recurring terms in dictation history.</summary>
/// <remarks>
/// History is written after the dictionary has already run, so it records what the recognizer got
/// right and never the misrecognition a rule would repair. That makes it usable for finding which
/// terms matter to this user, but not for inventing the pattern side of a rule. See
/// <see cref="DictionaryTermVariants"/> for the two patterns that are genuinely recoverable, and
/// note that anything requiring a phonetic guess has to come from a user correction instead.
/// </remarks>
public static class DictionaryHistoryLearner
{
    public static IReadOnlyList<DictionaryEntry> BuildEntries(
        IEnumerable<HistoryEntry> history,
        IEnumerable<DictionaryEntry> existing,
        int minDictations = 3,
        int maxSuggestions = 12)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var existingList = existing as IReadOnlyCollection<DictionaryEntry> ?? [.. existing];

        // A disabled row is still a decision the user made, and the miner only skips terms it can
        // see: without this, "learn from history" would re-add a pattern somebody switched off.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in existingList)
        {
            known.Add(entry.Pattern.Trim());
        }

        return DictionarySuggestionMiner.Mine(history, existingList, minDictations, maxSuggestions)
            .SelectMany(suggestion => DictionaryTermVariants
                .For(suggestion.Term)
                .Select(pattern => (Pattern: pattern, suggestion.Term)))
            .Where(candidate => known.Add(candidate.Pattern))
            .Select(candidate => DictionaryEntry.New(candidate.Pattern, candidate.Term))
            .ToList();
    }
}