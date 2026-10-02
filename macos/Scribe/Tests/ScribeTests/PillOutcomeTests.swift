import XCTest

@testable import Scribe

final class PillOutcomeTests: XCTestCase {
    func testACompleteInsertionIsTyped() {
        let outcome = PillOutcome.of(InjectionResult(delivery: .typed), cleanupRequested: false, cleanupOutcome: .off)
        XCTAssertEqual(outcome?.kind, .typed)
        XCTAssertEqual(outcome?.detail, "")
        XCTAssertEqual(outcome?.hold, PillTiming.typedHold)
    }

    func testACompleteInsertionWithoutCleanupUsesTheFixedDetail() {
        let outcome = PillOutcome.of(
            InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored),
            cleanupRequested: true,
            cleanupOutcome: .fellBack)
        XCTAssertEqual(outcome?.kind, .typedWithoutCleanup)
        XCTAssertEqual(outcome?.detail, PillOutcome.cleanupDidNotRun)
        XCTAssertEqual(outcome?.hold, PillTiming.noticeHold)
    }

    func testACompleteInsertionStaysTypedWhenCleanupWorked() {
        XCTAssertEqual(
            PillOutcome.of(InjectionResult(delivery: .typed), cleanupRequested: true, cleanupOutcome: .cleaned)?.kind,
            .typed)
        XCTAssertEqual(
            PillOutcome.of(InjectionResult(delivery: .typed), cleanupRequested: true, cleanupOutcome: .unchanged)?.kind,
            .typed)
    }

    func testAPartialInsertionUsesTheRecoveryStep() {
        let outcome = PillOutcome.of(
            InjectionResult(delivery: .typedPartially),
            cleanupRequested: false,
            cleanupOutcome: .off)
        XCTAssertEqual(outcome?.kind, .partlyTyped)
        XCTAssertEqual(outcome?.detail, PillOutcome.recoveryStep)
    }

    func testAFailedInsertionUsesTheRecoveryStepOrTheAccessibilityStep() {
        XCTAssertEqual(
            PillOutcome.of(
                InjectionResult(delivery: .targetChanged),
                cleanupRequested: false,
                cleanupOutcome: .off)?.detail,
            PillOutcome.recoveryStep)
        XCTAssertEqual(
            PillOutcome.of(
                InjectionResult(delivery: .accessibilityDenied),
                cleanupRequested: false,
                cleanupOutcome: .off)?.detail,
            PillOutcome.accessibilityStep)
    }

    func testNothingToInsertStaysQuiet() {
        XCTAssertNil(
            PillOutcome.of(
                InjectionResult(delivery: .nothingToInsert),
                cleanupRequested: false,
                cleanupOutcome: .off))
    }
}
