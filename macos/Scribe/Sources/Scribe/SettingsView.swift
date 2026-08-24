import AppKit
import Charts
import SwiftUI
import UniformTypeIdentifiers

/// Full Settings window replacing the earlier static scaffold text. Tabs mirror the feature areas
/// already implemented: overlay position, dictionary, snippets, and per-app profiles. Backed
/// directly by `PersistenceStore` (no separate view-model layer yet; the store's CRUD surface is
/// already small and synchronous, matching the CLI verbs used to verify each feature).
struct SettingsView: View {
    let persistenceStore: PersistenceStore
    let overlayPanelController: OverlayPanelController
    let pipelineReportStore: PipelineReportStore
    let onProfilesOrRulesChanged: () -> Void

    var body: some View {
        TabView {
            OverlaySettingsTab(overlayPanelController: overlayPanelController)
                .tabItem { Label("Overlay", systemImage: "rectangle.on.rectangle") }
            DictionarySettingsTab(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
                .tabItem { Label("Dictionary", systemImage: "character.book.closed") }
            SnippetsSettingsTab(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
                .tabItem { Label("Snippets", systemImage: "text.append") }
            AppProfilesSettingsTab(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
                .tabItem { Label("App Profiles", systemImage: "app.badge") }
            PlaygroundSettingsTab(pipelineReportStore: pipelineReportStore)
                .tabItem { Label("Playground", systemImage: "wand.and.rays") }
            DiagnosticsSettingsTab(persistenceStore: persistenceStore)
                .tabItem { Label("Diagnostics", systemImage: "waveform.path.ecg") }
            UsageInsightsSettingsTab(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
                .tabItem { Label("Usage Insights", systemImage: "chart.bar.xaxis") }
            AboutView(persistenceStore: persistenceStore)
                .tabItem { Label("About", systemImage: "info.circle") }
        }
        .padding(20)
        .frame(minWidth: 560, minHeight: 420)
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
