import Foundation

@MainActor
final class CleanupVocabularyStatusModel: ObservableObject {
    @Published private(set) var wholeVocabularyTokens = 0

    private let persistenceStore: PersistenceStore
    private let librarySource: any CleanupVocabularyLibrarySource

    init(persistenceStore: PersistenceStore, librarySource: any CleanupVocabularyLibrarySource) {
        self.persistenceStore = persistenceStore
        self.librarySource = librarySource
    }

    func refresh() async {
        do {
            let ruleSet = try await persistenceStore.loadRuleSet()
            let libraryEntries = await librarySource.cleanupVocabularyEntries()
            let vocabulary = CleanupVocabulary(
                glossaryEntries: CleanupPrompt.composeVocabulary(ruleSet.dictionaryEntries, libraryEntries))
            wholeVocabularyTokens = vocabulary.wholeGlossaryTokens
        } catch {
            wholeVocabularyTokens = 0
        }
    }
}
