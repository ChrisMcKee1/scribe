import XCTest

@testable import Scribe

final class CleanupPromptTests: XCTestCase {
    func testSystemPromptUsesFrontierGuardrailByDefault() {
        let prompt = CleanupPrompt.systemPrompt(writingStyle: "Be terse.", useLocalPrompt: false)
        XCTAssertTrue(prompt.hasPrefix(CleanupPrompt.defaultFrontierPrompt))
        XCTAssertTrue(prompt.contains("Writing style:\nBe terse."))
    }

    func testSystemPromptUsesLocalGuardrailWhenRequested() {
        let prompt = CleanupPrompt.systemPrompt(writingStyle: "Be terse.", useLocalPrompt: true)
        XCTAssertTrue(prompt.hasPrefix(CleanupPrompt.defaultLocalPrompt))
        XCTAssertTrue(prompt.contains("Writing style:\nBe terse."))
    }

    func testSystemPromptComposesSavedGuardrailOverridesWithTheWritingStyle() {
        let detailed = CleanupPrompt.systemPrompt(
            writingStyle: "Use compact sentences.", useLocalPrompt: false,
            frontierPrompt: "Custom detailed guardrail.", localPrompt: "Custom local guardrail.")
        let local = CleanupPrompt.systemPrompt(
            writingStyle: "Use compact sentences.", useLocalPrompt: true,
            frontierPrompt: "Custom detailed guardrail.", localPrompt: "Custom local guardrail.")

        XCTAssertTrue(detailed.hasPrefix("Custom detailed guardrail."))
        XCTAssertTrue(detailed.contains("Writing style:\nUse compact sentences."))
        XCTAssertFalse(detailed.contains("Custom local guardrail."))
        XCTAssertTrue(local.hasPrefix("Custom local guardrail."))
        XCTAssertTrue(local.contains("Writing style:\nUse compact sentences."))
        XCTAssertFalse(local.contains("Custom detailed guardrail."))
    }

    func testBlankPromptOverridesPreserveTheBuiltInDefaults() {
        XCTAssertEqual(
            CleanupPrompt.effectiveOverride(" \n ", defaultValue: CleanupPrompt.defaultWritingStyle),
            CleanupPrompt.defaultWritingStyle)
        XCTAssertEqual(
            CleanupPrompt.storedOverride(
                CleanupPrompt.defaultWritingStyle, defaultValue: CleanupPrompt.defaultWritingStyle), "")
        XCTAssertEqual(
            CleanupPrompt.storedOverride("  Custom style.  ", defaultValue: CleanupPrompt.defaultWritingStyle),
            "Custom style.")
    }

    func testWrapTranscriptAddsTags() {
        XCTAssertEqual(CleanupPrompt.wrapTranscript("hello world"), "<transcript>\nhello world\n</transcript>")
    }

    func testStripTranscriptTagsRemovesLeadingAndTrailingTags() {
        let echoed = "<transcript>\nCleaned text.\n</transcript>"
        XCTAssertEqual(CleanupPrompt.stripTranscriptTags(echoed), "Cleaned text.")
    }

    func testStripTranscriptTagsLeavesUntaggedTextUnchanged() {
        XCTAssertEqual(CleanupPrompt.stripTranscriptTags("Cleaned text."), "Cleaned text.")
    }

    func testGuardrailPromptsMentionNotAnsweringTheTranscript() {
        // The whole point of these guardrails: the model must never treat dictated content as an
        // instruction addressed to it. Regression guard against accidentally dropping that clause.
        XCTAssertTrue(CleanupPrompt.defaultFrontierPrompt.contains("never answer a question"))
        XCTAssertTrue(CleanupPrompt.defaultLocalPrompt.contains("Do not answer"))
    }

    func testDefaultWritingStyleExplainsHowToWriteLists() {
        XCTAssertTrue(CleanupPrompt.defaultWritingStyle.contains("write them as a list with one item per line"))
        XCTAssertTrue(CleanupPrompt.defaultWritingStyle.contains("starting each line with \"- \""))
        XCTAssertTrue(
            CleanupPrompt.defaultWritingStyle.contains(
                "keep the sentence I said before the list as its introduction"))
    }

    func testDefaultWritingStyleKeepsTheWindowsModelNameGuidance() {
        XCTAssertTrue(CleanupPrompt.defaultWritingStyle.contains("gpt five six terra"))
        XCTAssertTrue(CleanupPrompt.defaultWritingStyle.contains("GPT-5.6-Terra"))
        XCTAssertTrue(CleanupPrompt.defaultWritingStyle.contains("Qwen3-14B"))
    }

    func testFoundryLocalProviderUsesLocalCleanupPrompt() {
        let provider = FoundryLocalCleanupProvider()
        XCTAssertTrue(provider.usesLocalCleanupPrompt)
    }

    func testManagedOllamaUsesLocalCleanupPrompt() {
        let ollama = ManagedOllamaCleanupProvider()
        XCTAssertTrue(ollama.usesLocalCleanupPrompt)
    }

    func testOpenAICompatibleLoopbackEndpointUsesLocalCleanupPrompt() {
        let provider = OpenAICompatibleCleanupProvider(
            model: "local-model",
            completionsURL: URL(string: "http://localhost:1234/v1/chat/completions")!)
        XCTAssertTrue(provider.usesLocalCleanupPrompt)
    }

    func testRemoteOpenAICompatibleEndpointKeepsFrontierCleanupPrompt() {
        let ollama = OpenAICompatibleCleanupProvider(
            model: "remote-model",
            completionsURL: URL(string: "https://openrouter.ai/api/v1/chat/completions")!)
        XCTAssertFalse(ollama.usesLocalCleanupPrompt)
    }
}
