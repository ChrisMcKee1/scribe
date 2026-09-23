import Foundation

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
}
