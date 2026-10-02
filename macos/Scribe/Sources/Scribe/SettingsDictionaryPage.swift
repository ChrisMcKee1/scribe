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

    @State private var workspace = LibraryWorkspace(libraries: [])
    @State private var selectedID: String?
    @State private var searchText = ""
    @State private var sortOrder: LibraryTermSortOrder = .savedOrder
    @State private var errorMessage: String?
    @State private var statusMessage: String?
    @State private var showingImporter = false
    @State private var editor: WordPackTermEditorState?
    @State private var renameText = ""
    @State private var recentlyDeleted: [RecentlyDeletedLibrary] = []

    private var enabledCount: Int {
        visiblePacks.count { $0.enabled && !$0.pendingDelete }
    }

    private var visiblePacks: [DraftLibrary] {
        LibraryOrdering().sort(
            workspace.draft.libraries.filter { !$0.pendingDelete }.map { library in
                DictionaryLibrary(
                    id: library.id,
                    name: library.name,
                    category: library.category,
                    description: library.description,
                    builtIn: library.builtIn,
                    entries: library.rows.map { $0.row.values.dictionaryEntry },
                    fileName: library.builtIn ? nil : "\(library.id).csv",
                    basedOn: library.basedOn)
            }
        ).compactMap { ordered in
            workspace.draft.libraries.first { $0.id.caseInsensitiveCompare(ordered.id) == .orderedSame }
        }
    }

    private var selectedPack: DraftLibrary? {
        let selected = selectedID.flatMap(workspace.draft.find)
        return selected?.pendingDelete == false ? selected : visiblePacks.first
    }

    private var filteredRows: [DraftTermRow] {
        guard let selectedPack else { return [] }
        let search = LibrarySearch.forCurrentLocale()
        let rows =
            searchText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? selectedPack.rows
            : selectedPack.rows.filter { search.matches($0.row.values, query: searchText) }
        return LibraryTermSort.forCurrentLocale().sort(rows, by: sortOrder)
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
                Button("Import CSV...") { showingImporter = true }
                Button("New word pack") { createWordPack() }
            }

            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }
            if let statusMessage {
                Text(statusMessage).foregroundStyle(.secondary).font(.caption)
            }

            HStack(alignment: .top, spacing: 14) {
                wordPackList
                    .frame(minWidth: 280, idealWidth: 280, maxWidth: 280, minHeight: 420)
                Divider()
                selectedEditor
                    .frame(minHeight: 420)
            }

            HStack {
                Text(
                    SettingsDictionaryPageLogic.enabledSummary(
                        enabled: enabledCount, total: visiblePacks.count, noun: "word pack")
                )
                .cardDescription()
                Spacer()
                Button("Undo") { workspace.undo() }
                    .disabled(!workspace.canUndo)
                    .keyboardShortcut("z", modifiers: [.command])
                Button("Redo") { workspace.redo() }
                    .disabled(!workspace.canRedo)
                    .keyboardShortcut("z", modifiers: [.command, .shift])
                Button("Discard") { discard() }
                    .disabled(!workspace.hasUnsavedChanges)
                Button("Save") { save() }
                    .disabled(!workspace.hasUnsavedChanges)
                    .keyboardShortcut("s", modifiers: [.command])
            }

            recentlyDeletedSection
        }
        .onAppear(perform: reload)
        .fileImporter(isPresented: $showingImporter, allowedContentTypes: [.commaSeparatedText, .plainText]) { result in
            importWordPack(result)
        }
        .sheet(item: $editor) { state in
            WordPackTermEditorView(state: state) { values in
                applyTermEdit(state: state, values: values)
            }
        }
    }

    private var wordPackList: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("A to Z").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
            List(selection: Binding(get: { selectedID }, set: { selectedID = $0 })) {
                ForEach(visiblePacks, id: \.id) { pack in
                    HStack(alignment: .top, spacing: 8) {
                        Toggle(
                            "",
                            isOn: Binding(
                                get: { pack.enabled },
                                set: { workspace.setEnabled(pack.id, enabled: $0) })
                        )
                        .labelsHidden()
                        VStack(alignment: .leading, spacing: 3) {
                            Text(pack.name).font(.body.weight(.medium))
                            Text(
                                "\(pack.builtIn ? "Built-in" : "Your word pack") · \(pack.rows.count.formatted()) terms"
                            )
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            if let description = pack.description, !description.isEmpty {
                                Text(description).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                            }
                        }
                    }
                    .padding(.vertical, 4)
                    .tag(pack.id as String?)
                }
            }
        }
    }

    @ViewBuilder
    private var selectedEditor: some View {
        if let pack = selectedPack {
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(pack.name).font(.title3.weight(.semibold))
                        Text(
                            "\(pack.builtIn ? "Built-in word pack" : "Your word pack") · \(pack.rows.count.formatted()) terms"
                        )
                        .foregroundStyle(.secondary)
                        if pack.rows.contains(where: { $0.row.review != nil }) {
                            Text("Review built-in updates before saving.")
                                .font(.caption)
                                .foregroundStyle(.orange)
                        }
                    }
                    Spacer()
                    if pack.builtIn {
                        Button("Reset built-in edits") { resetBuiltInEdits(pack.id) }
                    } else {
                        Button("Export CSV...") { export(pack) }
                        Button("Rename") { rename(pack) }
                        Button("Delete", role: .destructive) { delete(pack) }
                    }
                }

                Toggle(
                    "Include this word pack in AI cleanup vocabulary",
                    isOn: Binding(
                        get: { pack.aiPermitted },
                        set: { workspace.setAiPermission(pack.id, permitted: $0) }))

                HStack {
                    TextField("Search terms", text: $searchText)
                        .textFieldStyle(.roundedBorder)
                    Picker("Sort", selection: $sortOrder) {
                        Text("Saved order").tag(LibraryTermSortOrder.savedOrder)
                        Text("Spoken A to Z").tag(LibraryTermSortOrder.spokenAscending)
                        Text("Spoken Z to A").tag(LibraryTermSortOrder.spokenDescending)
                        Text("Written A to Z").tag(LibraryTermSortOrder.writtenAscending)
                        Text("Written Z to A").tag(LibraryTermSortOrder.writtenDescending)
                    }
                    .labelsHidden()
                    .frame(width: 150)
                    Button("Add term") {
                        editor = WordPackTermEditorState(packID: pack.id, rowID: nil, values: TermValues("", ""))
                    }
                }

                termHeader
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(filteredRows, id: \.rowID) { row in
                            termRow(packID: pack.id, row: row)
                            Divider()
                        }
                    }
                }
                .background(Color(nsColor: .controlBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            }
        } else {
            VStack(spacing: 10) {
                Image(systemName: "shippingbox")
                    .font(.largeTitle)
                    .foregroundStyle(.secondary)
                Text("No word pack selected")
                    .font(.headline)
                    .foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private var termHeader: some View {
        HStack {
            Text("On").frame(width: 42, alignment: .leading)
            Text("Spoken form").frame(maxWidth: .infinity, alignment: .leading)
            Text("Written form").frame(maxWidth: .infinity, alignment: .leading)
            Text("Whole word").frame(width: 90, alignment: .leading)
            Text("").frame(width: 80)
        }
        .font(.caption.weight(.semibold))
        .foregroundStyle(.secondary)
    }

    private func termRow(packID: String, row: DraftTermRow) -> some View {
        HStack(spacing: 8) {
            Toggle(
                "",
                isOn: Binding(
                    get: { row.row.values.enabled },
                    set: { workspace.setTermEnabled(packID, rowID: row.rowID, enabled: $0) })
            )
            .labelsHidden()
            .frame(width: 42, alignment: .leading)
            Text(row.row.values.spoken).frame(maxWidth: .infinity, alignment: .leading)
            Text(row.row.values.written).frame(maxWidth: .infinity, alignment: .leading)
            Text(row.row.values.wholeWord ? "Yes" : "No").frame(width: 90, alignment: .leading)
            HStack(spacing: 6) {
                if row.row.review != nil {
                    Text("Built-in update")
                        .font(.caption)
                        .foregroundStyle(.orange)
                    Button("Keep mine") { workspace.resolveReview(packID, rowID: row.rowID, choice: .keepMine) }
                        .buttonStyle(.link)
                    Button("Use built-in update") {
                        workspace.resolveReview(packID, rowID: row.rowID, choice: .useUpdated)
                    }
                    .buttonStyle(.link)
                }
                Button("Edit") {
                    editor = WordPackTermEditorState(packID: packID, rowID: row.rowID, values: row.row.values)
                }
                .buttonStyle(.link)
                Button("Delete", role: .destructive) { workspace.deleteTerm(packID, rowID: row.rowID) }
                    .buttonStyle(.link)
            }
            .frame(width: 80, alignment: .trailing)
        }
        .padding(.vertical, 7)
        .padding(.horizontal, 8)
    }

    private var recentlyDeletedSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Recently deleted")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Prune expired") {
                    do {
                        let removed = try dictionaryLibraryService.pruneRecentlyDeleted()
                        statusMessage =
                            removed == 1 ? "Deleted 1 expired word pack." : "Deleted \(removed) expired word packs."
                        reload()
                    } catch {
                        errorMessage = wordPackError(error)
                    }
                }
                .disabled(recentlyDeleted.isEmpty)
            }
            if recentlyDeleted.isEmpty {
                Text("Deleted custom word packs are kept here for 30 days.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(recentlyDeleted) { entry in
                    HStack {
                        VStack(alignment: .leading) {
                            Text(entry.name)
                            Text(
                                "\(entry.termCount.formatted()) terms · deleted \(entry.deletedAt.formatted(date: .abbreviated, time: .shortened))"
                            )
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Restore") { restoreRecentlyDeleted(entry) }
                            .disabled(entry.state != .available && entry.state != .partlyReadable)
                        Button("Delete permanently", role: .destructive) {
                            workspace.deleteRecentlyDeletedPermanently(entry)
                            recentlyDeleted.removeAll { $0.id == entry.id }
                            statusMessage = "Will permanently delete \"\(entry.name)\" when you save."
                        }
                    }
                    .padding(.vertical, 3)
                }
            }
        }
    }

    private func reload() {
        Task {
            do {
                let catalog = try await dictionaryLibraryService.loadCatalog()
                await MainActor.run {
                    workspace = LibraryWorkspace(catalog: catalog)
                    selectedID = selectedID ?? visiblePacks.first?.id
                    recentlyDeleted = catalog.recentlyDeleted
                    statusMessage = nil
                    errorMessage = nil
                }
            } catch {
                await MainActor.run { errorMessage = wordPackError(error) }
            }
        }
    }

    private func save() {
        let capture = workspace.captureChangeSet()
        if let issue = capture.issues.first {
            errorMessage = LibraryEditor.message(for: issue)
            return
        }
        guard let changeSet = capture.changeSet else { return }
        do {
            try dictionaryLibraryService.save(changeSet: changeSet)
            workspace.markSaved()
            statusMessage = "Saved word packs."
            errorMessage = nil
            onChanged()
            reload()
        } catch {
            errorMessage = wordPackError(error)
        }
    }

    private func discard() {
        workspace.discard()
        statusMessage = "Discarded unsaved word pack changes."
        errorMessage = nil
    }

    private func createWordPack() {
        selectedID = workspace.createLibrary()
        statusMessage = "Created a new word pack. Save to keep it."
    }

    private func rename(_ pack: DraftLibrary) {
        let alert = NSAlert()
        alert.messageText = "Rename word pack"
        alert.informativeText = "Type the new name for this word pack."
        let field = NSTextField(string: pack.name)
        field.frame = NSRect(x: 0, y: 0, width: 280, height: 24)
        alert.accessoryView = field
        alert.addButton(withTitle: "Rename")
        alert.addButton(withTitle: "Cancel")
        if alert.runModal() == .alertFirstButtonReturn {
            let result = workspace.rename(pack.id, name: field.stringValue)
            if let issue = result.issue {
                errorMessage = LibraryEditor.message(for: issue)
            }
        }
    }

    private func delete(_ pack: DraftLibrary) {
        workspace.deleteLibrary(pack.id)
        selectedID = visiblePacks.first { $0.id != pack.id }?.id
        statusMessage = "Deleted \"\(pack.name)\". Save to move it to Recently deleted."
    }

    private func restoreRecentlyDeleted(_ entry: RecentlyDeletedLibrary) {
        let restoreID = LibraryNaming.newCustomID(
            name: entry.originalID,
            takenIDs: workspace.draft.libraries.map(\.id))
        workspace.restoreRecentlyDeleted(entry, restoreAsID: restoreID)
        recentlyDeleted.removeAll { $0.id == entry.id }
        statusMessage = "Will restore \"\(entry.name)\" when you save."
    }

    private func resetBuiltInEdits(_ id: String) {
        guard let pack = workspace.draft.find(id) else { return }
        for row in pack.rows where row.row.origin != .shipped {
            workspace.deleteTerm(id, rowID: row.rowID)
        }
        statusMessage = "Reset staged edits for \"\(pack.name)\". Save to apply."
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
                let data = try Data(contentsOf: url)
                let document = DictionaryLibraryCsv.parseImport(data)
                if !document.errors.isEmpty {
                    throw DictionaryLibraryServiceError.invalidCsv(
                        document.errors.prefix(5).map(\.legacyMessage).joined(separator: "\n"))
                }
                let id = workspace.createLibrary(
                    name: document.name ?? url.deletingPathExtension().lastPathComponent)
                for term in document.terms {
                    _ = workspace.addTerm(id, values: term)
                }
                selectedID = id
                statusMessage = "Imported \(document.terms.count.formatted()) terms. Save to keep the word pack."
            } catch {
                errorMessage = wordPackError(error)
            }
        }
    }

    private func export(_ pack: DraftLibrary) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "\(pack.id).csv"
        panel.allowedContentTypes = [.commaSeparatedText]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let data = DictionaryLibraryCsv.exportSharing(
                LibraryCsvContent(
                    name: pack.name,
                    category: pack.category,
                    description: pack.description,
                    basedOn: pack.basedOn,
                    rows: pack.rows.map(\.row.values)))
            try data.write(to: url, options: .atomic)
            statusMessage = "Exported \"\(pack.name)\"."
        } catch {
            errorMessage = wordPackError(error)
        }
    }

    private func applyTermEdit(state: WordPackTermEditorState, values: TermValues) {
        let result: LibraryEditResult
        if let rowID = state.rowID {
            result = workspace.editTerm(state.packID, rowID: rowID, values: values)
        } else {
            result = workspace.addTerm(state.packID, values: values)
        }
        if let issue = result.issue {
            errorMessage = LibraryEditor.message(for: issue)
        } else {
            editor = nil
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

private struct WordPackTermEditorState: Identifiable {
    let packID: String
    let rowID: Int64?
    let values: TermValues

    var id: String { "\(packID):\(rowID ?? 0)" }
}

private struct WordPackTermEditorView: View {
    let state: WordPackTermEditorState
    let onSave: (TermValues) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var spoken: String
    @State private var written: String
    @State private var wholeWord: Bool
    @State private var enabled: Bool

    init(state: WordPackTermEditorState, onSave: @escaping (TermValues) -> Void) {
        self.state = state
        self.onSave = onSave
        _spoken = State(initialValue: state.values.spoken)
        _written = State(initialValue: state.values.written)
        _wholeWord = State(initialValue: state.values.wholeWord)
        _enabled = State(initialValue: state.values.enabled)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(state.rowID == nil ? "Add term" : "Edit term")
                .font(.title3.weight(.semibold))
            TextField("Spoken form", text: $spoken)
                .textFieldStyle(.roundedBorder)
            TextField("Written form", text: $written)
                .textFieldStyle(.roundedBorder)
            Toggle("Match whole words only", isOn: $wholeWord)
            Toggle("Term is on", isOn: $enabled)
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") {
                    onSave(TermValues(spoken, written, wholeWord, enabled))
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 420)
    }
}
