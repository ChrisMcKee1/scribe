import CoreGraphics
import Foundation

/// A small catalog of "recordable" virtual key codes for push-to-talk. Deliberately limited to
/// keys that make sense as a held-while-talking modifier or a rarely-used function key, mirroring
/// the kind of choices Windows offers for its rebindable hotkey (`HotkeySettings.Key`). Key codes
/// are the standard macOS `kVK_*` constants from `Carbon/HIToolbox` (not linked directly here to
/// avoid the Carbon dependency for eleven integers).
enum HotkeyKeyCodeCatalog {
    struct Entry: Identifiable, Equatable {
        let name: String
        let keyCode: CGKeyCode
        var id: CGKeyCode { keyCode }
    }

    static let entries: [Entry] = [
        Entry(name: "Right Option", keyCode: 61),
        Entry(name: "Left Option", keyCode: 58),
        Entry(name: "Right Control", keyCode: 62),
        Entry(name: "Left Control", keyCode: 59),
        Entry(name: "Right Command", keyCode: 54),
        Entry(name: "Left Command", keyCode: 55),
        Entry(name: "Right Shift", keyCode: 60),
        Entry(name: "Left Shift", keyCode: 56),
        Entry(name: "Caps Lock", keyCode: 57),
        Entry(name: "F13", keyCode: 105),
        Entry(name: "F14", keyCode: 107),
        Entry(name: "F15", keyCode: 113),
        Entry(name: "F18", keyCode: 79),
        Entry(name: "F19", keyCode: 80),
    ]

    /// Falls back to the raw key code for a key the user recorded that isn't in the curated list
    /// above (e.g. any other function key), so an unrecognized-but-valid recording never looks
    /// broken in the UI.
    static func displayName(for keyCode: CGKeyCode) -> String {
        entries.first(where: { $0.keyCode == keyCode })?.name ?? "Key code \(keyCode)"
    }
}
