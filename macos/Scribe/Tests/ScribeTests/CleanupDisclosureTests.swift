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
        XCTAssertTrue(text.contains("LM Studio's loaded size is checked before text is sent"))
        XCTAssertTrue(text.contains("only the fixed readying request loads it"))
        XCTAssertTrue(text.contains("Ollama at its own size is checked before and after"))
        XCTAssertTrue(text.contains("fixed readying request"))
        XCTAssertTrue(text.contains("Neither request contains dictated text or vocabulary"))
        XCTAssertTrue(text.contains("each native request first asks Ollama for the model's context limit"))
        XCTAssertTrue(text.contains("only its model name and any key saved for that address"))
        XCTAssertTrue(text.contains("its loaded size, without sending text"))
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

    /// Windows' `CleanupDisclosure.SuggestionConsentFor`, with the Mac's destinations.
    func testTheSuggestionQuestionSaysWhatLearnFromHistorySendsAndWhere() {
        let remote = CleanupConnection.Target.openAICompatible(
            serviceURL: URL(string: "https://openrouter.ai/api/v1")!, model: "m", apiKey: .environment,
            apiStyle: .chatCompletions)
        let text = CleanupDisclosure.suggestionConsent(for: remote)

        XCTAssertEqual(CleanupDisclosure.suggestionConsentTitle, "Send recent dictations to your AI service?")
        XCTAssertEqual(
            text,
            "To suggest vocabulary, Scribe will send up to 6,000 characters of your most recent dictations, as they "
                + "were inserted, to the AI service you set up. That text can include words your dictionary and "
                + "snippets added. Your dictionary list, your writing style and audio are not sent. If where AI "
                + "cleanup runs changes before the request goes out, nothing is sent.")
        XCTAssertTrue(text.contains(AIDictionarySuggester.defaultMaxSampleChars.formatted()))
        XCTAssertFalse(DashNormalizer.containsDash(text))
    }

    func testTheSuggestionQuestionNamesEachDestinationAndOnlyFoundryLocalGoesUnasked() {
        let lmStudio = CleanupConnection.Target.openAICompatible(
            serviceURL: URL(string: LocalAiServer.lmStudioAddress)!, model: "m", apiKey: .environment,
            apiStyle: .chatCompletions)
        let ollamaAddress = CleanupConnection.Target.openAICompatible(
            serviceURL: URL(string: LocalAiServer.ollamaAddress)!, model: "m", apiKey: .environment,
            apiStyle: .chatCompletions)
        let foundry = CleanupConnection.Target.microsoftFoundry(
            inferenceBase: URL(string: "https://example.services.ai.azure.com/openai/v1/")!, deployment: "d",
            identity: .azureCli(tenantId: nil))
        let cases: [(CleanupConnection.Target, String, Bool)] = [
            (.foundryLocal(modelAlias: "a"), "Foundry Local, which runs on this Mac", false),
            (.ollama(model: "m"), "Ollama, which runs on this Mac", true),
            (ollamaAddress, "Ollama, which runs on this Mac", true),
            (lmStudio, "LM Studio, which runs on this Mac", true),
            (foundry, "your Microsoft Foundry deployment", true),
        ]
        for (target, destination, asks) in cases {
            XCTAssertEqual(CleanupDisclosure.suggestionDestination(for: target), destination)
            XCTAssertTrue(CleanupDisclosure.suggestionConsent(for: target).contains(" to \(destination). "))
            XCTAssertEqual(CleanupDisclosure.suggestionConsentIsNeeded(for: target), asks, destination)
        }
    }
}
