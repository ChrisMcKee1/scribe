import Foundation

/// The Try dictation page. Windows: `SettingsWindow.xaml` lines 2346 to 2504, `SettingsWindow.TryDictation.cs`,
/// `TryDictationRules.cs`, `UserFacingError.cs` (the summary and stage lines).
struct TryDictationCopy: CopyCatalog {
    let prefix = "tryDictation"

    let subtitle = CopyItem.same("tryDictation.subtitle", "Check that dictation works, and see what Scribe changed.")
    let saveNow = CopyItem.same("tryDictation.saveNow", "Save now")
    let tryIt = CopyItem.same("tryDictation.tryIt", "Try it")
    let boxName = CopyItem.same("tryDictation.boxName", "Try dictation box")
    let clear = CopyItem.same("tryDictation.clear", "Clear")
    let holdHint = CopyItem.same(
        "tryDictation.holdHint", "Click in the box, hold {shortcut} and speak. Let go to finish.")
    let toggleHint = CopyItem.same(
        "tryDictation.toggleHint", "Click in the box, press {shortcut} and speak. Press it again to finish.")
    let plainHoldHint = CopyItem.same("tryDictation.plainHoldHint", "Hold {shortcut} to try it without AI cleanup.")
    let plainToggleHint = CopyItem.same(
        "tryDictation.plainToggleHint", "Press {shortcut} to try it without AI cleanup.")
    let sampleDefault = CopyItem.changed(
        "tryDictation.sampleDefault",
        "Not sure what to say? Try: \"This is a test of Scribe on my Mac.\"",
        windows: "Not sure what to say? Try: \"This is a test of Scribe on my PC.\"",
        because: .thisMac)
    let sampleWithTerm = CopyItem.same(
        "tryDictation.sampleWithTerm", "Or try: \"Please book a meeting about {spoken}.\"")

    let result = CopyItem.same("tryDictation.result", "Result")
    let resultEmpty = CopyItem.same(
        "tryDictation.resultEmpty", "Your result appears here after you dictate into the box.")
    let openSoundSettings = CopyItem.changed(
        "tryDictation.openSoundSettings",
        "Open Sound settings",
        windows: "Open sound settings",
        because: .macSystemFeature)
    let openAiCleanup = CopyItem.same("tryDictation.openAiCleanup", "Open AI cleanup")
    let heard = CopyItem.same("tryDictation.heard", "What Scribe heard")
    let typed = CopyItem.same("tryDictation.typed", "What Scribe typed")
    let changesNone = CopyItem.same("tryDictation.changesNone", "No changes.")
    let changesTitle = CopyItem.same("tryDictation.changesTitle", "Changes")
    let changesTitleCounted = CopyItem.same("tryDictation.changesTitleCounted", "Changes ({count})")
    let aiCleanupRewrote = CopyItem.same("tryDictation.aiCleanupRewrote", "AI cleanup rewrote the text.")
    let changeSource = CopyItem.same("tryDictation.changeSource", "your dictionary or a word pack")
    let snippetSource = CopyItem.same("tryDictation.snippetSource", "snippet")

    let timing = CopyItem.same("tryDictation.timing", "Timing details")
    let stageRecording = CopyItem.same("tryDictation.stageRecording", "Recording")
    let stageTrimming = CopyItem.same("tryDictation.stageTrimming", "Trimming silence")
    let stageRecognition = CopyItem.same("tryDictation.stageRecognition", "Speech recognition")
    let stageCleanup = CopyItem.same("tryDictation.stageCleanup", "AI cleanup")
    let stageDictionary = CopyItem.same("tryDictation.stageDictionary", "Dictionary and snippets")
    let stageTyping = CopyItem.same("tryDictation.stageTyping", "Typing")
    let stageTotal = CopyItem.same("tryDictation.stageTotal", "Processing, all steps")

    let stateOffInSettings = CopyItem.same("tryDictation.stateOffInSettings", "Off in settings")
    let stateNotReached = CopyItem.same("tryDictation.stateNotReached", "Not reached")
    let stateCleaned = CopyItem.same("tryDictation.stateCleaned", "Cleaned")
    let stateNoChangesNeeded = CopyItem.same("tryDictation.stateNoChangesNeeded", "Ran, no changes needed")
    let stateDidNotFinish = CopyItem.same("tryDictation.stateDidNotFinish", "Didn't finish")
    let stateNotReady = CopyItem.same("tryDictation.stateNotReady", "Not ready")
    let stateSkipped = CopyItem.same("tryDictation.stateSkipped", "Skipped")
    let stateTypingFailed = CopyItem.same("tryDictation.stateTypingFailed", "Typing failed")
    let stateTrimmingUnavailable = CopyItem.same(
        "tryDictation.stateTrimmingUnavailable", "Off, silence trimming isn't available")
    let methodKeystrokes = CopyItem.same("tryDictation.methodKeystrokes", "Typed as keystrokes")
    let methodPasted = CopyItem.same("tryDictation.methodPasted", "Pasted")
    let methodDirect = CopyItem.same("tryDictation.methodDirect", "Inserted directly")

    let summaryDone = CopyItem.same("tryDictation.summaryDone", "Done. Processing took {seconds} seconds. {cleanup}")
    let summaryDoneCleanupOff = CopyItem.same(
        "tryDictation.summaryDoneCleanupOff", "Done. Processing took {seconds} seconds. AI cleanup: off.")
    let summaryCleanupFailed = CopyItem.same(
        "tryDictation.summaryCleanupFailed",
        "Done. AI cleanup didn't finish, so Scribe typed what it heard. Try again, or turn AI cleanup off.")
    let summaryCleanupNotReady = CopyItem.same(
        "tryDictation.summaryCleanupNotReady", "Done. AI cleanup wasn't ready, so Scribe typed what it heard. {reason}")
    let summaryStopped = CopyItem.same("tryDictation.summaryStopped", "Stopped at {stage}. {reason}")
    let cleanupNotReadyFallback = CopyItem.same("tryDictation.cleanupNotReadyFallback", "Open AI cleanup to check it.")
    let emptyAfterRules = CopyItem.same(
        "tryDictation.emptyAfterRules",
        "Your dictionary or snippets removed all of the text, so there was nothing to type.")
    let reasonRecording = CopyItem.same(
        "tryDictation.reasonRecording", "Check your microphone on the Dictation page, then try again.")
    let reasonTrimming = CopyItem.same(
        "tryDictation.reasonTrimming", "Scribe couldn't finish trimming silence. Try again.")
    let reasonRecognition = CopyItem.same(
        "tryDictation.reasonRecognition", "Scribe couldn't turn the recording into text. Try again.")
    let reasonCleanup = CopyItem.same(
        "tryDictation.reasonCleanup", "Scribe couldn't finish AI cleanup. Try again, or turn AI cleanup off.")
    let reasonDictionary = CopyItem.same(
        "tryDictation.reasonDictionary", "Scribe couldn't apply your dictionary and snippets. Try again.")
    let reasonTyping = CopyItem.same(
        "tryDictation.reasonTyping", "Scribe couldn't type the text. Click in the box, then try again.")
    let noSpeechHeard = CopyItem.same(
        "tryDictation.noSpeechHeard",
        "Scribe didn't hear any speech. Check your microphone on the Dictation page, then try again.")
    let couldNotRecord = CopyItem.same(
        "tryDictation.couldNotRecord",
        "Scribe couldn't record from your microphone. Check your microphone on the Dictation page, then try again.")
}
