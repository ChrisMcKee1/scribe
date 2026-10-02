import SwiftUI

struct SettingsAICleanupPage: View {
    let drafts: SettingsDrafts

    var body: some View {
        SettingsPage(
            title: "AI cleanup",
            subtitle: "Optional. An AI model fixes punctuation, grammar and repeated words before Scribe types. Dictation works without it."
        ) {
            CleanupSettingsTab(drafts: drafts)
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
        VStack(alignment: .leading, spacing: 14) {
            enableCard

            if !model.values.isEnabled {
                Text("Turn on AI cleanup to choose where it runs and set your writing style.")
                    .cardDescription()
                    .padding(.horizontal, 2)
            }

            providerCard
                .disabled(model.isDisabled(.provider))

            providerDetailsCard
                .disabled(model.isDisabled(.providerDetails))

            writingStyleCard
        }
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

    private var enableCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 4) {
                Toggle(isOn: $model.values.isEnabled) {
                    Text("Use AI cleanup")
                        .cardTitle()
                }
                .disabled(model.isDisabled(.enableSwitch))

                Text(enableStatusText)
                    .cardDescription()
                    .padding(.leading, 22)
            }
        }
    }

    private var providerCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 12) {
                Text("Where AI cleanup runs")
                    .cardTitle()

                VStack(alignment: .leading, spacing: 8) {
                    RadioDescriptionRow(
                        isSelected: providerLocation == .local,
                        title: "On this Mac",
                        description: "Private: your text stays on this Mac. Scribe can use Foundry Local or Ollama.",
                        action: { providerLocation = .local })
                    RadioDescriptionRow(
                        isSelected: providerLocation == .microsoftFoundry,
                        title: "Microsoft Foundry",
                        description: "Uses a model in your Azure account. Your text goes to your Azure resource.",
                        action: { providerLocation = .microsoftFoundry })
                    RadioDescriptionRow(
                        isSelected: providerLocation == .anotherService,
                        title: "Another AI service",
                        description: "Connects to the server address you enter, such as OpenRouter, OpenAI or a server on another computer. Your text goes to that address.",
                        action: { providerLocation = .anotherService })
                }

                Text(providerPrivacyText)
                    .cardDescription()
                    .padding(.top, 2)

                DisclosureGroup("What AI cleanup sends") {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Scribe sends the transcribed text and cleanup instructions for the writing style. It never sends audio.")
                        Text("The macOS cleanup path does not send dictionary or library terms to the cleanup model.")
                        Text("A local provider keeps that request on this Mac. Microsoft Foundry sends it to your Azure resource. Another AI service sends it to the server address you enter.")
                    }
                    .cardDescription()
                    .padding(.top, 6)
                }
                .font(.body.weight(.semibold))
            }
        }
    }

    @ViewBuilder
    private var providerDetailsCard: some View {
        switch providerLocation {
        case .local:
            localProviderCard
        case .microsoftFoundry:
            microsoftFoundryCard
        case .anotherService:
            anotherServiceCard
        }
    }

    private var localProviderCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 14) {
                VStack(alignment: .leading, spacing: 10) {
                    Text("How to run it")
                        .cardTitle()
                    RadioDescriptionRow(
                        isSelected: model.values.providerKind == .foundryLocal,
                        title: "Let Scribe manage it",
                        description: "Uses Foundry Local on this Mac. The model downloads on first use.",
                        action: { model.values.providerKind = .foundryLocal })
                    RadioDescriptionRow(
                        isSelected: model.values.providerKind == .ollama,
                        title: "Ollama",
                        description: "Uses a model you downloaded in Ollama.",
                        action: { model.values.providerKind = .ollama })
                }

                Divider()

                if model.values.providerKind == .ollama {
                    FormField(title: "Model", hint: "Use the exact Ollama model name, for example qwen2.5:3b.") {
                        TextField("Model", text: $model.values.ollamaModel)
                            .textFieldStyle(.roundedBorder)
                    }
                } else {
                    FormField(
                        title: "Model",
                        hint: "Use the Foundry Local model alias. The recommended default is qwen2.5-1.5b."
                    ) {
                        TextField("Model alias", text: $model.values.foundryLocalModelAlias)
                            .textFieldStyle(.roundedBorder)
                    }

                    Text(
                        "Requires 'brew install microsoft/foundrylocal/foundrylocal'. Nothing downloads until you choose Load or save with AI cleanup on."
                    )
                    .cardDescription()
                }

                actionStatusRow(primaryTitle: localActionTitle)
            }
        }
    }

    private var microsoftFoundryCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 14) {
                VStack(alignment: .leading, spacing: 10) {
                    Text("How to sign in")
                        .cardTitle()
                    RadioDescriptionRow(
                        isSelected: model.values.azureAuthMode == .azureCli,
                        title: "Your Azure account (Azure CLI)",
                        description: "Uses the signed-in 'az login' session on this Mac. Install the Azure CLI and run 'az login' once.",
                        action: { model.values.azureAuthMode = .azureCli })
                    RadioDescriptionRow(
                        isSelected: model.values.azureAuthMode == .servicePrincipal,
                        title: "An app registration (service principal)",
                        description: "Uses an Entra app registration your organization set up for Scribe.",
                        action: { model.values.azureAuthMode = .servicePrincipal })
                }

                Divider()

                FormField(title: "Endpoint", hint: "For example, https://my-resource.cognitiveservices.azure.com.") {
                    TextField("Endpoint", text: $model.values.azureEndpoint)
                        .textFieldStyle(.roundedBorder)
                }

                FormField(title: "Deployment name", hint: "The model's exact deployment name in Microsoft Foundry.") {
                    TextField("Deployment name", text: $model.values.azureDeployment)
                        .textFieldStyle(.roundedBorder)
                }

                if model.values.azureAuthMode == .servicePrincipal {
                    FormField(title: "Directory (tenant) ID", hint: "Use a tenant ID or domain name.") {
                        TextField("Directory (tenant) ID", text: $model.values.azureTenantId)
                            .textFieldStyle(.roundedBorder)
                    }

                    FormField(title: "Application (client) ID", hint: "From the app registration overview page.") {
                        TextField("Application (client) ID", text: $model.values.azureClientId)
                            .textFieldStyle(.roundedBorder)
                    }

                    FormField(title: "Client secret", hint: "Saved in Keychain, never in an environment variable, a plist or a script.") {
                        SecureField(
                            model.hasSavedAzureClientSecret
                                ? "Client secret saved, leave blank to keep"
                                : "Client secret",
                            text: $drafts.azureClientSecret)
                            .textFieldStyle(.roundedBorder)
                    }

                    HStack {
                        Button("Save Secret") { model.saveAzureClientSecret() }
                            .disabled(!model.canSaveAzureClientSecret)
                        if model.hasSavedAzureClientSecret {
                            Button("Clear Secret", role: .destructive) { model.clearAzureClientSecret() }
                        }
                    }
                }

                actionStatusRow(primaryTitle: "Test Connection")
            }
        }
    }

    private var anotherServiceCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 14) {
                FormField(
                    title: "Server address",
                    hint: "Use the server's base address, such as http://localhost:1234 or https://openrouter.ai/api/v1."
                ) {
                    TextField("Server address", text: $model.values.openAIBaseURL)
                        .textFieldStyle(.roundedBorder)
                }

                FormField(title: "API", hint: "macOS currently uses OpenAI-compatible chat completions.") {
                    Text("OpenAI-compatible")
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }

                FormField(title: "Model name", hint: "Use the exact model name this server expects.") {
                    TextField("Model name", text: $model.values.openAIModel)
                        .textFieldStyle(.roundedBorder)
                }

                FormField(title: "API key (optional)", hint: "Saved in Keychain when you choose Save Key.") {
                    SecureField(
                        model.hasSavedOpenAIApiKey ? "API key saved, leave blank to keep" : "API key (optional)",
                        text: $drafts.openAIApiKey)
                        .textFieldStyle(.roundedBorder)
                }

                HStack {
                    Button("Save Key") { model.saveOpenAIApiKey() }
                        .disabled(!model.canSaveOpenAIApiKey)
                    if model.hasSavedOpenAIApiKey {
                        Button("Clear Key", role: .destructive) { model.clearOpenAIApiKey() }
                    }
                }

                actionStatusRow(primaryTitle: "Test Connection")
            }
        }
    }

    private var writingStyleCard: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 10) {
                Text("Writing style")
                    .cardTitle()
                Text(
                    "Scribe uses this default cleanup style unless an app profile overrides it. Edit per-app styles on the App profiles page."
                )
                .cardDescription()

                ScrollView {
                    Text(CleanupPrompt.defaultWritingStyle)
                        .font(.system(.caption, design: .monospaced))
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8)
                }
                .frame(minHeight: 130, maxHeight: 180)
                .background(
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .fill(Color(nsColor: .textBackgroundColor))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .stroke(Color(nsColor: .separatorColor).opacity(0.4), lineWidth: 1)
                )
            }
        }
    }

    @ViewBuilder
    private func actionStatusRow(primaryTitle: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Button(model.isTesting ? "Testing..." : primaryTitle) {
                    Task { await model.testConnection() }
                }
                .disabled(model.isDisabled(.connectionTest))

                if model.isTesting {
                    ProgressView()
                        .controlSize(.small)
                    Button("Cancel") { model.cancelConnectionTest() }
                }

                Spacer()
            }

            if let errorMessage = model.errorMessage {
                Text(errorMessage)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            } else if let statusMessage = model.statusMessage {
                Text(statusMessage)
                    .cardDescription()
            }
        }
    }

    private var providerLocation: ProviderLocation {
        get { ProviderLocation(kind: model.values.providerKind) }
        nonmutating set {
            switch newValue {
            case .local:
                if !model.values.providerKind.isLocalProvider {
                    model.values.providerKind = .foundryLocal
                }
            case .microsoftFoundry:
                model.values.providerKind = .microsoftFoundry
            case .anotherService:
                model.values.providerKind = .openAICompatible
            }
        }
    }

    private var enableStatusText: String {
        if !model.values.isEnabled {
            return "Off. Scribe types what it hears, with your dictionary and snippets."
        }
        if model.isTesting {
            return "On. Checking whether \(model.values.providerKind.providerName) is ready."
        }
        if !model.values.providerKind.isLocalProvider && !isConfigured {
            return "On, but not set up yet. Until it's ready, Scribe types what it hears."
        }
        if !isConfigured {
            return "On, but not set up yet. Until it's ready, Scribe types what it hears."
        }
        return "On. Scribe will clean up text with \(model.values.providerKind.providerName) before typing."
    }

    private var providerPrivacyText: String {
        switch providerLocation {
        case .local:
            return "Audio never leaves this Mac. With this choice, cleanup text stays on this Mac too."
        case .microsoftFoundry:
            return "Audio never leaves this Mac. Cleanup sends transcribed text to your Azure resource."
        case .anotherService:
            return "Audio never leaves this Mac. Cleanup sends transcribed text to the server address you enter."
        }
    }

    private var localActionTitle: String {
        model.values.providerKind == .foundryLocal ? "Load" : "Test Connection"
    }

    private var isConfigured: Bool {
        !model.isDisabled(.connectionTest) || model.isTesting
    }
}

private enum ProviderLocation {
    case local
    case microsoftFoundry
    case anotherService

    init(kind: CleanupProviderKind) {
        switch kind {
        case .foundryLocal, .ollama:
            self = .local
        case .microsoftFoundry:
            self = .microsoftFoundry
        case .openAICompatible:
            self = .anotherService
        }
    }
}

private extension CleanupProviderKind {
    var isLocalProvider: Bool {
        self == .foundryLocal || self == .ollama
    }
}

private struct RadioDescriptionRow: View {
    let isSelected: Bool
    let title: String
    let description: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: isSelected ? "largecircle.fill.circle" : "circle")
                    .imageScale(.medium)
                    .foregroundStyle(isSelected ? Color.accentColor : Color.secondary)
                    .padding(.top, 1)

                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .cardTitle()
                    Text(description)
                        .cardDescription()
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
    }
}

private struct FormField<Content: View>: View {
    let title: String
    let hint: String?
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .cardTitle()
            content()
            if let hint {
                Text(hint)
                    .cardDescription()
            }
        }
    }
}
