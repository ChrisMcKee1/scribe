import SwiftUI

struct SettingsHistoryPage: View {
    let access: HistorySettingsAccess
    let onCleared: @MainActor () -> Void

    var body: some View {
        SettingsPage(title: "History", subtitle: "Find, copy or delete your recent dictations.") {
            HistorySettingsTab(access: access, onCleared: onCleared)
        }
    }
}

struct HistorySettingsTab: View {
    @StateObject private var model: HistorySettingsModel

    init(access: HistorySettingsAccess, onCleared: @escaping @MainActor () -> Void) {
        _model = StateObject(wrappedValue: HistorySettingsModel(access: access, onCleared: onCleared))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            SettingsCard(searchID: "history.keep") { retentionCard }
            SettingsCard(searchID: "history.delete") { historyActionsCard }
            SettingsCard(searchID: "history.list") { macOSHistoryGapCard }
        }
        .onAppear {
            Task { await model.reload() }
        }
        .confirmationDialog(
            "Clear all dictation history?",
            isPresented: $model.isConfirmingClear,
            titleVisibility: .visible
        ) {
            Button("Delete all history", role: .destructive) {
                Task { await model.confirmClear() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(
                "This deletes every stored dictation and empties Recent Dictations in the menu bar. It cannot be undone."
            )
        }
    }

    private var retentionCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("History settings").cardTitle()
                    Text(model.storedCountText ?? "Dictation count has not loaded yet.")
                        .cardDescription()
                }
                Spacer()
            }

            Divider()

            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Keep dictations").cardTitle()
                    Text("Older dictations are deleted automatically.")
                        .cardDescription()
                }
                Spacer()
                Picker(
                    "Keep dictations",
                    selection: Binding(
                        get: { model.selection },
                        set: { choice in _ = model.choose(choice) }
                    )
                ) {
                    ForEach(model.options, id: \.self) { option in
                        Text(option.label).tag(option)
                    }
                }
                .frame(width: 240)
                .disabled(!model.canChooseRetention)
            }

            Text(model.hint)
                .cardDescription()

            messages
        }
    }

    private var historyActionsCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Stored dictations").cardTitle()
                    Text("Delete all saved dictation text from this Mac.")
                        .cardDescription()
                }
                Spacer()
                Button(model.isClearing ? "Deleting..." : "Delete all history...", role: .destructive) {
                    model.requestClear()
                }
                .disabled(!model.canClear)
                if model.isClearing {
                    ProgressView().controlSize(.small)
                }
            }
            Text("Delete all history also empties Recent Dictations in the menu bar.")
                .cardDescription()
        }
    }

    private var macOSHistoryGapCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("History list")
                .cardTitle()
            Text(
                "The macOS port can store and clear dictation history, but this page does not yet include the Windows history table with search, copy, per-item delete and feedback buttons."
            )
            .cardDescription()
        }
    }

    @ViewBuilder
    private var messages: some View {
        if let loadError = model.loadError {
            Text(loadError).foregroundStyle(.red).font(.caption)
        }
        if let errorMessage = model.errorMessage {
            Text(errorMessage).foregroundStyle(.red).font(.caption)
        }
        if let statusMessage = model.statusMessage {
            Text(statusMessage).foregroundStyle(.secondary).font(.caption)
        }
    }
}
