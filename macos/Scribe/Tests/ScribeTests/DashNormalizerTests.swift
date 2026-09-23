import XCTest

@testable import Scribe

/// The writing style asks the model not to emit em or en dashes, but that is advisory. These pin the deterministic
/// backstop that makes the house style hold whichever model answered. Ported case for case from Windows'
/// `Scribe.Core.Tests.DashNormalizerTests`; the dashes are written as escapes, since the repository bans the
/// characters themselves.
final class DashNormalizerTests: XCTestCase {
    private let em = "\u{2014}"
    private let en = "\u{2013}"

    func testLeavesTextWithoutDashesUntouched() {
        let input = "A normal sentence, with a comma; and a semicolon."
        XCTAssertEqual(DashNormalizer.normalize(input), input)
    }

    func testHyphensAndProductNamesSurvive() {
        let input = "Use GPT-5.6-Terra with Qwen3-14B on a well-structured run-on sentence."
        XCTAssertEqual(DashNormalizer.normalize(input), input)
    }

    func testSpacedEmDashBecomesAComma() {
        XCTAssertEqual(
            DashNormalizer.normalize("The pill shows Scribe is listening \(em) and where it lives."),
            "The pill shows Scribe is listening, and where it lives.")
    }

    func testUnspacedEmDashBecomesACommaAndASpace() {
        XCTAssertEqual(DashNormalizer.normalize("It works\(em)mostly."), "It works, mostly.")
    }

    func testPairedEmDashesBothBecomeCommas() {
        XCTAssertEqual(
            DashNormalizer.normalize("Say a phrase \(em) like this one \(em) and Scribe types it."),
            "Say a phrase, like this one, and Scribe types it.")
    }

    func testEnDashBetweenNumbersBecomesARangeWord() {
        XCTAssertEqual(DashNormalizer.normalize("about 1\(en)2 GB"), "about 1 to 2 GB")
        XCTAssertEqual(DashNormalizer.normalize("pages 3 \(en) 7"), "pages 3 to 7")
    }

    func testDoesNotDoubleUpExistingPunctuation() {
        XCTAssertEqual(DashNormalizer.normalize("Wait, \(em) I mean the park."), "Wait, I mean the park.")
        XCTAssertEqual(DashNormalizer.normalize("Done. \(em) Next up."), "Done. Next up.")
    }

    func testLineLeadingDashIsDroppedRatherThanTurnedIntoAComma() {
        XCTAssertEqual(DashNormalizer.normalize("Items:\n\(em) first\n\(em) second"), "Items:\nfirst\nsecond")
    }

    func testTrailingDashIsRemovedWithoutAddingPunctuation() {
        XCTAssertEqual(DashNormalizer.normalize("An unfinished thought \(em)"), "An unfinished thought")
    }

    func testDashBeforeAClosingBracketOrQuoteIsDroppedNotTurnedIntoAComma() {
        XCTAssertEqual(DashNormalizer.normalize("(an aside \(em))"), "(an aside)")
        XCTAssertEqual(DashNormalizer.normalize("he said \"fine\(em)\""), "he said \"fine\"")
        XCTAssertEqual(DashNormalizer.normalize("[note\(em)]"), "[note]")
    }

    func testDashAtTheEndOfALineDoesNotLeaveADanglingComma() {
        XCTAssertEqual(
            DashNormalizer.normalize("First thought \(em)\nSecond thought"), "First thought\nSecond thought")
        XCTAssertEqual(DashNormalizer.normalize("First\(em)\r\nSecond"), "First\r\nSecond")
    }

    func testNewlinesArePreserved() {
        XCTAssertEqual(
            DashNormalizer.normalize("First para \(em) with an aside.\n\nSecond para."),
            "First para, with an aside.\n\nSecond para.")
    }

    func testRunsOfDashesCollapseToOneReplacement() {
        XCTAssertEqual(DashNormalizer.normalize("Yes \(em)\(em) really."), "Yes, really.")
        XCTAssertEqual(DashNormalizer.normalize("Yes \(em)\(en) really."), "Yes, really.")
    }

    func testEmptyTextIsSafe() {
        XCTAssertEqual(DashNormalizer.normalize(""), "")
    }

    func testOutputNeverContainsADash() {
        let cases = [
            "a \(em) b",
            "a\(em)b",
            "\(em)leading",
            "trailing\(em)",
            "1\(en)2",
            "one \(em) two \(em) three \(em) four",
            "\(em)",
            " \(em) ",
            "(aside \(em))",
            "line \(em)\nnext",
            "quote\(em)\"",
            // A combining mark after the dash makes one Character of the two; the dash is still found.
            "a \(em)\u{301} b",
        ]

        for input in cases {
            XCTAssertFalse(DashNormalizer.containsDash(DashNormalizer.normalize(input)), input)
        }
    }

    /// The real guarantee: whatever the model answered, what the guard accepts has no dashes.
    func testSanitizedCleanupOutputIsDashFree() {
        let result = CleanupResponseGuard.sanitize(
            candidate: "The defaults are right for most people \(em) changes take effect after restart.",
            original: "the defaults are right for most people changes take effect after restart")

        XCTAssertEqual(result, .accepted("The defaults are right for most people, changes take effect after restart."))
    }

    /// A refusal stays a refusal whatever punctuation the model used: the guard judges the answer before any dash
    /// comes out of it.
    func testARefusalWithADashIsStillRejected() {
        let result = CleanupResponseGuard.sanitize(
            candidate: "I'm sorry \(em) I cannot assist with that request.",
            original: "Please schedule the meeting for tomorrow morning.")

        XCTAssertEqual(result, .rejected(.refusalLike))
    }

    /// A prompt that models dash punctuation teaches the model to imitate it.
    func testThePromptsAreThemselvesDashFree() {
        XCTAssertFalse(DashNormalizer.containsDash(CleanupPrompt.defaultWritingStyle))
        XCTAssertFalse(DashNormalizer.containsDash(CleanupPrompt.defaultFrontierPrompt))
        XCTAssertFalse(DashNormalizer.containsDash(CleanupPrompt.defaultLocalPrompt))
        XCTAssertFalse(DashNormalizer.containsDash(UsageInsight.systemPrompt))
    }
}
