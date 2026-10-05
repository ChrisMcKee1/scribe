import Foundation

/// Settings from the Advanced page that affect dictation before any app profile override.
struct AdvancedDictationSettingsStore {
    private static let newlineModeDefaultsKey = "ScribeNewlineMode"
    private static let speechModelAliasDefaultsKey = "ScribeSpeechModelAlias"

    static var live: AdvancedDictationSettingsStore {
        AdvancedDictationSettingsStore(defaults: .standard)
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
    }

    var newlineMode: NewlineInjectionMode {
        get {
            guard let raw = defaults.string(forKey: Self.newlineModeDefaultsKey),
                let mode = NewlineInjectionMode(rawValue: raw)
            else {
                return .smartFlatten
            }
            return mode
        }
        nonmutating set {
            defaults.set(newValue.rawValue, forKey: Self.newlineModeDefaultsKey)
        }
    }

    var speechModelAlias: String {
        get {
            defaults.string(forKey: Self.speechModelAliasDefaultsKey).flatMap { $0.isEmpty ? nil : $0 }
                ?? TranscriptionBackendResolver.defaultFoundryModelAlias
        }
        nonmutating set {
            guard !newValue.isEmpty, !newValue.utf8.contains(0) else { return }
            defaults.set(newValue, forKey: Self.speechModelAliasDefaultsKey)
        }
    }
}

enum DictationControllerConfigurationFactory {
    @MainActor
    static func live(
        advanced: AdvancedDictationSettingsStore = .live,
        typing: TypingSettingsStore = .live,
        toggleKeyStopsOnSilence: @escaping @MainActor @Sendable () -> Bool = {
            HotkeySettingsStore.live.autoStopOnSilence
        }
    ) -> DictationController.Configuration {
        var configuration = DictationController.Configuration()
        configuration.newlineMode = advanced.newlineMode
        configuration.toggleKeyStopsOnSilence = toggleKeyStopsOnSilence
        configuration.addSpaceAfterDictation = { typing.addSpaceAfterDictation }
        return configuration
    }
}
