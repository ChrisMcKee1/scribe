import AppKit
import Foundation

/// Provider work that runs outside dictation: Settings' Test Connection and the Usage Insights summary. Either can
/// start `az` or `foundry`, and `ProcessRunner` stops and reaps a child's process group only when the call awaiting
/// it returns, so Quit closes this before it replies: nothing new is admitted, everything running is cancelled, and
/// Quit waits until all of it has returned (`ApplicationTermination`).
@MainActor
final class AuxiliaryOperations {
    /// The operations of the app's windows.
    static let shared = AuxiliaryOperations()

    /// Why an operation did not run.
    enum Refusal: LocalizedError, Equatable {
        /// Quit has closed admission.
        case closed

        var errorDescription: String? {
            "Scribe is quitting."
        }
    }

    private struct Running {
        let cancel: @Sendable () -> Void
        let finished: @Sendable () async -> Void
    }

    private var running: [UInt64: Running] = [:]
    private var nextID: UInt64 = 0
    private let beforeStart: @Sendable () async -> Void
    private(set) var isClosed = false
    /// How many operations were ever admitted: past the refusal at Quit and the caller's cancellation, into a task of
    /// their own.
    private(set) var admittedCount = 0

    /// `beforeStart` runs in each admitted operation's own task, before that task checks for cancellation and starts
    /// the operation; tests hold it there to cancel the caller in between.
    init(beforeStart: @escaping @Sendable () async -> Void = {}) {
        self.beforeStart = beforeStart
    }

    /// How many operations are running.
    var runningCount: Int {
        running.count
    }

    /// Runs `operation` off the main actor as one registered operation and returns its result. Cancelling the
    /// calling task cancels the operation, and the call returns only once the operation has. Throws
    /// `Refusal.closed`, without running anything, once Quit has closed admission, and `CancellationError`, without
    /// running anything, when the calling task is already cancelled: a detached task does not inherit its creator's
    /// cancellation, so an operation started for a cancelled caller would run uncancelled until the forwarding below
    /// reached it, past any check of its own it makes on entry.
    func run<Value: Sendable>(_ operation: @escaping @Sendable () async throws -> Value) async throws -> Value {
        guard !isClosed else { throw Refusal.closed }
        try Task.checkCancellation()
        admittedCount += 1
        nextID &+= 1
        let id = nextID
        let beforeStart = beforeStart
        let task = Task.detached(priority: .userInitiated) {
            await beforeStart()
            // A cancellation forwarded before this task got here: the operation never starts.
            try Task.checkCancellation()
            return try await operation()
        }
        running[id] = Running(
            cancel: { task.cancel() },
            finished: { _ = try? await task.value })
        defer { running[id] = nil }
        // A cancellation of work already admitted is forwarded to it.
        return try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            task.cancel()
        }
    }

    /// Quit: nothing new is admitted, and every operation running is cancelled.
    func beginClosing() {
        isClosed = true
        for operation in running.values {
            operation.cancel()
        }
    }

    /// Returns once every operation running now has returned.
    func waitUntilFinished() async {
        let operations = Array(running.values)
        for operation in operations {
            await operation.finished()
        }
    }
}

/// Quit, in its one order. `request()` answers `applicationShouldTerminate` with `.terminateLater` every time and
/// starts the work once: a second Quit while shutdown runs schedules nothing more, and `reply` runs exactly once,
/// after the work. The push-to-talk tap stops, admission to provider work outside dictation closes and that work is
/// cancelled, dictation shuts down in its own order (`DictationController.shutDown`), the cancelled provider work is
/// awaited so its children are reaped, this process's scratch audio is removed, and only then does the app reply.
@MainActor
final class ApplicationTermination {
    struct Work {
        /// Stops the push-to-talk event tap, so no new press arrives.
        var stopListening: @MainActor @Sendable () -> Void
        var operations: AuxiliaryOperations
        var dictation: DictationController
        /// Stops storage maintenance; blocks, so the dictation shutdown runs it off the main actor.
        var stopMaintenance: (@Sendable () -> Void)?
        /// Removes this process's scratch audio; blocks, so it runs off the main actor.
        var removeScratchAudio: @Sendable () -> Void
    }

    private let work: Work
    private let reply: @MainActor @Sendable () -> Void
    private var running: Task<Void, Never>?

    init(work: Work, reply: @escaping @MainActor @Sendable () -> Void) {
        self.work = work
        self.reply = reply
    }

    /// Whether Quit has begun.
    var isTerminating: Bool {
        running != nil
    }

    /// The answer to `applicationShouldTerminate`.
    func request() -> NSApplication.TerminateReply {
        guard running == nil else {
            ScribeLog.info(.app, "Quit was asked for again while Scribe is already shutting down")
            return .terminateLater
        }
        let work = work
        let reply = reply
        running = Task { @MainActor in
            work.stopListening()
            work.operations.beginClosing()
            _ = await work.dictation.shutDown(stoppingMaintenance: work.stopMaintenance)
            await work.operations.waitUntilFinished()
            let removeScratchAudio = work.removeScratchAudio
            await Task.detached(priority: .userInitiated) {
                removeScratchAudio()
            }.value
            ScribeLog.info(.app, "Shutdown finished; Scribe replies to Quit")
            reply()
        }
        return .terminateLater
    }

    /// Returns once the reply has been sent.
    func waitUntilReplied() async {
        await running?.value
    }
}
