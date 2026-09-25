import CoreGraphics
import XCTest

@testable import Scribe

final class InputSettingsModelTests: XCTestCase {
    private var suite: SettingsTestDefaults!
    private var hotkeyStore: HotkeySettingsStore!
    private var deviceStore: AudioDeviceStore!

    private let headset = AudioInputDevice(uid: "usb-headset", name: "USB Headset", isDefault: false)
    private let builtIn = AudioInputDevice(uid: "built-in", name: "MacBook Pro Microphone", isDefault: true)

    override func setUpWithError() throws {
        try super.setUpWithError()
        suite = try SettingsTestDefaults()
        hotkeyStore = HotkeySettingsStore(defaults: suite.defaults)
        deviceStore = AudioDeviceStore(defaults: suite.defaults)
    }

    override func tearDown() {
        suite.remove()
        suite = nil
        hotkeyStore = nil
        deviceStore = nil
        super.tearDown()
    }

    @MainActor
    private func makeModel(
        devices: [AudioInputDevice]? = nil,
        onHotkeyChanged: @escaping (CGKeyCode) -> Void = { _ in }
    ) -> InputSettingsModel {
        let listed = devices ?? [builtIn, headset]
        return InputSettingsModel(
            hotkeyStore: hotkeyStore,
            deviceStore: deviceStore,
            listDevices: { listed },
            onHotkeyChanged: onHotkeyChanged)
    }

    @MainActor
    func testApplyingAKeyStoresItAndHandsItToTheListener() {
        var handedOver: [CGKeyCode] = []
        let model = makeModel(onHotkeyChanged: { handedOver.append($0) })

        model.apply(keyCode: 61)

        XCTAssertEqual(hotkeyStore.keyCode, 61)
        XCTAssertEqual(handedOver, [61])
        XCTAssertEqual(model.binding, HotkeyBinding(keyCode: 61))
        XCTAssertFalse(model.isDefaultBinding)
        XCTAssertTrue(model.hint.hasPrefix("Hold Right Option"), model.hint)
    }

    @MainActor
    func testTheHintFollowsCapsLockAsAToggle() {
        let model = makeModel()
        XCTAssertTrue(model.isDefaultBinding)
        XCTAssertTrue(model.hint.hasPrefix("Tap Caps Lock"), model.hint)
    }

    @MainActor
    func testChoosingAMicrophoneStoresItsUIDAndName() {
        let model = makeModel()

        model.selectDevice(uid: headset.uid)

        XCTAssertEqual(deviceStore.selectedDeviceUID, headset.uid)
        XCTAssertEqual(deviceStore.selectedDeviceName, headset.name)
        XCTAssertEqual(model.selectedDeviceUID, headset.uid)
        XCTAssertNil(model.unavailableSelectionLabel)
    }

    @MainActor
    func testChoosingSystemDefaultClearsTheSavedMicrophone() {
        deviceStore.select(headset)
        let model = makeModel()

        model.selectDevice(uid: nil)

        XCTAssertNil(deviceStore.selectedDeviceUID)
        XCTAssertNil(deviceStore.selectedDeviceName)
        XCTAssertNil(model.selectedDeviceUID)
    }

    @MainActor
    func testAnUnpluggedSavedMicrophoneKeepsItsNameAndIsListedAsUnavailable() {
        deviceStore.select(headset)
        let model = makeModel(devices: [builtIn])

        XCTAssertEqual(model.unavailableSelectionLabel, "Unavailable: USB Headset")

        // Picking the entry that is already selected must not clear the saved microphone.
        model.selectDevice(uid: headset.uid)
        XCTAssertEqual(deviceStore.selectedDeviceUID, headset.uid)
        XCTAssertEqual(deviceStore.selectedDeviceName, headset.name)
    }

    @MainActor
    func testAUIDThatIsNotListedLeavesTheSavedMicrophoneAlone() {
        deviceStore.select(builtIn)
        let model = makeModel(devices: [builtIn])

        model.selectDevice(uid: "not-connected")

        XCTAssertEqual(deviceStore.selectedDeviceUID, builtIn.uid)
        XCTAssertEqual(model.selectedDeviceUID, builtIn.uid)
    }

    @MainActor
    func testAChangeStoredElsewhereIsShownWithoutReopeningTheTab() {
        let model = makeModel()

        hotkeyStore.keyCode = 105
        deviceStore.select(headset)
        hotkeyStore.autoStopOnSilence = true

        XCTAssertEqual(model.binding.keyCode, 105)
        XCTAssertEqual(model.selectedDeviceUID, headset.uid)
        XCTAssertTrue(model.autoStopOnSilence)
    }

    /// The switch starts off, is stored the moment it changes (the next press reads the store), and applies only to a
    /// key tapped on and off.
    @MainActor
    func testTheSilenceAutoStopSwitchIsOffByDefaultAndStoredAtOnce() {
        let model = makeModel()
        XCTAssertFalse(model.autoStopOnSilence)
        XCTAssertTrue(model.autoStopAppliesToBinding, "Caps Lock is tapped on and off")

        model.setAutoStopOnSilence(true)
        XCTAssertTrue(hotkeyStore.autoStopOnSilence)
        XCTAssertTrue(model.autoStopOnSilence)

        model.apply(keyCode: 61)
        XCTAssertFalse(model.autoStopAppliesToBinding, "a held key never stops on silence")
        XCTAssertTrue(hotkeyStore.autoStopOnSilence, "rebinding changed the choice")

        model.setAutoStopOnSilence(false)
        XCTAssertFalse(hotkeyStore.autoStopOnSilence)
        XCTAssertFalse(model.autoStopOnSilence)
    }

    @MainActor
    func testRefreshingDevicesPicksUpANewMicrophone() {
        var listed = [builtIn]
        let model = InputSettingsModel(
            hotkeyStore: hotkeyStore,
            deviceStore: deviceStore,
            listDevices: { listed },
            onHotkeyChanged: { _ in })
        XCTAssertEqual(model.devices, [builtIn])

        listed.append(headset)
        model.refreshDevices()

        XCTAssertEqual(model.devices, [builtIn, headset])
    }
}
