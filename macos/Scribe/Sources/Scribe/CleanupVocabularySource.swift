import Foundation

/// The word-pack entries AI cleanup may receive as vocabulary. This is a temporary seam until the library snapshot
/// API lands on macOS; the current implementation uses the enabled word packs as-is, and a later merge can narrow it
/// without changing dictation wiring.
protocol CleanupVocabularyLibrarySource {
    func cleanupVocabularyEntries() async -> [DictionaryEntry]
}

extension DictionaryLibraryService: CleanupVocabularyLibrarySource {
    func cleanupVocabularyEntries() async -> [DictionaryEntry] {
        enabledLibraryEntries()
    }
}
