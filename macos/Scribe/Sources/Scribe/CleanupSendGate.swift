import Foundation
import os

/// Identity only. Neither describing a receipt nor logging a refusal reveals configuration or vocabulary.
struct CleanupRecipient: Equatable, Sendable, CustomStringConvertible, CustomReflectable {
    let connection: CleanupConnection
    let settings: CleanupSettingsSnapshot
    var candidateRevision: UUID? = nil

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
    case changedAfterSending
    case closed

    var errorDescription: String? {
        switch self {
        case .vocabularyChanged:
            return "Your word pack choices changed, so AI cleanup did not continue."
        case .recipientChanged:
            return "Where AI cleanup runs changed, so this request did not continue."
        case .changedAfterSending:
            return "AI cleanup changed while the request was running, so its answer wasn't used."
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
        var uncertain = false
        var vocabularyRevision: UInt64 = 0
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    /// Publish on every permission/content change, including a change at the same catalog generation.
    func publishVocabulary(_ scope: AiVocabularyScope) {
        state.withLock {
            $0.scope = scope
            $0.vocabularyRevision &+= 1
        }
    }

    var vocabularyRevision: UInt64 {
        state.withLock { $0.vocabularyRevision }
    }

    /// A legacy asynchronous read must not republish permissions a commit withdrew while it was reading.
    @discardableResult
    func publishReadVocabulary(_ scope: AiVocabularyScope, after revision: UInt64) -> Bool {
        state.withLock { current in
            guard current.vocabularyRevision == revision else { return false }
            current.scope = scope
            current.vocabularyRevision &+= 1
            return true
        }
    }

    func publishRecipient(_ recipient: CleanupRecipient?) {
        state.withLock { $0.recipient = recipient }
    }

    /// Recovery publishes a verified pair; merely capturing a recipient cannot undo an uncertain commit.
    func publish(vocabulary: AiVocabularyScope, recipient: CleanupRecipient?) {
        state.withLock {
            $0.scope = vocabulary
            $0.recipient = recipient
            $0.vocabularyRevision &+= 1
            $0.uncertain = false
        }
    }

    /// A logical commit may publish both authorities together. Prepare files and credentials beforehand.
    /// This callback is synchronous; it must neither await nor call back into this gate.
    /// A thrown commit can have an uncertain durable outcome, so it withholds sends until recovery publishes.
    func committing<Result>(
        vocabulary: AiVocabularyScope,
        recipient: CleanupRecipient?,
        _ commit: () throws -> Result
    ) rethrows -> Result {
        try state.withLockUnchecked { current in
            do {
                let result = try commit()
                current.scope = vocabulary
                current.vocabularyRevision &+= 1
                current.recipient = recipient
                current.uncertain = false
                return result
            } catch {
                current.scope = .none
                current.recipient = nil
                current.uncertain = true
                current.vocabularyRevision &+= 1
                throw error
            }
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
            guard !current.uncertain else { throw CleanupHoldback.noAdmission }
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
    let recipient: CleanupRecipient

    var id: String { provider.id }
    var displayName: String { provider.displayName }
    var usesLocalCleanupPrompt: Bool { provider.usesLocalCleanupPrompt }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        guard let receipt = request.receipt else { throw CleanupHoldback.noAdmission }
        guard receipt.recipient == recipient else { throw CleanupHoldback.recipientChanged }
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
