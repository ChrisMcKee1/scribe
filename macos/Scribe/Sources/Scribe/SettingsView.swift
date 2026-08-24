import SwiftUI

/// Full Settings window replacing the earlier static scaffold text. Tabs mirror the feature areas
/// already implemented: overlay position, dictionary, snippets, and per-app profiles. Backed
/// directly by `PersistenceStore` (no separate view-model layer yet; the store's CRUD surface is
/// already small and synchronous, matching the CLI verbs used to verify each feature).
struct SettingsView: View {
    let persistenceStore: PersistenceStore
    let overlayPanelController: OverlayPanelController
    let onProfilesOrRulesChanged: () -> Void

    var body: some View {
        TabView {
            OverlaySettingsTab(overlayPanelController: overlayPanelController)
                .tabItem { Label("Overlay", systemImage: "rectangle.on.rectangle") }
            DictionarySettingsTab(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
                .tabItem { Label("Dictionary", systemImage: "character.book.closed") }
            SnippetsSettingsTab(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
                .tabItem { Label("Snippets", systemImage: "text.append") }
            AppProfilesSettingsTab(persistenceStore: persistenceStore, onChanged: onProfilesOrRulesChanged)
                .tabItem { Label("App Profiles", systemImage: "app.badge") }
        }
        .padding(20)
        .frame(minWidth: 560, minHeight: 420)
    }
}

// MARK: - Overlay tab

private struct OverlaySettingsTab: View {
    let overlayPanelController: OverlayPanelController
    @State private var selectedAnchor: OverlayAnchor = .bottomCenter

    private static let overlayAnchorDefaultsKey = "ScribeOverlayAnchor"

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Recording Pill Position")
                .font(.headline)
            Text("Choose where the recording pill appears on screen while dictating.")
                .foregroundStyle(.secondary)

            LazyVGrid(columns: Array(repeating: GridItem(.flexible()), count: 3), spacing: 8) {
                ForEach(OverlayAnchor.allCases, id: \.self) { anchor in
                    Button {
                        selectedAnchor = anchor
                        overlayPanelController.anchor = anchor
                        UserDefaults.standard.set(anchor.rawValue, forKey: Self.overlayAnchorDefaultsKey)
                    } label: {
                        Text(anchor.displayName)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 8)
                            .background(selectedAnchor == anchor ? Color.accentColor.opacity(0.25) : Color.gray.opacity(0.1))
                            .cornerRadius(6)
                    }
                    .buttonStyle(.plain)
                }
            }
            Spacer()
        }
        .onAppear {
            selectedAnchor = overlayPanelController.anchor
        }
    }
}

// MARK: - Dictionary tab

private struct DictionarySettingsTab: View {
    let persistenceStore: PersistenceStore
    let onChanged: () -> Void

    @State private var entries: [DictionaryEntry] = []
    @State private var newPattern = ""
    @State private var newReplacement = ""
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("User Dictionary")
                .font(.headline)

            HStack {
                TextField("Spoken form (e.g. \"sherpa onnx\")", text: $newPattern)
                TextField("Written form (e.g. \"sherpa-onnx\")", text: $newReplacement)
                Button("Add", action: addEntry)
                    .disabled(newPattern.trimmingCharacters(in: .whitespaces).isEmpty
                        || newReplacement.trimmingCharacters(in: .whitespaces).isEmpty)
            }

            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }

            List {
                ForEach(entries, id: \.id) { entry in
                    HStack {
                        Toggle("", isOn: binding(for: entry))
                            .labelsHidden()
                        Text(entry.pattern)
                        Image(systemName: "arrow.right")
                            .foregroundStyle(.secondary)
                        Text(entry.replacement)
                        Spacer()
                        Button(role: .destructive) {
                            deleteEntry(entry)
                        } label: {
                            Image(systemName: "trash")
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .onAppear(perform: reload)
    }

    private func binding(for entry: DictionaryEntry) -> Binding<Bool> {
        Binding(
            get: { entry.enabled },
            set: { newValue in setEnabled(entry, enabled: newValue) })
    }

    private func reload() {
        do {
            entries = try persistenceStore.fetchAllDictionaryEntries()
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func addEntry() {
        do {
            _ = try persistenceStore.insertDictionaryEntry(
                DictionaryEntry(pattern: newPattern, replacement: newReplacement))
            newPattern = ""
            newReplacement = ""
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func setEnabled(_ entry: DictionaryEntry, enabled: Bool) {
        do {
            try persistenceStore.setDictionaryEntryEnabled(id: entry.id, enabled: enabled)
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func deleteEntry(_ entry: DictionaryEntry) {
        do {
            try persistenceStore.deleteDictionaryEntry(id: entry.id)
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - Snippets tab

private struct SnippetsSettingsTab: View {
    let persistenceStore: PersistenceStore
    let onChanged: () -> Void

    @State private var snippets: [Snippet] = []
    @State private var newPhrase = ""
    @State private var newTemplate = ""
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Voice Snippets")
                .font(.headline)

            HStack(alignment: .top) {
                TextField("Trigger phrase (e.g. \"sign off block\")", text: $newPhrase)
                TextEditor(text: $newTemplate)
                    .frame(height: 60)
                    .border(Color.gray.opacity(0.3))
                Button("Add", action: addSnippet)
                    .disabled(newPhrase.trimmingCharacters(in: .whitespaces).isEmpty
                        || newTemplate.trimmingCharacters(in: .whitespaces).isEmpty)
            }

            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }

            List {
                ForEach(snippets, id: \.id) { snippet in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Toggle("", isOn: binding(for: snippet))
                                .labelsHidden()
                            Text(snippet.phrase).fontWeight(.medium)
                            Spacer()
                            Button(role: .destructive) {
                                deleteSnippet(snippet)
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
        .onAppear(perform: reload)
    }

    private func binding(for snippet: Snippet) -> Binding<Bool> {
        Binding(
            get: { snippet.enabled },
            set: { newValue in setEnabled(snippet, enabled: newValue) })
    }

    private func reload() {
        do {
            snippets = try persistenceStore.fetchAllSnippets()
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func addSnippet() {
        do {
            _ = try persistenceStore.insertSnippet(Snippet(phrase: newPhrase, template: newTemplate))
            newPhrase = ""
            newTemplate = ""
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func setEnabled(_ snippet: Snippet, enabled: Bool) {
        do {
            try persistenceStore.setSnippetEnabled(id: snippet.id, enabled: enabled)
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func deleteSnippet(_ snippet: Snippet) {
        do {
            try persistenceStore.deleteSnippet(id: snippet.id)
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

// MARK: - App profiles tab

private struct AppProfilesSettingsTab: View {
    let persistenceStore: PersistenceStore
    let onChanged: () -> Void

    @State private var profiles: [AppProfile] = []
    @State private var newName = ""
    @State private var newBundleIdentifiers = ""
    @State private var newWritingStyle = ""
    @State private var newNewlineMode: NewlineInjectionMode = .smartFlatten
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Per-App Profiles")
                .font(.headline)
            Text("Override writing style or line-break handling for specific apps, matched by bundle identifier (e.g. com.apple.Terminal).")
                .foregroundStyle(.secondary)
                .font(.caption)

            VStack(alignment: .leading, spacing: 6) {
                TextField("Profile name (e.g. \"Terminal\")", text: $newName)
                TextField("Bundle identifiers, comma-separated", text: $newBundleIdentifiers)
                TextField("Writing style override (optional)", text: $newWritingStyle)
                Picker("Newline handling", selection: $newNewlineMode) {
                    Text("Smart Flatten").tag(NewlineInjectionMode.smartFlatten)
                    Text("Always Flatten").tag(NewlineInjectionMode.alwaysFlatten)
                    Text("Keep Newlines").tag(NewlineInjectionMode.keepNewlines)
                }
                Button("Add Profile", action: addProfile)
                    .disabled(newName.trimmingCharacters(in: .whitespaces).isEmpty
                        || newBundleIdentifiers.trimmingCharacters(in: .whitespaces).isEmpty)
            }

            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red).font(.caption)
            }

            List {
                ForEach(profiles, id: \.id) { profile in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Text(profile.name).fontWeight(.medium)
                            Spacer()
                            Button(role: .destructive) {
                                deleteProfile(profile)
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.plain)
                        }
                        Text(profile.bundleIdentifiers.joined(separator: ", "))
                            .foregroundStyle(.secondary)
                            .font(.caption)
                        if let writingStylePrompt = profile.writingStylePrompt, !writingStylePrompt.isEmpty {
                            Text("Style: \(writingStylePrompt)").font(.caption)
                        }
                    }
                }
            }
        }
        .onAppear(perform: reload)
    }

    private func reload() {
        do {
            profiles = try persistenceStore.fetchAppProfiles()
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func addProfile() {
        do {
            let bundleIdentifiers = newBundleIdentifiers
                .split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
            _ = try persistenceStore.insertAppProfile(AppProfile(
                name: newName,
                bundleIdentifiers: bundleIdentifiers,
                processNames: [],
                writingStylePrompt: newWritingStyle.isEmpty ? nil : newWritingStyle,
                newlineHandling: newNewlineMode))
            newName = ""
            newBundleIdentifiers = ""
            newWritingStyle = ""
            newNewlineMode = .smartFlatten
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func deleteProfile(_ profile: AppProfile) {
        do {
            try persistenceStore.deleteAppProfile(id: profile.id)
            reload()
            onChanged()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
