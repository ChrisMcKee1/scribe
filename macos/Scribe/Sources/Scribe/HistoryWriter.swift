import Foundation
import OSLog

/// The one call the history writer makes on the store, so tests can hold a write in flight.
protocol HistoryRecording: Sendable {
    func recordDictation(_ record: DictationHistoryRecord) throws
}

extension PersistenceStore: HistoryRecording {}

/// What a `HistoryWriter.complete(timeout:)` call left behind.
struct HistoryDrainResult: Equatable, Sendable {
    /// Nothing accepted is still queued or being written.
    let drained: Bool
    /// Writes still executing when the wait ended (at most one). They finish on their own.
    let stillWriting: Int
    /// Accepted writes that will never be committed.
    let abandoned: Int
}

/// Commits dictation history in the background, one write at a time, in the order the writes were
/// accepted: the macOS side of Windows `HistoryWriter`.
///
/// A dictation hands its entry here once the text has been delivered and moves on, so storage latency
/// never sits between speaking and typing. `enqueue` never blocks, because its caller is the main
/// actor: when `capacity` writes are already outstanding (the database has failed to commit for that
/// many dictations in a row) the new entry is dropped and logged rather than waited for. Entries hold
/// text only, which is why the bound is larger than the two Windows allows, whose entries can carry
/// captured audio.
///
/// `waitForAcceptedWrites(timeout:)` is the barrier Clear history uses, so that it removes everything
/// dictated before the click. `complete(timeout:)` closes the writer at quit with a bounded wait;
/// anything still queued behind a write that outlives the wait is abandoned, never committed later.
///
/// Logs are shapes only: counts, durations and SQLite result codes, never an entry's text or an error
/// message, which a failure could build from the content being written.
///
/// `@unchecked Sendable` because the compiler cannot see the locking: every mutable field below is
/// only read or written while `condition` is held, and the recorder is itself `Sendable`.
final class HistoryWriter: @unchecked Sendable {
    static let defaultCapacity = 16

    let capacity: Int

    private struct PendingWrite {
        let ticket: UInt64
        let record: DictationHistoryRecord
        let dictationID: UInt64
        let queuedAt: UInt64
    }

    private let recorder: any HistoryRecording
    private let onBarrierWait: (@Sendable () -> Void)?
    private let queue = DispatchQueue(label: "com.scribe.macos.history-writer", qos: .utility)
    private let logger = Logger(subsystem: "com.scribe.macos", category: "History")
    private let condition = NSCondition()

    private var pending: [PendingWrite] = []
    /// Tickets issued to accepted writes. Writes finish strictly in ticket order, so `finished` is a
    /// watermark: every ticket at or below it has been committed, failed or abandoned.
    private var accepted: UInt64 = 0
    private var finished: UInt64 = 0
    private var consumerActive = false
    private var inFlight = false
    private var closed = false
    private var abandonQueued = false
    private var droppedForRoom = 0
    private var abandoned = 0

    /// - Parameter onBarrierWait: test seam, called while holding the writer's lock when a
    ///   `waitForAcceptedWrites` caller first has to wait, so a test knows the barrier is in place
    ///   instead of guessing from a timer.
    init(
        recorder: any HistoryRecording,
        capacity: Int = HistoryWriter.defaultCapacity,
        onBarrierWait: (@Sendable () -> Void)? = nil
    ) {
        self.recorder = recorder
        self.capacity = max(1, capacity)
        self.onBarrierWait = onBarrierWait
    }

    /// Queues one entry for an ordered background commit and returns at once. Returns false, and
    /// records nothing, when the writer has closed or `capacity` writes are already outstanding. A
    /// failed write is logged as a shape and dropped, with no retry; it never reaches the caller.
    /// - Parameter dictationID: stamped on this writer's log lines. Diagnostic only.
    @discardableResult
    func enqueue(_ record: DictationHistoryRecord, dictationID: UInt64 = 0) -> Bool {
        condition.lock()
        if closed {
            condition.unlock()
            logger.warning("#\(dictationID) dictation history was not recorded: the history writer has shut down.")
            return false
        }

        let outstanding = pending.count + (inFlight ? 1 : 0)
        guard outstanding < capacity else {
            droppedForRoom += 1
            let dropped = droppedForRoom
            condition.unlock()
            logger.error(
                "#\(dictationID) history not recorded: \(outstanding) earlier writes outstanding (\(dropped) dropped).")
            return false
        }

        accepted += 1
        pending.append(
            PendingWrite(
                ticket: accepted,
                record: record,
                dictationID: dictationID,
                queuedAt: DispatchTime.now().uptimeNanoseconds))
        let startConsumer = !consumerActive
        consumerActive = true
        condition.unlock()

        if startConsumer {
            queue.async { [self] in
                consume()
            }
        }
        return true
    }

    /// Waits up to `timeout` until every write accepted before this call has finished (committed,
    /// failed or abandoned). Writes accepted after the call are not waited for. Returns false when the
    /// wait timed out.
    func waitForAcceptedWrites(timeout: TimeInterval) -> Bool {
        let deadline = Date(timeIntervalSinceNow: timeout)
        condition.lock()
        defer { condition.unlock() }

        let target = accepted
        if finished < target {
            onBarrierWait?()
        }
        while finished < target {
            if !condition.wait(until: deadline) {
                return finished >= target
            }
        }
        return true
    }

    /// Stops accepting writes and waits up to `timeout` for the accepted ones to finish. Writes still
    /// queued behind a write that outlives the wait are abandoned rather than committed later.
    @discardableResult
    func complete(timeout: TimeInterval) -> HistoryDrainResult {
        let deadline = Date(timeIntervalSinceNow: timeout)
        condition.lock()
        closed = true
        while !pending.isEmpty || inFlight {
            if !condition.wait(until: deadline) {
                break
            }
        }

        let result: HistoryDrainResult
        if pending.isEmpty, !inFlight {
            result = HistoryDrainResult(drained: true, stillWriting: 0, abandoned: abandoned)
        } else {
            abandonQueued = true
            result = HistoryDrainResult(
                drained: false, stillWriting: inFlight ? 1 : 0, abandoned: abandoned + pending.count)
        }
        condition.unlock()

        if !result.drained {
            logger.warning(
                "History writer closed with \(result.stillWriting) write committing, \(result.abandoned) abandoned.")
        }
        return result
    }

    // MARK: - Consumer (on `queue`)

    private func consume() {
        while true {
            condition.lock()
            guard !pending.isEmpty else {
                consumerActive = false
                condition.broadcast()
                condition.unlock()
                return
            }
            let item = pending.removeFirst()
            let drop = abandonQueued
            inFlight = !drop
            condition.unlock()

            let started = DispatchTime.now().uptimeNanoseconds
            var failure: (any Error)?
            if !drop {
                do {
                    try recorder.recordDictation(item.record)
                } catch {
                    // Isolated to this entry: its dictation already finished, and the next write still runs.
                    failure = error
                }
            }

            // Reported before the write counts as finished, so a caller that waited for it finds the
            // outcome already in the log.
            let writeNanoseconds = DispatchTime.now().uptimeNanoseconds - started
            report(item, dropped: drop, failure: failure, writeNanoseconds: writeNanoseconds)

            condition.lock()
            inFlight = false
            finished = item.ticket
            if drop {
                abandoned += 1
            }
            condition.broadcast()
            condition.unlock()
        }
    }

    private func report(_ item: PendingWrite, dropped: Bool, failure: (any Error)?, writeNanoseconds: UInt64) {
        let writeMilliseconds = writeNanoseconds / 1_000_000
        if dropped {
            logger.warning("#\(item.dictationID) history write was abandoned at shutdown; the entry was not recorded.")
            return
        }

        if let failure {
            let code = (failure as? PersistenceError)?.sqliteCode ?? -1
            let errorType = String(describing: type(of: failure))
            logger.error(
                "#\(item.dictationID) history write failed after \(writeMilliseconds) ms (\(errorType, privacy: .public), SQLite \(code)).")
            return
        }

        let totalMilliseconds = (DispatchTime.now().uptimeNanoseconds - item.queuedAt) / 1_000_000
        logger.debug(
            "#\(item.dictationID) history committed \(totalMilliseconds) ms after queueing (write \(writeMilliseconds) ms).")
    }
}
