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
    private let publication: SettingsCommitPublication
    private let externalPublication: SettingsExternalPublication
    let database: PersistenceStore

    init(
        defaults: UserDefaults,
        database: PersistenceStore,
        publication: @escaping SettingsCommitPublication = { try $0() },
        externalPublication: @escaping SettingsExternalPublication = { try $0() }
    ) {
        self.defaults = defaults
        self.database = database
        self.publication = publication
        self.externalPublication = externalPublication
    }

    func load() async throws -> SettingsDocument {
        let legacy = SettingsMigrationLedger.capture(defaults)
        let read = try await database.loadSettingsSession()
        if let greatest = read.stored?.savedThrough.values.max() {
            guard greatest < UInt64.max else { throw SettingsSaveFailure.storage }
            SettingsIntentRevision.observe(greatest)
        }
        return read.document(legacy: legacy)
    }

    func access(
        preparation: SettingsSavePreparation,
        apply: @escaping @MainActor (SettingsCommitReceipt) async -> SettingsApplicationOutcome
    ) -> SettingsSessionAccess {
        SettingsSessionAccess(
            validate: SettingsSessionValidation.isValid,
            prepare: preparation.prepare,
            commit: { [database, publication] in
                let receipt = try await database.commitSettingsSession($0, publication: publication)
                preparation.committed(receipt.id)
                return receipt
            },
            recover: { [database] in
                let receipt = try await database.loadSettingsReceipt($0)
                if let receipt { preparation.committed(receipt.id) }
                return receipt
            },
            apply: apply,
            discardPreparation: { preparation.discard($0) })
    }

    func access(
        prepare: @escaping @MainActor (SettingsSubmission) async throws -> SettingsSubmission = { $0 },
        apply: @escaping @MainActor (SettingsCommitReceipt) async -> SettingsApplicationOutcome,
        discardPreparation: @escaping @MainActor (UUID) async -> Void = { _ in }
    ) -> SettingsSessionAccess {
        SettingsSessionAccess(
            validate: SettingsSessionValidation.isValid,
            prepare: prepare,
            commit: { [database, publication] in
                try await database.commitSettingsSession($0, publication: publication)
            },
            recover: { [database] in try await database.loadSettingsReceipt($0) },
            apply: apply,
            discardPreparation: discardPreparation)
    }

    func updateExternal(
        _ setting: SettingsExternalSetting,
        values: [String: SettingsValue],
        revision: UInt64
    ) async throws -> SettingsDocument {
        let legacy = SettingsMigrationLedger.capture(defaults)
        let read = try await database.updateSettingsExternal(
            setting, values: values, revision: revision, legacy: legacy, publication: externalPublication)
        return read.document(legacy: legacy)
    }
}

enum SettingsSessionValidation {
    static func isValid(_ document: SettingsDocument) -> Bool {
        if let rows = document.dictionary {
            guard Set(rows.map(\.id)).count == rows.count else { return false }
            let invalid = rows.contains {
                $0.id <= 0 && $0.pattern.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            }
            if invalid {
                return false
            }
        }
        if let rows = document.snippets {
            guard Set(rows.map(\.id)).count == rows.count else { return false }
            let invalid = rows.contains {
                $0.id <= 0 && ($0.phrase.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || $0.template.isEmpty)
            }
            if invalid {
                return false
            }
        }
        if let rows = document.profiles {
            guard Set(rows.map(\.id)).count == rows.count else { return false }
            let invalid = rows.contains {
                $0.id <= 0
                    && ($0.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                        || ($0.bundleIdentifiers.isEmpty && $0.processNames.isEmpty))
            }
            if invalid {
                return false
            }
        }
        return true
    }
}
