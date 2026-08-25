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

/// `UserDefaults`-backed storage for the selected microphone, the same stopgap pattern used by
/// `HotkeySettingsStore` (a general structured settings store doesn't exist yet on macOS).
/// `nil` means "system default", matching Windows' `InputDeviceId == null` convention. Bluetooth
/// microphones (AirPods, headsets) need no special handling here: CoreAudio surfaces them as
/// ordinary input devices the moment macOS has them connected as an audio input.
enum AudioDeviceStore {
    private static let uidKey = "ScribeInputDeviceUID"
    private static let nameKey = "ScribeInputDeviceName"

    /// The persisted device UID, or `nil` for "system default". Stored alongside `selectedDeviceName`
    /// so the Settings UI can still show something meaningful (e.g. "Unavailable: My Headset") if the
    /// device is unplugged, the same fallback Windows shows for a saved-but-missing device.
    static var selectedDeviceUID: String? {
        get { UserDefaults.standard.string(forKey: uidKey) }
        set {
            UserDefaults.standard.set(newValue, forKey: uidKey)
        }
    }

    static var selectedDeviceName: String? {
        get { UserDefaults.standard.string(forKey: nameKey) }
        set { UserDefaults.standard.set(newValue, forKey: nameKey) }
    }

    /// Records the chosen device (or clears it back to "system default" when passed `nil`).
    static func select(_ device: AudioInputDevice?) {
        selectedDeviceUID = device?.uid
        selectedDeviceName = device?.name
    }

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

    /// Resolves the persisted UID (if any) to a live `AudioDeviceID` for this boot session, so
    /// `AudioCaptureEngine` can point the capture unit at it. Returns `nil` for "use system
    /// default" and also when a saved device can no longer be found (e.g. unplugged/out of range).
    static func resolveSelectedDeviceID() -> AudioDeviceID? {
        guard let uid = selectedDeviceUID, let deviceIDs = allDeviceIDs() else { return nil }
        return deviceIDs.first { deviceUID($0) == uid }
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
