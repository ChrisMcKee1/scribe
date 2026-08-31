import XCTest
@testable import Scribe

/// Exercises `AudioDeviceStore`'s persistence layer against the real `UserDefaults.standard`
/// (no dependency-injected store, matching `HotkeySettingsStoreTests`). Device enumeration itself
/// depends on live CoreAudio hardware and is not exercised here; `AudioCaptureEngine`'s manual
/// verification path (running the packaged app) is what proves that end, the same tradeoff already
/// made for `HotkeyManager`'s CGEventTap.
final class AudioDeviceStoreTests: XCTestCase {
    private var originalUID: String?
    private var originalName: String?

    override func setUp() {
        super.setUp()
        originalUID = AudioDeviceStore.selectedDeviceUID
        originalName = AudioDeviceStore.selectedDeviceName
    }

    override func tearDown() {
        AudioDeviceStore.selectedDeviceUID = originalUID
        AudioDeviceStore.selectedDeviceName = originalName
        super.tearDown()
    }

    func testDefaultsToSystemDefaultWhenNothingStored() {
        UserDefaults.standard.removeObject(forKey: "ScribeInputDeviceUID")
        UserDefaults.standard.removeObject(forKey: "ScribeInputDeviceName")
        XCTAssertNil(AudioDeviceStore.selectedDeviceUID)
        XCTAssertNil(AudioDeviceStore.selectedDeviceName)
        XCTAssertNil(AudioDeviceStore.resolveSelectedDeviceID())
    }

    func testSelectingADevicePersistsUIDAndName() {
        let device = AudioInputDevice(uid: "com.example.bluetooth-headset", name: "AirPods Pro", isDefault: false)
        AudioDeviceStore.select(device)

        XCTAssertEqual(AudioDeviceStore.selectedDeviceUID, device.uid)
        XCTAssertEqual(AudioDeviceStore.selectedDeviceName, device.name)
    }

    func testSelectingNilClearsBackToSystemDefault() {
        AudioDeviceStore.select(AudioInputDevice(uid: "some-uid", name: "Some Mic", isDefault: false))
        AudioDeviceStore.select(nil)

        XCTAssertNil(AudioDeviceStore.selectedDeviceUID)
        XCTAssertNil(AudioDeviceStore.selectedDeviceName)
    }

    func testResolveSelectedDeviceIDReturnsNilForAnUnknownUID() {
        AudioDeviceStore.selectedDeviceUID = "a-uid-that-cannot-possibly-be-connected"
        XCTAssertNil(AudioDeviceStore.resolveSelectedDeviceID())
    }
}
