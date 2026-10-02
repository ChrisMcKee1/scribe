import AppKit
import SwiftUI

struct SettingsHistoryPage: View {
    let access: HistorySettingsAccess
    let onCleared: @MainActor () -> Void
    let listAccess: HistoryListAccess

    var body: some View {
        SettingsPage(title: "History", subtitle: "Find, copy or delete your dictations.") {
            HistorySettingsTab(access: access, onCleared: onCleared, listAccess: listAccess)
        }
    }
}

struct HistorySettingsTab: View {
    @StateObject private var model: HistorySettingsModel
    @StateObject private var list: HistoryListModel
    private let onCleared: @MainActor () -> Void
    @State private var pendingDelete: StoredDictation?
    @State private var isVisible = false

    init(
        access: HistorySettingsAccess, onCleared: @escaping @MainActor () -> Void,
        listAccess: HistoryListAccess
    ) {
        _model = StateObject(wrappedValue: HistorySettingsModel(access: access, onCleared: onCleared))
        _list = StateObject(wrappedValue: HistoryListModel(access: listAccess))
        self.onCleared = onCleared
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            SettingsCard(searchID: "history.keep") { retentionCard }
            SettingsCard(searchID: "history.delete") { historyActionsCard }
            SettingsCard(searchID: "history.list") { historyListCard }
        }
        .onAppear {
            isVisible = true
            Task { await model.reload() }
            list.appear()
        }
        .onDisappear {
            isVisible = false
            list.stop()
        }
        .confirmationDialog(
            "Clear all dictation history?",
            isPresented: $model.isConfirmingClear,
            titleVisibility: .visible
        ) {
            Button("Delete all history", role: .destructive) {
                Task {
                    list.stop()
                    await model.confirmClear()
                    if isVisible { list.appear() }
                }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(
                "This deletes every stored dictation and empties Recent Dictations in the menu bar. It cannot be undone."
            )
        }
        .confirmationDialog(
            "Delete this dictation?",
            isPresented: Binding(
                get: { pendingDelete != nil },
                set: { if !$0 { pendingDelete = nil } }),
            titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive) {
                if let row = pendingDelete {
                    pendingDelete = nil
                    Task {
                        await list.delete(row, onDeleted: onCleared)
                        await model.reload()
                    }
                }
            }
            Button("Cancel", role: .cancel) { pendingDelete = nil }
        } message: {
            Text("This deletes the saved dictation text. It cannot be undone.")
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

    private var historyListCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Your dictations").cardTitle()
            HStack {
                TextField("Search all dictations", text: $list.query)
                    .textFieldStyle(.roundedBorder)
                    .onExitCommand {
                        if !list.query.isEmpty { list.query = "" }
                    }
                if !list.query.isEmpty {
                    Button("Clear search") { list.query = "" }
                }
                if list.isLoading { ProgressView().controlSize(.small) }
            }
            Text(
                list.query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    ? "Showing up to 200 recent dictations."
                    : "Searching all stored dictations. Showing up to 200 matches."
            ).cardDescription()
            if let error = list.errorMessage {
                HStack {
                    Text(error).font(.caption).foregroundStyle(.red)
                    Button("Retry") { list.refresh() }
                }
            } else if list.rows.isEmpty && !list.isLoading {
                Text(list.query.isEmpty ? "No dictations yet." : "No dictations match your search.")
                    .cardDescription()
            }
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 12) {
                    ForEach(list.rows) { row in
                        VStack(alignment: .leading, spacing: 6) {
                            HStack {
                                Text(row.record.startedAt.formatted(date: .abbreviated, time: .shortened))
                                if let app = row.record.targetApp { Text(app).foregroundStyle(.secondary) }
                                Spacer()
                                Button("Copy") {
                                    NSPasteboard.general.clearContents()
                                    NSPasteboard.general.setString(row.record.transcriptText ?? "", forType: .string)
                                }
                                .disabled(row.record.transcriptText?.isEmpty != false)
                                Button("Delete", role: .destructive) { pendingDelete = row }
                                    .disabled(list.isDeleting || model.isClearing)
                            }
                            .font(.caption)
                            Text(row.record.transcriptText ?? "No text was stored.")
                                .textSelection(.enabled)
                            Divider()
                        }
                    }
                }
            }
            .frame(height: 320)
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
