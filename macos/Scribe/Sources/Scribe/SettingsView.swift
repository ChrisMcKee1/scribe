import AppKit
import Charts
import CoreGraphics
import SwiftUI
import UniformTypeIdentifiers

/// Full Settings window replacing the earlier static scaffold text. Sections mirror the feature
/// areas already implemented: overlay position, hotkey, dictionary, snippets, and per-app
/// profiles. Backed directly by `PersistenceStore` (no separate view-model layer yet; the store's
/// CRUD surface is already small and synchronous, matching the CLI verbs used to verify each
/// feature).
///
/// Uses a `NavigationSplitView` sidebar rather than `TabView`'s top segmented control: once the
/// section count reached double digits (Overlay/Hotkey/Dictionary/Libraries/Snippets/App
/// Profiles/AI Cleanup/Playground/Diagnostics/Usage Insights/About), the segmented strip truncated
/// labels and became unreadable. A sidebar `List` scales to any number of sections the same way
/// System Settings itself does.
enum SettingsSection: String, CaseIterable, Identifiable {
    case overlay
    case hotkey
    case dictionary
    case libraries
    case snippets
    case appProfiles
    case aiCleanup
    case playground
    case diagnostics
    case usageInsights
    case about

    var id: String { rawValue }

    var label: String {
        switch self {
        case .overlay: return "Overlay"
        case .hotkey: return "Hotkey"
        case .dictionary: return "Dictionary"
        case .libraries: return "Libraries"
        case .snippets: return "Snippets"
        case .appProfiles: return "App Profiles"
        case .aiCleanup: return "AI Cleanup"
        case .playground: return "Playground"
        case .diagnostics: return "Diagnostics"
        case .usageInsights: return "Usage Insights"
        case .about: return "About"
        }
    }

    var systemImage: String {
        switch self {
        case .overlay: return "rectangle.on.rectangle"
        case .hotkey: return "keyboard"
        case .dictionary: return "character.book.closed"
        case .libraries: return "books.vertical"
        case .snippets: return "text.append"
        case .appProfiles: return "app.badge"
        case .aiCleanup: return "sparkles"
        case .playground: return "wand.and.rays"
        case .diagnostics: return "waveform.path.ecg"
        case .usageInsights: return "chart.bar.xaxis"
        case .about: return "info.circle"
        }
    }
}

struct SettingsView: View {
    let persistenceStore: PersistenceStore
    let overlayPanelController: OverlayPanelController
    let pipelineReportStore: PipelineReportStore
    let dictionaryLibraryService: DictionaryLibraryService
    let onProfilesOrRulesChanged: () -> Void
    let onHotkeyChanged: (CGKeyCode) -> Void

    @State private var selection: SettingsSection? = .overlay

    var body: some View {
        NavigationSplitView {
            List(SettingsSection.allCases, selection: $selection) { section in
                Label(section.label, systemImage: section.systemImage)
                    .tag(section)
            }
            .navigationSplitViewColumnWidth(min: 160, ideal: 190, max: 220)
        } detail: {
            ScrollView {
                detailContent
                    .padding(20)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .frame(minWidth: 760, minHeight: 520)
    }

    @ViewBuilder
    private var detailContent: some View {
        switch selection ?? .overlay {
        case .overlay:
            OverlaySettingsTab(overlayPanelController: overlayPanelController)
        case .hotkey:
            HotkeySettingsTab(onHotkeyChanged: onHotkeyChanged)
        case .dictionary:
            DictionarySettingsTab(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
        case .libraries:
            DictionaryLibrariesSettingsTab(dictionaryLibraryService: dictionaryLibraryService, onChanged: onProfilesOrRulesChanged)
        case .snippets:
            SnippetsSettingsTab(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
        case .appProfiles:
            AppProfilesSettingsTab(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
        case .aiCleanup:
            CleanupSettingsTab()
        case .playground:
            PlaygroundSettingsTab(pipelineReportStore: pipelineReportStore)
        case .diagnostics:
            DiagnosticsSettingsTab(persistenceStore: persistenceStore)
        case .usageInsights:
            UsageInsightsSettingsTab(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
        case .about:
            AboutView(persistenceStore: persistenceStore)
        }
    }
}

// MARK: - Overlay tab

private struct OverlaySettingsTab: View {
    let overlayPanelController: OverlayPanelController
    @State private var selectedAnchor: OverlayAnchor = .bottomCenter

    private static let overlayAnchorDefaultsKey = "ScribeOverlayAnchor"

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Recording Pill Position")
                .font(.headline)
            Text("Choose where the recording pill appears on screen while dictating.")
                .foregroundStyle(.secondary)

            LazyVGrid(columns: Array(repeating: GridItem(.flexible()), count: 3), spacing: 8) {
                ForEach(OverlayAnchor.allCases, id: \.self) { anchor in
                    Button {
                        selectedAnchor = anchor
                        overlayPanelController.anchor = anchor
                        UserDefaults.standard.set(anchor.rawValue, forKey: Self.overlayAnchorDefaultsKey)
                    } label: {
                        Text(anchor.displayName)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 8)
                            .background(selectedAnchor == anchor ? Color.accentColor.opacity(0.25) : Color.gray.opacity(0.1))
                            .cornerRadius(6)
                    }
                    .buttonStyle(.plain)
                }
            }
            Spacer()
        }
        .onAppear {
            selectedAnchor = overlayPanelController.anchor
        }
    }
}

// MARK: - Hotkey tab

private struct HotkeySettingsTab: View {
    let onHotkeyChanged: (CGKeyCode) -> Void

    @State private var currentKeyCode: CGKeyCode = HotkeySettingsStore.keyCode
    @State private var isRecording = false
    @State private var localMonitor: Any?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Push-to-Talk Key")
                .font(.headline)
            Text(currentKeyCode == 57
                ? "Tap Caps Lock once to start dictating, and tap it again to stop, just like Caps Lock's own on/off light."
                : "Hold this key anywhere on your Mac to start dictating, and release it to stop.")
                .foregroundStyle(.secondary)
            // Input Monitoring is what a global push-to-talk key requires, and is easy to miss
            // since (unlike Microphone/Accessibility) macOS never shows a system prompt for it;
            // the user has to add Scribe manually. Surfaced here because "the key does nothing"
            // is otherwise indistinguishable from a wrong binding.
            Text("If the key does nothing at all, grant Scribe Input Monitoring access in System Settings > Privacy & Security > Input Monitoring, then relaunch Scribe.")
                .font(.footnote)
                .foregroundStyle(.secondary)

            HStack(spacing: 12) {
                Text(isRecording ? "Press any key..." : HotkeyKeyCodeCatalog.displayName(for: currentKeyCode))
                    .font(.title3.bold())
                    .padding(.horizontal, 14)
                    .padding(.vertical, 8)
                    .frame(minWidth: 160)
                    .background(
                        RoundedRectangle(cornerRadius: 8)
                            .fill(isRecording ? Color.accentColor.opacity(0.2) : Color.gray.opacity(0.12)))

                Button(isRecording ? "Cancel" : "Record New Key") {
                    if isRecording {
                        stopRecording()
                    } else {
                        startRecording()
                    }
                }

                if currentKeyCode != HotkeySettingsStore.defaultKeyCode {
                    Button("Reset to \(HotkeyKeyCodeCatalog.displayName(for: HotkeySettingsStore.defaultKeyCode))") {
                        apply(keyCode: HotkeySettingsStore.defaultKeyCode)
                    }
                }
            }

            Text("Common choices")
                .font(.subheadline.bold())
                .padding(.top, 4)
            LazyVGrid(columns: Array(repeating: GridItem(.flexible()), count: 3), spacing: 8) {
                ForEach(HotkeyKeyCodeCatalog.entries) { entry in
                    Button {
                        apply(keyCode: entry.keyCode)
                    } label: {
                        Text(entry.name)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 6)
                            .background(currentKeyCode == entry.keyCode ? Color.accentColor.opacity(0.25) : Color.gray.opacity(0.1))
                            .cornerRadius(6)
                    }
                    .buttonStyle(.plain)
                }
            }
            Spacer()
        }
        .onDisappear {
            stopRecording()
        }
    }

    /// Captures the next key press (modifier or regular key) within the Settings window only, via
    /// a local `NSEvent` monitor (no extra permission needed beyond the window having focus,
    /// unlike `HotkeyManager`'s system-wide `CGEvent` tap). Swallows the captured event so it
    /// never also types into the window.
    private func startRecording() {
        isRecording = true
        localMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged]) { event in
            let candidateKeyCode = CGKeyCode(event.keyCode)

            if event.type == .flagsChanged {
                // A flagsChanged event fires on both press AND release of a modifier key; only
                // treat this as a recording when the key is actually down right now, the same
                // check HotkeyManager itself uses to tell the two apart.
                guard CGEventSource.keyState(.combinedSessionState, key: candidateKeyCode) else {
                    return event
                }
            }

            apply(keyCode: candidateKeyCode)
            stopRecording()
            return nil
        }
    }

    private func stopRecording() {
        isRecording = false
        if let localMonitor {
            NSEvent.removeMonitor(localMonitor)
        }
        localMonitor = nil
    }

    private func apply(keyCode: CGKeyCode) {
        currentKeyCode = keyCode
        HotkeySettingsStore.keyCode = keyCode
        onHotkeyChanged(keyCode)
    }
}

// MARK: - Dictionary tab

private struct DictionarySettingsTab: View {
    let persistenceStore: PersistenceStore
    let onChanged: () -> Void

    @State private var entries: [DictionaryEntry] = []
    @State private var newPattern = ""
    @State private var newReplacement = ""
    @State private var errorMessage: String?
    @State private var statusMessage: String?
    @State private var isLearning = false
    @State private var isCleaning = false
    @State private var cleanupReport: DictionaryUsageReport?
    @State private var cleanupSelection: Set<Int64> = []

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("User Dictionary")
                .font(.headline)

            HStack {
                TextField("Spoken form (e.g. \"sherpa onnx\")", text: $newPattern)
                TextField("Written form (e.g. \"sherpa-onnx\")", text: $newReplacement)
                Button("Add", action: addEntry)
                    .disabled(newPattern.trimmingCharacters(in: .whitespaces).isEmpty
                        || newReplacement.trimmingCharacters(in: .whitespaces).isEmpty)
            }

            HStack {
                Button("Import CSV\u{2026}", action: importCsv)
                Button("Export CSV\u{2026}", action: exportCsv)
                    .disabled(entries.isEmpty)
                Button("Get Template\u{2026}", action: saveTemplate)
                Button("Learn from History", action: learnFromHistory)
                    .disabled(isLearning)
                Button("Clean Up\u{2026}", action: runCleanup)
                    .disabled(isCleaning)
                Spacer()
            }

            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }
            if let statusMessage {
                Text(statusMessage).foregroundStyle(.secondary).font(.caption)
            }

            List {
                ForEach(entries, id: \.id) { entry in
                    HStack {
                        Toggle("", isOn: binding(for: entry))
                            .labelsHidden()
                        Text(entry.pattern)
                        Image(systemName: "arrow.right")
                            .foregroundStyle(.secondary)
                        Text(entry.replacement)
                        Spacer()
                        Button(role: .destructive) {
                            deleteEntry(entry)
                        } label: {
                            Image(systemName: "trash")
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .onAppear(perform: reload)
        .sheet(isPresented: Binding(
            get: { cleanupReport != nil },
            set: { if !$0 { cleanupReport = nil } }
        )) {
            if let report = cleanupReport {
                DictionaryCleanupView(
                    report: report,
                    onApply: { idsToDisable in
                        applyCleanup(idsToDisable: idsToDisable)
                        cleanupReport = nil
                    },
                    onCancel: { cleanupReport = nil })
            }
        }
    }

    private func binding(for entry: DictionaryEntry) -> Binding<Bool> {
        Binding(
            get: { entry.enabled },
            set: { newValue in setEnabled(entry, enabled: newValue) })
    }

    private func reload() {
        do {
            entries = try persistenceStore.fetchAllDictionaryEntries()
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func addEntry() {
        do {
            _ = try persistenceStore.insertDictionaryEntry(
                DictionaryEntry(pattern: newPattern, replacement: newReplacement))
            newPattern = ""
            newReplacement = ""
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func setEnabled(_ entry: DictionaryEntry, enabled: Bool) {
        do {
            try persistenceStore.setDictionaryEntryEnabled(id: entry.id, enabled: enabled)
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func deleteEntry(_ entry: DictionaryEntry) {
        do {
            try persistenceStore.deleteDictionaryEntry(id: entry.id)
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    // MARK: - CSV import/export

    private func importCsv() {
        errorMessage = nil
        statusMessage = nil

        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.commaSeparatedText, .plainText]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.message = "Choose a dictionary CSV file to import."
        guard panel.runModal() == .OK, let url = panel.url else {
            return
        }

        do {
            let csv = try String(contentsOf: url, encoding: .utf8)
            let result = DictionaryCsv.parse(csv)

            let existingRows = entries.enumerated().map { index, entry in
                DictionaryImportMerger.ExistingRow(
                    index: index,
                    id: entry.id,
                    pattern: entry.pattern,
                    replacement: entry.replacement,
                    wholeWord: entry.wholeWord,
                    enabled: entry.enabled)
            }
            let plan = DictionaryImportMerger.merge(existing: existingRows, imported: result.entries)

            for operation in plan.operations {
                switch operation.kind {
                case .add:
                    _ = try persistenceStore.insertDictionaryEntry(operation.entry)
                case .update:
                    try persistenceStore.updateDictionaryEntry(operation.entry)
                }
            }

            reload()
            onChanged()

            var summary = "Imported: \(plan.added) added, \(plan.updated) updated, \(plan.unchanged) unchanged."
            if !result.errors.isEmpty {
                summary += " \(result.errors.count) row(s) skipped: \(result.errors.joined(separator: "; "))"
            }
            statusMessage = summary
        } catch {
            errorMessage = "Couldn't import that file: \(error.localizedDescription)"
        }
    }

    private func exportCsv() {
        errorMessage = nil
        statusMessage = nil

        let panel = NSSavePanel()
        panel.allowedContentTypes = [.commaSeparatedText]
        panel.nameFieldStringValue = "scribe-dictionary.csv"
        panel.message = "Choose where to save the exported dictionary."
        guard panel.runModal() == .OK, let url = panel.url else {
            return
        }

        do {
            let csv = DictionaryCsv.export(entries)
            try csv.write(to: url, atomically: true, encoding: .utf8)
            statusMessage = "Exported \(entries.count) entr\(entries.count == 1 ? "y" : "ies") to \(url.lastPathComponent)."
        } catch {
            errorMessage = "Couldn't export the dictionary: \(error.localizedDescription)"
        }
    }

    private func saveTemplate() {
        errorMessage = nil
        statusMessage = nil

        let panel = NSSavePanel()
        panel.allowedContentTypes = [.commaSeparatedText]
        panel.nameFieldStringValue = "scribe-dictionary-template.csv"
        panel.message = "Choose where to save the dictionary import template."
        guard panel.runModal() == .OK, let url = panel.url else {
            return
        }

        do {
            try DictionaryCsv.template.write(to: url, atomically: true, encoding: .utf8)
            statusMessage = "Saved the template to \(url.lastPathComponent)."
        } catch {
            errorMessage = "Couldn't save the template: \(error.localizedDescription)"
        }
    }

    // MARK: - Learn from history

    /// Mines recent dictation history for recurring jargon (`DictionaryHistoryLearner`) and adds
    /// any newly discovered entries. Guarded by `isLearning` because the mining scan, while quick,
    /// still shouldn't be re-entrant if the user clicks twice.
    private func learnFromHistory() {
        guard !isLearning else { return }
        isLearning = true
        errorMessage = nil
        statusMessage = nil

        defer { isLearning = false }

        do {
            let history = try persistenceStore.fetchDictationHistory()
            let learned = DictionaryHistoryLearner.buildEntries(history: history, existing: entries)

            guard !learned.isEmpty else {
                statusMessage = "No new recurring terms found in your dictation history yet."
                return
            }

            for entry in learned {
                _ = try persistenceStore.insertDictionaryEntry(entry)
            }

            reload()
            onChanged()
            statusMessage = "Learned \(learned.count) new entr\(learned.count == 1 ? "y" : "ies") from your dictation history."
        } catch {
            errorMessage = "Couldn't learn from history: \(error.localizedDescription)"
        }
    }

    // MARK: - Clean up unused entries

    /// Scans dictation history for entries that never appear, in either their spoken or written
    /// form (`DictionaryUsageAnalyzer`), and presents them for review. Nothing is disabled until
    /// the user confirms in the sheet.
    private func runCleanup() {
        guard !isCleaning else { return }
        isCleaning = true
        errorMessage = nil
        statusMessage = nil
        defer { isCleaning = false }

        do {
            let history = try persistenceStore.fetchDictationHistory()
            let transcripts = history.compactMap { $0.transcriptText }
            let report = DictionaryUsageAnalyzer.analyze(transcripts: transcripts, baseEntries: entries)

            guard report.hasFindings else {
                statusMessage = report.hasEnoughEvidence
                    ? "Every term in your dictionary turned up in your recent dictations. Nothing to clean up."
                    : report.summary
                return
            }

            cleanupReport = report
        } catch {
            errorMessage = "Couldn't check dictionary usage: \(error.localizedDescription)"
        }
    }

    /// Soft-disables the confirmed entries rather than deleting them, mirroring Windows: the
    /// evidence is a sample of recent history, not proof the term will never be needed again.
    private func applyCleanup(idsToDisable: Set<Int64>) {
        guard !idsToDisable.isEmpty else { return }
        do {
            for id in idsToDisable {
                try persistenceStore.setDictionaryEntryEnabled(id: id, enabled: false)
            }
            reload()
            onChanged()
            statusMessage = "Turned off \(idsToDisable.count) unused entr\(idsToDisable.count == 1 ? "y" : "ies")."
        } catch {
            errorMessage = "Couldn't update the dictionary: \(error.localizedDescription)"
        }
    }
}

/// Review sheet for `DictionaryUsageAnalyzer`'s findings. Shows the evidence behind every proposed
/// disable and never applies anything on its own: it returns a set of chosen ids, and the caller
/// is what actually writes to the store.
private struct DictionaryCleanupView: View {
    let report: DictionaryUsageReport
    let onApply: (Set<Int64>) -> Void
    let onCancel: () -> Void

    @State private var selected: Set<Int64>

    init(report: DictionaryUsageReport, onApply: @escaping (Set<Int64>) -> Void, onCancel: @escaping () -> Void) {
        self.report = report
        self.onApply = onApply
        self.onCancel = onCancel
        // Everything proposed starts checked; the user unchecks what they want to keep.
        _selected = State(initialValue: Set(report.unusedEntries.map { $0.entry.id }))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Clean Up Dictionary")
                .font(.headline)
            Text(report.summary)
                .font(.callout)
                .foregroundStyle(.secondary)

            List {
                ForEach(report.unusedEntries, id: \.entry.id) { usage in
                    HStack {
                        Toggle("", isOn: binding(for: usage.entry.id))
                            .labelsHidden()
                        VStack(alignment: .leading) {
                            Text("\"\(usage.entry.pattern)\" becomes \"\(usage.entry.replacement)\"")
                            Text(usage.entry.enabled
                                ? "Currently on. Neither wording came up in your recent dictations."
                                : "Already off. Neither wording came up in your recent dictations.")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                    }
                }
            }
            .frame(minHeight: 160)

            Text("Turning a term off is reversible: it stays in your dictionary with its tick "
                + "cleared and stops being applied. Nothing is written until you confirm below.")
                .font(.caption)
                .foregroundStyle(.secondary)

            HStack {
                Spacer()
                Button("Cancel", action: onCancel)
                Button("Turn Off Selected") {
                    onApply(selected)
                }
                .keyboardShortcut(.defaultAction)
                .disabled(selected.isEmpty)
            }
        }
        .padding()
        .frame(width: 460)
    }

    private func binding(for id: Int64) -> Binding<Bool> {
        Binding(
            get: { selected.contains(id) },
            set: { isOn in
                if isOn {
                    selected.insert(id)
                } else {
                    selected.remove(id)
                }
            })
    }
}

// MARK: - Dictionary Libraries tab

private struct DictionaryLibrariesSettingsTab: View {
    let dictionaryLibraryService: DictionaryLibraryService
    let onChanged: () -> Void

    @State private var libraries: [DictionaryLibrary] = []
    @State private var enabledIds: Set<String> = []
    @State private var errorMessage: String?
    @State private var statusMessage: String?
    @State private var showingImporter = false

    private var groupedByCategory: [(category: String, libraries: [DictionaryLibrary])] {
        var order: [String] = []
        var byCategory: [String: [DictionaryLibrary]] = [:]
        for library in libraries {
            if byCategory[library.category] == nil {
                order.append(library.category)
            }
            byCategory[library.category, default: []].append(library)
        }
        return order.map { ($0, byCategory[$0] ?? []) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Dictionary Libraries")
                .font(.headline)
            Text("Switch on ready-made glossaries to canonicalize domain vocabulary (Azure, GitHub, "
                + "programming languages, and more) without typing every term yourself. Library entries "
                + "never override your own dictionary.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack {
                Button("Import Library CSV\u{2026}") { showingImporter = true }
                Spacer()
            }

            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }
            if let statusMessage {
                Text(statusMessage).foregroundStyle(.secondary).font(.caption)
            }

            List {
                ForEach(groupedByCategory, id: \.category) { group in
                    Section(group.category) {
                        ForEach(group.libraries, id: \.id) { library in
                            HStack(alignment: .top) {
                                Toggle("", isOn: binding(for: library.id))
                                    .labelsHidden()
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(library.name).font(.body)
                                    if let description = library.description, !description.isEmpty {
                                        Text(description).font(.caption).foregroundStyle(.secondary)
                                    }
                                    Text("\(library.entries.count) term(s)")
                                        .font(.caption2)
                                        .foregroundStyle(.secondary)
                                }
                                Spacer()
                                if !library.builtIn {
                                    Button(role: .destructive) {
                                        removeLibrary(library)
                                    } label: {
                                        Image(systemName: "trash")
                                    }
                                    .buttonStyle(.plain)
                                }
                            }
                        }
                    }
                }
            }
        }
        .padding(.vertical, 4)
        .onAppear(perform: reload)
        .fileImporter(isPresented: $showingImporter, allowedContentTypes: [.commaSeparatedText, .plainText]) { result in
            importLibrary(result)
        }
    }

    private func binding(for id: String) -> Binding<Bool> {
        Binding(
            get: { enabledIds.contains(id) },
            set: { isOn in
                DictionaryLibrarySettingsStore.setEnabled(isOn, id: id)
                enabledIds = DictionaryLibrarySettingsStore.enabledLibraryIds
                onChanged()
            })
    }

    private func reload() {
        libraries = dictionaryLibraryService.libraries()
        enabledIds = DictionaryLibrarySettingsStore.enabledLibraryIds
    }

    private func importLibrary(_ result: Result<URL, Error>) {
        errorMessage = nil
        statusMessage = nil
        switch result {
        case .failure(let error):
            errorMessage = error.localizedDescription
        case .success(let url):
            let accessed = url.startAccessingSecurityScopedResource()
            defer { if accessed { url.stopAccessingSecurityScopedResource() } }
            do {
                let csv = try String(contentsOf: url, encoding: .utf8)
                let library = try dictionaryLibraryService.import(
                    csv: csv, suggestedName: url.deletingPathExtension().lastPathComponent)
                statusMessage = "Imported \"\(library.name)\" (\(library.entries.count) term(s))."
                reload()
            } catch {
                errorMessage = error.localizedDescription
            }
        }
    }

    private func removeLibrary(_ library: DictionaryLibrary) {
        errorMessage = nil
        do {
            try dictionaryLibraryService.remove(id: library.id)
            statusMessage = "Removed \"\(library.name)\"."
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - Snippets tab

private struct SnippetsSettingsTab: View {
    let persistenceStore: PersistenceStore
    let onChanged: () -> Void

    @State private var snippets: [Snippet] = []
    @State private var newPhrase = ""
    @State private var newTemplate = ""
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Voice Snippets")
                .font(.headline)

            HStack(alignment: .top) {
                TextField("Trigger phrase (e.g. \"sign off block\")", text: $newPhrase)
                TextEditor(text: $newTemplate)
                    .frame(height: 60)
                    .border(Color.gray.opacity(0.3))
                Button("Add", action: addSnippet)
                    .disabled(newPhrase.trimmingCharacters(in: .whitespaces).isEmpty
                        || newTemplate.trimmingCharacters(in: .whitespaces).isEmpty)
            }

            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }

            List {
                ForEach(snippets, id: \.id) { snippet in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Toggle("", isOn: binding(for: snippet))
                                .labelsHidden()
                            Text(snippet.phrase).fontWeight(.medium)
                            Spacer()
                            Button(role: .destructive) {
                                deleteSnippet(snippet)
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.plain)
                        }
                        Text(snippet.template)
                            .foregroundStyle(.secondary)
                            .font(.caption)
                    }
                }
            }
        }
        .onAppear(perform: reload)
    }

    private func binding(for snippet: Snippet) -> Binding<Bool> {
        Binding(
            get: { snippet.enabled },
            set: { newValue in setEnabled(snippet, enabled: newValue) })
    }

    private func reload() {
        do {
            snippets = try persistenceStore.fetchAllSnippets()
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func addSnippet() {
        do {
            _ = try persistenceStore.insertSnippet(Snippet(phrase: newPhrase, template: newTemplate))
            newPhrase = ""
            newTemplate = ""
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func setEnabled(_ snippet: Snippet, enabled: Bool) {
        do {
            try persistenceStore.setSnippetEnabled(id: snippet.id, enabled: enabled)
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func deleteSnippet(_ snippet: Snippet) {
        do {
            try persistenceStore.deleteSnippet(id: snippet.id)
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - App profiles tab

private struct AppProfilesSettingsTab: View {
    let persistenceStore: PersistenceStore
    let onChanged: () -> Void

    @State private var profiles: [AppProfile] = []
    @State private var newName = ""
    @State private var newBundleIdentifiers = ""
    @State private var newWritingStyle = ""
    @State private var newNewlineMode: NewlineInjectionMode = .smartFlatten
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Per-App Profiles")
                .font(.headline)
            Text("Override writing style or line-break handling for specific apps, matched by bundle identifier (e.g. com.apple.Terminal).")
                .foregroundStyle(.secondary)
                .font(.caption)

            VStack(alignment: .leading, spacing: 6) {
                TextField("Profile name (e.g. \"Terminal\")", text: $newName)
                TextField("Bundle identifiers, comma-separated", text: $newBundleIdentifiers)
                TextField("Writing style override (optional)", text: $newWritingStyle)
                Picker("Newline handling", selection: $newNewlineMode) {
                    Text("Smart Flatten").tag(NewlineInjectionMode.smartFlatten)
                    Text("Always Flatten").tag(NewlineInjectionMode.alwaysFlatten)
                    Text("Keep Newlines").tag(NewlineInjectionMode.keepNewlines)
                }
                Button("Add Profile", action: addProfile)
                    .disabled(newName.trimmingCharacters(in: .whitespaces).isEmpty
                        || newBundleIdentifiers.trimmingCharacters(in: .whitespaces).isEmpty)
            }

            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }

            List {
                ForEach(profiles, id: \.id) { profile in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Text(profile.name).fontWeight(.medium)
                            Spacer()
                            Button(role: .destructive) {
                                deleteProfile(profile)
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.plain)
                        }
                        Text(profile.bundleIdentifiers.joined(separator: ", "))
                            .foregroundStyle(.secondary)
                            .font(.caption)
                        if let writingStylePrompt = profile.writingStylePrompt, !writingStylePrompt.isEmpty {
                            Text("Style: \(writingStylePrompt)").font(.caption)
                        }
                    }
                }
            }
        }
        .onAppear(perform: reload)
    }

    private func reload() {
        do {
            profiles = try persistenceStore.fetchAppProfiles()
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func addProfile() {
        do {
            let bundleIdentifiers = newBundleIdentifiers
                .split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
            _ = try persistenceStore.insertAppProfile(AppProfile(
                name: newName,
                bundleIdentifiers: bundleIdentifiers,
                processNames: [],
                writingStylePrompt: newWritingStyle.isEmpty ? nil : newWritingStyle,
                newlineHandling: newNewlineMode))
            newName = ""
            newBundleIdentifiers = ""
            newWritingStyle = ""
            newNewlineMode = .smartFlatten
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func deleteProfile(_ profile: AppProfile) {
        do {
            try persistenceStore.deleteAppProfile(id: profile.id)
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - AI Cleanup tab

/// Settings surface for AI cleanup: turning it on, picking a provider, and configuring that
/// provider's connection details and credentials, replacing the original env-var-only
/// configuration story. Reads and writes `CleanupSettingsStore` directly (no separate view-model
/// layer, matching every other tab in this file); non-secret fields save on every change, while
/// the two secret fields (OpenAI-compatible API key, Azure service-principal client secret) are
/// explicit "Save"/"Clear" actions against Keychain so a partially-typed secret is never persisted.
private struct CleanupSettingsTab: View {
    @State private var isEnabled = CleanupSettingsStore.isEnabled
    @State private var providerKind = CleanupSettingsStore.providerKind
    @State private var foundryLocalModelAlias = CleanupSettingsStore.foundryLocalModelAlias
    @State private var ollamaModel = CleanupSettingsStore.ollamaModel
    @State private var openAIBaseURL = CleanupSettingsStore.openAIBaseURL
    @State private var openAIModel = CleanupSettingsStore.openAIModel
    @State private var openAIApiKeyInput = ""
    @State private var hasSavedOpenAIApiKey = CleanupSettingsStore.openAIApiKey() != nil
    @State private var azureEndpoint = CleanupSettingsStore.azureEndpoint
    @State private var azureDeployment = CleanupSettingsStore.azureDeployment
    @State private var azureAuthMode = CleanupSettingsStore.azureAuthMode
    @State private var azureTenantId = CleanupSettingsStore.azureTenantId
    @State private var azureClientId = CleanupSettingsStore.azureClientId
    @State private var azureClientSecretInput = ""
    @State private var hasSavedAzureClientSecret = false
    @State private var statusMessage: String?
    @State private var errorMessage: String?
    @State private var isTesting = false

    var body: some View {
        Form {
            Section {
                Toggle("Enable AI Cleanup", isOn: $isEnabled)
                    .onChange(of: isEnabled) { _ in CleanupSettingsStore.isEnabled = isEnabled }
                Text("Cleans up punctuation and phrasing after each dictation using a locally or "
                    + "remotely hosted model. Strictly opt-in and off by default: only the "
                    + "transcribed text is ever sent to a cleanup provider, never audio.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Provider") {
                Picker("Provider", selection: $providerKind) {
                    ForEach(CleanupProviderKind.allCases) { kind in
                        Text(kind.displayName).tag(kind)
                    }
                }
                .onChange(of: providerKind) { _ in CleanupSettingsStore.providerKind = providerKind }
                .disabled(!isEnabled)
            }

            providerConfigurationSection

            Section {
                HStack {
                    Button(isTesting ? "Testing\u{2026}" : "Test Connection", action: testConnection)
                        .disabled(isTesting || !CleanupSettingsStore.isConfigured(for: providerKind))
                    if isTesting {
                        ProgressView().controlSize(.small)
                    }
                    Spacer()
                }
                if let errorMessage {
                    Text(errorMessage).foregroundStyle(.red).font(.caption)
                } else if let statusMessage {
                    Text(statusMessage).foregroundStyle(.secondary).font(.caption)
                }
            }
        }
        .formStyle(.grouped)
        .disabled(!isEnabled)
        .onAppear { refreshAzureSecretState() }
    }

    @ViewBuilder
    private var providerConfigurationSection: some View {
        switch providerKind {
        case .foundryLocal:
            Section("Foundry Local") {
                TextField("Model alias", text: $foundryLocalModelAlias)
                    .onChange(of: foundryLocalModelAlias) { _ in
                        CleanupSettingsStore.foundryLocalModelAlias = foundryLocalModelAlias
                    }
                Text("Runs fully on-device via Foundry Local. Requires "
                    + "'brew install microsoft/foundrylocal/foundrylocal'; the model downloads on "
                    + "first use.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        case .ollama:
            Section("Local model (Ollama managed)") {
                TextField("Model", text: $ollamaModel)
                    .onChange(of: ollamaModel) { _ in CleanupSettingsStore.ollamaModel = ollamaModel }
                Text("Runs fully on-device via a local Ollama installation "
                    + "(http://127.0.0.1:11434). Appropriate if you already run Ollama for other tools.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        case .openAICompatible:
            Section("OpenAI-compatible endpoint") {
                TextField("Base URL (e.g. http://localhost:1234)", text: $openAIBaseURL)
                    .onChange(of: openAIBaseURL) { _ in CleanupSettingsStore.openAIBaseURL = openAIBaseURL }
                TextField("Model", text: $openAIModel)
                    .onChange(of: openAIModel) { _ in CleanupSettingsStore.openAIModel = openAIModel }
                SecureField(
                    hasSavedOpenAIApiKey ? "API key saved (leave blank to keep)" : "API key (optional)",
                    text: $openAIApiKeyInput)
                HStack {
                    Button("Save Key", action: saveOpenAIApiKey)
                        .disabled(openAIApiKeyInput.isEmpty)
                    if hasSavedOpenAIApiKey {
                        Button("Clear Key", role: .destructive, action: clearOpenAIApiKey)
                    }
                }
                Text("For LM Studio, OpenRouter, or any other OpenAI-compatible server. The API "
                    + "key, if any, is stored in Keychain, never in plain text.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        case .microsoftFoundry:
            Section("Microsoft Foundry (cloud)") {
                TextField("Endpoint (e.g. https://my-resource.cognitiveservices.azure.com)", text: $azureEndpoint)
                    .onChange(of: azureEndpoint) { _ in CleanupSettingsStore.azureEndpoint = azureEndpoint }
                TextField("Deployment name", text: $azureDeployment)
                    .onChange(of: azureDeployment) { _ in CleanupSettingsStore.azureDeployment = azureDeployment }
                Picker("Authentication", selection: $azureAuthMode) {
                    Text("Azure CLI (az login)").tag(AzureAuthMode.azureCli)
                    Text("Service principal").tag(AzureAuthMode.servicePrincipal)
                }
                .onChange(of: azureAuthMode) { _ in CleanupSettingsStore.azureAuthMode = azureAuthMode }

                if azureAuthMode == .servicePrincipal {
                    TextField("Tenant ID", text: $azureTenantId)
                        .onChange(of: azureTenantId) { _ in CleanupSettingsStore.azureTenantId = azureTenantId }
                    TextField("Client ID", text: $azureClientId)
                        .onChange(of: azureClientId) { _ in
                            CleanupSettingsStore.azureClientId = azureClientId
                            refreshAzureSecretState()
                        }
                    SecureField(
                        hasSavedAzureClientSecret ? "Client secret saved (leave blank to keep)" : "Client secret",
                        text: $azureClientSecretInput)
                    HStack {
                        Button("Save Secret", action: saveAzureClientSecret)
                            .disabled(azureClientSecretInput.isEmpty
                                || azureClientId.trimmingCharacters(in: .whitespaces).isEmpty)
                        if hasSavedAzureClientSecret {
                            Button("Clear Secret", role: .destructive, action: clearAzureClientSecret)
                        }
                    }
                    Text("The client secret is stored in Keychain, never in an environment "
                        + "variable, a plist, or a script.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } else {
                    Text("Uses the signed-in 'az login' session on this Mac. Install the Azure "
                        + "CLI and run 'az login' once.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    private func refreshAzureSecretState() {
        hasSavedAzureClientSecret = CleanupSettingsStore.azureClientSecret(clientId: azureClientId) != nil
    }

    private func saveOpenAIApiKey() {
        do {
            try CleanupSettingsStore.setOpenAIApiKey(openAIApiKeyInput)
            openAIApiKeyInput = ""
            hasSavedOpenAIApiKey = true
            statusMessage = "API key saved to Keychain."
            errorMessage = nil
        } catch {
            errorMessage = "Failed to save API key: \(error.localizedDescription)"
        }
    }

    private func clearOpenAIApiKey() {
        do {
            try CleanupSettingsStore.setOpenAIApiKey(nil)
            hasSavedOpenAIApiKey = false
            statusMessage = "API key removed."
            errorMessage = nil
        } catch {
            errorMessage = "Failed to remove API key: \(error.localizedDescription)"
        }
    }

    private func saveAzureClientSecret() {
        do {
            try CleanupSettingsStore.setAzureClientSecret(azureClientSecretInput, clientId: azureClientId)
            azureClientSecretInput = ""
            hasSavedAzureClientSecret = true
            statusMessage = "Client secret saved to Keychain."
            errorMessage = nil
        } catch {
            errorMessage = "Failed to save client secret: \(error.localizedDescription)"
        }
    }

    private func clearAzureClientSecret() {
        do {
            try CleanupSettingsStore.setAzureClientSecret(nil, clientId: azureClientId)
            hasSavedAzureClientSecret = false
            statusMessage = "Client secret removed."
            errorMessage = nil
        } catch {
            errorMessage = "Failed to remove client secret: \(error.localizedDescription)"
        }
    }

    /// Builds a provider from current settings and runs its cheap reachability check. Uses
    /// `CleanupProviderResolver`, so an env-var override (if one happens to be set) is tested
    /// exactly the same way the live dictation pipeline would resolve it, avoiding a UI that lies
    /// about which provider is actually about to run.
    private func testConnection() {
        isTesting = true
        statusMessage = nil
        errorMessage = nil
        Task {
            do {
                let provider = try CleanupProviderResolver.tryResolveDefaultProvider()
                let snapshot = await provider.healthSnapshot()
                isTesting = false
                if snapshot.reachable {
                    statusMessage = "\(provider.displayName): \(snapshot.detail)"
                } else {
                    errorMessage = "\(provider.displayName): \(snapshot.detail)"
                }
            } catch {
                isTesting = false
                errorMessage = error.localizedDescription
            }
        }
    }
}

// MARK: - Playground tab

/// Live view of the last dictation run through the full pipeline: raw recognition, replacement
/// highlights, and per-step timings. Mirrors Windows' Playground panel (see
/// src/Scribe.App/Settings/SettingsWindow.xaml, "Playground" section), which is populated from
/// `DictationController.PipelineReported`. On macOS the analogous signal is `PipelineReportStore`,
/// published from `AppDelegate.transcribeAndInject` after every real dictation (hotkey or the
/// "Start Test Dictation" menu item) — there is no separate "Run" button here because macOS's
/// push-to-talk hotkey already works regardless of which window is focused, so simply dictating
/// normally while this tab is open is enough to see a report land.
private struct PlaygroundSettingsTab: View {
    @ObservedObject var pipelineReportStore: PipelineReportStore

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Playground")
                    .font(.headline)
                Text("Dictate normally (hotkey or \"Start Test Dictation\") while this tab is open to see the raw transcript, dictionary/snippet replacements, and per-step timings for the most recent run.")
                    .foregroundStyle(.secondary)

                if let report = pipelineReportStore.latest {
                    if let failureStage = report.failureStage {
                        Label("Failed at \(failureStage.rawValue): \(report.failureReason ?? "unknown error")", systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.red)
                    }

                    GroupBox("Raw Recognition") {
                        Text(report.rawText?.isEmpty == false ? report.rawText! : "(no speech recognized)")
                            .font(.system(.body, design: .monospaced))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.vertical, 4)
                    }

                    GroupBox("Processed Text (Replacements Highlighted)") {
                        highlightedText(for: report.postProcessing)
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.vertical, 4)
                    }

                    GroupBox("Timings") {
                        VStack(alignment: .leading, spacing: 4) {
                            timingRow("Capture", report.captureDuration)
                            timingRow("Speech Recognition (Decode)", report.decodeDuration)
                            timingRow("Dictionary / Snippets", report.postProcessingDuration)
                            if let cleanupDuration = report.cleanupDuration {
                                timingRow(report.cleanupApplied ? "AI Cleanup" : "AI Cleanup (failed, raw text used)", cleanupDuration)
                            }
                            timingRow("Text Insertion", report.injectionDuration)
                            Divider()
                            timingRow("Total", report.totalDuration)
                            if let rtf = report.realTimeFactor {
                                Text("Real-time factor: \(String(format: "%.2fx", rtf))")
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.vertical, 4)
                    }

                    // NOTE: There is no timing row here for "AI cleanup" or a Silero-style "VAD
                    // decode" stage. macOS's live pipeline does not yet call AI cleanup (only
                    // reachable via the `--cleanup-text` CLI verb), and macOS's capture uses an
                    // energy-threshold auto-stop detector rather than a true VAD model with a
                    // discrete inference step to time. See PORTING-PLAN.md for tracking.
                } else {
                    Text("No dictation captured yet this session.")
                        .foregroundStyle(.secondary)
                        .padding(.top, 8)
                }
            }
            .padding(.vertical, 8)
        }
    }

    private func timingRow(_ label: String, _ duration: TimeInterval?) -> some View {
        HStack {
            Text(label)
            Spacer()
            if let duration {
                Text(String(format: "%.0f ms", duration * 1_000))
                    .foregroundStyle(.secondary)
            } else {
                Text("—")
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func highlightedText(for result: TextPostProcessingResult?) -> Text {
        guard let result, !result.text.isEmpty else {
            return Text("(no text)")
        }
        guard !result.replacements.isEmpty else {
            return Text(result.text).font(.system(.body, design: .monospaced))
        }

        let nsText = result.text as NSString
        var segments: [Text] = []
        var cursor = 0
        for replacement in result.replacements.sorted(by: { $0.start < $1.start }) {
            guard replacement.start >= cursor, replacement.start + replacement.length <= nsText.length else { continue }
            if replacement.start > cursor {
                segments.append(Text(nsText.substring(with: NSRange(location: cursor, length: replacement.start - cursor))))
            }
            let highlighted = nsText.substring(with: NSRange(location: replacement.start, length: replacement.length))
            let color: Color = replacement.kind == .dictionary ? .blue : .green
            segments.append(Text(highlighted).foregroundColor(color).underline())
            cursor = replacement.start + replacement.length
        }
        if cursor < nsText.length {
            segments.append(Text(nsText.substring(from: cursor)))
        }

        return segments.reduce(Text("")) { partial, next in partial + next }
            .font(.system(.body, design: .monospaced))
    }
}

// MARK: - Diagnostics tab

/// Read-only performance panel over `dictation_history`, mirroring Windows' Diagnostics tab
/// (P50/P95 decode latency, real-time factor). Computed with `DictationStats.compute`, the same
/// aggregation used by `Scribe --diagnostics` for headless verification.
private struct DiagnosticsSettingsTab: View {
    let persistenceStore: PersistenceStore

    @State private var snapshot: DictationStats.Snapshot?
    @State private var errorMessage: String?
    @State private var windowDays: Double = 7

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("Performance")
                    .font(.headline)
                Spacer()
                Picker("Window", selection: $windowDays) {
                    Text("24 hours").tag(1.0)
                    Text("7 days").tag(7.0)
                    Text("30 days").tag(30.0)
                }
                .pickerStyle(.segmented)
                .frame(width: 260)
                .onChange(of: windowDays) { _ in reload() }
            }

            if let errorMessage {
                Text(errorMessage)
                    .foregroundStyle(.red)
            }

            if let snapshot {
                ScrollView {
                    VStack(alignment: .leading, spacing: 12) {
                        metricRow(label: "Dictations", value: "\(snapshot.count)")
                        metricRow(
                            label: "Total audio",
                            value: String(format: "%.1f s (longest %.1f s)", snapshot.totalAudioSeconds, snapshot.longestAudioSeconds))

                        Divider()

                        if let decodeMs = snapshot.decodeMs {
                            Text("Decode latency (ms)")
                                .font(.subheadline.bold())
                            metricRow(label: "Average", value: String(format: "%.0f", decodeMs.average))
                            metricRow(label: "P50", value: String(format: "%.0f", decodeMs.p50))
                            metricRow(label: "P95", value: String(format: "%.0f", decodeMs.p95))
                            metricRow(label: "Min / Max", value: String(format: "%.0f / %.0f", decodeMs.min, decodeMs.max))

                            Divider()

                            Text("Real-time factor")
                                .font(.subheadline.bold())
                            metricRow(label: "Fastest", value: String(format: "%.3fx", snapshot.fastestRtf))
                            metricRow(label: "P50", value: String(format: "%.3fx", snapshot.rtfP50))
                            metricRow(label: "P95", value: String(format: "%.3fx", snapshot.rtfP95))
                        } else {
                            Text("No timed dictations yet. Decode latency is recorded starting with the next dictation.")
                                .foregroundStyle(.secondary)
                        }

                        if let cleanupMs = snapshot.cleanupMs {
                            Divider()
                            Text("AI cleanup latency (ms)")
                                .font(.subheadline.bold())
                            metricRow(label: "Average", value: String(format: "%.0f", cleanupMs.average))
                            metricRow(label: "Min / Max", value: String(format: "%.0f / %.0f", cleanupMs.min, cleanupMs.max))
                        }
                    }
                }
            } else {
                Text("No dictations in this window yet.")
                    .foregroundStyle(.secondary)
            }

            Spacer()
        }
        .onAppear(perform: reload)
    }

    private func metricRow(label: String, value: String) -> some View {
        HStack {
            Text(label)
                .foregroundStyle(.secondary)
            Spacer()
            Text(value)
                .monospacedDigit()
        }
    }

    private func reload() {
        do {
            let history = try persistenceStore.fetchDictationHistory()
            let since = Date().addingTimeInterval(-windowDays * 86400)
            snapshot = DictationStats.compute(entries: history, since: since)
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - Usage Insights tab

/// Local-only usage totals, a trend chart, top apps, and recurring-term mining, backed by
/// `UsageAnalyzer`. The AI summary section is the one part of this tab that leaves the device: it
/// is opt-in per generation (never automatic) and sends only the aggregate `UsageInsight` payload
/// (counts and dictionary-covered term labels), never raw transcripts. Mirrors Windows' Usage
/// Insights page, split across the totals/top-apps/recurring-terms/AI-summary PORTING-PLAN rows.
private struct UsageInsightsSettingsTab: View {
    let persistenceStore: PersistenceStore
    let onChanged: () -> Void

    @State private var snapshot: UsageAnalyzer.Snapshot?
    @State private var errorMessage: String?
    @State private var windowDays: Double = 30

    @State private var aiSummaryText: String?
    @State private var aiSummaryError: String?
    @State private var isGeneratingSummary = false

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("Usage Insights")
                    .font(.headline)
                Spacer()
                Picker("Window", selection: $windowDays) {
                    Text("7 days").tag(7.0)
                    Text("30 days").tag(30.0)
                    Text("90 days").tag(90.0)
                }
                .pickerStyle(.segmented)
                .frame(width: 260)
                .onChange(of: windowDays) { _ in reload() }
            }

            if let errorMessage {
                Text(errorMessage)
                    .foregroundStyle(.red)
            }

            if let snapshot {
                ScrollView {
                    VStack(alignment: .leading, spacing: 16) {
                        totalsSection(snapshot)
                        Divider()
                        trendSection(snapshot)
                        Divider()
                        topAppsSection(snapshot)
                        Divider()
                        termsSection(snapshot)
                        Divider()
                        aiSummarySection(snapshot)
                    }
                }
            } else {
                Text("No dictations in this window yet.")
                    .foregroundStyle(.secondary)
            }

            Spacer()
        }
        .onAppear(perform: reload)
    }

    // MARK: Totals

    private func totalsSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Totals")
                .font(.subheadline.bold())
            metricRow(label: "Dictations", value: "\(snapshot.dictations)")
            metricRow(label: "Words", value: "\(snapshot.words)")
            metricRow(label: "Active days", value: "\(snapshot.activeDays)")
            metricRow(label: "Speech time", value: String(format: "%.1f min", snapshot.speechSeconds / 60.0))
            metricRow(label: "Average words / dictation", value: String(format: "%.1f", snapshot.averageWords))
        }
    }

    // MARK: Trend

    private func trendSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(snapshot.granularity == .daily ? "Trend (daily)" : "Trend (weekly)")
                .font(.subheadline.bold())
            if snapshot.trend.isEmpty {
                Text("Not enough history to chart a trend yet.")
                    .foregroundStyle(.secondary)
            } else {
                Chart(snapshot.trend, id: \.start) { point in
                    BarMark(
                        x: .value("Period", String(format: "%04d-%02d-%02d", point.start.year, point.start.month, point.start.day)),
                        y: .value("Dictations", point.dictations))
                }
                .frame(height: 160)
            }
        }
    }

    // MARK: Top apps

    private func topAppsSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Top apps")
                .font(.subheadline.bold())
            if snapshot.topApps.isEmpty {
                Text("No app usage recorded yet.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(snapshot.topApps, id: \.name) { app in
                    HStack {
                        Text(app.name)
                        Spacer()
                        Text("\(app.dictations) dictations, \(app.words) words")
                            .foregroundStyle(.secondary)
                            .monospacedDigit()
                    }
                }
            }
        }
    }

    // MARK: Recurring terms

    private func termsSection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Recurring terms")
                .font(.subheadline.bold())
            if snapshot.terms.isEmpty {
                Text("No recurring terms found yet.")
                    .foregroundStyle(.secondary)
            } else {
                ForEach(snapshot.terms, id: \.text) { term in
                    HStack {
                        Text(term.text)
                        Text("(\(term.dictations) dictations, \(term.occurrences)x)")
                            .foregroundStyle(.secondary)
                            .font(.caption)
                        Spacer()
                        if term.covered {
                            Text("In dictionary")
                                .foregroundStyle(.secondary)
                                .font(.caption)
                        } else {
                            Button("Add to Dictionary") { addTermToDictionary(term) }
                        }
                    }
                }
            }
        }
    }

    private func addTermToDictionary(_ term: UsageAnalyzer.TermUsage) {
        do {
            _ = try persistenceStore.insertDictionaryEntry(
                DictionaryEntry(pattern: term.text, replacement: term.text, wholeWord: true, enabled: true))
            onChanged()
            reload()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    // MARK: AI summary (opt-in, sends aggregate counts only, never raw transcripts)

    private func aiSummarySection(_ snapshot: UsageAnalyzer.Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("AI summary")
                .font(.subheadline.bold())
            Text("Sends only aggregate totals and dictionary-covered term labels to your configured AI cleanup provider. Novel terms and raw transcripts never leave this device.")
                .font(.caption)
                .foregroundStyle(.secondary)

            HStack {
                Button(isGeneratingSummary ? "Generating..." : "Generate AI Summary") {
                    generateSummary(snapshot)
                }
                .disabled(isGeneratingSummary)
                if isGeneratingSummary {
                    ProgressView()
                        .controlSize(.small)
                }
            }

            if let aiSummaryError {
                Text(aiSummaryError)
                    .foregroundStyle(.red)
            }

            if let aiSummaryText {
                Text(aiSummaryText)
                    .textSelection(.enabled)
                    .padding(.top, 4)
            }
        }
    }

    private func generateSummary(_ snapshot: UsageAnalyzer.Snapshot) {
        isGeneratingSummary = true
        aiSummaryError = nil
        let payload = UsageInsight.buildSummary(snapshot)
        Task {
            do {
                let provider = CleanupProviderResolver.resolveDefaultProvider()
                let response = try await provider.clean(
                    CleanupRequest(transcript: payload, writingStylePrompt: UsageInsight.systemPrompt))
                let parsed = UsageInsight.parse(response.cleanedText)
                await MainActor.run {
                    aiSummaryText = parsed ?? "The AI provider returned no usable summary."
                    isGeneratingSummary = false
                }
            } catch {
                await MainActor.run {
                    aiSummaryError = error.localizedDescription
                    isGeneratingSummary = false
                }
            }
        }
    }

    private func metricRow(label: String, value: String) -> some View {
        HStack {
            Text(label)
                .foregroundStyle(.secondary)
            Spacer()
            Text(value)
                .monospacedDigit()
        }
    }

    private func reload() {
        do {
            let history = try persistenceStore.fetchDictationHistory()
            let knownTerms = try persistenceStore.fetchAllDictionaryEntries()
            let now = Date()
            let since = now.addingTimeInterval(-windowDays * 86400)
            let entries = history.map {
                UsageAnalyzer.Entry(
                    timestampUtc: $0.startedAt,
                    text: $0.transcriptText ?? "",
                    audioMilliseconds: $0.audioMilliseconds,
                    targetApp: $0.targetApp)
            }
            snapshot = UsageAnalyzer.compute(entries: entries, knownTerms: knownTerms, sinceUtc: since, nowUtc: now)
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
