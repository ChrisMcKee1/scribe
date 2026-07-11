using Scribe.Core.Models;

namespace Scribe.Core.PostProcessing;

/// <summary>Builds high-confidence dictionary entries from recurring terms in dictation history.</summary>
public static class DictionaryHistoryLearner
{
    public static IReadOnlyList<DictionaryEntry> BuildEntries(
        IEnumerable<HistoryEntry> history,
        IEnumerable<DictionaryEntry> existing,
        int minDictations = 3,
        int maxSuggestions = 12) =>
        DictionarySuggestionMiner.Mine(history, existing, minDictations, maxSuggestions)
            .Select(suggestion => DictionaryEntry.New(
                suggestion.Term.ToLowerInvariant(),
                suggestion.Term))
            .ToList();
}