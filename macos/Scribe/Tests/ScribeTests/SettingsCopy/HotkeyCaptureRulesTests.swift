import Foundation
import XCTest

@testable import Scribe

final class HotkeyCaptureRulesTests: XCTestCase {
    func testKeysAreNamedByCodeNotByLayout() {
        XCTAssertEqual(HotkeyCaptureRules.name(61), "Right Option")
        XCTAssertEqual(HotkeyCaptureRules.name(57), "Caps Lock")
        XCTAssertEqual(HotkeyCaptureRules.name(105), "F13")
        XCTAssertEqual(HotkeyCaptureRules.name(63), "Fn")
        XCTAssertEqual(HotkeyCaptureRules.name(200), "Key 200")
        XCTAssertEqual(HotkeyCaptureRules.describe(57, toggle: true), "Press Caps Lock")
        XCTAssertEqual(HotkeyCaptureRules.describe(61, toggle: false), "Hold Right Option")
    }

    func testEveryKeyOfTheExistingCatalogHasTheSameName() {
        for entry in HotkeyKeyCodeCatalog.entries {
            XCTAssertEqual(HotkeyCaptureRules.name(UInt16(entry.keyCode)), entry.name)
        }
    }

    func testEveryCatalogKeyCanBeRecorded() {
        for entry in HotkeyKeyCodeCatalog.entries {
            guard case .accepted = HotkeyCaptureRules.evaluate(UInt16(entry.keyCode)) else {
                XCTFail("\(entry.name) is refused")
                continue
            }
        }
    }

    func testLettersNumbersAndPunctuationAreRefusedWithTheNextStep() {
        for code: UInt16 in [0, 12, 18, 29, 44, 50, 82] {
            guard case .refused(let text) = HotkeyCaptureRules.evaluate(code) else {
                XCTFail("Key \(code) was accepted")
                continue
            }
            XCTAssertTrue(text.contains("Press a key like Caps Lock, Right Option or F13."), text)
        }
    }

    func testAPrintableKeyIsNamedByWhatItTypes() {
        let verdict = HotkeyCaptureRules.evaluate(12, typed: "q")
        let text = "Q can't be a shortcut on its own: you type it all the time. "
        XCTAssertEqual(verdict, .refused(text + "Press a key like Caps Lock, Right Option or F13."))
    }

    func testEditingKeysEscapeAndGlobeAreRefused() {
        for code: UInt16 in [36, 48, 49, 51, 117, 123, 124, 125, 126] {
            guard case .refused(let text) = HotkeyCaptureRules.evaluate(code) else {
                XCTFail("Key \(code) was accepted")
                continue
            }
            XCTAssertTrue(text.contains("every app needs it"), text)
        }
        let globe = "The Globe key can't be a shortcut: macOS uses it for emoji and dictation."
        XCTAssertEqual(HotkeyCaptureRules.evaluate(63), .refused(globe))
        XCTAssertEqual(HotkeyCaptureRules.evaluate(53), .refused("Press Escape to cancel."))
    }

    func testWarningsFollowTheKey() {
        func warnings(_ code: UInt16) -> [String] {
            if case .accepted(let found) = HotkeyCaptureRules.evaluate(code) { return found }
            return ["refused"]
        }
        XCTAssertEqual(warnings(61), ["Scribe only listens to Right Option, so it still works as usual in other apps."])
        XCTAssertTrue(warnings(57)[0].hasPrefix("Caps Lock keeps its light."))
        XCTAssertTrue(warnings(55)[0].contains("Almost every keyboard shortcut uses Command"))
        XCTAssertTrue(warnings(60)[0].contains("Sticky Keys"))
        XCTAssertTrue(warnings(122)[0].contains("F1 controls brightness"))
        XCTAssertTrue(warnings(105).isEmpty)
    }

    func testTheRecorderWordsFollowTheWindowsStyle() {
        XCTAssertEqual(SettingsCopy.shortcut.prompt.render(), "Press a key\u{2026}")
        XCTAssertEqual(SettingsCopy.shortcut.prompt.windows, "Press a key, two keys or a mouse button...")
        XCTAssertNotNil(SettingsCopy.shortcut.prompt.deviation)
    }
}
