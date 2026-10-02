import XCTest

@testable import Scribe

final class CleanupResponseGuardTests: XCTestCase {
    func testReplyLikeAnswerToQuestionIsRejected() {
        let result = CleanupResponseGuard.sanitize(
            candidate: "Yes, it's working correctly.",
            original: "Is this working now?")

        XCTAssertEqual(result, .rejected(.replyLike))
    }

    func testGenuinelyDictatedAffirmationIsPreserved() {
        let result = CleanupResponseGuard.sanitize(
            candidate: "Yes, I will do that.",
            original: "Yes, I will do that.")

        XCTAssertEqual(result, .accepted("Yes, I will do that."))
    }

    func testRefusalLikeResponseIsRejected() {
        let result = CleanupResponseGuard.sanitize(
            candidate: "I'm sorry, but I cannot assist with that request.",
            original: "Please schedule the meeting for tomorrow morning.")

        XCTAssertEqual(result, .rejected(.refusalLike))
    }

    func testNumericReformatIsNotMistakenForReply() {
        let result = CleanupResponseGuard.sanitize(
            candidate: "$950",
            original: "nine hundred fifty dollars")

        XCTAssertEqual(result, .accepted("$950"))
    }

    func testNormalCleanupEditPassesThrough() {
        let result = CleanupResponseGuard.sanitize(
            candidate: "We should ship the build by Thursday, and Bob needs to know today.",
            original: "we should ship the build by thursday and bob needs to know today")

        XCTAssertEqual(
            result,
            .accepted("We should ship the build by Thursday, and Bob needs to know today."))
    }

    func testOverlongRambleIsRejected() {
        let result = CleanupResponseGuard.sanitize(
            candidate: String(repeating: "a", count: 200),
            original: "hi")

        XCTAssertEqual(result, .rejected(.overlongRamble))
    }

    func testWrappingArtifactsAreStrippedBeforeAcceptance() {
        let result = CleanupResponseGuard.sanitize(
            candidate: "\"<transcript>\nHello world.\n</transcript>\"",
            original: "Hello world.")

        XCTAssertEqual(result, .accepted("Hello world."))
    }

    func testSmallModelRewriteAnnouncementIsStripped() {
        let result = CleanupResponseGuard.sanitize(
            candidate: "Here is the rewritten transcript:\n\nWe need to ship the build by Thursday.",
            original: "um so we need to uh ship the build by friday no thursday")

        XCTAssertEqual(result, .accepted("We need to ship the build by Thursday."))
    }

    func testSmallModelWrapperTagsAreStripped() {
        let result = CleanupResponseGuard.sanitize(
            candidate: "<rewritten_transcript>\nWe need to ship the build by Thursday.\n</rewritten_transcript>",
            original: "um so we need to uh ship the build by friday no thursday")

        XCTAssertEqual(result, .accepted("We need to ship the build by Thursday."))
    }

    func testTrailingCommentaryIsRemoved() {
        let result = CleanupResponseGuard.sanitize(
            candidate: """
                We need to ship the build by Thursday.

                ---

                This version maintains the original meaning while removing filler words.
                """,
            original: "um so we need to uh ship the build by friday no thursday")

        XCTAssertEqual(result, .accepted("We need to ship the build by Thursday."))
    }

    func testADictationThatOpensLikeAnAnnouncementKeepsItsFirstLine() {
        let answer = "Here's the revised text for the email:\nHi Bob, we ship Thursday."
        let result = CleanupResponseGuard.sanitize(
            candidate: answer,
            original: "um so here's the revised text for the email hi bob we ship thursday")

        XCTAssertEqual(result, .accepted(answer))
    }

    func testATagTheDictationItselfContainsIsKept() {
        let result = CleanupResponseGuard.sanitize(
            candidate: "<output>Done</output>",
            original: "wrap the value in an output tag like <output>done</output>")

        XCTAssertEqual(result, .accepted("<output>Done</output>"))
    }
}
