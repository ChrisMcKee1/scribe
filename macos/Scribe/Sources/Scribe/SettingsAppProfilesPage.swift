import SwiftUI

struct SettingsAppProfilesPage: View {
    let persistenceStore: PersistenceStore
    let onChanged: @MainActor () -> Void
    let drafts: SettingsDrafts

    var body: some View {
        SettingsPage(
            title: "App profiles",
            subtitle: "Use a different writing style or line-break rule in specific apps, like Outlook or Teams."
        ) {
            SettingsCard { AppProfilesSettingsTab(persistenceStore: persistenceStore, onChanged: onChanged, drafts: drafts) }
        }
    }
}

struct AppProfilesSettingsTab: View {
    @StateObject private var model: AppProfileSettingsModel
    @ObservedObject private var drafts: SettingsDrafts
    @State private var selectedProfileID: Int64?

    init(persistenceStore: PersistenceStore, onChanged: @escaping @MainActor () -> Void, drafts: SettingsDrafts) {
        _drafts = ObservedObject(wrappedValue: drafts)
        _model = StateObject(
            wrappedValue: AppProfileSettingsModel(access: .live(persistenceStore), drafts: drafts, onChanged: onChanged)
        )
    }

    private var selectedProfile: AppProfile? {
        guard let selectedProfileID else { return nil }
        return model.profiles.first { $0.id == selectedProfileID }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("When an app matches more than one profile, Scribe uses the one higher in the list.")
                .cardDescription()

            messages

            HStack(alignment: .top, spacing: 12) {
                profileList
                    .frame(width: 270)
                Divider()
                profileEditor
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(minHeight: 460)
        }
        .onAppear {
            Task { await model.reload() }
        }
        .onChange(of: model.profiles) { profiles in
            if let selectedProfileID, profiles.contains(where: { $0.id == selectedProfileID }) {
                return
            }
            selectedProfileID = profiles.first?.id
        }
    }

    private var profileList: some View {
        VStack(alignment: .leading, spacing: 10) {
            List(selection: $selectedProfileID) {
                ForEach(model.profiles, id: \.id) { profile in
                    VStack(alignment: .leading, spacing: 4) {
                        Text(profile.name)
                            .font(.body.weight(.medium))
                            .lineLimit(1)
                        Text(profile.bundleIdentifiers.joined(separator: ", "))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(2)
                    }
                    .padding(.vertical, 4)
                    .tag(Optional(profile.id))
                }
            }
            HStack {
                Button("Add") {
                    selectedProfileID = nil
                    drafts.profileName = ""
                    drafts.profileBundleIdentifiers = ""
                    drafts.profileWritingStyle = ""
                    drafts.profileNewlineMode = .smartFlatten
                }
                Button("Delete...", role: .destructive) {
                    if let selectedProfile {
                        Task { await model.delete(selectedProfile) }
                    }
                }
                .disabled(selectedProfile == nil)
            }
        }
    }

    private var profileEditor: some View {
        VStack(alignment: .leading, spacing: 14) {
            if let profile = selectedProfile {
                Text("Profile")
                    .font(.title3.weight(.semibold))
                labeledReadOnlyValue(title: "Name", value: profile.name)
                labeledReadOnlyValue(title: "Apps", value: profile.bundleIdentifiers.joined(separator: ", "))
                if !profile.processNames.isEmpty {
                    labeledReadOnlyValue(title: "Program names", value: profile.processNames.joined(separator: ", "))
                }
                VStack(alignment: .leading, spacing: 6) {
                    Text("Writing style for these apps").cardTitle()
                    Text(profile.writingStylePrompt?.isEmpty == false ? profile.writingStylePrompt! : "Uses your main writing style.")
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8)
                        .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor)))
                }
                labeledReadOnlyValue(title: "Line breaks in these apps", value: profile.newlineHandling?.label ?? "Uses your main line-break setting.")
            } else {
                Text("New profile")
                    .font(.title3.weight(.semibold))
                VStack(alignment: .leading, spacing: 6) {
                    Text("Name").cardTitle()
                    TextField("For example, Email", text: $drafts.profileName)
                        .textFieldStyle(.roundedBorder)
                }
                VStack(alignment: .leading, spacing: 6) {
                    Text("Apps").cardTitle()
                    Text("Scribe uses this profile when one of these apps is in front.")
                        .cardDescription()
                    TextField("Bundle identifiers, comma-separated", text: $drafts.profileBundleIdentifiers)
                        .textFieldStyle(.roundedBorder)
                }
                VStack(alignment: .leading, spacing: 6) {
                    Text("Writing style for these apps").cardTitle()
                    TextEditor(text: $drafts.profileWritingStyle)
                        .frame(minHeight: 110)
                        .border(Color(nsColor: .separatorColor).opacity(0.35))
                    Text("Leave empty to use your main writing style.")
                        .cardDescription()
                }
                VStack(alignment: .leading, spacing: 6) {
                    Text("Line breaks in these apps").cardTitle()
                    Picker("Line breaks", selection: $drafts.profileNewlineMode) {
                        ForEach([NewlineInjectionMode.smartFlatten, .alwaysFlatten, .keepNewlines], id: \.self) { mode in
                            Text(mode.label).tag(mode)
                        }
                    }
                    .labelsHidden()
                    .frame(maxWidth: 280)
                }
                Button(model.isAdding ? "Adding..." : "Add profile") {
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

    private func labeledReadOnlyValue(title: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).cardTitle()
            Text(value.isEmpty ? "Not set" : value)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(8)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor)))
        }
    }
}

private extension NewlineInjectionMode {
    var label: String {
        switch self {
        case .smartFlatten: return "Smart Flatten"
        case .alwaysFlatten: return "Always Flatten"
        case .keepNewlines: return "Keep Newlines"
        }
    }
}
