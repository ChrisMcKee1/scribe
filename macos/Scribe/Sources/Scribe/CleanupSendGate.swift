import Foundation
import os

/// Identity only. Neither describing a receipt nor logging a refusal reveals configuration or vocabulary.
struct CleanupRecipient: Equatable, Sendable, CustomStringConvertible, CustomReflectable {
    let connection: CleanupConnection
    let settings: CleanupSettingsSnapshot

    var description: String { "CleanupRecipient" }
    var customMirror: Mirror { Mirror(self, children: [:]) }
}

enum CleanupRequestKind: Sendable, Equatable {
    case dictation
    case probe
    case auxiliary
    case explicitCommand
}

enum CleanupHoldback: String, Error, LocalizedError, Sendable, Equatable {
    case noAdmission
    case vocabularyChanged
    case recipientChanged
    case closed

    var errorDescription: String? {
        switch self {
        case .vocabularyChanged:
            return "Your word pack choices changed before the request was sent, so AI cleanup was skipped."
        case .recipientChanged:
            return "Where AI cleanup runs changed before the request was sent, so nothing was sent."
        case .noAdmission, .closed:
            return "AI cleanup was skipped because this request is no longer allowed."
        }
    }
}

/// Publication and starting a transport share this gate. The response is always awaited outside it.
final class CleanupSendGate: Sendable {
    static let shared = CleanupSendGate()

    private struct State {
        var scope = AiVocabularyScope.none
        var recipient: CleanupRecipient?
        var closed = false
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    /// Publish on every permission/content change, including a change at the same catalog generation.
    func publishVocabulary(_ scope: AiVocabularyScope) {
        state.withLock { $0.scope = scope }
    }

    func publishRecipient(_ recipient: CleanupRecipient?) {
        state.withLock { $0.recipient = recipient }
    }

    /// A logical commit may publish both authorities together. Prepare files and credentials beforehand.
    /// This callback is synchronous; it must neither await nor call back into this gate.
    func committing<Result>(
        vocabulary: AiVocabularyScope,
        recipient: CleanupRecipient?,
        _ commit: () throws -> Result
    ) rethrows -> Result {
        try state.withLockUnchecked { current in
            let result = try commit()
            current.scope = vocabulary
            current.recipient = recipient
            return result
        }
    }

    var currentVocabularyScope: AiVocabularyScope {
        state.withLock { $0.scope }
    }

    func close() {
        state.withLock { $0.closed = true }
    }

    func receipt(
        scope: AiVocabularyScope,
        recipient: CleanupRecipient,
        kind: CleanupRequestKind,
        isCurrent: @escaping @Sendable () -> Bool = { true }
    ) -> CleanupRequestReceipt {
        CleanupRequestReceipt(
            gate: self, scope: scope, recipient: recipient, kind: kind, isCurrent: isCurrent)
    }

    fileprivate func start<Result>(
        _ receipt: CleanupRequestReceipt,
        _ start: () throws -> Result
    ) throws -> Result {
        // The callback runs synchronously on the caller, never escapes or crosses an isolation boundary.
        try state.withLockUnchecked { current in
            guard !current.closed else { throw CleanupHoldback.closed }
            guard current.scope.covers(receipt.scope) else { throw CleanupHoldback.vocabularyChanged }
            guard current.recipient == receipt.recipient, receipt.isCurrent() else {
                throw CleanupHoldback.recipientChanged
            }
            return try start()
        }
    }
}

struct CleanupRequestReceipt: Sendable, CustomStringConvertible, CustomReflectable {
    fileprivate let gate: CleanupSendGate
    let scope: AiVocabularyScope
    let recipient: CleanupRecipient
    let kind: CleanupRequestKind
    fileprivate let isCurrent: @Sendable () -> Bool

    var description: String { "CleanupRequestReceipt" }
    var customMirror: Mirror { Mirror(self, children: [:]) }

    func check() throws {
        try start {}
    }

    func start<Result>(_ start: () throws -> Result) throws -> Result {
        try gate.start(self, start)
    }
}

/// Local model preparation inherits the same receipt as the inference it prepares.
enum CleanupSendContext {
    @TaskLocal static var receipt: CleanupRequestReceipt?
    @TaskLocal static var beforeTransportStart: (@Sendable () async -> Void)?
}

/// The application factory wraps its clients. Raw clients remain usable by isolated wire tests and CLI callers
/// that supply no application vocabulary; an application client can never accidentally send without admission.
struct AdmittedCleanupProvider: CleanupProvider {
    let provider: any CleanupProvider

    var id: String { provider.id }
    var displayName: String { provider.displayName }
    var usesLocalCleanupPrompt: Bool { provider.usesLocalCleanupPrompt }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        guard let receipt = request.receipt else { throw CleanupHoldback.noAdmission }
        try receipt.check()
        return try await CleanupSendContext.$receipt.withValue(receipt) {
            try await provider.clean(request)
        }
    }
}

/// `data(for:)` starts after a suspension. A suspended data task resumed under the gate supplies an actual
/// synchronous handoff, and cancellation is registered before that resume rather than after sending.
enum CleanupHTTP {
    private final class Completion: Sendable {
        private let pending: OSAllocatedUnfairLock<CheckedContinuation<(Data, URLResponse), any Error>?>

        init(_ continuation: CheckedContinuation<(Data, URLResponse), any Error>) {
            pending = OSAllocatedUnfairLock(initialState: continuation)
        }

        func finish(_ result: Result<(Data, URLResponse), any Error>) {
            let continuation = pending.withLock { current in
                let value = current
                current = nil
                return value
            }
            continuation?.resume(with: result)
        }
    }

    private struct Cancellation {
        var cancelled = false
        var task: URLSessionDataTask?
    }

    static func send(
        _ request: URLRequest,
        session: URLSession,
        receipt: CleanupRequestReceipt?
    ) async throws -> (Data, URLResponse) {
        if let beforeStart = CleanupSendContext.beforeTransportStart {
            await beforeStart()
        }
        try Task.checkCancellation()
        let cancellation = OSAllocatedUnfairLock(initialState: Cancellation())
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                let completion = Completion(continuation)
                let task = session.dataTask(with: request) { data, response, error in
                    if let error {
                        completion.finish(.failure(error))
                    } else if let data, let response {
                        completion.finish(.success((data, response)))
                    } else {
                        completion.finish(.failure(URLError(.badServerResponse)))
                    }
                }
                task.delegate = RedirectRefuser()
                do {
                    let start = {
                        try cancellation.withLock { current in
                            guard !current.cancelled else { throw CancellationError() }
                            current.task = task
                            task.resume()
                        }
                    }
                    if let receipt {
                        try receipt.start(start)
                    } else {
                        try start()
                    }
                } catch {
                    completion.finish(.failure(error))
                    task.cancel()
                }
            }
        } onCancel: {
            cancellation.withLock { current in
                current.cancelled = true
                current.task?.cancel()
            }
        }
    }

    private final class RedirectRefuser: NSObject, URLSessionTaskDelegate, Sendable {
        func urlSession(
            _ session: URLSession,
            task: URLSessionTask,
            willPerformHTTPRedirection response: HTTPURLResponse,
            newRequest request: URLRequest,
            completionHandler: @escaping @Sendable (URLRequest?) -> Void
        ) {
            completionHandler(nil)
        }
    }
}
