import CoreGraphics
import Foundation

/// The bound push-to-talk key, kept current while a window shows it. It re-reads the store after any preference
/// write in this process, so a Welcome window left open follows a rebind made in Settings.
@MainActor
final class HotkeyBindingModel: ObservableObject {
    @Published private(set) var binding: HotkeyBinding

    private let store: HotkeySettingsStore
    private var observation: SettingsNotificationObservation?

    init(store: HotkeySettingsStore, center: NotificationCenter = .default) {
        self.store = store
        binding = store.binding
        _ = center // MUTATION M8: Welcome keeps the key it was created with
    }

    func reload() {
        let stored = store.binding
        if stored != binding {
            binding = stored
        }
    }
}

/// The Input tab: the push-to-talk key and the microphone. Both are stored the moment they change, and the tab
/// re-reads them after any preference write in this process.
@MainActor
final class InputSettingsModel: ObservableObject {
    @Published private(set) var binding: HotkeyBinding
    @Published private(set) var devices: [AudioInputDevice]
    @Published private(set) var selectedDeviceUID: String?
    @Published private(set) var selectedDeviceName: String?

    private let hotkeyStore: HotkeySettingsStore
    private let deviceStore: AudioDeviceStore
    private let listDevices: () -> [AudioInputDevice]
    private let onHotkeyChanged: (CGKeyCode) -> Void
    private var observation: SettingsNotificationObservation?

    init(
        hotkeyStore: HotkeySettingsStore,
        deviceStore: AudioDeviceStore,
        listDevices: @escaping () -> [AudioInputDevice] = AudioDeviceStore.availableInputDevices,
        onHotkeyChanged: @escaping (CGKeyCode) -> Void,
        center: NotificationCenter = .default
    ) {
        self.hotkeyStore = hotkeyStore
        self.deviceStore = deviceStore
        self.listDevices = listDevices
        self.onHotkeyChanged = onHotkeyChanged
        binding = hotkeyStore.binding
        devices = listDevices()
        selectedDeviceUID = deviceStore.selectedDeviceUID
        selectedDeviceName = deviceStore.selectedDeviceName
        observation = SettingsNotificationObservation(UserDefaults.didChangeNotification, center: center) {
            [weak self] in
            self?.reload()
        }
    }

    var hint: String {
        HotkeyHint.settings(for: binding)
    }

    var isDefaultBinding: Bool {
        binding.keyCode == HotkeySettingsStore.defaultKeyCode
    }

    /// Stores `keyCode` and hands it to the running hotkey listener in the same step, so the next press uses it.
    func apply(keyCode: CGKeyCode) {
        hotkeyStore.keyCode = keyCode
        reload()
        onHotkeyChanged(keyCode)
    }

    /// Stores the microphone with this UID, or "system default" for nil. Choosing the saved microphone while it is
    /// unplugged changes nothing, so its saved name survives until it comes back or another one is chosen.
    func selectDevice(uid: String?) {
        guard uid != selectedDeviceUID else { return }
        if let uid {
            deviceStore.select(devices.first(where: { $0.uid == uid })) // MUTATION M12: unlisted UID clears
        } else {
            deviceStore.select(nil)
        }
        reload()
    }

    /// The picker's entry for a saved microphone that is not connected now, or nil when there is none.
    var unavailableSelectionLabel: String? {
        guard let uid = selectedDeviceUID, !devices.contains(where: { $0.uid == uid }) else { return nil }
        return "Unavailable: \(selectedDeviceName ?? "saved microphone")"
    }

    func refreshDevices() {
        let current = listDevices()
        if current != devices {
            devices = current
        }
    }

    func reload() {
        let stored = hotkeyStore.binding
        if stored != binding {
            binding = stored
        }
        let uid = deviceStore.selectedDeviceUID
        if uid != selectedDeviceUID {
            selectedDeviceUID = uid
        }
        let name = deviceStore.selectedDeviceName
        if name != selectedDeviceName {
            selectedDeviceName = name
        }
    }
}
