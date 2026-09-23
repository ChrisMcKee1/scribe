import Foundation
import Security

/// Where Scribe keeps one kind of secret, the OpenAI-compatible API key or a service principal's client secret, keyed
/// by account. Production uses `KeychainSecretStore`; tests use an in-memory store, or a Keychain store under a service
/// of their own, so a test never reads, replaces or deletes a real credential.
///
/// Secrets live nowhere else: never in `UserDefaults`, a plist, an environment variable, a `.env` file or a script,
/// which mirrors Windows' DPAPI-at-rest guarantee. For `AZURE_CLIENT_*` names in particular, an environment variable
/// would also change how every other Azure tool on the Mac finds its credentials.
protocol SecretStore: Sendable {
    /// The secret saved for `account`, or `nil` when none is saved.
    func secret(for account: String) throws -> String?
    /// Saves `secret` for `account`, replacing a saved one.
    func save(_ secret: String, for account: String) throws
    /// Removes the secret saved for `account`. Removing a secret that is not there is not an error.
    func removeSecret(for account: String) throws
}

/// Generic-password items under one Keychain service.
struct KeychainSecretStore: SecretStore {
    let service: String

    func secret(for account: String) throws -> String? {
        try KeychainStore.get(service: service, account: account)
    }

    func save(_ secret: String, for account: String) throws {
        try KeychainStore.set(secret, service: service, account: account)
    }

    func removeSecret(for account: String) throws {
        try KeychainStore.delete(service: service, account: account)
    }
}

/// Keychain Services calls for generic-password items, the layer under `KeychainSecretStore`.
enum KeychainStore {
    enum KeychainError: Error, LocalizedError, Equatable {
        case unhandled(OSStatus)

        var errorDescription: String? {
            switch self {
            case .unhandled(let status):
                // The system's own words for a status: they name the failure, never anything Scribe stored.
                let message = SecCopyErrorMessageString(status, nil) as String? ?? "OSStatus \(status)"
                return "Keychain error: \(message)"
            }
        }
    }

    /// Saves `secret` for `account` under `service`: adds the item, and when one already exists, updates its data in
    /// place.
    ///
    /// Updating instead of deleting first means a reader never finds the item missing halfway through a save, and two
    /// writers never race a delete against an add, which made the loser fail with `errSecDuplicateItem`. An updated
    /// item keeps the access control it was created with, so changing the protection class later needs a migration of
    /// its own.
    static func set(_ secret: String, service: String, account: String) throws {
        let item = itemQuery(service: service, account: account)
        let data = Data(secret.utf8)
        // Bounded: going round again only happens when another process removes the item between the add and the update.
        for _ in 0..<3 {
            var attributes = item
            attributes[kSecValueData as String] = data
            attributes[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            let added = SecItemAdd(attributes as CFDictionary, nil)
            if added == errSecSuccess {
                return
            }
            guard added == errSecDuplicateItem else {
                throw KeychainError.unhandled(added)
            }

            let changes: [String: Any] = [kSecValueData as String: data]
            let updated = SecItemUpdate(item as CFDictionary, changes as CFDictionary)
            if updated == errSecSuccess {
                return
            }
            guard updated == errSecItemNotFound else {
                throw KeychainError.unhandled(updated)
            }
        }
        throw KeychainError.unhandled(errSecDuplicateItem)
    }

    /// Returns the stored secret, or `nil` if nothing has been saved for `account` yet (a normal, expected state before
    /// the user has entered credentials, not an error).
    static func get(service: String, account: String) throws -> String? {
        var query = itemQuery(service: service, account: account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound {
            return nil
        }
        guard status == errSecSuccess, let data = result as? Data else {
            throw KeychainError.unhandled(status)
        }
        return String(data: data, encoding: .utf8)
    }

    /// Removes the stored secret, if any. "Already absent" is not an error, which keeps callers such as Clear simple.
    static func delete(service: String, account: String) throws {
        let status = SecItemDelete(itemQuery(service: service, account: account) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError.unhandled(status)
        }
    }

    private static func itemQuery(service: String, account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }
}
