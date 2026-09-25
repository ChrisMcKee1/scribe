import Foundation
import OSLog

/// What the history writer does to the store, so tests can hold a step in flight.
protocol HistoryRecording: Sendable {
    func recordDictation(_ record: DictationHistoryRecord) throws
    func clearHistory() throws -> Int
}

extension PersistenceStore: HistoryRecording {}

/// What a `HistoryWriter.complete(timeout:)` call left behind.
struct HistoryDrainResult: Equatable, Sendable {
    /// Nothing accepted is still queued or being executed.
    let drained: Bool
    /// Steps still executing when the wait ended (at most one). They finish on their own.
    let stillWriting: Int
    /// Accepted steps that will never run.
    let abandoned: Int
}

enum HistoryWriterError: LocalizedError, Equatable {
    /// The writer has shut down; the request did nothing.
    case closed
    /// Clear history did not reach the front of the queue in time, so it was withdrawn and cleared
    /// nothing. It never runs later.
    case clearTimedOut

    var errorDescription: String? {
        switch self {
        case .closed:
            return "Scribe is shutting down, so history was not cleared."
        case .clearTimedOut:
            return "Scribe is still saving earlier dictations, so history was not cleared. Try again in a moment."
        }
    }
}

/// Commits dictation history in the background, one step at a time, in the order the steps were
/// accepted: the macOS side of Windows `HistoryWriter`.
///
/// A dictation hands its entry here once the text has been delivered and moves on, so storage latency
/// never sits between speaking and typing. History is best-effort from that moment until the entry is
/// committed: a crash in between loses it (the tray's recovery ring is memory-only), and the drain at
/// quit is bounded. `enqueue` never blocks, because its caller is the main actor: when `capacity`
/// writes are already outstanding (the database has failed to commit for that many dictations in a
/// row) the new entry is dropped and logged rather than waited for. Entries hold text only, which is
/// why the bound is larger than the two Windows allows, whose entries can carry captured audio.
///
/// Clear history is a step in the same line (`clearHistory(timeout:)`), so it runs after every write
/// accepted before it and before any accepted after it: no entry dictated before a successful Clear
/// can land afterwards. `complete(timeout:)` closes the writer at quit with a bounded wait; anything
/// still queued behind a step that outlives the wait is abandoned, never run later.
///
/// Logs are shapes only: counts, durations and SQLite result codes, never an entry's text or an error
/// message, which a failure could build from the content being written.
///
/// `@unchecked Sendable` because the compiler cannot see the locking: every mutable field below, and
/// the `state` of each queued Clear, is only read or written while `condition` is held, and the
/// recorder is itself `Sendable`.
final class HistoryWriter: @unchecked Sendable {
    static let defaultCapacity = 16

    /// Test seams. The app uses none.
    struct Hooks: Sendable {
        /// Called while holding the writer's lock when a `waitForAcceptedWrites` caller first has to
        /// wait, so a test knows the barrier is in place instead of guessing from a timer.
        var onBarrierWait: (@Sendable () -> Void)?
        /// Called once a Clear is queued, so a test can release earlier writes knowing it is behind them.
        var onClearQueued: (@Sendable () -> Void)?
    }

    let capacity: Int

    private struct PendingWrite {
        let ticket: UInt64
        let record: DictationHistoryRecord
        let dictationID: UInt64
        let queuedAt: UInt64
    }

    /// One Clear waiting in line. A reference, so its timeout can withdraw it while it is still queued.
    /// `@unchecked Sendable`: `state` is only touched while the writer's `condition` is held, and
    /// `resume` is called exactly once, outside it.
    private final class PendingClear: @unchecked Sendable {
        enum State {
            case queued
            case running
            case settled
        }

        let ticket: UInt64
        var state = State.queued
        let resume: @Sendable (Result<Int, Error>) -> Void

        init(ticket: UInt64, resume: @escaping @Sendable (Result<Int, Error>) -> Void) {
            self.ticket = ticket
            self.resume = resume
        }
    }

    private enum Work {
        case write(PendingWrite)
        case clear(PendingClear)
    }

    /// What the consumer does with the next item, decided under the lock together with taking it.
    private enum Step {
        case commit(PendingWrite)
        case drop(PendingWrite)
        case clear(PendingClear)
        case skip(PendingClear, abandonedAtClose: Bool)
    }

    private let recorder: any HistoryRecording
    private let hooks: Hooks
    private let queue = DispatchQueue(label: "com.scribe.macos.history-writer", qos: .utility)
    private let logger = Logger(subsystem: "com.scribe.macos", category: "History")
    private let condition = NSCondition()

    private var pending: [Work] = []
    private var pendingWrites = 0
    /// Tickets issued to accepted steps. Steps finish strictly in ticket order, so `finished` is a
    /// watermark: every ticket at or below it has run, failed, been withdrawn or been abandoned.
    private var accepted: UInt64 = 0
    private var finished: UInt64 = 0
    private var consumerActive = false
    private var inFlight = false
    private var inFlightIsWrite = false
    private var closed = false
    private var abandonQueued = false
    private var droppedForRoom = 0
    private var abandoned = 0

    init(recorder: any HistoryRecording, capacity: Int = HistoryWriter.defaultCapacity, hooks: Hooks = Hooks()) {
        self.recorder = recorder
        self.capacity = max(1, capacity)
        self.hooks = hooks
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

        let outstanding = pendingWrites + (inFlightIsWrite ? 1 : 0)
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
            .write(
                PendingWrite(
                    ticket: accepted,
                    record: record,
                    dictationID: dictationID,
                    queuedAt: DispatchTime.now().uptimeNanoseconds)))
        pendingWrites += 1
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

    /// Deletes all history as a step in this writer's line, after every write accepted before the call,
    /// and returns how many entries went. If the step has not started within `timeout`, it is withdrawn
    /// and this throws `HistoryWriterError.clearTimedOut`, having deleted nothing; a withdrawn Clear
    /// never runs later. Once started it always finishes (bounded by the store's busy timeout).
    ///
    /// What it removes is every entry whose write was accepted before the call. A dictation still being
    /// transcribed, cleaned up or inserted when Clear is requested has not reached the writer yet, so it
    /// is recorded afterwards. That matches Windows, whose Clear deletes what the database holds at that
    /// moment.
    func clearHistory(timeout: TimeInterval) async throws -> Int {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Int, Error>) in
            condition.lock()
            guard !closed else {
                condition.unlock()
                continuation.resume(throwing: HistoryWriterError.closed)
                return
            }

            accepted += 1
            let request = PendingClear(ticket: accepted) { result in
                continuation.resume(with: result)
            }
            pending.append(.clear(request))
            let startConsumer = !consumerActive
            consumerActive = true
            condition.unlock()

            hooks.onClearQueued?()
            if startConsumer {
                queue.async { [self] in
                    consume()
                }
            }
            // Strong on purpose: this closure is what guarantees the continuation is resumed even if
            // nothing else ever reaches the request, and it keeps the writer alive for `timeout` at most.
            DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + timeout) { [self] in
                withdraw(request)
            }
        }
    }

    /// Waits up to `timeout` until every step accepted before this call has finished. Steps accepted
    /// after the call are not waited for. Returns false when the wait timed out; it cancels nothing.
    func waitForAcceptedWrites(timeout: TimeInterval) -> Bool {
        let deadline = Date(timeIntervalSinceNow: timeout)
        condition.lock()
        defer { condition.unlock() }

        let target = accepted
        if finished < target {
            hooks.onBarrierWait?()
        }
        while finished < target {
            if !condition.wait(until: deadline) {
                return finished >= target
            }
        }
        return true
    }

    /// Stops accepting steps and waits up to `timeout` for the accepted ones to finish. Steps still
    /// queued behind a step that outlives the wait are abandoned rather than run later; a queued Clear
    /// then fails with `HistoryWriterError.closed`.
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
                "History writer closed with \(result.stillWriting) step running, \(result.abandoned) abandoned.")
        }
        return result
    }

    // MARK: - Consumer (on `queue`)

    private func consume() {
        while let step = takeNextStep() {
            switch step {
            case .commit(let item):
                let started = DispatchTime.now().uptimeNanoseconds
                var failure: (any Error)?
                do {
                    try recorder.recordDictation(item.record)
                } catch {
                    // Isolated to this entry: its dictation already finished, and the next write still runs.
                    failure = error
                }
                // Reported before the write counts as finished, so a caller that waited for it finds
                // the outcome already in the log.
                let writeNanoseconds = DispatchTime.now().uptimeNanoseconds - started
                report(item, dropped: false, failure: failure, writeNanoseconds: writeNanoseconds)
                finish(item.ticket, abandonedOne: false)

            case .drop(let item):
                report(item, dropped: true, failure: nil, writeNanoseconds: 0)
                finish(item.ticket, abandonedOne: true)

            case .clear(let request):
                let result = Result { try recorder.clearHistory() }
                reportClear(result)
                finish(request.ticket, abandonedOne: false)
                request.resume(result)

            case .skip(let request, let abandonedAtClose):
                finish(request.ticket, abandonedOne: abandonedAtClose)
                if abandonedAtClose {
                    request.resume(.failure(HistoryWriterError.closed))
                }
            }
        }
    }

    /// Takes the next item and records what is now running, both under the lock, so `complete` never
    /// sees an empty, idle queue while a step is about to start.
    private func takeNextStep() -> Step? {
        condition.lock()
        defer { condition.unlock() }
        guard !pending.isEmpty else {
            consumerActive = false
            condition.broadcast()
            return nil
        }

        switch pending.removeFirst() {
        case .write(let item):
            pendingWrites -= 1
            guard !abandonQueued else {
                return .drop(item)
            }
            inFlight = true
            inFlightIsWrite = true
            return .commit(item)

        case .clear(let request):
            guard request.state == .queued else {
                // Withdrawn by its timeout: it never runs.
                return .skip(request, abandonedAtClose: false)
            }
            guard !abandonQueued else {
                request.state = .settled
                return .skip(request, abandonedAtClose: true)
            }
            request.state = .running
            inFlight = true
            return .clear(request)
        }
    }

    private func finish(_ ticket: UInt64, abandonedOne: Bool) {
        condition.lock()
        inFlight = false
        inFlightIsWrite = false
        finished = ticket
        if abandonedOne {
            abandoned += 1
        }
        condition.broadcast()
        condition.unlock()
    }

    private func withdraw(_ request: PendingClear) {
        condition.lock()
        guard request.state == .queued else {
            condition.unlock()
            return
        }
        request.state = .settled
        condition.unlock()

        logger.warning("Clear history was withdrawn: earlier history writes were still running when it timed out.")
        request.resume(.failure(HistoryWriterError.clearTimedOut))
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
                """
                #\(item.dictationID) history write failed after \(writeMilliseconds) ms \
                (\(errorType, privacy: .public), SQLite \(code)).
                """
            )
            return
        }

        let totalMilliseconds = (DispatchTime.now().uptimeNanoseconds - item.queuedAt) / 1_000_000
        logger.debug(
            """
            #\(item.dictationID) history committed \(totalMilliseconds) ms after queueing \
            (write \(writeMilliseconds) ms).
            """
        )
    }

    private func reportClear(_ result: Result<Int, Error>) {
        switch result {
        case .success(let removed):
            logger.info("History cleared in order: \(removed) entries removed.")
        case .failure(let error):
            let code = (error as? PersistenceError)?.sqliteCode ?? -1
            let errorType = String(describing: type(of: error))
            logger.error("Clear history failed (\(errorType, privacy: .public), SQLite \(code)).")
        }
    }
}
