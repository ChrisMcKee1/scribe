import SwiftUI


struct SettingsAICleanupPage: View {
    let drafts: SettingsDrafts

    var body: some View {
        SettingsPage(
            title: "AI cleanup",
            subtitle: "Optional. An AI model fixes punctuation, grammar and repeated words before Scribe types. Dictation works without it."
        ) {
            SettingsCard { CleanupSettingsTab(drafts: drafts) }
        }
    }
}

// MARK: - AI Cleanup tab

/// Settings surface for AI cleanup: turning it on, picking a provider, and configuring that provider's
/// connection details and credentials. `CleanupSettingsModel` stores every non-secret field the moment it changes
/// and re-reads them after a change made elsewhere, such as the tray's AI Cleanup item. The two secrets
/// (OpenAI-compatible API key, Azure service-principal client secret) are explicit Save and Clear actions against
/// Keychain, so a partly typed secret is never stored; until it is saved, what was typed lives in `SettingsDrafts`.
struct CleanupSettingsTab: View {
    @StateObject private var model: CleanupSettingsModel
    @ObservedObject private var drafts: SettingsDrafts

    init(drafts: SettingsDrafts, access: CleanupSettingsAccess = .live) {
        _drafts = ObservedObject(wrappedValue: drafts)
        _model = StateObject(wrappedValue: CleanupSettingsModel(access: access, drafts: drafts))
    }

    var body: some View {
        Form {
            Section {
                Toggle("Enable AI cleanup", isOn: $model.values.isEnabled)
                    .disabled(model.isDisabled(.enableSwitch))
                Text(
                    "Cleans up punctuation and phrasing after each dictation using a locally or "
                        + "remotely hosted model. Strictly opt-in and off by default: only the "
                        + "transcribed text is ever sent to a cleanup provider, never audio."
                )
                .font(.caption)
                .foregroundStyle(.secondary)
            }

            Section("Provider") {
                Picker("Provider", selection: $model.values.providerKind) {
                    ForEach(CleanupProviderKind.allCases) { kind in
                        Text(kind.displayName).tag(kind)
                    }
                }
            }
            .disabled(model.isDisabled(.provider))

            providerConfigurationSection
                .disabled(model.isDisabled(.providerDetails))

            Section {
                HStack {
                    Button(model.isTesting ? "Testing\u{2026}" : "Test Connection") {
                        Task { await model.testConnection() }
                    }
                    .disabled(model.isDisabled(.connectionTest))
                    if model.isTesting {
                        ProgressView().controlSize(.small)
                        Button("Cancel") { model.cancelConnectionTest() }
                    }
                    Spacer()
                }
                if let errorMessage = model.errorMessage {
                    Text(errorMessage).foregroundStyle(.red).font(.caption)
                } else if let statusMessage = model.statusMessage {
                    Text(statusMessage).foregroundStyle(.secondary).font(.caption)
                }
            }
        }
        .formStyle(.grouped)
        .onAppear {
            model.reload()
            model.refreshSecretState()
        }
        // Leaving the tab stops a Test Connection still running; closing the window does too, through
        // `SettingsWindowController.willCloseNotification`, in case the window goes without this firing.
        .onDisappear {
            model.cancelConnectionTest()
        }
    }

    @ViewBuilder
    private var providerConfigurationSection: some View {
        switch model.values.providerKind {
        case .foundryLocal:
            Section("Foundry Local") {
                TextField("Model alias", text: $model.values.foundryLocalModelAlias)
                Text(
                    "Runs fully on-device via Foundry Local. Requires "
                        + "'brew install microsoft/foundrylocal/foundrylocal'; the model downloads on "
                        + "first use."
                )
                .font(.caption)
                .foregroundStyle(.secondary)
            }
        case .ollama:
            Section("Local model (Ollama managed)") {
                TextField("Model", text: $model.values.ollamaModel)
                Text(
                    "Runs fully on-device via a local Ollama installation "
                        + "(http://127.0.0.1:11434). Appropriate if you already run Ollama for other tools."
                )
                .font(.caption)
                .foregroundStyle(.secondary)
            }
        case .openAICompatible:
            Section("OpenAI-compatible endpoint") {
                TextField("Base URL (e.g. http://localhost:1234)", text: $model.values.openAIBaseURL)
                TextField("Model", text: $model.values.openAIModel)
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
                    "For LM Studio, OpenRouter, or any other OpenAI-compatible server. The API "
                        + "key, if any, is stored in Keychain, never in plain text."
                )
                .font(.caption)
                .foregroundStyle(.secondary)
            }
        case .microsoftFoundry:
            Section("Microsoft Foundry (cloud)") {
                TextField(
                    "Endpoint (e.g. https://my-resource.cognitiveservices.azure.com)",
                    text: $model.values.azureEndpoint)
                TextField("Deployment name", text: $model.values.azureDeployment)
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
                        "The client secret is stored in Keychain, never in an environment "
                            + "variable, a plist, or a script."
                    )
                    .font(.caption)
                    .foregroundStyle(.secondary)
                } else {
                    Text(
                        "Uses the signed-in 'az login' session on this Mac. Install the Azure "
                            + "CLI and run 'az login' once."
                    )
                    .font(.caption)
                    .foregroundStyle(.secondary)
                }
            }
        }
    }
}

