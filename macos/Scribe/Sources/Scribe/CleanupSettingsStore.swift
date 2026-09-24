import Foundation

/// User-facing identifier for which `CleanupProvider` Settings should build. Persisted as a plain string (not a
/// secret) in `CleanupSettingsStore`.
enum CleanupProviderKind: String, CaseIterable, Identifiable, Sendable {
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

    /// What the provider of this kind calls itself (`CleanupProvider.displayName`), for a message about a provider
    /// that was not built.
    var providerName: String {
        switch self {
        case .foundryLocal: return "Foundry Local"
        case .ollama: return "Ollama"
        case .openAICompatible: return "OpenAI-compatible endpoint"
        case .microsoftFoundry: return "Microsoft Foundry"
        }
    }
}

/// The AI cleanup settings as stored at one moment. Secrets are not part of it: `secretRevision` changes whenever one
/// is saved or removed, which is all a provider cache needs to know about them.
struct CleanupSettingsSnapshot: Sendable, Equatable {
    var isEnabled: Bool
    var providerKind: CleanupProviderKind
    var foundryLocalModelAlias: String
    var ollamaModel: String
    var openAIBaseURL: String
    var openAIModel: String
    var azureEndpoint: String
    var azureDeployment: String
    var azureAuthMode: AzureAuthMode
    var azureTenantId: String
    var azureClientId: String
    var secretRevision: String
}

/// Settings-window-backed configuration for AI cleanup, so a user can turn it on and pick and configure a provider
/// entirely from the GUI instead of setting environment variables before launch.
///
/// Non-secret fields live in `UserDefaults`, under the keys the tray's AI Cleanup item and the AI Cleanup tab share.
/// Secrets (the OpenAI-compatible API key and the service principal's client secret) live in a `SecretStore`, the
/// Keychain in production, and are never written to `UserDefaults`, a plist or an environment variable.
///
/// All of its storage is injected. A test builds a store over a defaults suite and secret stores of its own, so it
/// never reads or replaces the developer's settings or credentials, and suites run in parallel without sharing
/// anything; `live` is the production store. The defaults are named by `Domain` rather than held, because
/// `UserDefaults` is not `Sendable` and this store travels with the provider cache, which any task may use.
///
/// `SCRIBE_CLEANUP_PROVIDER` and its related environment variables take priority over these settings when present
/// (see `CleanupProviderResolver`), so scripted use (`--cleanup-text`, the offline eval harness) keeps working.
struct CleanupSettingsStore: Sendable {
    /// Which defaults the non-secret fields live in.
    enum Domain: Sendable, Equatable {
        /// `UserDefaults.standard`, the app's own preferences.
        case standard
        /// `UserDefaults(suiteName:)`, for tests.
        case suite(String)
    }

    private enum Key {
        // Shared with AppDelegate's tray "AI Cleanup" checkbox, which pre-dates this store, so the tray toggle and the
        // Settings tab always read and write the exact same flag.
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
        static let secretRevision = "ScribeCleanupSecretRevision"
    }

    static let openAIApiKeyKeychainService = "com.scribe.macos.openai-compatible-api-key"
    /// The account is the trimmed client id, so switching Entra app registrations never reads a stale secret.
    static let azureClientSecretKeychainService = "com.scribe.macos.azure-client-secret"
    /// One OpenAI-compatible key per Mac, under one fixed account.
    static let openAIApiKeyAccount = "default"
    /// The benchmarked recommendations (see CLEANUP-MODEL-BENCHMARK.md), shared with the providers' own defaults.
    static let defaultFoundryLocalModelAlias = "qwen2.5-1.5b"
    static let defaultOllamaModel = "qwen2.5:3b"

    /// The app's own store: `UserDefaults.standard` and the production Keychain services. No test uses it.
    static var live: CleanupSettingsStore {
        CleanupSettingsStore(
            domain: .standard,
            apiKeys: KeychainSecretStore(service: openAIApiKeyKeychainService),
            clientSecrets: KeychainSecretStore(service: azureClientSecretKeychainService))
    }

    let domain: Domain
    /// Holds the OpenAI-compatible API key, under `openAIApiKeyAccount`.
    let apiKeys: any SecretStore
    /// Holds each service principal's client secret, under its trimmed client id.
    let clientSecrets: any SecretStore

    init(domain: Domain, apiKeys: any SecretStore, clientSecrets: any SecretStore) {
        self.domain = domain
        self.apiKeys = apiKeys
        self.clientSecrets = clientSecrets
    }

    private var defaults: UserDefaults {
        switch domain {
        case .standard:
            return .standard
        case .suite(let name):
            guard let suite = UserDefaults(suiteName: name) else {
                preconditionFailure("UserDefaults rejected a test suite name")
            }
            return suite
        }
    }

    // MARK: - Settings

    /// Whether AI cleanup is turned on at all. Mirrors the tray "AI Cleanup" checkbox
    /// (`AppDelegate.isAiCleanupEnabled`), which reads and writes the same key, so enabling it from either the tray or
    /// Settings updates the other.
    var isEnabled: Bool {
        get { defaults.bool(forKey: Key.isEnabled) }
        nonmutating set { defaults.set(newValue, forKey: Key.isEnabled) }
    }

    var providerKind: CleanupProviderKind {
        get { Self.providerKind(in: defaults) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: Key.providerKind) }
    }

    var foundryLocalModelAlias: String {
        get { defaults.string(forKey: Key.foundryLocalModelAlias) ?? Self.defaultFoundryLocalModelAlias }
        nonmutating set { defaults.set(newValue, forKey: Key.foundryLocalModelAlias) }
    }

    var ollamaModel: String {
        get { defaults.string(forKey: Key.ollamaModel) ?? Self.defaultOllamaModel }
        nonmutating set { defaults.set(newValue, forKey: Key.ollamaModel) }
    }

    var openAIBaseURL: String {
        get { defaults.string(forKey: Key.openAIBaseURL) ?? "" }
        nonmutating set { defaults.set(newValue, forKey: Key.openAIBaseURL) }
    }

    var openAIModel: String {
        get { defaults.string(forKey: Key.openAIModel) ?? "" }
        nonmutating set { defaults.set(newValue, forKey: Key.openAIModel) }
    }

    var azureEndpoint: String {
        get { defaults.string(forKey: Key.azureEndpoint) ?? "" }
        nonmutating set { defaults.set(newValue, forKey: Key.azureEndpoint) }
    }

    var azureDeployment: String {
        get { defaults.string(forKey: Key.azureDeployment) ?? "" }
        nonmutating set { defaults.set(newValue, forKey: Key.azureDeployment) }
    }

    var azureAuthMode: AzureAuthMode {
        get { Self.azureAuthMode(in: defaults) }
        nonmutating set { defaults.set(newValue.rawValue, forKey: Key.azureAuthMode) }
    }

    var azureTenantId: String {
        get { defaults.string(forKey: Key.azureTenantId) ?? "" }
        nonmutating set { defaults.set(newValue, forKey: Key.azureTenantId) }
    }

    var azureClientId: String {
        get { defaults.string(forKey: Key.azureClientId) ?? "" }
        nonmutating set { defaults.set(newValue, forKey: Key.azureClientId) }
    }

    /// Changes whenever a secret is saved or removed through this store. A provider built with a secret is keyed by
    /// it, so the first request after a new key or client secret is saved builds a new provider, while the secret
    /// itself never leaves the secret store to become part of a key. A save made by another Scribe process (the
    /// `--set-azure-client-secret` verb) changes it too, since both share the defaults.
    var secretRevision: String {
        defaults.string(forKey: Key.secretRevision) ?? ""
    }

    /// Everything above, read from one defaults instance.
    func snapshot() -> CleanupSettingsSnapshot {
        let defaults = self.defaults
        return CleanupSettingsSnapshot(
            isEnabled: defaults.bool(forKey: Key.isEnabled),
            providerKind: Self.providerKind(in: defaults),
            foundryLocalModelAlias: defaults.string(forKey: Key.foundryLocalModelAlias)
                ?? Self.defaultFoundryLocalModelAlias,
            ollamaModel: defaults.string(forKey: Key.ollamaModel) ?? Self.defaultOllamaModel,
            openAIBaseURL: defaults.string(forKey: Key.openAIBaseURL) ?? "",
            openAIModel: defaults.string(forKey: Key.openAIModel) ?? "",
            azureEndpoint: defaults.string(forKey: Key.azureEndpoint) ?? "",
            azureDeployment: defaults.string(forKey: Key.azureDeployment) ?? "",
            azureAuthMode: Self.azureAuthMode(in: defaults),
            azureTenantId: defaults.string(forKey: Key.azureTenantId) ?? "",
            azureClientId: defaults.string(forKey: Key.azureClientId) ?? "",
            secretRevision: defaults.string(forKey: Key.secretRevision) ?? "")
    }

    /// Whether Settings has enough fields filled in to build a provider for `kind`, which gates the Settings UI's
    /// "Test Connection" button.
    func isConfigured(for kind: CleanupProviderKind) -> Bool {
        switch kind {
        case .foundryLocal, .ollama:
            return true
        case .openAICompatible:
            return !Self.isBlank(openAIBaseURL) && !Self.isBlank(openAIModel)
        case .microsoftFoundry:
            return !Self.isBlank(azureEndpoint) && !Self.isBlank(azureDeployment)
        }
    }

    // MARK: - Secrets

    /// The saved OpenAI-compatible API key. Throws when the secret store cannot be read, so a caller can tell a locked
    /// Keychain from a key that was never saved.
    func readOpenAIApiKey() throws -> String? {
        try apiKeys.secret(for: Self.openAIApiKeyAccount)
    }

    /// The saved key for the Settings window, where a Keychain that cannot be read shows as no key.
    func openAIApiKey() -> String? {
        try? readOpenAIApiKey()
    }

    /// Saves the OpenAI-compatible API key, or removes it when given `nil` or an empty string.
    func setOpenAIApiKey(_ key: String?) throws {
        if let key, !key.isEmpty {
            try apiKeys.save(key, for: Self.openAIApiKeyAccount)
        } else {
            try apiKeys.removeSecret(for: Self.openAIApiKeyAccount)
        }
        secretsChanged()
    }

    /// The client secret saved for `clientId`, or `nil` for a blank client id. Throws when the secret store cannot be
    /// read.
    ///
    /// `clientId` is the id as configured, surrounding whitespace included. Builds before this one saved a secret under
    /// exactly that text, and a Keychain item matches only its own account, so when nothing is saved under the trimmed
    /// id, the secret is looked for under that one earlier account (`legacySecretAccount(forClientId:)`) and moved to
    /// the trimmed one. No other account is ever read.
    func readAzureClientSecret(clientId: String) throws -> String? {
        let account = Self.secretAccount(forClientId: clientId)
        guard !account.isEmpty else { return nil }
        if let secret = try clientSecrets.secret(for: account) {
            return secret
        }
        guard let legacy = Self.legacySecretAccount(forClientId: clientId),
            let secret = try clientSecrets.secret(for: legacy)
        else {
            return nil
        }
        // Saved under the trimmed id before the earlier item goes, so a failure at either step still leaves a copy for
        // the next read, and neither keeps the secret from being used now. The secret itself is unchanged, so the
        // revision stays, and a provider built with it is kept.
        do {
            try clientSecrets.save(secret, for: account)
        } catch {
            ScribeLog.warning(.cleanup, "Could not move a client secret to its trimmed client id", .failure(error))
            return secret
        }
        removeLegacyClientSecret(legacy)
        ScribeLog.info(.cleanup, "Moved a client secret to its trimmed client id")
        return secret
    }

    /// The saved client secret for the Settings window, where a Keychain that cannot be read shows as no secret.
    func azureClientSecret(clientId: String) -> String? {
        try? readAzureClientSecret(clientId: clientId)
    }

    /// Saves the client secret for `clientId`, or removes it when given `nil` or an empty string. A blank client id
    /// stores nothing, so no item is ever keyed by an empty account shared by every unconfigured install.
    ///
    /// Either way, the item an earlier build saved under the id exactly as configured goes too: left behind, it would
    /// come back on the next read after a Clear, and linger beside the new secret after a Save.
    func setAzureClientSecret(_ secret: String?, clientId: String) throws {
        let account = Self.secretAccount(forClientId: clientId)
        guard !account.isEmpty else { return }
        let legacy = Self.legacySecretAccount(forClientId: clientId)
        if let secret, !secret.isEmpty {
            try clientSecrets.save(secret, for: account)
            secretsChanged()
            if let legacy {
                removeLegacyClientSecret(legacy)
            }
            return
        }
        if let legacy {
            // First, so a Clear that cannot remove it fails having changed nothing. Left behind once the trimmed
            // item is gone, it would be moved back by the next read.
            try clientSecrets.removeSecret(for: legacy)
        }
        try clientSecrets.removeSecret(for: account)
        secretsChanged()
    }

    /// The account a client secret is saved under: the client id without surrounding whitespace, so a client id pasted
    /// with a stray space still finds its own secret.
    static func secretAccount(forClientId clientId: String) -> String {
        clientId.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// The account builds before the trimmed one saved `clientId`'s secret under: the id exactly as configured. `nil`
    /// when that is the trimmed account already, so a client id without surrounding whitespace reads one account only.
    static func legacySecretAccount(forClientId clientId: String) -> String? {
        let account = secretAccount(forClientId: clientId)
        return account.isEmpty || account == clientId ? nil : clientId
    }

    /// Removes the earlier item once the trimmed account holds the secret. Failing only leaves it where no read looks
    /// again, since the trimmed account is read first, so it is logged, not thrown.
    private func removeLegacyClientSecret(_ legacy: String) {
        do {
            try clientSecrets.removeSecret(for: legacy)
        } catch {
            ScribeLog.warning(
                .cleanup, "Could not remove a client secret saved under an untrimmed client id", .failure(error))
        }
    }

    // MARK: - Helpers

    private func secretsChanged() {
        defaults.set(UUID().uuidString, forKey: Key.secretRevision)
    }

    private static func providerKind(in defaults: UserDefaults) -> CleanupProviderKind {
        CleanupProviderKind(rawValue: defaults.string(forKey: Key.providerKind) ?? "") ?? .foundryLocal
    }

    private static func azureAuthMode(in defaults: UserDefaults) -> AzureAuthMode {
        AzureAuthMode(rawValue: defaults.string(forKey: Key.azureAuthMode) ?? "") ?? .azureCli
    }

    private static func isBlank(_ value: String) -> Bool {
        value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}
