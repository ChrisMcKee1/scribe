import Foundation

/// Pure composition of the effective dictionary from the user's base entries plus any enabled
/// libraries. De-duplicates by spoken form (trimmed, case-insensitive) so a term defined in more
/// than one place resolves to a single rule, mirroring the unique-pattern rule the base dictionary
/// enforces in SQLite. The first occurrence wins, and callers pass the base dictionary first so
/// the user's own entries always take precedence over a library's. Direct port of Windows'
/// `Scribe.Core.PostProcessing.DictionaryLibraryComposer`.
enum DictionaryLibraryComposer {
    /// Flattens the enabled entries of the supplied libraries into one de-duplicated list, in
    /// library order then entry order. Only entries whose `enabled` flag is set contribute.
    static func composeLibraries(_ libraries: [DictionaryLibrary]) -> [DictionaryEntry] {
        deduplicate(libraries.flatMap(\.enabledEntries))
    }

    /// Merges the base dictionary with library entries into the effective rule set. Base entries
    /// come first and win on conflict; a library entry is appended only when its spoken form is
    /// not already present. Used by both the deterministic post-processor and (in the future) an
    /// AI glossary builder, so the two stay consistent.
    static func merge(baseEntries: [DictionaryEntry], libraryEntries: [DictionaryEntry]) -> [DictionaryEntry] {
        deduplicate(baseEntries + libraryEntries)
    }

    private static func deduplicate(_ entries: [DictionaryEntry]) -> [DictionaryEntry] {
        var seen = Set<String>()
        var result: [DictionaryEntry] = []
        for entry in entries {
            let key = entry.pattern.trimmingCharacters(in: .whitespaces).lowercased()
            guard !key.isEmpty else { continue }
            if seen.insert(key).inserted {
                result.append(entry)
            }
        }
        return result
    }
}
