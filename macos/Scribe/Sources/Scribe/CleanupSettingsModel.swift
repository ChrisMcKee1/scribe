import Foundation

/// The AI cleanup settings the AI Cleanup tab shows, as one value. Secrets are not part of it: they live in
/// Keychain and are only written by an explicit Save.
struct CleanupSettingsValues: Equatable {
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
}

/// What "Test Connection" found, in words for the tab.
struct CleanupConnectionCheck: Equatable, Sendable {
    let reachable: Bool
    let message: String
}

/// How the AI Cleanup tab reaches stored settings, Keychain and the provider. `live` goes through
/// `CleanupSettingsStore` and `CleanupProviderResolver`, the same store and resolver the tray and the dictation
/// pipeline use, so the tab can never describe a configuration they would not run; tests pass their own.
struct CleanupSettingsAccess {
    var load: @MainActor () -> CleanupSettingsValues
    /// Stores the fields that differ between `new` and `old`.
    var save: @MainActor (_ new: CleanupSettingsValues, _ old: CleanupSettingsValues) -> Void
    var isConfigured: @MainActor (CleanupProviderKind) -> Bool
    var hasOpenAIApiKey: @MainActor () -> Bool
    var setOpenAIApiKey: @MainActor (String?) throws -> Void
    var hasAzureClientSecret: @MainActor (_ clientId: String) -> Bool
    var setAzureClientSecret: @MainActor (_ secret: String?, _ clientId: String) throws -> Void
    /// Resolves the provider the pipeline would use and runs its health check. Runs off the main actor.
    var checkConnection: @Sendable () async -> CleanupConnectionCheck
}

extension CleanupSettingsAccess {
    static var live: CleanupSettingsAccess {
        CleanupSettingsAccess(
            load: {
                CleanupSettingsValues(
                    isEnabled: CleanupSettingsStore.isEnabled,
                    providerKind: CleanupSettingsStore.providerKind,
                    foundryLocalModelAlias: CleanupSettingsStore.foundryLocalModelAlias,
                    ollamaModel: CleanupSettingsStore.ollamaModel,
                    openAIBaseURL: CleanupSettingsStore.openAIBaseURL,
                    openAIModel: CleanupSettingsStore.openAIModel,
                    azureEndpoint: CleanupSettingsStore.azureEndpoint,
                    azureDeployment: CleanupSettingsStore.azureDeployment,
                    azureAuthMode: CleanupSettingsStore.azureAuthMode,
                    azureTenantId: CleanupSettingsStore.azureTenantId,
                    azureClientId: CleanupSettingsStore.azureClientId)
            },
            save: { new, old in
                if new.isEnabled != old.isEnabled { CleanupSettingsStore.isEnabled = new.isEnabled }
                if new.providerKind != old.providerKind { CleanupSettingsStore.providerKind = new.providerKind }
                if new.foundryLocalModelAlias != old.foundryLocalModelAlias {
                    CleanupSettingsStore.foundryLocalModelAlias = new.foundryLocalModelAlias
                }
                if new.ollamaModel != old.ollamaModel { CleanupSettingsStore.ollamaModel = new.ollamaModel }
                if new.openAIBaseURL != old.openAIBaseURL { CleanupSettingsStore.openAIBaseURL = new.openAIBaseURL }
                if new.openAIModel != old.openAIModel { CleanupSettingsStore.openAIModel = new.openAIModel }
                if new.azureEndpoint != old.azureEndpoint { CleanupSettingsStore.azureEndpoint = new.azureEndpoint }
                if new.azureDeployment != old.azureDeployment {
                    CleanupSettingsStore.azureDeployment = new.azureDeployment
                }
                if new.azureAuthMode != old.azureAuthMode { CleanupSettingsStore.azureAuthMode = new.azureAuthMode }
                if new.azureTenantId != old.azureTenantId { CleanupSettingsStore.azureTenantId = new.azureTenantId }
                if new.azureClientId != old.azureClientId { CleanupSettingsStore.azureClientId = new.azureClientId }
            },
            isConfigured: { CleanupSettingsStore.isConfigured(for: $0) },
            hasOpenAIApiKey: { CleanupSettingsStore.openAIApiKey() != nil },
            setOpenAIApiKey: { try CleanupSettingsStore.setOpenAIApiKey($0) },
            hasAzureClientSecret: { CleanupSettingsStore.azureClientSecret(clientId: $0) != nil },
            setAzureClientSecret: { try CleanupSettingsStore.setAzureClientSecret($0, clientId: $1) },
            checkConnection: {
                do {
                    let provider = try CleanupProviderResolver.tryResolveDefaultProvider()
                    let snapshot = await provider.healthSnapshot()
                    return CleanupConnectionCheck(
                        reachable: snapshot.reachable,
                        message: "\(provider.displayName): \(snapshot.detail)")
                } catch {
                    return CleanupConnectionCheck(reachable: false, message: error.localizedDescription)
                }
            })
    }
}

/// The AI Cleanup tab. Every field is stored the moment it changes, and the tab re-reads storage after any
/// preference write elsewhere in the process (the tray's AI Cleanup item above all), so the switch and the fields
/// always show what the pipeline will use. Only the provider controls depend on the switch, so cleanup can always
/// be turned on from here. What is typed into the two secret fields lives in `SettingsDrafts`, so it survives the
/// window closing until it is saved or cleared.
@MainActor
final class CleanupSettingsModel: ObservableObject {
    enum Control {
        case enableSwitch
        case provider
        case providerDetails
        case connectionTest
    }

    @Published var values: CleanupSettingsValues {
        didSet { store(changesFrom: oldValue) }
    }
    @Published private(set) var hasSavedOpenAIApiKey = false
    @Published private(set) var hasSavedAzureClientSecret = false
    @Published private(set) var isTesting = false
    @Published private(set) var statusMessage: String?
    @Published private(set) var errorMessage: String?

    let drafts: SettingsDrafts
    private let access: CleanupSettingsAccess
    private var isReloading = false
    private var isSaving = false
    /// Advances on every change to `values` and every stored credential change, so a connection test can tell its
    /// result is for a configuration the tab no longer shows.
    private var revision = 0
    private var observation: SettingsNotificationObservation?

    init(access: CleanupSettingsAccess, drafts: SettingsDrafts, center: NotificationCenter = .default) {
        self.access = access
        self.drafts = drafts
        values = access.load()
        observation = SettingsNotificationObservation(UserDefaults.didChangeNotification, center: center) {
            [weak self] in
            self?.reload()
        }
    }

    /// Whether a control is disabled now. The switch never is: the provider controls depend on it, it does not.
    func isDisabled(_ control: Control) -> Bool {
        switch control {
        case .enableSwitch:
            return false
        case .provider, .providerDetails:
            return !values.isEnabled
        case .connectionTest:
            return !values.isEnabled || isTesting || !access.isConfigured(values.providerKind)
        }
    }

    var canSaveOpenAIApiKey: Bool {
        !drafts.openAIApiKey.isEmpty
    }

    var canSaveAzureClientSecret: Bool {
        !drafts.azureClientSecret.isEmpty && !values.azureClientId.trimmingCharacters(in: .whitespaces).isEmpty
    }

    /// Re-reads stored settings. What is typed into a secret field is kept: it is not stored until Save.
    func reload() {
        // A write of the tab's own reaches here from inside `save`; storage already holds what the tab shows.
        guard !isSaving else { return }
        let stored = access.load()
        guard stored != values else { return }
        isReloading = true
        values = stored
        isReloading = false
    }

    /// Re-reads which secrets Keychain holds. Done when the tab appears and after a change that affects it, never
    /// for every preference write, so an open tab does not read Keychain each time anything is stored.
    func refreshSecretState() {
        hasSavedOpenAIApiKey = access.hasOpenAIApiKey()
        hasSavedAzureClientSecret = access.hasAzureClientSecret(values.azureClientId)
    }

    func saveOpenAIApiKey() {
        do {
            try access.setOpenAIApiKey(drafts.openAIApiKey)
            drafts.openAIApiKey = ""
            hasSavedOpenAIApiKey = true
            credentialsChanged()
            show(status: "API key saved to Keychain.")
        } catch {
            show(error: "Failed to save API key: \(error.localizedDescription)")
        }
    }

    func clearOpenAIApiKey() {
        do {
            try access.setOpenAIApiKey(nil)
            hasSavedOpenAIApiKey = false
            credentialsChanged()
            show(status: "API key removed.")
        } catch {
            show(error: "Failed to remove API key: \(error.localizedDescription)")
        }
    }

    func saveAzureClientSecret() {
        do {
            try access.setAzureClientSecret(drafts.azureClientSecret, values.azureClientId)
            drafts.azureClientSecret = ""
            hasSavedAzureClientSecret = true
            credentialsChanged()
            show(status: "Client secret saved to Keychain.")
        } catch {
            show(error: "Failed to save client secret: \(error.localizedDescription)")
        }
    }

    func clearAzureClientSecret() {
        do {
            try access.setAzureClientSecret(nil, values.azureClientId)
            hasSavedAzureClientSecret = false
            credentialsChanged()
            show(status: "Client secret removed.")
        } catch {
            show(error: "Failed to remove client secret: \(error.localizedDescription)")
        }
    }

    /// Resolves the provider exactly as the pipeline would, environment overrides included, and runs its health
    /// check. A result that arrives after the settings or a stored credential changed is dropped rather than shown
    /// against them.
    func testConnection() async {
        guard !isDisabled(.connectionTest) else { return }
        let started = revision
        isTesting = true
        statusMessage = nil
        errorMessage = nil
        let result = await access.checkConnection()
        isTesting = false
        guard started == revision else { return }
        if result.reachable {
            statusMessage = result.message
        } else {
            errorMessage = result.message
        }
    }

    /// A stored key or secret changed: a connection test still running checked the credential that was replaced.
    private func credentialsChanged() {
        revision += 1
    }

    private func store(changesFrom old: CleanupSettingsValues) {
        guard values != old else { return }
        revision += 1
        if !isReloading {
            isSaving = true
            access.save(values, old)
            isSaving = false
        }
        if values.azureClientId != old.azureClientId {
            hasSavedAzureClientSecret = access.hasAzureClientSecret(values.azureClientId)
        }
    }

    private func show(status: String) {
        statusMessage = status
        errorMessage = nil
    }

    private func show(error: String) {
        errorMessage = error
        statusMessage = nil
    }
}