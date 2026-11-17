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

/// The word pack permission AI cleanup is held to now. It is ordered by the operations that change it, never by the
/// persisted generation: two reads of one stored state share a generation and can still find different things (a
/// built-in paused by an unreadable file), and an unreadable state reads as generation 1, below any the gate has seen,
/// so a generation would keep a repaired state from widening what a reset narrowed. Each operation owns its place by a
/// ticket taken in memory when it begins, so nothing persisted is added.
///
/// - A read of the word packs (`DictionaryLibraryService.loadVocabulary`) takes a ticket when it begins, before it
///   reads anything, and publishes what it found with it. The ticket orders where the read began, not when it looked
///   at the files, so a read that began before one that has already published may have looked later than that one did
///   and seen a revocation it missed. It can therefore only narrow what is established, never widen it, and it leaves
///   the newest read where it was. A read newer than every read that has published, and that began after the newest
///   save that committed, replaces what is established, wider or narrower, which is how a repaired state restores a
///   permission whatever its generation. One that began before such a save can only narrow, never widen what the save
///   withdrew.
/// - A save that withdraws a permission narrows the gate at once, before it commits (`narrow`), and ends it with
///   `commit`, which keeps the narrowing, or `abandon`, which drops it. The narrowing is a hold the save owns on top of
///   what the reads established, not a copy of an earlier value to put back, so a save that does not commit never
///   gives back what a read found revoked meanwhile, however equal that read's result is to the narrowing.
///
/// A request that carries word pack vocabulary asks the gate as it leaves (`CleanupVocabularyAdmission`), under the
/// lock the settings handoff uses, so a revocation and the start of a request are ordered: a request either started
/// before the revocation took the lock, with the permission it was admitted under, or finds the permission narrowed
/// and is not sent.
///
/// Every operation below is made under `CleanupSettingsHandoff.shared`, and none waits while it holds it.
final class AiVocabularyGate: @unchecked Sendable {
    static let shared = AiVocabularyGate()

    /// A read's place among the operations that change the permission, taken when it begins.
    struct ReadTicket: Sendable, Equatable {
        fileprivate let order: UInt64
    }

    /// A save's hold on the permission, from the moment it narrows to the moment it commits or is abandoned.
    struct Narrowing: Sendable {
        fileprivate let order: UInt64
    }

    private var clock: UInt64 = 0
    /// What the reads and the committed saves have left, before the holds of saves still under way.
    private var established = AiVocabularyScope.none
    private var newestRead: UInt64 = 0
    private var newestCommit: UInt64 = 0
    private var holds: [UInt64: @Sendable (String, String?) -> Bool] = [:]

    /// Takes the ticket for a read of the word packs that is about to begin.
    func beginRead() -> ReadTicket {
        CleanupSettingsHandoff.shared.synchronized {
            clock += 1
            return ReadTicket(order: clock)
        }
    }

    /// Takes in what the read `read` found. A read that began before one that has already published only narrows what
    /// is established, and leaves the newest read as it was, so a read that began between the two and publishes later
    /// is not taken for the newest either, and cannot widen.
    func publish(_ scope: AiVocabularyScope, from read: ReadTicket) {
        CleanupSettingsHandoff.shared.synchronized {
            guard read.order > newestRead else {
                established = established.intersection(scope)
                return
            }
            newestRead = read.order
            established = read.order > newestCommit ? scope : established.intersection(scope)
        }
    }

    /// Takes in what a read that began and finished at once found, for a caller with no earlier read in flight.
    func publish(_ scope: AiVocabularyScope) {
        CleanupSettingsHandoff.shared.synchronized {
            publish(scope, from: beginRead())
        }
    }

    /// Narrows the permission at once to the libraries `keeps` accepts, each judged by its id and the content
    /// permitted for it, before the change that withdraws the others commits. The save ends the hold with `commit` or
    /// `abandon`; a permission only widens when a read newer than every read that has published, and than the last
    /// committed save, publishes it.
    @discardableResult
    func narrow(keeping keeps: @escaping @Sendable (_ library: String, _ content: String?) -> Bool) -> Narrowing {
        CleanupSettingsHandoff.shared.synchronized {
            clock += 1
            holds[clock] = keeps
            return Narrowing(order: clock)
        }
    }

    /// The save committed: what it withdrew stays withdrawn, and a read that began before it can no longer widen it.
    func commit(_ narrowing: Narrowing) {
        CleanupSettingsHandoff.shared.synchronized {
            guard let keeps = holds.removeValue(forKey: narrowing.order) else { return }
            established = established.narrowed(keeping: keeps)
            newestCommit = max(newestCommit, narrowing.order)
        }
    }

    /// The save did not commit: its hold ends and nothing else changes, so what reads published meanwhile stands.
    func abandon(_ narrowing: Narrowing) {
        CleanupSettingsHandoff.shared.synchronized {
            _ = holds.removeValue(forKey: narrowing.order)
        }
    }

    /// Whether the permission still covers `admitted`: every library it permitted, for the same content.
    func covers(_ admitted: AiVocabularyScope) -> Bool {
        CleanupSettingsHandoff.shared.synchronized { effective().covers(admitted) }
    }

    private func effective() -> AiVocabularyScope {
        guard !holds.isEmpty else { return established }
        let active = Array(holds.values)
        return established.narrowed { library, content in
            active.allSatisfy { $0(library, content) }
        }
    }
}

/// The word pack scope one dictation's cleanup requests were composed under, which each of them is held to as it
/// leaves. The dictation takes it with the vocabulary it admits (`CleanupVocabulary.admission`) and runs its requests
/// inside `whileAdmitted`, so a permission withdrawn while a request waited, for a credential say, stops it at the
/// last step before the transport starts (`CleanupSendHandoff.data`), and every retry is held to it again.
struct CleanupVocabularyAdmission: Sendable {
    @TaskLocal static var current: CleanupVocabularyAdmission?

    let scope: AiVocabularyScope
    let gate: AiVocabularyGate

    init(scope: AiVocabularyScope, gate: AiVocabularyGate = .shared) {
        self.scope = scope
        self.gate = gate
    }

    func check() throws {
        guard gate.covers(scope) else { throw CleanupSendHandoff.Refusal.vocabularyChanged }
    }

    /// Runs `work`, every request of which is held to this scope as it leaves. Nothing starts when the permission has
    /// already narrowed. A scope that permits no word pack has nothing to withdraw, so `work` runs as it is.
    func whileAdmitted<Value: Sendable>(_ work: @Sendable () async throws -> Value) async throws -> Value {
        guard !scope.permittedContent.isEmpty else { return try await work() }
        try check()
        return try await Self.$current.withValue(self) { try await work() }
    }
}

struct CleanupSendHandoff: Sendable {
    enum Refusal: LocalizedError, Equatable {
        case settingsChanged
        case vocabularyChanged

        var errorDescription: String? {
            switch self {
            case .settingsChanged:
                return "AI cleanup settings changed. Try again with the saved settings."
            case .vocabularyChanged:
                return "The word packs AI cleanup may use changed. Try again with the saved word packs."
            }
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
        let vocabulary = CleanupVocabularyAdmission.current
        guard handoff != nil || lifetime != nil || vocabulary != nil else {
            return try await session.data(for: request)
        }
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
                        // The word pack permission is judged at the same last step as the settings, under the same
                        // lock a save narrows it under, so a revocation made while this request waited for a
                        // credential still stops it.
                        let begin = {
                            try vocabulary?.check()
                            try operation.start()
                        }
                        if let handoff {
                            try handoff.perform(begin)
                        } else {
                            try begin()
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
