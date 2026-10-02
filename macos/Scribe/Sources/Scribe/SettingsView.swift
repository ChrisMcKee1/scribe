import AppKit
import CoreGraphics
import SwiftUI

/// The Settings window uses the same information architecture as Windows while keeping native macOS sidebar chrome.
enum SettingsSection: String, CaseIterable, Identifiable {
    case dictation
    case tryDictation
    case aiCleanup
    case dictionary
    case voiceSnippets
    case appProfiles
    case history
    case usage
    case advanced
    case diagnostics
    case about

    var id: String { rawValue }

    static let topLevel: [SettingsSection] = [.dictation, .tryDictation, .aiCleanup]
    static let personalize: [SettingsSection] = [.dictionary, .voiceSnippets, .appProfiles]
    static let review: [SettingsSection] = [.history, .usage]
    static let more: [SettingsSection] = [.advanced, .diagnostics, .about]

    var label: String {
        switch self {
        case .dictation: return "Dictation"
        case .tryDictation: return "Try dictation"
        case .aiCleanup: return "AI cleanup"
        case .dictionary: return "Dictionary"
        case .voiceSnippets: return "Voice snippets"
        case .appProfiles: return "App profiles"
        case .history: return "History"
        case .usage: return "Usage"
        case .advanced: return "Advanced"
        case .diagnostics: return "Diagnostics"
        case .about: return "About"
        }
    }

    var group: String? {
        switch self {
        case .dictation, .tryDictation, .aiCleanup: return nil
        case .dictionary, .voiceSnippets, .appProfiles: return "Personalize"
        case .history, .usage: return "Review"
        case .advanced, .diagnostics, .about: return "More"
        }
    }

    var systemImage: String {
        switch self {
        case .dictation: return "mic"
        case .tryDictation: return "flask"
        case .aiCleanup: return "wand.and.stars"
        case .dictionary: return "book.closed"
        case .voiceSnippets: return "quote.bubble"
        case .appProfiles: return "list.bullet.rectangle"
        case .history: return "clock.arrow.circlepath"
        case .usage: return "chart.line.uptrend.xyaxis"
        case .advanced: return "wrench.and.screwdriver"
        case .diagnostics: return "waveform.path.ecg"
        case .about: return "info.circle"
        }
    }

    static func parse(_ value: String) -> SettingsSection? {
        let normalized = normalize(value)
        return allCases.first { section in
            normalize(section.rawValue) == normalized || normalize(section.label) == normalized
        } ?? legacyAliases[normalized]
    }

    private static let legacyAliases: [String: SettingsSection] = [
        "overlay": .dictation,
        "input": .dictation,
        "hotkey": .dictation,
        "playground": .tryDictation,
        "cleanup": .aiCleanup,
        "aicleanup": .aiCleanup,
        "libraries": .dictionary,
        "wordpacks": .dictionary,
        "snippets": .voiceSnippets,
        "voice": .voiceSnippets,
        "appprofiles": .appProfiles,
        "profiles": .appProfiles,
        "usageinsights": .usage
    ]

    private static func normalize(_ value: String) -> String {
        value.unicodeScalars.filter(CharacterSet.alphanumerics.contains).map { String($0).lowercased() }.joined()
    }
}

struct SettingsView: View {
    let persistenceStore: PersistenceStore
    let overlayPanelController: OverlayPanelController
    let pipelineReportStore: PipelineReportStore
    let dictionaryLibraryService: DictionaryLibraryService
    let onProfilesOrRulesChanged: @MainActor () -> Void
    let onHotkeyChanged: (CGKeyCode) -> Void
    let historyAccess: HistorySettingsAccess
    let onHistoryCleared: @MainActor () -> Void
    @ObservedObject var drafts: SettingsDrafts
    var hotkeyStore: HotkeySettingsStore = .live
    var audioDeviceStore: AudioDeviceStore = .live

    var body: some View {
        NavigationSplitView {
            List(selection: $drafts.section) {
                ForEach(SettingsSection.topLevel) { section in
                    sidebarRow(section)
                }
                Section("Personalize") {
                    ForEach(SettingsSection.personalize) { section in sidebarRow(section) }
                }
                Section("Review") {
                    ForEach(SettingsSection.review) { section in sidebarRow(section) }
                }
                Section("More") {
                    ForEach(SettingsSection.more) { section in sidebarRow(section) }
                }
            }
            .navigationSplitViewColumnWidth(min: 180, ideal: 210, max: 240)
        } detail: {
            ScrollView {
                detailContent
                    .padding(24)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .frame(minWidth: 860, minHeight: 600)
    }

    private func sidebarRow(_ section: SettingsSection) -> some View {
        Label(section.label, systemImage: section.systemImage)
            .tag(section)
    }

    @ViewBuilder
    private var detailContent: some View {
        switch drafts.section ?? .dictation {
        case .dictation:
            SettingsDictationPage(
                overlayPanelController: overlayPanelController,
                hotkeyStore: hotkeyStore,
                audioDeviceStore: audioDeviceStore,
                onHotkeyChanged: onHotkeyChanged)
        case .tryDictation:
            SettingsTryDictationPage(pipelineReportStore: pipelineReportStore)
        case .aiCleanup:
            SettingsAICleanupPage(drafts: drafts)
        case .dictionary:
            SettingsDictionaryPage(
                persistenceStore: persistenceStore,
                dictionaryLibraryService: dictionaryLibraryService,
                onChanged: onProfilesOrRulesChanged,
                drafts: drafts)
        case .voiceSnippets:
            SettingsVoiceSnippetsPage(
                persistenceStore: persistenceStore,
                onChanged: onProfilesOrRulesChanged,
                drafts: drafts)
        case .appProfiles:
            SettingsAppProfilesPage(
                persistenceStore: persistenceStore,
                onChanged: onProfilesOrRulesChanged,
                drafts: drafts)
        case .history:
            SettingsHistoryPage(access: historyAccess, onCleared: onHistoryCleared)
        case .usage:
            SettingsUsagePage(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
        case .advanced:
            SettingsAdvancedPage()
        case .diagnostics:
            SettingsDiagnosticsPage(persistenceStore: persistenceStore)
        case .about:
            SettingsAboutPage(persistenceStore: persistenceStore)
        }
    }
}
