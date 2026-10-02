import Foundation

/// The Dictionary page's Your words tab. Windows: `SettingsWindow.xaml` lines 1814 to 2039,
/// `SettingsWindow.YourWords.cs`, `DictionaryTabsText.cs`.
struct DictionaryCopy: CopyCatalog {
    let prefix = "dictionary"

    let subtitle = CopyItem.same(
        "dictionary.subtitle", "Teach Scribe how to write the words it hears, like \"dot net\" as .NET.")
    let yourWordsTab = CopyItem.same("dictionary.yourWordsTab", "Your words")
    let wordPacksTab = CopyItem.same("dictionary.wordPacksTab", "Word packs")
    let yourWordsGuide = CopyItem.same(
        "dictionary.yourWordsGuide", "Words you add yourself. If a word pack writes a word differently, yours wins.")
    let yourWordsLink = CopyItem.same("dictionary.yourWordsLink", "Browse word packs")
    let wordPacksGuide = CopyItem.same(
        "dictionary.wordPacksGuide",
        "Ready-made lists of words, like product names. Turn on the ones you use. Your own words always win.")
    let wordPacksLink = CopyItem.same("dictionary.wordPacksLink", "Go to Your words")
    let noWordPacks = CopyItem.same("dictionary.noWordPacks", "No word packs")
    let packsOn = CopyItem.same("dictionary.packsOn", "{on} of {total} on")
    let offTitle = CopyItem.same("dictionary.offTitle", "Dictionary and snippets are off")
    let turnOn = CopyItem.same("dictionary.turnOn", "Turn on")

    let addWord = CopyItem.same("dictionary.addWord", "Add word")
    let addWordTip = CopyItem.same("dictionary.addWordTip", "Add one or several ways Scribe hears the same word.")
    let edit = CopyItem.same("dictionary.edit", "Edit")
    let editTip = CopyItem.same("dictionary.editTip", "Edit the selected word, or add another way Scribe hears it.")
    let learn = CopyItem.same("dictionary.learn", "Learn from history")
    let learnTip = CopyItem.changed(
        "dictionary.learnTip",
        "Suggests words from your recent dictations, found on this Mac. You review every suggestion.",
        windows: "Suggests words from your recent dictations. If AI cleanup uses an online service, Scribe asks "
            + "before sending anything.",
        because: .staleWindowsCorrected)
    let cleanUp = CopyItem.same("dictionary.cleanUp", "Clean up unused words...")
    let cleanUpTip = CopyItem.changed(
        "dictionary.cleanUpTip",
        "Finds words you haven't used, so you can turn them off. You review every change.",
        windows: "Finds words and word packs you haven't used, so you can turn them off. You review every change.",
        because: .staleWindowsCorrected)
    let cleanUpTitle = CopyItem.same("dictionary.cleanUpTitle", "Clean up unused words")
    let more = CopyItem.same("dictionary.more", "More")
    let moreTip = CopyItem.same("dictionary.moreTip", "More dictionary actions")
    let reviewOverlaps = CopyItem.same("dictionary.reviewOverlaps", "Review overlaps with word packs...")
    let importCsv = CopyItem.same("dictionary.importCsv", "Import from a CSV file...")
    let exportCsv = CopyItem.same("dictionary.exportCsv", "Export to a CSV file...")
    let csvTemplate = CopyItem.same("dictionary.csvTemplate", "Get a CSV template...")
    let learning = CopyItem.same("dictionary.learning", "Learning from your history...")
    let checking = CopyItem.same("dictionary.checking", "Checking your history...")
    let find = CopyItem.same("dictionary.find", "Find a word")

    let columnOn = CopyItem.same("dictionary.columnOn", "On")
    let columnHears = CopyItem.same("dictionary.columnHears", "Scribe hears")
    let columnWrites = CopyItem.same("dictionary.columnWrites", "Scribe writes")
    let wholeWords = CopyItem.same("dictionary.wholeWords", "Whole words only")
    let wholeWordsTip = CopyItem.same(
        "dictionary.wholeWordsTip",
        "Off lets Scribe replace text inside longer words, for example \"net\" inside \"network\".")
    let columnWordPack = CopyItem.same("dictionary.columnWordPack", "Word pack")
    let editThisWord = CopyItem.same("dictionary.editThisWord", "Edit this word")
    let deleteThisWord = CopyItem.same("dictionary.deleteThisWord", "Delete this word")
    let details = CopyItem.same("dictionary.details", "Details")

    let emptyNoWords = CopyItem.same("dictionary.emptyNoWords", "No words yet. Add names and terms Scribe gets wrong.")
    let emptyNoMatches = CopyItem.same("dictionary.emptyNoMatches", "No words match your search.")
    let clearSearch = CopyItem.same("dictionary.clearSearch", "Clear search")
    let loading = CopyItem.same("dictionary.loading", "Loading your dictionary...")
    let loadFailed = CopyItem.same("dictionary.loadFailed", "Couldn't load your dictionary.")
    let tryAgain = CopyItem.same("dictionary.tryAgain", "Try again")
    let wordsOn = CopyItem.same("dictionary.wordsOn", "{on} of {total} words are on.")
    let packVocabularyUnavailable = CopyItem.same(
        "dictionary.packVocabularyUnavailable", "Word pack vocabulary is unavailable right now.")
    let noOverlaps = CopyItem.same("dictionary.noOverlaps", "No words overlap with word packs.")
    let coverageSame = CopyItem.same(
        "dictionary.coverageSame",
        "\"{pack}\" already writes this as \"{theirs}\", so this word changes nothing. You can keep it or delete it.")
    let coverageDifferent = CopyItem.same(
        "dictionary.coverageDifferent",
        "\"{pack}\" writes this as \"{theirs}\". Your spelling wins. Turn yours off to use the word pack's.")
    let waitForSave = CopyItem.same(
        "dictionary.waitForSave", "Wait for Settings to finish saving before opening the word editor.")
    let waitForLoad = CopyItem.same(
        "dictionary.waitForLoad", "Wait for your dictionary to load before adding or editing a word.")
    let finishCorrecting = CopyItem.same(
        "dictionary.finishCorrecting", "Finish correcting the word you are editing first.")
    let changedWhileOpen = CopyItem.same(
        "dictionary.changedWhileOpen", "This word changed while its editor was open. Cancel and open it again.")

    let deleteTitle = CopyItem.same("dictionary.deleteTitle", "Delete selected words?")
    let deleteBody = CopyItem.same(
        "dictionary.deleteBody",
        "{words} will be removed from your dictionary when you save. This can't be undone once saved. Turning them "
            + "off instead keeps them in the list so you can switch them back on later.")
    let nothingToSuggest = CopyItem.same("dictionary.nothingToSuggest", "Nothing to suggest")
    let noHistoryToLearn = CopyItem.same(
        "dictionary.noHistoryToLearn",
        "There are no recent dictations to learn from yet. Keep dictating and try again later.")
    let noNewWords = CopyItem.same(
        "dictionary.noNewWords", "No recurring technical words found in your recent dictations yet.")
    let suggestionsNeedThree = CopyItem.same(
        "dictionary.suggestionsNeedThree",
        "Suggestions appear once a word shows up in three or more dictations, so keep dictating and try again later.")
    let added = CopyItem.same("dictionary.added", "Added {count} suggested {words}")
    let addedReview = CopyItem.same(
        "dictionary.addedReview",
        "from your recent dictations. Review them in the grid, delete any you don't want, then save.")
    let scanFailed = CopyItem.same("dictionary.scanFailed", "Couldn't scan history")
    let scanOutOfDate = CopyItem.same(
        "dictionary.scanOutOfDate",
        "Your dictionary changed while the scan was running, so the results are out of date. Run the cleanup again.")
    let templateFailed = CopyItem.same("dictionary.templateFailed", "Couldn't save the template")
    let exportFailed = CopyItem.same("dictionary.exportFailed", "Couldn't export the dictionary")
    let readFailed = CopyItem.same("dictionary.readFailed", "Couldn't read that file")
}

/// The Dictionary page's Word packs tab. Windows: `SettingsWindow.xaml` 2017 to 2039, `SettingsWindow.WordPacks.cs`,
/// `WordPackUiText.cs`.
struct WordPackCopy: CopyCatalog {
    let prefix = "wordPacks"

    let newPack = CopyItem.same("wordPacks.newPack", "New word pack")
    let importPack = CopyItem.same("wordPacks.importPack", "Import a word pack...")
    let importTip = CopyItem.same("wordPacks.importTip", "Add a word pack from a CSV file.")
    let more = CopyItem.same("wordPacks.more", "More")
    let aboutPacks = CopyItem.same("wordPacks.aboutPacks", "About word packs")
    let searchAll = CopyItem.changed(
        "wordPacks.searchAll",
        "Search all word packs (\u{2318}F)",
        windows: "Search all word packs (Ctrl+F)",
        because: .macKeys)
    let backToPacks = CopyItem.same("wordPacks.backToPacks", "Back to word packs")
    let packs = CopyItem.same("wordPacks.packs", "Word packs")
    let columnOn = CopyItem.same("wordPacks.columnOn", "On")
    let columnPack = CopyItem.same("wordPacks.columnPack", "Word pack")
    let packName = CopyItem.same("wordPacks.packName", "Word pack name")
    let details = CopyItem.same("wordPacks.details", "Details")
    let usePack = CopyItem.same("wordPacks.usePack", "Use this word pack")
    let useInAi = CopyItem.same("wordPacks.useInAi", "Use in AI cleanup")
    let export = CopyItem.same("wordPacks.export", "Export...")
    let exportTip = CopyItem.same("wordPacks.exportTip", "Save the selected word pack as a CSV file.")
    let addWord = CopyItem.same("wordPacks.addWord", "Add word")
    let sort = CopyItem.same("wordPacks.sort", "Sort")
    let wordsInPack = CopyItem.same("wordPacks.wordsInPack", "Words in selected word pack")
    let columnUse = CopyItem.same("wordPacks.columnUse", "Use")
    let columnHears = CopyItem.same("wordPacks.columnHears", "Scribe hears")
    let columnWrites = CopyItem.same("wordPacks.columnWrites", "Scribe writes")
    let choosePack = CopyItem.same("wordPacks.choosePack", "Choose a word pack to edit, or create your own.")
    let packOff = CopyItem.same("wordPacks.packOff", "This word pack is off. You can still edit its words.")
    let wordDetails = CopyItem.same("wordPacks.wordDetails", "Word details")
    let backToWords = CopyItem.same("wordPacks.backToWords", "Back to words")
    let wholeWords = CopyItem.same("wordPacks.wholeWords", "Whole words only")
    let goToEntry = CopyItem.same("wordPacks.goToEntry", "Go to dictionary entry")
    let builtIn = CopyItem.same("wordPacks.builtIn", "Built-in")
    let imported = CopyItem.same("wordPacks.imported", "Imported")
    let aiOnThisMac = CopyItem.changed(
        "wordPacks.aiOnThisMac",
        "AI cleanup runs on this Mac, so nothing leaves it.",
        windows: "AI cleanup runs on this PC, so nothing leaves it.",
        because: .thisMac)
    let aiSends = CopyItem.changed(
        "wordPacks.aiSends",
        "Sends the words of this word pack that your dictation seems to mention, as vocabulary with the AI cleanup "
            + "request.",
        windows: "Sends this word pack's words as vocabulary with every AI cleanup request, whether or not you say "
            + "them.",
        because: .staleWindowsCorrected)
    let aiNotSent = CopyItem.same(
        "wordPacks.aiNotSent", "Not sent as vocabulary. Words you dictate still reach the AI service in the text.")
    let markerNotUsed = CopyItem.same("wordPacks.markerNotUsed", "Not used")
    let markerUpdate = CopyItem.same("wordPacks.markerUpdate", "Update")
    let markerOffHere = CopyItem.same("wordPacks.markerOffHere", "Off here")
    let markerCheck = CopyItem.same("wordPacks.markerCheck", "Check")
    let markerRemoves = CopyItem.same("wordPacks.markerRemoves", "Removes")
    let markerChanged = CopyItem.same("wordPacks.markerChanged", "Changed")
    let notSentPermission = CopyItem.same(
        "wordPacks.notSentPermission", "Not sent to AI cleanup: this word pack isn't used in AI cleanup.")
    let notSentWord = CopyItem.same(
        "wordPacks.notSentWord", "Not sent to AI cleanup: this word is not included in vocabulary.")
    let csvMissing = CopyItem.same("wordPacks.csvMissing", "missing a value")
    let csvEmptySpoken = CopyItem.same("wordPacks.csvEmptySpoken", "Scribe hears is empty")
    let csvWholeWord = CopyItem.same("wordPacks.csvWholeWord", "whole words value is not true or false")
    let csvEnabled = CopyItem.same("wordPacks.csvEnabled", "on value is not true or false")
    let csvQuote = CopyItem.same("wordPacks.csvQuote", "a quoted value is not closed")
    let csvTooLong = CopyItem.same("wordPacks.csvTooLong", "a value is too long")
    let csvFormat = CopyItem.same("wordPacks.csvFormat", "row format is not supported")
}
