import Foundation

/// The menu bar menu and the status item's tooltip. Windows: `TrayMenu.cs`, `TrayToolTip.cs`. The menu keeps the
/// Windows wording in sentence case, so Settings, the menu and the notices name things alike.
struct MenuBarCopy: CopyCatalog {
    let prefix = "menuBar"

    let settings = CopyItem.changed("menuBar.settings", "Settings...", windows: "Settings", because: .macSystemFeature)
    let addToDictionary = CopyItem.same("menuBar.addToDictionary", "Add to dictionary...")
    let copyLast = CopyItem.same("menuBar.copyLast", "Copy last dictation")
    let copyRecent = CopyItem.same("menuBar.copyRecent", "Copy a recent dictation")
    let noRecent = CopyItem.same("menuBar.noRecent", "No recent dictations")
    let openHistory = CopyItem.same("menuBar.openHistory", "Open history")
    let microphone = CopyItem.same("menuBar.microphone", "Microphone")
    let aiCleanup = CopyItem.same("menuBar.aiCleanup", "Use AI cleanup")
    let pause = CopyItem.same("menuBar.pause", "Pause dictation")
    let quit = CopyItem.same("menuBar.quit", "Quit Scribe")
    let ready = CopyItem.same("menuBar.ready", "Scribe: ready.")
    let readyHold = CopyItem.same("menuBar.readyHold", "Scribe: ready. Hold {shortcut} and speak.")
    let readyToggle = CopyItem.same("menuBar.readyToggle", "Scribe: ready. Press {shortcut} to start and stop.")
    let listening = CopyItem.same("menuBar.listening", "Scribe: listening...")
    let writing = CopyItem.same("menuBar.writing", "Scribe: writing your text...")
    let paused = CopyItem.same(
        "menuBar.paused", "Scribe: paused. Your shortcuts work as usual in other apps until you resume.")
    let yourShortcut = CopyItem.same("menuBar.yourShortcut", "your shortcut")
    let defaultSettings = CopyItem.same(
        "menuBar.defaultSettings", "Using default settings. Open Settings to review them.")
    let noSpeechModel = CopyItem.changed(
        "menuBar.noSpeechModel",
        "No speech model is installed. Install Foundry Local to start dictating.",
        windows: "No speech model is installed. Choose one in Settings.",
        because: .foundryLocal)
    let speechModelFailed = CopyItem.same(
        "menuBar.speechModelFailed", "The speech model didn't load. Scribe tries again when you dictate.")
}

/// The notices Scribe shows from the menu bar and as macOS notifications. Windows: `TrayNotice.cs`.
struct NoticeCopy: CopyCatalog {
    let prefix = "notice"

    let nothingToCopyTitle = CopyItem.same("notice.nothingToCopyTitle", "Nothing to copy")
    let nothingToCopyBody = CopyItem.same("notice.nothingToCopyBody", "There's no dictation to copy yet.")
    let copiedTitle = CopyItem.same("notice.copiedTitle", "Copied")
    let copiedLastBody = CopyItem.changed(
        "notice.copiedLastBody",
        "Your last dictation is on the clipboard. Press \u{2318}V to paste it.",
        windows: "Your last dictation is on the clipboard. Press Ctrl+V to paste it.",
        because: .macKeys)
    let copiedRecentBody = CopyItem.changed(
        "notice.copiedRecentBody",
        "That dictation is on the clipboard. Press \u{2318}V to paste it.",
        windows: "That dictation is on the clipboard. Press Ctrl+V to paste it.",
        because: .macKeys)
    let copyFailedTitle = CopyItem.same("notice.copyFailedTitle", "Couldn't copy")
    let copyFailedBody = CopyItem.same(
        "notice.copyFailedBody", "Another app may be using the clipboard. Try again in a moment.")
    let quickAddFailedTitle = CopyItem.same("notice.quickAddFailedTitle", "Couldn't open Add to dictionary")
    let quickAddFailedBody = CopyItem.same(
        "notice.quickAddFailedBody", "Try again, or add the word in Settings, Dictionary.")
    let quickAddSavedTitle = CopyItem.same("notice.quickAddSavedTitle", "Saved to your dictionary")
    let quickAddSavedBody = CopyItem.same("notice.quickAddSavedBody", "Scribe uses it from your next dictation.")
    let quickAddNotInUseTitle = CopyItem.same("notice.quickAddNotInUseTitle", "Saved, but not in use yet")
    let quickAddNotInUseBody = CopyItem.same(
        "notice.quickAddNotInUseBody",
        "Scribe saved your word but couldn't start using it. Quit and reopen Scribe to use it.")
    let typingFailedTitle = CopyItem.same("notice.typingFailedTitle", "Couldn't type your dictation")
    let typingIncompleteBody = CopyItem.changed(
        "notice.typingIncompleteBody",
        "This app didn't accept all of the text. Click the Scribe icon in the menu bar and choose Copy last dictation, then paste it.",
        windows: "This app didn't accept all of the text. Right-click the Scribe icon and choose Copy last dictation, then paste it.",
        because: .macSystemFeature)
    let typingChangedBody = CopyItem.changed(
        "notice.typingChangedBody",
        "The window changed before Scribe finished typing. Click the Scribe icon in the menu bar and choose Copy last dictation, then paste it.",
        windows: "The window changed before Scribe finished typing. Right-click the Scribe icon and choose Copy last dictation, then paste it.",
        because: .macSystemFeature)
    let soundFailedTitle = CopyItem.changed(
        "notice.soundFailedTitle",
        "Couldn't open Sound settings",
        windows: "Couldn't open sound settings",
        because: .macSystemFeature)
    let soundFailedBody = CopyItem.changed(
        "notice.soundFailedBody",
        "Open System Settings, then choose Sound.",
        windows: "Open Windows Settings > System > Sound.",
        because: .macSystemFeature)
    let cleanupChangeFailedTitle = CopyItem.same("notice.cleanupChangeFailedTitle", "Couldn't change AI cleanup")
    let cleanupChangeFailedBody = CopyItem.same(
        "notice.cleanupChangeFailedBody", "Try again, or change it in Settings, AI cleanup.")
    let microphoneChangeFailedTitle = CopyItem.same(
        "notice.microphoneChangeFailedTitle", "Couldn't change the microphone")
    let microphoneChangeFailedBody = CopyItem.same(
        "notice.microphoneChangeFailedBody", "Try again, or choose one in Settings, Dictation.")
    let noSpeechModelTitle = CopyItem.same("notice.noSpeechModelTitle", "No speech model")
    let noSpeechModelBody = CopyItem.added(
        "notice.noSpeechModelBody",
        "Install Foundry Local to start dictating. In Terminal, run brew install microsoft/foundrylocal/foundrylocal.",
        because: .foundryLocal)
    let speechModelFailedTitle = CopyItem.same("notice.speechModelFailedTitle", "Speech model didn't load")
    let speechModelFailedBody = CopyItem.added(
        "notice.speechModelFailedBody",
        "Scribe tries again when you dictate. If dictation doesn't work, copy the support details in Settings, Diagnostics and report the problem.")
    let cleanupFailingTitle = CopyItem.same("notice.cleanupFailingTitle", "AI cleanup isn't working")
    let cleanupFailingBody = CopyItem.same(
        "notice.cleanupFailingBody",
        "Scribe types what it hears until it's fixed. Open Settings, AI cleanup to see why.")
    let defaultsTitle = CopyItem.same("notice.defaultsTitle", "Using default settings")
    let defaultsBody = CopyItem.same(
        "notice.defaultsBody",
        "Scribe couldn't use your saved settings, so it's using defaults for now. Open Settings, review them and choose Save to keep them.")
    let reviewTitle = CopyItem.same("notice.reviewTitle", "Your settings need a review")
    let reviewBody = CopyItem.same(
        "notice.reviewBody",
        "Scribe couldn't use your saved settings, so it's using defaults. Open Settings, review them and choose Save. Then you can change {change} here.")
    let repairedTitle = CopyItem.same("notice.repairedTitle", "Scribe repaired its data")
    let cleanupIsOn = CopyItem.same("notice.cleanupIsOn", "AI cleanup is on")
    let cleanupIsOff = CopyItem.same("notice.cleanupIsOff", "AI cleanup is off")
}

/// What the recording indicator says when a dictation ends. Windows: `PillOutcome.cs`, `DictationProblem.cs`
/// (`PillLine`). The Mac adds the permission steps, which Windows never needs.
struct IndicatorCopy: CopyCatalog {
    let prefix = "indicator"

    let typed = CopyItem.same("indicator.typed", "Typed")
    let typedWithoutCleanup = CopyItem.same("indicator.typedWithoutCleanup", "Typed without AI cleanup")
    let cleanupStep = CopyItem.same("indicator.cleanupStep", "See Settings, AI cleanup")
    let nothingTyped = CopyItem.same("indicator.nothingTyped", "Nothing typed")
    let partlyTyped = CopyItem.same("indicator.partlyTyped", "Not all of it was typed")
    let copyStep = CopyItem.changed(
        "indicator.copyStep",
        "Copy it from the menu bar",
        windows: "Copy it from the tray menu",
        because: .macSystemFeature)
    let holdStep = CopyItem.same("indicator.holdStep", "Hold the shortcut to speak")
    let toggleStep = CopyItem.same("indicator.toggleStep", "Press, speak, press again")
    let microphoneStep = CopyItem.same("indicator.microphoneStep", "Check your microphone")
    let otherMicrophone = CopyItem.same("indicator.otherMicrophone", "Try another microphone")
    let noWords = CopyItem.same("indicator.noWords", "No words heard, try again")
    let wentWrong = CopyItem.same("indicator.wentWrong", "Something went wrong")
    let defaultMicrophone = CopyItem.same("indicator.defaultMicrophone", "Using the default mic")
    let noSpeechModel = CopyItem.same("indicator.noSpeechModel", "No speech model")
    let startingModel = CopyItem.added("indicator.startingModel", "Starting local model...")
    let canTakeTime = CopyItem.added("indicator.canTakeTime", "This can take time")
    let allowAccessibility = CopyItem.added("indicator.allowAccessibility", "Allow Accessibility access")
    let allowMicrophone = CopyItem.added("indicator.allowMicrophone", "Allow Microphone access")
    let installFoundry = CopyItem.added("indicator.installFoundry", "Install Foundry Local")
    let tryAgain = CopyItem.added("indicator.tryAgain", "Try again")
}

/// The words Find a setting shows around its results. Windows: `SettingsWindow.xaml` (the box), `SettingsWindow.Search.cs`.
struct SearchCopy: CopyCatalog {
    let prefix = "search"

    let placeholder = CopyItem.same("search.placeholder", "Find a setting")
    let noResults = CopyItem.same("search.noResults", "No settings found")
    let chooseToSee = CopyItem.same("search.chooseToSee", "Choose \"{label}\" to see this setting.")
    let turnOnToSee = CopyItem.same("search.turnOnToSee", "Turn on \"{label}\" to see this setting.")
    let signInToSee = CopyItem.same("search.signInToSee", "Sign in to Azure to see this setting.")
    let resultOn = CopyItem.same("search.resultOn", "{setting} on {page}")
}
