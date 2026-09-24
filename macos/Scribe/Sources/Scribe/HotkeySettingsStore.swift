import CoreGraphics
import Foundation

/// How a push-to-talk key starts and stops a dictation.
enum HotkeyGesture: Equatable, Sendable {
    /// Held down while talking and released to stop.
    case hold
    /// Tapped once to start and again to stop. Caps Lock works this way because the event tap sees its lock state
    /// (`CGEventFlags.maskAlphaShift` on each flags-changed event, which `HotkeyKeyState` receives as `.lockChanged`),
    /// which a tap flips, rather than whether it is held.
    case toggle
}

/// The bound push-to-talk key, with the gesture and name every description of it is built from.
struct HotkeyBinding: Equatable, Sendable {
    let keyCode: CGKeyCode

    var gesture: HotkeyGesture {
        keyCode == HotkeySettingsStore.capsLockKeyCode ? .toggle : .hold
    }

    var displayName: String {
        HotkeyKeyCodeCatalog.displayName(for: keyCode)
    }
}

/// What Welcome and Settings say about the bound key. Built from the binding rather than written per screen, so
/// both always name the key that is actually bound and the gesture it actually uses.
enum HotkeyHint {
    static func welcome(for binding: HotkeyBinding) -> String {
        switch binding.gesture {
        case .hold:
            return "Hold \(binding.displayName) and start talking. Release it when you are done, "
                + "and the text appears wherever your cursor is."
        case .toggle:
            return "Tap \(binding.displayName) and start talking. Tap it again when you are done, "
                + "and the text appears wherever your cursor is."
        }
    }

    static func settings(for binding: HotkeyBinding) -> String {
        switch binding.gesture {
        case .hold:
            return "Hold \(binding.displayName) anywhere on your Mac to start dictating, and release it to stop."
        case .toggle:
            return "Tap \(binding.displayName) once to start dictating, and tap it again to stop, "
                + "just like its own on/off light."
        }
    }
}

/// The push-to-talk key, kept in `UserDefaults`. Production uses `UserDefaults.standard` through `live`; tests
/// pass a suite of their own, so they never touch the developer's binding or race another test process.
///
/// `HotkeyManager` starts from the stored key, and Settings stores a new key and hands it to the running
/// `HotkeyManager` in the same step, so a rebind takes effect without a relaunch.
struct HotkeySettingsStore {
    static let capsLockKeyCode: CGKeyCode = 57
    static let defaultKeyCode: CGKeyCode = capsLockKeyCode

    private static let defaultsKey = "ScribePushToTalkKeyCode"
    private static let autoStopDefaultsKey = "ScribeAutoStopOnSilence"

    static var live: HotkeySettingsStore {
        HotkeySettingsStore(defaults: .standard)
    }

    /// The key bound in the live store, for callers that cannot take a store yet (`HotkeyManager`).
    static var keyCode: CGKeyCode {
        get { live.keyCode }
        set {
            let store = live
            store.keyCode = newValue
        }
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
    }

    var keyCode: CGKeyCode {
        get {
            guard let stored = defaults.object(forKey: Self.defaultsKey) as? Int,
                let keyCode = CGKeyCode(exactly: stored)
            else {
                return Self.defaultKeyCode
            }
            return keyCode
        }
        nonmutating set {
            defaults.set(Int(newValue), forKey: Self.defaultsKey)
        }
    }

    var binding: HotkeyBinding {
        HotkeyBinding(keyCode: keyCode)
    }

    /// Whether a push-to-talk key tapped on and off (Caps Lock) also ends its dictation after a pause, as the tray's
    /// test dictation does. Off unless the user turns it on, as on Windows (`AppSettings.AutoStopOnSilence`): four
    /// seconds of thinking would end the dictation, and a dictation that ends itself leaves Caps Lock's light on,
    /// because Scribe only listens to the key and never changes its lock state. A held key never stops on silence.
    /// Read at each press, so a change applies to the next dictation.
    var autoStopOnSilence: Bool {
        get { defaults.bool(forKey: Self.autoStopDefaultsKey) }
        nonmutating set { defaults.set(newValue, forKey: Self.autoStopDefaultsKey) }
    }
}
