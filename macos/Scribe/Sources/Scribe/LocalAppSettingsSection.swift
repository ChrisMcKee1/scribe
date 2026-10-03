import Foundation
import SwiftUI

enum CleanupProviderSelection: String, CaseIterable, Identifiable, Sendable {
    case onThisMac
    case otherService
    case microsoftFoundry

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .onThisMac:
            return "On this PC"
        case .otherService:
            return "Another AI service"
        case .microsoftFoundry:
            return "Microsoft Foundry (cloud)"
        }
    }
}

enum CleanupLocalAppChoice: String, CaseIterable, Identifiable, Sendable {
    case letScribeManageIt
    case ollama
    case lmStudio

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .letScribeManageIt:
            return "Let Scribe manage it"
        case .ollama:
            return "Ollama"
        case .lmStudio:
            return "LM Studio"
        }
    }

    var serverApp: LocalServerApp {
        switch self {
        case .letScribeManageIt:
            return .none
        case .ollama:
            return .ollama
        case .lmStudio:
            return .lmStudio
        }
    }
}

@MainActor
final class LocalAppSettingsModel: ObservableObject {
    @Published private var states: [LocalServerApp: LocalServerState] = [:]
    @Published private var loading: Set<LocalServerApp> = []

    @Published private(set) var freeMemoryNotice: String?

    private let read: @Sendable (String) async -> LocalServerState
    private var refreshOwners: [LocalServerApp: UUID] = [:]
    private let lifecycle: LocalModelLifecycle

    init(client: LocalServerClient = LocalServerClient(), lifecycle: LocalModelLifecycle = .shared) {
        self.read = { await client.read($0) }
        self.lifecycle = lifecycle
    }

    init(
        read: @escaping @Sendable (String) async -> LocalServerState,
        lifecycle: LocalModelLifecycle = .shared
    ) {
        self.read = read
        self.lifecycle = lifecycle
    }

    func state(for app: LocalServerApp) -> LocalServerState? {
        if loading.contains(app) {
            return nil
        }
        return states[app]
    }

    func refresh(for app: LocalServerApp, endpoint: String?) async {
        guard app != .none, let endpoint, !Task.isCancelled else {
            return
        }
        let owner = UUID()
        refreshOwners[app] = owner
        loading.insert(app)
        defer {
            if refreshOwners[app] == owner {
                refreshOwners[app] = nil
                loading.remove(app)
            }
        }
        let state = await read(endpoint)
        guard refreshOwners[app] == owner, !Task.isCancelled else { return }
        states[app] = state
    }

    func unload(for app: LocalServerApp, endpoint: String?, model: String) async {
        guard app != .none, let endpoint else {
            return
        }
        let target = LocalModelTarget(endpoint: endpoint, model: model, app: app, apiKey: nil)
        let outcome = await lifecycle.release(.freeMemory, target: target)
        switch outcome {
        case .released, .nothingToRelease:
            freeMemoryNotice = nil
        case .drainTimedOut:
            freeMemoryNotice = "The model is still in use. Try again in a moment."
        default:
            freeMemoryNotice = "Could not free the model."
            ScribeLog.warning(.cleanup, "Could not free the local model", .name("outcome", outcome))
        }
        await refresh(for: app, endpoint: endpoint)
    }
}

struct CleanupProviderSettingsSection: View {
    @ObservedObject var model: CleanupSettingsModel
    @ObservedObject var drafts: SettingsDrafts
    @StateObject private var local = LocalAppSettingsModel()
    @StateObject private var vocabularyStatus: CleanupVocabularyStatusModel

    init(
        model: CleanupSettingsModel,
        drafts: SettingsDrafts,
        persistenceStore: PersistenceStore,
        dictionaryLibraryService: DictionaryLibraryService
    ) {
        _model = ObservedObject(wrappedValue: model)
        _drafts = ObservedObject(wrappedValue: drafts)
        _vocabularyStatus = StateObject(
            wrappedValue: CleanupVocabularyStatusModel(
                persistenceStore: persistenceStore,
                librarySource: dictionaryLibraryService))
    }

    var body: some View {
        Group {
            Section("Provider") {
                Picker(
                    "Provider",
                    selection: Binding(
                        get: { model.providerSelection },
                        set: { model.setProviderSelection($0) })
                ) {
                    ForEach(CleanupProviderSelection.allCases) { selection in
                        Text(selection.displayName).tag(selection)
                    }
                }
            }

            switch model.providerSelection {
            case .onThisMac:
                onThisMacSection
            case .otherService:
                otherServiceSection
            case .microsoftFoundry:
                microsoftFoundrySection
            }
        }
        .task(id: localRefreshKey) {
            await vocabularyStatus.refresh()
            let choice = model.localAppChoice
            let app = choice.serverApp
            guard app != .none else {
                return
            }
            await local.refresh(for: app, endpoint: model.localAppEndpoint(for: choice))
        }
    }

    private var localRefreshKey: String {
        "\(model.providerSelection.rawValue)|\(model.localAppChoice.rawValue)|\(model.values.isEnabled)"
    }

    @ViewBuilder
    private var onThisMacSection: some View {
        Section("On this PC") {
            Picker(
                "Runs through",
                selection: Binding(
                    get: { model.localAppChoice },
                    set: { model.setLocalAppChoice($0) })
            ) {
                ForEach(CleanupLocalAppChoice.allCases) { choice in
                    Text(choice.displayName).tag(choice)
                }
            }

            switch model.localAppChoice {
            case .letScribeManageIt:
                TextField("Model alias", text: $model.values.foundryLocalModelAlias)
                Toggle(
                    LocalModelTuningText.wholeVocabularyTitle,
                    isOn: Binding(
                        get: { model.sendsWholeVocabulary(for: .letScribeManageIt) },
                        set: { model.setSendsWholeVocabulary($0, for: .letScribeManageIt) }))
                Text(LocalModelTuningText.foundryWholeVocabularyHint)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(
                    "Runs fully on-device via Foundry Local. "
                        + FoundryLocalSetupText.installationHint() + " The model downloads on first use."
                )
                .font(.caption)
                .foregroundStyle(.secondary)
            case .ollama, .lmStudio:
                localAppModelSection
            }
        }
    }

    @ViewBuilder
    private var localAppModelSection: some View {
        let choice = model.localAppChoice
        let app = choice.serverApp
        let selectedModel = model.localModel(for: choice)
        let state = local.state(for: app)
        let choices = LocalAppSetup.modelChoices(state?.models ?? [], selectedModel)
        let status = LocalAppSetup.describe(
            app, state, choices.selected, idleMinutes: CleanupSettingsStore.live.localModelIdleMinutes)
        let askedContext = model.localContextTokens(for: choice)
        let effectiveContext = state?.loaded(for: choices.selected)?.contextTokens ?? 0
        Picker("Free local model memory after", selection: $drafts.cleanupIdleMinutes) {
            ForEach(Array(Set([0, 1, 2, 5, 10, 15, 30, 60, drafts.cleanupIdleMinutes])).sorted(), id: \.self) {
                minutes in
                Text(minutes == 0 ? "Never" : "\(minutes) minutes").tag(minutes)
            }
        }
        Text("Applies to Ollama and LM Studio. Save applies the change; the speech recognizer manages its own memory.")
            .font(.caption)
            .foregroundStyle(.secondary)
        Picker(
            "Model",
            selection: Binding(
                get: { choices.selected ?? "" },
                set: { model.setLocalModel($0, for: choice) })
        ) {
            ForEach(choices.models) { localModel in
                Text(localModel.displayName).tag(localModel.id)
            }
        }
        Picker(
            LocalModelTuningText.contextSizeTitle,
            selection: Binding(
                get: { askedContext },
                set: { model.setLocalContextTokens($0, for: choice) })
        ) {
            ForEach(LocalModelTuningText.contextSizes(app.displayName, stored: askedContext), id: \.tokens) { size in
                Text(size.label).tag(size.tokens)
            }
        }
        Text(app == .ollama ? LocalModelTuningText.ollamaContextSizeHint : LocalModelTuningText.lmStudioContextSizeHint)
            .font(.caption)
            .foregroundStyle(.secondary)
        Toggle(
            LocalModelTuningText.wholeVocabularyTitle,
            isOn: Binding(
                get: { model.sendsWholeVocabulary(for: choice) },
                set: { model.setSendsWholeVocabulary($0, for: choice) }))
        Text(LocalModelTuningText.appWholeVocabularyHint)
            .font(.caption)
            .foregroundStyle(.secondary)
        Text(status.text)
            .font(.caption)
            .foregroundStyle(color(for: status.kind))
        Text(
            LocalModelTuningText.contextStatus(
                app.displayName,
                inUse: effectiveContext,
                asked: ContextBudget.sanitize(askedContext),
                vocabularyTokens: vocabularyStatus.wholeVocabularyTokens,
                vocabularyRoom: vocabularyRoom(inUse: effectiveContext, asked: askedContext)
            )
        )
        .font(.caption)
        .foregroundStyle(.secondary)
        if let action = status.primary {
            HStack {
                Button(action.text) {
                    Task {
                        switch action.id {
                        case .checkAgain:
                            await local.refresh(for: app, endpoint: model.localAppEndpoint(for: choice))
                        case .unload:
                            await local.unload(
                                for: app,
                                endpoint: model.localAppEndpoint(for: choice),
                                model: model.localModel(for: choice))
                        }
                    }
                }
                .disabled(!action.isEnabled)
                Spacer()
            }
        }
        if let notice = local.freeMemoryNotice {
            Text(notice).font(.caption).foregroundStyle(.secondary)
        }
    }

    private var otherServiceSection: some View {
        Section("Another AI service") {
            TextField(
                "Base URL (e.g. https://openrouter.ai/api/v1)",
                text: Binding(
                    get: { model.otherServiceEndpoint },
                    set: { model.setOtherServiceEndpoint($0) }))
            Text(CustomAPIStyleText.addressHint)
                .font(.caption)
                .foregroundStyle(.secondary)
            if CustomAPIStyleText.canChoose(model.otherServiceEndpoint) {
                Picker(
                    "API",
                    selection: Binding(
                        get: { model.otherServiceApiStyle },
                        set: { model.setOtherServiceAPIStyle($0) })
                ) {
                    ForEach(CustomAPIStyleText.choices) { style in
                        Text(CustomAPIStyleText.name(of: style)).tag(style)
                    }
                }
            }
            Text(CustomAPIStyleText.hint(model.otherServiceEndpoint))
                .font(.caption)
                .foregroundStyle(.secondary)
            TextField(
                "Model",
                text: Binding(
                    get: { model.otherServiceModel },
                    set: { model.setOtherServiceModel($0) }))
            SecureField(
                model.hasSavedOpenAIApiKey ? "API key saved (leave blank to keep)" : "API key (optional)",
                text: $drafts.openAIApiKey)
            HStack {
                Button("Save Key") { model.saveOpenAIApiKey() }
                    .disabled(!model.canSaveOpenAIApiKey)
                if model.hasSavedOpenAIApiKey {
                    Button("Clear Key", role: .destructive) { model.clearOpenAIApiKey() }
                }
            }
            Text(
                "For OpenRouter, llama.cpp, or any other OpenAI-compatible server. If you want Ollama or LM Studio "
                    + "on this Mac, choose On this PC above. The API key, if any, is stored in Keychain, never in "
                    + "plain text."
            )
            .font(.caption)
            .foregroundStyle(.secondary)
        }
    }

    private var microsoftFoundrySection: some View {
        Section("Microsoft Foundry (cloud)") {
            TextField(
                "Endpoint (e.g. https://my-resource.cognitiveservices.azure.com)",
                text: $model.values.azureEndpoint)
            TextField("Deployment name", text: $model.values.azureDeployment)
            Toggle("Let Microsoft Foundry cache what Scribe sends", isOn: $model.values.azurePromptCaching)
            Text(
                "On: Microsoft Foundry may keep temporary prompt-cache data derived from what Scribe sends. Off: "
                    + "Scribe asks Microsoft Foundry not to use its prompt cache for new cleanup requests. "
                    + "Some older or provisioned deployments reject that request, and cleanup stays unavailable "
                    + "until you turn it back on."
            )
            .font(.caption)
            .foregroundStyle(.secondary)
            Picker(
                "Authentication",
                selection: Binding(
                    get: { model.azureAuthenticationSelection },
                    set: { model.setAzureAuthenticationSelection($0) })
            ) {
                ForEach(AzureAuthenticationSelection.allCases) { selection in
                    Text(selection.label).tag(selection)
                }
            }

            if model.azureAuthenticationSelection == .apiKey {
                SecureField(
                    model.hasSavedAzureApiKey ? "API key saved (leave blank to keep)" : "API key",
                    text: $drafts.azureApiKey)
                HStack {
                    Button("Save API Key") { model.saveAzureApiKey() }
                        .disabled(!model.canSaveAzureApiKey)
                    if model.hasSavedAzureApiKey {
                        Button("Clear API Key", role: .destructive) { model.clearAzureApiKey() }
                    }
                }
                Text(
                    "The API key is stored only in Keychain. When selected, it takes precedence over Azure CLI or service principal sign-in. Save it before testing the connection."
                )
                .font(.caption)
                .foregroundStyle(.secondary)
            } else if model.azureAuthenticationSelection == .servicePrincipal {
                TextField("Tenant ID", text: $model.values.azureTenantId)
                TextField("Client ID", text: $model.values.azureClientId)
                SecureField(
                    model.hasSavedAzureClientSecret ? "Client secret saved (leave blank to keep)" : "Client secret",
                    text: $drafts.azureClientSecret)
                HStack {
                    Button("Save Secret") { model.saveAzureClientSecret() }
                        .disabled(!model.canSaveAzureClientSecret)
                    if model.hasSavedAzureClientSecret {
                        Button("Clear Secret", role: .destructive) { model.clearAzureClientSecret() }
                    }
                }
                Text(
                    "The client secret is stored in Keychain, never in an environment variable, a plist, or a script."
                )
                .font(.caption)
                .foregroundStyle(.secondary)
            } else {
                Text(
                    "Uses the signed-in 'az login' session on this Mac. Install the Azure CLI and run 'az login' once."
                )
                .font(.caption)
                .foregroundStyle(.secondary)
            }
        }
    }

    private func color(for kind: AiCleanupStatusKind) -> Color {
        switch kind {
        case .warning, .error:
            return .red
        case .success:
            return .green
        case .busy, .info, .none:
            return .secondary
        }
    }

    private func vocabularyRoom(inUse: Int, asked: Int) -> Int? {
        let context = inUse > 0 ? inUse : ContextBudget.sanitize(asked)
        guard context > 0 else {
            return nil
        }
        return ContextBudget.vocabularyRoom(
            context,
            instructions: CleanupPrompt.systemPrompt(
                writingStyle: CleanupPrompt.defaultWritingStyle,
                useLocalPrompt: true))
    }
}
