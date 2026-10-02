import Foundation

/// The "plain words, one next step" notices for everything that can go wrong while dictating. Windows:
/// `DictationProblem.cs` (`DictationProblemText`), `UserFacingError.cs`. The Mac adds the three permissions, the
/// speech runtime and the local AI apps, which are not Windows problems.
struct ProblemCopy: CopyCatalog {
    let prefix = "problem"

    let tooQuickTitle = CopyItem.same("problem.tooQuickTitle", "Nothing recorded")
    let tooQuickToggle = CopyItem.same(
        "problem.tooQuickToggle", "That was too quick. Press {key}, speak, then press it again.")
    let tooQuickHold = CopyItem.same(
        "problem.tooQuickHold", "That was too quick. Hold {key} while you speak, then let go.")
    let noSoundTitle = CopyItem.same("problem.noSoundTitle", "No sound recorded")
    let noSoundBody = CopyItem.changed(
        "problem.noSoundBody",
        "Scribe didn't get any sound from your microphone. Check that it's connected, or choose another in Settings, "
            + "Dictation.",
        windows: "Scribe didn't get any sound from your microphone. Check that it's connected, or choose another from "
            + "the Microphone menu.",
        because: .macSystemFeature)
    let noSoundDeviceBody = CopyItem.changed(
        "problem.noSoundDeviceBody",
        "Scribe didn't get any sound from {device}. Choose another microphone in Settings, Dictation.",
        windows: "Scribe didn't get any sound from {device}. Choose another microphone from the Microphone menu.",
        because: .macSystemFeature)
    let silenceTitle = CopyItem.same("problem.silenceTitle", "Only silence recorded")
    let silenceBody = CopyItem.same("problem.silenceBody", "Your microphone may be muted. Unmute it and try again.")
    let silenceDeviceBody = CopyItem.same(
        "problem.silenceDeviceBody", "{device} may be muted. Unmute it and try again.")
    let mutedTitle = CopyItem.same("problem.mutedTitle", "Your microphone is muted")
    let mutedBody = CopyItem.same("problem.mutedBody", "Unmute it to keep dictating. Scribe is still recording.")
    let unavailableTitle = CopyItem.same("problem.unavailableTitle", "Couldn't start recording")
    let unavailableBody = CopyItem.changed(
        "problem.unavailableBody",
        "Scribe couldn't open your microphone. Check that it's connected, or choose another in Settings, Dictation.",
        windows: "Scribe couldn't open your microphone. Check that it's connected, or choose another from the "
            + "Microphone menu.",
        because: .macSystemFeature)
    let disconnectedTitle = CopyItem.same("problem.disconnectedTitle", "Microphone disconnected")
    let disconnectedBody = CopyItem.same(
        "problem.disconnectedBody",
        "Your microphone stopped during the dictation. Check that it's connected, then try again.")
    let limitTitle = CopyItem.changed(
        "problem.limitTitle",
        "Dictation stopped at {duration}",
        windows: "Dictation stopped at {minutes} minutes",
        because: .macTemplate)
    let limitBody = CopyItem.changed(
        "problem.limitBody",
        "Scribe stops recording after {duration} and types what it heard. You can change this in Settings, Advanced.",
        windows: "Scribe stops recording after {minutes} minutes and types what it heard. You can change this in "
            + "Settings, Advanced.",
        because: .macTemplate)
    let noWordsTitle = CopyItem.same("problem.noWordsTitle", "No words recognized")
    let noWordsBody = CopyItem.same(
        "problem.noWordsBody", "Scribe didn't catch any words. Try again, a little closer to the microphone.")
    let typingTitle = CopyItem.same("problem.typingTitle", "Couldn't type your dictation")
    let focusChangedBody = CopyItem.changed(
        "problem.focusChangedBody",
        "The window changed before Scribe finished typing. Click the Scribe icon in the menu bar and choose Copy last "
            + "dictation, then paste it.",
        windows: "The window changed before Scribe finished typing. Right-click the Scribe icon and choose Copy last "
            + "dictation, then paste it.",
        because: .macSystemFeature)
    let incompleteBody = CopyItem.changed(
        "problem.incompleteBody",
        "This app didn't accept all of the text. Click the Scribe icon in the menu bar and choose Copy last "
            + "dictation, then paste it.",
        windows: "This app didn't accept all of the text. Right-click the Scribe icon and choose Copy last dictation, "
            + "then paste it.",
        because: .macSystemFeature)
    let recognitionFailedTitle = CopyItem.same("problem.recognitionFailedTitle", "Dictation didn't finish")
    let recognitionFailedBody = CopyItem.added(
        "problem.recognitionFailedBody",
        "Something went wrong while Scribe turned your speech into text. Try again. If it keeps happening, copy the "
            + "support details in Settings, Diagnostics.",
        because: .macBehaviour)
    let modelLoadTitle = CopyItem.same("problem.modelLoadTitle", "Speech model didn't load")
    let modelLoadBody = CopyItem.added(
        "problem.modelLoadBody",
        "Scribe tries again when you dictate. If dictation doesn't work, copy the support details in Settings, "
            + "Diagnostics and report the problem.",
        because: .macBehaviour)
    let fallbackTitle = CopyItem.same("problem.fallbackTitle", "Using another microphone")
    let fallbackBody = CopyItem.added(
        "problem.fallbackBody",
        "{chosen} isn't available, so Scribe is using {used}. Choose a microphone in Settings, Dictation to change it.",
        because: .macBehaviour)
    let genericTitle = CopyItem.same("problem.genericTitle", "Dictation didn't finish")
    let genericBody = CopyItem.same("problem.genericBody", "Try again.")

    let noSpeechModelTitle = CopyItem.same("problem.noSpeechModelTitle", "No speech model")
    let noFoundryBody = CopyItem.added(
        "problem.noFoundryBody",
        "Scribe couldn't find Foundry Local. In Terminal, run brew install microsoft/foundrylocal/foundrylocal, then "
            + "try again.",
        because: .foundryLocal)
    let noWhisperBody = CopyItem.added(
        "problem.noWhisperBody",
        "Scribe can't find whisper-cli, the speech tool it was set up to use. Open the setup help to fix it, or "
            + "install Foundry Local instead.",
        because: .macBehaviour)
    let noWhisperModelBody = CopyItem.added(
        "problem.noWhisperModelBody",
        "Scribe can't find the Whisper model it was set up to use. Open the setup help to fix it, or install Foundry "
            + "Local instead.",
        because: .macBehaviour)
    let microphoneAccessTitle = CopyItem.added(
        "problem.microphoneAccessTitle", "Scribe can't use the microphone", because: .macBehaviour)
    let microphoneAccessBody = CopyItem.added(
        "problem.microphoneAccessBody",
        "Allow Scribe under Microphone in System Settings, Privacy & Security, then dictate again.",
        because: .macBehaviour)
    let accessibilityTitle = CopyItem.added(
        "problem.accessibilityTitle", "Scribe can't type into other apps", because: .macBehaviour)
    let accessibilityBody = CopyItem.added(
        "problem.accessibilityBody",
        "Allow Scribe under Accessibility in System Settings, Privacy & Security. Your dictation is kept: click the "
            + "Scribe icon in the menu bar and choose Copy last dictation.",
        because: .macBehaviour)
    let inputMonitoringTitle = CopyItem.added(
        "problem.inputMonitoringTitle", "Scribe can't hear your shortcut", because: .macBehaviour)
    let inputMonitoringBody = CopyItem.added(
        "problem.inputMonitoringBody",
        "Allow Scribe under Input Monitoring in System Settings, Privacy & Security, then try again.",
        because: .macBehaviour)
    let appNotRunningTitle = CopyItem.added("problem.appNotRunningTitle", "{app} isn't running", because: .macBehaviour)
    let appNotRunningBody = CopyItem.added(
        "problem.appNotRunningBody",
        "Open {app} and make sure it is running, then try again. Until it is, Scribe types what it hears.",
        because: .macBehaviour)
    let modelMissingTitle = CopyItem.added(
        "problem.modelMissingTitle", "{app} doesn't have that model", because: .macBehaviour)
    let modelMissingBody = CopyItem.added(
        "problem.modelMissingBody",
        "Choose a model {app} has, in Settings, AI cleanup. Until then, Scribe types what it hears.",
        because: .macBehaviour)
    let signInTitle = CopyItem.added("problem.signInTitle", "Not signed in to Azure", because: .macBehaviour)
    let signInBody = CopyItem.changed(
        "problem.signInBody",
        "Open Settings, AI cleanup and sign in to Azure. Until you do, Scribe types what it heard.",
        windows: "Not signed in to Azure. Until you sign in, Scribe types what it heard.",
        because: .macTemplate)

    // The one-line format of Try dictation and the AI cleanup status: what happened, why, then the next step.
    let couldnt = CopyItem.added("problem.couldnt", "Couldn't {operation}. {why} {next}", because: .macTemplate)
    let pillHold = CopyItem.same("problem.pillHold", "Hold the shortcut to speak")
    let pillToggle = CopyItem.same("problem.pillToggle", "Press, speak, press again")
    let pillMuted = CopyItem.same("problem.pillMuted", "Microphone muted")
    let pillMaybeMuted = CopyItem.same("problem.pillMaybeMuted", "Microphone may be muted")
    let pillUnavailable = CopyItem.same("problem.pillUnavailable", "Microphone unavailable")
    let pillStopped = CopyItem.changed(
        "problem.pillStopped", "Stopped at {duration}", windows: "Stopped at {minutes} minutes", because: .macTemplate)
}
