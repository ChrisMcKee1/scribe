import Foundation

/// The group a Settings page sits in, in the sidebar. The first group has no heading, as on Windows.
enum SettingsPageGroup: Int, CaseIterable, Sendable {
    case primary
    case personalize
    case review
    case more

    /// The heading shown above the group; empty for the first group.
    var title: String {
        switch self {
        case .primary: return ""
        case .personalize: return "Personalize"
        case .review: return "Review"
        case .more: return "More"
        }
    }
}

/// The two tabs of the Dictionary page.
enum DictionaryTab: Int, CaseIterable, Sendable {
    case yourWords
    case wordPacks

    var title: String {
        switch self {
        case .yourWords: return "Your words"
        case .wordPacks: return "Word packs"
        }
    }

    var symbol: String {
        switch self {
        case .yourWords: return "character.cursor.ibeam"
        case .wordPacks: return "books.vertical"
        }
    }
}

/// The eleven Settings pages, in the order and under the names the Windows window shows
/// (`src/Scribe.Core/Settings/SettingsNavigation.cs`). This is the redesigned window's page model; the older
/// `SettingsSection` stays until the new window replaces it.
enum SettingsPage: Int, CaseIterable, Identifiable, Sendable {
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

    var id: Int { rawValue }

    /// The page's name in the sidebar and as the page title.
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

    var group: SettingsPageGroup {
        switch self {
        case .dictation, .tryDictation, .aiCleanup: return .primary
        case .dictionary, .voiceSnippets, .appProfiles: return .personalize
        case .history, .usage: return .review
        case .advanced, .diagnostics, .about: return .more
        }
    }

    /// 1-based, as `SettingsNavigationItem.Position` on Windows.
    var position: Int { rawValue + 1 }

    /// The SF Symbol in the sidebar. The Windows icons are Fluent; these are the nearest SF Symbols on macOS 13.
    var symbol: String {
        switch self {
        case .dictation: return "mic"
        case .tryDictation: return "testtube.2"
        case .aiCleanup: return "wand.and.stars"
        case .dictionary: return "character.book.closed"
        case .voiceSnippets: return "text.quote"
        case .appProfiles: return "square.grid.2x2"
        case .history: return "clock.arrow.circlepath"
        case .usage: return "chart.line.uptrend.xyaxis"
        case .advanced: return "wrench.and.screwdriver"
        case .diagnostics: return "waveform.path.ecg"
        case .about: return "info.circle"
        }
    }

    /// Words a person might use for the page, including the names this window used to give things. Find a setting
    /// matches them but never shows them.
    var keywords: [String] {
        switch self {
        case .dictation: return ["shortcut", "hotkey", "microphone", "mic", "language", "dictate", "typing"]
        case .tryDictation: return ["playground", "test", "practice", "check"]
        case .aiCleanup: return ["cleanup", "provider", "model", "ollama", "lm studio", "foundry", "cloud", "polish"]
        case .dictionary: return ["words", "word packs", "libraries", "library", "spelling", "vocabulary"]
        case .voiceSnippets: return ["snippets", "trigger phrase", "template", "expand", "shortcut text"]
        case .appProfiles: return ["profiles", "per app", "writing style", "newline", "process name"]
        case .history: return ["transcripts", "recent", "retention", "delete", "recordings"]
        case .usage: return ["insights", "statistics", "totals", "top apps", "trend"]
        case .advanced: return ["login", "startup", "appearance", "idle", "memory", "limit", "overlay", "pill"]
        case .diagnostics: return ["latency", "speed", "performance", "logs", "support", "decode"]
        case .about: return ["version", "privacy", "licence", "license", "update", "support", "source"]
        }
    }

    /// The tabs the page has, in order; empty for every page but Dictionary.
    var tabs: [DictionaryTab] {
        self == .dictionary ? DictionaryTab.allCases : []
    }
}

/// One row of the sidebar: the page, its group heading and whether it opens a group.
struct SettingsNavigationItem: Equatable, Sendable {
    let page: SettingsPage
    let label: String
    let group: String
    let position: Int
    let isFirstInGroup: Bool
}

/// The sidebar's order and the parsing of a page name, a port of `SettingsNavigation.cs`.
enum SettingsNavigation {
    static let items: [SettingsNavigationItem] = SettingsPage.allCases.enumerated().map { index, page in
        let previous = index > 0 ? SettingsPage.allCases[index - 1] : nil
        return SettingsNavigationItem(
            page: page,
            label: page.title,
            group: page.group.title,
            position: page.position,
            isFirstInGroup: previous?.group != page.group
        )
    }

    static func title(_ page: SettingsPage) -> String {
        page.title
    }

    /// Reads a page from its title or its case name, ignoring case, spaces and punctuation.
    static func parsePage(_ value: String?) -> SettingsPage? {
        guard let value, !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return nil
        }
        let wanted = normalize(value)
        return SettingsPage.allCases.first { page in
            normalize(page.title) == wanted || normalize(String(describing: page)) == wanted
        }
    }

    /// `--settings` opens Dictation; `--settings=<page>` opens that page; nil when the arguments ask for neither.
    static func parseSettingsArgument(_ arguments: [String]) -> SettingsPage? {
        let prefix = "--settings="
        for argument in arguments {
            if argument.lowercased().hasPrefix(prefix) {
                return parsePage(String(argument.dropFirst(prefix.count)))
            }
            if argument.lowercased() == "--settings" {
                return .dictation
            }
        }
        return nil
    }

    private static func normalize(_ value: String) -> String {
        String(value.filter { $0.isLetter || $0.isNumber }).lowercased()
    }
}
