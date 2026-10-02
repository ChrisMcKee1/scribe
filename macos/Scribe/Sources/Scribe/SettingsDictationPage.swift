import AppKit
import SwiftUI

struct SettingsDictationPage: View {
    let overlayPanelController: OverlayPanelController
    let hotkeyStore: HotkeySettingsStore
    let audioDeviceStore: AudioDeviceStore
    let onHotkeyChanged: (CGKeyCode) -> Void

    var body: some View {
        SettingsPage(
            title: "Dictation",
            subtitle: "Your microphone, your shortcuts, and what you see while you dictate."
        ) {
            SettingsDictationControls(
                overlayPanelController: overlayPanelController,
                hotkeyStore: hotkeyStore,
                audioDeviceStore: audioDeviceStore,
                onHotkeyChanged: onHotkeyChanged)
        }
    }
}

private struct SettingsDictationControls: View {
    @StateObject private var input: InputSettingsModel
    @StateObject private var overlay: OverlayAnchorSelection
    @StateObject private var loginItem = LoginItemSwitch()
    @State private var isRecording = false
    @State private var localMonitor: Any?

    init(
        overlayPanelController: OverlayPanelController,
        hotkeyStore: HotkeySettingsStore,
        audioDeviceStore: AudioDeviceStore,
        onHotkeyChanged: @escaping (CGKeyCode) -> Void
    ) {
        _input = StateObject(
            wrappedValue: InputSettingsModel(
                hotkeyStore: hotkeyStore,
                deviceStore: audioDeviceStore,
                onHotkeyChanged: onHotkeyChanged))
        _overlay = StateObject(wrappedValue: OverlayAnchorSelection(controller: overlayPanelController))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            SettingsGroupHeader("Microphone")
            SettingsCard { microphoneCard }

            SettingsGroupHeader("Shortcuts")
            SettingsCard { shortcutCard }
            SettingsCard { autoStopCard }

            SettingsGroupHeader("Recording indicator")
            SettingsCard { recordingIndicatorCard }

            SettingsGroupHeader("Startup")
            SettingsCard { startupCard }
        }
        .onAppear {
            input.reload()
            input.refreshDevices()
            overlay.reload()
        }
        .onDisappear {
            stopRecording()
        }
    }

    private var microphoneCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Microphone").cardTitle()
            Text("Mac default follows the microphone you choose in System Settings.")
                .cardDescription()
            Picker(
                "Microphone",
                selection: Binding(get: { input.selectedDeviceUID }, set: { input.selectDevice(uid: $0) })
            ) {
                Text("System default (recommended)").tag(String?.none)
                ForEach(input.devices) { device in
                    Text(device.isDefault ? "\(device.name) (default)" : device.name)
                        .tag(String?.some(device.uid))
                }
                if let label = input.unavailableSelectionLabel, let uid = input.selectedDeviceUID {
                    Text(label).tag(String?.some(uid))
                }
            }
            .labelsHidden()
            .frame(maxWidth: 380)
        }
    }

    private var shortcutCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Dictation shortcut").cardTitle()
            Text(input.hint).cardDescription()
            Text(
                "If the key does nothing at all, grant Scribe Input Monitoring access in System Settings > "
                    + "Privacy & Security > Input Monitoring, then relaunch Scribe."
            )
            .cardDescription()

            HStack(spacing: 12) {
                Text(isRecording ? "Press any key..." : input.binding.displayName)
                    .font(.title3.bold())
                    .padding(.horizontal, 14)
                    .padding(.vertical, 8)
                    .frame(minWidth: 160)
                    .background(
                        RoundedRectangle(cornerRadius: 8)
                            .fill(isRecording ? Color.accentColor.opacity(0.2) : Color.gray.opacity(0.12)))

                Button(isRecording ? "Cancel" : "Record New Key") {
                    isRecording ? stopRecording() : startRecording()
                }

                if !input.isDefaultBinding {
                    Button("Reset to \(HotkeyKeyCodeCatalog.displayName(for: HotkeySettingsStore.defaultKeyCode))") {
                        input.apply(keyCode: HotkeySettingsStore.defaultKeyCode)
                    }
                }
            }

            Text("Common choices")
                .font(.subheadline.weight(.semibold))
                .padding(.top, 4)
            LazyVGrid(columns: Array(repeating: GridItem(.flexible()), count: 3), spacing: 8) {
                ForEach(HotkeyKeyCodeCatalog.entries) { entry in
                    Button {
                        input.apply(keyCode: entry.keyCode)
                    } label: {
                        Text(entry.name)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 6)
                            .background(
                                input.binding.keyCode == entry.keyCode
                                    ? Color.accentColor.opacity(0.25) : Color.gray.opacity(0.1)
                            )
                            .cornerRadius(6)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private var autoStopCard: some View {
        VStack(alignment: .leading, spacing: 4) {
            Toggle("Stop when I stop talking", isOn: autoStopBinding)
                .font(.body.weight(.semibold))
            Text("Scribe stops after a few seconds of silence. A noisy room can stop it early.")
                .cardDescription()
            if !input.autoStopAppliesToBinding {
                Text("\(input.binding.displayName) is held while you talk, so it never stops on silence.")
                    .cardDescription()
            }
        }
    }

    private var recordingIndicatorCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Show the recording indicator").cardTitle()
            Text("A small bar with a live sound level appears while Scribe listens.")
                .cardDescription()
            Text("Where it appears").cardTitle()
            Text("Choose a spot for the recording indicator on your screen.")
                .cardDescription()
            LazyVGrid(columns: Array(repeating: GridItem(.flexible()), count: 3), spacing: 8) {
                ForEach(OverlayAnchor.allCases, id: \.self) { anchor in
                    Button {
                        overlay.select(anchor)
                    } label: {
                        Text(anchor.displayName)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 8)
                            .background(
                                overlay.anchor == anchor ? Color.accentColor.opacity(0.25) : Color.gray.opacity(0.1)
                            )
                            .cornerRadius(6)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private var startupCard: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Start Scribe when you log in").cardTitle()
                Text(loginItem.message)
                    .font(.caption)
                    .foregroundStyle(loginItem.refusal == nil ? Color.secondary : Color.red)
                if loginItem.showsOpenLoginItems {
                    Button("Open Login Items Settings") {
                        loginItem.openLoginItems()
                    }
                    .padding(.top, 4)
                }
            }
            Spacer()
            Toggle(
                "Start Scribe when you log in",
                isOn: Binding(
                    get: { loginItem.isOn },
                    set: { requested in Task { await loginItem.setEnabled(requested) } })
            )
            .labelsHidden()
            .disabled(!loginItem.canFlip)
        }
        .task {
            await loginItem.refresh()
        }
    }

    private var autoStopBinding: Binding<Bool> {
        Binding(get: { input.autoStopOnSilence }, set: { input.setAutoStopOnSilence($0) })
    }

    private func startRecording() {
        isRecording = true
        localMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged]) { event in
            let candidateKeyCode = CGKeyCode(event.keyCode)
            if event.type == .flagsChanged {
                guard CGEventSource.keyState(.combinedSessionState, key: candidateKeyCode) else {
                    return event
                }
            }
            input.apply(keyCode: candidateKeyCode)
            stopRecording()
            return nil
        }
    }

    private func stopRecording() {
        isRecording = false
        if let localMonitor {
            NSEvent.removeMonitor(localMonitor)
        }
        localMonitor = nil
    }
}

@MainActor
final class OverlayAnchorSelection: ObservableObject {
    static let defaultsKey = "ScribeOverlayAnchor"

    @Published private(set) var anchor: OverlayAnchor

    private let controller: OverlayPanelController
    private let defaults: UserDefaults
    private var observation: SettingsNotificationObservation?

    init(controller: OverlayPanelController, defaults: UserDefaults = .standard) {
        self.controller = controller
        self.defaults = defaults
        anchor = controller.anchor
        observation = SettingsNotificationObservation(UserDefaults.didChangeNotification) { [weak self] in
            self?.reload()
        }
    }

    func select(_ anchor: OverlayAnchor) {
        controller.anchor = anchor
        defaults.set(anchor.rawValue, forKey: Self.defaultsKey)
        reload()
    }

    func reload() {
        if controller.anchor != anchor {
            anchor = controller.anchor
        }
    }
}
