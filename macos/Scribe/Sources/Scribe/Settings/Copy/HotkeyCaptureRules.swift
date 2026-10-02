import Foundation

/// How a key is named for a person and judged as a push-to-talk shortcut on a Mac. Windows: `KeyNames`,
/// `HotkeyText`, `PhaseThreeRules.cs`. Keys are named by their `kVK_*` virtual key code, never by the character
/// they type, so a name does not change with the keyboard layout.
enum HotkeyCaptureRules {
    /// What the recorder does with a pressed key.
    enum Verdict: Equatable, Sendable {
        /// The key is accepted; `warnings` are shown under it, each one a catalog id with its rendered text.
        case accepted(warnings: [String])
        /// The key is refused with this text.
        case refused(String)
    }

    static let command: Set<UInt16> = [54, 55]
    static let shift: Set<UInt16> = [56, 60]
    static let capsLock: UInt16 = 57
    static let function: UInt16 = 63
    static let escape: UInt16 = 53
    static let modifiers: Set<UInt16> = [54, 55, 56, 57, 58, 59, 60, 61, 62, 63]
    static let functionKeys: [UInt16: String] = [
        122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8", 101: "F9", 109: "F10",
        103: "F11", 111: "F12", 105: "F13", 107: "F14", 113: "F15", 106: "F16", 64: "F17", 79: "F18", 80: "F19",
        90: "F20",
    ]
    /// Return, Tab, Space, Delete, Forward Delete and the arrow keys.
    static let editing: [UInt16: String] = [
        36: "Return", 48: "Tab", 49: "Space", 51: "Delete", 117: "Forward Delete", 123: "Left Arrow",
        124: "Right Arrow", 125: "Down Arrow", 126: "Up Arrow",
    ]
    private static let modifierNames: [UInt16: String] = [
        54: "Right Command", 55: "Left Command", 56: "Left Shift", 57: "Caps Lock", 58: "Left Option",
        59: "Left Control", 60: "Right Shift", 61: "Right Option", 62: "Right Control", 63: "Fn",
    ]
    private static let otherNames: [UInt16: String] = [
        53: "Escape", 114: "Help", 115: "Home", 116: "Page Up", 119: "End", 121: "Page Down",
    ]

    private static let keypadPrintable: Set<UInt16> = [65, 67, 69, 75, 78, 81, 82, 83, 84, 85, 86, 87, 88, 89, 91, 92]

    /// True for the letter, number, punctuation and keypad keys.
    static func isPrintable(_ keyCode: UInt16) -> Bool {
        keyCode <= 47 || keyCode == 50 || keypadPrintable.contains(keyCode)
    }

    /// The name a person reads: "Right Option", "Caps Lock", "F13", or "Key 12" for one the table does not name.
    static func name(_ keyCode: UInt16) -> String {
        modifierNames[keyCode] ?? functionKeys[keyCode] ?? editing[keyCode] ?? otherNames[keyCode]
            ?? "Key " + String(keyCode)
    }

    /// "Hold Right Option" or "Press Caps Lock", as the Dictation page names the action.
    static func describe(_ keyCode: UInt16, toggle: Bool) -> String {
        (toggle ? "Press " : "Hold ") + name(keyCode)
    }

    /// Whether `keyCode` can be a shortcut, and what to say about it.
    static func evaluate(_ keyCode: UInt16) -> Verdict {
        let key = name(keyCode)
        let copy = SettingsCopy.shortcut
        if keyCode == escape {
            return .refused(copy.cancel.render())
        }
        if keyCode == function {
            return .refused(copy.refusedGlobe.render())
        }
        if editing[keyCode] != nil {
            return .refused(copy.refusedEditing.render(["key": key]))
        }
        if isPrintable(keyCode) {
            return .refused(copy.refusedPrintable.render(["key": key]))
        }
        var warnings: [String] = []
        if keyCode == capsLock {
            warnings.append(copy.infoCapsLock.render())
        } else if command.contains(keyCode) {
            warnings.append(copy.warnCommand.render(["key": key]))
        } else if shift.contains(keyCode) {
            warnings.append(copy.warnShift.render())
        } else if modifiers.contains(keyCode) {
            warnings.append(copy.infoModifier.render(["key": key]))
        } else if let number = functionKeys[keyCode].flatMap({ Int($0.dropFirst()) }), number <= 12 {
            warnings.append(copy.warnFunction.render(["key": key]))
        }
        return .accepted(warnings: warnings)
    }
}
