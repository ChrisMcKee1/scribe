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
    static let window = WindowCopy()

    /// Every catalog, in the order the window shows them.
    static let catalogs: [any CopyCatalog] = [
        window
    ]

    /// Every item in every catalog.
    static var allItems: [CopyItem] {
        catalogs.flatMap { $0.items }
    }
}
