import Foundation

/// The typing preferences kept in `UserDefaults`. Production uses `UserDefaults.standard`; tests use a suite of their
/// own. A document written before the setting existed reads as on, which keeps the Windows 0.4.4 rule that every
/// install, new or upgraded, starts with the trailing space.
struct TypingSettingsStore {
    private static let addSpaceDefaultsKey = "ScribeAddSpaceAfterDictation"

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
}
