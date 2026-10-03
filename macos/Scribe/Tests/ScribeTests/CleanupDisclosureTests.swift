import XCTest

@testable import Scribe

final class CleanupDisclosureTests: XCTestCase {
    func testWhatCleanupSendsQuotesTheLimitsTheCodeEnforces() {
        let text = CleanupDisclosure.whatCleanupSends

        XCTAssertTrue(text.contains("Foundry Local runs cleanup on this Mac"))
        XCTAssertTrue(text.contains("Microsoft Foundry"))
        XCTAssertTrue(text.contains("any other AI service you set up"))
        XCTAssertTrue(text.contains("with every cleanup request"))
        XCTAssertTrue(text.contains("the text Scribe recognized for that dictation"))
        XCTAssertTrue(text.contains("writing style"))
        XCTAssertTrue(text.contains("the word packs the dictation appears to mention"))
        XCTAssertTrue(
            text.contains(
                "up to 5,000 words or phrases and 24,000 characters, or 80 words or phrases with the short "
                    + "instructions"))
        XCTAssertTrue(text.contains("runs past 100 characters"))
        XCTAssertFalse(text.localizedCaseInsensitiveContains("only the transcribed text"))
    }

    func testConnectionAndLocalReadyingDisclosureExcludesDictatedContent() {
        let text = CleanupDisclosure.whatCleanupNeverSends

        XCTAssertTrue(text.contains("Test Connection sends a short request"))
        XCTAssertTrue(text.contains("the current writing style and guardrails"))
        XCTAssertTrue(text.contains("none of your vocabulary"))
        XCTAssertTrue(text.contains("Ollama or LM Studio on this Mac"))
        XCTAssertTrue(text.contains("fixed readying request"))
        XCTAssertTrue(text.contains("Neither request contains dictated text or vocabulary"))
        XCTAssertTrue(text.contains("each native request first asks Ollama for the model's context limit"))
        XCTAssertTrue(text.contains("only its model name and any key saved for that address"))
        XCTAssertTrue(text.contains("snippet templates"))
        XCTAssertTrue(text.contains("audio never leaves this Mac"))
    }

    func testProviderSummariesNameTheDestination() {
        XCTAssertEqual(
            CleanupDisclosure.summary(for: .foundryLocal, endpoint: nil),
            "Your text, writing style and vocabulary stay on this Mac. Audio never leaves it.")
        XCTAssertEqual(
            CleanupDisclosure.summary(for: .openAICompatible, endpoint: "https://openrouter.ai/api/v1"),
            "Each cleanup sends the text Scribe heard, your writing style, and the dictionary and word pack words it "
                + "mentions to the address you enter. Audio never leaves this Mac.")
        XCTAssertEqual(
            CleanupDisclosure.summary(for: .openAICompatible, endpoint: LocalAiServer.lmStudioAddress),
            "Your text, writing style and vocabulary stay on this Mac. Audio never leaves it.")
        XCTAssertEqual(
            CleanupDisclosure.summary(for: .microsoftFoundry, endpoint: nil),
            "Each cleanup sends the text Scribe heard, your writing style, and the dictionary and word pack words it "
                + "mentions to your Microsoft Foundry deployment. Audio never leaves this Mac.")
    }

    func testTheSettingsViewUsesTheSharedDisclosureText() throws {
        let root = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let aiCleanupPage = try String(
            contentsOf: root.appendingPathComponent("Sources").appendingPathComponent("Scribe")
                .appendingPathComponent("SettingsAICleanupPage.swift"))
        let settingsModel = try String(
            contentsOf: root.appendingPathComponent("Sources").appendingPathComponent("Scribe")
                .appendingPathComponent("CleanupSettingsModel.swift"))
        let section = try String(
            contentsOf: root.appendingPathComponent("Sources").appendingPathComponent("Scribe")
                .appendingPathComponent("CleanupDisclosureSection.swift"))

        XCTAssertTrue(aiCleanupPage.contains("Text(model.cleanupSummary)"))
        XCTAssertTrue(settingsModel.contains("CleanupDisclosure.summary("))
        XCTAssertTrue(aiCleanupPage.contains("CleanupDisclosureSection("))
        XCTAssertTrue(section.contains("CleanupDisclosure.whatCleanupSends"))
        XCTAssertTrue(section.contains("CleanupDisclosure.whatCleanupNeverSends"))
    }
}
