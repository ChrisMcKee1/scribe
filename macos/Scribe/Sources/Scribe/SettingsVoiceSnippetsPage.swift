import SwiftUI

struct SettingsVoiceSnippetsPage: View {
    let persistenceStore: PersistenceStore
    let onChanged: @MainActor () -> Void
    let drafts: SettingsDrafts

    var body: some View {
        SettingsPage(
            title: "Voice snippets",
            subtitle: "Say a phrase and Scribe types saved text instead, like your email address or a sign-off."
        ) {
            SettingsCard { SnippetsSettingsTab(persistenceStore: persistenceStore, onChanged: onChanged, drafts: drafts) }
        }
    }
}

struct SnippetsSettingsTab: View {
    @StateObject private var model: SnippetSettingsModel
    @ObservedObject private var drafts: SettingsDrafts
    @State private var selectedSnippetID: Int64?

    init(persistenceStore: PersistenceStore, onChanged: @escaping @MainActor () -> Void, drafts: SettingsDrafts) {
        _drafts = ObservedObject(wrappedValue: drafts)
        _model = StateObject(
            wrappedValue: SnippetSettingsModel(access: .live(persistenceStore), drafts: drafts, onChanged: onChanged))
    }

    private var selectedSnippet: Snippet? {
        guard let selectedSnippetID else { return nil }
        return model.snippets.first { $0.id == selectedSnippetID }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            messages

            HStack(alignment: .top, spacing: 12) {
                snippetList
                    .frame(width: 270)
                Divider()
                snippetEditor
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(minHeight: 420)
        }
        .onAppear {
            Task { await model.reload() }
        }
        .onChange(of: model.snippets) { snippets in
            if let selectedSnippetID, snippets.contains(where: { $0.id == selectedSnippetID }) {
                return
            }
            selectedSnippetID = snippets.first?.id
        }
    }

    private var snippetList: some View {
        VStack(alignment: .leading, spacing: 10) {
            ZStack(alignment: .topLeading) {
                List(selection: $selectedSnippetID) {
                    ForEach(model.snippets, id: \.id) { snippet in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(snippet.phrase)
                                .font(.body.weight(.medium))
                                .lineLimit(1)
                            Text(snippet.template)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(2)
                        }
                        .padding(.vertical, 4)
                        .tag(Optional(snippet.id))
                    }
                }
                if model.load.isLoaded, model.snippets.isEmpty {
                    Text("No snippets yet. Add a snippet to get started.")
                        .foregroundStyle(.secondary)
                        .padding(10)
                }
            }
            HStack {
                Button("Add") {
                    selectedSnippetID = nil
                    drafts.snippetPhrase = ""
                    drafts.snippetTemplate = ""
                }
                Button("Delete...", role: .destructive) {
                    if let selectedSnippet {
                        Task { await model.delete(selectedSnippet) }
                    }
                }
                .disabled(selectedSnippet == nil)
            }
        }
    }

    private var snippetEditor: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let snippet = selectedSnippet {
                HStack(alignment: .firstTextBaseline) {
                    Text("Snippet")
                        .font(.title3.weight(.semibold))
                    Spacer()
                    Toggle("On", isOn: binding(for: snippet))
                }
                labeledReadOnlyValue(title: "When you say", value: snippet.phrase)
                VStack(alignment: .leading, spacing: 6) {
                    Text("Scribe types").cardTitle()
                    ScrollView {
                        Text(snippet.template)
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(10)
                    }
                    .frame(minHeight: 180)
                    .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor)))
                    Text("Line breaks follow your line-break settings for the app you dictate into.")
                        .cardDescription()
                }
            } else {
                Text("New snippet")
                    .font(.title3.weight(.semibold))
                VStack(alignment: .leading, spacing: 6) {
                    Text("When you say").cardTitle()
                    TextField("Say this phrase", text: $drafts.snippetPhrase)
                        .textFieldStyle(.roundedBorder)
                    Text("Choose words you would not say by accident.")
                        .cardDescription()
                }
                VStack(alignment: .leading, spacing: 6) {
                    Text("Scribe types").cardTitle()
                    TextEditor(text: $drafts.snippetTemplate)
                        .frame(minHeight: 180)
                        .border(Color(nsColor: .separatorColor).opacity(0.35))
                    Text("Line breaks follow your line-break settings for the app you dictate into.")
                        .cardDescription()
                }
                Button(model.isAdding ? "Adding..." : "Add snippet") {
                    Task { await model.addFromDrafts() }
                }
                .disabled(!model.canAdd)
            }
            Spacer(minLength: 0)
        }
        .padding(4)
    }

    @ViewBuilder
    private var messages: some View {
        if let loadError = model.loadError {
            Text(loadError).foregroundStyle(.red).font(.caption)
        }
        if let errorMessage = model.errorMessage {
            Text(errorMessage).foregroundStyle(.red).font(.caption)
        }
    }

    private func binding(for snippet: Snippet) -> Binding<Bool> {
        Binding(
            get: { snippet.enabled },
            set: { newValue in
                Task { await model.setEnabled(snippet, enabled: newValue) }
            })
    }

    private func labeledReadOnlyValue(title: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).cardTitle()
            Text(value)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(8)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor)))
        }
    }
}
