import Foundation

/// What the shortcut recorder says. Windows: `HotkeyCaptureSession.cs` (`Prompt`, the rules and the warnings) and
/// `PhaseThreeRules.cs` (the per-key hints). The Mac records one key, so the mouse button and two-key texts are
/// listed in `SettingsCopyOmissions`.
struct ShortcutCopy: CopyCatalog {
    let prefix = "shortcut"

    let prompt = CopyItem.changed(
        "shortcut.prompt", "Press a key...", windows: "Press a key, two keys or a mouse button...", because: .macKeys)
    let rule = CopyItem.changed(
        "shortcut.rule",
        "A shortcut is one key, such as Caps Lock, Right Option or F13.",
        windows: "A shortcut is one or two keys or mouse buttons, or one key or button with Ctrl, Alt or Shift.",
        because: .macKeys)
    let cancel = CopyItem.added("shortcut.cancel", "Press Escape to cancel.", because: .macKeys)
    let refusedPrintable = CopyItem.added(
        "shortcut.refusedPrintable",
        "{key} can't be a shortcut on its own: you type it all the time. Press a key like Caps Lock, Right Option or "
            + "F13.",
        because: .macKeys)
    let refusedEditing = CopyItem.added(
        "shortcut.refusedEditing",
        "{key} can't be a shortcut: every app needs it. Press a key like Caps Lock, Right Option or F13.",
        because: .macKeys)
    let refusedGlobe = CopyItem.added(
        "shortcut.refusedGlobe",
        "The Globe key can't be a shortcut: macOS uses it for emoji and dictation.",
        because: .macKeys)
    let warnCommand = CopyItem.added(
        "shortcut.warnCommand",
        "Almost every keyboard shortcut uses Command, so pressing {key} by accident can start a dictation.",
        because: .macKeys)
    let warnShift = CopyItem.added(
        "shortcut.warnShift",
        "Pressing Shift five times can turn on Sticky Keys. Try Right Option instead.",
        because: .macKeys)
    let warnFunction = CopyItem.added(
        "shortcut.warnFunction",
        "On many Mac keyboards, {key} controls brightness, volume or media unless you hold Fn.",
        because: .macKeys)
    let infoCapsLock = CopyItem.added(
        "shortcut.infoCapsLock",
        "Caps Lock keeps its light. Scribe only listens to it, so tap it once if the light and Scribe disagree.",
        because: .macKeys)
    let infoModifier = CopyItem.added(
        "shortcut.infoModifier",
        "Scribe only listens to {key}, so it still works as usual in other apps.",
        because: .macKeys)
    let permissionNeeded = CopyItem.added(
        "shortcut.permissionNeeded",
        "Allow Scribe under Input Monitoring in System Settings so it can hear {key}.",
        because: .macKeys)
    let recorded = CopyItem.added("shortcut.recorded", "Shortcut set to {key}.", because: .macKeys)
    let removed = CopyItem.added("shortcut.removed", "Shortcut removed.", because: .macKeys)
    let sameAsOther = CopyItem.added(
        "shortcut.sameAsOther", "{key} is already the other shortcut. Press a different key.", because: .macKeys)
}
