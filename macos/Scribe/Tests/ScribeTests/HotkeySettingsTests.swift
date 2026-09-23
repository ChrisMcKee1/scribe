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

/// Runs against a suite of its own (`SettingsTestDefaults`), never `UserDefaults.standard`, so it cannot touch
/// the developer's binding or race another test process.
final class HotkeySettingsStoreTests: XCTestCase {
    private var suite: SettingsTestDefaults!
    private var store: HotkeySettingsStore!

    override func setUpWithError() throws {
        try super.setUpWithError()
        suite = try SettingsTestDefaults()
        store = HotkeySettingsStore(defaults: suite.defaults)
    }

    override func tearDown() {
        suite.remove()
        suite = nil
        store = nil
        super.tearDown()
    }

    func testDefaultsToCapsLockWhenNothingStored() {
        XCTAssertEqual(store.keyCode, 57)
        XCTAssertEqual(store.keyCode, HotkeySettingsStore.defaultKeyCode)
        XCTAssertEqual(store.binding.gesture, .toggle)
    }

    func testRoundTripsAStoredKeyCode() {
        store.keyCode = 62
        XCTAssertEqual(store.keyCode, 62)

        store.keyCode = 105
        XCTAssertEqual(store.keyCode, 105)
    }

    func testWritesOnlyToItsOwnSuite() throws {
        let other = try SettingsTestDefaults()
        defer { other.remove() }

        store.keyCode = 62

        XCTAssertEqual(HotkeySettingsStore(defaults: other.defaults).keyCode, HotkeySettingsStore.defaultKeyCode)
    }

    func testAStoredValueThatIsNotAKeyCodeFallsBackToTheDefault() {
        suite.defaults.set(-1, forKey: "ScribePushToTalkKeyCode")
        XCTAssertEqual(store.keyCode, HotkeySettingsStore.defaultKeyCode)

        suite.defaults.set("Right Option", forKey: "ScribePushToTalkKeyCode")
        XCTAssertEqual(store.keyCode, HotkeySettingsStore.defaultKeyCode)
    }
}

final class HotkeyBindingTests: XCTestCase {
    func testCapsLockIsATapToggleAndEveryOtherKeyIsHeld() {
        XCTAssertEqual(HotkeyBinding(keyCode: 57).gesture, .toggle)
        for entry in HotkeyKeyCodeCatalog.entries where entry.keyCode != 57 {
            XCTAssertEqual(HotkeyBinding(keyCode: entry.keyCode).gesture, .hold, entry.name)
        }
    }

    func testWelcomeHintNamesTheBoundKeyAndItsGesture() {
        let capsLock = HotkeyHint.welcome(for: HotkeyBinding(keyCode: 57))
        XCTAssertTrue(capsLock.hasPrefix("Tap Caps Lock"), capsLock)
        XCTAssertTrue(capsLock.contains("Tap it again"), capsLock)
        XCTAssertFalse(capsLock.contains("Hold"), capsLock)

        let rightOption = HotkeyHint.welcome(for: HotkeyBinding(keyCode: 61))
        XCTAssertTrue(rightOption.hasPrefix("Hold Right Option"), rightOption)
        XCTAssertTrue(rightOption.contains("Release it"), rightOption)
    }

    func testWelcomeHintNeverNamesAKeyThatIsNotBound() {
        for entry in HotkeyKeyCodeCatalog.entries {
            let hint = HotkeyHint.welcome(for: HotkeyBinding(keyCode: entry.keyCode))
            XCTAssertTrue(hint.contains(entry.name), hint)
            for other in HotkeyKeyCodeCatalog.entries where other.keyCode != entry.keyCode {
                XCTAssertFalse(hint.contains("\(other.name) "), "\(hint) names \(other.name)")
            }
        }
    }

    func testSettingsHintFollowsTheGesture() {
        let capsLock = HotkeyHint.settings(for: HotkeyBinding(keyCode: 57))
        XCTAssertTrue(capsLock.hasPrefix("Tap Caps Lock once"), capsLock)

        let f13 = HotkeyHint.settings(for: HotkeyBinding(keyCode: 105))
        XCTAssertTrue(f13.hasPrefix("Hold F13"), f13)
        XCTAssertTrue(f13.contains("release it to stop"), f13)
    }

    func testHintsForAnUncataloguedKeyUseItsKeyCode() {
        let hint = HotkeyHint.welcome(for: HotkeyBinding(keyCode: 122))
        XCTAssertTrue(hint.hasPrefix("Hold Key code 122"), hint)
    }
}

final class HotkeyBindingModelTests: XCTestCase {
    private var suite: SettingsTestDefaults!

    override func setUpWithError() throws {
        try super.setUpWithError()
        suite = try SettingsTestDefaults()
    }

    override func tearDown() {
        suite.remove()
        suite = nil
        super.tearDown()
    }

    /// A Welcome window left open follows a rebind made in Settings, because the model re-reads the store after any
    /// preference write rather than keeping the key it was created with.
    @MainActor
    func testFollowsARebindStoredWhileItIsShown() {
        let store = HotkeySettingsStore(defaults: suite.defaults)
        let model = HotkeyBindingModel(store: store)
        XCTAssertEqual(model.binding.keyCode, HotkeySettingsStore.defaultKeyCode)

        store.keyCode = 61

        XCTAssertEqual(model.binding, HotkeyBinding(keyCode: 61))
        XCTAssertTrue(HotkeyHint.welcome(for: model.binding).hasPrefix("Hold Right Option"))
    }
}

/// `HotkeyManager.keyCode` is a plain, publicly settable property (no event tap needs recreating
/// to change it; `isPushToTalkEvent` reads it live), so this only needs to verify the starting key
/// and that assignment sticks, without standing up a real CGEvent tap.
final class HotkeyManagerKeyCodeTests: XCTestCase {
    func testStartsFromTheStoredKey() {
        // Reads the live store without writing it; the value is whatever this Mac has saved.
        let manager = HotkeyManager(audioCaptureEngine: AudioCaptureEngine(), logSink: { _ in })
        XCTAssertEqual(manager.keyCode, HotkeySettingsStore.keyCode)
    }

    func testKeyCodeCanBeReassignedLive() {
        let manager = HotkeyManager(audioCaptureEngine: AudioCaptureEngine(), logSink: { _ in })
        manager.keyCode = 105
        XCTAssertEqual(manager.keyCode, 105)
    }
}
