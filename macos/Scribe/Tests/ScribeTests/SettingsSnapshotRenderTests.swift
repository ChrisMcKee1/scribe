import AppKit
import SwiftUI
import XCTest

@testable import Scribe

@MainActor
final class SettingsSnapshotRenderTests: XCTestCase {
    private static var retainedWindows: [NSWindow] = []
    private let imageSize = CGSize(width: 1_000, height: 760)

    func testRenderSettingsPagesToPNGs() throws {
        guard let outputRoot = ProcessInfo.processInfo.environment["SCRIBE_RENDER_SETTINGS_DIR"], !outputRoot.isEmpty
        else {
            throw XCTSkip("Set SCRIBE_RENDER_SETTINGS_DIR to render Settings snapshots.")
        }

        _ = NSApplication.shared
        NSApp.setActivationPolicy(.prohibited)

        let outputURL = URL(fileURLWithPath: outputRoot, isDirectory: true)
        try FileManager.default.createDirectory(at: outputURL, withIntermediateDirectories: true)

        let scratchURL = outputURL.appendingPathComponent("scratch-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: scratchURL, withIntermediateDirectories: true)
        addTeardownBlock {
            try? FileManager.default.removeItem(at: scratchURL)
        }

        let databaseURL = scratchURL.appendingPathComponent("scribe.db", isDirectory: false)
        let persistenceStore = PersistenceStore(databaseURL: databaseURL)
        try persistenceStore.initialize()

        let defaults = makeIsolatedDefaults(label: "settings-snapshots")
        let cleanupAccess = makeCleanupAccess(defaults: defaults)
        let drafts = SettingsDrafts()
        let dependencies = SnapshotDependencies(
            persistenceStore: persistenceStore,
            overlayPanelController: OverlayPanelController(),
            pipelineReportStore: PipelineReportStore(),
            dictionaryLibraryService: DictionaryLibraryService(),
            drafts: drafts,
            defaults: defaults.defaults,
            cleanupAccess: cleanupAccess,
            historyAccess: HistorySettingsAccess(
                load: { HistoryStorageState(retention: .chosen(.days(90)), storedCount: 0) },
                setRetention: { _ in },
                clearHistory: { 0 }))

        var rendered: [URL] = []
        for section in SettingsSection.allCases {
            drafts.section = section
            let url = outputURL.appendingPathComponent("\(section.rawValue)-light.png", isDirectory: false)
            try render(section, dependencies: dependencies, appearance: .aqua, to: url)
            rendered.append(url)
        }

        for section in SettingsSection.allCases {
            drafts.section = section
            let url = outputURL.appendingPathComponent("\(section.rawValue)-dark.png", isDirectory: false)
            try render(section, dependencies: dependencies, appearance: .darkAqua, to: url)
            rendered.append(url)
        }

        let searchLight = outputURL.appendingPathComponent("search-results-light.png", isDirectory: false)
        try render(.dictation, dependencies: dependencies, appearance: .aqua, searchQuery: "model", to: searchLight)
        rendered.append(searchLight)

        let searchDark = outputURL.appendingPathComponent("search-results-dark.png", isDirectory: false)
        try render(.dictation, dependencies: dependencies, appearance: .darkAqua, searchQuery: "model", to: searchDark)
        rendered.append(searchDark)

        for url in rendered {
            try assertPNGIsNotBlank(url)
        }
    }

    private func makeCleanupAccess(defaults: IsolatedDefaults) -> CleanupSettingsAccess {
        let store = CleanupSettingsStore(
            domain: .suite(defaults.suiteName),
            apiKeys: InMemorySecretStore(),
            clientSecrets: InMemorySecretStore())
        store.providerKind = .foundryLocal
        store.foundryLocalModelAlias = CleanupSettingsStore.defaultFoundryLocalModelAlias
        return CleanupSettingsAccess(
            load: {
                let snapshot = store.snapshot()
                return CleanupSettingsValues(
                    isEnabled: snapshot.isEnabled,
                    providerKind: snapshot.providerKind,
                    foundryLocalModelAlias: snapshot.foundryLocalModelAlias,
                    ollamaModel: snapshot.ollamaModel,
                    lmStudioModel: store.lmStudioModel,
                    selectedLocalApp: snapshot.selectedLocalApp,
                    openAIBaseURL: snapshot.openAIBaseURL,
                    openAIModel: snapshot.openAIModel,
                    openAIApiStyle: snapshot.openAIApiStyle,
                    ollamaContextTokens: snapshot.ollamaContextTokens,
                    lmStudioContextTokens: snapshot.lmStudioContextTokens,
                    foundryLocalSendWholeVocabulary: snapshot.foundryLocalSendWholeVocabulary,
                    ollamaSendWholeVocabulary: snapshot.ollamaSendWholeVocabulary,
                    lmStudioSendWholeVocabulary: snapshot.lmStudioSendWholeVocabulary,
                    otherServiceBaseURL: store.otherServiceBaseURL,
                    otherServiceModel: store.otherServiceModel,
                    otherServiceApiStyle: snapshot.otherServiceApiStyle,
                    azureEndpoint: snapshot.azureEndpoint,
                    azureDeployment: snapshot.azureDeployment,
                    azurePromptCaching: snapshot.azurePromptCaching,
                    azureAuthMode: snapshot.azureAuthMode,
                    azureTenantId: snapshot.azureTenantId,
                    azureClientId: snapshot.azureClientId)
            },
            save: { new, old in
                if new.isEnabled != old.isEnabled { store.isEnabled = new.isEnabled }
                if new.providerKind != old.providerKind { store.providerKind = new.providerKind }
                if new.foundryLocalModelAlias != old.foundryLocalModelAlias {
                    store.foundryLocalModelAlias = new.foundryLocalModelAlias
                }
                if new.ollamaModel != old.ollamaModel { store.ollamaModel = new.ollamaModel }
                if new.lmStudioModel != old.lmStudioModel { store.lmStudioModel = new.lmStudioModel }
                if new.selectedLocalApp != old.selectedLocalApp { store.selectedLocalApp = new.selectedLocalApp }
                if new.openAIBaseURL != old.openAIBaseURL { store.openAIBaseURL = new.openAIBaseURL }
                if new.openAIModel != old.openAIModel { store.openAIModel = new.openAIModel }
                if new.openAIApiStyle != old.openAIApiStyle { store.openAIApiStyle = new.openAIApiStyle }
                if new.ollamaContextTokens != old.ollamaContextTokens {
                    store.ollamaContextTokens = new.ollamaContextTokens
                }
                if new.lmStudioContextTokens != old.lmStudioContextTokens {
                    store.lmStudioContextTokens = new.lmStudioContextTokens
                }
                if new.foundryLocalSendWholeVocabulary != old.foundryLocalSendWholeVocabulary {
                    store.foundryLocalSendWholeVocabulary = new.foundryLocalSendWholeVocabulary
                }
                if new.ollamaSendWholeVocabulary != old.ollamaSendWholeVocabulary {
                    store.ollamaSendWholeVocabulary = new.ollamaSendWholeVocabulary
                }
                if new.lmStudioSendWholeVocabulary != old.lmStudioSendWholeVocabulary {
                    store.lmStudioSendWholeVocabulary = new.lmStudioSendWholeVocabulary
                }
                if new.otherServiceBaseURL != old.otherServiceBaseURL {
                    store.otherServiceBaseURL = new.otherServiceBaseURL
                }
                if new.otherServiceModel != old.otherServiceModel { store.otherServiceModel = new.otherServiceModel }
                if new.otherServiceApiStyle != old.otherServiceApiStyle {
                    store.otherServiceApiStyle = new.otherServiceApiStyle
                }
                if new.azureEndpoint != old.azureEndpoint { store.azureEndpoint = new.azureEndpoint }
                if new.azureDeployment != old.azureDeployment { store.azureDeployment = new.azureDeployment }
                if new.azurePromptCaching != old.azurePromptCaching {
                    store.azurePromptCaching = new.azurePromptCaching
                }
                if new.azureAuthMode != old.azureAuthMode { store.azureAuthMode = new.azureAuthMode }
                if new.azureTenantId != old.azureTenantId { store.azureTenantId = new.azureTenantId }
                if new.azureClientId != old.azureClientId { store.azureClientId = new.azureClientId }
            },
            isConfigured: { kind in kind == .foundryLocal },
            hasOpenAIApiKey: { false },
            setOpenAIApiKey: { _ in },
            hasAzureClientSecret: { _ in false },
            setAzureClientSecret: { _, _ in },
            checkConnection: { CleanupConnectionCheck(reachable: true, message: "Snapshot test connection") })
    }

    private func render(
        _ section: SettingsSection,
        dependencies: SnapshotDependencies,
        appearance: NSAppearance.Name,
        searchQuery: String = "",
        to url: URL
    ) throws {
        let colorScheme: ColorScheme = appearance == .darkAqua ? .dark : .light
        let window = NSWindow(
            contentRect: NSRect(origin: .zero, size: imageSize),
            styleMask: [.titled, .closable, .resizable],
            backing: .buffered,
            defer: false)
        window.appearance = NSAppearance(named: appearance)
        window.backgroundColor = NSColor.windowBackgroundColor
        window.contentView = NSHostingView(
            rootView: SnapshotSettingsShell(selection: section, dependencies: dependencies, searchQuery: searchQuery)
                .environment(\.colorScheme, colorScheme))
        window.layoutIfNeeded()

        RunLoop.main.run(until: Date().addingTimeInterval(0.5))
        window.contentView?.layoutSubtreeIfNeeded()

        guard let view = window.contentView else {
            XCTFail("Settings snapshot window has no content view")
            return
        }
        let bounds = view.bounds
        guard let representation = view.bitmapImageRepForCachingDisplay(in: bounds) else {
            XCTFail("Could not create bitmap for \(section.rawValue)")
            return
        }
        view.cacheDisplay(in: bounds, to: representation)
        guard let png = representation.representation(using: .png, properties: [:]) else {
            XCTFail("Could not encode \(section.rawValue) as PNG")
            return
        }
        try png.write(to: url, options: .atomic)
        Self.retainedWindows.append(window)
    }

    private func assertPNGIsNotBlank(_ url: URL, file: StaticString = #filePath, line: UInt = #line) throws {
        let data = try Data(contentsOf: url)
        XCTAssertGreaterThan(data.count, 10_000, "Snapshot is unexpectedly small: \(url.path)", file: file, line: line)
        let image = try XCTUnwrap(NSBitmapImageRep(data: data), "Could not read \(url.path)", file: file, line: line)
        let sampleX = stride(from: 40, to: max(41, image.pixelsWide - 40), by: 80)
        let sampleY = stride(from: 40, to: max(41, image.pixelsHigh - 40), by: 80)
        var colors = Set<String>()
        for y in sampleY {
            for x in sampleX {
                if let color = image.colorAt(x: x, y: y)?.usingColorSpace(.sRGB) {
                    colors.insert(
                        String(
                            format: "%.2f/%.2f/%.2f/%.2f", color.redComponent, color.greenComponent,
                            color.blueComponent, color.alphaComponent))
                }
            }
        }
        XCTAssertGreaterThan(colors.count, 4, "Snapshot appears blank: \(url.path)", file: file, line: line)
    }
}

@MainActor
private struct SnapshotDependencies {
    let persistenceStore: PersistenceStore
    let overlayPanelController: OverlayPanelController
    let pipelineReportStore: PipelineReportStore
    let dictionaryLibraryService: DictionaryLibraryService
    let drafts: SettingsDrafts
    let defaults: UserDefaults
    let cleanupAccess: CleanupSettingsAccess
    let historyAccess: HistorySettingsAccess
}

@MainActor
private struct SnapshotSettingsShell: View {
    let selection: SettingsSection
    let dependencies: SnapshotDependencies
    let searchQuery: String

    init(selection: SettingsSection, dependencies: SnapshotDependencies, searchQuery: String = "") {
        self.selection = selection
        self.dependencies = dependencies
        self.searchQuery = searchQuery
    }

    var body: some View {
        HStack(spacing: 0) {
            sidebar
                .frame(width: 220)
                .background(Color(nsColor: .windowBackgroundColor))
            Divider()
            ScrollView {
                content
                    .padding(24)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .background(Color(nsColor: .windowBackgroundColor))
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .frame(width: 1_000, height: 760)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var sidebar: some View {
        VStack(spacing: 0) {
            SnapshotSearchHeader(query: searchQuery)
            Divider()
            List {
                ForEach(SettingsSection.topLevel) { section in row(section) }
                Section("Personalize") { ForEach(SettingsSection.personalize) { section in row(section) } }
                Section("Review") { ForEach(SettingsSection.review) { section in row(section) } }
                Section("More") { ForEach(SettingsSection.more) { section in row(section) } }
            }
            .listStyle(.sidebar)
        }
    }

    private func row(_ section: SettingsSection) -> some View {
        Label(section.label, systemImage: section.systemImage)
            .padding(.vertical, 3)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selection == section ? Color.accentColor.opacity(0.18) : Color.clear)
            .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
    }

    @ViewBuilder
    private var content: some View {
        switch selection {
        case .dictation:
            SettingsDictationPage(
                overlayPanelController: dependencies.overlayPanelController,
                hotkeyStore: HotkeySettingsStore(defaults: dependencies.defaults),
                audioDeviceStore: AudioDeviceStore(defaults: dependencies.defaults),
                onHotkeyChanged: { _ in },
                onTryDictation: {})
        case .tryDictation:
            SettingsTryDictationPage(pipelineReportStore: dependencies.pipelineReportStore)
        case .aiCleanup:
            SettingsPage(
                title: "AI cleanup",
                subtitle:
                    "Optional. An AI model fixes punctuation, grammar and repeated words before Scribe types. Dictation works without it."
            ) {
                CleanupSettingsTab(
                    drafts: dependencies.drafts,
                    persistenceStore: dependencies.persistenceStore,
                    dictionaryLibraryService: dependencies.dictionaryLibraryService,
                    access: dependencies.cleanupAccess)
            }
        case .dictionary:
            SettingsDictionaryPage(
                persistenceStore: dependencies.persistenceStore,
                dictionaryLibraryService: dependencies.dictionaryLibraryService,
                onChanged: {},
                drafts: dependencies.drafts)
        case .voiceSnippets:
            SettingsVoiceSnippetsPage(
                persistenceStore: dependencies.persistenceStore,
                onChanged: {},
                drafts: dependencies.drafts)
        case .appProfiles:
            SettingsAppProfilesPage(
                persistenceStore: dependencies.persistenceStore,
                onChanged: {},
                drafts: dependencies.drafts)
        case .history:
            SettingsHistoryPage(access: dependencies.historyAccess, onCleared: {})
        case .usage:
            SettingsUsagePage(persistenceStore: dependencies.persistenceStore, onChanged: {})
        case .advanced:
            SettingsAdvancedPage(newlineStore: AdvancedDictationSettingsStore(defaults: dependencies.defaults))
        case .diagnostics:
            SettingsDiagnosticsPage(persistenceStore: dependencies.persistenceStore)
        case .about:
            SettingsAboutPage(persistenceStore: dependencies.persistenceStore)
        }
    }
}

@MainActor
private struct SnapshotSearchHeader: View {
    let query: String

    private var results: [SettingsSearchResult] {
        SettingsSearchIndex.search(query)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Find a setting")
                .font(.caption)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(
                    RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .fill(Color(nsColor: .textBackgroundColor))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .stroke(Color(nsColor: .separatorColor).opacity(0.45), lineWidth: 1)
                )
            if !query.isEmpty {
                ForEach(results.prefix(4), id: \.entry.id) { result in
                    Text(result.displayText)
                        .font(.caption)
                        .lineLimit(2)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 5)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(Color.accentColor.opacity(result == results.first ? 0.18 : 0))
                        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                }
            }
        }
        .padding(10)
    }
}
