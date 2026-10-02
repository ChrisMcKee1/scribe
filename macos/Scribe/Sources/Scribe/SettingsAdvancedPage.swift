import SwiftUI

struct SettingsAdvancedPage: View {
    private let newlineStore: AdvancedDictationSettingsStore
    @State private var newlineMode: NewlineInjectionMode
    @State private var speechModelAlias: String
    @State private var cachedSpeechModels: Set<String> = []
    @State private var speechModelStatus = "Checking Foundry Local model cache…"
    @State private var isDownloadingSpeechModel = false

    init(newlineStore: AdvancedDictationSettingsStore = .live) {
        self.newlineStore = newlineStore
        _newlineMode = State(initialValue: newlineStore.newlineMode)
        _speechModelAlias = State(initialValue: newlineStore.speechModelAlias)
    }

    var body: some View {
        SettingsPage(
            title: "Advanced",
            subtitle: "Settings most people never need to change. The defaults suit most Macs."
        ) {
            VStack(alignment: .leading, spacing: 14) {
                SettingsGroupHeader("Speech recognition")
                SettingsCard(searchID: "advanced.speech-model") { speechModelCard }
                SettingsCard(searchID: "advanced.threads") {
                    readOnlyCard(
                        title: "Processor threads",
                        value: "Automatic",
                        description:
                            "Scribe does not pass a processor-thread setting to Foundry Local. The foundry transcribe command chooses how to run on this Mac."
                    )
                }
                SettingsCard(searchID: "advanced.free-memory") {
                    readOnlyCard(
                        title: "Free memory when Scribe is not used",
                        value: "Managed by the recognizer",
                        description:
                            "Scribe starts a recognizer subprocess for each dictation and keeps only a warm status for timing. Foundry Local manages its own model cache."
                    )
                }
                Text("Changes to the recognizer backend take effect on the next dictation.")
                    .cardDescription()

                SettingsGroupHeader("Recording")
                SettingsCard(searchID: "advanced.trim-silence") {
                    readOnlyCard(
                        title: "Trim silence",
                        value: "No separate trim step",
                        description:
                            "macOS sends the captured recording to the recognizer as recorded. Silence auto-stop can end toggle and test dictations, but there is no separate silence trimming stage."
                    )
                }
                SettingsCard(searchID: "advanced.longest-recording") {
                    readOnlyCard(
                        title: "Longest recording",
                        value: "10 minutes",
                        description:
                            "A recording stops at ten minutes and Scribe types what it heard, so a stuck key cannot record forever."
                    )
                }

                SettingsGroupHeader("Typing into apps")
                SettingsCard(searchID: "advanced.typing-method") {
                    readOnlyCard(
                        title: "Typing method",
                        value: "Unicode keystrokes",
                        description:
                            "Scribe types Unicode keystrokes directly. The legacy Accessibility and paste path is not selected by the app."
                    )
                }
                SettingsCard(searchID: "advanced.line-breaks") { lineBreaksCard }
                SettingsCard(searchID: "advanced.chat-lines") {
                    readOnlyCard(
                        title: "Do not send chat messages early",
                        value: "On for typed fallback line breaks",
                        description:
                            "When Scribe has to type line breaks as keystrokes, it uses Shift-Return so chat apps such as Teams and Slack start a new line instead of sending."
                    )
                }

                SettingsGroupHeader("Text changes")
                SettingsCard(searchID: "advanced.text-changes") {
                    readOnlyCard(
                        title: "Apply your dictionary and snippets",
                        value: "On",
                        description:
                            "Every dictation runs through your dictionary, snippets and spacing fixes. When AI cleanup is on, Scribe applies vocabulary before the request and finishes snippets and template-style replacements after the reply."
                    )
                }
            }
        }
        .onAppear {
            newlineMode = newlineStore.newlineMode
            speechModelAlias = newlineStore.speechModelAlias
            Task { await refreshSpeechModelCache() }
        }
    }

    private var speechModelCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Speech model").cardTitle()
            Picker(
                "Speech model",
                selection: Binding(
                    get: { speechModelAlias },
                    set: { alias in
                        speechModelAlias = alias
                        newlineStore.speechModelAlias = alias
                        updateSpeechModelStatus()
                    })
            ) {
                ForEach(FoundrySpeechModelCatalog.choices) { choice in
                    Text(choice.title).tag(choice.alias)
                }
            }
            .pickerStyle(.menu)
            .frame(maxWidth: 420, alignment: .leading)
            .disabled(isDownloadingSpeechModel)
            Text(
                "Scribe passes this alias to Foundry Local. Choosing another model does not download it. Download it below before dictating; Scribe checks that a newly selected model is cached and never downloads it during dictation. Existing settings continue to use Parakeet TDT v2. If SCRIBE_WHISPER_CLI and SCRIBE_WHISPER_MODEL are set, the developer fallback is whisper.cpp with ggml-tiny.en."
            )
            .cardDescription()
            Text(speechModelStatus)
                .cardDescription()
            Button(isDownloadingSpeechModel ? "Downloading…" : "Download selected model") {
                Task { await downloadSelectedSpeechModel() }
            }
            .disabled(isDownloadingSpeechModel || cachedSpeechModels.contains(speechModelAlias))
        }
    }

    @MainActor
    private func refreshSpeechModelCache() async {
        guard let cliURL = TranscriptionBackendResolver.live().foundryExecutable() else {
            speechModelStatus = "Foundry Local is not installed. Model availability cannot be checked."
            return
        }
        do {
            cachedSpeechModels = try await FoundrySpeechModelCatalog.cachedAliases(cliURL: cliURL)
            updateSpeechModelStatus()
        } catch is CancellationError {
            speechModelStatus = "The model cache check was cancelled."
        } catch {
            speechModelStatus = "Foundry Local could not report its model cache. Try again later."
        }
    }

    private func updateSpeechModelStatus() {
        if cachedSpeechModels.contains(speechModelAlias) {
            speechModelStatus = "This model is downloaded and ready."
        } else if speechModelAlias == TranscriptionEngine.defaultFoundryModelAlias {
            speechModelStatus =
                "The default model is not cached. Foundry may download it on first use, or download it here."
        } else {
            speechModelStatus = "This newly selected model is not downloaded. Dictation will not download it."
        }
    }

    @MainActor
    private func downloadSelectedSpeechModel() async {
        guard let cliURL = TranscriptionBackendResolver.live().foundryExecutable() else {
            speechModelStatus = "Foundry Local is not installed."
            return
        }
        isDownloadingSpeechModel = true
        speechModelStatus = "Downloading the selected model. This may take a while."
        defer { isDownloadingSpeechModel = false }
        do {
            try await FoundrySpeechModelCatalog.download(alias: speechModelAlias, cliURL: cliURL)
            await refreshSpeechModelCache()
        } catch is CancellationError {
            speechModelStatus = "The model download was cancelled."
        } catch {
            speechModelStatus = "Foundry Local could not download this model. Check Foundry Local and try again."
        }
    }

    private var lineBreaksCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Line breaks").cardTitle()
            Text("In command-line apps such as Terminal, a line break works like Return and can send text early.")
                .cardDescription()
            Picker(
                "Line breaks",
                selection: Binding(
                    get: { newlineMode },
                    set: { mode in
                        newlineMode = mode
                        newlineStore.newlineMode = mode
                    })
            ) {
                Text("Smart flatten for terminals").tag(NewlineInjectionMode.smartFlatten)
                Text("Always flatten to spaces").tag(NewlineInjectionMode.alwaysFlatten)
                Text("Keep line breaks").tag(NewlineInjectionMode.keepNewlines)
            }
            .pickerStyle(.menu)
            .frame(maxWidth: 420, alignment: .leading)
            Text(description(for: newlineMode))
                .cardDescription()
        }
    }

    private func readOnlyCard(title: String, value: String, description: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).cardTitle()
            valuePill(value)
            Text(description).cardDescription()
        }
    }

    private func valuePill(_ value: String) -> some View {
        Text(value)
            .font(.callout.weight(.semibold))
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .background(
                Capsule(style: .continuous)
                    .fill(Color.accentColor.opacity(0.14)))
    }

    private func description(for mode: NewlineInjectionMode) -> String {
        switch mode {
        case .smartFlatten:
            return "Scribe keeps paragraphs in editors and flattens line breaks only for known terminal apps."
        case .alwaysFlatten:
            return "Scribe replaces every line break with a space before insertion."
        case .keepNewlines:
            return "Scribe keeps the line breaks produced by cleanup and snippets."
        }
    }
}
