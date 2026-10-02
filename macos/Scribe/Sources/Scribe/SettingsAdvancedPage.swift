import SwiftUI

struct SettingsAdvancedPage: View {
    private let newlineStore: AdvancedDictationSettingsStore
    @State private var newlineMode: NewlineInjectionMode
    @State private var speechModelAlias: String
    @State private var speechModels: [FoundrySpeechModelChoice] = []
    @State private var speechModelCatalogLoaded = false
    @State private var speechModelTask: Task<Void, Never>?
    @State private var speechModelStatus = "Checking Foundry Local model cache…"
    @State private var speechModelCatalogProblem: String?
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
            startSpeechModelRefresh()
        }
        .onDisappear { cancelSpeechModelTask() }
        .onReceive(NotificationCenter.default.publisher(for: SettingsWindowController.willCloseNotification)) { _ in
            cancelSpeechModelTask()
        }
    }

    private var speechModelChoices: [FoundrySpeechModelChoice] {
        FoundrySpeechModelCatalog.choices(from: speechModels, preserving: speechModelAlias)
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
                ForEach(speechModelChoices) { choice in
                    Text(choice.title).tag(choice.alias)
                }
            }
            .pickerStyle(.menu)
            .frame(maxWidth: 420, alignment: .leading)
            .disabled(isDownloadingSpeechModel || !speechModelCatalogLoaded)
            Text(
                "Scribe reads speech model choices from Foundry Local. Choosing a model does not download it. Download an uncached choice here; dictation never downloads a newly selected model. Existing settings continue to use Parakeet TDT v2. If SCRIBE_WHISPER_CLI and SCRIBE_WHISPER_MODEL are set, the developer fallback is whisper.cpp with ggml-tiny.en."
            )
            .cardDescription()
            Text(speechModelStatus)
                .cardDescription()
            HStack {
                Button("Refresh model list") { startSpeechModelRefresh() }
                    .disabled(isDownloadingSpeechModel)
                Button(isDownloadingSpeechModel ? "Downloading…" : "Download selected model") {
                    startSpeechModelDownload()
                }
                .disabled(!canDownloadSelectedSpeechModel)
            }
        }
    }

    private var canDownloadSelectedSpeechModel: Bool {
        guard !isDownloadingSpeechModel, speechModelCatalogLoaded,
            let choice = speechModels.first(where: { $0.alias == speechModelAlias })
        else {
            return false
        }
        return choice.isCached == false
    }

    private func startSpeechModelRefresh() {
        speechModelTask?.cancel()
        speechModelTask = Task { @MainActor in
            await refreshSpeechModelList()
        }
    }

    private func cancelSpeechModelTask() {
        speechModelTask?.cancel()
        speechModelTask = nil
    }

    private func startSpeechModelDownload() {
        speechModelTask?.cancel()
        speechModelTask = Task { @MainActor in
            await downloadSelectedSpeechModel()
        }
    }

    @MainActor
    private func refreshSpeechModelList() async {
        guard let cliURL = TranscriptionBackendResolver.live().foundryExecutable() else {
            speechModelCatalogLoaded = false
            speechModelCatalogProblem = "Foundry Local is not installed."
            speechModelStatus = "Foundry Local is not installed. Model availability cannot be checked."
            return
        }
        do {
            speechModels = try await AuxiliaryOperations.shared.run {
                try await FoundrySpeechModelCatalog.list(cliURL: cliURL)
            }
            speechModelCatalogLoaded = true
            speechModelCatalogProblem = nil
            updateSpeechModelStatus()
        } catch is CancellationError {
            speechModelStatus = "The model list check was cancelled."
        } catch AuxiliaryOperations.Refusal.closed {
            speechModelCatalogProblem = "Scribe is quitting."
            speechModelStatus = "Scribe is quitting. The selected model has not changed."
        } catch {
            speechModelCatalogLoaded = false
            speechModelCatalogProblem = "Foundry Local could not list speech models."
            speechModelStatus = "Foundry Local could not list speech models. Your saved selection is unchanged."
        }
    }

    private func updateSpeechModelStatus() {
        guard speechModelCatalogLoaded else {
            speechModelStatus =
                speechModelCatalogProblem
                .map { "\($0) Your saved selection is unchanged." }
                ?? "Waiting for Foundry Local's speech model list."
            return
        }
        guard let choice = speechModels.first(where: { $0.alias == speechModelAlias }) else {
            speechModelStatus =
                "Foundry Local does not list this saved alias. It is preserved; choose a listed model to use or download."
            return
        }
        if choice.isCached == true {
            speechModelStatus = "This model is downloaded and ready."
        } else if choice.isCached == false {
            if speechModelAlias == TranscriptionEngine.defaultFoundryModelAlias {
                speechModelStatus =
                    "The default model is not downloaded. Foundry may download it on first use, as before; you can download it here."
            } else {
                speechModelStatus = "This newly selected model is not downloaded. Download it before dictating."
            }
        } else {
            speechModelStatus = "Foundry Local did not report whether this model is downloaded."
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
            let models = try await AuxiliaryOperations.shared.run {
                try await FoundrySpeechModelCatalog.download(alias: speechModelAlias, cliURL: cliURL)
                try Task.checkCancellation()
                return try await FoundrySpeechModelCatalog.list(cliURL: cliURL)
            }
            speechModels = models
            speechModelCatalogLoaded = true
            speechModelCatalogProblem = nil
            updateSpeechModelStatus()
        } catch is CancellationError {
            speechModelStatus = "The model download was cancelled."
        } catch AuxiliaryOperations.Refusal.closed {
            speechModelStatus = "Scribe is quitting. The model download did not start."
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
