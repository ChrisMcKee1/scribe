import Foundation

/// How the App Profiles tab reaches storage. `live(_:)` uses the store's asynchronous forms, so no call waits on the
/// main actor. Tests pass their own.
struct AppProfileSettingsAccess: Sendable {
    var loadProfiles: @Sendable () async throws -> [AppProfile]
    var addProfile: @Sendable (AppProfile) async throws -> Void
    var deleteProfile: @Sendable (_ id: Int64) async throws -> Void
}

extension AppProfileSettingsAccess {
    static func live(_ store: PersistenceStore) -> AppProfileSettingsAccess {
        AppProfileSettingsAccess(
            loadProfiles: { try await store.loadAppProfiles() },
            addProfile: { profile in _ = try await store.addAppProfile(profile) },
            deleteProfile: { id in try await store.removeAppProfile(id: id) })
    }
}

/// The App Profiles tab: the stored per-app profiles, adding and deleting them. Storage calls are asynchronous, only
/// the newest read may replace the rows or report an error, and after each change the app's profiles are refreshed
/// (`onChanged`) and the rows read again.
@MainActor
final class AppProfileSettingsModel: ObservableObject {
    @Published private(set) var profiles: [AppProfile] = []
    /// Why the newest read failed. Kept apart from `errorMessage`, so a read that finishes after an action failed
    /// never clears the action's failure.
    @Published private(set) var loadError: String?
    /// Why the last action (an add, switch, delete, import and the like) failed.
    @Published private(set) var errorMessage: String?
    @Published private(set) var isAdding = false
    @Published private(set) var load = SettingsSectionLoad()

    let drafts: SettingsDrafts
    private let access: AppProfileSettingsAccess
    private let onChanged: @MainActor () -> Void

    init(access: AppProfileSettingsAccess, drafts: SettingsDrafts, onChanged: @escaping @MainActor () -> Void) {
        self.access = access
        self.drafts = drafts
        self.onChanged = onChanged
    }

    var canAdd: Bool {
        !isAdding && !Self.isBlank(drafts.profileName) && !Self.isBlank(drafts.profileBundleIdentifiers)
    }

    func reload() async {
        let ticket = load.begin()
        do {
            let loaded = try await access.loadProfiles()
            guard load.publish(ticket) else {
                return
            }
            profiles = loaded
            loadError = nil
        } catch {
            guard load.fail(ticket) else {
                return
            }
            loadError = error.localizedDescription
        }
    }

    /// Adds the profile typed into the drafts, which are reset only if they still hold what was added.
    func addFromDrafts() async {
        let name = drafts.profileName
        let identifiersText = drafts.profileBundleIdentifiers
        let writingStyle = drafts.profileWritingStyle
        let newlineMode = drafts.profileNewlineMode
        guard canAdd else {
            return
        }
        isAdding = true
        defer { isAdding = false }

        let profile = AppProfile(
            name: name,
            bundleIdentifiers: identifiersText
                .split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty },
            processNames: [],
            writingStylePrompt: writingStyle.isEmpty ? nil : writingStyle,
            newlineHandling: newlineMode)
        let added = await write {
            try await self.access.addProfile(profile)
        }
        if added, drafts.profileName == name, drafts.profileBundleIdentifiers == identifiersText,
           drafts.profileWritingStyle == writingStyle, drafts.profileNewlineMode == newlineMode {
            drafts.profileName = ""
            drafts.profileBundleIdentifiers = ""
            drafts.profileWritingStyle = ""
            drafts.profileNewlineMode = .smartFlatten
        }
    }

    func delete(_ profile: AppProfile) async {
        await write {
            try await self.access.deleteProfile(profile.id)
        }
    }

    /// Runs one write. On success the app's profiles are refreshed and the rows read again. Returns whether it
    /// succeeded.
    @discardableResult
    private func write(_ operation: @MainActor () async throws -> Void) async -> Bool {
        errorMessage = nil
        do {
            try await operation()
        } catch {
            errorMessage = error.localizedDescription
            return false
        }
        onChanged()
        await reload()
        return true
    }

    private static func isBlank(_ text: String) -> Bool {
        text.trimmingCharacters(in: .whitespaces).isEmpty
    }
}
