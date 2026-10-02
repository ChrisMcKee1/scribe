import XCTest

@testable import Scribe

final class ContextBudgetTests: XCTestCase {
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
        let entries = (0..<200).map { DictionaryEntry(pattern: uniquePattern($0), replacement: "Term\($0)") }
        let vocabulary = CleanupVocabulary(glossaryEntries: entries)

        let whole = vocabulary.glossary(
            mode: .mentioned,
            everything: true,
            dictation: uniquePattern(199),
            tokenBudget: 1_000_000,
            maxTerms: .max)
        XCTAssertEqual(whole, CleanupPrompt.buildGlossary(entries))

        let fitted = vocabulary.glossary(
            mode: .mentioned,
            everything: false,
            dictation: uniquePattern(199),
            tokenBudget: 200,
            maxTerms: CleanupPrompt.maxGlossaryTermsLocal)
        XCTAssertNotNil(fitted)
        XCTAssertTrue(fitted?.contains("Term199") == true)
        XCTAssertFalse(fitted?.contains("Term0") == true)
    }

    private func uniquePattern(_ value: Int) -> String {
        let alphabet = Array("abcdefghijklmnopqrstuvwxyz")
        var number = value
        var pattern = ""
        repeat {
            pattern.insert(alphabet[number % alphabet.count], at: pattern.startIndex)
            number /= alphabet.count
        } while number > 0
        return "term" + pattern
    }
}
