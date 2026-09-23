import Foundation

/// Why an operation run with `OperationDeadline.run` ended: its deadline passed first. `seconds` is the limit in whole
/// seconds, so a failure shape reads `OperationDeadlineError.exceeded values=90`.
enum OperationDeadlineError: Error, Equatable, Sendable {
    case exceeded(seconds: Int)
}

/// An elapsed-time limit on a whole piece of work, measured on the continuous clock from the call.
///
/// `URLRequest.timeoutInterval` is not one: it is an idle timeout that every byte received resets, and a request
/// waits on no timer at all while a token is fetched or an endpoint is looked up first. This is the macOS side of
/// Windows' `CancellationTokenSource.CancelAfter` around its readiness probe.
enum OperationDeadline {
    /// Runs `operation` and returns what it returns, or throws what it throws, unless `limit` passes first. Then the
    /// operation's task is cancelled and, once the operation has stopped, `OperationDeadlineError.exceeded` is thrown.
    /// Cancelling the caller cancels the operation too, and the call then throws the operation's own error, a
    /// `CancellationError` for everything Scribe runs, so a deadline and a cancellation stay apart.
    ///
    /// The operation must observe cancellation for the deadline to end it promptly, and everything Scribe runs this
    /// way does: `URLSession`'s async calls cancel their task, `ProcessRunner` stops its child, and `AsyncLane` lets a
    /// waiting caller go at once. Waiting for the operation to stop is what keeps its work from outliving the call.
    static func run<Value: Sendable>(
        within limit: Duration,
        _ operation: @escaping @Sendable () async throws -> Value
    ) async throws -> Value {
        try await withThrowingTaskGroup(of: Value.self) { group in
            group.addTask {
                try await operation()
            }
            group.addTask {
                try await Task.sleep(for: limit)
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
