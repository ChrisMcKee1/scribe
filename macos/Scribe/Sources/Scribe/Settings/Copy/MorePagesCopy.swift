import Foundation

/// The Advanced page. Windows: `SettingsWindow.xaml` lines 1108 to 1300, `SettingsWindow.Advanced.cs`,
/// `ChoiceControls.cs`. The Windows accent switch, Remote Desktop pacing and the speech model download have no Mac
/// twin and are listed in `SettingsCopyOmissions`.
struct AdvancedCopy: CopyCatalog {
    let prefix = "advanced"

    let subtitle = CopyItem.changed(
        "advanced.subtitle",
        "Settings most people never need to change. The defaults suit most Macs.",
        windows: "Settings most people never need to change. The defaults suit most PCs.",
        because: .thisMac)
    let speechRecognition = CopyItem.same("advanced.speechRecognition", "Speech recognition")
    let speechModel = CopyItem.same("advanced.speechModel", "Speech model")
    let threads = CopyItem.same("advanced.threads", "Processor threads")
    let threadsHint = CopyItem.changed(
        "advanced.threadsHint",
        "How many processor threads speech recognition uses. Automatic suits most Macs.",
        windows: "How many processor threads speech recognition uses. Automatic suits most PCs.",
        because: .thisMac)
    let freeMemory = CopyItem.same("advanced.freeMemory", "Free memory when Scribe isn't used")
    let freeMemoryHint = CopyItem.changed(
        "advanced.freeMemoryHint",
        "After this long without a dictation, Scribe frees the memory it uses for dictation. The next dictation may "
            + "take longer before Scribe types it.",
        windows: "After this long without a dictation, Scribe frees the memory its speech model and its own AI "
            + "cleanup model use, and asks Ollama and LM Studio to free theirs. The next dictation may take longer "
            + "before Scribe types it. Ollama and LM Studio may also free a model on their own.",
        because: .staleWindowsCorrected)
    let restartNote = CopyItem.same(
        "advanced.restartNote",
        "Changes to the speech model and processor threads take effect after you restart Scribe.")
    let idleNever = CopyItem.same("advanced.idleNever", "Never")
    let idle5 = CopyItem.same("advanced.idle5", "After 5 minutes")
    let idle10 = CopyItem.same("advanced.idle10", "After 10 minutes (default)")

    let recording = CopyItem.same("advanced.recording", "Recording")
    let trimSilence = CopyItem.same("advanced.trimSilence", "Trim silence")
    let trimSilenceHint = CopyItem.same(
        "advanced.trimSilenceHint",
        "Removes silence at the start and end of a recording, and skips recordings with no speech.")
    let longest = CopyItem.same("advanced.longest", "Longest recording")
    let longestHint = CopyItem.same(
        "advanced.longestHint",
        "A recording stops at this length and Scribe types what it heard, so a stuck key can't record forever.")
    let longest5 = CopyItem.same("advanced.longest5", "5 minutes")
    let longest10 = CopyItem.same("advanced.longest10", "10 minutes (default)")
    let longest30 = CopyItem.same("advanced.longest30", "30 minutes")
    let longest60 = CopyItem.same("advanced.longest60", "1 hour")
    let longestNone = CopyItem.same("advanced.longestNone", "No limit")

    let typingIntoApps = CopyItem.same("advanced.typingIntoApps", "Typing into apps")
    let typingMethod = CopyItem.same("advanced.typingMethod", "Typing method")
    let typingMethodHint = CopyItem.added(
        "advanced.typingMethodHint",
        "Scribe types into the app in front. Pasting is faster for long text: Scribe uses the clipboard for a moment "
            + "and puts back what you had copied when it can. Some apps block pasting.",
        because: .macBehaviour)
    let lineBreaks = CopyItem.same("advanced.lineBreaks", "Line breaks")
    let lineBreaksHint = CopyItem.same(
        "advanced.lineBreaksHint",
        "In command windows such as Terminal, a line break works like Enter and can send text early.")
    let chatSafe = CopyItem.same("advanced.chatSafe", "Don't send chat messages early")
    let chatSafeHint = CopyItem.changed(
        "advanced.chatSafeHint",
        "Types line breaks as Shift-Return, so apps like Teams and Slack start a new line instead of sending. Turn it "
            + "off if an app uses Shift-Return for something else.",
        windows: "Types line breaks as Shift+Enter, so apps like Teams and Slack start a new line instead of sending. "
            + "Turn it off if an app uses Shift+Enter for something else.",
        because: .macKeys)
    let textChanges = CopyItem.same("advanced.textChanges", "Text changes")
    let applyRules = CopyItem.same("advanced.applyRules", "Apply your dictionary and snippets")
    let applyRulesHint = CopyItem.same(
        "advanced.applyRulesHint",
        "Applies your dictionary, snippets and spacing fixes to what Scribe hears. AI cleanup still receives your "
            + "vocabulary when this is off.")
    let restoreDefaults = CopyItem.same("advanced.restoreDefaults", "Restore advanced defaults")
    let restoreTitle = CopyItem.same("advanced.restoreTitle", "Restore the advanced settings to their defaults?")
    let restoreBody = CopyItem.same("advanced.restoreBody", "Nothing changes until you save.")
    let restoreConfirm = CopyItem.same("advanced.restoreConfirm", "Restore defaults")
    let restored = CopyItem.same("advanced.restored", "Advanced defaults restored. Choose Save to keep them.")
}

/// The Diagnostics page. Windows: `SettingsWindow.xaml` lines 3010 to 3151. The Mac has no log files or zip: its log
/// lines go to the unified log, so the "Save diagnostics" and "Logs folder" rows become Console rows.
struct DiagnosticsCopy: CopyCatalog {
    let prefix = "diagnostics"
    let title = CopyItem.same("diagnostics.title", "Diagnostics")

    let subtitle = CopyItem.same(
        "diagnostics.subtitle", "Get help, see what went wrong, and check how fast dictation runs.")
    let getHelp = CopyItem.same("diagnostics.getHelp", "Get help")
    let report = CopyItem.same("diagnostics.report", "Report a problem")
    let reportHint = CopyItem.same(
        "diagnostics.reportHint",
        "Opens GitHub to report a problem or suggest a feature. Don't include dictations, recordings or keys in a "
            + "public report.")
    let supportDetails = CopyItem.added("diagnostics.supportDetails", "Copy support details")
    let supportDetailsHint = CopyItem.added(
        "diagnostics.supportDetailsHint",
        "Copies your Scribe and macOS versions, your settings choices and your recent timings. It never contains your "
            + "dictations.")
    let console = CopyItem.added("diagnostics.console", "Open Console")
    let consoleHint = CopyItem.added(
        "diagnostics.consoleHint",
        "Scribe's log lines appear in Console under com.scribe.macos. They record app events, timings and errors, "
            + "never your dictations. Read them before sharing.")
    let cleanupProblems = CopyItem.same("diagnostics.cleanupProblems", "AI cleanup problems")
    let cleanupProblemsHint = CopyItem.same(
        "diagnostics.cleanupProblemsHint",
        "Times AI cleanup couldn't finish in the last 7 days, so Scribe typed what it heard instead.")
    let clearList = CopyItem.same("diagnostics.clearList", "Clear this list...")
    let columnWhen = CopyItem.same("diagnostics.columnWhen", "When")
    let columnModel = CopyItem.same("diagnostics.columnModel", "Model")
    let columnWhat = CopyItem.same("diagnostics.columnWhat", "What happened")
    let noFailures = CopyItem.same("diagnostics.noFailures", "No AI cleanup failures recorded in the last 7 days.")
    let speed = CopyItem.same("diagnostics.speed", "Speed")
    let speedHint = CopyItem.same("diagnostics.speedHint", "How long each step takes")
    let noRuns = CopyItem.same("diagnostics.noRuns", "No dictations in the last 7 days.")
    let tryAgain = CopyItem.same("diagnostics.tryAgain", "Try again")
    let speechRecognition = CopyItem.same("diagnostics.speechRecognition", "Speech recognition")
    let aiCleanup = CopyItem.same("diagnostics.aiCleanup", "AI cleanup")
    let both = CopyItem.same("diagnostics.both", "Both")
    let typical = CopyItem.same("diagnostics.typical", "Typical")
    let nineteenInTwenty = CopyItem.same("diagnostics.nineteenInTwenty", "19 in 20 finish within")
    let speedDetails = CopyItem.same("diagnostics.speedDetails", "Speed details")
    let speedDetailsHint = CopyItem.same("diagnostics.speedDetailsHint", "Average, fastest and slowest runs by step.")
    let latestOnly = CopyItem.same("diagnostics.latestOnly", "Only your latest 1,000 dictations are counted.")
    let recognitionOnly = CopyItem.same(
        "diagnostics.recognitionOnly", "Speech recognition time only. AI cleanup isn't counted here.")
    let recognitionNone = CopyItem.same("diagnostics.recognitionNone", "No speech recognition runs in this period yet.")
    let cleanupAfter = CopyItem.same("diagnostics.cleanupAfter", "AI cleanup time after speech recognition finishes.")
    let cleanupNone = CopyItem.same("diagnostics.cleanupNone", "No AI cleanup runs in this period yet.")
    let recognitionPlusCleanup = CopyItem.same("diagnostics.recognitionPlusCleanup", "Recognition plus AI cleanup")
    let bothNone = CopyItem.same("diagnostics.bothNone", "No cleanup-enabled runs in this period yet.")
    let average = CopyItem.same("diagnostics.average", "average")
    let fastest = CopyItem.same("diagnostics.fastest", "fastest")
    let slowest = CopyItem.same("diagnostics.slowest", "slowest")
    let thisMac = CopyItem.changed("diagnostics.thisMac", "This Mac", windows: "This PC", because: .thisMac)
    let checkingHardware = CopyItem.same("diagnostics.checkingHardware", "Checking hardware...")
    let whereData = CopyItem.same("diagnostics.whereData", "Where Scribe keeps your data")
    let dataWarning = CopyItem.changed(
        "diagnostics.dataWarning",
        "Never send or share this file. It holds everything you've dictated. To delete dictations, use History.",
        windows: "Never send or share this file. It holds everything you've dictated and your saved keys. To delete "
            + "dictations, use History.",
        because: .keychain)
    let dataFile = CopyItem.same("diagnostics.dataFile", "Scribe data file")
    let copyPath = CopyItem.same("diagnostics.copyPath", "Copy")
    let copyPathTip = CopyItem.same("diagnostics.copyPathTip", "Copy this path to the clipboard.")
    let openFolder = CopyItem.changed(
        "diagnostics.openFolder", "Show in Finder", windows: "Open folder", because: .macSystemFeature)
    let openFolderTip = CopyItem.changed(
        "diagnostics.openFolderTip",
        "Show the containing folder in Finder.",
        windows: "Open the containing folder in File Explorer.",
        because: .macSystemFeature)
}

/// The About page. Windows: `SettingsWindow.xaml` lines 3152 to 3212.
struct AboutCopy: CopyCatalog {
    let prefix = "about"
    let title = CopyItem.same("about.title", "About")

    let subtitle = CopyItem.same("about.subtitle", "Version, updates, help, privacy and feedback.")
    let version = CopyItem.same("about.version", "Version")
    let tagline = CopyItem.changed(
        "about.tagline",
        "Private push-to-talk dictation for Mac",
        windows: "Private push-to-talk dictation for Windows",
        because: .macSystemFeature)
    let updates = CopyItem.same("about.updates", "Updates")
    let updatesHint = CopyItem.added(
        "about.updatesHint",
        "Scribe for Mac doesn't update itself yet. Get the latest version from GitHub.",
        because: .macBehaviour)
    let getHelp = CopyItem.same("about.getHelp", "Get help")
    let getHelpHint = CopyItem.added(
        "about.getHelpHint",
        "Having a problem? Open Diagnostics, copy the support details and attach them when you report it.")
    let report = CopyItem.same("about.report", "Report a problem")
    let openDiagnostics = CopyItem.same("about.openDiagnostics", "Open Diagnostics")
    let welcomeAgain = CopyItem.same("about.welcomeAgain", "Show the welcome again")
    let privacy = CopyItem.same("about.privacy", "Privacy")
    let privacyHint = CopyItem.changed(
        "about.privacyHint",
        "Speech recognition runs on this Mac, and your audio never leaves it. AI cleanup is optional, and online "
            + "services receive text only while you use them.",
        windows: "Speech recognition runs on this PC, and your audio never leaves it. AI cleanup is optional, and "
            + "online services receive text only while you use them.",
        because: .thisMac)
    let privacyLink = CopyItem.same("about.privacyLink", "Read the privacy policy")
    let feedback = CopyItem.same("about.feedback", "Feedback")
    let reportResult = CopyItem.same("about.reportResult", "Report an AI result...")
    let reportResultHint = CopyItem.same(
        "about.reportResultHint",
        "If AI cleanup wrote something inappropriate, tell us. Your mail app opens with the report so you can read it "
            + "first. Scribe sends nothing by itself. You can also report a result from History.")
    let support = CopyItem.same("about.support", "Support Scribe")
    let star = CopyItem.same("about.star", "Star Scribe on GitHub")
    let starHint = CopyItem.same(
        "about.starHint", "A GitHub star helps other people discover private, offline dictation.")
    let openGitHub = CopyItem.same("about.openGitHub", "Open GitHub")
    let source = CopyItem.same("about.source", "Source")
    let sourceHint = CopyItem.same("about.sourceHint", "Scribe is open source under the MIT License.")
    let viewSource = CopyItem.same("about.viewSource", "View source")
}
