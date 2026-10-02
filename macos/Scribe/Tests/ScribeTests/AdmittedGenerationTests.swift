import XCTest

@testable import Scribe

final class AdmittedGenerationTests: XCTestCase {
    private func snapshot(_ written: String, hash: String) -> DictationRuleSnapshot {
        DictationRuleSnapshot(
            PersistenceRuleSet(
                dictionaryEntries: [DictionaryEntry(pattern: "term", replacement: written)],
                snippets: [], appProfiles: []),
            libraryEntries: [],
            cleanupVocabularyEntries: [DictionaryEntry(pattern: "pack word", replacement: written)],
            aiScope: AiVocabularyScope(generation: 1, permittedContent: ["pack": hash]))
    }

    @MainActor
    func testOneAdmissionKeepsRulesVocabularyAndPermissionTogetherAcrossPublication() {
        let startup = StartupGate()
        startup.open(.ready)
        let source = DictationRules(gate: startup)
        source.install(snapshot("Before", hash: "old"))
        let admitted = source.admitGeneration()
        source.install(snapshot("After", hash: "new"))
        XCTAssertEqual(admitted.postProcess("term").text, "Before")
        XCTAssertEqual(admitted.correctVocabulary("term").text, "Before")
        XCTAssertEqual(admitted.aiScope, AiVocabularyScope(generation: 1, permittedContent: ["pack": "old"]))
        XCTAssertEqual(source.admitGeneration().postProcess("term").text, "After")
    }

    @MainActor
    func testAnAdmissionDuringStartupKeepsTheFirstPublicationNotTheLatestOne() async {
        let startup = StartupGate()
        let source = DictationRules(gate: startup)
        let admitted = source.admitGeneration()
        source.install(snapshot("First", hash: "first"))
        source.install(snapshot("Second", hash: "second"))
        startup.open(.ready)
        _ = await admitted.waitUntilLoaded()
        XCTAssertEqual(admitted.postProcess("term").text, "First")
        XCTAssertEqual(admitted.aiScope, AiVocabularyScope(generation: 1, permittedContent: ["pack": "first"]))
    }

    @MainActor
    func testAnAdmissionWithoutStoredRulesCannotGainRulesDuringProcessing() async {
        let startup = StartupGate()
        let source = DictationRules(gate: startup)
        let admitted = source.admitGeneration()
        startup.open(.withoutStoredRules)
        _ = await admitted.waitUntilLoaded()
        source.install(snapshot("Repaired", hash: "repaired"))
        XCTAssertEqual(admitted.postProcess("term").text, "term")
        XCTAssertEqual(admitted.aiScope, .none)
    }
}
