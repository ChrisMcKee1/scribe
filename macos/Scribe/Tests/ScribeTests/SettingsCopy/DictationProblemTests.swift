import Foundation
import XCTest

@testable import Scribe

final class DictationProblemTests: XCTestCase {
    private let english = Locale(identifier: "en_US")

    private func context(toggle: Bool = false) -> DictationProblemContext {
        DictationProblemContext(shortcut: "Caps Lock", toggle: toggle, device: "Studio Mic", locale: english)
    }

    func testEveryProblemHasAPlainTitleABodyAPillLineAndNoLeftoverPlaceholder() {
        let all: [DictationProblem] = [
            .tooQuick, .noAudio, .noAudioFromDevice, .onlySilence, .onlySilenceFromDevice, .microphoneMuted,
            .microphoneUnavailable, .microphoneDisconnected, .microphoneAccessDenied, .accessibilityDenied,
            .inputMonitoringDenied, .durationLimit, .nothingRecognized, .focusChanged, .typingIncomplete,
            .foundryLocalMissing, .whisperMissing, .whisperModelMissing, .recognitionFailed, .modelLoadFailed,
            .fallbackMicrophone, .localAppNotRunning, .localModelMissing, .azureSignInNeeded,
        ]
        for problem in all {
            let notice = problem.notice(context())
            for text in [notice.title, notice.body, notice.pillLine] {
                XCTAssertFalse(text.isEmpty, "\(problem)")
                XCTAssertFalse(text.contains("{"), "\(problem) keeps a placeholder: \(text)")
                XCTAssertTrue(CopyGlossary.violations(in: text).isEmpty, "\(problem) uses a retired word: \(text)")
            }
            XCTAssertLessThanOrEqual(notice.pillLine.count, 40, "\(problem) pill line is long")
        }
    }

    func testTheWindowsProblemsKeepTheirWindowsWording() {
        let quick = DictationProblem.tooQuick.notice(context())
        XCTAssertEqual(quick.title, "Nothing recorded")
        XCTAssertEqual(quick.body, "That was too quick. Hold Caps Lock while you speak, then let go.")
        XCTAssertEqual(quick.pillLine, "Hold the shortcut to speak")
        let toggle = DictationProblem.tooQuick.notice(context(toggle: true))
        XCTAssertEqual(toggle.body, "That was too quick. Press Caps Lock, speak, then press it again.")
        XCTAssertEqual(toggle.pillLine, "Press, speak, press again")
        let device = DictationProblem.onlySilenceFromDevice.notice(context())
        XCTAssertEqual(device.body, "\u{201C}Studio Mic\u{201D} may be muted. Unmute it and try again.")
        XCTAssertEqual(device.pillLine, "Microphone may be muted")
    }

    func testTheDurationLimitIsWrittenInTheLocalesWords() {
        let notice = DictationProblem.durationLimit.notice(context())
        XCTAssertEqual(notice.title, "Dictation stopped at 10 minutes")
        XCTAssertEqual(notice.pillLine, "Stopped at 10 minutes")
        XCTAssertTrue(notice.body.contains("after 10 minutes"))
    }

    func testPermissionProblemsOpenTheirSystemSettingsPaneWithOneNextStep() {
        XCTAssertEqual(DictationProblem.microphoneAccessDenied.notice().action, .openSystemSettings(.microphone))
        XCTAssertEqual(DictationProblem.accessibilityDenied.notice().action, .openSystemSettings(.accessibility))
        XCTAssertEqual(DictationProblem.inputMonitoringDenied.notice().action, .openSystemSettings(.inputMonitoring))
        let microphone = DictationProblem.microphoneAccessDenied.notice()
        let allow = "Allow Scribe under Microphone in System Settings, Privacy & Security, then dictate again."
        XCTAssertEqual(microphone.body, allow)
        XCTAssertEqual(DictationProblem.focusChanged.notice().action, .copyLastDictation)
        XCTAssertEqual(DictationProblem.microphoneUnavailable.notice().severity, .error)
        XCTAssertEqual(DictationProblem.microphoneMuted.notice().severity, .recordingWarning)
    }

    func testTheSpeechRuntimeAndLocalAppProblemsNameTheAppAndTheFix() {
        let foundry = DictationProblem.foundryLocalMissing.notice()
        XCTAssertTrue(foundry.body.contains("brew install microsoft/foundrylocal/foundrylocal"))
        XCTAssertEqual(foundry.title, "No speech model")
        var ollama = DictationProblemContext()
        ollama.app = "Ollama"
        let notice = DictationProblem.localAppNotRunning.notice(ollama)
        XCTAssertEqual(notice.title, "Ollama isn't running")
        XCTAssertTrue(notice.body.hasPrefix("Open Ollama and make sure it is running"))
        XCTAssertEqual(notice.action, .openSettings(.aiCleanup))
    }

    func testLongDeviceNamesAreShortenedAndFallbackNamesBothMicrophones() {
        var long = DictationProblemContext()
        long.device = String(repeating: "A", count: 100)
        let notice = DictationProblem.noAudioFromDevice.notice(long)
        XCTAssertTrue(notice.body.contains(String(repeating: "A", count: 59) + "\u{2026}"))
        var fallback = DictationProblemContext()
        fallback.chosenDevice = "USB Mic"
        fallback.usedDevice = "MacBook Microphone"
        let expected = "\u{201C}USB Mic\u{201D} isn't available, so Scribe is using \u{201C}MacBook Microphone\u{201D}."
        XCTAssertTrue(DictationProblem.fallbackMicrophone.notice(fallback).body.hasPrefix(expected))
    }

    func testTheOneLineFormatReadsWhatHappenedWhyAndTheNextStep() {
        XCTAssertEqual(
            DictationProblem.couldnt("record", why: "The microphone is muted.", next: "Unmute it, then try again."),
            "Couldn't record. The microphone is muted. Unmute it, then try again.")
        XCTAssertEqual(DictationProblem.couldnt("record", why: "", next: "Try again."), "Couldn't record. Try again.")
    }
}
