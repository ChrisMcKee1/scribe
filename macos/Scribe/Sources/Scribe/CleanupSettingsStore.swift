import Foundation

/// User-facing identifier for which `CleanupProvider` Settings should build. Persisted as a plain
/// string (not a secret) in `CleanupSettingsStore`.
enum CleanupProviderKind: String, CaseIterable, Identifiable {
    case foundryLocal
    case ollama
    case openAICompatible
    case microsoftFoundry

    var id: String { rawValue }

    /// Matches the naming convention from PORTING-PLAN.md's "Provider naming in the macOS UI".
    var displayName: String {
        switch self {
        case .foundryLocal: return "Foundry Local (recommended)"
        case .ollama: return "Local model (Ollama managed)"
        case .openAICompatible: return "OpenAI-compatible endpoint"
        case .microsoftFoundry: return "Microsoft Foundry (cloud)"
        }
    }
}

/// Settings-window-backed configuration for AI cleanup, so a user can turn it on and pick/configure
/// a provider entirely from the GUI instead of setting environment variables before launch.
///
/// Non-secret fields are `UserDefaults`-backed, the same stopgap pattern already used for the
/// overlay anchor and tray quick-toggle preferences (see `AppDelegate`'s doc comments); a general
/// structured settings store doesn't exist yet on macOS. Secrets (the OpenAI-compatible API key and
/// the Azure service-principal client secret) are Keychain-backed via `KeychainStore` and are never
/// written to `UserDefaults`, a plist, or an environment variable, matching AGENTS.md's DPAPI-at-rest
/// guarantee for the equivalent Windows settings.
///
/// `SCRIBE_CLEANUP_PROVIDER` and its related environment variables remain fully supported and take
/// priority over these settings when present (see `CleanupProviderResolver`), so existing scripted
/// usage (`--cleanup-text`, the offline eval harness) keeps working unchanged; Settings only applies
/// once no such environment variable is set.
enum CleanupSettingsStore {
    private enum Key {
        // Shared with AppDelegate's tray "AI Cleanup" checkbox, which pre-dates this store, so the
        // tray toggle and the Settings tab always read and write the exact same flag.
        static let isEnabled = "ScribeAiCleanupEnabled"
        static let providerKind = "ScribeCleanupProviderKind"
        static let foundryLocalModelAlias = "ScribeCleanupFoundryLocalModelAlias"
        static let ollamaModel = "ScribeCleanupOllamaModel"
        static let openAIBaseURL = "ScribeCleanupOpenAIBaseURL"
        static let openAIModel = "ScribeCleanupOpenAIModel"
        static let azureEndpoint = "ScribeCleanupAzureEndpoint"
        static let azureDeployment = "ScribeCleanupAzureDeployment"
        static let azureAuthMode = "ScribeCleanupAzureAuthMode"
        static let azureTenantId = "ScribeCleanupAzureTenantId"
        static let azureClientId = "ScribeCleanupAzureClientId"
    }

    static let openAIApiKeyKeychainService = "com.scribe.macos.openai-compatible-api-key"
    private static let openAIApiKeyKeychainAccount = "default"

    /// Whether AI cleanup is turned on at all. Mirrors the tray "AI Cleanup" checkbox
    /// (`AppDelegate.isAiCleanupEnabled`); the two are kept in sync by `AppDelegate` reading this
    /// value at launch and writing back to it from the tray toggle, so enabling it from either the
    /// tray or Settings updates the other.
    static var isEnabled: Bool {
        get { UserDefaults.standard.bool(forKey: Key.isEnabled) }
        set { UserDefaults.standard.set(newValue, forKey: Key.isEnabled) }
    }

    static var providerKind: CleanupProviderKind {
        get { CleanupProviderKind(rawValue: UserDefaults.standard.string(forKey: Key.providerKind) ?? "") ?? .foundryLocal }
        set { UserDefaults.standard.set(newValue.rawValue, forKey: Key.providerKind) }
    }

    /// `qwen2.5-1.5b` matches `FoundryLocalCleanupProvider`'s own default (the benchmarked
    /// recommendation; see CLEANUP-MODEL-BENCHMARK.md).
    static var foundryLocalModelAlias: String {
        get { UserDefaults.standard.string(forKey: Key.foundryLocalModelAlias) ?? "qwen2.5-1.5b" }
        set { UserDefaults.standard.set(newValue, forKey: Key.foundryLocalModelAlias) }
    }

    /// `qwen2.5:3b` matches `ManagedOllamaCleanupProvider`'s own default (the best-scoring Ollama
    /// model in CLEANUP-MODEL-BENCHMARK.md).
    static var ollamaModel: String {
        get { UserDefaults.standard.string(forKey: Key.ollamaModel) ?? "qwen2.5:3b" }
        set { UserDefaults.standard.set(newValue, forKey: Key.ollamaModel) }
    }

    static var openAIBaseURL: String {
        get { UserDefaults.standard.string(forKey: Key.openAIBaseURL) ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: Key.openAIBaseURL) }
    }

    static var openAIModel: String {
        get { UserDefaults.standard.string(forKey: Key.openAIModel) ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: Key.openAIModel) }
    }

    static func openAIApiKey() -> String? {
        try? KeychainStore.get(service: openAIApiKeyKeychainService, account: openAIApiKeyKeychainAccount)
    }

    /// Saves (or, given `nil`/empty, clears) the OpenAI-compatible API key in Keychain.
    static func setOpenAIApiKey(_ key: String?) throws {
        if let key, !key.isEmpty {
            try KeychainStore.set(key, service: openAIApiKeyKeychainService, account: openAIApiKeyKeychainAccount)
        } else {
            try KeychainStore.delete(service: openAIApiKeyKeychainService, account: openAIApiKeyKeychainAccount)
        }
    }

    static var azureEndpoint: String {
        get { UserDefaults.standard.string(forKey: Key.azureEndpoint) ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: Key.azureEndpoint) }
    }

    static var azureDeployment: String {
        get { UserDefaults.standard.string(forKey: Key.azureDeployment) ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: Key.azureDeployment) }
    }

    static var azureAuthMode: AzureAuthMode {
        get { AzureAuthMode(rawValue: UserDefaults.standard.string(forKey: Key.azureAuthMode) ?? "") ?? .azureCli }
        set { UserDefaults.standard.set(newValue.rawValue, forKey: Key.azureAuthMode) }
    }

    static var azureTenantId: String {
        get { UserDefaults.standard.string(forKey: Key.azureTenantId) ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: Key.azureTenantId) }
    }

    static var azureClientId: String {
        get { UserDefaults.standard.string(forKey: Key.azureClientId) ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: Key.azureClientId) }
    }

    /// Keyed by client id (like `CleanupProviderResolver`'s CLI-set secret), so switching Entra app
    /// registrations in Settings never reads a stale secret left over from a previous client id.
    static func azureClientSecret(clientId: String) -> String? {
        guard !clientId.isEmpty else { return nil }
        return try? KeychainStore.get(service: CleanupProviderResolver.azureClientSecretKeychainService, account: clientId)
    }

    static func setAzureClientSecret(_ secret: String?, clientId: String) throws {
        guard !clientId.isEmpty else { return }
        if let secret, !secret.isEmpty {
            try KeychainStore.set(secret, service: CleanupProviderResolver.azureClientSecretKeychainService, account: clientId)
        } else {
            try KeychainStore.delete(service: CleanupProviderResolver.azureClientSecretKeychainService, account: clientId)
        }
    }

    /// Whether Settings has enough fields filled in to build a provider for `kind`, used to gate
    /// the Settings UI's "not configured yet" messaging before the user hits "Test Connection".
    static func isConfigured(for kind: CleanupProviderKind) -> Bool {
        switch kind {
        case .foundryLocal, .ollama:
            return true
        case .openAICompatible:
            return !openAIBaseURL.isEmpty && !openAIModel.isEmpty
        case .microsoftFoundry:
            return !azureEndpoint.isEmpty && !azureDeployment.isEmpty
        }
    }
}
