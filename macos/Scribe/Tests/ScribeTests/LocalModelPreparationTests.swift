import XCTest

@testable import Scribe

@MainActor
final class LocalModelPreparationTests: XCTestCase {
    func testCancelledPreparationCannotBeReplacedByALateSuccessfulAnswer() async throws {
        for answer in [LocalModelPreparationResult.resident, .started, .notApplicable] {
            let cleanup = LateCleanup()
            var changes = 0
            let preparation = LocalModelPreparation { changes += 1 }
            preparation.start(using: cleanup)
            try await cleanup.began.wait()
            XCTAssertEqual(preparation.state, .starting)
            preparation.cancel()
            XCTAssertEqual(preparation.state, .finished(.cancelled))
            let changesAtCancellation = changes
            cleanup.answer = answer
            cleanup.resume.open()
            let result = await preparation.wait()
            XCTAssertEqual(result, .cancelled)
            XCTAssertEqual(preparation.state, .finished(.cancelled))
            XCTAssertEqual(changes, changesAtCancellation)
        }
    }

    func testCompletedPreparationKeepsItsAnswerWhenCancelledAfterward() async throws {
        let cleanup = LateCleanup()
        let preparation = LocalModelPreparation {}
        preparation.start(using: cleanup)
        try await cleanup.began.wait()
        cleanup.answer = .resident
        cleanup.resume.open()
        let result = await preparation.wait()
        XCTAssertEqual(result, .resident)
        preparation.cancel()
        XCTAssertEqual(preparation.state, .finished(.resident))
    }

    private final class LateCleanup: DictationCleaning {
        let began = LifecycleGate()
        let resume = LifecycleGate()
        var answer: LocalModelPreparationResult = .started
        var isEnabled: Bool { true }
        private let fallback = FakeCleanup()

        func provider() async throws -> any CleanupProvider { try await fallback.provider() }
        func currentSettings() -> CleanupSettingsSnapshot { fallback.currentSettings() }
        func invalidate() {}

        func prepareLocalModel(
            isCurrent: @escaping @MainActor @Sendable () async -> Bool,
            onStarting: @escaping @MainActor @Sendable () async -> Void
        ) async -> LocalModelPreparationResult {
            await onStarting()
            began.open()
            let resume = resume
            await Task.detached { try? await resume.wait() }.value
            return answer
        }
    }
}
