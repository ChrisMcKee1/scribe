import AppKit

@MainActor
final class MicrophoneMenu: NSObject, NSMenuDelegate {
    let item = NSMenuItem(title: "Microphone", action: nil, keyEquivalent: "")
    private let store: AudioDeviceStore
    private let devices: () -> [AudioInputDevice]
    private let openSound: () -> Void

    init(
        store: AudioDeviceStore = .live,
        devices: @escaping () -> [AudioInputDevice] = AudioDeviceStore.availableInputDevices,
        openSound: @escaping () -> Void = {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Sound-Settings.extension")!)
        }
    ) {
        self.store = store
        self.devices = devices
        self.openSound = openSound
        super.init()
        let menu = NSMenu()
        menu.delegate = self
        item.submenu = menu
        refresh()
    }

    func menuWillOpen(_ menu: NSMenu) {
        refresh()
    }

    func refresh() {
        guard let menu = item.submenu else { return }
        menu.removeAllItems()
        let selected = store.selectedDeviceUID
        let current = devices()
        let system = NSMenuItem(title: "System default", action: #selector(selectDevice(_:)), keyEquivalent: "")
        system.target = self
        system.state = selected == nil ? .on : .off
        menu.addItem(system)
        for device in current {
            let row = NSMenuItem(
                title: device.name + (device.isDefault ? " (default)" : ""),
                action: #selector(selectDevice(_:)), keyEquivalent: "")
            row.target = self
            row.representedObject = device.uid
            row.state = selected == device.uid ? .on : .off
            menu.addItem(row)
        }
        if let selected, !current.contains(where: { $0.uid == selected }) {
            let row = NSMenuItem(
                title: "Unavailable: \(store.selectedDeviceName ?? "saved microphone")",
                action: nil, keyEquivalent: "")
            row.state = .on
            row.isEnabled = false
            menu.addItem(row)
        }
        menu.addItem(.separator())
        let sound = NSMenuItem(title: "Sound settings...", action: #selector(showSound(_:)), keyEquivalent: "")
        sound.target = self
        menu.addItem(sound)
    }

    @objc private func selectDevice(_ sender: NSMenuItem) {
        if let uid = sender.representedObject as? String {
            guard let device = devices().first(where: { $0.uid == uid }) else {
                ScribeLog.warning(.audio, "Microphone menu choice disappeared")
                refresh()
                return
            }
            store.select(device)
        } else {
            store.select(nil)
        }
        refresh()
    }

    @objc private func showSound(_ sender: NSMenuItem) {
        openSound()
    }
}
