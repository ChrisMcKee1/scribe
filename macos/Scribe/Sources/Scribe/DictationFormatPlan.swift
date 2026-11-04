import Foundation

/// How a dictation's text reaches the target app. Mirrors Windows' `DictationTextFormat`
/// (Models/DictationTextFormat.cs). Every format keeps the characters the pipeline produced: none asks AI cleanup for
/// Markdown or generates syntax. `markdownWithFormatting` pastes that same Markdown text and adds an HTML rendering of
/// it beside it on the pasteboard, for apps that read HTML but leave pasted Markdown as typed.
enum DictationTextFormat: String, CaseIterable, Equatable, Sendable {
    case plain = "Plain"
    case markdownSource = "MarkdownSource"
    case markdownWithFormatting = "MarkdownWithFormatting"

    /// Reads a stored name, ignoring case. A name this build does not know reads as plain text, as Windows reads one
    /// (`FormattingJson.ReadFormat`), so an older build given a newer format pastes plain text instead of failing.
    init(storedName: String) {
        self = Self.allCases.first { $0.rawValue.caseInsensitiveCompare(storedName) == .orderedSame } ?? .plain
    }

    /// Whether the format pastes Markdown source, with or without its formatted copy.
    var isMarkdown: Bool {
        self != .plain
    }
}

/// Why a dictation got the format it did, for the log. Mirrors Windows' `DictationFormatDecision`.
enum DictationFormatDecision: String, Equatable, Sendable {
    /// App-aware formatting is off: plain text, typed, as before the feature existed.
    case formattingOff
    /// No app profile matches and the app is not a known Markdown app.
    case noMatchingProfile
    case plain
    case markdownSource
    case markdownWithFormatting
    /// Plain text once was armed and this recording took it.
    case plainOnce
    /// A Markdown format was chosen for an app whose line-break rule uses one line, so plain text was used.
    case markdownNewlineConflict
}

/// How a dictation's text is handed to the target.
enum DictationDelivery: String, Equatable, Sendable {
    /// Unicode keystrokes, what every macOS dictation used before app-aware formatting.
    case typing
    /// One paste, so a Markdown app takes a multi-line list as a unit. Typed lines would arrive one Shift-Return at
    /// a time, and the app's list continuation would add a marker of its own to each ("2. 2."). A paste that cannot
    /// happen types the same text.
    case paste
}

/// The formatting preferences read for one dictation (`TypingSettingsStore`).
struct DictationFormattingSettings: Equatable, Sendable {
    /// On for every install: only a stored false turns it off (Windows' `AppAwareFormattingEnabled`).
    var appAwareFormatting = true
    /// The format an app profile without a format of its own uses.
    var defaultTextFormat = DictationTextFormat.plain
}

/// One dictation's formatting, decided once from what was captured when its recording started: the target app, the
/// profiles, the formatting settings and Plain text once. Mirrors Windows' `DictationFormatPlan`. An app match is a
/// preference, not evidence of what a field accepts; it changes neither recognition nor cleanup and adds nothing to a
/// cleanup request.
///
/// The Mac has no typing method setting (it always types), so a Markdown format is what asks for a paste: that is the
/// reason a Markdown app pastes at all. Windows' per-profile typing method and typed line-break overrides have no Mac
/// counterpart yet.
struct DictationFormatPlan: Equatable, Sendable {
    let textFormat: DictationTextFormat
    let newlineMode: NewlineInjectionMode
    let delivery: DictationDelivery
    let bundleIdentifier: String?
    let decision: DictationFormatDecision

    /// Known Markdown-aware apps, by bundle identifier, that get literal Markdown source pasted when no profile
    /// matches. They are the Mac builds of the apps Windows lists in `DictationFormatPlan.MarkdownProcesses`: chat and
    /// AI apps, code editors and Markdown notes. Pasted as a unit, a multi-line list keeps one marker per item, where
    /// typed line by line the app's own list continuation doubles it. An identifier that is wrong only never matches.
    static let markdownBundleIdentifiers: Set<String> = [
        "com.anthropic.claudefordesktop",
        "com.microsoft.m365copilot",
        "ai.perplexity.mac",
        "com.tinyspeck.slackmacgap",
        "com.hnc.Discord",
        "com.microsoft.VSCode",
        "com.microsoft.VSCodeInsiders",
        "com.vscodium",
        "com.todesktop.230313mzl4w4u92",
        "com.exafunction.windsurf",
        "dev.zed.Zed",
        "com.sublimetext.4",
        "com.jetbrains.intellij",
        "com.jetbrains.intellij.ce",
        "com.jetbrains.pycharm",
        "com.jetbrains.pycharm.ce",
        "com.jetbrains.goland",
        "com.jetbrains.CLion",
        "com.jetbrains.WebStorm",
        "com.jetbrains.rider",
        "md.obsidian",
        "notion.id",
        "com.linear",
        "abnerworks.Typora",
        "com.github.marktext",
        "net.cozic.joplin-desktop",
        "com.electron.logseq",
    ]

    /// Apps that read the pasteboard's HTML but leave pasted Markdown as typed, measured on Windows by pasting into
    /// each on 2026-11-04: Teams showed "**Bold**" and "- item" literally and turned the HTML copy into real
    /// formatting; Microsoft Copilot half-converted pasted Markdown and ran a fenced code block into the lines after
    /// it, while the HTML copy lost no line; Codex kept Markdown literal and formatted the HTML copy. They default to
    /// Markdown with formatting. Not measured on a Mac. The GitHub Copilot app is deliberately not here: it converts
    /// pasted Markdown fully, and given both it takes the HTML, which loses task boxes and a code block's language.
    static let formattedMarkdownBundleIdentifiers: Set<String> = [
        "com.microsoft.teams2",
        "com.microsoft.teams",
        "com.openai.codex",
    ]

    /// Apps matched by their name because their bundle identifier is not published: Microsoft Copilot from the Mac
    /// App Store, and the GitHub Copilot app. Compared ignoring case with the name macOS gives the running app.
    static let formattedMarkdownApplicationNames: Set<String> = ["Copilot"]
    static let markdownApplicationNames: Set<String> = ["GitHub Copilot"]

    /// Whether this plan's paste keeps everything on the pasteboard (an image, formatted text, copied files) and puts
    /// it back, rather than typing when the pasteboard holds more than plain text. Only Markdown pasted asks for it:
    /// typed line by line, that text makes a Markdown app continue each list with a marker of its own, so a screenshot
    /// left on the pasteboard must not turn the paste into typing.
    var keepsWholePasteboard: Bool {
        textFormat.isMarkdown && delivery == .paste
    }

    /// Whether line breaks are flattened for this target, which also asks AI cleanup for a single line.
    var flattensNewlines: Bool {
        AppProfileMatcher.flattensNewlines(newlineMode, bundleIdentifier: bundleIdentifier)
    }

    /// The format a known app gets when no profile matches it: Markdown with formatting for an app that reads the
    /// pasteboard's HTML but not pasted Markdown, literal Markdown source for the other Markdown-aware apps, and nil
    /// for every other app.
    static func builtInFormat(bundleIdentifier: String?, applicationName: String?) -> DictationTextFormat? {
        if let bundleIdentifier = bundleIdentifier?.trimmingCharacters(in: .whitespaces), !bundleIdentifier.isEmpty {
            if contains(formattedMarkdownBundleIdentifiers, bundleIdentifier) {
                return .markdownWithFormatting
            }
            if contains(markdownBundleIdentifiers, bundleIdentifier) {
                return .markdownSource
            }
        }
        if let applicationName = applicationName?.trimmingCharacters(in: .whitespaces), !applicationName.isEmpty {
            if contains(formattedMarkdownApplicationNames, applicationName) {
                return .markdownWithFormatting
            }
            if contains(markdownApplicationNames, applicationName) {
                return .markdownSource
            }
        }
        return nil
    }

    /// Decides the plan. `profile` is the profile that matched the target (`AppProfileMatcher.match`), and
    /// `plainOnce` whether this recording took Plain text once when it was admitted.
    static func capture(
        profile: AppProfile?,
        globalNewlineMode: NewlineInjectionMode,
        settings: DictationFormattingSettings,
        bundleIdentifier: String?,
        applicationName: String?,
        plainOnce: Bool
    ) -> DictationFormatPlan {
        let newline = AppProfileMatcher.resolveNewlineMode(
            profile: profile, globalDefault: globalNewlineMode, bundleIdentifier: bundleIdentifier)
        let legacy = DictationFormatPlan(
            textFormat: .plain,
            newlineMode: newline,
            delivery: .typing,
            bundleIdentifier: bundleIdentifier,
            decision: settings.appAwareFormatting ? .noMatchingProfile : .formattingOff)
        guard settings.appAwareFormatting else {
            return legacy
        }

        guard let profile else {
            guard
                let builtIn = builtInFormat(bundleIdentifier: bundleIdentifier, applicationName: applicationName),
                !AppProfileMatcher.flattensNewlines(newline, bundleIdentifier: bundleIdentifier)
            else {
                return legacy
            }
            return plainOnce
                ? legacy.with(decision: .plainOnce)
                : legacy.with(textFormat: builtIn, delivery: .paste, decision: decision(for: builtIn))
        }

        let format = profile.textFormat ?? settings.defaultTextFormat
        if plainOnce {
            return legacy.with(decision: .plainOnce)
        }
        // Refuse the format before choosing a representation: flattened Markdown is never labeled Markdown. The
        // profile's newline and writing-style rules still apply.
        if conflictsWithNewlines(format, newlineMode: newline, bundleIdentifier: bundleIdentifier) {
            return legacy.with(decision: .markdownNewlineConflict)
        }
        return legacy.with(
            textFormat: format, delivery: format.isMarkdown ? .paste : .typing, decision: decision(for: format))
    }

    /// Whether `format` needs line breaks that `newlineMode` takes out for this target.
    static func conflictsWithNewlines(
        _ format: DictationTextFormat,
        newlineMode: NewlineInjectionMode,
        bundleIdentifier: String?
    ) -> Bool {
        format.isMarkdown && AppProfileMatcher.flattensNewlines(newlineMode, bundleIdentifier: bundleIdentifier)
    }

    /// Whether a profile's chosen format needs line breaks its line-break rule takes out in any of its apps, which
    /// the App profiles page refuses before adding it. A bundle identifier cannot tell a message box from a document
    /// or a command window, so this catches only the conflicts that are certain.
    static func conflictsWithNewlines(
        _ format: DictationTextFormat,
        newlineMode: NewlineInjectionMode,
        bundleIdentifiers: [String]
    ) -> Bool {
        guard format.isMarkdown else { return false }
        if newlineMode == .alwaysFlatten {
            return true
        }
        return bundleIdentifiers.contains {
            conflictsWithNewlines(format, newlineMode: newlineMode, bundleIdentifier: $0)
        }
    }

    /// R: the postprocessed characters after this target's line-break rule. Every format's text is an identity
    /// rendering: no markers added or removed, no parsing of code or addresses. Delivery, a refused paste's typing
    /// included, gets this one result, and history keeps it.
    func represent(_ text: String) -> String {
        AppProfileMatcher.applyNewlineMode(newlineMode, to: text, bundleIdentifier: bundleIdentifier)
    }

    private static func decision(for format: DictationTextFormat) -> DictationFormatDecision {
        switch format {
        case .plain:
            return .plain
        case .markdownSource:
            return .markdownSource
        case .markdownWithFormatting:
            return .markdownWithFormatting
        }
    }

    private static func contains(_ names: Set<String>, _ name: String) -> Bool {
        names.contains { $0.caseInsensitiveCompare(name) == .orderedSame }
    }

    private func with(
        textFormat: DictationTextFormat? = nil,
        delivery: DictationDelivery? = nil,
        decision: DictationFormatDecision
    ) -> DictationFormatPlan {
        DictationFormatPlan(
            textFormat: textFormat ?? self.textFormat,
            newlineMode: newlineMode,
            delivery: delivery ?? self.delivery,
            bundleIdentifier: bundleIdentifier,
            decision: decision)
    }
}

/// The words the App profiles page and the notices use for formatting. Mirrors Windows' `DictationFormattingText`.
enum DictationFormattingText {
    static let newlineConflict =
        "Literal Markdown source needs line breaks. Choose Plain text, or Keep Newlines and check delivery in the "
        + "app yourself. An app's name cannot tell a message box from a document or a command window."

    static let runtimeNewlineConflict =
        "Scribe typed plain text because this app's line-break rule uses one line. Choose Plain text, or Keep "
        + "Newlines and check delivery in the app yourself."

    static let sourceLimits =
        "Literal Markdown source keeps the characters already in your text, including a snippet's markers. It "
        + "doesn't turn speech into lists, links or code blocks, and doesn't correct changes made by AI cleanup. "
        + "Markdown with formatting pastes that same text and adds a formatted copy of it, for apps that show "
        + "pasted Markdown as typed."

    static func label(_ format: DictationTextFormat) -> String {
        switch format {
        case .plain:
            return "Plain text"
        case .markdownSource:
            return "Literal Markdown source"
        case .markdownWithFormatting:
            return "Markdown with formatting"
        }
    }
}
