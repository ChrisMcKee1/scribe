import XCTest
@testable import Scribe

/// Exercises `AudioDeviceStore`'s saved selection against a suite of its own (`SettingsTestDefaults`), never
/// `UserDefaults.standard`. Device enumeration depends on live CoreAudio hardware and is not exercised here;
/// running the packaged app is what proves that end, the same tradeoff made for `HotkeyManager`'s event tap.
final class AudioDeviceStoreTests: XCTestCase {
    private var suite: SettingsTestDefaults!
    private var store: AudioDeviceStore!

    override func setUpWithError() throws {
        try super.setUpWithError()
        suite = try SettingsTestDefaults()
        store = AudioDeviceStore(defaults: suite.defaults)
    }

    override func tearDown() {
        suite.remove()
        suite = nil
        store = nil
        super.tearDown()
    }

    func testDefaultsToSystemDefaultWhenNothingStored() {
        XCTAssertNil(store.selectedDeviceUID)
        XCTAssertNil(store.selectedDeviceName)
        XCTAssertNil(store.resolveSelectedDeviceID())
    }

    func testSelectingADevicePersistsUIDAndName() {
        let device = AudioInputDevice(uid: "com.example.bluetooth-headset", name: "AirPods Pro", isDefault: false)
        store.select(device)

        XCTAssertEqual(store.selectedDeviceUID, device.uid)
        XCTAssertEqual(store.selectedDeviceName, device.name)
        let reopened = AudioDeviceStore(defaults: suite.defaults)
        XCTAssertEqual(reopened.selectedDeviceUID, device.uid)
    }

    func testSelectingNilClearsBackToSystemDefault() {
        store.select(AudioInputDevice(uid: "some-uid", name: "Some Mic", isDefault: false))
        store.select(nil)

        XCTAssertNil(store.selectedDeviceUID)
        XCTAssertNil(store.selectedDeviceName)
    }

    func testResolveSelectedDeviceIDReturnsNilForAnUnknownUID() {
        store.selectedDeviceUID = "a-uid-that-cannot-possibly-be-connected"
        XCTAssertNil(store.resolveSelectedDeviceID())
    }

    func testWritesOnlyToItsOwnSuite() throws {
        let other = try SettingsTestDefaults()
        defer { other.remove() }

        store.select(AudioInputDevice(uid: "some-uid", name: "Some Mic", isDefault: false))

        XCTAssertNil(AudioDeviceStore(defaults: other.defaults).selectedDeviceUID)
    }
}
