import Foundation

/// Everything that can go wrong around a dictation, in the words a person reads: a title, a body with one next
/// step, the line the recording indicator shows, and what clicking the notice does. A port of `DictationProblemText`
/// with the Mac's permissions, speech runtime and local AI apps added.
enum DictationProblem: Equatable, Sendable {
    case tooQuick
    case noAudio
    case noAudioFromDevice
    case onlySilence
    case onlySilenceFromDevice
    case microphoneMuted
    case microphoneUnavailable
    case microphoneDisconnected
    case microphoneAccessDenied
    case accessibilityDenied
    case inputMonitoringDenied
    case durationLimit
    case nothingRecognized
    case focusChanged
    case typingIncomplete
    case foundryLocalMissing
    case whisperMissing
    case whisperModelMissing
    case recognitionFailed
    case modelLoadFailed
    case fallbackMicrophone
    case localAppNotRunning
    case localModelMissing
    case azureSignInNeeded
}

/// How serious a notice is, which decides its icon and whether it interrupts.
enum DictationProblemSeverity: Equatable, Sendable {
    case warning
    case error
    case recordingWarning
}

/// What choosing the notice does.
enum DictationProblemAction: Equatable, Sendable {
    case none
    case copyLastDictation
    case openSettings(SettingsPage)
    case openSystemSettings(SystemSettingsPane)
    case openSetupHelp(SetupHelp)
}

/// The setup instructions a notice can open.
enum SetupHelp: Equatable, Sendable {
    case speechRuntime
}

/// The macOS Privacy & Security panes a notice can open, one for each PrivacyPane the app already links to.
enum SystemSettingsPane: CaseIterable, Equatable, Sendable {
    case microphone
    case accessibility
    case inputMonitoring

    var privacyPane: PrivacyPane {
        switch self {
        case .microphone: return .microphone
        case .accessibility: return .accessibility
        case .inputMonitoring: return .inputMonitoring
        }
    }
}

/// The values a notice's text can need.
struct DictationProblemContext: Equatable, Sendable {
    var shortcut: String?
    var toggle = false
    var device: String?
    var chosenDevice: String?
    var usedDevice: String?
    var limitSeconds = 600
    var app: String?
    var locale: Locale = .current
}

struct DictationProblemNotice: Equatable, Sendable {
    let title: String
    let body: String
    let pillLine: String
    let severity: DictationProblemSeverity
    let action: DictationProblemAction
}

extension DictationProblem {
    /// The notice for this problem. Quoted device names are shortened to 60 characters, as on Windows.
    func notice(_ context: DictationProblemContext = DictationProblemContext()) -> DictationProblemNotice {
        let copy = SettingsCopy.problem
        let indicator = SettingsCopy.indicator
        let key = Self.clean(context.shortcut) ?? SettingsCopy.menuBar.yourShortcut.render()
        let device = Self.quote(Self.shorten(Self.clean(context.device) ?? "your microphone", 60))
        let duration = SettingsFormat.duration(seconds: context.limitSeconds, locale: context.locale)
        let app = Self.clean(context.app) ?? "The AI app"
        func make(
            _ title: CopyItem,
            _ body: CopyItem,
            _ values: [String: String] = [:],
            pill: String,
            _ severity: DictationProblemSeverity = .warning,
            _ action: DictationProblemAction = .none
        ) -> DictationProblemNotice {
            DictationProblemNotice(
                title: title.render(values), body: body.render(values), pillLine: pill, severity: severity,
                action: action)
        }
        switch self {
        case .tooQuick:
            let pill = context.toggle ? copy.pillToggle : copy.pillHold
            return make(
                copy.tooQuickTitle,
                context.toggle ? copy.tooQuickToggle : copy.tooQuickHold,
                ["key": key],
                pill: pill.render())
        case .noAudio:
            return make(
                copy.noSoundTitle,
                copy.noSoundBody,
                pill: indicator.microphoneStep.render(),
                .warning,
                .openSettings(.dictation))
        case .noAudioFromDevice:
            return make(
                copy.noSoundTitle,
                copy.noSoundDeviceBody,
                ["device": device],
                pill: indicator.otherMicrophone.render(),
                .warning,
                .openSettings(.dictation))
        case .onlySilence:
            return make(copy.silenceTitle, copy.silenceBody, pill: copy.pillMaybeMuted.render())
        case .onlySilenceFromDevice:
            return make(
                copy.silenceTitle, copy.silenceDeviceBody, ["device": device], pill: copy.pillMaybeMuted.render())
        case .microphoneMuted:
            return make(copy.mutedTitle, copy.mutedBody, pill: copy.pillMuted.render(), .recordingWarning)
        case .microphoneUnavailable:
            return make(
                copy.unavailableTitle,
                copy.unavailableBody,
                pill: copy.pillUnavailable.render(),
                .error,
                .openSettings(.dictation))
        case .microphoneDisconnected:
            return make(
                copy.disconnectedTitle,
                copy.disconnectedBody,
                pill: copy.pillUnavailable.render(),
                .warning,
                .openSettings(.dictation))
        case .microphoneAccessDenied:
            return make(
                copy.microphoneAccessTitle,
                copy.microphoneAccessBody,
                pill: indicator.allowMicrophone.render(),
                .error,
                .openSystemSettings(.microphone))
        case .accessibilityDenied:
            return make(
                copy.accessibilityTitle,
                copy.accessibilityBody,
                pill: indicator.allowAccessibility.render(),
                .error,
                .openSystemSettings(.accessibility))
        case .inputMonitoringDenied:
            return make(
                copy.inputMonitoringTitle,
                copy.inputMonitoringBody,
                pill: indicator.tryAgain.render(),
                .error,
                .openSystemSettings(.inputMonitoring))
        case .durationLimit:
            return make(
                copy.limitTitle,
                copy.limitBody,
                ["duration": duration],
                pill: copy.pillStopped.render(["duration": duration]))
        case .nothingRecognized:
            return make(copy.noWordsTitle, copy.noWordsBody, pill: indicator.noWords.render())
        case .focusChanged:
            return make(
                copy.typingTitle, copy.focusChangedBody, pill: indicator.copyStep.render(), .error, .copyLastDictation)
        case .typingIncomplete:
            return make(
                copy.typingTitle, copy.incompleteBody, pill: indicator.copyStep.render(), .error, .copyLastDictation)
        case .foundryLocalMissing:
            return make(
                copy.noSpeechModelTitle,
                copy.noFoundryBody,
                pill: indicator.installFoundry.render(),
                .warning,
                .openSetupHelp(.speechRuntime))
        case .whisperMissing:
            return make(
                copy.noSpeechModelTitle,
                copy.noWhisperBody,
                pill: indicator.noSpeechModel.render(),
                .warning,
                .openSetupHelp(.speechRuntime))
        case .whisperModelMissing:
            return make(
                copy.noSpeechModelTitle,
                copy.noWhisperModelBody,
                pill: indicator.noSpeechModel.render(),
                .warning,
                .openSetupHelp(.speechRuntime))
        case .recognitionFailed:
            return make(
                copy.recognitionFailedTitle,
                copy.recognitionFailedBody,
                pill: indicator.wentWrong.render(),
                .error,
                .openSettings(.diagnostics))
        case .modelLoadFailed:
            return make(
                copy.modelLoadTitle,
                copy.modelLoadBody,
                pill: copy.modelLoadTitle.render(),
                .warning,
                .openSettings(.diagnostics))
        case .fallbackMicrophone:
            let values = [
                "chosen": Self.quote(Self.shorten(Self.clean(context.chosenDevice) ?? "Your microphone", 60)),
                "used": Self.quote(Self.shorten(Self.clean(context.usedDevice) ?? "the Mac default", 60)),
            ]
            return make(
                copy.fallbackTitle,
                copy.fallbackBody,
                values,
                pill: indicator.defaultMicrophone.render(),
                .recordingWarning,
                .openSettings(.dictation))
        case .localAppNotRunning:
            return make(
                copy.appNotRunningTitle,
                copy.appNotRunningBody,
                ["app": app],
                pill: indicator.cleanupStep.render(),
                .warning,
                .openSettings(.aiCleanup))
        case .localModelMissing:
            return make(
                copy.modelMissingTitle,
                copy.modelMissingBody,
                ["app": app],
                pill: indicator.cleanupStep.render(),
                .warning,
                .openSettings(.aiCleanup))
        case .azureSignInNeeded:
            return make(
                copy.signInTitle,
                copy.signInBody,
                pill: indicator.cleanupStep.render(),
                .warning,
                .openSettings(.aiCleanup))
        }
    }

    /// "Couldn't {operation}. {why} {next}": what happened, why in plain words, then the one next step.
    static func couldnt(_ operation: String, why: String, next: String) -> String {
        SettingsCopy.problem.couldnt.render(["operation": operation, "why": why, "next": next])
            .replacingOccurrences(of: "  ", with: " ")
            .trimmingCharacters(in: .whitespaces)
    }

    private static func clean(_ value: String?) -> String? {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else { return nil }
        return trimmed
    }

    private static func shorten(_ value: String, _ limit: Int) -> String {
        value.count <= limit ? value : String(value.prefix(limit - 1)) + "\u{2026}"
    }

    private static func quote(_ value: String) -> String {
        "\u{201C}" + value + "\u{201D}"
    }
}
