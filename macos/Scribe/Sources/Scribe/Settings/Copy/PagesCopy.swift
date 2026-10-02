import Foundation

/// The Voice snippets page. Windows: `SettingsWindow.xaml` lines 2040 to 2155, `SettingsWindow.Snippets.cs`.
struct SnippetsCopy: CopyCatalog {
    let prefix = "snippets"
    let title = CopyItem.same("snippets.title", "Voice snippets")

    let subtitle = CopyItem.same(
        "snippets.subtitle", "Say a phrase and Scribe types saved text instead, like your email address or a sign-off.")
    let offTitle = CopyItem.same("snippets.offTitle", "Dictionary and snippets are off")
    let turnOn = CopyItem.same("snippets.turnOn", "Turn on")
    let add = CopyItem.same("snippets.add", "Add")
    let delete = CopyItem.same("snippets.delete", "Delete...")
    let snippet = CopyItem.same("snippets.snippet", "Snippet")
    let on = CopyItem.same("snippets.on", "On")
    let whenYouSay = CopyItem.same("snippets.whenYouSay", "When you say")
    let sayThis = CopyItem.same("snippets.sayThis", "Say this phrase")
    let chooseUnusual = CopyItem.same("snippets.chooseUnusual", "Choose words you wouldn't say by accident.")
    let scribeTypes = CopyItem.same("snippets.scribeTypes", "Scribe types")
    let lineBreaks = CopyItem.same(
        "snippets.lineBreaks", "Line breaks follow your line-break settings for the app you dictate into.")
    let select = CopyItem.same("snippets.select", "Select a snippet to edit it.")
    let addSnippet = CopyItem.same("snippets.addSnippet", "Add snippet")
    let loading = CopyItem.same("snippets.loading", "Loading snippets...")
    let loadFailed = CopyItem.same("snippets.loadFailed", "Couldn't load your snippets.")
    let empty = CopyItem.same("snippets.empty", "No snippets yet. A snippet types saved text when you say its phrase.")
    let deleteTitle = CopyItem.same("snippets.deleteTitle", "Delete this snippet?")
    let deleteBody = CopyItem.same("snippets.deleteBody", "Nothing changes until you save.")
    let deleteConfirm = CopyItem.same("snippets.deleteConfirm", "Delete snippet")
    let removeTitle = CopyItem.same("snippets.removeTitle", "Remove these phrases from your dictations?")
    let removeBody = CopyItem.same(
        "snippets.removeBody", "Scribe will remove these phrases instead of replacing them: {list}.")
}

/// The App profiles page. Windows: `SettingsWindow.xaml` lines 2156 to 2345, `SettingsWindow.Profiles.cs`.
/// Windows picks apps by program name; on the Mac an app is identified by its name in Finder.
struct AppProfilesCopy: CopyCatalog {
    let prefix = "appProfiles"
    let title = CopyItem.same("appProfiles.title", "App profiles")

    let subtitle = CopyItem.same(
        "appProfiles.subtitle",
        "Use a different writing style or line-break rule in specific apps, like Outlook or Teams.")
    let cleanupOffTitle = CopyItem.same("appProfiles.cleanupOffTitle", "AI cleanup is off")
    let goToCleanup = CopyItem.same("appProfiles.goToCleanup", "Go to AI cleanup")
    let add = CopyItem.same("appProfiles.add", "Add")
    let delete = CopyItem.same("appProfiles.delete", "Delete...")
    let moveUp = CopyItem.same("appProfiles.moveUp", "Move up")
    let moveDown = CopyItem.same("appProfiles.moveDown", "Move down")
    let order = CopyItem.same(
        "appProfiles.order", "When an app matches more than one profile, Scribe uses the one higher in the list.")
    let name = CopyItem.same("appProfiles.name", "Name")
    let nameExample = CopyItem.same("appProfiles.nameExample", "For example, Email")
    let apps = CopyItem.same("appProfiles.apps", "Apps")
    let appsHint = CopyItem.same("appProfiles.appsHint", "Scribe uses this profile when one of these apps is in front.")
    let addApp = CopyItem.same("appProfiles.addApp", "Add app...")
    let addAppTitle = CopyItem.same("appProfiles.addAppTitle", "Add app")
    let addAppHint = CopyItem.same(
        "appProfiles.addAppHint", "Choose apps Scribe can see now, or apps you've dictated into recently.")
    let noApps = CopyItem.same("appProfiles.noApps", "No apps found.")
    let cancel = CopyItem.same("appProfiles.cancel", "Cancel")
    let writingStyle = CopyItem.same("appProfiles.writingStyle", "Writing style for these apps")
    let writingStyleHint = CopyItem.same("appProfiles.writingStyleHint", "Leave empty to use your main writing style.")
    let lineBreaks = CopyItem.same("appProfiles.lineBreaks", "Line breaks in these apps")
    let select = CopyItem.same("appProfiles.select", "Select a profile to edit it.")
    let empty = CopyItem.same(
        "appProfiles.empty", "No app profiles yet. Add one to change how Scribe writes in a specific app.")
    let breaksUseAdvanced = CopyItem.same("appProfiles.breaksUseAdvanced", "Use the Advanced setting")
    let breaksAutomatic = CopyItem.same("appProfiles.breaksAutomatic", "Automatic: one line in command windows")
    let breaksOneLine = CopyItem.same("appProfiles.breaksOneLine", "Always one line")
    let breaksKeep = CopyItem.same("appProfiles.breaksKeep", "Keep line breaks")
    let deleteTitle = CopyItem.same("appProfiles.deleteTitle", "Delete this profile?")
    let deleteBody = CopyItem.same("appProfiles.deleteBody", "Nothing changes until you save.")
    let deleteConfirm = CopyItem.same("appProfiles.deleteConfirm", "Delete profile")
    let emptyPreset = CopyItem.same("appProfiles.emptyPreset", "Start with an empty app profile.")
}

/// The History page. Windows: `SettingsWindow.xaml` lines 2505 to 2743, `SettingsWindow.History.cs`,
/// `ChoiceControls.cs` (the keep-for choices).
struct HistoryCopy: CopyCatalog {
    let prefix = "history"

    let subtitle = CopyItem.same("history.subtitle", "Find, copy or delete your recent dictations.")
    let settings = CopyItem.same("history.settings", "History settings")
    let keep = CopyItem.same("history.keep", "Keep dictations")
    let keepHint = CopyItem.same("history.keepHint", "Older dictations are deleted automatically.")
    let keep7 = CopyItem.same("history.keep7", "For 7 days")
    let keep30 = CopyItem.same("history.keep30", "For 30 days")
    let keep90 = CopyItem.same("history.keep90", "For 90 days (default)")
    let keepYear = CopyItem.same("history.keepYear", "For 1 year")
    let keepAlways = CopyItem.same("history.keepAlways", "Until I delete them")
    let saveRecording = CopyItem.same("history.saveRecording", "Save a recording with each dictation")
    let saveRecordingHint = CopyItem.changed(
        "history.saveRecordingHint",
        "Keeps the audio of each dictation on this Mac for up to 7 days (250 MB in total), then deletes it. Scribe "
            + "doesn't play recordings back.",
        windows: "Keeps the audio of each dictation on this PC for up to 7 days (250 MB in total), then deletes it. "
            + "Scribe doesn't play recordings back.",
        because: .thisMac)
    let find = CopyItem.same("history.find", "Find a dictation")
    let copy = CopyItem.same("history.copy", "Copy")
    let delete = CopyItem.same("history.delete", "Delete...")
    let deleteAll = CopyItem.same("history.deleteAll", "Delete all history...")
    let tryAgain = CopyItem.same("history.tryAgain", "Try again")
    let columnWhen = CopyItem.same("history.columnWhen", "When")
    let columnApp = CopyItem.same("history.columnApp", "App")
    let columnText = CopyItem.same("history.columnText", "Text")
    let columnCleanup = CopyItem.same("history.columnCleanup", "AI cleanup")
    let empty = CopyItem.same("history.empty", "No dictations yet. Hold your shortcut in any app and speak.")
    let loadOlder = CopyItem.same("history.loadOlder", "Load older")
    let selectedText = CopyItem.same("history.selectedText", "Selected dictation text")
    let useful = CopyItem.same("history.useful", "Useful")
    let notUseful = CopyItem.same("history.notUseful", "Not useful")
    let usefulSelected = CopyItem.same("history.usefulSelected", "Useful, selected")
    let notUsefulSelected = CopyItem.same("history.notUsefulSelected", "Not useful, selected")
    let report = CopyItem.same("history.report", "Report an AI cleanup problem...")
    let ratingFailed = CopyItem.same("history.ratingFailed", "Couldn't save that rating. Try again.")
    let copied = CopyItem.same("history.copied", "Copied the selected dictation.")
    let copyFailed = CopyItem.same("history.copyFailed", "Couldn't copy the dictation. Try again.")
    let deleteTitle = CopyItem.same("history.deleteTitle", "Delete this dictation?")
    let deleteBody = CopyItem.same("history.deleteBody", "This can't be undone.")
    let deleteConfirm = CopyItem.same("history.deleteConfirm", "Delete dictation")
    let deleted = CopyItem.same("history.deleted", "Deleted the selected history entry.")
    let deleteFailed = CopyItem.same("history.deleteFailed", "Couldn't delete the history entry. Try again.")
    let deleteAllTitle = CopyItem.same("history.deleteAllTitle", "Delete all history?")
    let deleteAllBody = CopyItem.same(
        "history.deleteAllBody",
        "This deletes every saved dictation and recording now, including ones not shown here. Your dictionary, "
            + "snippets and settings are kept. This can't be undone.")
    let deleteAllConfirm = CopyItem.same("history.deleteAllConfirm", "Delete all history")
    let cleared = CopyItem.same("history.cleared", "Cleared dictation history.")
    let clearFailed = CopyItem.same("history.clearFailed", "Couldn't clear history. Try again.")
}

/// The Usage page. Windows: `SettingsWindow.xaml` lines 2744 to 3009, `SettingsWindow.Usage.cs`.
struct UsageCopy: CopyCatalog {
    let prefix = "usage"

    let subtitle = CopyItem.same(
        "usage.subtitle", "How much you've dictated, and words you might add to your dictionary.")
    let period = CopyItem.same("usage.period", "Period")
    let refresh = CopyItem.same("usage.refresh", "Refresh")
    let tryAgain = CopyItem.same("usage.tryAgain", "Try again")
    let empty = CopyItem.same("usage.empty", "No dictations in this period.")
    let allKeptHistory = CopyItem.same("usage.allKeptHistory", "All kept history")
    let dictations = CopyItem.same("usage.dictations", "dictations")
    let words = CopyItem.same("usage.words", "words")
    let activeDays = CopyItem.same("usage.activeDays", "active days")
    let speakingTime = CopyItem.same("usage.speakingTime", "speaking time")
    let wordsPerDictation = CopyItem.same("usage.wordsPerDictation", "words per dictation")
    let longest = CopyItem.same("usage.longest", "longest dictation")
    let topApps = CopyItem.same("usage.topApps", "Top apps")
    let columnApp = CopyItem.same("usage.columnApp", "App")
    let columnDictations = CopyItem.same("usage.columnDictations", "Dictations")
    let columnWords = CopyItem.same("usage.columnWords", "Words")
    let trend = CopyItem.same("usage.trend", "Trend")
    let trendChart = CopyItem.same("usage.trendChart", "Trend chart")
    let showTable = CopyItem.same("usage.showTable", "Show as a table")
    let columnPeriod = CopyItem.same("usage.columnPeriod", "Period")
    let weekOf = CopyItem.same("usage.weekOf", "Week of {date}")
    let known = CopyItem.same("usage.known", "Words your dictionary already knows")
    let knownHint = CopyItem.same("usage.knownHint", "Dictionary and word pack words that came up in this period.")
    let knownNone = CopyItem.same("usage.knownNone", "None of your dictionary words came up in this period.")
    let suggestions = CopyItem.same("usage.suggestions", "Words you could add")
    let suggestionsHint = CopyItem.same(
        "usage.suggestionsHint", "Words that came up often and aren't in your dictionary yet.")
    let suggestionsNone = CopyItem.same("usage.suggestionsNone", "No new words in this period.")
    let addToDictionary = CopyItem.same("usage.addToDictionary", "Add to dictionary")
    let alreadyInGrid = CopyItem.same("usage.alreadyInGrid", "\"{word}\" is already in the dictionary grid.")
    let addedSaved = CopyItem.same("usage.addedSaved", "Added {word} to your dictionary. This is already saved.")
    let addFailed = CopyItem.same("usage.addFailed", "Couldn't add that word to your dictionary. Try again.")
    let aiSummary = CopyItem.same("usage.aiSummary", "AI summary")
    let getSummary = CopyItem.same("usage.getSummary", "Get summary")
    let summaryFailed = CopyItem.added("usage.summaryFailed", "The AI service returned no usable summary.")
}
