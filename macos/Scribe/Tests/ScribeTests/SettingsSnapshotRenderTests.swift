import AppKit
import SwiftUI
import XCTest

@testable import Scribe

@MainActor
final class SettingsSnapshotRenderTests: XCTestCase {
    private static var retainedWindows: [NSWindow] = []
    private let imageSize = CGSize(width: 1_000, height: 760)

    func testRenderSettingsPagesToPNGs() throws {
        guard let outputRoot = ProcessInfo.processInfo.environment["SCRIBE_RENDER_SETTINGS_DIR"], !outputRoot.isEmpty else {
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

        for section in [SettingsSection.dictation, .aiCleanup] {
            drafts.section = section
            let url = outputURL.appendingPathComponent("\(section.rawValue)-dark.png", isDirectory: false)
            try render(section, dependencies: dependencies, appearance: .darkAqua, to: url)
            rendered.append(url)
        }

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
                    openAIBaseURL: snapshot.openAIBaseURL,
                    openAIModel: snapshot.openAIModel,
                    azureEndpoint: snapshot.azureEndpoint,
                    azureDeployment: snapshot.azureDeployment,
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
                if new.openAIBaseURL != old.openAIBaseURL { store.openAIBaseURL = new.openAIBaseURL }
                if new.openAIModel != old.openAIModel { store.openAIModel = new.openAIModel }
                if new.azureEndpoint != old.azureEndpoint { store.azureEndpoint = new.azureEndpoint }
                if new.azureDeployment != old.azureDeployment { store.azureDeployment = new.azureDeployment }
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
        to url: URL
    ) throws {
        let window = NSWindow(
            contentRect: NSRect(origin: .zero, size: imageSize),
            styleMask: [.titled, .closable, .resizable],
            backing: .buffered,
            defer: false)
        window.appearance = NSAppearance(named: appearance)
        window.contentView = NSHostingView(rootView: SnapshotSettingsShell(selection: section, dependencies: dependencies))
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
                    colors.insert(String(format: "%.2f/%.2f/%.2f/%.2f", color.redComponent, color.greenComponent, color.blueComponent, color.alphaComponent))
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
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .frame(width: 1_000, height: 760)
    }

    private var sidebar: some View {
        List {
            ForEach(SettingsSection.topLevel) { section in row(section) }
            Section("Personalize") { ForEach(SettingsSection.personalize) { section in row(section) } }
            Section("Review") { ForEach(SettingsSection.review) { section in row(section) } }
            Section("More") { ForEach(SettingsSection.more) { section in row(section) } }
        }
        .listStyle(.sidebar)
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
                subtitle: "Optional. An AI model fixes punctuation, grammar and repeated words before Scribe types. Dictation works without it."
            ) {
                CleanupSettingsTab(drafts: dependencies.drafts, access: dependencies.cleanupAccess)
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
