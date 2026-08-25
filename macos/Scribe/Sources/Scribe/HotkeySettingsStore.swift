import CoreGraphics
import Foundation

/// `UserDefaults`-backed storage for the push-to-talk key, the same stopgap pattern used for the
/// overlay anchor and AI cleanup settings (a general structured settings store doesn't exist yet
/// on macOS). Read once at launch by `AppDelegate` into `HotkeyManager.keyCode`, and again live
/// whenever the Settings window records a new key, so a rebind takes effect immediately with no
/// relaunch required.
enum HotkeySettingsStore {
    private static let defaultsKey = "ScribePushToTalkKeyCode"

    /// Caps Lock: tapping it once engages the lock (begins capture) and tapping it again
    /// disengages it (ends capture), mirroring Caps Lock's own toggle gesture rather than the
    /// hold-and-release behavior every other push-to-talk key uses. See
    /// `HotkeyManager.isKeyCurrentlyPressed`.
    static let defaultKeyCode: CGKeyCode = 57

    static var keyCode: CGKeyCode {
        get {
            guard let stored = UserDefaults.standard.object(forKey: defaultsKey) as? Int,
                let keyCode = CGKeyCode(exactly: stored)
            else {
                return defaultKeyCode
            }
            return keyCode
        }
        set {
            UserDefaults.standard.set(Int(newValue), forKey: defaultsKey)
        }
    }
}
