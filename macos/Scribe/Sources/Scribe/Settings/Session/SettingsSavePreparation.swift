import Foundation

/// Composes bounded credential preparation with a word-pack participant before the one durable commit.
@MainActor
final class SettingsSavePreparation {
    private let database: PersistenceStore
    private let stores: [SettingsCredentialSlot: any SecretStore]
    private let participant: @MainActor (SettingsSubmission) async throws -> SettingsSubmission
    private var prepared: [UUID: SettingsPreparedCredentials] = [:]

    init(
        database: PersistenceStore,
        stores: [SettingsCredentialSlot: any SecretStore],
        participant: @escaping @MainActor (SettingsSubmission) async throws -> SettingsSubmission = { $0 }
    ) {
        self.database = database
        self.stores = stores
        self.participant = participant
    }

    func prepare(_ original: SettingsSubmission) async throws -> SettingsSubmission {
        var submission = original
        if !submission.credentials.isEmpty {
            let previous = try await database.loadStringSetting(key: SettingsCredentialReference.storageKey)
            let references = try previous.map {
                try JSONDecoder().decode([String: SettingsCredentialReference].self, from: Data($0.utf8))
            } ?? [:]
            let credentials = try await SettingsCredentialPreparer.prepare(
                edits: submission.credentials, existing: references, stores: stores)
            prepared[submission.id] = credentials
            try credentials.attach(to: &submission, previous: previous)
        }
        return try await participant(submission)
    }

    func discard(_ id: UUID) {
        if let credentials = prepared.removeValue(forKey: id) {
            SettingsCredentialPreparer.discard(credentials, stores: stores)
        }
    }

    func committed(_ id: UUID) {
        prepared[id] = nil
    }
}
