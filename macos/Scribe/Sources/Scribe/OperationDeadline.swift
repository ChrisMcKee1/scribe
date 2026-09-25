import Foundation

/// Why an operation run with `OperationDeadline.run` ended: its deadline passed first. `seconds` is the limit in whole
/// seconds, so a failure shape reads `OperationDeadlineError.exceeded values=90`.
enum OperationDeadlineError: Error, Equatable, Sendable {
    case exceeded(seconds: Int)
}

/// An elapsed-time limit on a whole piece of work, measured from the call.
///
/// `URLRequest.timeoutInterval` is not one: it is an idle timeout that every byte received resets, and a request
/// waits on no timer at all while a token is fetched or an endpoint is looked up first. This is the macOS side of
/// Windows' `CancellationTokenSource.CancelAfter` around its readiness probe.
///
/// Cancellation is cooperative, as it is on Windows: when the deadline passes, the operation's task is cancelled and
/// the call then waits for the operation to stop. Everything asynchronous Scribe runs this way stops at once
/// (`URLSession`'s async calls cancel their task, `ProcessRunner` stops its child, `AsyncLane` lets a waiting caller
/// go), but a synchronous step already under way, such as a Keychain read waiting for the user to answer a prompt,
/// finishes first. Waiting for the operation to stop is what keeps its work from outliving the call.
enum OperationDeadline {
    /// Runs `operation` and returns what it returns, or throws what it throws, unless `limit` passes first. Then the
    /// operation's task is cancelled and, once the operation has stopped, `OperationDeadlineError.exceeded` is thrown.
    /// Cancelling the caller cancels the operation too, and the call then throws the operation's own error, a
    /// `CancellationError` for everything Scribe runs, so a deadline and a cancellation stay apart.
    ///
    /// - Parameter sleep: Waits out the limit; `Task.sleep` on the continuous clock. A test passes a timer it fires by
    ///   hand, so it can decide exactly where in the operation the deadline passes.
    static func run<Value: Sendable>(
        within limit: Duration,
        sleep: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) },
        _ operation: @escaping @Sendable () async throws -> Value
    ) async throws -> Value {
        try await withThrowingTaskGroup(of: Value.self) { group in
            group.addTask {
                try await operation()
            }
            group.addTask {
                try await sleep(limit)
                throw OperationDeadlineError.exceeded(seconds: Int(limit.components.seconds))
            }
            // The first to finish decides; the other is cancelled, and the group waits for it before returning.
            defer { group.cancelAll() }
            guard let first = try await group.next() else {
                throw CancellationError()
            }
            return first
        }
    }
}
