import Foundation

/// The word-pack entries AI cleanup may receive as vocabulary. Read from the committed word pack vocabulary's AI-permitted entries.
@MainActor
protocol CleanupVocabularyLibrarySource {
    func cleanupVocabularyEntries() async -> [DictionaryEntry]
}

extension DictionaryLibraryService: CleanupVocabularyLibrarySource {
    func cleanupVocabularyEntries() async -> [DictionaryEntry] {
        (try? await loadVocabulary().aiEntries) ?? []
    }
}
