import CoreGraphics
import XCTest

@testable import Scribe

/// Covers how dictated text becomes keystrokes for the typing fallback, and what each keystroke's events
/// carry, without posting anything.
final class InjectionKeystrokesTests: XCTestCase {
    private let limit = KeystrokePlan.maximumUnitsPerEvent

    func testShortTextIsOneKeystroke() {
        XCTAssertEqual(
            KeystrokePlan.keystrokes(for: "Hello there."),
            [.text(Array("Hello there.".utf16))])
    }

    func testNoKeystrokeCarriesMoreThanOneEventCanHold() {
        let samples = [
            String(repeating: "a", count: 97),
            "The quick brown fox jumps over the lazy dog while the band plays on and on.",
            String(repeating: "\u{1F600}", count: 31),
            String(repeating: "e\u{301}", count: 40),
            "https://example.com/a/very/long/path/without/any/spaces/at/all/anywhere",
        ]

        for sample in samples {
            for keystroke in KeystrokePlan.keystrokes(for: sample) {
                if case .text(let units) = keystroke {
                    XCTAssertFalse(units.isEmpty)
                    XCTAssertLessThanOrEqual(units.count, limit)
                }
            }
        }
    }

    func testKeystrokesReassembleTheTextWithOneBreakPerLineEnding() {
        let text = "First line, with a comma.\r\nSecond line \u{1F44B}\nThird\rFourth ends here."
        XCTAssertEqual(
            typedText(KeystrokePlan.keystrokes(for: text)),
            "First line, with a comma.\nSecond line \u{1F44B}\nThird\nFourth ends here.")
    }

    func testLineBreaksAreShiftReturnAndACarriageReturnLineFeedIsOneBreak() {
        XCTAssertEqual(
            KeystrokePlan.keystrokes(for: "one\r\ntwo\nthree\rfour"),
            [
                .text(Array("one".utf16)),
                .lineBreak(shifted: true),
                .text(Array("two".utf16)),
                .lineBreak(shifted: true),
                .text(Array("three".utf16)),
                .lineBreak(shifted: true),
                .text(Array("four".utf16)),
            ])
    }

    func testLineBreaksArePlainReturnWhenShiftReturnIsOff() {
        XCTAssertEqual(
            KeystrokePlan.keystrokes(for: "one\n\ntwo", shiftReturnLineBreaks: false),
            [
                .text(Array("one".utf16)),
                .lineBreak(shifted: false),
                .lineBreak(shifted: false),
                .text(Array("two".utf16)),
            ])
    }

    func testSurrogatePairsAreNeverSplitAcrossEvents() {
        let text = String(repeating: "\u{1F600}", count: 15)
        let keystrokes = KeystrokePlan.keystrokes(for: text)

        XCTAssertEqual(keystrokes.count, 2)
        for keystroke in keystrokes {
            guard case .text(let units) = keystroke else {
                return XCTFail("Expected only text keystrokes.")
            }
            let piece = String(decoding: units, as: UTF16.self)
            XCTAssertFalse(piece.contains("\u{FFFD}"), "A piece carried half of a surrogate pair.")
        }
        XCTAssertEqual(typedText(keystrokes), text)
    }

    func testGraphemeClustersStayInOneEvent() {
        let text = String(repeating: "e\u{301}", count: 15)
        for keystroke in KeystrokePlan.keystrokes(for: text) {
            guard case .text(let units) = keystroke else {
                return XCTFail("Expected only text keystrokes.")
            }
            let piece = String(decoding: units, as: UTF16.self)
            XCTAssertEqual(piece.unicodeScalars.count, piece.count * 2, "A cluster was split between events.")
        }
    }

    func testEventsEndAfterWhitespaceWhenThatKeepsAtLeastHalfAnEvent() {
        let keystrokes = KeystrokePlan.keystrokes(for: "hello world this is a test of chunking")

        XCTAssertEqual(
            keystrokes,
            [
                .text(Array("hello world this is ".utf16)),
                .text(Array("a test of chunking".utf16)),
            ])
    }

    func testAnUnbrokenTokenStillMovesInFullEvents() {
        let token = "abcdefghijklmnopqrstuvwxyz0123"
        let keystrokes = KeystrokePlan.keystrokes(for: token)

        XCTAssertEqual(
            keystrokes,
            [
                .text(Array("abcdefghijklmnopqrst".utf16)),
                .text(Array("uvwxyz0123".utf16)),
            ])
    }

    func testAShortWordBeforeALongTokenDoesNotShrinkTheEvent() {
        // Breaking after "aaaa " would give up three quarters of the event, so the break is not taken.
        let keystrokes = KeystrokePlan.keystrokes(for: "aaaa " + String(repeating: "b", count: 20))

        guard case .text(let first) = keystrokes.first else {
            return XCTFail("Expected a text keystroke first.")
        }
        XCTAssertEqual(first.count, limit)
    }

    func testAClusterLongerThanAnEventIsSplitOnScalarBoundaries() {
        // One grapheme cluster: a base letter followed by twelve emoji modifiers, 25 UTF-16 units.
        let cluster = "a" + String(repeating: "\u{1F3FB}", count: 12)
        XCTAssertEqual(cluster.count, 1)

        let keystrokes = KeystrokePlan.keystrokes(for: "x" + cluster + "y")

        XCTAssertEqual(typedText(keystrokes), "x" + cluster + "y")
        for keystroke in keystrokes {
            guard case .text(let units) = keystroke else {
                return XCTFail("Expected only text keystrokes.")
            }
            XCTAssertLessThanOrEqual(units.count, limit)
            XCTAssertFalse(String(decoding: units, as: UTF16.self).contains("\u{FFFD}"))
        }
    }

    func testTextEventsCarryTheUnicodeStringWithNoModifiersAndScribesMarker() throws {
        let units = Array("Hi \u{1F44B}".utf16)
        let events = try XCTUnwrap(KeystrokeEvents.events(for: .text(units), source: nil))

        XCTAssertEqual(events.map(\.type), [.keyDown, .keyUp])
        for event in events {
            XCTAssertEqual(unicodeString(of: event), units)
            XCTAssertTrue(event.flags.intersection(modifierFlags).isEmpty)
            XCTAssertEqual(event.getIntegerValueField(.eventSourceUserData), KeystrokeEvents.syntheticMarker)
        }
    }

    func testShiftedLineBreakIsReturnCarryingShift() throws {
        let events = try XCTUnwrap(KeystrokeEvents.events(for: .lineBreak(shifted: true), source: nil))

        XCTAssertEqual(events.map(\.type), [.keyDown, .keyUp])
        for event in events {
            XCTAssertEqual(event.getIntegerValueField(.keyboardEventKeycode), Int64(KeystrokeEvents.returnKeyCode))
            XCTAssertTrue(event.flags.contains(.maskShift))
            XCTAssertFalse(event.flags.contains(.maskCommand))
            XCTAssertEqual(event.getIntegerValueField(.eventSourceUserData), KeystrokeEvents.syntheticMarker)
        }
    }

    func testPlainLineBreakIsReturnWithoutModifiers() throws {
        let events = try XCTUnwrap(KeystrokeEvents.events(for: .lineBreak(shifted: false), source: nil))

        for event in events {
            XCTAssertEqual(event.getIntegerValueField(.keyboardEventKeycode), Int64(KeystrokeEvents.returnKeyCode))
            XCTAssertTrue(event.flags.intersection(modifierFlags).isEmpty)
        }
    }

    func testPasteIsCommandVWithTheCommandFlagOnV() throws {
        let events = try XCTUnwrap(KeystrokeEvents.events(for: .paste, source: nil))

        XCTAssertEqual(
            events.map { $0.getIntegerValueField(.keyboardEventKeycode) },
            [55, 9, 9, 55] as [Int64])
        XCTAssertEqual(events[1].type, .keyDown)
        XCTAssertEqual(events[2].type, .keyUp)
        XCTAssertTrue(events[1].flags.contains(.maskCommand))
        XCTAssertTrue(events[2].flags.contains(.maskCommand))
        for event in events {
            XCTAssertEqual(event.getIntegerValueField(.eventSourceUserData), KeystrokeEvents.syntheticMarker)
        }
    }

    private var modifierFlags: CGEventFlags {
        [.maskShift, .maskControl, .maskAlternate, .maskCommand, .maskAlphaShift]
    }

    private func unicodeString(of event: CGEvent) -> [UInt16] {
        let capacity = 64
        var length = 0
        var buffer = [UniChar](repeating: 0, count: capacity)
        event.keyboardGetUnicodeString(maxStringLength: capacity, actualStringLength: &length, unicodeString: &buffer)
        return Array(buffer.prefix(length))
    }

    private func typedText(_ keystrokes: [InjectionKeystroke]) -> String {
        keystrokes.map { keystroke -> String in
            switch keystroke {
            case .text(let units):
                return String(decoding: units, as: UTF16.self)
            case .lineBreak:
                return "\n"
            case .paste:
                return "<paste>"
            }
        }.joined()
    }
}
