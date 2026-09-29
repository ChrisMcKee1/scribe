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
}
