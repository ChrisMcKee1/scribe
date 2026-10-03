import XCTest

@testable import Scribe

final class ContextBudgetTests: XCTestCase {
    func testRequestFitReservesTheActualWrappedTextPromptAnswerAndMargin() {
        let context = 2048
        let prompt = String(repeating: "a", count: 260)
        let transcript = CleanupPrompt.wrapTranscript("日本語")
        let room =
            context - ContextBudget.chatTemplateTokens - 64
            - TokenEstimate.vocabulary(prompt) - TokenEstimate.transcript(transcript)
        XCTAssertTrue(
            ContextBudget.requestFits(
                CleanupRequest(transcript: transcript, writingStylePrompt: prompt, maxOutputTokens: room),
                contextTokens: context))
        XCTAssertFalse(
            ContextBudget.requestFits(
                CleanupRequest(transcript: transcript, writingStylePrompt: prompt, maxOutputTokens: room + 1),
                contextTokens: context))
        XCTAssertFalse(ContextBudget.requestFits(CleanupRequest(transcript: "small"), contextTokens: context))
        XCTAssertFalse(
            ContextBudget.requestFits(
                CleanupRequest(transcript: String(repeating: "語", count: 2048), maxOutputTokens: 1),
                contextTokens: context))
    }

    func testTheEstimateCountsProseAndVocabularyAtTheirOwnRates() {
        XCTAssertEqual(TokenEstimate.prose(""), 0)
        XCTAssertEqual(TokenEstimate.prose(String(repeating: "a", count: 360)), 100)
        XCTAssertEqual(TokenEstimate.vocabulary(String(repeating: "a", count: 260)), 100)
        XCTAssertEqual(TokenEstimate.transcript("abc"), TokenEstimate.prose("abc") + TokenEstimate.shortTextAllowance)
        XCTAssertEqual(TokenEstimate.prose("日本語のテキストです"), 10)
        XCTAssertEqual(TokenEstimate.vocabulary("ab日本語"), 4)
    }

    func testTheDictatedTextAndItsAnswerTakeTheirRoomBeforeTheVocabulary() {
        let instructions = "Rewrite the dictation."
        let shortText = "send the report to sarah"
        let longText = Array(repeating: "we need to ship the build by thursday", count: 60).joined(separator: " ")

        let shortRoom = ContextBudget.vocabularyTokens(
            4096,
            instructions: instructions,
            transcript: shortText,
            outputCeiling: 256)
        let longRoom = ContextBudget.vocabularyTokens(
            4096,
            instructions: instructions,
            transcript: longText,
            outputCeiling: 256)

        XCTAssertGreaterThan(shortRoom, longRoom)
        XCTAssertEqual(
            longRoom - 100,
            ContextBudget.vocabularyTokens(
                4096,
                instructions: instructions,
                transcript: longText,
                outputCeiling: 356))
    }

    func testWholeVocabularyGoesWhenItFitsAndFallsBackToMentionedTermsWhenItDoesNot() {
        let entries = [
            DictionaryEntry(pattern: "kubernetes", replacement: "Kubernetes"),
            DictionaryEntry(pattern: "ollama", replacement: "Ollama"),
            DictionaryEntry(pattern: "kubeflow", replacement: "Kubeflow"),
        ]
        let vocabulary = CleanupVocabulary(glossaryEntries: entries)

        let whole = vocabulary.glossary(
            mode: .mentioned,
            everything: true,
            dictation: "ollama",
            tokenBudget: 1_000_000,
            maxTerms: .max)
        XCTAssertEqual(whole, CleanupPrompt.buildGlossary(entries))

        let fitted = vocabulary.glossary(
            mode: .mentioned,
            everything: false,
            dictation: "ollama",
            tokenBudget: 200,
            maxTerms: CleanupPrompt.maxGlossaryTermsLocal)
        XCTAssertNotNil(fitted)
        XCTAssertTrue(fitted?.contains("Ollama") == true)
        XCTAssertFalse(fitted?.contains("Kubernetes") == true)
    }
}
