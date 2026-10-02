import Foundation

enum AzureAuthenticationSelection: String, CaseIterable, Identifiable, Sendable {
    case azureCli
    case servicePrincipal
    case apiKey

    var id: String { rawValue }

    var label: String {
        switch self {
        case .azureCli: return "Azure CLI (az login)"
        case .servicePrincipal: return "Service principal"
        case .apiKey: return "API key"
        }
    }
}

/// The AI cleanup settings the AI Cleanup tab shows, as one value. Secrets are not part of it: they live in
/// Keychain and are only written by an explicit Save.
struct CleanupSettingsValues: Equatable, Sendable {
    var isEnabled: Bool
    var providerKind: CleanupProviderKind
    var foundryLocalModelAlias: String
    var ollamaModel: String
    var lmStudioModel: String
    var selectedLocalApp: LocalServerApp
    var openAIBaseURL: String
    var openAIModel: String
    var openAIApiStyle: CustomAPIStyle
    var ollamaContextTokens: Int
    var lmStudioContextTokens: Int
    var foundryLocalSendWholeVocabulary: Bool
    var ollamaSendWholeVocabulary: Bool
    var lmStudioSendWholeVocabulary: Bool
    var otherServiceBaseURL: String
    var otherServiceModel: String
    var otherServiceApiStyle: CustomAPIStyle
    var azureEndpoint: String
    var azureDeployment: String
    var azurePromptCaching = true
    var azureAuthMode: AzureAuthMode
    var azureTenantId: String
    var azureClientId: String
    var azureApiKeySelected = false
    var writingStyle = ""
    var frontierPrompt = ""
    var localPrompt = ""
    var localModelIdleMinutes = LocalModelDefaults.keepAliveMinutes
}

/// The full unsaved configuration tested by the button. Secret values stay in memory only and are redacted if the
/// candidate is inspected for debugging.
struct CleanupConnectionCandidate: Sendable, Equatable, CustomStringConvertible, CustomReflectable {
    let settings: CleanupSettingsValues
    let openAIApiKey: String?
    let azureClientSecret: String?
    let azureApiKey: String?
    let writingStyle: String
    let frontierPrompt: String
    let localPrompt: String

    var description: String { "CleanupConnectionCandidate" }
    var customMirror: Mirror { Mirror(self, children: ["provider": settings.providerKind]) }
}

/// What "Test Connection" found, in words for the tab.
struct CleanupConnectionCheck: Equatable, Sendable {
    let reachable: Bool
    let message: String
}

/// How the AI Cleanup tab reaches stored settings, Keychain and the provider. `live` goes through
/// `CleanupSettingsStore.live` and `CleanupProviderCache.shared`, the same store and provider the tray and the
/// dictation pipeline use, so the tab can never describe a configuration they would not run; tests pass their own.
struct CleanupSettingsAccess {
    var load: @MainActor () -> CleanupSettingsValues
    /// Stores the fields that differ between `new` and `old`.
    var save: @MainActor (_ new: CleanupSettingsValues, _ old: CleanupSettingsValues) -> Void
    var isConfigured: @MainActor (CleanupProviderKind) -> Bool
    var hasOpenAIApiKey: @MainActor () -> Bool
    var setOpenAIApiKey: @MainActor (String?) throws -> Void
    var hasAzureClientSecret: @MainActor (_ clientId: String) -> Bool
    var setAzureClientSecret: @MainActor (_ secret: String?, _ clientId: String) throws -> Void
    var hasAzureApiKey: @MainActor () -> Bool = { false }
    var setAzureApiKey: (@MainActor (String?) throws -> Void)? = nil
    /// Runs Test Connection through the provider the pipeline would use. Runs off the main actor.
    var checkConnection: @Sendable () async -> CleanupConnectionCheck
    /// Uses the unsaved settings and credentials captured by Test Connection, without persisting them.
    var checkCandidateConnection: (@Sendable (CleanupConnectionCandidate) async -> CleanupConnectionCheck)? = nil
}

extension CleanupSettingsAccess {
    static var live: CleanupSettingsAccess {
        backed(by: .live, providers: .shared)
    }

    /// The tab over `store`, with Test Connection through `providers`, which should read the same store.
    static func backed(by store: CleanupSettingsStore, providers: CleanupProviderCache) -> CleanupSettingsAccess {
        CleanupSettingsAccess(
            load: {
                return CleanupSettingsValues(
                    isEnabled: store.isEnabled,
                    providerKind: store.providerKind,
                    foundryLocalModelAlias: store.foundryLocalModelAlias,
                    ollamaModel: store.ollamaModel,
                    lmStudioModel: store.lmStudioModel,
                    selectedLocalApp: store.selectedLocalApp,
                    openAIBaseURL: store.openAIBaseURL,
                    openAIModel: store.openAIModel,
                    openAIApiStyle: store.openAIApiStyle,
                    ollamaContextTokens: store.ollamaContextTokens,
                    lmStudioContextTokens: store.lmStudioContextTokens,
                    foundryLocalSendWholeVocabulary: store.foundryLocalSendWholeVocabulary,
                    ollamaSendWholeVocabulary: store.ollamaSendWholeVocabulary,
                    lmStudioSendWholeVocabulary: store.lmStudioSendWholeVocabulary,
                    otherServiceBaseURL: store.otherServiceBaseURL,
                    otherServiceModel: store.otherServiceModel,
                    otherServiceApiStyle: store.otherServiceApiStyle,
                    azureEndpoint: store.azureEndpoint,
                    azureDeployment: store.azureDeployment,
                    azurePromptCaching: store.azurePromptCaching,
                    azureAuthMode: store.azureAuthMode,
                    azureTenantId: store.azureTenantId,
                    azureClientId: store.azureClientId,
                    azureApiKeySelected: store.azureApiKeySelected,
                    writingStyle: store.writingStyle,
                    frontierPrompt: store.frontierPrompt,
                    localPrompt: store.localPrompt,
                    localModelIdleMinutes: store.localModelIdleMinutes)
            },
            save: { new, old in
                if new.localModelIdleMinutes != old.localModelIdleMinutes {
                    store.localModelIdleMinutes = new.localModelIdleMinutes
                }
                if new.isEnabled != old.isEnabled { store.isEnabled = new.isEnabled }
                if new.providerKind != old.providerKind { store.providerKind = new.providerKind }
                if new.foundryLocalModelAlias != old.foundryLocalModelAlias {
                    store.foundryLocalModelAlias = new.foundryLocalModelAlias
                }
                if new.ollamaModel != old.ollamaModel { store.ollamaModel = new.ollamaModel }
                if new.lmStudioModel != old.lmStudioModel { store.lmStudioModel = new.lmStudioModel }
                if new.selectedLocalApp != old.selectedLocalApp { store.selectedLocalApp = new.selectedLocalApp }
                if new.openAIBaseURL != old.openAIBaseURL { store.openAIBaseURL = new.openAIBaseURL }
                if new.openAIModel != old.openAIModel { store.openAIModel = new.openAIModel }
                if new.openAIApiStyle != old.openAIApiStyle { store.openAIApiStyle = new.openAIApiStyle }
                if new.ollamaContextTokens != old.ollamaContextTokens {
                    store.ollamaContextTokens = new.ollamaContextTokens
                }
                if new.lmStudioContextTokens != old.lmStudioContextTokens {
                    store.lmStudioContextTokens = new.lmStudioContextTokens
                }
                if new.foundryLocalSendWholeVocabulary != old.foundryLocalSendWholeVocabulary {
                    store.foundryLocalSendWholeVocabulary = new.foundryLocalSendWholeVocabulary
                }
                if new.ollamaSendWholeVocabulary != old.ollamaSendWholeVocabulary {
                    store.ollamaSendWholeVocabulary = new.ollamaSendWholeVocabulary
                }
                if new.lmStudioSendWholeVocabulary != old.lmStudioSendWholeVocabulary {
                    store.lmStudioSendWholeVocabulary = new.lmStudioSendWholeVocabulary
                }
                if new.otherServiceBaseURL != old.otherServiceBaseURL {
                    store.otherServiceBaseURL = new.otherServiceBaseURL
                }
                if new.otherServiceModel != old.otherServiceModel {
                    store.otherServiceModel = new.otherServiceModel
                }
                if new.otherServiceApiStyle != old.otherServiceApiStyle {
                    store.otherServiceApiStyle = new.otherServiceApiStyle
                }
                if new.azureEndpoint != old.azureEndpoint { store.azureEndpoint = new.azureEndpoint }
                if new.azureDeployment != old.azureDeployment { store.azureDeployment = new.azureDeployment }
                if new.azurePromptCaching != old.azurePromptCaching {
                    store.azurePromptCaching = new.azurePromptCaching
                }
                if new.azureAuthMode != old.azureAuthMode { store.azureAuthMode = new.azureAuthMode }
                if new.azureTenantId != old.azureTenantId { store.azureTenantId = new.azureTenantId }
                if new.azureClientId != old.azureClientId { store.azureClientId = new.azureClientId }
                if new.azureApiKeySelected != old.azureApiKeySelected {
                    store.azureApiKeySelected = new.azureApiKeySelected
                }
            },
            isConfigured: { store.isConfigured(for: $0) },
            hasOpenAIApiKey: { store.openAIApiKey() != nil },
            setOpenAIApiKey: { try store.setOpenAIApiKey($0) },
            hasAzureClientSecret: { store.azureClientSecret(clientId: $0) != nil },
            setAzureClientSecret: { try store.setAzureClientSecret($0, clientId: $1) },
            hasAzureApiKey: { store.azureApiKey() != nil },
            setAzureApiKey: { try store.setAzureApiKey($0) },
            checkConnection: { await providers.checkConnection() },
            checkCandidateConnection: { await providers.checkConnection(candidate: $0) })
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
    @Published private(set) var hasSavedAzureApiKey = false
    @Published private(set) var isTesting = false
    @Published private(set) var statusMessage: String?
    @Published private(set) var errorMessage: String?

    let drafts: SettingsDrafts
    private let access: CleanupSettingsAccess
    /// Where Test Connection runs, so Quit can cancel it and wait for its `az` or `foundry` to be reaped.
    private let operations: AuxiliaryOperations
    private var isReloading = false
    private var isSaving = false
    /// Advances on every change to `values` and every stored credential change, so a connection test can tell its
    /// result is for a configuration the tab no longer shows.
    private var revision = 0
    /// The Test Connection running now, for `cancelConnectionTest()`, and whether the user stopped it.
    private var runningCheck: Task<CleanupConnectionCheck?, Never>?
    private var checkCancelledByUser = false
    private var observation: SettingsNotificationObservation?
    /// Stops a running Test Connection when Settings closes. The task running it keeps this model alive, so waiting
    /// for the model to be freed would wait for the check.
    private var closeObservation: SettingsNotificationObservation?

    init(
        access: CleanupSettingsAccess, drafts: SettingsDrafts, center: NotificationCenter = .default,
        operations: AuxiliaryOperations = .shared
    ) {
        self.access = access
        self.drafts = drafts
        self.operations = operations
        values = access.load()
        drafts.loadCleanupIdleTime(values.localModelIdleMinutes)
        drafts.loadCleanupPrompts(
            writingStyle: values.writingStyle, frontierPrompt: values.frontierPrompt, localPrompt: values.localPrompt)
        observation = SettingsNotificationObservation(UserDefaults.didChangeNotification, center: center) {
            [weak self] in
            self?.reload()
        }
        closeObservation = SettingsNotificationObservation(
            SettingsWindowController.willCloseNotification, center: center
        ) { [weak self] in
            self?.cancelConnectionTest()
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
            guard values.isEnabled, !isTesting, access.isConfigured(values.providerKind) else { return true }
            if values.providerKind == .microsoftFoundry {
                switch azureAuthenticationSelection {
                case .apiKey:
                    return !hasSavedAzureApiKey && drafts.azureApiKey.isEmpty
                case .servicePrincipal:
                    return values.azureTenantId.trimmingCharacters(in: .whitespaces).isEmpty
                        || values.azureClientId.trimmingCharacters(in: .whitespaces).isEmpty
                        || (!hasSavedAzureClientSecret && drafts.azureClientSecret.isEmpty)
                case .azureCli:
                    return false
                }
            }
            return false
        }
    }

    var canSaveOpenAIApiKey: Bool {
        !drafts.openAIApiKey.isEmpty
    }

    var canSaveAzureClientSecret: Bool {
        !drafts.azureClientSecret.isEmpty && !values.azureClientId.trimmingCharacters(in: .whitespaces).isEmpty
    }

    var canSaveAzureApiKey: Bool {
        !drafts.azureApiKey.isEmpty
    }

    var azureAuthenticationSelection: AzureAuthenticationSelection {
        values.azureApiKeySelected ? .apiKey : values.azureAuthMode == .servicePrincipal ? .servicePrincipal : .azureCli
    }

    var providerSelection: CleanupProviderSelection {
        switch values.providerKind {
        case .microsoftFoundry:
            return .microsoftFoundry
        case .foundryLocal, .ollama:
            return .onThisMac
        case .openAICompatible:
            return CustomServiceFields.savedApp(values: values, hasSavedAPIKey: hasSavedOpenAIApiKey) == .none
                ? .otherService
                : .onThisMac
        }
    }

    var localAppChoice: CleanupLocalAppChoice {
        switch values.providerKind {
        case .foundryLocal:
            return .letScribeManageIt
        case .ollama:
            return .ollama
        case .microsoftFoundry:
            return .letScribeManageIt
        case .openAICompatible:
            switch CustomServiceFields.savedApp(values: values, hasSavedAPIKey: hasSavedOpenAIApiKey) {
            case .ollama:
                return .ollama
            case .lmStudio:
                return .lmStudio
            case .none:
                return .letScribeManageIt
            }
        }
    }

    var otherServiceEndpoint: String {
        providerSelection == .otherService ? values.openAIBaseURL : values.otherServiceBaseURL
    }

    var otherServiceModel: String {
        providerSelection == .otherService ? values.openAIModel : values.otherServiceModel
    }

    var otherServiceApiStyle: CustomAPIStyle {
        providerSelection == .otherService ? values.openAIApiStyle : values.otherServiceApiStyle
    }

    var showsConnectionTest: Bool {
        true
    }

    var cleanupSummary: String {
        CleanupDisclosure.summary(
            for: values.providerKind,
            endpoint: values.openAIBaseURL,
            forceLocal: providerSelection == .onThisMac)
    }

    func setProviderSelection(_ selection: CleanupProviderSelection) {
        switch selection {
        case .onThisMac:
            setLocalAppChoice(localAppChoice)
        case .otherService:
            var updated = values
            let other = CustomServiceFields.otherService(values: values, hasSavedAPIKey: hasSavedOpenAIApiKey)
            updated.providerKind = .openAICompatible
            updated.selectedLocalApp = .none
            updated.openAIBaseURL = other.endpoint ?? ""
            updated.openAIModel = other.model ?? ""
            updated.openAIApiStyle = other.apiStyle
            values = updated
        case .microsoftFoundry:
            var updated = values
            updated.providerKind = .microsoftFoundry
            updated.selectedLocalApp = .none
            values = updated
        }
    }

    func setLocalAppChoice(_ choice: CleanupLocalAppChoice) {
        switch choice {
        case .letScribeManageIt:
            var updated = values
            updated.providerKind = .foundryLocal
            updated.selectedLocalApp = .none
            values = updated
        case .ollama, .lmStudio:
            let other = CustomServiceFields.otherService(values: values, hasSavedAPIKey: hasSavedOpenAIApiKey)
            let saved = CustomServiceFields.forSave(
                app: choice.serverApp,
                appModel: localModel(for: choice),
                otherService: other,
                saved: values)
            var updated = values
            updated.providerKind = .openAICompatible
            updated.selectedLocalApp = choice.serverApp
            updated.openAIBaseURL = saved.stored.endpoint ?? ""
            updated.openAIModel = saved.stored.model ?? ""
            updated.openAIApiStyle = saved.stored.apiStyle
            updated.otherServiceBaseURL = saved.remembered.endpoint ?? ""
            updated.otherServiceModel = saved.remembered.model ?? ""
            updated.otherServiceApiStyle = saved.remembered.apiStyle
            values = updated
        }
    }

    func localModel(for choice: CleanupLocalAppChoice? = nil) -> String {
        switch choice ?? localAppChoice {
        case .letScribeManageIt:
            return values.foundryLocalModelAlias
        case .ollama:
            return values.ollamaModel
        case .lmStudio:
            return values.lmStudioModel
        }
    }

    func setLocalModel(_ model: String, for choice: CleanupLocalAppChoice? = nil) {
        let choice = choice ?? localAppChoice
        var updated = values
        switch choice {
        case .letScribeManageIt:
            updated.foundryLocalModelAlias = model
        case .ollama:
            updated.ollamaModel = model
            let saved = CustomServiceFields.forSave(
                app: .ollama,
                appModel: model,
                otherService: CustomServiceFields.otherService(values: values, hasSavedAPIKey: hasSavedOpenAIApiKey),
                saved: values)
            updated.providerKind = .openAICompatible
            updated.selectedLocalApp = .ollama
            updated.openAIBaseURL = saved.stored.endpoint ?? ""
            updated.openAIModel = saved.stored.model ?? ""
            updated.openAIApiStyle = saved.stored.apiStyle
            updated.otherServiceBaseURL = saved.remembered.endpoint ?? ""
            updated.otherServiceModel = saved.remembered.model ?? ""
            updated.otherServiceApiStyle = saved.remembered.apiStyle
        case .lmStudio:
            updated.lmStudioModel = model
            let saved = CustomServiceFields.forSave(
                app: .lmStudio,
                appModel: model,
                otherService: CustomServiceFields.otherService(values: values, hasSavedAPIKey: hasSavedOpenAIApiKey),
                saved: values)
            updated.providerKind = .openAICompatible
            updated.selectedLocalApp = .lmStudio
            updated.openAIBaseURL = saved.stored.endpoint ?? ""
            updated.openAIModel = saved.stored.model ?? ""
            updated.openAIApiStyle = saved.stored.apiStyle
            updated.otherServiceBaseURL = saved.remembered.endpoint ?? ""
            updated.otherServiceModel = saved.remembered.model ?? ""
            updated.otherServiceApiStyle = saved.remembered.apiStyle
        }
        values = updated
    }

    func localContextTokens(for choice: CleanupLocalAppChoice? = nil) -> Int {
        switch choice ?? localAppChoice {
        case .letScribeManageIt:
            return 0
        case .ollama:
            return values.ollamaContextTokens
        case .lmStudio:
            return values.lmStudioContextTokens
        }
    }

    func setLocalContextTokens(_ tokens: Int, for choice: CleanupLocalAppChoice? = nil) {
        let choice = choice ?? localAppChoice
        var updated = values
        switch choice {
        case .letScribeManageIt:
            break
        case .ollama:
            updated.ollamaContextTokens = tokens
        case .lmStudio:
            updated.lmStudioContextTokens = tokens
        }
        values = updated
    }

    func sendsWholeVocabulary(for choice: CleanupLocalAppChoice? = nil) -> Bool {
        switch choice ?? localAppChoice {
        case .letScribeManageIt:
            return values.foundryLocalSendWholeVocabulary
        case .ollama:
            return values.ollamaSendWholeVocabulary
        case .lmStudio:
            return values.lmStudioSendWholeVocabulary
        }
    }

    func setSendsWholeVocabulary(_ enabled: Bool, for choice: CleanupLocalAppChoice? = nil) {
        let choice = choice ?? localAppChoice
        var updated = values
        switch choice {
        case .letScribeManageIt:
            updated.foundryLocalSendWholeVocabulary = enabled
        case .ollama:
            updated.ollamaSendWholeVocabulary = enabled
        case .lmStudio:
            updated.lmStudioSendWholeVocabulary = enabled
        }
        values = updated
    }

    func localAppEndpoint(for choice: CleanupLocalAppChoice? = nil) -> String? {
        let choice = choice ?? localAppChoice
        let app = choice.serverApp
        guard app != .none else {
            return nil
        }
        return LocalAiServer.appAt(values.openAIBaseURL) == app
            ? values.openAIBaseURL
            : LocalAiServer.address(of: app)
    }

    func setOtherServiceEndpoint(_ endpoint: String) {
        var updated = values
        updated.openAIBaseURL = endpoint
        updated.otherServiceBaseURL = endpoint
        if updated.providerKind != .microsoftFoundry {
            updated.providerKind = .openAICompatible
        }
        updated.selectedLocalApp = .none
        values = updated
    }

    func setOtherServiceAPIStyle(_ style: CustomAPIStyle) {
        var updated = values
        updated.openAIApiStyle = style
        updated.otherServiceApiStyle = style
        values = updated
    }

    func setOtherServiceModel(_ model: String) {
        var updated = values
        updated.openAIModel = model
        updated.otherServiceModel = model
        if updated.providerKind != .microsoftFoundry {
            updated.providerKind = .openAICompatible
        }
        updated.selectedLocalApp = .none
        values = updated
    }

    func setAzureAuthenticationSelection(_ selection: AzureAuthenticationSelection) {
        var updated = values
        switch selection {
        case .apiKey:
            updated.azureApiKeySelected = true
        case .azureCli:
            updated.azureApiKeySelected = false
            updated.azureAuthMode = .azureCli
        case .servicePrincipal:
            updated.azureApiKeySelected = false
            updated.azureAuthMode = .servicePrincipal
        }
        values = updated
    }

    /// Re-reads stored settings. What is typed into a secret field is kept: it is not stored until Save.
    func reload() {
        // A write of the tab's own reaches here from inside `save`; storage already holds what the tab shows.
        guard !isSaving else { return }
        let stored = access.load()
        guard stored != values else { return }
        isReloading = true
        values = stored
        drafts.loadCleanupIdleTime(stored.localModelIdleMinutes)
        isReloading = false
        drafts.loadCleanupPrompts(
            writingStyle: stored.writingStyle, frontierPrompt: stored.frontierPrompt, localPrompt: stored.localPrompt)
    }

    /// Re-reads which secrets Keychain holds. Done when the tab appears and after a change that affects it, never
    /// for every preference write, so an open tab does not read Keychain each time anything is stored.
    func refreshSecretState() {
        hasSavedOpenAIApiKey = access.hasOpenAIApiKey()
        hasSavedAzureClientSecret = access.hasAzureClientSecret(values.azureClientId)
        hasSavedAzureApiKey = access.hasAzureApiKey()
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

    /// Removes the saved key, and with it anything typed into the field: Clear means no key, so a half-typed
    /// replacement must not come back when Settings reopens. A failed removal keeps both.
    func clearOpenAIApiKey() {
        do {
            try access.setOpenAIApiKey(nil)
            drafts.openAIApiKey = ""
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

    /// Removes the saved secret for this client ID, and with it anything typed into the field, as Clear does for
    /// the API key. A failed removal keeps both.
    func clearAzureClientSecret() {
        do {
            try access.setAzureClientSecret(nil, values.azureClientId)
            drafts.azureClientSecret = ""
            hasSavedAzureClientSecret = false
            credentialsChanged()
            show(status: "Client secret removed.")
        } catch {
            show(error: "Failed to remove client secret: \(error.localizedDescription)")
        }
    }

    func saveAzureApiKey() {
        do {
            guard let setAzureApiKey = access.setAzureApiKey else {
                throw KeychainStore.KeychainError.unhandled(errSecInternalComponent)
            }
            try setAzureApiKey(drafts.azureApiKey)
            drafts.azureApiKey = ""
            hasSavedAzureApiKey = true
            var updated = values
            updated.azureApiKeySelected = true
            values = updated
            credentialsChanged()
            show(status: "API key saved to Keychain.")
        } catch {
            show(error: "Failed to save API key: \(error.localizedDescription)")
        }
    }

    func clearAzureApiKey() {
        do {
            guard let setAzureApiKey = access.setAzureApiKey else {
                throw KeychainStore.KeychainError.unhandled(errSecInternalComponent)
            }
            try setAzureApiKey(nil)
            drafts.azureApiKey = ""
            hasSavedAzureApiKey = false
            var updated = values
            updated.azureApiKeySelected = false
            values = updated
            credentialsChanged()
            show(status: "API key removed.")
        } catch {
            show(error: "Failed to remove API key: \(error.localizedDescription)")
        }
    }

    private var candidateSettings: CleanupSettingsValues {
        var settings = values
        settings.localModelIdleMinutes = drafts.cleanupIdleMinutes
        return settings
    }

    /// Runs Test Connection through the provider the pipeline would use, environment overrides included. A result that
    /// arrives after the settings or a stored credential changed is dropped rather than shown against them.
    /// `cancelConnectionTest()` stops it while it runs, and so does Quit (`AuxiliaryOperations`).
    func testConnection() async {
        guard !isDisabled(.connectionTest) else { return }
        let started = revision
        let candidate = CleanupConnectionCandidate(
            settings: candidateSettings,
            openAIApiKey: drafts.openAIApiKey.isEmpty ? nil : drafts.openAIApiKey,
            azureClientSecret: drafts.azureClientSecret.isEmpty ? nil : drafts.azureClientSecret,
            azureApiKey: drafts.azureApiKey.isEmpty ? nil : drafts.azureApiKey,
            writingStyle: drafts.cleanupWritingStyle,
            frontierPrompt: drafts.cleanupFrontierPrompt,
            localPrompt: drafts.cleanupLocalPrompt)
        isTesting = true
        statusMessage = nil
        errorMessage = nil
        checkCancelledByUser = false
        let checkConnection = access.checkConnection
        let checkCandidateConnection = access.checkCandidateConnection
        let operations = operations
        // Nil when the check was cancelled before it was admitted, so nothing ran; a refusal at Quit says so.
        let check = Task { () -> CleanupConnectionCheck? in
            do {
                return try await operations.run {
                    if let checkCandidateConnection {
                        return await checkCandidateConnection(candidate)
                    }
                    return await checkConnection()
                }
            } catch AuxiliaryOperations.Refusal.closed {
                return CleanupConnectionCheck(
                    reachable: false, message: "Test Connection did not run, because Scribe is quitting.")
            } catch {
                return nil
            }
        }
        runningCheck = check
        let outcome = await withTaskCancellationHandler {
            await check.value
        } onCancel: {
            check.cancel()
        }
        runningCheck = nil
        isTesting = false
        let stillCurrent =
            started == revision
            && candidate
                == CleanupConnectionCandidate(
                    settings: candidateSettings,
                    openAIApiKey: drafts.openAIApiKey.isEmpty ? nil : drafts.openAIApiKey,
                    azureClientSecret: drafts.azureClientSecret.isEmpty ? nil : drafts.azureClientSecret,
                    azureApiKey: drafts.azureApiKey.isEmpty ? nil : drafts.azureApiKey,
                    writingStyle: drafts.cleanupWritingStyle,
                    frontierPrompt: drafts.cleanupFrontierPrompt,
                    localPrompt: drafts.cleanupLocalPrompt)
        guard stillCurrent else { return }
        guard let result = outcome, !checkCancelledByUser else {
            statusMessage = "Test Connection was cancelled."
            return
        }
        if result.reachable {
            statusMessage = result.message
        } else {
            errorMessage = result.message
        }
    }

    /// Stops the Test Connection that is running, if one is: its request, and any `az` or `foundry` it started, are
    /// cancelled, and the tab says it was cancelled rather than showing a failure.
    func cancelConnectionTest() {
        guard let runningCheck else { return }
        checkCancelledByUser = true
        runningCheck.cancel()
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
