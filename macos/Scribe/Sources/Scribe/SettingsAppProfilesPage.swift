import SwiftUI


struct SettingsAppProfilesPage: View {
    let persistenceStore: PersistenceStore
    let onChanged: @MainActor () -> Void
    let drafts: SettingsDrafts

    var body: some View {
        SettingsPage(title: "App profiles", subtitle: "Use a different writing style or line-break rule in specific apps, like Outlook or Teams.") {
            SettingsGroupHeader("Profiles")
            SettingsCard { AppProfilesSettingsTab(persistenceStore: persistenceStore, onChanged: onChanged, drafts: drafts) }
        }
    }
}

// MARK: - App profiles tab

struct AppProfilesSettingsTab: View {
    @StateObject private var model: AppProfileSettingsModel
    @ObservedObject private var drafts: SettingsDrafts

    init(persistenceStore: PersistenceStore, onChanged: @escaping @MainActor () -> Void, drafts: SettingsDrafts) {
        _drafts = ObservedObject(wrappedValue: drafts)
        _model = StateObject(
            wrappedValue: AppProfileSettingsModel(access: .live(persistenceStore), drafts: drafts, onChanged: onChanged)
        )
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("App profiles")
                .font(.headline)
            Text(
                """
                Override writing style or line-break handling for specific apps, matched by bundle identifier \
                (e.g. com.apple.Terminal).
                """
            )
            .foregroundStyle(.secondary)
            .font(.caption)

            VStack(alignment: .leading, spacing: 6) {
                TextField("Profile name (e.g. \"Terminal\")", text: $drafts.profileName)
                TextField("Bundle identifiers, comma-separated", text: $drafts.profileBundleIdentifiers)
                TextField("Writing style override (optional)", text: $drafts.profileWritingStyle)
                Picker("Newline handling", selection: $drafts.profileNewlineMode) {
                    Text("Smart Flatten").tag(NewlineInjectionMode.smartFlatten)
                    Text("Always Flatten").tag(NewlineInjectionMode.alwaysFlatten)
                    Text("Keep Newlines").tag(NewlineInjectionMode.keepNewlines)
                }
                Button("Add profile") {
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
                ForEach(model.profiles, id: \.id) { profile in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Text(profile.name).fontWeight(.medium)
                            Spacer()
                            Button(role: .destructive) {
                                Task { await model.delete(profile) }
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
        .onAppear {
            Task { await model.reload() }
        }
    }
}

