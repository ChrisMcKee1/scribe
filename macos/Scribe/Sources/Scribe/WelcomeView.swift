import AppKit
import SwiftUI

/// One-time first-run welcome, mirroring Windows' `WelcomeWindow`. Scribe is a tray-only app with
/// no main window, so a brand-new user has nothing on screen to teach them the push-to-talk
/// gesture; this fills that gap non-modally (the tray and dictation loop stay live behind it). The
/// instruction names the key that is bound and its gesture, and follows a rebind while the window is open.
struct WelcomeView: View {
    @StateObject private var hotkey: HotkeyBindingModel
    let onOpenSettings: () -> Void
    let onDismiss: () -> Void

    init(
        hotkeyStore: HotkeySettingsStore = .live,
        onOpenSettings: @escaping () -> Void,
        onDismiss: @escaping () -> Void
    ) {
        _hotkey = StateObject(wrappedValue: HotkeyBindingModel(store: hotkeyStore))
        self.onOpenSettings = onOpenSettings
        self.onDismiss = onDismiss
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(spacing: 12) {
                Image(systemName: "mic.fill")
                    .font(.largeTitle)
                    .foregroundStyle(.tint)
                Text("Welcome to Scribe")
                    .font(.title)
                    .bold()
            }

            Text(HotkeyHint.welcome(for: hotkey.binding))
                .font(.body)

            VStack(alignment: .leading, spacing: 8) {
                Label(
                    "Fully offline. Audio is transcribed on this Mac and discarded; nothing is uploaded.",
                    systemImage: "lock.shield")
                Label(
                    "Scribe lives in the menu bar (top right). Click the microphone icon any time.",
                    systemImage: "menubar.rectangle")
                Label(
                    "AI cleanup, if you enable it, sends only transcribed text, never audio.", systemImage: "sparkles")
            }
            .font(.callout)
            .foregroundStyle(.secondary)

            Spacer()

            HStack {
                Button("Open Settings") {
                    onOpenSettings()
                }
                Spacer()
                Button("Got It") {
                    onDismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(24)
        .frame(width: 420, height: 320)
    }
}
