import Foundation

/// The typing preferences kept in `UserDefaults`. Production uses `UserDefaults.standard`; tests use a suite of their
/// own. A document written before the setting existed reads as on, which keeps the Windows 0.4.4 rule that every
/// install, new or upgraded, starts with the trailing space.
struct TypingSettingsStore {
    private static let addSpaceDefaultsKey = "ScribeAddSpaceAfterDictation"
    private static let appAwareFormattingDefaultsKey = "ScribeAppAwareFormatting"
    private static let defaultTextFormatDefaultsKey = "ScribeDefaultTextFormat"

    static var live: TypingSettingsStore {
        TypingSettingsStore(defaults: .standard)
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
    }

    var addSpaceAfterDictation: Bool {
        get {
            guard let stored = defaults.object(forKey: Self.addSpaceDefaultsKey) as? Bool else {
                return true
            }
            return stored
        }
        nonmutating set {
            defaults.set(newValue, forKey: Self.addSpaceDefaultsKey)
        }
    }

    /// On for every install, as on Windows since 0.5.19: only a stored false turns it off, so a value of any other
    /// kind, or none, reads as on.
    var appAwareFormatting: Bool {
        get {
            // `as? Bool` also accepts a stored number (0 reads as false), so only a real Boolean may turn it off.
            guard let stored = defaults.object(forKey: Self.appAwareFormattingDefaultsKey) as? NSNumber,
                CFGetTypeID(stored) == CFBooleanGetTypeID()
            else { return true }
            return stored.boolValue
        }
        nonmutating set {
            defaults.set(newValue, forKey: Self.appAwareFormattingDefaultsKey)
        }
    }

    /// The format an app profile without a format of its own uses. Plain text until chosen; a stored name this build
    /// does not know reads as plain text.
    var defaultTextFormat: DictationTextFormat {
        get {
            defaults.string(forKey: Self.defaultTextFormatDefaultsKey).map { DictationTextFormat(storedName: $0) }
                ?? .plain
        }
        nonmutating set {
            defaults.set(newValue.rawValue, forKey: Self.defaultTextFormatDefaultsKey)
        }
    }

    /// Both formatting preferences, read together for one dictation.
    var formatting: DictationFormattingSettings {
        DictationFormattingSettings(appAwareFormatting: appAwareFormatting, defaultTextFormat: defaultTextFormat)
    }
}
