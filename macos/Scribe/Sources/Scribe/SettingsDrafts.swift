import Foundation

/// A kind of entry a Settings tab adds from its drafts.
enum SettingsDraftEntry: Hashable, Sendable {
    case dictionaryRule
    case snippet
    case appProfile
}

/// What the user has typed into Settings but not saved yet (a new dictionary rule, snippet or app profile, and the
/// two secret fields), plus the section that was showing. The app owns this rather than the window, so closing
/// Settings, which releases the window and everything it hosts, never throws away a half-written entry, just as
/// when the window was kept alive. Stored settings are not kept here; each tab reads those when it appears.
///
/// A typed secret stays in memory until it is saved or cleared, or Scribe quits, as it did in a kept window. It is
/// never written anywhere by this type and never logged.
@MainActor
final class SettingsDrafts: ObservableObject {
    @Published var section: SettingsSection? = .overlay

    @Published var dictionaryPattern = ""
    @Published var dictionaryReplacement = ""

    @Published var snippetPhrase = ""
    @Published var snippetTemplate = ""

    @Published var profileName = ""
    @Published var profileBundleIdentifiers = ""
    @Published var profileWritingStyle = ""
    @Published var profileNewlineMode: NewlineInjectionMode = .smartFlatten

    @Published var openAIApiKey = ""
    @Published var azureClientSecret = ""

    /// The entries whose drafts are on their way to storage. A tab's model is built again each time its section
    /// shows, or Settings opens, while an add it started keeps running and the drafts keep what it is adding until
    /// it finishes. So the add in flight belongs here, beside the drafts: every model built for them sees it, and
    /// none can send the same entry again.
    @Published private(set) var entriesBeingAdded: Set<SettingsDraftEntry> = []

    func isAdding(_ entry: SettingsDraftEntry) -> Bool {
        entriesBeingAdded.contains(entry)
    }

    /// Claims the add of `entry` from these drafts: true when none is in flight, and then one is until
    /// `finishAdding(_:)`. It runs on the main actor with no suspension, so of two models that ask, one gets it.
    func beginAdding(_ entry: SettingsDraftEntry) -> Bool {
        entriesBeingAdded.insert(entry).inserted
    }

    func finishAdding(_ entry: SettingsDraftEntry) {
        entriesBeingAdded.remove(entry)
    }
}
