import Foundation

/// Line-break handling for injected text. Terminals treat a typed/pasted newline as Enter, so a
/// multi-paragraph dictation can submit a half-finished command; flattening replaces line breaks
/// with spaces to avoid that. Mirrors Windows' `NewlineInjectionMode` (Models/Enums.cs).
enum NewlineInjectionMode: String, Equatable {
    /// Flatten line breaks only when the focused app is a known terminal (default).
    case smartFlatten
    /// Always replace line breaks with spaces, in every app.
    case alwaysFlatten
    /// Inject text exactly as produced, line breaks included.
    case keepNewlines
}

/// A per-app dictation profile: when the focused app at the end of a capture matches one of
/// `bundleIdentifiers`/`processNames`, the profile's overrides apply — a different AI writing style
/// and/or line-break handling. Nil overrides fall back to the global setting. Mirrors Windows'
/// `AppProfile` (Models/AppProfile.cs), but keys on bundle identifier first (the stable macOS
/// identity for an app) with process name as a secondary/fallback match, since macOS apps don't
/// have the Windows-style ".exe process name" as their primary identity.
struct AppProfile: Equatable {
    /// Display name shown in the settings list (e.g. "Email", "Chat", "Terminal").
    var name: String
    /// Bundle identifiers this profile applies to (e.g. "com.apple.Terminal", "com.tinyspeck.slackmacgap").
    var bundleIdentifiers: [String]
    /// Process names this profile applies to as a fallback when bundle identifier isn't available.
    var processNames: [String]
    /// AI writing style for this app; nil/blank keeps the global style.
    var writingStylePrompt: String?
    /// Line-break handling for this app; nil keeps the global setting.
    var newlineHandling: NewlineInjectionMode?
}

/// Resolves which profile (if any) applies to a foreground app, and the effective newline mode to
/// use given that resolution. Mirrors Windows' `AppProfileMatcher`.
enum AppProfileMatcher {
    /// Bundle identifiers of terminal-like apps for the SmartFlatten default, since macOS has no
    /// single "is this app a console host" API the way Windows can inspect a conhost/terminal
    /// process. This list covers the terminals most likely to be used with Scribe; unmatched
    /// terminal emulators fall back to the app's chosen default (see resolveNewlineMode).
    static let knownTerminalBundleIdentifiers: Set<String> = [
        "com.apple.Terminal",
        "com.googlecode.iterm2",
        "dev.warp.Warp-Stable",
        "com.github.wez.wezterm",
        "net.kovidgoyal.kitty",
        "co.zeit.hyper",
        "com.mitchellh.ghostty"
    ]

    /// Returns the first profile matching `bundleIdentifier`/`processName` (case-insensitive),
    /// checking bundle identifier first, then process name. First match wins, in the user's
    /// configured order.
    static func match(profiles: [AppProfile], bundleIdentifier: String?, processName: String?) -> AppProfile? {
        guard !profiles.isEmpty else { return nil }

        if let bundleIdentifier, !bundleIdentifier.isEmpty {
            for profile in profiles {
                if profile.bundleIdentifiers.contains(where: { $0.caseInsensitiveCompare(bundleIdentifier) == .orderedSame }) {
                    return profile
                }
            }
        }

        if let processName, !processName.isEmpty {
            for profile in profiles {
                if profile.processNames.contains(where: { $0.caseInsensitiveCompare(processName) == .orderedSame }) {
                    return profile
                }
            }
        }

        return nil
    }

    /// Resolves the effective newline mode for a dictation: the matched profile's override, else
    /// the global default, with SmartFlatten consulting `bundleIdentifier` against the known
    /// terminal list.
    static func resolveNewlineMode(
        profile: AppProfile?,
        globalDefault: NewlineInjectionMode,
        bundleIdentifier: String?
    ) -> NewlineInjectionMode {
        profile?.newlineHandling ?? globalDefault
    }

    /// Applies a resolved newline mode to text about to be injected.
    static func applyNewlineMode(_ mode: NewlineInjectionMode, to text: String, bundleIdentifier: String?) -> String {
        switch mode {
        case .keepNewlines:
            return text
        case .alwaysFlatten:
            return flatten(text)
        case .smartFlatten:
            guard let bundleIdentifier, knownTerminalBundleIdentifiers.contains(bundleIdentifier) else {
                return text
            }
            return flatten(text)
        }
    }

    private static func flatten(_ text: String) -> String {
        text
            .replacingOccurrences(of: "\r\n", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\r", with: " ")
    }
}
