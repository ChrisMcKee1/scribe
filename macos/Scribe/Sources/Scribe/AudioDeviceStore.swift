import CoreAudio
import Foundation

/// A CoreAudio input-capable device, keyed by its persistent UID (stable across reboots and
/// reconnects) rather than its `AudioDeviceID` (only stable for the current boot session).
/// Mirrors Windows' `AudioDevice(Id, Name, IsDefault)` record.
struct AudioInputDevice: Identifiable, Equatable {
    let uid: String
    let name: String
    let isDefault: Bool

    var id: String { uid }
}

/// The chosen microphone, kept in `UserDefaults` by its persistent UID. Production uses `UserDefaults.standard`
/// through `live`; tests pass a suite of their own. `nil` means "system default", matching Windows'
/// `InputDeviceId == null` convention. Bluetooth microphones (AirPods, headsets) need no special handling here:
/// CoreAudio surfaces them as ordinary input devices the moment macOS has them connected as an audio input.
///
/// The device list comes from live CoreAudio hardware rather than stored state, so those queries stay static.
struct AudioDeviceStore {
    private static let uidKey = "ScribeInputDeviceUID"
    private static let nameKey = "ScribeInputDeviceName"

    static var live: AudioDeviceStore {
        AudioDeviceStore(defaults: .standard)
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
    }

    /// The saved device UID, or `nil` for "system default". Saved alongside `selectedDeviceName` so Settings can
    /// still name a saved microphone that is unplugged ("Unavailable: My Headset"), the same fallback Windows
    /// shows for a saved but missing device.
    var selectedDeviceUID: String? {
        get { defaults.string(forKey: Self.uidKey) }
        nonmutating set { defaults.set(newValue, forKey: Self.uidKey) }
    }

    var selectedDeviceName: String? {
        get { defaults.string(forKey: Self.nameKey) }
        nonmutating set { defaults.set(newValue, forKey: Self.nameKey) }
    }

    /// Saves the chosen device, or clears the choice back to "system default" when passed `nil`.
    func select(_ device: AudioInputDevice?) {
        selectedDeviceUID = device?.uid
        selectedDeviceName = device?.name
    }

    /// The saved UID resolved to a live `AudioDeviceID` for this boot session, so `AudioCaptureEngine` can point
    /// the capture unit at it. `nil` means "use the system default", including when the saved device can no
    /// longer be found (unplugged, out of range).
    func resolveSelectedDeviceID() -> AudioDeviceID? {
        guard let uid = selectedDeviceUID, let deviceIDs = Self.allDeviceIDs() else { return nil }
        return deviceIDs.first { Self.deviceUID($0) == uid }
    }

    // MARK: Live store, for callers that cannot take a store yet (AudioCaptureEngine, the CLI verbs)

    static var selectedDeviceUID: String? {
        get { live.selectedDeviceUID }
        set {
            let store = live
            store.selectedDeviceUID = newValue
        }
    }

    static var selectedDeviceName: String? {
        get { live.selectedDeviceName }
        set {
            let store = live
            store.selectedDeviceName = newValue
        }
    }

    static func select(_ device: AudioInputDevice?) {
        live.select(device)
    }

    static func resolveSelectedDeviceID() -> AudioDeviceID? {
        live.resolveSelectedDeviceID()
    }

    // MARK: Hardware

    /// Every currently connected input-capable device (built-in mic, USB, Bluetooth HFP/AirPods,
    /// or a virtual device such as a conferencing app's audio device), each with its stable UID.
    static func availableInputDevices() -> [AudioInputDevice] {
        guard let deviceIDs = allDeviceIDs() else { return [] }
        let defaultDeviceID = defaultInputDeviceID()

        return deviceIDs.compactMap { deviceID -> AudioInputDevice? in
            guard hasInputStreams(deviceID), let uid = deviceUID(deviceID), let name = deviceName(deviceID) else {
                return nil
            }
            return AudioInputDevice(uid: uid, name: name, isDefault: deviceID == defaultDeviceID)
        }
    }

    /// Looks up the UID/name for an arbitrary `AudioDeviceID`, e.g. one read back from a live
    /// `AudioUnit` via `kAudioOutputUnitProperty_CurrentDevice`, for diagnostics such as
    /// `--verify-selected-microphone`.
    static func describe(_ deviceID: AudioDeviceID) -> AudioInputDevice? {
        guard let uid = deviceUID(deviceID), let name = deviceName(deviceID) else { return nil }
        return AudioInputDevice(uid: uid, name: name, isDefault: deviceID == defaultInputDeviceID())
    }

    /// Ground-truth check for whether a given device is actively being used for I/O right now,
    /// independent of whatever `AVAudioEngine`/AUHAL reports for its own current-device property
    /// (which can point at an internal aggregate wrapper on modern macOS). Used to verify a
    /// specific microphone selection actually took effect.
    static func isDeviceRunning(uid: String) -> Bool {
        guard let deviceIDs = allDeviceIDs(), let deviceID = deviceIDs.first(where: { deviceUID($0) == uid }) else {
            return false
        }

        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceIsRunningSomewhere,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)

        var isRunning: UInt32 = 0
        var dataSize = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &isRunning) == noErr else {
            return false
        }
        return isRunning != 0
    }

    private static func allDeviceIDs() -> [AudioDeviceID]? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)

        var dataSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &dataSize) == noErr,
            dataSize > 0
        else {
            return nil
        }

        let count = Int(dataSize) / MemoryLayout<AudioDeviceID>.size
        var deviceIDs = [AudioDeviceID](repeating: 0, count: count)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &dataSize, &deviceIDs) == noErr else {
            return nil
        }
        return deviceIDs
    }

    private static func defaultInputDeviceID() -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultInputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)

        var deviceID = AudioDeviceID(0)
        var dataSize = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &dataSize, &deviceID) == noErr else {
            return nil
        }
        return deviceID
    }

    /// A device with zero input channels (e.g. a set of output-only speakers) is filtered out by
    /// checking its input-scoped stream configuration rather than assuming every hardware device
    /// supports capture.
    private static func hasInputStreams(_ deviceID: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)

        var dataSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(deviceID, &address, 0, nil, &dataSize) == noErr, dataSize > 0 else {
            return false
        }

        let bufferListPointer = UnsafeMutableRawPointer.allocate(byteCount: Int(dataSize), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { bufferListPointer.deallocate() }

        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, bufferListPointer) == noErr else {
            return false
        }

        let bufferList = bufferListPointer.assumingMemoryBound(to: AudioBufferList.self)
        return UnsafeMutableAudioBufferListPointer(bufferList).contains { $0.mNumberChannels > 0 }
    }

    private static func deviceUID(_ deviceID: AudioDeviceID) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceUID,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)

        var uid: Unmanaged<CFString>?
        var dataSize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &uid) == noErr, let uid else {
            return nil
        }
        return uid.takeRetainedValue() as String
    }

    private static func deviceName(_ deviceID: AudioDeviceID) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioObjectPropertyName,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)

        var name: Unmanaged<CFString>?
        var dataSize = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, &name) == noErr, let name else {
            return nil
        }
        return name.takeRetainedValue() as String
    }
}
