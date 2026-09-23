import CoreGraphics
import Foundation

/// One synthetic keyboard action Scribe posts to the target application. Holds dictated text, so it is
/// never logged.
enum InjectionKeystroke: Equatable, Sendable {
    /// One key press whose Unicode string is these UTF-16 code units.
    case text([UInt16])
    /// Return, carrying the Shift modifier when `shifted`.
    case lineBreak(shifted: Bool)
    /// Command-V.
    case paste
}

/// Splits dictated text into keystrokes for `CGEvent.keyboardSetUnicodeString`.
enum KeystrokePlan {
    /// The most UTF-16 code units Scribe puts in one keyboard event. The event system silently truncates
    /// a longer Unicode string (20 units is the widely observed limit), so a dictation is typed in pieces
    /// of this size or smaller.
    static let maximumUnitsPerEvent = 20

    /// The keystrokes that type `text`. Line breaks (`\n`, `\r` and `\r\n`, which is one break) become
    /// real Return presses: sent as Unicode characters, most text views would drop them or run the lines
    /// together. They carry Shift by default because chat apps (Slack, Teams, Discord, and their web
    /// clients) send the message on a bare Return, while Shift-Return is their soft line break and plain
    /// text views treat it as an ordinary new line. A piece never splits a grapheme cluster, and so never
    /// a surrogate pair, unless one cluster alone is longer than an event can carry.
    static func keystrokes(for text: String, shiftReturnLineBreaks: Bool = true) -> [InjectionKeystroke] {
        var keystrokes: [InjectionKeystroke] = []
        var pending: [Character] = []
        var pendingUnits = 0

        func send(_ count: Int) {
            keystrokes.append(.text(pending.prefix(count).flatMap { Array($0.utf16) }))
            pending.removeFirst(count)
            pendingUnits = pending.reduce(0) { $0 + $1.utf16.count }
        }

        for character in text {
            if character == "\n" || character == "\r" || character == "\r\n" {
                if !pending.isEmpty {
                    send(pending.count)
                }
                keystrokes.append(.lineBreak(shifted: shiftReturnLineBreaks))
                continue
            }

            let units = character.utf16.count
            if units > maximumUnitsPerEvent {
                if !pending.isEmpty {
                    send(pending.count)
                }
                keystrokes.append(contentsOf: oversizedPieces(of: character))
                continue
            }

            while pendingUnits + units > maximumUnitsPerEvent {
                send(preferredBreak(in: pending))
            }
            pending.append(character)
            pendingUnits += units
        }

        if !pending.isEmpty {
            send(pending.count)
        }
        return keystrokes
    }

    /// How many pending characters the next event should carry. Ending an event after whitespace means the
    /// target shows whole words arriving instead of words torn in half between two events. The break never
    /// gives up more than half an event, so an unbroken token such as a URL still moves in full pieces.
    private static func preferredBreak(in pending: [Character]) -> Int {
        var units = 0
        var count = pending.count
        for (index, character) in pending.enumerated() {
            units += character.utf16.count
            if character.isWhitespace && units * 2 >= maximumUnitsPerEvent {
                count = index + 1
            }
        }
        return count
    }

    /// Splits a grapheme cluster longer than one event on Unicode scalar boundaries, so no piece ever
    /// carries half of a surrogate pair.
    private static func oversizedPieces(of character: Character) -> [InjectionKeystroke] {
        var pieces: [InjectionKeystroke] = []
        var units: [UInt16] = []
        for scalar in character.unicodeScalars {
            let scalarUnits = Array(String(scalar).utf16)
            if units.count + scalarUnits.count > maximumUnitsPerEvent {
                pieces.append(.text(units))
                units.removeAll()
            }
            units.append(contentsOf: scalarUnits)
        }

        if !units.isEmpty {
            pieces.append(.text(units))
        }
        return pieces
    }
}

/// Builds the Core Graphics events for a keystroke without posting them, so what Scribe would post can be
/// checked without Accessibility permission.
enum KeystrokeEvents {
    /// Stamped into `eventSourceUserData` on every event Scribe posts ("SCRIBE" in ASCII), so an event tap
    /// can tell Scribe's own keystrokes from the user's.
    static let syntheticMarker: Int64 = 0x5343_5249_4245

    static let returnKeyCode: CGKeyCode = 36
    static let commandKeyCode: CGKeyCode = 55
    static let vKeyCode: CGKeyCode = 9

    /// Every event for `keystroke`, in posting order, or nil when any of them cannot be created, so a
    /// keystroke is posted whole or not at all.
    static func events(for keystroke: InjectionKeystroke, source: CGEventSource?) -> [CGEvent]? {
        let events: [CGEvent]
        switch keystroke {
        case .text(let units):
            guard
                let down = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: true),
                let up = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: false)
            else {
                return nil
            }

            for event in [down, up] {
                units.withUnsafeBufferPointer { buffer in
                    event.keyboardSetUnicodeString(stringLength: buffer.count, unicodeString: buffer.baseAddress)
                }
                // No modifiers, so one the user is still holding cannot turn typed text into shortcuts.
                event.flags = []
            }
            events = [down, up]

        case .lineBreak(let shifted):
            guard
                let down = CGEvent(keyboardEventSource: source, virtualKey: returnKeyCode, keyDown: true),
                let up = CGEvent(keyboardEventSource: source, virtualKey: returnKeyCode, keyDown: false)
            else {
                return nil
            }

            // Shift travels as a flag on the Return events rather than as Shift key events of its own: the
            // target reads modifiers from the key event, and without a synthetic Shift press there is no
            // Shift release that could go missing and leave the key logically held.
            let flags: CGEventFlags = shifted ? .maskShift : []
            down.flags = flags
            up.flags = flags
            events = [down, up]

        case .paste:
            guard
                let commandDown = CGEvent(keyboardEventSource: source, virtualKey: commandKeyCode, keyDown: true),
                let vDown = CGEvent(keyboardEventSource: source, virtualKey: vKeyCode, keyDown: true),
                let vUp = CGEvent(keyboardEventSource: source, virtualKey: vKeyCode, keyDown: false),
                let commandUp = CGEvent(keyboardEventSource: source, virtualKey: commandKeyCode, keyDown: false)
            else {
                return nil
            }

            vDown.flags = .maskCommand
            vUp.flags = .maskCommand
            events = [commandDown, vDown, vUp, commandUp]
        }

        for event in events {
            event.setIntegerValueField(.eventSourceUserData, value: syntheticMarker)
        }
        return events
    }
}
