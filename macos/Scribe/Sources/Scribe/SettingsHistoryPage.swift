import SwiftUI


struct SettingsHistoryPage: View {
    let access: HistorySettingsAccess
    let onCleared: @MainActor () -> Void

    var body: some View {
        SettingsPage(title: "History", subtitle: "Find, copy or delete your recent dictations.") {
            SettingsGroupHeader("History settings")
            SettingsCard { HistorySettingsTab(access: access, onCleared: onCleared) }
        }
    }
}

// MARK: - History tab

/// How long dictation text is kept, and Clear history (`HistorySettingsModel`). Windows keeps the limit on its storage
/// card and Clear on its History page; macOS has no history list, so both live here.
struct HistorySettingsTab: View {
    @StateObject private var model: HistorySettingsModel

    init(access: HistorySettingsAccess, onCleared: @escaping @MainActor () -> Void) {
        _model = StateObject(wrappedValue: HistorySettingsModel(access: access, onCleared: onCleared))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Dictation History")
                .font(.headline)

            Picker(
                "Keep dictation history for",
                selection: Binding(
                    get: { model.selection },
                    set: { choice in _ = model.choose(choice) }
                )
            ) {
                ForEach(model.options, id: \.self) { option in
                    Text(option.label).tag(option)
                }
            }
            .frame(maxWidth: 360)
            .disabled(!model.canChooseRetention)

            Text(model.hint)
                .font(.caption)
                .foregroundStyle(.secondary)

            Divider()

            if let storedCountText = model.storedCountText {
                Text(storedCountText)
                    .foregroundStyle(.secondary)
            }

            HStack {
                Button(model.isClearing ? "Clearing\u{2026}" : "Clear History\u{2026}", role: .destructive) {
                    model.requestClear()
                }
                .disabled(!model.canClear)
                if model.isClearing {
                    ProgressView()
                        .controlSize(.small)
                }
                Spacer()
            }

            Text("Clear deletes every stored dictation and also empties Recent Dictations in the menu bar.")
                .font(.caption)
                .foregroundStyle(.secondary)

            if let loadError = model.loadError {
                Text(loadError).foregroundStyle(.red).font(.caption)
            }
            if let errorMessage = model.errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }
            if let statusMessage = model.statusMessage {
                Text(statusMessage).foregroundStyle(.secondary).font(.caption)
            }

            Spacer()
        }
        .onAppear {
            Task { await model.reload() }
        }
        .confirmationDialog(
            "Clear all dictation history?",
            isPresented: $model.isConfirmingClear,
            titleVisibility: .visible
        ) {
            Button("Clear All", role: .destructive) {
                Task { await model.confirmClear() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(
                """
                This deletes every stored dictation and empties Recent Dictations in the menu bar. It cannot be \
                undone.
                """
            )
        }
    }
}
