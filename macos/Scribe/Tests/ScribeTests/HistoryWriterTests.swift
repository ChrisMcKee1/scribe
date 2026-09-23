import XCTest
@testable import Scribe

/// A `HistoryRecording` that can hold writes at their start, fail chosen ones, and forward the rest
/// to a real store.
final class StorageTestGatedRecorder: HistoryRecording, @unchecked Sendable {
    struct InjectedFailure: Error {}

    // `@unchecked Sendable`: every mutable field is only read or written while `condition` is held.
    private let condition = NSCondition()
    private let inner: PersistenceStore?
    private var gateOpen = true
    private var started = 0
    private var failing: Set<Int> = []
    private var texts: [String?] = []

    init(forwardingTo inner: PersistenceStore? = nil) {
        self.inner = inner
    }

    var recordedTexts: [String?] {
        condition.lock()
        defer { condition.unlock() }
        return texts
    }

    func closeGate() {
        condition.lock()
        gateOpen = false
        condition.unlock()
    }

    func openGate() {
        condition.lock()
        gateOpen = true
        condition.broadcast()
        condition.unlock()
    }

    /// Makes the `number`th write (counting from 1) throw.
    func failWrite(number: Int) {
        condition.lock()
        failing.insert(number)
        condition.unlock()
    }

    /// True once `count` writes have begun, within `timeout` seconds.
    func waitUntilStarted(_ count: Int, timeout: TimeInterval = 10) -> Bool {
        let deadline = Date(timeIntervalSinceNow: timeout)
        condition.lock()
        defer { condition.unlock() }
        while started < count {
            if !condition.wait(until: deadline) {
                return started >= count
            }
        }
        return true
    }

    func recordDictation(_ record: DictationHistoryRecord) throws {
        condition.lock()
        started += 1
        let number = started
        condition.broadcast()
        // Bounded, so a test that forgets to open the gate fails instead of hanging the run.
        let deadline = Date(timeIntervalSinceNow: 10)
        while !gateOpen, condition.wait(until: deadline) {}
        let shouldFail = failing.contains(number)
        condition.unlock()

        if shouldFail {
            throw InjectedFailure()
        }
        try inner?.recordDictation(record)

        condition.lock()
        texts.append(record.transcriptText)
        condition.unlock()
    }
}

final class HistoryWriterTests: XCTestCase {
    private var directory: StorageTestDirectory!

    override func setUpWithError() throws {
        directory = try StorageTestDirectory()
    }

    override func tearDownWithError() throws {
        directory.remove()
    }

    private func record(_ text: String, secondsAgo: TimeInterval = 0) -> DictationHistoryRecord {
        DictationHistoryRecord(
            startedAt: Date(timeIntervalSince1970: 1_800_000_000 - secondsAgo),
            durationSeconds: 1,
            sampleCount: 16_000,
            transcriptText: text)
    }

    func testEntriesAreCommittedInTheOrderTheyWereAccepted() throws {
        let store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        let writer = HistoryWriter(recorder: store)

        // Timestamps run backwards, so only commit order can put these rows in accepted order.
        for index in 0..<8 {
            XCTAssertTrue(writer.enqueue(record("entry \(index)", secondsAgo: Double(index))))
        }

        XCTAssertTrue(writer.waitForAcceptedWrites(timeout: 10))
        XCTAssertEqual(try store.fetchDictationHistory().compactMap(\.transcriptText), (0..<8).map { "entry \($0)" })
        XCTAssertEqual(writer.complete(timeout: 5), HistoryDrainResult(drained: true, stillWriting: 0, abandoned: 0))
    }

    func testTheNewEntryIsDroppedWhenCapacityWritesAreOutstanding() {
        let recorder = StorageTestGatedRecorder()
        recorder.closeGate()
        let writer = HistoryWriter(recorder: recorder, capacity: 2)

        XCTAssertTrue(writer.enqueue(record("one")))
        XCTAssertTrue(recorder.waitUntilStarted(1))
        XCTAssertTrue(writer.enqueue(record("two")))
        XCTAssertFalse(writer.enqueue(record("three")))

        recorder.openGate()
        XCTAssertTrue(writer.waitForAcceptedWrites(timeout: 10))
        XCTAssertEqual(recorder.recordedTexts, ["one", "two"])
        writer.complete(timeout: 5)
    }

    func testTheBarrierWaitsForAWriteStillInFlight() {
        let recorder = StorageTestGatedRecorder()
        recorder.closeGate()
        let writer = HistoryWriter(recorder: recorder)

        writer.enqueue(record("held"))
        XCTAssertTrue(recorder.waitUntilStarted(1))
        XCTAssertFalse(writer.waitForAcceptedWrites(timeout: 0.05))

        recorder.openGate()
        XCTAssertTrue(writer.waitForAcceptedWrites(timeout: 10))
        XCTAssertEqual(recorder.recordedTexts, ["held"])
        writer.complete(timeout: 5)
    }

    func testCompleteDrainsAcceptedWritesAndThenRefusesNewOnes() throws {
        let store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        let writer = HistoryWriter(recorder: store)
        for index in 0..<3 {
            writer.enqueue(record("entry \(index)"))
        }

        XCTAssertEqual(writer.complete(timeout: 10), HistoryDrainResult(drained: true, stillWriting: 0, abandoned: 0))
        XCTAssertEqual(try store.historyCount(), 3)
        XCTAssertFalse(writer.enqueue(record("after close")))
        XCTAssertEqual(try store.historyCount(), 3)
    }

    func testCompleteAbandonsWritesQueuedBehindAStuckWrite() {
        let recorder = StorageTestGatedRecorder()
        recorder.closeGate()
        let writer = HistoryWriter(recorder: recorder)

        writer.enqueue(record("stuck"))
        XCTAssertTrue(recorder.waitUntilStarted(1))
        writer.enqueue(record("queued one"))
        writer.enqueue(record("queued two"))

        XCTAssertEqual(writer.complete(timeout: 0.05), HistoryDrainResult(drained: false, stillWriting: 1, abandoned: 2))

        // The stuck write finishes on its own; what was queued behind it is never committed.
        recorder.openGate()
        XCTAssertTrue(writer.waitForAcceptedWrites(timeout: 10))
        XCTAssertEqual(recorder.recordedTexts, ["stuck"])
    }

    func testAFailedWriteDoesNotStopTheWritesBehindIt() {
        let recorder = StorageTestGatedRecorder()
        recorder.failWrite(number: 2)
        let writer = HistoryWriter(recorder: recorder)

        writer.enqueue(record("a"))
        writer.enqueue(record("b"))
        writer.enqueue(record("c"))

        XCTAssertTrue(writer.waitForAcceptedWrites(timeout: 10))
        XCTAssertEqual(recorder.recordedTexts, ["a", "c"])
        writer.complete(timeout: 5)
    }
}
