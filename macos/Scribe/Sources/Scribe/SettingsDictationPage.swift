import AppKit
import SwiftUI

struct SettingsDictationPage: View {
    @ObservedObject var drafts: SettingsDrafts
    let overlayPanelController: OverlayPanelController
    let hotkeyStore: HotkeySettingsStore
    let audioDeviceStore: AudioDeviceStore
    let onHotkeyChanged: (CGKeyCode) -> Void
    let onTryDictation: () -> Void

    var body: some View {
        SettingsPage(
            title: "Dictation",
            subtitle: "Your microphone, your shortcuts, and what you see while you dictate."
        ) {
            SettingsDictationControls(
                drafts: drafts,
                hotkeyStore: hotkeyStore,
                audioDeviceStore: audioDeviceStore,
                onHotkeyChanged: onHotkeyChanged,
                onTryDictation: onTryDictation)
        }
        .onAppear {
            drafts.configureIndicator(controller: overlayPanelController)
        }
    }
}

private struct SettingsDictationControls: View {
    @ObservedObject private var drafts: SettingsDrafts
    @StateObject private var input: InputSettingsModel
    @StateObject private var loginItem = LoginItemSwitch()
    @State private var isRecording = false
    @State private var localMonitor: Any?

    private let onTryDictation: () -> Void

    init(
        drafts: SettingsDrafts,
        hotkeyStore: HotkeySettingsStore,
        audioDeviceStore: AudioDeviceStore,
        onHotkeyChanged: @escaping (CGKeyCode) -> Void,
        onTryDictation: @escaping () -> Void
    ) {
        self.onTryDictation = onTryDictation
        self.drafts = drafts
        _input = StateObject(
            wrappedValue: InputSettingsModel(
                hotkeyStore: hotkeyStore,
                deviceStore: audioDeviceStore,
                onHotkeyChanged: onHotkeyChanged))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            SettingsGroupHeader("Microphone")
            SettingsCard(searchID: "dictation.microphone") { microphoneCard }

            SettingsGroupHeader("Shortcuts")
            SettingsCard(searchID: "dictation.shortcut") { shortcutCard }
            SettingsCard(searchID: "dictation.silence-stop") { autoStopCard }

            SettingsGroupHeader("Text insertion")
            SettingsCard(searchID: "dictation.space") { InputTypingSettingsSection() }

            SettingsGroupHeader("Recording indicator")
            SettingsCard(searchID: "dictation.indicator") { recordingIndicatorCard }
            SettingsCard(searchID: "dictation.indicator.position") { recordingPositionCard }

            SettingsGroupHeader("Startup")
            SettingsCard(searchID: "dictation.startup") { startupCard }
        }
        .onAppear {
            input.reload()
            input.refreshDevices()
            drafts.indicator?.reload()
        }
        .onDisappear {
            stopRecording()
            drafts.indicator?.cancelPreview()
        }
    }

    private var microphoneCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Microphone").cardTitle()
            Text("Mac default follows the microphone you choose in System Settings.")
                .cardDescription()
            Picker(
                "Microphone",
                selection: Binding(get: { input.selectedDeviceUID }, set: { input.selectDevice(uid: $0) })
            ) {
                Text(defaultMicrophoneLabel).tag(String?.none)
                ForEach(input.devices) { device in
                    Text(device.isDefault ? "\(device.name) (Mac default)" : device.name)
                        .tag(String?.some(device.uid))
                }
                if let label = input.unavailableSelectionLabel, let uid = input.selectedDeviceUID {
                    Text(label).tag(String?.some(uid))
                }
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .frame(maxWidth: .infinity, alignment: .leading)

            HStack(spacing: 8) {
                Button("Sound settings") { openSoundSettings() }
                Button("Try dictation") { onTryDictation() }
            }
        }
    }

    private var defaultMicrophoneLabel: String {
        if let device = input.devices.first(where: \.isDefault) {
            return "Mac default: \(device.name)"
        }
        return "Mac default"
    }

    private var shortcutCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Dictation shortcut").cardTitle()
            HStack(spacing: 12) {
                Text(isRecording ? "Press any key..." : input.binding.displayName)
                    .font(.title3.bold())
                    .padding(.horizontal, 14)
                    .padding(.vertical, 8)
                    .frame(minWidth: 180)
                    .background(
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .fill(isRecording ? Color.accentColor.opacity(0.2) : Color.gray.opacity(0.12)))

                Button(isRecording ? "Cancel" : "Change") {
                    isRecording ? stopRecording() : startRecording()
                }

                Text(shortcutModeText)
                    .font(.callout.weight(.semibold))
                    .foregroundStyle(.secondary)
            }

            Text(input.hint).cardDescription()
            Text(
                "If the key does nothing at all, grant Scribe Input Monitoring access in System Settings > Privacy & Security > Input Monitoring, then relaunch Scribe."
            )
            .cardDescription()

            Button("Restore default shortcut") {
                input.apply(keyCode: HotkeySettingsStore.defaultKeyCode)
            }
            .disabled(input.isDefaultBinding)

            DisclosureGroup("Common choices") {
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
                .padding(.top, 8)
            }
            .font(.caption.weight(.semibold))
            .padding(.top, 2)
        }
    }

    private var shortcutModeText: String {
        switch input.binding.gesture {
        case .hold:
            return "Hold to dictate"
        case .toggle:
            return "Press to start or stop"
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
        VStack(alignment: .leading, spacing: 4) {
            Toggle(
                "Show the recording indicator",
                isOn: Binding(
                    get: { drafts.indicator?.showIndicator ?? true },
                    set: { drafts.indicator?.showIndicator = $0 })
            )
            .cardTitle()
            Text(
                "Shows listening, processing and the result. Save applies this choice; Discard keeps your saved choice."
            )
            .cardDescription()
        }
    }

    private var recordingPositionCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Where it appears").cardTitle()
            Text("Choose a spot for the recording indicator on your screen.")
                .cardDescription()
            LazyVGrid(columns: Array(repeating: GridItem(.flexible()), count: 3), spacing: 8) {
                ForEach(OverlayAnchor.allCases, id: \.self) { anchor in
                    Button {
                        drafts.indicator?.select(anchor)
                    } label: {
                        Text(anchor.displayName)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 8)
                            .background(
                                drafts.indicator?.anchor == anchor
                                    ? Color.accentColor.opacity(0.25) : Color.gray.opacity(0.1)
                            )
                            .cornerRadius(6)
                    }
                    .buttonStyle(.plain)
                }
            }
            Text("Selected: \(drafts.indicator?.anchor.displayName ?? OverlayAnchor.bottomCenter.displayName)")
                .cardDescription()
            Button("Preview on screen") { drafts.indicator?.preview() }
            Text(
                "Preview shows for two seconds, even when the indicator is off. Finish a dictation and wait for its "
                    + "result to disappear before previewing. Save applies the position; Discard keeps your saved position."
            )
            .cardDescription()
            if let message = drafts.indicator?.previewMessage {
                Text(message).cardDescription()
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

    private func openSoundSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.Sound-Settings.extension") else { return }
        NSWorkspace.shared.open(url)
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
    static let showDefaultsKey = "ScribeShowOverlay"

    @Published var anchor: OverlayAnchor
    @Published var showIndicator: Bool
    @Published private(set) var previewMessage: String?
    private var savedAnchor: OverlayAnchor
    private var savedShowIndicator: Bool

    var hasUnsavedChanges: Bool { anchor != savedAnchor || showIndicator != savedShowIndicator }

    private let controller: OverlayPanelController
    private let defaults: UserDefaults
    private var observation: SettingsNotificationObservation?

    init(controller: OverlayPanelController, defaults: UserDefaults = .standard) {
        self.controller = controller
        self.defaults = defaults
        anchor = controller.anchor
        savedAnchor = controller.anchor
        showIndicator = controller.showIndicator
        savedShowIndicator = controller.showIndicator
        observation = SettingsNotificationObservation(UserDefaults.didChangeNotification) { [weak self] in
            self?.reload()
        }
    }

    func select(_ anchor: OverlayAnchor) {
        self.anchor = anchor
        cancelPreview()
    }

    func preview() {
        previewMessage =
            controller.previewAnchor(anchor)
            ? nil : "Finish the current dictation or wait for its result to disappear, then preview again."
    }

    func cancelPreview() {
        controller.cancelPreview()
        previewMessage = nil
    }

    func save() {
        let selectedAnchor = anchor
        let selectedShow = showIndicator
        controller.anchor = selectedAnchor
        controller.showIndicator = selectedShow
        defaults.set(selectedAnchor.rawValue, forKey: Self.defaultsKey)
        defaults.set(selectedShow, forKey: Self.showDefaultsKey)
        savedAnchor = selectedAnchor
        savedShowIndicator = selectedShow
        cancelPreview()
    }

    func discard() {
        cancelPreview()
        anchor = controller.anchor
        showIndicator = controller.showIndicator
        savedAnchor = anchor
        savedShowIndicator = showIndicator
    }

    func reload() {
        guard !hasUnsavedChanges else { return }
        discard()
    }
}
