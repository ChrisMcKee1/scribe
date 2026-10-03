import XCTest

@testable import Scribe

final class LocalModelTuningTextTests: XCTestCase {
    func testAStaleAppSelectionCannotGiveAnotherAddressLocalTuning() {
        let store = makeCleanupStore().store
        store.providerKind = .openAICompatible
        store.selectedLocalApp = .ollama
        store.ollamaContextTokens = 32768
        store.ollamaSendWholeVocabulary = true
        for endpoint in ["https://remote.example/v1", LocalAiServer.lmStudioAddress, "http://localhost:9000/v1"] {
            store.openAIBaseURL = endpoint
            XCTAssertEqual(LocalModelTuning.appForSettings(store.snapshot()), .none)
            XCTAssertEqual(LocalModelTuning.forSettings(store.snapshot()), .none)
        }
        store.openAIBaseURL = LocalAiServer.ollamaAddress
        XCTAssertEqual(
            LocalModelTuning.forSettings(store.snapshot()),
            LocalModelTuning(contextTokens: 32768, sendWholeVocabulary: true))
    }

    func testTheContextSizeListStartsWithTheAppsOwnSetting() {
        let sizes = LocalModelTuningText.contextSizes("Ollama")

        XCTAssertEqual(sizes[0].tokens, 0)
        XCTAssertEqual(sizes[0].label, "Ollama's setting")
        XCTAssertEqual(sizes.dropFirst().map(\.tokens), ContextBudget.offeredSizes)
        XCTAssertTrue(sizes.contains { $0.tokens == 32768 && $0.label == "32K (32,768 tokens)" })
        XCTAssertEqual(LocalModelTuningText.sizeLabel(3000), "3,000 tokens")
    }

    func testTheStatusSaysWhatTheModelReadsAndWhetherTheVocabularyFits() {
        XCTAssertEqual(
            LocalModelTuningText.contextStatus(
                "Ollama",
                inUse: 4096,
                asked: 32768,
                vocabularyTokens: 8308,
                vocabularyRoom: 1480),
            "The model is reading up to 4,096 tokens. Your whole vocabulary needs about 8,308 tokens; at this size, "
                + "about 1,480 of them fit beside a dictation.")
        XCTAssertEqual(
            LocalModelTuningText.contextStatus(
                "LM Studio",
                inUse: 0,
                asked: 32768,
                vocabularyTokens: 8308,
                vocabularyRoom: 29000),
            "Scribe asks for 32,768 tokens when the model loads. Your whole vocabulary needs about 8,308 tokens, "
                + "which fits at this size.")
    }
}
