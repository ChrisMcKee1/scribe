import AppKit
import XCTest

@testable import Scribe

@MainActor
final class MicrophoneMenuTests: XCTestCase {
    func testDefaultAndMissingMicrophoneAreDisplayedHonestly() throws {
        let name = UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let store = AudioDeviceStore(defaults: defaults)
        let device = AudioInputDevice(uid: "mic", name: "Test microphone", isDefault: true)
        let picker = MicrophoneMenu(store: store, devices: { [device] }, openSound: {})
        var rows = try XCTUnwrap(picker.item.submenu).items
        XCTAssertEqual(rows[0].state, .on)
        XCTAssertEqual(rows[1].title, "Test microphone (default)")
        store.select(AudioInputDevice(uid: "missing", name: "Unplugged microphone", isDefault: false))
        picker.refresh()
        rows = try XCTUnwrap(picker.item.submenu).items
        XCTAssertEqual(rows[0].state, .off)
        XCTAssertEqual(rows[2].title, "Unavailable: Unplugged microphone")
        XCTAssertEqual(rows[2].state, .on)
        XCTAssertFalse(rows[2].isEnabled)
        XCTAssertEqual(store.selectedDeviceUID, "missing")
    }

    func testTheDeviceAndDefaultActionsSaveOnlyTheMicrophoneChoice() throws {
        let name = UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let store = AudioDeviceStore(defaults: defaults)
        defaults.set("keep", forKey: "UnrelatedSetting")
        let device = AudioInputDevice(uid: "mic", name: "Test microphone", isDefault: false)
        let picker = MicrophoneMenu(store: store, devices: { [device] }, openSound: {})
        let menu = try XCTUnwrap(picker.item.submenu)
        let deviceItem = menu.items[1]
        XCTAssertTrue(deviceItem.target === picker)
        picker.perform(try XCTUnwrap(deviceItem.action), with: deviceItem)
        XCTAssertEqual(store.selectedDeviceUID, "mic")
        XCTAssertEqual(store.selectedDeviceName, "Test microphone")
        let defaultItem = menu.items[0]
        picker.perform(try XCTUnwrap(defaultItem.action), with: defaultItem)
        XCTAssertNil(store.selectedDeviceUID)
        XCTAssertNil(store.selectedDeviceName)
        XCTAssertEqual(defaults.string(forKey: "UnrelatedSetting"), "keep")
    }
}
