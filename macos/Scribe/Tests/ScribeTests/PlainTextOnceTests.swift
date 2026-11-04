import Foundation
import XCTest

@testable import Scribe

/// Plain text once, ported from Windows' `PlainTextOnceTests`: armed for 60 seconds, taken only by an admitted
/// recording, and every change numbered so a late notice never replaces a newer one.
@MainActor
final class PlainTextOnceTests: XCTestCase {
    private var now = ContinuousClock.now
    private var changes: [PlainTextOnceState] = []

    private func makeOwner(
        sleep: @escaping @Sendable (ContinuousClock.Instant) async throws -> Void = { _ in
            try await Task.sleep(for: .seconds(3_600))
        }
    ) -> PlainTextOnce {
        let owner = PlainTextOnce(now: { [unowned self] in self.now }, sleep: sleep)
        owner.onChange = { [unowned self] state in self.changes.append(state) }
        return owner
    }

    func testItStartsOffAndOnlyAnArmedOwnerIsTaken() {
        let owner = makeOwner()
        XCTAssertEqual(owner.current, PlainTextOnceState(status: .off, revision: 0))

        let notArmed = owner.consumeForAdmittedRecording()
        XCTAssertFalse(notArmed.consumed)
        XCTAssertNil(notArmed.change)

        owner.arm()
        XCTAssertTrue(owner.current.isArmed)
        let taken = owner.consumeForAdmittedRecording()
        XCTAssertTrue(taken.consumed)
        XCTAssertEqual(taken.change?.status, .consumed)
        XCTAssertFalse(owner.consumeForAdmittedRecording().consumed)
        XCTAssertEqual(changes.map(\.status), [.armed])

        owner.notify(taken.change)
        XCTAssertEqual(changes.map(\.status), [.armed, .consumed])
    }

    func testItExpiresSixtySecondsAfterItWasArmed() {
        let owner = makeOwner()
        owner.arm()

        now = now.advanced(by: .seconds(59))
        XCTAssertTrue(owner.current.isArmed)
        now = now.advanced(by: .seconds(1))
        XCTAssertEqual(owner.current.status, .expired)
        XCTAssertEqual(changes.map(\.status), [.armed, .expired])
        XCTAssertFalse(owner.consumeForAdmittedRecording().consumed)
    }

    func testARecordingAdmittedAfterTheDeadlineTakesNothingAndReportsTheExpiry() {
        let owner = makeOwner()
        owner.arm()
        now = now.advanced(by: .seconds(61))

        let taken = owner.consumeForAdmittedRecording()

        XCTAssertFalse(taken.consumed)
        XCTAssertEqual(taken.change?.status, .expired)
    }

    func testTheTimerExpiresItWithoutAnyoneAsking() async {
        let gate = PlainTextOnceSleepGate()
        let owner = makeOwner(sleep: { _ in await gate.wait() })
        owner.arm()
        await gate.waitForSleeper()

        now = now.advanced(by: PlainTextOnce.lifetime)
        await gate.open()
        for _ in 0..<1_000 where changes.last?.status != .expired {
            await Task.yield()
        }

        XCTAssertEqual(changes.map(\.status), [.armed, .expired])
    }

    func testCancellingClearsItAndOnlyAnArmedOwnerCancels() {
        let owner = makeOwner()
        owner.cancel()
        XCTAssertTrue(changes.isEmpty)

        owner.arm()
        owner.cancel()
        XCTAssertEqual(owner.current.status, .cancelled)
        XCTAssertFalse(owner.consumeForAdmittedRecording().consumed)
        XCTAssertEqual(changes.map(\.status), [.armed, .cancelled])
    }

    /// A consumption published late is dropped once a newer change has replaced it, so its notice never shows over the
    /// newer state.
    func testALateConsumptionNoticeIsDroppedOnceItIsNoLongerTheNewest() {
        let owner = makeOwner()
        owner.arm()
        let taken = owner.consumeForAdmittedRecording()
        owner.arm()

        owner.notify(taken.change)

        XCTAssertEqual(changes.map(\.status), [.armed, .armed])
        XCTAssertTrue(owner.current.isArmed)
    }

    func testRevisionsOnlyGrow() {
        let owner = makeOwner()
        owner.arm()
        owner.cancel()
        owner.arm()
        owner.notify(owner.consumeForAdmittedRecording().change)
        owner.arm()
        now = now.advanced(by: .seconds(60))
        _ = owner.current

        let revisions = changes.map(\.revision)
        XCTAssertEqual(revisions, revisions.sorted())
        XCTAssertEqual(Set(revisions).count, revisions.count)
    }

    func testShuttingDownClearsItForGood() {
        let owner = makeOwner()
        owner.arm()
        owner.shutDown()

        XCTAssertEqual(owner.current.status, .shutdown)
        owner.arm()
        XCTAssertFalse(owner.consumeForAdmittedRecording().consumed)
        XCTAssertEqual(changes.map(\.status), [.armed, .shutdown])
    }

    func testEveryChangeAUserSeesHasANoticeAndTheWordsSayWhatStillRuns() {
        XCTAssertNil(PlainTextOnceText.notice(PlainTextOnceState(status: .off, revision: 0)))
        XCTAssertNil(PlainTextOnceText.notice(PlainTextOnceState(status: .shutdown, revision: 1)))
        for status in [PlainTextOnceStatus.armed, .consumed, .expired, .cancelled] {
            XCTAssertNotNil(PlainTextOnceText.notice(PlainTextOnceState(status: status, revision: 1)), status.rawValue)
        }
        let armed = PlainTextOnceText.notice(PlainTextOnceState(status: .armed, revision: 1))?.body ?? ""
        XCTAssertTrue(armed.contains("60 seconds"))
        XCTAssertTrue(armed.contains("AI cleanup, your dictionary and snippets still run"))
    }
}

/// Holds the expiry timer's sleep until the test opens it.
private actor PlainTextOnceSleepGate {
    private var sleeper: CheckedContinuation<Void, Never>?
    private var arrivals: [CheckedContinuation<Void, Never>] = []
    private var isOpen = false

    func wait() async {
        guard !isOpen else { return }
        await withCheckedContinuation { continuation in
            sleeper = continuation
            for arrival in arrivals {
                arrival.resume()
            }
            arrivals = []
        }
    }

    func waitForSleeper() async {
        guard sleeper == nil else { return }
        await withCheckedContinuation { arrivals.append($0) }
    }

    func open() {
        isOpen = true
        sleeper?.resume()
        sleeper = nil
    }
}
