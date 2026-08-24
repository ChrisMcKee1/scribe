import Foundation

/// Orchestrates history mining end to end: mine recurring jargon, derive safe dictionary
/// patterns for each, dedupe against what's already known, and return ready-to-insert entries.
/// Mirrors `Scribe.Core.PostProcessing.DictionaryHistoryLearner` on Windows.
enum DictionaryHistoryLearner {
    /// Builds new `DictionaryEntry` values learned from `history`, skipping any pattern already
    /// present (case-insensitively) in `existing`. Entries are built enabled and whole-word,
    /// matching how a user-facing suggestion should behave once accepted.
    static func buildEntries(
        history: [DictationHistoryRecord],
        existing: [DictionaryEntry],
        minDictations: Int = 3,
        maxSuggestions: Int = 12
    ) -> [DictionaryEntry] {
        let suggestions = DictionarySuggestionMiner.mine(
            entries: history,
            existing: existing,
            minDictations: minDictations,
            maxSuggestions: maxSuggestions)

        guard !suggestions.isEmpty else {
            return []
        }

        var knownPatterns = Set(existing.map { $0.pattern.trimmingCharacters(in: .whitespaces).lowercased() })
        var entries: [DictionaryEntry] = []

        for suggestion in suggestions {
            for pattern in DictionaryTermVariants.variants(for: suggestion.term) {
                let key = pattern.lowercased()
                guard !key.isEmpty, knownPatterns.insert(key).inserted else {
                    continue
                }
                entries.append(
                    DictionaryEntry(
                        pattern: pattern,
                        replacement: suggestion.term,
                        wholeWord: true,
                        enabled: true))
            }
        }

        return entries
    }
}
