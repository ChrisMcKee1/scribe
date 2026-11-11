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
    @Published private(set) var load = SettingsSectionLoad()

    let drafts: SettingsDrafts
    private let access: AppProfileSettingsAccess
    private let onChanged: @MainActor () -> Void
    private let formatting: @MainActor () -> DictationFormattingSettings

    init(
        access: AppProfileSettingsAccess,
        drafts: SettingsDrafts,
        formatting: @escaping @MainActor () -> DictationFormattingSettings = { TypingSettingsStore.live.formatting },
        onChanged: @escaping @MainActor () -> Void
    ) {
        self.access = access
        self.drafts = drafts
        self.formatting = formatting
        self.onChanged = onChanged
    }

    /// Whether the profile in the drafts is being added, by this model or by one built earlier for the same drafts.
    var isAdding: Bool {
        drafts.isAdding(.appProfile)
    }

    var canAdd: Bool {
        !isAdding && !Self.isBlank(drafts.profileName) && !Self.isBlank(drafts.profileBundleIdentifiers)
            && !hasNewlineConflict
    }

    /// Whether the profile in the drafts would paste Markdown where its line-break rule takes the line breaks out,
    /// which Windows' Save refuses too (`SettingsDraftValidation.ValidateAppFormatting`). Judged on the format it
    /// would use, its own or the default, and only while app-aware formatting is on, since otherwise the format is
    /// ignored.
    var hasNewlineConflict: Bool {
        let settings = formatting()
        guard settings.appAwareFormatting else { return false }
        return DictationFormatPlan.conflictsWithNewlines(
            drafts.profileTextFormat ?? settings.defaultTextFormat,
            newlineMode: drafts.profileNewlineMode,
            bundleIdentifiers: Self.identifiers(in: drafts.profileBundleIdentifiers))
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
        let textFormat = drafts.profileTextFormat
        guard canAdd, drafts.beginAdding(.appProfile) else {
            return
        }
        defer { drafts.finishAdding(.appProfile) }

        let profile = AppProfile(
            name: name,
            bundleIdentifiers: Self.identifiers(in: identifiersText),
            processNames: [],
            writingStylePrompt: writingStyle.isEmpty ? nil : writingStyle,
            newlineHandling: newlineMode,
            textFormat: textFormat)
        let added = await write {
            try await self.access.addProfile(profile)
        }
        if added, drafts.profileName == name, drafts.profileBundleIdentifiers == identifiersText,
            drafts.profileWritingStyle == writingStyle, drafts.profileNewlineMode == newlineMode,
            drafts.profileTextFormat == textFormat
        {
            drafts.profileName = ""
            drafts.profileBundleIdentifiers = ""
            drafts.profileWritingStyle = ""
            drafts.profileNewlineMode = .smartFlatten
            drafts.profileTextFormat = nil
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

    private static func identifiers(in text: String) -> [String] {
        text
            .split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }

    private static func isBlank(_ text: String) -> Bool {
        text.trimmingCharacters(in: .whitespaces).isEmpty
    }
}
