import XCTest

@testable import Scribe

final class CleanupVocabularyTests: XCTestCase {
    func testOnlyASingleLineReplacementWithinTheLimitIsVocabulary() {
        XCTAssertTrue(
            TextPostProcessor.isVocabulary(DictionaryEntry(pattern: "kubernetes", replacement: "Kubernetes")))
        XCTAssertFalse(
            TextPostProcessor.isVocabulary(DictionaryEntry(pattern: "sign off", replacement: "Best,\nChris")))
        XCTAssertFalse(
            TextPostProcessor.isVocabulary(
                DictionaryEntry(
                    pattern: "footer",
                    replacement: "Sent from Scribe. " + String(repeating: "x", count: 90))))
    }

    func testTemplateLikeReplacementsAreLeftOutOfTheGlossaryAndItsCount() {
        let template = "Best regards,\nChris McKee"
        let entries = [
            DictionaryEntry(pattern: "kubernetes", replacement: "Kubernetes"),
            DictionaryEntry(pattern: "sign off", replacement: template),
            DictionaryEntry(pattern: "a p i m", replacement: "APIM"),
        ]

        let glossary = CleanupPrompt.buildGlossary(entries)
        let count = CleanupPrompt.countGlossary(entries)

        XCTAssertTrue(glossary.contains("- Kubernetes"))
        XCTAssertTrue(glossary.contains("- APIM (transcribed as \"a p i m\")"))
        XCTAssertFalse(glossary.contains("sign off"))
        XCTAssertEqual(count, GlossaryCount(included: 2, eligible: 2))
    }

    func testMentionedModeRendersItsOwnGlossary() {
        let vocabulary = CleanupVocabulary(glossaryEntries: [
            DictionaryEntry(pattern: "o llama", replacement: "Ollama"),
            DictionaryEntry(pattern: "kubernetes", replacement: "Kubernetes"),
        ])

        XCTAssertEqual(
            vocabulary.glossary(maxTerms: 80, mode: .all, dictation: "we tried it locally through o llama"),
            CleanupPrompt.buildGlossary(vocabulary.glossaryEntries, maxTerms: 80))
        XCTAssertNil(vocabulary.glossary(maxTerms: 80, mode: .none, dictation: "we tried it locally through o llama"))

        let mentioned = vocabulary.glossary(
            maxTerms: 80,
            mode: .mentioned,
            dictation: "we tried it locally through o llama")
        XCTAssertNotNil(mentioned)
        XCTAssertTrue(mentioned?.contains("Ollama") == true)
        XCTAssertFalse(mentioned?.contains("Kubernetes") == true)
        XCTAssertNil(vocabulary.glossary(maxTerms: 80, mode: .mentioned, dictation: nil))
    }
}
