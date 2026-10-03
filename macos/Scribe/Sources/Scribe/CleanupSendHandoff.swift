import Foundation
import os

/// The settings write and URLSession task resume are ordered at this boundary, not across an await.
final class CleanupSettingsHandoff: @unchecked Sendable {
    static let shared = CleanupSettingsHandoff()
    private let lock = NSRecursiveLock()
    private var revisions: [CleanupSettingsStore.Domain: UUID] = [:]
    private var secretChanges: [CleanupSettingsStore.Domain: Int] = [:]

    func synchronized<Value>(_ operation: () throws -> Value) rethrows -> Value {
        lock.lock()
        defer { lock.unlock() }
        return try operation()
    }

    func revision(for domain: CleanupSettingsStore.Domain) -> UUID {
        synchronized {
            if let revision = revisions[domain] { return revision }
            let revision = UUID()
            revisions[domain] = revision
            return revision
        }
    }

    func changed(_ domain: CleanupSettingsStore.Domain) {
        synchronized { revisions[domain] = UUID() }
    }

    func beginSecretChange(_ domain: CleanupSettingsStore.Domain) {
        synchronized {
            changed(domain)
            secretChanges[domain, default: 0] += 1
        }
    }

    func endSecretChange(_ domain: CleanupSettingsStore.Domain) {
        synchronized {
            changed(domain)
            secretChanges[domain, default: 0] -= 1
        }
    }

    func hasSecretChange(_ domain: CleanupSettingsStore.Domain) -> Bool {
        synchronized { secretChanges[domain, default: 0] > 0 }
    }
}

final class CleanupRequestLifetime: @unchecked Sendable {
    @TaskLocal static var current: CleanupRequestLifetime?
    private var closed = false

    func close() {
        CleanupSettingsHandoff.shared.synchronized { closed = true }
    }

    func check() throws {
        try CleanupSettingsHandoff.shared.synchronized {
            guard !closed else { throw CancellationError() }
        }
    }

    func whileOpen<Value: Sendable>(_ work: @Sendable () async throws -> Value) async throws -> Value {
        try check()
        let value = try await Self.$current.withValue(self) { try await work() }
        try check()
        return value
    }
}

struct CleanupSendHandoff: Sendable {
    enum Refusal: LocalizedError, Equatable {
        case settingsChanged

        var errorDescription: String? {
            "AI cleanup settings changed. Try again with the saved settings."
        }
    }

    @TaskLocal static var current: CleanupSendHandoff?
    private let store: CleanupSettingsStore
    private let snapshot: CleanupSettingsSnapshot
    private let revision: UUID
    private let lifetime: CleanupRequestLifetime?

    init(store: CleanupSettingsStore, lifetime: CleanupRequestLifetime? = nil) {
        let captured = CleanupSettingsHandoff.shared.synchronized {
            (store.snapshot(), CleanupSettingsHandoff.shared.revision(for: store.domain))
        }
        self.store = store
        snapshot = captured.0
        revision = captured.1
        self.lifetime = lifetime
    }

    func perform<Value>(_ start: () throws -> Value) throws -> Value {
        try CleanupSettingsHandoff.shared.synchronized {
            try lifetime?.check()
            guard snapshot.isEnabled, store.snapshot() == snapshot,
                CleanupSettingsHandoff.shared.revision(for: store.domain) == revision,
                !CleanupSettingsHandoff.shared.hasSecretChange(store.domain)
            else {
                throw Refusal.settingsChanged
            }
            return try start()
        }
    }

    static func data(for request: URLRequest, session: URLSession) async throws -> (Data, URLResponse) {
        let handoff = current
        let lifetime = CleanupRequestLifetime.current
        guard handoff != nil || lifetime != nil else { return try await session.data(for: request) }
        let operation = BoundCleanupDataTask()
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                let task = session.dataTask(with: request) { data, response, error in
                    if let error {
                        operation.finish(.failure(error))
                    } else if let data, let response {
                        operation.finish(.success((data, response)))
                    } else {
                        operation.finish(.failure(URLError(.badServerResponse)))
                    }
                }
                operation.install(task, continuation: continuation)
                do {
                    try CleanupSettingsHandoff.shared.synchronized {
                        try lifetime?.check()
                        if let handoff {
                            try handoff.perform { try operation.start() }
                        } else {
                            try operation.start()
                        }
                    }
                } catch {
                    operation.finish(.failure(error))
                    task.cancel()
                }
            }
        } onCancel: {
            operation.cancel()
        }
    }
}

private final class BoundCleanupDataTask: Sendable {
    private struct State {
        var task: URLSessionDataTask?
        var continuation: CheckedContinuation<(Data, URLResponse), any Error>?
        var cancelled = false
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    func install(
        _ task: URLSessionDataTask,
        continuation: CheckedContinuation<(Data, URLResponse), any Error>
    ) {
        state.withLock {
            $0.task = task
            $0.continuation = continuation
        }
    }

    func start() throws {
        try state.withLock {
            guard !$0.cancelled else { throw CancellationError() }
            $0.task?.resume()
        }
    }

    func finish(_ result: Result<(Data, URLResponse), any Error>) {
        let continuation = state.withLock {
            let continuation = $0.continuation
            $0.continuation = nil
            $0.task = nil
            return continuation
        }
        continuation?.resume(with: result)
    }

    func cancel() {
        let task = state.withLock {
            $0.cancelled = true
            return $0.task
        }
        task?.cancel()
    }
}
