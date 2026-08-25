import XCTest
@testable import Scribe

final class HotkeyKeyCodeCatalogTests: XCTestCase {
    func testDisplayNameReturnsCuratedNameForKnownKeyCode() {
        XCTAssertEqual(HotkeyKeyCodeCatalog.displayName(for: 61), "Right Option")
        XCTAssertEqual(HotkeyKeyCodeCatalog.displayName(for: 62), "Right Control")
    }

    func testDisplayNameFallsBackToRawKeyCodeForUnknownKey() {
        XCTAssertEqual(HotkeyKeyCodeCatalog.displayName(for: 999), "Key code 999")
    }

    func testEntriesHaveNoDuplicateKeyCodes() {
        let keyCodes = HotkeyKeyCodeCatalog.entries.map(\.keyCode)
        XCTAssertEqual(keyCodes.count, Set(keyCodes).count)
    }
}

/// Exercises `HotkeySettingsStore` against the real `UserDefaults.standard` (no dependency-injected
/// store to substitute, matching the rest of this port's stopgap-persistence tests). The original
/// value is captured in `setUp` and restored in `tearDown` so running this suite never overwrites a
/// developer's real saved hotkey binding.
final class HotkeySettingsStoreTests: XCTestCase {
    private var originalKeyCode: CGKeyCode = HotkeySettingsStore.defaultKeyCode

    override func setUp() {
        super.setUp()
        originalKeyCode = HotkeySettingsStore.keyCode
    }

    override func tearDown() {
        HotkeySettingsStore.keyCode = originalKeyCode
        super.tearDown()
    }

    func testDefaultsToRightOptionWhenNothingStored() {
        UserDefaults.standard.removeObject(forKey: "ScribePushToTalkKeyCode")
        XCTAssertEqual(HotkeySettingsStore.keyCode, 61)
        XCTAssertEqual(HotkeySettingsStore.keyCode, HotkeySettingsStore.defaultKeyCode)
    }

    func testRoundTripsAStoredKeyCode() {
        HotkeySettingsStore.keyCode = 62
        XCTAssertEqual(HotkeySettingsStore.keyCode, 62)

        HotkeySettingsStore.keyCode = 105
        XCTAssertEqual(HotkeySettingsStore.keyCode, 105)
    }
}

/// `HotkeyManager.keyCode` is a plain, publicly settable property (no event tap needs recreating
/// to change it; `isPushToTalkEvent` reads it live), so this only needs to verify the default and
/// that assignment sticks, without standing up a real CGEvent tap.
final class HotkeyManagerKeyCodeTests: XCTestCase {
    func testDefaultsToRightOptionKeyCode() {
        let manager = HotkeyManager(audioCaptureEngine: AudioCaptureEngine(), logSink: { _ in })
        XCTAssertEqual(manager.keyCode, HotkeySettingsStore.defaultKeyCode)
    }

    func testKeyCodeCanBeReassignedLive() {
        let manager = HotkeyManager(audioCaptureEngine: AudioCaptureEngine(), logSink: { _ in })
        manager.keyCode = 105
        XCTAssertEqual(manager.keyCode, 105)
    }
}
