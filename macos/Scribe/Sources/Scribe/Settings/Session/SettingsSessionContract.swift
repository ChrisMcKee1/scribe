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
    case number(Double)
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

    func legacyBool(default fallback: Bool = false, strict: Bool = false) -> Bool {
        switch self {
        case .bool(let value): return value
        case .integer(let value):
            if strict { return (NSNumber(value: value) as? Bool) ?? fallback }
            return value != 0
        case .number(let value):
            if strict { return (NSNumber(value: value) as? Bool) ?? fallback }
            return NSNumber(value: value).boolValue
        case .string(let value): return strict ? fallback : (value as NSString).boolValue
        default: return fallback
        }
    }

    var legacyInteger: Int {
        switch self {
        case .integer(let value): return value
        case .number(let value): return NSNumber(value: value).intValue
        case .bool(let value): return value ? 1 : 0
        case .string(let value): return (value as NSString).integerValue
        default: return 0
        }
    }

    var legacyRawInteger: Int? {
        switch self {
        case .integer(let value): return value
        case .bool(let value): return NSNumber(value: value) as? Int
        case .number(let value): return NSNumber(value: value) as? Int
        default: return nil
        }
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
        get { values["ScribeAiCleanupEnabled"]?.legacyBool() ?? false }
        set { values["ScribeAiCleanupEnabled"] = .bool(newValue) }
    }

    var shortcutKeyCode: Int {
        get {
            guard let value = values["ScribePushToTalkKeyCode"]?.legacyRawInteger,
                value >= 0, value <= Int(UInt16.max)
            else {
                return Int(HotkeySettingsStore.defaultKeyCode)
            }
            return value
        }
        set { values["ScribePushToTalkKeyCode"] = .integer(newValue) }
    }

    var addSpaceAfterDictation: Bool {
        get { values["ScribeAddSpaceAfterDictation"]?.legacyBool(default: true, strict: true) ?? true }
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

    init(
        id: Int64,
        name: String,
        bundleIdentifiers: [String],
        processNames: [String],
        writingStyle: String?,
        newlineMode: String?
    ) {
        self.id = id
        self.name = name
        self.bundleIdentifiers = bundleIdentifiers
        self.processNames = processNames
        self.writingStyle = writingStyle
        self.newlineMode = newlineMode
    }

    init(_ profile: AppProfile) {
        id = profile.id
        name = profile.name
        bundleIdentifiers = profile.bundleIdentifiers
        processNames = profile.processNames
        writingStyle = profile.writingStylePrompt
        newlineMode = profile.newlineHandling?.rawValue
    }

    var profile: AppProfile {
        let mode = newlineMode.flatMap(NewlineInjectionMode.init(rawValue:))
        return AppProfile(
            id: id, name: name, bundleIdentifiers: bundleIdentifiers, processNames: processNames,
            writingStylePrompt: writingStyle, newlineHandling: mode)
    }
}

/// Nil collections are not loaded, not empty. Word-pack content belongs to its workspace and commit attachment.
struct SettingsDocument: Codable, Equatable, Sendable {
    var preferences = SettingsPreferences()
    var historyRetentionValue: String?
    var dictionary: [SettingsDictionaryRow]?
    var snippets: [SettingsSnippetRow]?
    var profiles: [SettingsProfileRow]?
    var wordPackSignature: Data?

    var withoutPlaceholders: SettingsDocument {
        var document = self
        document.dictionary = dictionary?.filter {
            $0.id > 0 || !$0.pattern.isEmpty || !$0.replacement.isEmpty
        }
        document.snippets = snippets?.filter {
            $0.id > 0 || !$0.phrase.isEmpty || !$0.template.isEmpty
        }
        document.profiles = profiles?.filter {
            $0.id > 0 || !$0.name.isEmpty || !$0.bundleIdentifiers.isEmpty || !$0.processNames.isEmpty
                || $0.writingStyle?.isEmpty == false
        }
        return document
    }
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
struct SettingsCommitAttachment: Codable, Equatable, Sendable {
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
    var credentials: [SettingsCredentialID: SettingsCredentialEdit] = [:]
}

struct SettingsCommitReceipt: Codable, Equatable, Sendable {
    let id: UUID
    let revision: UInt64
    let document: SettingsDocument
    var attachment = SettingsCommitAttachment()
    var rowIDs: [String: Int64] = [:]

    init(id: UUID, revision: UInt64, document: SettingsDocument) {
        self.id = id
        self.revision = revision
        self.document = document
    }

    private enum CodingKeys: String, CodingKey {
        case id, revision, document, attachment, rowIDs
    }

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        revision = try container.decode(UInt64.self, forKey: .revision)
        document = try container.decode(SettingsDocument.self, forKey: .document)
        let decodedAttachment = try container.decodeIfPresent(SettingsCommitAttachment.self, forKey: .attachment)
        attachment = decodedAttachment ?? SettingsCommitAttachment()
        rowIDs = try container.decodeIfPresent([String: Int64].self, forKey: .rowIDs) ?? [:]
    }
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
    case outcomeUnknown(UUID)
    case committed(SettingsCommitReceipt, SettingsApplicationOutcome, changedWhileSaving: Bool)

    var mayClose: Bool {
        switch self {
        case .unchanged: return true
        case .committed(_, .applied, changedWhileSaving: false): return true
        default: return false
        }
    }
}

/// An adapter that loses a commit reply must not discard possibly committed credentials.
struct SettingsCommitUncertain: Error, Sendable {
    let id: UUID
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
