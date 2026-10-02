import Foundation

/// Startup admissions can wait for the first publication. Once resolved, an admission never sees a later one.
@MainActor
final class FrozenDictationRules: DictationRuleSource {
    private static let emptyRules = TextPostProcessor.CompiledRules(
        dictionaryEntries: [], snippets: [], libraryEntries: [])
    private var snapshot: DictationRuleSnapshot?
    private let gate: StartupGate
    private var settled: Bool

    init(snapshot: DictationRuleSnapshot?, gate: StartupGate) {
        self.snapshot = snapshot
        self.gate = gate
        settled = snapshot != nil || gate.isOpen
    }

    func installFirst(_ snapshot: DictationRuleSnapshot) {
        guard !settled else { return }
        self.snapshot = snapshot
        settled = true
    }

    var isLoaded: Bool { settled }
    var appProfiles: [AppProfile] { snapshot?.appProfiles ?? [] }
    var cleanupVocabulary: CleanupVocabulary { snapshot?.cleanupVocabulary ?? .none }
    var aiScope: AiVocabularyScope { snapshot?.aiScope ?? .none }
    func admitGeneration() -> any DictationRuleSource { self }

    func waitUntilLoaded() async -> StartupGate.State {
        let state = await gate.wait()
        settled = true
        return state
    }

    func postProcess(_ text: String) -> TextPostProcessingResult {
        (snapshot?.rules ?? Self.emptyRules).processDetailed(text)
    }

    func correctVocabulary(_ text: String) -> VocabularyPass {
        (snapshot?.rules ?? Self.emptyRules).correctVocabulary(text)
    }

    func finishAfterCleanup(_ reply: String, after pass: VocabularyPass) -> TextPostProcessingResult {
        TextPostProcessor.restore(reply, after: pass).result
    }
}
