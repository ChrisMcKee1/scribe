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

    @MainActor
    func testTheControllerUsesItsAdmittedRulesAfterRecognitionAndASettingsPublication() async throws {
        let harness = makeHarness()
        harness.load(dictionary: [DictionaryEntry(pattern: "term", replacement: "Before")])
        let recognition = DictationGate<String>()
        harness.transcriber.steps = [.gate(recognition)]
        await harness.dictate()
        await waitUntil("recognition is held") { recognition.waitingCount == 1 }
        harness.load(dictionary: [DictionaryEntry(pattern: "term", replacement: "After")])
        recognition.open("term")
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.fakeInjector.texts, ["Before "])
    }

    @MainActor
    func testPermissionWithdrawalProducesAHoldbackNotAProviderFailure() async throws {
        let fixture = makeCleanupStore()
        let store = fixture.store
        store.isEnabled = true
        store.providerKind = .openAICompatible
        store.openAIBaseURL = "https://example.invalid/v1"
        store.openAIModel = "model"
        let requests = RequestLog()
        let gate = CleanupSendGate()
        let cache = CleanupProviderCache(
            store: store, environment: [:],
            factory: .testing(
                session: makeStubSession { request in
                    requests.record(request)
                    return StubReply.completion(request, "Must not send.")
                }),
            sendGate: gate)
        let harness = makeHarness(cleanupSource: LiveDictationCleanup(cache: cache))
        let first = snapshot("Before", hash: "first")
        gate.publishVocabulary(first.aiScope)
        harness.rules.install(first)
        let recognition = DictationGate<String>()
        harness.transcriber.steps = [.gate(recognition)]
        await harness.dictate()
        await waitUntil("recognition is held") { recognition.waitingCount == 1 }
        gate.publishVocabulary(.none)
        harness.rules.install(snapshot("After", hash: "next"))
        recognition.open("term")
        await harness.waitUntilProcessed()
        XCTAssertEqual(requests.count, 0)
        XCTAssertEqual(harness.fakeInjector.texts, ["Before "])
        XCTAssertEqual(harness.reports.latest?.cleanupOutcome, .heldBack)
        XCTAssertEqual(harness.reports.latest?.cleanupHoldback, .vocabularyChanged)
        XCTAssertTrue(harness.presenter.noticesShown().contains(.cleanupHeldBack))
        XCTAssertFalse(harness.notifier.kinds.contains(.cleanupFellBack))
    }
}
