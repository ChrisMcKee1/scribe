import Foundation
import XCTest

@testable import Scribe

/// Tests added with the review of the Settings copy foundation: each holds a sentence to the behavior it describes.
final class ReviewResolutionTests: XCTestCase {
    func testProfileOrderTextMatchesTheMatcherThatPutsTheAppItselfFirst() {
        let legacy = AppProfile(
            name: "Older", bundleIdentifiers: [], processNames: ["Slack"], writingStylePrompt: nil,
            newlineHandling: nil)
        let exact = AppProfile(
            name: "Exact", bundleIdentifiers: ["com.tinyspeck.slackmacgap"], processNames: [], writingStylePrompt: nil,
            newlineHandling: nil)
        let match = AppProfileMatcher.match(
            profiles: [legacy, exact], bundleIdentifier: "com.tinyspeck.slackmacgap", processName: "Slack")
        XCTAssertEqual(match?.name, "Exact", "a lower profile matched to the app itself must win")
        let order = SettingsCopy.appProfiles.order.render()
        XCTAssertTrue(order.hasPrefix("Profiles matched to the app itself take priority"), order)
        XCTAssertTrue(order.contains("higher in the list"))
        XCTAssertTrue(SettingsCopy.appProfiles.legacyMatch.render().contains("matches by app name"))
    }

    func testTheKeepModelTextStatesTheRetentionTheRequestsAsk() {
        let seconds = LocalModelDefaults.keepAliveMinutes * 60
        let duration = SettingsFormat.duration(seconds: seconds, locale: Locale(identifier: "en_US"))
        let text = SettingsCopy.aiCleanup.keepModel.render(["app": "Ollama", "duration": duration])
        let expected = "Scribe asks Ollama to keep the model for 10 minutes after a request. "
        XCTAssertEqual(text, expected + "Ollama decides when its memory is freed.")
        XCTAssertEqual(LocalModelDefaults.keepAliveMinutes, 10)
        let advanced = SettingsCopy.advanced.items.map { $0.render() }.joined(separator: " ")
        XCTAssertFalse(advanced.contains("Free memory when Scribe isn't used"))
    }

    func testTheWordLimitsAreTheBuildersLimits() {
        let entries = (0..<81).map { DictionaryEntry(pattern: "spoken \($0)", replacement: "Word\($0)") }
        let local = CleanupPrompt.countGlossary(entries, maxTerms: CleanupPrompt.maxGlossaryTermsLocal)
        XCTAssertEqual(local.included, 80)
        XCTAssertEqual(local.eligible, 81)
        let few = CleanupPrompt.countGlossary(Array(entries.prefix(80)), maxTerms: CleanupPrompt.maxGlossaryTermsLocal)
        XCTAssertEqual(few.included, 80)

        let filler = String(repeating: "x", count: 90)
        let long = (0..<400).map { DictionaryEntry(pattern: "spoken \($0)", replacement: filler + String($0)) }
        let lines = CleanupPrompt.glossaryLines(long, maxTerms: .max, maxCharacters: CleanupPrompt.maxGlossaryChars)
        XCTAssertLessThan(lines.count, long.count)
        XCTAssertLessThanOrEqual(lines.map(\.text.count).reduce(0, +), CleanupPrompt.maxGlossaryChars)
    }

    func testAReplacementOverTheLimitOrOverSeveralLinesIsNotVocabulary() {
        let limit = CleanupPrompt.maxGlossaryTermChars
        XCTAssertEqual(limit, 100)
        XCTAssertTrue(CleanupPrompt.isVocabularyReplacement(String(repeating: "a", count: limit)))
        XCTAssertFalse(CleanupPrompt.isVocabularyReplacement(String(repeating: "a", count: limit + 1)))
        XCTAssertFalse(CleanupPrompt.isVocabularyReplacement("Kind regards,\nA. Person"))
        XCTAssertFalse(CleanupPrompt.isVocabularyReplacement(nil))
    }

    func testTheWordPackHelpNamesTheLimitAndTheWholeVocabularyException() {
        let text = SettingsCopy.wordPacks.aiSends.render()
        XCTAssertTrue(text.contains("seems to mention, within its limit"), text)
        XCTAssertTrue(text.contains(SettingsCopy.aiCleanup.wholeVocabulary.render()), text)
        XCTAssertEqual(SettingsCopy.aiCleanup.wholeVocabulary.render(), LocalModelTuningText.wholeVocabularyTitle)
    }

    func testEverySystemSettingsPaneMapsToAPaneTheAppAlreadyOpens() {
        XCTAssertEqual(SystemSettingsPane.allCases.map(\.privacyPane), [.microphone, .accessibility, .inputMonitoring])
        for pane in SystemSettingsPane.allCases {
            XCTAssertNotNil(pane.privacyPane.settingsURL)
        }
    }

    func testNoticeActionsLeadSomewhereThatExists() {
        let microphoneProblems: [DictationProblem] = [
            .noAudio, .noAudioFromDevice, .microphoneUnavailable, .microphoneDisconnected,
        ]
        for problem in microphoneProblems {
            XCTAssertEqual(problem.notice().action, .openSettings(.dictation), "\(problem)")
        }
        XCTAssertEqual(DictationProblem.fallbackMicrophone.notice().action, .openSettings(.dictation))
        XCTAssertEqual(DictationProblem.whisperModelMissing.notice().action, .openSetupHelp(.speechRuntime))
        XCTAssertEqual(DictationProblem.foundryLocalMissing.notice().action, .openSetupHelp(.speechRuntime))
        let azure = DictationProblem.azureSignInNeeded.notice()
        XCTAssertTrue(azure.body.hasPrefix("Open Settings, AI cleanup and sign in to Azure."), azure.body)
        for item in SettingsCopy.allItems {
            let text = item.render()
            XCTAssertFalse(text.contains("Microphone menu"), "\(item.id) names a menu that is not there")
        }
    }

    func testShiftReturnTextIsConditionalOnTheTypingRoute() {
        let text = SettingsCopy.advanced.chatSafeHint.render()
        XCTAssertTrue(text.hasPrefix("When Scribe types text a key at a time"), text)
        XCTAssertTrue(text.contains("doesn't change pasted text or text inserted directly"), text)
    }

    func testHistoryAndDiagnosticsWordsFollowTheShortcutAndThePeriod() {
        XCTAssertTrue(SettingsCopy.history.emptyState(toggle: false).contains("Hold your shortcut"))
        XCTAssertEqual(SettingsCopy.history.emptyState(toggle: true), SettingsCopy.history.emptyToggle.render())
        let toggle = SettingsCopy.history.emptyToggle.render()
        XCTAssertTrue(toggle.contains("press it again") && !toggle.contains("Hold"), toggle)
        XCTAssertFalse(SettingsCopy.history.deleteAllBody.render().contains("recording"))
        let diagnostics = SettingsCopy.diagnostics
        let periods = [
            (diagnostics.periodDay, "24 hours"),
            (diagnostics.periodWeek, "7 days"),
            (diagnostics.periodMonth, "30 days"),
        ]
        for (item, expected) in periods {
            let text = diagnostics.noRuns.render(["period": item.render()])
            XCTAssertEqual(text, "No dictations in the last \(expected).")
        }
        let session = diagnostics.noFailures.render(["period": diagnostics.periodSession.render()])
        XCTAssertEqual(session, "No AI cleanup failures recorded in this session.")
    }

    func testTheLoginRowFollowsTheStagedWindowDecision() {
        let item = SettingsCopy.dictation.appliesWhenSaved
        XCTAssertEqual(item.render(), "Applies when you save.")
        XCTAssertEqual(item.deviation, .stagedSave)
        XCTAssertFalse(SettingsCopy.allItems.contains { $0.render().contains("Applies immediately") })
    }
}
