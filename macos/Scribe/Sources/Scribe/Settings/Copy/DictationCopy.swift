import Foundation

/// The Dictation page: microphone, shortcuts, typing, the recording indicator and start at login.
/// Windows: `SettingsWindow.xaml` lines 876 to 1107, `ShortcutRules.cs`, `MicrophoneChoices.cs`,
/// `SettingsWindow.xaml.cs` (the shortcut hints).
struct DictationCopy: CopyCatalog {
    let prefix = "dictation"

    // Page
    let subtitle = CopyItem.same(
        "dictation.subtitle", "Your microphone, your shortcuts, and what you see while you dictate.")
    let usingDefaultsTitle = CopyItem.same("dictation.usingDefaultsTitle", "Using default settings")
    let readyTitle = CopyItem.same("dictation.readyTitle", "Ready to dictate")
    let tryHere = CopyItem.same("dictation.tryHere", "Try it here")
    let tryDictation = CopyItem.same("dictation.tryDictation", "Try dictation")

    // Microphone
    let microphone = CopyItem.same("dictation.microphone", "Microphone")
    let microphoneHint = CopyItem.changed(
        "dictation.microphoneHint",
        "Mac default follows the microphone you choose in System Settings, Sound.",
        windows: "Windows default follows the microphone you choose in Windows sound settings.",
        because: .macSystemFeature)
    let soundSettings = CopyItem.same("dictation.soundSettings", "Sound settings")
    let macDefault = CopyItem.changed(
        "dictation.macDefault", "Mac default", windows: "Windows default", because: .macSystemFeature)
    let macDefaultNoMicrophone = CopyItem.changed(
        "dictation.macDefaultNoMicrophone",
        "Mac default (no microphone found)",
        windows: "Windows default (no microphone found)",
        because: .macSystemFeature)
    let macDefaultNamed = CopyItem.changed(
        "dictation.macDefaultNamed",
        "Mac default: {name}",
        windows: "Windows default: {name}",
        because: .macSystemFeature)
    let microphoneUnavailable = CopyItem.same("dictation.microphoneUnavailable", "Unavailable: {name}")
    let savedMicrophone = CopyItem.same("dictation.savedMicrophone", "Saved microphone")

    // Shortcuts
    let shortcuts = CopyItem.same("dictation.shortcuts", "Shortcuts")
    let shortcut = CopyItem.same("dictation.shortcut", "Dictation shortcut")
    let shortcutHelp = CopyItem.same("dictation.shortcutHelp", "Types your words with AI cleanup.")
    let recorderPlaceholder = CopyItem.changed(
        "dictation.recorderPlaceholder",
        "Choose Change, then press a key",
        windows: "Choose Change, then press a key, two keys or a mouse button",
        because: .macKeys)
    let change = CopyItem.same("dictation.change", "Change")
    let remove = CopyItem.same("dictation.remove", "Remove")
    let plainShortcut = CopyItem.same("dictation.plainShortcut", "Shortcut without AI cleanup")
    let plainShortcutHelp = CopyItem.same(
        "dictation.plainShortcutHelp", "Optional. Types exactly what Scribe hears, with your dictionary and snippets.")
    let none = CopyItem.same("dictation.none", "None")
    let restoreDefaults = CopyItem.same("dictation.restoreDefaults", "Restore default shortcuts")
    let modeHold = CopyItem.same("dictation.modeHold", "Press and hold")
    let modeToggle = CopyItem.same("dictation.modeToggle", "Press to start and stop")
    let holdHint = CopyItem.same(
        "dictation.holdHint", "Click in any text box, hold {shortcut} and speak. Let go to type.")
    let toggleHint = CopyItem.same(
        "dictation.toggleHint", "Click in any text box, press {shortcut} and speak. Press it again to type.")
    let capsLockHint = CopyItem.added(
        "dictation.capsLockHint",
        "Press Caps Lock to start and again to stop. Its light is on while Scribe listens.",
        because: .macKeys)
    let capsLockResync = CopyItem.added(
        "dictation.capsLockResync",
        "If the light is on and Scribe isn't listening, tap Caps Lock once.",
        because: .macKeys)

    // Stop when I stop talking
    let silenceStop = CopyItem.same("dictation.silenceStop", "Stop when I stop talking")
    let silenceStopHint = CopyItem.same(
        "dictation.silenceStopHint", "Scribe stops after a few seconds of silence. A noisy room can stop it early.")
    let silenceStopDisabled = CopyItem.same(
        "dictation.silenceStopDisabled", "Available when a shortcut is set to Press to start and stop.")
    let setShortcutFirst = CopyItem.same("dictation.setShortcutFirst", "Set a shortcut first.")

    // Typing
    let typing = CopyItem.same("dictation.typing", "Typing")
    let addSpace = CopyItem.same("dictation.addSpace", "Add a space after each dictation")
    let addSpaceHint = CopyItem.same(
        "dictation.addSpaceHint",
        "Makes back-to-back dictations flow. Turn it off if an app needs text without a trailing space.")
    let moreTyping = CopyItem.same("dictation.moreTyping", "More typing options are on the Advanced page.")

    // Recording indicator
    let indicator = CopyItem.same("dictation.indicator", "Recording indicator")
    let showIndicator = CopyItem.same("dictation.showIndicator", "Show the recording indicator")
    let showIndicatorHint = CopyItem.same(
        "dictation.showIndicatorHint", "A small bar with a live sound level appears while Scribe listens.")
    let whereItAppears = CopyItem.same("dictation.whereItAppears", "Where it appears")
    let whereItAppearsHint = CopyItem.same(
        "dictation.whereItAppearsHint", "Choose a spot, then preview it on your screen.")
    let previewOnScreen = CopyItem.same("dictation.previewOnScreen", "Preview on screen")
    let selectedPosition = CopyItem.same("dictation.selectedPosition", "Selected: {position}")
    let topLeft = CopyItem.same("dictation.topLeft", "Top left")
    let topCenter = CopyItem.same("dictation.topCenter", "Top center")
    let topRight = CopyItem.same("dictation.topRight", "Top right")
    let middleLeft = CopyItem.same("dictation.middleLeft", "Middle left")
    let center = CopyItem.same("dictation.center", "Center")
    let middleRight = CopyItem.same("dictation.middleRight", "Middle right")
    let bottomLeft = CopyItem.same("dictation.bottomLeft", "Bottom left")
    let bottomCenter = CopyItem.same("dictation.bottomCenter", "Bottom center (default)")
    let bottomRight = CopyItem.same("dictation.bottomRight", "Bottom right")

    // Startup
    let startup = CopyItem.same("dictation.startup", "Startup")
    let openAtLogin = CopyItem.changed(
        "dictation.openAtLogin",
        "Open Scribe at login",
        windows: "Start Scribe when you sign in to Windows",
        because: .macSystemFeature)
    let loginChecking = CopyItem.changed(
        "dictation.loginChecking",
        "Checking login items...",
        windows: "Checking Windows startup settings...",
        because: .macSystemFeature)
    let loginSettings = CopyItem.changed(
        "dictation.loginSettings",
        "Login Items settings",
        windows: "Windows startup settings",
        because: .macSystemFeature)
    let appliesWhenSaved = CopyItem.changed(
        "dictation.appliesWhenSaved", "Applies when you save.", windows: "Applies immediately.", because: .stagedSave)

    // Permissions (the Mac asks for three; Windows asks for none)
    let permissions = CopyItem.added("dictation.permissions", "Permissions")
    let permissionsHint = CopyItem.added(
        "dictation.permissionsHint", "macOS asks you to allow each of these once. Scribe only uses them to dictate.")
    let microphoneAccess = CopyItem.same("dictation.microphoneAccess", "Microphone")
    let microphoneAccessHint = CopyItem.added(
        "dictation.microphoneAccessHint", "Lets Scribe hear you while you dictate.")
    let accessibilityAccess = CopyItem.added("dictation.accessibilityAccess", "Accessibility")
    let accessibilityAccessHint = CopyItem.added(
        "dictation.accessibilityAccessHint", "Lets Scribe type into the app you are using.")
    let inputMonitoringAccess = CopyItem.added("dictation.inputMonitoringAccess", "Input Monitoring")
    let inputMonitoringAccessHint = CopyItem.added(
        "dictation.inputMonitoringAccessHint", "Lets Scribe notice your shortcut while another app is in front.")
    let permissionAllowed = CopyItem.added("dictation.permissionAllowed", "Allowed")
    let permissionNotAllowed = CopyItem.added("dictation.permissionNotAllowed", "Not allowed yet")
    let openSystemSettings = CopyItem.added("dictation.openSystemSettings", "Open System Settings")
}
