import SwiftUI


struct SettingsVoiceSnippetsPage: View {
    let persistenceStore: PersistenceStore
    let onChanged: @MainActor () -> Void
    let drafts: SettingsDrafts

    var body: some View {
        SettingsPage(title: "Voice snippets", subtitle: "Say a phrase and Scribe types saved text instead, like your email address or a sign-off.") {
            SettingsGroupHeader("Voice snippets")
            SettingsCard { SnippetsSettingsTab(persistenceStore: persistenceStore, onChanged: onChanged, drafts: drafts) }
        }
    }
}

// MARK: - Snippets tab

struct SnippetsSettingsTab: View {
    @StateObject private var model: SnippetSettingsModel
    @ObservedObject private var drafts: SettingsDrafts

    init(persistenceStore: PersistenceStore, onChanged: @escaping @MainActor () -> Void, drafts: SettingsDrafts) {
        _drafts = ObservedObject(wrappedValue: drafts)
        _model = StateObject(
            wrappedValue: SnippetSettingsModel(access: .live(persistenceStore), drafts: drafts, onChanged: onChanged))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Voice snippets")
                .font(.headline)

            HStack(alignment: .top) {
                TextField("Trigger phrase (e.g. \"sign off block\")", text: $drafts.snippetPhrase)
                TextEditor(text: $drafts.snippetTemplate)
                    .frame(height: 60)
                    .border(Color.gray.opacity(0.3))
                Button("Add") {
                    Task { await model.addFromDrafts() }
                }
                .disabled(!model.canAdd)
            }

            if let loadError = model.loadError {
                Text(loadError).foregroundStyle(.red).font(.caption)
            }
            if let errorMessage = model.errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }

            List {
                ForEach(model.snippets, id: \.id) { snippet in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Toggle("", isOn: binding(for: snippet))
                                .labelsHidden()
                            Text(snippet.phrase).fontWeight(.medium)
                            Spacer()
                            Button(role: .destructive) {
                                Task { await model.delete(snippet) }
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.plain)
                        }
                        Text(snippet.template)
                            .foregroundStyle(.secondary)
                            .font(.caption)
                    }
                }
            }
        }
        .onAppear {
            Task { await model.reload() }
        }
    }

    private func binding(for snippet: Snippet) -> Binding<Bool> {
        Binding(
            get: { snippet.enabled },
            set: { newValue in
                Task { await model.setEnabled(snippet, enabled: newValue) }
            })
    }
}

