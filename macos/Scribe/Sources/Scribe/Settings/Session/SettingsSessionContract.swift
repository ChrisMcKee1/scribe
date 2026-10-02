import Foundation

/// Stable session page IDs. The navigation workstream can map its presentation model through these raw values.
enum SettingsSessionPage: String, CaseIterable, Codable, Sendable {
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

    var title: String {
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
}

enum SettingsValue: Codable, Equatable, Sendable {
    case bool(Bool)
    case integer(Int)
    case string(String)
    case strings([String])
    case data(Data)

    var bool: Bool? {
        if case .bool(let value) = self { return value }
        return nil
    }

    var integer: Int? {
        if case .integer(let value) = self { return value }
        return nil
    }

    var string: String? {
        if case .string(let value) = self { return value }
        return nil
    }
}

/// Missing keys stay missing, preserving the legacy stores' property defaults and downgrade behavior.
struct SettingsPreferences: Codable, Equatable, Sendable {
    var values: [String: SettingsValue] = [:]

    subscript(_ key: String) -> SettingsValue? {
        get { values[key] }
        set { values[key] = newValue }
    }

    var aiCleanupEnabled: Bool {
        get { values["ScribeAiCleanupEnabled"]?.bool ?? false }
        set { values["ScribeAiCleanupEnabled"] = .bool(newValue) }
    }

    var shortcutKeyCode: Int {
        get { values["ScribePushToTalkKeyCode"]?.integer ?? Int(HotkeySettingsStore.defaultKeyCode) }
        set { values["ScribePushToTalkKeyCode"] = .integer(newValue) }
    }

    var addSpaceAfterDictation: Bool {
        get { values["ScribeAddSpaceAfterDictation"]?.bool ?? true }
        set { values["ScribeAddSpaceAfterDictation"] = .bool(newValue) }
    }

    var overlayAnchor: String? {
        get { values["ScribeOverlayAnchor"]?.string }
        set { values["ScribeOverlayAnchor"] = newValue.map(SettingsValue.string) }
    }
}

struct SettingsDictionaryRow: Codable, Equatable, Sendable {
    var id: Int64
    var pattern: String
    var replacement: String
    var wholeWord: Bool
    var enabled: Bool

    init(_ entry: DictionaryEntry) {
        id = entry.id
        pattern = entry.pattern
        replacement = entry.replacement
        wholeWord = entry.wholeWord
        enabled = entry.enabled
    }

    var entry: DictionaryEntry {
        DictionaryEntry(id: id, pattern: pattern, replacement: replacement, wholeWord: wholeWord, enabled: enabled)
    }
}

struct SettingsSnippetRow: Codable, Equatable, Sendable {
    var id: Int64
    var phrase: String
    var template: String
    var enabled: Bool

    init(_ snippet: Snippet) {
        id = snippet.id
        phrase = snippet.phrase
        template = snippet.template
        enabled = snippet.enabled
    }

    var snippet: Snippet {
        Snippet(id: id, phrase: phrase, template: template, enabled: enabled)
    }
}

struct SettingsProfileRow: Codable, Equatable, Sendable {
    var id: Int64
    var name: String
    var bundleIdentifiers: [String]
    var processNames: [String]
    var writingStyle: String?
    var newlineMode: String?

    init(_ profile: AppProfile) {
        id = profile.id
        name = profile.name
        bundleIdentifiers = profile.bundleIdentifiers
        processNames = profile.processNames
        writingStyle = profile.writingStylePrompt
        newlineMode = profile.newlineHandling?.rawValue
    }

    var profile: AppProfile {
        AppProfile(
            id: id, name: name, bundleIdentifiers: bundleIdentifiers, processNames: processNames,
            writingStylePrompt: writingStyle, newlineHandling: newlineMode.flatMap(NewlineInjectionMode.init(rawValue:)))
    }
}

/// Nil collections are not loaded, not empty. Word-pack content belongs to its workspace and commit attachment.
struct SettingsDocument: Equatable, Sendable {
    var preferences = SettingsPreferences()
    var historyRetentionValue: String?
    var dictionary: [SettingsDictionaryRow]?
    var snippets: [SettingsSnippetRow]?
    var profiles: [SettingsProfileRow]?
    var wordPackSignature: Data?
}

enum SettingsExternalSetting: String, CaseIterable, Codable, Sendable {
    case aiCleanup
    case microphone
    case overlayAnchor
}

struct SettingsExternalIntent: Equatable, Sendable {
    let revision: UInt64
    let values: [String: SettingsValue]
}

/// Expected values are checked and all values written inside the same transaction as the settings document.
struct SettingsCommitAttachment: Equatable, Sendable {
    var expectedValues: [String: String?] = [:]
    var values: [String: String?] = [:]
}

struct SettingsSubmission: Sendable {
    let id: UUID
    let revision: UInt64
    let baseline: SettingsDocument
    var document: SettingsDocument
    let intents: [SettingsExternalSetting: SettingsExternalIntent]
    var attachment = SettingsCommitAttachment()
}

struct SettingsCommitReceipt: Equatable, Sendable {
    let id: UUID
    let revision: UInt64
    let document: SettingsDocument
}

enum SettingsApplicationOutcome: Equatable, Sendable {
    case applied
    case notApplied
}

enum SettingsSaveFailure: String, Error, Codable, Sendable {
    case busy
    case validation
    case storage
    case conflict
    case credentials
    case preparationTimedOut
    case cancelled
}

enum SettingsSaveResult: Equatable, Sendable {
    case unchanged
    case notCommitted(SettingsSaveFailure)
    case committed(SettingsCommitReceipt, SettingsApplicationOutcome, changedWhileSaving: Bool)

    var mayClose: Bool {
        switch self {
        case .unchanged: return true
        case .committed(_, .applied, changedWhileSaving: false): return true
        default: return false
        }
    }
}

enum SettingsCommand: String, CaseIterable, Sendable {
    case usageAddToDictionary
    case freeMemory
    case checkAgain
    case testConnection
    case clearHistory
    case deleteHistoryEntry
    case saveDiagnostics
    case openConsole
    case openAtLogin
    case trayAiCleanup
    case trayPause
    case trayIndicatorPosition
    case copy
    case export
    case checkForUpdates
    case learnFromHistory
    case restoreDefaultShortcuts
    case removeCredential
    case deleteWordPackPermanently
}

enum SettingsCommandDisposition: Equatable, Sendable {
    case immediate
    case staged
}

enum SettingsCommandPolicy {
    static let dispositions: [SettingsCommand: SettingsCommandDisposition] = [
        .usageAddToDictionary: .immediate,
        .freeMemory: .immediate,
        .checkAgain: .immediate,
        .testConnection: .immediate,
        .clearHistory: .immediate,
        .deleteHistoryEntry: .immediate,
        .saveDiagnostics: .immediate,
        .openConsole: .immediate,
        .openAtLogin: .immediate,
        .trayAiCleanup: .immediate,
        .trayPause: .immediate,
        .trayIndicatorPosition: .immediate,
        .copy: .immediate,
        .export: .immediate,
        .checkForUpdates: .immediate,
        .learnFromHistory: .staged,
        .restoreDefaultShortcuts: .staged,
        .removeCredential: .staged,
        .deleteWordPackPermanently: .staged,
    ]
}
