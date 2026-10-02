import Foundation

/// The footer, the close prompt and the unsaved-changes lines of the Settings window.
/// Windows: `SettingsChangeTracker.cs`, `SettingsClosePrompt.cs`, `SettingsCloseGuard.cs`, `SettingsWindow.xaml`.
struct WindowCopy: CopyCatalog {
    let prefix = "window"

    let allChangesSaved = CopyItem.same("window.allChangesSaved", "All changes saved")
    let unsavedChanges = CopyItem.same("window.unsavedChanges", "Unsaved changes: {pages}")
    let tryDictationUnsaved = CopyItem.same(
        "window.tryDictationUnsaved",
        "You have unsaved changes. Try dictation uses the settings Scribe is running with.")
    let saving = CopyItem.same("window.saving", "Saving...")
    let save = CopyItem.changed("window.save", "Save", windows: "_Save", because: .macKeys)
    let saveTip = CopyItem.same("window.saveTip", "Save your changes and keep this window open.")
    let saveAndClose = CopyItem.changed(
        "window.saveAndClose", "Save and close", windows: "Save _and close", because: .macKeys)
    let saveAndCloseTip = CopyItem.same("window.saveAndCloseTip", "Save your changes and close the window.")
    let close = CopyItem.changed("window.close", "Close", windows: "_Close", because: .macKeys)

    let unsavedChangesTo = CopyItem.same("window.unsavedChangesTo", "You have unsaved changes to {names}.")
    let closeTitle = CopyItem.same("window.closeTitle", "Save changes before closing?")
    let quitTitle = CopyItem.same("window.quitTitle", "Save changes before quitting?")
    let restartTitle = CopyItem.same("window.restartTitle", "Save changes before restarting?")
    let closeBody = CopyItem.same(
        "window.closeBody",
        "Your changes haven't been saved. Things that already happened, such as deleting history, aren't undone.")
    let saveAndQuit = CopyItem.same("window.saveAndQuit", "Save and quit")
    let saveAndRestart = CopyItem.same("window.saveAndRestart", "Save and restart")
    let discard = CopyItem.same("window.discard", "Discard changes")
    let keepEditing = CopyItem.same("window.keepEditing", "Keep editing")
}

/// Every catalog of Settings text, for pages to read and for the manifest tests to walk.
enum SettingsCopy {
    static let dictation = DictationCopy()
    static let tryDictation = TryDictationCopy()
    static let aiCleanup = AiCleanupCopy()
    static let dictionary = DictionaryCopy()
    static let wordPacks = WordPackCopy()
    static let snippets = SnippetsCopy()
    static let appProfiles = AppProfilesCopy()
    static let history = HistoryCopy()
    static let usage = UsageCopy()
    static let advanced = AdvancedCopy()
    static let diagnostics = DiagnosticsCopy()
    static let about = AboutCopy()
    static let window = WindowCopy()
    static let menuBar = MenuBarCopy()
    static let notice = NoticeCopy()
    static let indicator = IndicatorCopy()
    static let search = SearchCopy()
    static let shortcut = ShortcutCopy()
    static let problem = ProblemCopy()

    /// Every catalog, in the order the window shows them.
    static let catalogs: [any CopyCatalog] = [
        dictation, tryDictation, aiCleanup, dictionary, wordPacks, snippets, appProfiles, history, usage, advanced,
        diagnostics, about, window, menuBar, notice, indicator, search, shortcut, problem,
    ]

    /// Every item in every catalog.
    static var allItems: [CopyItem] {
        catalogs.flatMap { $0.items }
    }
}

extension HistoryCopy {
    /// The empty History line for the shortcut the person has: hold for a held key, press twice for a toggle.
    func emptyState(toggle: Bool) -> String {
        (toggle ? emptyToggle : empty).render()
    }
}