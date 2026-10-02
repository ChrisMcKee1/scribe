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
    let requestedTab: DictionaryTab?

    init(
        persistenceStore: PersistenceStore,
        dictionaryLibraryService: DictionaryLibraryService,
        onChanged: @escaping @MainActor () -> Void,
        drafts: SettingsDrafts,
        requestedTab: DictionaryTab? = nil
    ) {
        self.persistenceStore = persistenceStore
        self.dictionaryLibraryService = dictionaryLibraryService
        self.onChanged = onChanged
        self.drafts = drafts
        self.requestedTab = requestedTab
    }

    @State private var selectedTab: DictionaryTab = .yourWords
    @State private var wordCount = 0
    @State private var enabledWordPackCount = 0
    @State private var wordPackCount = 0

    var body: some View {
        SettingsPage(
            title: "Dictionary",
            subtitle: "Teach Scribe how to write the words it hears, like \"dot net\" as .NET."
        ) {
            DictionaryTabHeader(
                selectedTab: $selectedTab,
                wordCaption: wordCount == 1 ? "1 word" : "\(wordCount.formatted()) words",
                wordPackCaption: "\(enabledWordPackCount.formatted()) of \(wordPackCount.formatted()) on"
            )
            .id(selectedTab == .wordPacks ? "dictionary.word-packs" : "dictionary.words")

            switch selectedTab {
            case .yourWords:
                SettingsCard(searchID: "dictionary.words") {
                    DictionarySettingsTab(
                        persistenceStore: persistenceStore,
                        dictionaryLibraryService: dictionaryLibraryService,
                        onChanged: childChanged,
                        drafts: drafts,
                        browseWordPacks: { selectedTab = .wordPacks })
                }
            case .wordPacks:
                SettingsCard(searchID: "dictionary.word-packs") {
                    DictionaryWordPacksSettingsTab(
                        dictionaryLibraryService: dictionaryLibraryService,
                        onChanged: childChanged)
                }
            }
        }
        .task { await refreshCounts() }
        .onAppear { applyRequestedTab() }
        .onChange(of: requestedTab) { _ in applyRequestedTab() }
    }

    private func applyRequestedTab() {
        if let requestedTab {
            selectedTab = requestedTab
        }
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
        let enabled = dictionaryLibraryService.settings.enabledLibraryIds
        wordPackCount = libraries.count
        enabledWordPackCount = libraries.filter { enabled.contains($0.id) }.count
    }
}

private struct DictionaryTabHeader: View {
    @Binding var selectedTab: SettingsDictionaryPage.DictionaryTab
    let wordCaption: String
    let wordPackCaption: String

    var body: some View {
        HStack(spacing: 10) {
            tabButton(.yourWords, icon: "person.text.rectangle", title: "Your words", caption: wordCaption)
            tabButton(.wordPacks, icon: "shippingbox", title: "Word packs", caption: wordPackCaption)
        }
    }

    private func tabButton(
        _ tab: SettingsDictionaryPage.DictionaryTab,
        icon: String,
        title: String,
        caption: String
    ) -> some View {
        Button {
            selectedTab = tab
        } label: {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 8) {
                    Image(systemName: icon)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(title).font(.body.weight(.semibold))
                        Text(caption).font(.caption).foregroundStyle(.secondary)
                    }
                }
                Capsule()
                    .fill(selectedTab == tab ? Color.accentColor : Color.clear)
                    .frame(height: 3)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .frame(minWidth: 180, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(
                        selectedTab == tab ? Color.accentColor.opacity(0.10) : Color(nsColor: .controlBackgroundColor))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .stroke(Color(nsColor: .separatorColor).opacity(0.35), lineWidth: 1)
            )
        }
        .buttonStyle(.plain)
    }
}

// MARK: - Your words

struct DictionarySettingsTab: View {
    @StateObject private var model: DictionarySettingsModel
    @ObservedObject private var drafts: SettingsDrafts

    private let dictionaryLibraryService: DictionaryLibraryService
    private let browseWordPacks: () -> Void

    @State private var searchText = ""
    @State private var editorEntry: DictionaryEntry?
    @State private var enabledWordPackSpokenForms: Set<String> = []

    init(
        persistenceStore: PersistenceStore,
        dictionaryLibraryService: DictionaryLibraryService,
        onChanged: @escaping @MainActor () -> Void,
        drafts: SettingsDrafts,
        browseWordPacks: @escaping () -> Void
    ) {
        self.dictionaryLibraryService = dictionaryLibraryService
        self.browseWordPacks = browseWordPacks
        _drafts = ObservedObject(wrappedValue: drafts)
        _model = StateObject(
            wrappedValue: DictionarySettingsModel(access: .live(persistenceStore), drafts: drafts, onChanged: onChanged)
        )
    }

    private var filteredEntries: [DictionaryEntry] {
        model.entries.filter { SettingsDictionaryPageLogic.matchesSearch($0, query: searchText) }
    }

    private var enabledCount: Int {
        model.entries.count(where: \.enabled)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .firstTextBaseline, spacing: 0) {
                Text("Words you add yourself. If a word pack writes a word differently, yours wins. ")
                    .foregroundStyle(.secondary)
                Button("Browse word packs", action: browseWordPacks)
                    .buttonStyle(.link)
            }
            .fixedSize(horizontal: false, vertical: true)

            toolbar

            messages

            dictionaryGrid
                .frame(minHeight: 260)

            VStack(alignment: .leading, spacing: 4) {
                Text(
                    SettingsDictionaryPageLogic.enabledSummary(
                        enabled: enabledCount, total: model.entries.count, noun: "word")
                )
                .cardDescription()
                Text("AI cleanup receives the text after your words and word packs have already been applied.")
                    .cardDescription()
            }
        }
        .onAppear {
            reloadWordPackCoverage()
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
        .sheet(item: $editorEntry) { entry in
            DictionaryWordEditorView(
                existing: model.entries,
                title: entry.id == 0 ? "Add word" : "Edit word",
                initialReplacement: entry.replacement,
                initialForms: entry.pattern.isEmpty ? [""] : [entry.pattern],
                onSave: { forms, replacement in
                    if entry.id == 0 {
                        return await model.addWords(replacement: replacement, forms: forms)
                    }
                    return await model.editWord(entry, replacement: replacement, forms: forms)
                },
                onCancel: { editorEntry = nil })
        }
    }

    private var toolbar: some View {
        HStack(alignment: .center, spacing: 8) {
            Button {
                editorEntry = DictionaryEntry(pattern: "", replacement: "")
            } label: {
                Label("Add word", systemImage: "plus")
            }

            Button("Learn from history") {
                Task { await model.learnFromHistory() }
            }
            .disabled(model.isLearning)

            Button("Clean up unused words...") {
                Task { await model.reviewUsage() }
            }
            .disabled(model.isCleaning)

            Menu("More") {
                Button(model.isImporting ? "Importing..." : "Import CSV...", action: importCsv)
                    .disabled(model.isImporting)
                Button("Export CSV...", action: exportCsv)
                    .disabled(!model.canExport)
                Button("Get template...", action: saveTemplate)
            }

            if model.isLearning || model.isCleaning {
                ProgressView()
                    .controlSize(.small)
            }

            Spacer()

            TextField("Find a word", text: $searchText)
                .textFieldStyle(.roundedBorder)
                .frame(width: 220)
        }
    }

    private var dictionaryGrid: some View {
        VStack(spacing: 0) {
            dictionaryGridHeader
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
                .background(Color(nsColor: .controlBackgroundColor))
            Divider()
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(filteredEntries, id: \.id) { entry in
                        dictionaryGridRow(entry)
                        Divider()
                    }
                }
            }
        }
        .overlay(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .stroke(Color(nsColor: .separatorColor).opacity(0.35), lineWidth: 1)
        )
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
    }

    private var dictionaryGridHeader: some View {
        HStack(spacing: 12) {
            Text("On")
                .frame(width: 34, alignment: .leading)
            Text("Scribe hears")
                .frame(maxWidth: .infinity, alignment: .leading)
            Text("Scribe writes")
                .frame(maxWidth: .infinity, alignment: .leading)
            Text("Whole words only")
                .frame(width: 120, alignment: .leading)
            Text("Word pack")
                .frame(width: 150, alignment: .leading)
            Text("")
                .frame(width: 32)
            Text("")
                .frame(width: 32)
        }
        .font(.caption.weight(.semibold))
        .foregroundStyle(.secondary)
    }

    private func dictionaryGridRow(_ entry: DictionaryEntry) -> some View {
        HStack(spacing: 12) {
            Toggle("", isOn: binding(for: entry))
                .labelsHidden()
                .frame(width: 34, alignment: .leading)
            Text(entry.pattern)
                .lineLimit(2)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(entry.replacement.isEmpty ? "(removes these words)" : entry.replacement)
                .foregroundStyle(entry.replacement.isEmpty ? .secondary : .primary)
                .lineLimit(2)
                .frame(maxWidth: .infinity, alignment: .leading)
            Image(systemName: entry.wholeWord ? "checkmark.circle.fill" : "circle")
                .foregroundStyle(entry.wholeWord ? .green : .secondary)
                .accessibilityLabel(entry.wholeWord ? "Whole words only" : "Can match inside longer words")
                .frame(width: 120, alignment: .leading)
            wordPackCapsule(for: entry)
                .frame(width: 150, alignment: .leading)
            Button {
                editorEntry = entry
            } label: {
                Image(systemName: "pencil")
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Edit \(entry.pattern)")
            .frame(width: 32)
            Button(role: .destructive) {
                Task { await model.delete(entry) }
            } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Delete \(entry.pattern)")
            .frame(width: 32)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 8)
    }

    @ViewBuilder
    private func wordPackCapsule(for entry: DictionaryEntry) -> some View {
        if enabledWordPackSpokenForms.contains(SettingsDictionaryPageLogic.normalizedSpokenForm(entry.pattern)) {
            Text("Same as word pack")
                .font(.caption)
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .background(Capsule().fill(Color(nsColor: .controlBackgroundColor)))
                .overlay(Capsule().stroke(Color(nsColor: .separatorColor).opacity(0.45)))
        } else {
            Text("")
        }
    }

    @ViewBuilder
    private var messages: some View {
        if let loadError = model.loadError {
            Text(loadError).foregroundStyle(.red).font(.caption)
        }
        if let errorMessage = model.errorMessage {
            Text(errorMessage).foregroundStyle(.red).font(.caption)
        }
        if let statusMessage = model.statusMessage {
            Text(statusMessage).foregroundStyle(.secondary).font(.caption)
        }
        if model.load.isLoaded, model.entries.isEmpty {
            Text("No words yet. Add a word or learn from history to get started.")
                .foregroundStyle(.secondary)
        } else if !searchText.isEmpty, filteredEntries.isEmpty {
            Text("No words match \"\(searchText)\".")
                .foregroundStyle(.secondary)
        }
    }

    private func binding(for entry: DictionaryEntry) -> Binding<Bool> {
        Binding(
            get: { entry.enabled },
            set: { newValue in
                Task { await model.setEnabled(entry, enabled: newValue) }
            })
    }

    private func reloadWordPackCoverage() {
        let libraries = dictionaryLibraryService.libraries()
        enabledWordPackSpokenForms = SettingsDictionaryPageLogic.enabledSpokenForms(
            libraries,
            enabledIds: dictionaryLibraryService.settings.enabledLibraryIds)
    }

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

private struct DictionaryCleanupView: View {
    let report: DictionaryUsageReport
    let onApply: (Set<Int64>) -> Void
    let onCancel: () -> Void

    @State private var selected: Set<Int64>

    init(report: DictionaryUsageReport, onApply: @escaping (Set<Int64>) -> Void, onCancel: @escaping () -> Void) {
        self.report = report
        self.onApply = onApply
        self.onCancel = onCancel
        _selected = State(initialValue: Set(report.unusedEntries.map { $0.entry.id }))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Clean up unused words")
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

            Text("Turning a word off is reversible. It stays in your dictionary with its tick cleared.")
                .font(.caption)
                .foregroundStyle(.secondary)

            HStack {
                Spacer()
                Button("Cancel", action: onCancel)
                Button("Turn off selected") {
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

// MARK: - Word packs

struct DictionaryWordPacksSettingsTab: View {
    let dictionaryLibraryService: DictionaryLibraryService
    let onChanged: @MainActor () -> Void

    @State private var wordPacks: [DictionaryLibrary] = []
    @State private var enabledIds: Set<String> = []
    @State private var errorMessage: String?
    @State private var statusMessage: String?
    @State private var showingImporter = false

    private var enabledCount: Int {
        wordPacks.count { enabledIds.contains($0.id) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Word packs")
                        .cardTitle()
                    Text(
                        "Switch on ready-made word packs for vocabulary such as Azure, GitHub and programming languages. Your words always win."
                    )
                    .cardDescription()
                }
                Spacer()
                Button("Import word pack CSV...") { showingImporter = true }
            }

            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }
            if let statusMessage {
                Text(statusMessage).foregroundStyle(.secondary).font(.caption)
            }

            List {
                ForEach(wordPacks, id: \.id) { pack in
                    HStack(alignment: .center, spacing: 12) {
                        Toggle("", isOn: binding(for: pack.id))
                            .labelsHidden()
                        VStack(alignment: .leading, spacing: 3) {
                            Text(pack.name)
                                .font(.body.weight(.medium))
                            HStack(spacing: 6) {
                                Text(pack.builtIn ? "Built-in" : "Your word pack")
                                Text("\(pack.enabledEntryCount.formatted()) terms")
                            }
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            if let description = pack.description, !description.isEmpty {
                                Text(description)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(2)
                            }
                        }
                        Spacer()
                        if !pack.builtIn {
                            Button(role: .destructive) {
                                removeWordPack(pack)
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel("Remove \(pack.name)")
                        }
                    }
                    .padding(.vertical, 4)
                }
            }
            .frame(minHeight: 320)

            Text(
                SettingsDictionaryPageLogic.enabledSummary(
                    enabled: enabledCount, total: wordPacks.count, noun: "word pack")
            )
            .cardDescription()
        }
        .onAppear(perform: reload)
        .fileImporter(isPresented: $showingImporter, allowedContentTypes: [.commaSeparatedText, .plainText]) { result in
            importWordPack(result)
        }
    }

    private func binding(for id: String) -> Binding<Bool> {
        Binding(
            get: { enabledIds.contains(id) },
            set: { isOn in
                dictionaryLibraryService.settings.setEnabled(isOn, id: id)
                enabledIds = dictionaryLibraryService.settings.enabledLibraryIds
                onChanged()
            })
    }

    private func reload() {
        wordPacks = SettingsDictionaryPageLogic.wordPackRows(dictionaryLibraryService.libraries())
        enabledIds = dictionaryLibraryService.settings.enabledLibraryIds
    }

    private func importWordPack(_ result: Result<URL, Error>) {
        errorMessage = nil
        statusMessage = nil
        switch result {
        case .failure(let error):
            errorMessage = wordPackError(error)
        case .success(let url):
            let accessed = url.startAccessingSecurityScopedResource()
            defer { if accessed { url.stopAccessingSecurityScopedResource() } }
            do {
                let csv = try String(contentsOf: url, encoding: .utf8)
                let pack = try dictionaryLibraryService.import(
                    csv: csv, suggestedName: url.deletingPathExtension().lastPathComponent)
                statusMessage = "Imported \"\(pack.name)\" (\(pack.entries.count.formatted()) terms)."
                reload()
                onChanged()
            } catch {
                errorMessage = wordPackError(error)
            }
        }
    }

    private func removeWordPack(_ pack: DictionaryLibrary) {
        errorMessage = nil
        do {
            try dictionaryLibraryService.remove(id: pack.id)
            statusMessage = "Removed \"\(pack.name)\"."
            reload()
            onChanged()
        } catch {
            errorMessage = wordPackError(error)
        }
    }

    private func wordPackError(_ error: Error) -> String {
        error.localizedDescription
            .replacingOccurrences(of: "libraries", with: "word packs")
            .replacingOccurrences(of: "Libraries", with: "Word packs")
            .replacingOccurrences(of: "library", with: "word pack")
            .replacingOccurrences(of: "Library", with: "Word pack")
    }
}
