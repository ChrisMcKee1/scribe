import Foundation

struct SettingsStoredDocument: Codable, Equatable, Sendable {
    static let key = "settings.document.v1"
    var version = 1
    var preferences: SettingsPreferences
    var lastCommit: UUID?
    var revision: UInt64 = 0
    var savedThrough: [String: UInt64] = [:]
    var credentialReferences: [String: SettingsCredentialReference] = [:]
    var profileOrder: [Int64]?
}

struct SettingsStorageRead: Sendable {
    let stored: SettingsStoredDocument?
    let historyRetentionValue: String?
    let dictionary: [SettingsDictionaryRow]
    let snippets: [SettingsSnippetRow]
    let profiles: [SettingsProfileRow]

    func document(legacy: SettingsPreferences) -> SettingsDocument {
        SettingsDocument(
            preferences: stored?.preferences ?? legacy,
            historyRetentionValue: historyRetentionValue,
            dictionary: dictionary,
            snippets: snippets,
            profiles: profiles)
    }
}

/// A new canonical record preserves the old keys as a rollback profile; no secret bytes enter this record.
@MainActor
final class SettingsLegacyStore {
    private let defaults: UserDefaults
    let database: PersistenceStore

    init(defaults: UserDefaults, database: PersistenceStore) {
        self.defaults = defaults
        self.database = database
    }

    func load() async throws -> SettingsDocument {
        let legacy = SettingsMigrationLedger.capture(defaults)
        return try await database.loadSettingsSession().document(legacy: legacy)
    }

    func access(
        prepare: @escaping @MainActor (SettingsSubmission) async throws -> SettingsSubmission = { $0 },
        apply: @escaping @MainActor (SettingsCommitReceipt) async -> SettingsApplicationOutcome,
        discardPreparation: @escaping @MainActor (UUID) async -> Void = { _ in }
    ) -> SettingsSessionAccess {
        SettingsSessionAccess(
            validate: SettingsSessionValidation.isValid,
            prepare: prepare,
            commit: { [database] in try await database.commitSettingsSession($0) },
            apply: apply,
            discardPreparation: discardPreparation)
    }

    func updateExternal(
        _ setting: SettingsExternalSetting,
        values: [String: SettingsValue],
        revision: UInt64
    ) async throws -> SettingsDocument {
        let legacy = SettingsMigrationLedger.capture(defaults)
        return try await database.updateSettingsExternal(
            setting, values: values, revision: revision, legacy: legacy).document(legacy: legacy)
    }
}

enum SettingsSessionValidation {
    static func isValid(_ document: SettingsDocument) -> Bool {
        if let rows = document.dictionary {
            guard Set(rows.map(\.id)).count == rows.count else { return false }
            guard rows.allSatisfy({ !$0.pattern.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) else {
                return false
            }
        }
        if let rows = document.snippets {
            guard Set(rows.map(\.id)).count == rows.count else { return false }
            guard rows.allSatisfy({
                !$0.phrase.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !$0.template.isEmpty
            }) else {
                return false
            }
        }
        if let rows = document.profiles {
            guard Set(rows.map(\.id)).count == rows.count else { return false }
            guard rows.allSatisfy({
                !$0.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    && (!$0.bundleIdentifiers.isEmpty || !$0.processNames.isEmpty)
            }) else {
                return false
            }
        }
        return true
    }
}
