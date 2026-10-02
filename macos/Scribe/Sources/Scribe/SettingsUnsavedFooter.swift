import SwiftUI

struct SettingsUnsavedFooter: View {
    @ObservedObject var drafts: SettingsDrafts
    @Environment(\.settingsCloseWindow) private var closeWindow

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Divider()
            HStack(spacing: 12) {
                if drafts.hasUnsavedChanges {
                    Circle().fill(Color.accentColor).frame(width: 7, height: 7)
                        .accessibilityHidden(true)
                }
                VStack(alignment: .leading, spacing: 3) {
                    Text(drafts.footerText).font(.callout)
                    if let message = drafts.footerMessage {
                        Text(message).font(.caption)
                            .foregroundStyle(drafts.saveFailed ? Color.red : Color.secondary)
                    }
                }
                .accessibilityElement(children: .combine)
                Spacer()
                Button("Discard changes") { drafts.discard() }
                    .disabled(!drafts.hasUnsavedChanges || drafts.isBusy)
                Button("Save") { Task { await drafts.save() } }
                    .disabled(!drafts.hasUnsavedChanges || drafts.isBusy)
                    .keyboardShortcut("s", modifiers: .command)
                Button("Close") { closeWindow() }
                    .disabled(drafts.isBusy)
            }
            .padding(.horizontal, 20)
            .padding(.bottom, 12)
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }
}
