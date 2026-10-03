import SwiftUI

struct SettingsAICleanupPage: View {
    let drafts: SettingsDrafts
    let persistenceStore: PersistenceStore
    let dictionaryLibraryService: DictionaryLibraryService

    var body: some View {
        SettingsPage(
            title: "AI cleanup",
            subtitle:
                "Optional. An AI model fixes punctuation, grammar and repeated words before Scribe types. Dictation works without it."
        ) {
            CleanupSettingsTab(
                drafts: drafts,
                persistenceStore: persistenceStore,
                dictionaryLibraryService: dictionaryLibraryService)
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
    private let persistenceStore: PersistenceStore
    private let dictionaryLibraryService: DictionaryLibraryService

    init(
        drafts: SettingsDrafts,
        persistenceStore: PersistenceStore,
        dictionaryLibraryService: DictionaryLibraryService,
        access: CleanupSettingsAccess = .live
    ) {
        _drafts = ObservedObject(wrappedValue: drafts)
        _model = StateObject(wrappedValue: CleanupSettingsModel(access: access, drafts: drafts))
        self.persistenceStore = persistenceStore
        self.dictionaryLibraryService = dictionaryLibraryService
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

            writingStyleCard
            guardrailPromptsCard
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
        SettingsCard(searchID: "ai.enabled") {
            VStack(alignment: .leading, spacing: 4) {
                Toggle(isOn: $model.values.isEnabled) {
                    Text("Use AI cleanup")
                        .cardTitle()
                }
                .disabled(model.isDisabled(.enableSwitch))

                Text(enableStatusText)
                    .cardDescription()
                    .padding(.leading, 22)
                Text(model.cleanupSummary)
                    .cardDescription()
                    .padding(.leading, 22)
            }
        }
    }

    private var providerCard: some View {
        SettingsCard(searchID: "ai.provider") {
            Form {
                CleanupProviderSettingsSection(
                    model: model,
                    drafts: drafts,
                    persistenceStore: persistenceStore,
                    dictionaryLibraryService: dictionaryLibraryService
                )
                .disabled(model.isDisabled(.providerDetails))

                if model.showsConnectionTest {
                    Section {
                        connectionRow
                    }

                    CleanupDisclosureSection(
                        providerKind: model.values.providerKind,
                        endpoint: model.values.openAIBaseURL,
                        forceLocal: model.providerSelection == .onThisMac)
                }
            }
            .formStyle(.grouped)
            .scrollDisabled(true)
            .frame(minHeight: 520)
        }
    }

    private var writingStyleCard: some View {
        SettingsCard(searchID: "ai.writing-style") {
            VStack(alignment: .leading, spacing: 10) {
                Text("Writing style")
                    .cardTitle()
                Text(
                    "Scribe uses this default cleanup style unless an app profile overrides it. Edit per-app styles on the App profiles page. The selected instructions, recognized text and applicable vocabulary go to the selected model. Audio is never sent."
                )
                .cardDescription()

                HStack {
                    Spacer()
                    Button("Restore default") {
                        drafts.restoreCleanupWritingStyle()
                    }
                    .disabled(drafts.cleanupWritingStyle == CleanupPrompt.defaultWritingStyle)
                }
                promptEditor(
                    text: $drafts.cleanupWritingStyle,
                    accessibilityID: "ai.writing-style.editor",
                    height: 180)
            }
        }
    }

    private var guardrailPromptsCard: some View {
        SettingsCard(searchID: "ai.guardrails") {
            VStack(alignment: .leading, spacing: 14) {
                Text("Guardrail prompts")
                    .cardTitle()
                Text(
                    "These instructions set how Scribe handles cleanup requests. Local models use the local prompt; cloud models use the detailed prompt. Changes are saved with the Settings footer."
                )
                .cardDescription()

                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text("Detailed prompt")
                            .font(.headline)
                        Spacer()
                        Button("Restore default") {
                            drafts.cleanupFrontierPrompt = CleanupPrompt.defaultFrontierPrompt
                        }
                        .disabled(drafts.cleanupFrontierPrompt == CleanupPrompt.defaultFrontierPrompt)
                    }
                    promptEditor(
                        text: $drafts.cleanupFrontierPrompt,
                        accessibilityID: "ai.guardrails.detailed",
                        height: 150)
                }

                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text("Local prompt")
                            .font(.headline)
                        Spacer()
                        Button("Restore default") {
                            drafts.cleanupLocalPrompt = CleanupPrompt.defaultLocalPrompt
                        }
                        .disabled(drafts.cleanupLocalPrompt == CleanupPrompt.defaultLocalPrompt)
                    }
                    promptEditor(
                        text: $drafts.cleanupLocalPrompt,
                        accessibilityID: "ai.guardrails.local",
                        height: 150)
                }
            }
        }
    }

    private func promptEditor(
        text: Binding<String>,
        accessibilityID: String,
        height: CGFloat
    ) -> some View {
        TextEditor(text: text)
            .font(.system(.caption, design: .monospaced))
            .accessibilityIdentifier(accessibilityID)
            .frame(minHeight: height, maxHeight: height)
            .padding(4)
            .background(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(Color(nsColor: .textBackgroundColor))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .stroke(Color(nsColor: .separatorColor).opacity(0.4), lineWidth: 1)
            )
    }

    @ViewBuilder
    private var connectionRow: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Button(model.isTesting ? "Testing..." : "Test Connection") {
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

    private var enableStatusText: String {
        if !model.values.isEnabled {
            return "Off. Scribe types what it hears, with your dictionary and snippets."
        }
        if model.isTesting {
            return "On. Checking whether \(model.values.providerKind.providerName) is ready."
        }
        if !isConfigured {
            return "On, but not set up yet. Until it's ready, Scribe types what it hears."
        }
        return "On. Scribe will clean up text with \(model.values.providerKind.providerName) before typing."
    }

    private var isConfigured: Bool {
        !model.isDisabled(.connectionTest) || model.isTesting
    }
}
