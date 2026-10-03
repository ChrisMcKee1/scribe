import XCTest

@testable import Scribe

final class LocalCleanupPlanTests: XCTestCase {
    func testFittingTextStaysWholeAndOversizedTextReconstructsExactly() throws {
        let short = "Keep these words."
        XCTAssertEqual(
            try LocalCleanupPlan.make(text: short, instructions: "Edit.", contextTokens: 4096).segments,
            [.init(text: short, separator: "")])
        for separator in [" ", "   ", "\r\n\r\n", "\t", "\u{00A0}"] {
            let text = Array(repeating: "Keep these words.", count: 600).joined(separator: separator)
            let plan = try LocalCleanupPlan.make(text: text, instructions: "Edit.", contextTokens: 2048)
            XCTAssertGreaterThan(plan.segments.count, 1)
            XCTAssertEqual(plan.segments.map { $0.text + $0.separator }.joined(), text)
            for segment in plan.segments {
                XCTAssertFalse(segment.text.isEmpty)
                XCTAssertTrue(
                    ContextBudget.requestFits(
                        CleanupRequest(
                            transcript: CleanupPrompt.wrapTranscript(segment.text), writingStylePrompt: "Edit.",
                            maxOutputTokens: ContextBudget.cleanupOutputCeiling(segment.text)),
                        contextTokens: 2048))
            }
        }
    }

    func testEveryChunkFitsNonASCIITextAndRefusesAnUnsplittableTokenOrInstructions() throws {
        let text = Array(repeating: "語彙を守ってください", count: 600).joined(separator: " ")
        let plan = try LocalCleanupPlan.make(text: text, instructions: "Edit.", contextTokens: 2048)
        XCTAssertGreaterThan(plan.segments.count, 1)
        XCTAssertEqual(plan.segments.map { $0.text + $0.separator }.joined(), text)
        for segment in plan.segments {
            XCTAssertTrue(
                ContextBudget.requestFits(
                    CleanupRequest(
                        transcript: CleanupPrompt.wrapTranscript(segment.text), writingStylePrompt: "Edit.",
                        maxOutputTokens: ContextBudget.cleanupOutputCeiling(segment.text)),
                    contextTokens: 2048))
        }
        XCTAssertThrowsError(
            try LocalCleanupPlan.make(
                text: String(repeating: "語", count: 5000), instructions: "Edit.", contextTokens: 2048))
        XCTAssertThrowsError(
            try LocalCleanupPlan.make(
                text: text, instructions: String(repeating: "語", count: 3000), contextTokens: 2048))
    }
}
