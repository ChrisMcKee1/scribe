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

    func testFoundryLocalProviderUsesLocalCleanupPrompt() {
        let provider = FoundryLocalCleanupProvider()
        XCTAssertTrue(provider.usesLocalCleanupPrompt)
    }

    func testOtherProvidersDefaultToFrontierCleanupPrompt() {
        let ollama = ManagedOllamaCleanupProvider()
        XCTAssertFalse(ollama.usesLocalCleanupPrompt)
    }
}
