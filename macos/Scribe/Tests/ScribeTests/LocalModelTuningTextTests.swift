import XCTest

@testable import Scribe

final class LocalModelTuningTextTests: XCTestCase {
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
