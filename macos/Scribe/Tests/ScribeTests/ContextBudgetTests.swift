import XCTest

@testable import Scribe

final class ContextBudgetTests: XCTestCase {
    func testPlanningNeverEnlargesTheConfiguredBudgetAndNeverTrustsUnknownCopies() throws {
        let empty = LocalServerState(reach: .reached, models: [], loaded: [])
        XCTAssertEqual(try ContextBudget.planningContext(empty, model: "model", ceiling: 4096), 4096)
        for size in [0, 512, 2048, 32768] {
            let state = LocalServerState(
                reach: .reached, models: [],
                loaded: [LocalServerLoadedModel("model", 0, contextTokens: size)])
            if size == 0 {
                XCTAssertThrowsError(try ContextBudget.planningContext(state, model: "model", ceiling: 4096))
            } else {
                XCTAssertEqual(
                    try ContextBudget.planningContext(state, model: "model", ceiling: 4096), min(4096, size))
            }
        }
        XCTAssertThrowsError(try ContextBudget.planningContext(.failed, model: "model", ceiling: 4096))
    }

    func testCleanupOutputReservesNonSpacedTextRatherThanCountingItAsOneWord() {
        let text = String(repeating: "語", count: 1000)
        XCTAssertGreaterThanOrEqual(ContextBudget.cleanupOutputCeiling(text), 1250 + 128)
        XCTAssertEqual(ContextBudget.cleanupOutputCeiling(String(repeating: "語", count: 5000)), 4096)
        let words = Array(repeating: "a", count: 100).joined(separator: " ")
        XCTAssertGreaterThanOrEqual(ContextBudget.cleanupOutputCeiling(words), 250 + 128)
    }

    func testVocabularyRoomUsesTheSameConservativeInstructionRateAsTheSendGuard() {
        for instructions in [
            String(repeating: "a", count: 2600),
            String(repeating: "語", count: 400),
        ] {
            let transcript = CleanupPrompt.wrapTranscript("Dictated words")
            let context = 4096
            let output = 256
            let budget = ContextBudget.vocabularyTokens(
                context, instructions: instructions, transcript: transcript, outputCeiling: output)
            let margin = max(64, context * 3 / 100)
            XCTAssertEqual(
                budget,
                context - ContextBudget.chatTemplateTokens - margin
                    - TokenEstimate.vocabulary(instructions + "\n\n") - TokenEstimate.transcript(transcript) - output)
            let glossary = String(repeating: "語", count: budget)
            XCTAssertTrue(
                ContextBudget.requestFits(
                    CleanupRequest(
                        transcript: transcript, writingStylePrompt: instructions + "\n\n" + glossary,
                        maxOutputTokens: output), contextTokens: context))
        }
    }

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
