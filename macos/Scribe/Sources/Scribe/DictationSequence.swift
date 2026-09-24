import Foundation

/// Turns taken in dictation order. Each dictation enrolls when it is admitted to processing, which happens in the
/// order its recordings stopped, and `waitForTurn` returns once every dictation enrolled before it has finished its
/// turn, so a later dictation's step can never overtake an earlier one's, whichever of them got there first.
///
/// The dictation lifecycle keeps two: one for speech recognition, so only one recognizer runs at a time, and one for
/// delivery, so a second dictation's text is never inserted before the first's, even when its cleanup finished
/// sooner. Every enrolled dictation must call `finish` on every way out, or the ones behind it wait forever; the
/// lifecycle does it in the `defer` of each dictation's processing.
///
/// Main-actor state without locks.
@MainActor
final class DictationTurns {
    private var enrolled = Set<UInt64>()
    private var waiting: [UInt64: CheckedContinuation<Bool, Never>] = [:]

    init() {}

    /// How many dictations hold a place: waiting, taking their turn, or not there yet.
    var enrolledCount: Int {
        enrolled.count
    }

    /// How many are suspended in `waitForTurn`.
    var waitingCount: Int {
        waiting.count
    }

    func enroll(_ dictation: RecordingID) {
        enrolled.insert(dictation.rawValue)
    }

    /// Returns true once every dictation enrolled before `dictation` has finished, at once when none has. Returns
    /// false when the calling task is cancelled first, or when `dictation` is not enrolled. A cancelled wait keeps its
    /// place until `finish`, which the caller still owes.
    func waitForTurn(_ dictation: RecordingID) async -> Bool {
        let ticket = dictation.rawValue
        guard enrolled.contains(ticket), !Task.isCancelled else { return false }
        if enrolled.min() == ticket {
            return true
        }
        return await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
                if Task.isCancelled {
                    continuation.resume(returning: false)
                } else {
                    waiting[ticket] = continuation
                }
            }
        } onCancel: {
            // Runs on whichever thread cancelled; the wait is state of the main actor.
            Task { @MainActor [weak self] in
                self?.stopWaiting(ticket)
            }
        }
    }

    /// Gives up `dictation`'s place and hands the turn to the earliest dictation still enrolled. Safe to call more
    /// than once.
    func finish(_ dictation: RecordingID) {
        let ticket = dictation.rawValue
        enrolled.remove(ticket)
        stopWaiting(ticket)
        if let next = enrolled.min(), let waiter = waiting.removeValue(forKey: next) {
            waiter.resume(returning: true)
        }
    }

    private func stopWaiting(_ ticket: UInt64) {
        waiting.removeValue(forKey: ticket)?.resume(returning: false)
    }
}

/// Waits for `operation` and returns its value, or returns nil as soon as the calling task is cancelled, whichever
/// comes first. The operation itself runs on to its end in a task of its own, and a result that arrives after the
/// cancellation is dropped: this is for waits that cannot be cancelled themselves but finish on their own, such as
/// `StartupGate`, so a dictation waiting there at quit does not hold up the quit.
@MainActor
func awaitUnlessCancelled<Value: Sendable>(
    _ operation: @escaping @MainActor @Sendable () async -> Value
) async -> Value? {
    let outcome = FirstValue<Value>()
    return await withTaskCancellationHandler {
        await withCheckedContinuation { (continuation: CheckedContinuation<Value?, Never>) in
            outcome.install(continuation)
            if Task.isCancelled {
                outcome.settle(nil)
            } else {
                Task { @MainActor in
                    outcome.settle(await operation())
                }
            }
        }
    } onCancel: {
        Task { @MainActor in
            outcome.settle(nil)
        }
    }
}

/// Hands the first value it is given to the one continuation it holds, and ignores every later one.
@MainActor
private final class FirstValue<Value: Sendable> {
    private var continuation: CheckedContinuation<Value?, Never>?
    private var settled = false

    func install(_ continuation: CheckedContinuation<Value?, Never>) {
        self.continuation = continuation
    }

    func settle(_ value: Value?) {
        guard !settled, let continuation else { return }
        settled = true
        self.continuation = nil
        continuation.resume(returning: value)
    }
}
