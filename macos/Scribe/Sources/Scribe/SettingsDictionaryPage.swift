import AppKit
import SwiftUI
import UniformTypeIdentifiers



struct SettingsDictionaryPage: View {
    enum DictionaryTab: String, CaseIterable, Identifiable {
        case yourWords
        case wordPacks

        var id: String { rawValue }
    }

    let persistenceStore: PersistenceStore
    let dictionaryLibraryService: DictionaryLibraryService
    let onChanged: @MainActor () -> Void
    let drafts: SettingsDrafts

    @State private var selectedTab: DictionaryTab = .yourWords
    @State private var wordCount = 0
    @State private var enabledWordPackCount = 0
    @State private var wordPackCount = 0

    var body: some View {
        SettingsPage(
            title: "Dictionary",
            subtitle: "Teach Scribe how to write the words it hears, like \"dot net\" as .NET."
        ) {
            Picker("Dictionary view", selection: $selectedTab) {
                Text("Your words (\(wordCount))").tag(DictionaryTab.yourWords)
                Text("Word packs (\(enabledWordPackCount) of \(wordPackCount) on)").tag(DictionaryTab.wordPacks)
            }
            .pickerStyle(.segmented)
            .frame(maxWidth: 420)

            switch selectedTab {
            case .yourWords:
                SettingsGroupHeader("Your words")
                SettingsCard {
                    DictionarySettingsTab(
                        persistenceStore: persistenceStore,
                        onChanged: childChanged,
                        drafts: drafts)
                }
            case .wordPacks:
                SettingsGroupHeader("Word packs")
                SettingsCard {
                    DictionaryLibrariesSettingsTab(
                        dictionaryLibraryService: dictionaryLibraryService,
                        onChanged: childChanged)
                }
            }
        }
        .task { await refreshCounts() }
    }

    @MainActor
    private func childChanged() {
        onChanged()
        Task { await refreshCounts() }
    }

    @MainActor
    private func refreshCounts() async {
        do {
            wordCount = try await persistenceStore.loadAllDictionaryEntries().count
        } catch {
            wordCount = 0
        }
        let libraries = dictionaryLibraryService.libraries()
        let enabled = DictionaryLibrarySettingsStore.enabledLibraryIds
        wordPackCount = libraries.count
        enabledWordPackCount = libraries.filter { enabled.contains($0.id) }.count
    }
}

// MARK: - Dictionary tab

struct DictionarySettingsTab: View {
    @StateObject private var model: DictionarySettingsModel
    @ObservedObject private var drafts: SettingsDrafts

    init(persistenceStore: PersistenceStore, onChanged: @escaping @MainActor () -> Void, drafts: SettingsDrafts) {
        _drafts = ObservedObject(wrappedValue: drafts)
        _model = StateObject(
            wrappedValue: DictionarySettingsModel(access: .live(persistenceStore), drafts: drafts, onChanged: onChanged)
        )
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("User Dictionary")
                .font(.headline)

            HStack {
                TextField("Spoken form (e.g. \"sherpa onnx\")", text: $drafts.dictionaryPattern)
                TextField("Written form (e.g. \"sherpa-onnx\")", text: $drafts.dictionaryReplacement)
                Button("Add") {
                    Task { await model.addFromDrafts() }
                }
                .disabled(!model.canAdd)
            }

            HStack {
                Button(model.isImporting ? "Importing\u{2026}" : "Import CSV\u{2026}", action: importCsv)
                    .disabled(model.isImporting)
                Button("Export CSV\u{2026}", action: exportCsv)
                    .disabled(!model.canExport)
                Button("Get Template\u{2026}", action: saveTemplate)
                Button("Learn from History") {
                    Task { await model.learnFromHistory() }
                }
                .disabled(model.isLearning)
                Button("Clean Up\u{2026}") {
                    Task { await model.reviewUsage() }
                }
                .disabled(model.isCleaning)
                Spacer()
            }

            if let loadError = model.loadError {
                Text(loadError).foregroundStyle(.red).font(.caption)
            }
            if let errorMessage = model.errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }
            if let statusMessage = model.statusMessage {
                Text(statusMessage).foregroundStyle(.secondary).font(.caption)
            }

            List {
                ForEach(model.entries, id: \.id) { entry in
                    HStack {
                        Toggle("", isOn: binding(for: entry))
                            .labelsHidden()
                        Text(entry.pattern)
                        Image(systemName: "arrow.right")
                            .foregroundStyle(.secondary)
                        Text(entry.replacement)
                        Spacer()
                        Button(role: .destructive) {
                            Task { await model.delete(entry) }
                        } label: {
                            Image(systemName: "trash")
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .onAppear {
            Task { await model.reload() }
        }
        .sheet(
            isPresented: Binding(
                get: { model.cleanupReport != nil },
                set: { if !$0 { model.cleanupReport = nil } }
            )
        ) {
            if let report = model.cleanupReport {
                DictionaryCleanupView(
                    report: report,
                    onApply: { idsToDisable in
                        Task { await model.applyCleanup(disabling: idsToDisable) }
                    },
                    onCancel: { model.cleanupReport = nil })
            }
        }
    }

    private func binding(for entry: DictionaryEntry) -> Binding<Bool> {
        Binding(
            get: { entry.enabled },
            set: { newValue in
                Task { await model.setEnabled(entry, enabled: newValue) }
            })
    }

    // MARK: - CSV import/export: the panels live here, the model reads and writes the files

    private func importCsv() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.commaSeparatedText, .plainText]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.message = "Choose a dictionary CSV file to import."
        guard panel.runModal() == .OK, let url = panel.url else {
            return
        }
        Task { await model.importCsvFile(at: url) }
    }

    private func exportCsv() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.commaSeparatedText]
        panel.nameFieldStringValue = "scribe-dictionary.csv"
        panel.message = "Choose where to save the exported dictionary."
        guard panel.runModal() == .OK, let url = panel.url else {
            return
        }
        Task { await model.exportCsv(to: url) }
    }

    private func saveTemplate() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.commaSeparatedText]
        panel.nameFieldStringValue = "scribe-dictionary-template.csv"
        panel.message = "Choose where to save the dictionary import template."
        guard panel.runModal() == .OK, let url = panel.url else {
            return
        }
        Task { await model.saveTemplate(to: url) }
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
                            Text(
                                usage.entry.enabled
                                    ? "Currently on. Neither wording came up in your recent dictations."
                                    : "Already off. Neither wording came up in your recent dictations."
                            )
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        }
                        Spacer()
                    }
                }
            }
            .frame(minHeight: 160)

            Text(
                "Turning a term off is reversible: it stays in your dictionary with its tick "
                    + "cleared and stops being applied. Nothing is written until you confirm below."
            )
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

// MARK: - Word packs tab

struct DictionaryLibrariesSettingsTab: View {
    let dictionaryLibraryService: DictionaryLibraryService
    let onChanged: @MainActor () -> Void

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
            Text("Word packs")
                .font(.headline)
            Text(
                "Switch on ready-made word packs for domain vocabulary such as Azure, GitHub and "
                    + "programming languages. Word pack entries never override your own dictionary."
            )
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)

            HStack {
                Button("Import word pack CSV\u{2026}") { showingImporter = true }
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

