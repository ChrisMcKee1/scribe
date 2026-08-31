import SwiftUI
import AppKit

/// One-time first-run welcome, mirroring Windows' `WelcomeWindow`. Scribe is a tray-only app with
/// no main window, so a brand-new user has nothing on screen to teach them the push-to-talk
/// gesture; this fills that gap non-modally (the tray and dictation loop stay live behind it).
struct WelcomeView: View {
    let hotkeyDisplayName: String
    let onOpenSettings: () -> Void
    let onDismiss: () -> Void

    private var gestureHint: String {
        let key = hotkeyDisplayName.trimmingCharacters(in: .whitespaces).isEmpty ? "Right Control" : hotkeyDisplayName
        return "Hold \(key) and start talking. Release when you are done, and the text appears wherever your cursor is."
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

            Text(gestureHint)
                .font(.body)

            VStack(alignment: .leading, spacing: 8) {
                Label("Fully offline. Audio is transcribed on this Mac and discarded; nothing is uploaded.", systemImage: "lock.shield")
                Label("Scribe lives in the menu bar (top right). Click the microphone icon any time.", systemImage: "menubar.rectangle")
                Label("AI cleanup, if you enable it, sends only transcribed text, never audio.", systemImage: "sparkles")
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
