import AppKit
import XCTest

@testable import Scribe

final class OperationDeadlineTests: XCTestCase {
    func testAnOperationThatFinishesInTimeKeepsItsResult() async throws {
        let value = try await OperationDeadline.run(within: .seconds(30)) { 42 }

        XCTAssertEqual(value, 42)
    }

    func testAnOperationThatFailsInTimeKeepsItsError() async {
        do {
            _ = try await OperationDeadline.run(within: .seconds(30)) { () async throws -> Int in
                throw CleanupProviderError.timedOut
            }
            XCTFail("Expected the operation's error")
        } catch {
            XCTAssertEqual(error as? CleanupProviderError, .timedOut)
        }
    }

    /// The deadline cancels work that would never end by itself, and the call returns only once that work has seen
    /// the cancellation and stopped.
    func testTheDeadlineCancelsTheOperationAndWaitsForItToStop() async {
        let held = HeldWork()
        let deadlineError = LockedValue<OperationDeadlineError>()
        let started = ContinuousClock.now

        await waitBounded("the deadline to end the operation") {
            do {
                _ = try await OperationDeadline.run(within: .milliseconds(100)) { () async throws -> Int in
                    try await held.hold()
                    return 0
                }
            } catch let error as OperationDeadlineError {
                deadlineError.set(error)
            } catch {}
        }

        XCTAssertEqual(deadlineError.value, .exceeded(seconds: 0))
        XCTAssertTrue(held.sawCancellation)
        XCTAssertGreaterThanOrEqual(started.duration(to: .now), .milliseconds(100))
    }

    /// Cancelling the caller cancels the operation and ends in a cancellation, never in the deadline's error.
    func testCancellingTheCallerIsNotReportedAsTheDeadline() async {
        let held = HeldWork()
        let running = Task {
            try await OperationDeadline.run(within: .seconds(60)) { () async throws -> Int in
                try await held.hold()
                return 0
            }
        }
        await waitBounded("the operation to start") { await held.waitUntilStarted() }

        running.cancel()
        let failure = LockedValue<String>()
        await waitBounded("the cancelled operation to end") {
            if case .failure(let error) = await running.result {
                failure.set(error is CancellationError ? "cancelled" : "\(FailureShape(error))")
            }
        }

        XCTAssertEqual(failure.value, "cancelled")
        XCTAssertTrue(held.sawCancellation)
    }

    func testTheDeadlineErrorIsShapedByItsLimit() {
        XCTAssertEqual(
            FailureShape(OperationDeadlineError.exceeded(seconds: 90)).description,
            "OperationDeadlineError.exceeded values=90")
    }
}

/// The AI Cleanup tab can stop a Test Connection that is running.
final class CleanupSettingsModelCancelTests: XCTestCase {
    @MainActor
    func testCancelStopsARunningTestConnection() async {
        let backing = CleanupSettingsBackingFake()
        backing.stored.isEnabled = true
        let held = HeldWork()
        var access = backing.access
        access.checkConnection = {
            do {
                try await held.hold()
            } catch {}
            return CleanupConnectionCheck(reachable: false, message: "Foundry Local: The check was cancelled.")
        }
        let model = CleanupSettingsModel(access: access, drafts: backing.drafts, center: backing.center)

        let test = Task { await model.testConnection() }
        let started = await finishes(within: 30) { await held.waitUntilStarted() }
        XCTAssertTrue(started, "the check did not start")
        XCTAssertTrue(model.isTesting)
        model.cancelConnectionTest()
        let ended = await finishes(within: 30) { await test.value }
        XCTAssertTrue(ended, "the cancelled test did not end")

        XCTAssertTrue(held.sawCancellation, "the check's work was cancelled")
        XCTAssertFalse(model.isTesting)
        XCTAssertEqual(model.statusMessage, "Test Connection was cancelled.")
        XCTAssertNil(model.errorMessage)
        XCTAssertFalse(model.isDisabled(.connectionTest), "it can be run again")
    }

    /// With nothing running, Cancel does nothing, and the next test runs and reports as usual.
    @MainActor
    func testCancelWithNothingRunningChangesNothing() async {
        let backing = CleanupSettingsBackingFake()
        backing.stored.isEnabled = true
        backing.connectionCheck = CleanupConnectionCheck(
            reachable: true, message: "Foundry Local cleaned a test phrase.")
        let model = CleanupSettingsModel(access: backing.access, drafts: backing.drafts, center: backing.center)

        model.cancelConnectionTest()
        await model.testConnection()

        XCTAssertEqual(model.statusMessage, "Foundry Local cleaned a test phrase.")
        XCTAssertNil(model.errorMessage)
    }

    /// Closing Settings stops a Test Connection still running, although its task keeps the tab's model alive, and the
    /// tab opened next runs a check of its own.
    @MainActor
    func testClosingSettingsCancelsARunningCheckAndTheReopenedTabRunsItsOwn() async {
        let backing = CleanupSettingsBackingFake()
        backing.stored.isEnabled = true
        let held = HeldWork()
        var access = backing.access
        access.checkConnection = {
            do {
                try await held.hold()
            } catch {}
            return CleanupConnectionCheck(reachable: false, message: "Foundry Local: The check was cancelled.")
        }
        let closing = CleanupSettingsModel(access: access, drafts: backing.drafts, center: backing.center)
        let test = Task { await closing.testConnection() }
        let started = await finishes(within: 30) { await held.waitUntilStarted() }
        XCTAssertTrue(started, "the check did not start")

        let window = SettingsWindowController(window: nil, onClose: { _ in }, center: backing.center)
        window.windowWillClose(Notification(name: NSWindow.willCloseNotification))
        let ended = await finishes(within: 30) { await test.value }

        XCTAssertTrue(ended, "closing Settings did not stop the check")
        XCTAssertTrue(held.sawCancellation)
        XCTAssertFalse(closing.isTesting)

        backing.connectionCheck = CleanupConnectionCheck(
            reachable: true, message: "Foundry Local is connected: the model answered the test in 0.1 s.")
        let reopened = CleanupSettingsModel(access: backing.access, drafts: backing.drafts, center: backing.center)
        await reopened.testConnection()

        XCTAssertEqual(reopened.statusMessage, "Foundry Local is connected: the model answered the test in 0.1 s.")
        XCTAssertFalse(reopened.isTesting)
    }
}
