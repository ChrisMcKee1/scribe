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

    private let client: LocalServerClient

    init(client: LocalServerClient = LocalServerClient()) {
        self.client = client
    }

    func state(for app: LocalServerApp) -> LocalServerState? {
        if loading.contains(app) {
            return nil
        }
        return states[app]
    }

    func refresh(for app: LocalServerApp, endpoint: String?) async {
        guard app != .none, let endpoint else {
            return
        }
        loading.insert(app)
        defer { loading.remove(app) }
        states[app] = await client.read(endpoint)
    }

    func unload(for app: LocalServerApp, endpoint: String?, model: String) async {
        guard app != .none, let endpoint else {
            return
        }
        _ = await client.unload(endpoint, modelID: model)
        await refresh(for: app, endpoint: endpoint)
    }
}

struct CleanupProviderSettingsSection: View {
    @ObservedObject var model: CleanupSettingsModel
    @ObservedObject var drafts: SettingsDrafts
    @StateObject private var local = LocalAppSettingsModel()

    private let defaultIdleMinutes = LocalModelDefaults.keepAliveMinutes

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
                Text(
                    "Runs fully on-device via Foundry Local. Requires "
                        + "'brew install microsoft/foundrylocal/foundrylocal'; the model downloads on first use."
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
        let status = LocalAppSetup.describe(app, state, choices.selected, idleMinutes: defaultIdleMinutes)
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
        Text(status.text)
            .font(.caption)
            .foregroundStyle(color(for: status.kind))
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
    }

    private var otherServiceSection: some View {
        Section("Another AI service") {
            TextField(
                "Base URL (e.g. https://openrouter.ai/api/v1)",
                text: Binding(
                    get: { model.otherServiceEndpoint },
                    set: { model.setOtherServiceEndpoint($0) }))
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
            Picker("Authentication", selection: $model.values.azureAuthMode) {
                Text("Azure CLI (az login)").tag(AzureAuthMode.azureCli)
                Text("Service principal").tag(AzureAuthMode.servicePrincipal)
            }

            if model.values.azureAuthMode == .servicePrincipal {
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
}
