import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// Full Settings window replacing the earlier static scaffold text. Tabs mirror the feature areas
/// already implemented: overlay position, dictionary, snippets, and per-app profiles. Backed
/// directly by `PersistenceStore` (no separate view-model layer yet; the store's CRUD surface is
/// already small and synchronous, matching the CLI verbs used to verify each feature).
struct SettingsView: View {
    let persistenceStore: PersistenceStore
    let overlayPanelController: OverlayPanelController
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
            DiagnosticsSettingsTab(persistenceStore: persistenceStore)
                .tabItem { Label("Diagnostics", systemImage: "waveform.path.ecg") }
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
