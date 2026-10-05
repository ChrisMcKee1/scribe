import XCTest

@testable import Scribe

@MainActor
final class SessionDictationProblemTests: XCTestCase {
    private func report(_ id: UInt64) -> PipelineReport {
        PipelineReport(
            dictationID: id, capturedAt: Date(timeIntervalSince1970: Double(id)),
            trigger: .menu, stopReason: .menu, captureDuration: 1)
    }

    func testOnlyProblemsAreRetainedAndSuccessfulRunsDoNotEraseOlderProblems() {
        let store = PipelineReportStore()
        for outcome in [DictationCleanupOutcome.off, .cleaned, .unchanged] {
            var value = report(1)
            value.cleanupOutcome = outcome
            store.publish(value)
        }
        XCTAssertTrue(store.problems.isEmpty)
        var failed = report(2)
        failed.cleanupOutcome = .fellBack
        store.publish(failed)
        store.publish(report(3))
        XCTAssertEqual(store.problems.map(\.id), [2])
        XCTAssertEqual(
            store.problems.first?.messages,
            [
                "AI cleanup did not run successfully. Scribe used what it heard."
            ])
    }

    func testProblemInventoryKeepsOnlyTheNewestTwentyAndClearsWithHistory() {
        let store = PipelineReportStore()
        for id in UInt64(1)...UInt64(31) {
            var value = report(id)
            value.failureStage = .decode
            store.publish(value)
        }
        XCTAssertEqual(store.problems.count, 20)
        XCTAssertEqual(store.problems.map(\.id), Array((UInt64(12)...UInt64(31)).reversed()))
        store.clear()
        XCTAssertNil(store.latest)
        XCTAssertTrue(store.problems.isEmpty)
        var next = report(32)
        next.failureStage = .capture
        store.publish(next)
        XCTAssertEqual(store.problems.map(\.id), [32])
    }

    func testRepeatedPublicationReplacesAnOutcomeWithoutDuplicatingIt() {
        let store = PipelineReportStore()
        var value = report(1)
        value.failureStage = .decode
        store.publish(value)
        value.failureStage = .injection
        store.publish(value)
        XCTAssertEqual(store.problems.count, 1)
        XCTAssertEqual(store.problems.first?.failureStage, .injection)
        store.publish(report(1))
        XCTAssertTrue(store.problems.isEmpty)
    }

    func testProblemShapeKeepsNeitherTextNorFailureDetailsAndShowsBothOutcomes() {
        let store = PipelineReportStore()
        var value = report(1)
        value.rawText = "private raw canary"
        value.sentText = "private prompt canary"
        value.cleanedText = "private reply canary"
        value.finalText = "private final canary"
        value.failureReason = "private service canary"
        value.cleanupOutcome = .fellBack
        value.failureStage = .injection
        store.publish(value)
        XCTAssertEqual(store.problems.first?.messages.count, 2)
        XCTAssertEqual(
            Mirror(reflecting: store.problems[0]).children.compactMap(\.label),
            ["id", "capturedAt", "cleanupFellBack", "failureStage"])
        XCTAssertFalse(String(describing: store.problems).contains("private"))
    }

    func testEveryFailureStageHasAnExplicitMessageWithoutDuplicatingCleanupFallback() {
        XCTAssertTrue(
            SettingsSearchIndex.search("Recent dictation problems").contains {
                $0.entry.id == "diagnostics.problems" && $0.section == .diagnostics
            })
        let store = PipelineReportStore()
        for stage in [
            PipelineFailureStage.capture, .decode, .cleanup, .postProcessing, .injection,
        ] {
            var value = report(1)
            value.failureStage = stage
            store.publish(value)
            XCTAssertEqual(store.problems[0].messages.count, 1, stage.rawValue)
            XCTAssertFalse(store.problems[0].messages[0].isEmpty)
        }
        var value = report(1)
        value.failureStage = .cleanup
        value.cleanupOutcome = .fellBack
        store.publish(value)
        XCTAssertEqual(store.problems[0].messages.count, 1)
    }
}
