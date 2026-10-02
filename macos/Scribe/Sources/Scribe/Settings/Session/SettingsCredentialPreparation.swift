import Foundation
import os

enum SettingsCredentialSlot: String, Codable, Sendable {
    case customApiKey
    case azureClientSecret
}

struct SettingsCredentialID: Hashable, Sendable {
    let slot: SettingsCredentialSlot
    let account: String

    var key: String { slot.rawValue + ":" + account }
}

enum SettingsCredentialReference: Codable, Equatable, Sendable {
    static let storageKey = "settings.credential.references.v1"
    case absent
    case account(String)
}

/// Credential edits are memory-only and intentionally not Codable.
enum SettingsCredentialEdit: Equatable, Sendable, CustomStringConvertible, CustomReflectable {
    case keep
    case remove
    case replace(String)

    var description: String { "SettingsCredentialEdit(redacted)" }
    var customMirror: Mirror { Mirror(self, children: ["redacted": true]) }
}

struct SettingsPreparedCredentials: Sendable {
    let references: [String: SettingsCredentialReference]
    let allocated: [SettingsCredentialID]

    func attach(to submission: inout SettingsSubmission, previous: String?) throws {
        let data = try JSONEncoder().encode(references)
        submission.attachment.expectedValues[SettingsCredentialReference.storageKey] = .some(previous)
        submission.attachment.values[SettingsCredentialReference.storageKey] = String(decoding: data, as: UTF8.self)
        submission.document.preferences["ScribeCleanupSecretRevision"] = .string(submission.id.uuidString)
    }
}

/// Only generated accounts are touched before the SQLite commit. A late worker can never change a serving account.
enum SettingsCredentialPreparer {
    static func prepare(
        edits: [SettingsCredentialID: SettingsCredentialEdit],
        existing: [String: SettingsCredentialReference],
        stores: [SettingsCredentialSlot: any SecretStore],
        within limit: Duration = .seconds(10),
        sleep: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }
    ) async throws -> SettingsPreparedCredentials {
        try Task.checkCancellation()
        let completion = SettingsCredentialPreparationCompletion()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                completion.install(continuation)
                let worker = Task.detached(priority: .userInitiated) {
                    var references = existing
                    var allocated: [SettingsCredentialID] = []
                    do {
                        for (id, edit) in edits {
                            guard !completion.isFinished else { throw CancellationError() }
                            switch edit {
                            case .keep:
                                break
                            case .remove:
                                references[id.key] = .absent
                            case .replace(let secret):
                                guard !secret.isEmpty, let store = stores[id.slot] else {
                                    throw SettingsSaveFailure.credentials
                                }
                                let account = "settings-" + UUID().uuidString
                                let generated = SettingsCredentialID(slot: id.slot, account: account)
                                allocated.append(generated)
                                try store.save(secret, for: account)
                                references[id.key] = .account(account)
                            }
                        }
                        let prepared = SettingsPreparedCredentials(references: references, allocated: allocated)
                        if completion.finish(.success(prepared)) { return }
                    } catch {
                        let failure =
                            (error as? SettingsSaveFailure) ?? (error is CancellationError ? .cancelled : .credentials)
                        _ = completion.finish(.failure(failure))
                    }
                    cleanup(allocated, stores: stores)
                }
                let timer = Task {
                    do {
                        try await sleep(limit)
                        if completion.finish(.failure(SettingsSaveFailure.preparationTimedOut)) {
                            worker.cancel()
                        }
                    } catch {
                        if !completion.isFinished {
                            _ = completion.finish(.failure(error))
                            worker.cancel()
                        }
                    }
                    completion.register(timer)
                }
            }
        } onCancel: {
            _ = completion.finish(.failure(SettingsSaveFailure.cancelled))
        }
    }

    /// Cleanup is not a UI/shutdown wait. Generated, unreferenced accounts are safe to retire if a native call is late.
    static func discard(
        _ prepared: SettingsPreparedCredentials,
        stores: [SettingsCredentialSlot: any SecretStore]
    ) {
        Task.detached(priority: .utility) {
            cleanup(prepared.allocated, stores: stores)
        }
    }

    private static func cleanup(
        _ allocated: [SettingsCredentialID], stores: [SettingsCredentialSlot: any SecretStore]
    ) {
        for id in allocated {
            try? stores[id.slot]?.removeSecret(for: id.account)
        }
    }
}

private final class SettingsCredentialPreparationCompletion: Sendable {
    private typealias Continuation = CheckedContinuation<SettingsPreparedCredentials, any Error>
    private typealias FinishDecision = (Bool, Continuation?, Task<Void, Never>?)

    private struct State {
        var continuation: CheckedContinuation<SettingsPreparedCredentials, any Error>?
        var result: Result<SettingsPreparedCredentials, any Error>?
        var finished = false
        var timer: Task<Void, Never>?
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    var isFinished: Bool { state.withLock { $0.finished } }

    func register(_ timer: Task<Void, Never>) {
        let finished = state.withLock {
            if $0.finished { return true }
            $0.timer = timer
            return false
        }
        if finished { timer.cancel() }
    }

    func install(_ continuation: CheckedContinuation<SettingsPreparedCredentials, any Error>) {
        let result = state.withLock {
            if $0.finished { return $0.result }
            $0.continuation = continuation
            return nil
        }
        if let result { continuation.resume(with: result) }
    }

    @discardableResult
    func finish(_ result: Result<SettingsPreparedCredentials, any Error>) -> Bool {
        let decision = state.withLock { state -> FinishDecision in
            guard !state.finished else { return (false, nil, nil) }
            state.finished = true
            state.result = result
            let continuation = state.continuation
            state.continuation = nil
            let timer = state.timer
            state.timer = nil
            return (true, continuation, timer)
        }
        decision.2?.cancel()
        decision.1?.resume(with: result)
        return decision.0
    }
}

/// Immutable committed references can be handed to the serving factory or an independent candidate factory.
struct SettingsReferencedSecretStore: SecretStore {
    let base: any SecretStore
    let slot: SettingsCredentialSlot
    let references: [String: SettingsCredentialReference]

    func secret(for account: String) throws -> String? {
        switch references[SettingsCredentialID(slot: slot, account: account).key] {
        case .absent?: return nil
        case .account(let selected)?: return try base.secret(for: selected)
        case nil: return try base.secret(for: account)
        }
    }

    func save(_ secret: String, for account: String) throws {
        throw SettingsSaveFailure.credentials
    }

    func removeSecret(for account: String) throws {
        throw SettingsSaveFailure.credentials
    }

    func renameAccount(_ account: String, to newAccount: String) throws -> SecretRename {
        throw SettingsSaveFailure.credentials
    }
}
