import Foundation
import Security

/// Thin wrapper over macOS Keychain Services (`generic password` items) for storing the Entra
/// service-principal client secret used by `MicrosoftFoundryCleanupProvider`.
///
/// Mirrors Windows' DPAPI-at-rest guarantee for `AppSettings.AiCleanupAzureClientSecret`: the
/// secret is never written to an environment variable, a `.env` file, or a script on disk (all of
/// which are plain unencrypted text and, for `AZURE_CLIENT_*` names specifically, would additionally
/// change how every other Azure tool on the machine resolves its own credentials). The Keychain
/// entry is scoped to this app's own service name, so it does not collide with, or get picked up by,
/// unrelated Azure tooling on the same machine.
enum KeychainStore {
    enum KeychainError: Error, LocalizedError {
        case unhandled(OSStatus)

        var errorDescription: String? {
            switch self {
            case .unhandled(let status):
                let message = SecCopyErrorMessageString(status, nil) as String? ?? "OSStatus \(status)"
                return "Keychain error: \(message)"
            }
        }
    }

    /// Writes (or overwrites) the secret for `account` under `service`. `SecItemDelete` before
    /// `SecItemAdd` rather than `SecItemUpdate`, so a changed access-control policy from a prior
    /// version can never resurrect a stale, differently-protected item.
    static func set(_ secret: String, service: String, account: String) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        SecItemDelete(query as CFDictionary)

        var addQuery = query
        addQuery[kSecValueData as String] = Data(secret.utf8)
        addQuery[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock

        let status = SecItemAdd(addQuery as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw KeychainError.unhandled(status)
        }
    }

    /// Returns the stored secret, or `nil` if nothing has been saved for `account` yet (a normal,
    /// expected state before the user has entered service-principal credentials, not an error).
    static func get(service: String, account: String) throws -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]

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

    /// Removes the stored secret, if any. Not treating "already absent" as an error keeps callers
    /// (e.g. a settings reset) simple.
    static func delete(service: String, account: String) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError.unhandled(status)
        }
    }
}
