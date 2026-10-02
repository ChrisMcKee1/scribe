import AVFoundation
import AppKit
import XCTest

@testable import Scribe

final class DictationProblemEpisodeTests: XCTestCase {
    func testEveryProblemIsAnnouncedOnceUntilItsOwnRecovery() {
        for problem in DictationProblemEpisodes.Problem.allCases {
            var episodes = DictationProblemEpisodes()
            XCTAssertTrue(episodes.failed(problem, under: episodes.begin(RecordingID(rawValue: 1))))
            XCTAssertFalse(episodes.failed(problem, under: episodes.begin(RecordingID(rawValue: 2))))
            episodes.recovered([problem], under: episodes.begin(RecordingID(rawValue: 3)))
            XCTAssertTrue(episodes.failed(problem, under: episodes.begin(RecordingID(rawValue: 4))))
        }
    }

    func testAnUnrelatedSuccessDoesNotResetAFault() {
        var episodes = DictationProblemEpisodes()
        XCTAssertTrue(episodes.failed(.transcriptionFailed, under: episodes.begin(RecordingID(rawValue: 1))))
        episodes.recovered([.microphoneUnavailable, .noAudio], under: episodes.begin(RecordingID(rawValue: 2)))
        XCTAssertFalse(episodes.failed(.transcriptionFailed, under: episodes.begin(RecordingID(rawValue: 3))))
    }

    func testOldFailureOrRecoveryCannotReplaceANewerObservation() {
        for problem in DictationProblemEpisodes.Problem.allCases {
            var episodes = DictationProblemEpisodes()
            let old = episodes.begin(RecordingID(rawValue: 1))
            let current = episodes.begin(RecordingID(rawValue: 2))
            XCTAssertTrue(episodes.failed(problem, under: current))
            episodes.recovered([problem], under: old)
            XCTAssertFalse(episodes.failed(problem, under: current))
            episodes.recovered([problem], under: episodes.begin(RecordingID(rawValue: 3)))
            XCTAssertFalse(episodes.failed(problem, under: old))
            XCTAssertTrue(episodes.failed(problem, under: episodes.begin(RecordingID(rawValue: 4))))
        }
    }

    func testAConfigurationChangeResetsFaultsAndRefusesStaleResultsEvenWhenTheChoiceReturns() {
        var episodes = DictationProblemEpisodes()
        let old = episodes.begin(RecordingID(rawValue: 1))
        XCTAssertTrue(episodes.failed(.noAudio, under: old))
        episodes.configurationChanged()
        episodes.configurationChanged()
        XCTAssertFalse(episodes.failed(.noAudio, under: old))
        let current = episodes.begin(RecordingID(rawValue: 2))
        XCTAssertTrue(episodes.failed(.noAudio, under: current))
        episodes.recovered([.noAudio], under: old)
        XCTAssertFalse(episodes.failed(.noAudio, under: current))
    }

    func testDiscardedFallbackDoesNotConsumeTheEpisodeAndSuccessfulSelectionResetsIt() {
        var episodes = DictationProblemEpisodes()
        let fallback = MicrophoneSelectionOutcome(requestedUID: "chosen", result: .systemDefault)
        let selected = MicrophoneSelectionOutcome(requestedUID: "chosen", result: .selected)
        let first = episodes.begin(RecordingID(rawValue: 1))
        XCTAssertFalse(
            episodes.selectionOpened(fallback, under: first, matchesCommittedSelection: true, retained: false))
        XCTAssertTrue(episodes.selectionOpened(fallback, under: first, matchesCommittedSelection: true, retained: true))
        XCTAssertFalse(
            episodes.selectionOpened(
                fallback, under: episodes.begin(RecordingID(rawValue: 2)),
                matchesCommittedSelection: true, retained: true))
        XCTAssertFalse(
            episodes.selectionOpened(
                selected, under: episodes.begin(RecordingID(rawValue: 3)),
                matchesCommittedSelection: true, retained: false))
        XCTAssertTrue(
            episodes.selectionOpened(
                fallback, under: episodes.begin(RecordingID(rawValue: 4)),
                matchesCommittedSelection: true, retained: true))
    }

    func testARetainedStaleFallbackCanBeAnnouncedWithoutChangingTheNewSelectionsEpisode() {
        var episodes = DictationProblemEpisodes()
        let old = episodes.begin(RecordingID(rawValue: 1))
        episodes.configurationChanged()
        let current = episodes.begin(RecordingID(rawValue: 2))
        let fallback = MicrophoneSelectionOutcome(requestedUID: "older", result: .systemDefault)
        XCTAssertTrue(episodes.selectionOpened(fallback, under: old, matchesCommittedSelection: false, retained: true))
        XCTAssertFalse(
            episodes.selectionOpened(fallback, under: old, matchesCommittedSelection: false, retained: false))
        XCTAssertTrue(episodes.failed(.fallbackMicrophone, under: current))
        XCTAssertFalse(
            episodes.selectionOpened(
                MicrophoneSelectionOutcome(requestedUID: "older", result: .selected),
                under: old, matchesCommittedSelection: false, retained: true))
        XCTAssertFalse(episodes.failed(.fallbackMicrophone, under: current))
    }

    func testSilenceThresholdAndUnknownSignalAreNotRecognitionGuesses() {
        func signal(_ value: Float) -> CaptureSignalReport {
            .analyze(interleaved: [value], channels: 1, sampleRate: 16_000)
        }
        XCTAssertTrue(DictationCaptureProblem.hasOnlySilence(signal(0)))
        XCTAssertTrue(DictationCaptureProblem.hasOnlySilence(signal(0.000999)))
        XCTAssertFalse(DictationCaptureProblem.hasOnlySilence(signal(0.001)))
        XCTAssertFalse(DictationCaptureProblem.hasOnlySilence(signal(0.25)))
        XCTAssertFalse(DictationCaptureProblem.hasOnlySilence(nil))
    }

    func testQuickTapThresholdAndDeviceFaultClassificationMatchWindows() {
        XCTAssertEqual(DictationCaptureProblem.empty(held: .milliseconds(999), reason: .hotkeyReleased), .tooQuick)
        XCTAssertEqual(DictationCaptureProblem.empty(held: .seconds(1), reason: .hotkeyReleased), .noAudio)
        XCTAssertEqual(DictationCaptureProblem.empty(held: .seconds(10), reason: .hotkeyReleased), .noAudio)
        XCTAssertEqual(DictationCaptureProblem.empty(held: .zero, reason: .deviceFault), .microphoneStoppedEarly)
    }

    func testActionErrorsAreSuppressedUntilRecoveryButSuccessesRemainUserFeedback() {
        var episode = TrayActionNoticeEpisode()
        XCTAssertTrue(episode.failed())
        XCTAssertFalse(episode.failed())
        episode.recovered()
        XCTAssertTrue(episode.failed())
        XCTAssertEqual(QuickAddNotice.forRefresh(applied: true).title, "Saved to your dictionary")
        XCTAssertEqual(QuickAddNotice.forRefresh(applied: false).title, "Saved, but not in use yet")
    }

    func testPlainNoticesContainOnlyFixedContentAndPreserveDeliveryTruth() {
        let notices: [DictationNotice] = [
            .microphoneUnavailable, .microphoneAccessNeeded, .noAudio, .onlySilence, .noWordsRecognized,
            .tooQuick(.menu), .tooQuick(.hotkey(HotkeyBinding(keyCode: 61))), .transcriptionFailed,
            .recognizerMissing(.foundryCliNotFound), .fallbackMicrophone(.systemDefault),
            .fallbackMicrophone(.unconfirmed), .microphoneDisconnected, .durationLimit(.seconds(600)),
            .cleanupFellBack, .copyFailed, .copiedRecentDictation, .quickAddOpenFailed, .quickAddSaved,
            .quickAddNotInUse, .cleanupActivation(true), .cleanupActivation(false),
        ]
        for notice in notices {
            XCTAssertNil(notice.recoveryText)
            XCTAssertFalse(notice.title.contains("\u{2013}") || notice.body.contains("\u{2014}"))
        }
        XCTAssertFalse(DictationNotice.fallbackMicrophone(.unconfirmed).body.contains("system default"))
        XCTAssertTrue(DictationNotice.cleanupFellBack.body.contains("types what it hears"))
        XCTAssertFalse(DictationNotice.cleanupFellBack.body.contains("inserted"))
        XCTAssertFalse(DictationNotice.copiedRecentDictation.playsSound)
        XCTAssertFalse(DictationNotice.quickAddSaved.playsSound)
        XCTAssertFalse(DictationNotice.fallbackMicrophone(.systemDefault).playsSound)
        XCTAssertTrue(DictationNotice.quickAddNotInUse.playsSound)
        XCTAssertTrue(DictationNotice.noAudio.opensScribeSettings)
        XCTAssertFalse(DictationNotice.quickAddSaved.opensScribeSettings)
    }
}

@MainActor
final class DictationProblemWiringTests: XCTestCase {
    func testNotificationContentUsesRealCategoriesAndNeverPersistsRecoveryText() {
        let problem = DictationNotificationCenter.content(for: .noAudio)
        XCTAssertEqual(problem.title, DictationNotice.noAudio.title)
        XCTAssertEqual(problem.body, DictationNotice.noAudio.body)
        XCTAssertEqual(problem.categoryIdentifier, DictationNotificationCenter.appSettingsCategoryIdentifier)
        XCTAssertNotNil(problem.sound)
        XCTAssertTrue(problem.userInfo.isEmpty)

        let copied = DictationNotificationCenter.content(for: .copiedRecentDictation)
        XCTAssertNil(copied.sound)
        XCTAssertTrue(copied.categoryIdentifier.isEmpty)

        let secret = "private recovered transcript"
        let recovery = DictationNotificationCenter.content(for: .notInserted(secret, recoveryGeneration: 1))
        XCTAssertEqual(recovery.categoryIdentifier, DictationNotificationCenter.recoveryCategoryIdentifier)
        XCTAssertFalse(recovery.title.contains(secret))
        XCTAssertFalse(recovery.body.contains(secret))
        XCTAssertTrue(recovery.userInfo.isEmpty)
    }

    private func failOpen(_ harness: DictationHarness) async {
        let count = harness.controller.checkpoints.openAnswers + 1
        _ = await harness.press()
        await waitUntil("the failed open is handled") { harness.controller.checkpoints.openAnswers == count }
        harness.release()
    }

    func testMicrophoneOpenAndPermissionFailuresAreEpisodesResetByASuccessfulOpen() async {
        for error in [
            AudioCaptureEngineError.missingInputNodeFormat,
            .microphoneNotAuthorized(.denied),
        ] {
            let harness = makeHarness()
            harness.capture.openError = error
            await failOpen(harness)
            await failOpen(harness)
            XCTAssertEqual(harness.notifier.notices.count, 1)
            harness.capture.openError = nil
            await harness.dictate()
            await harness.waitUntilProcessed()
            harness.capture.openError = error
            await failOpen(harness)
            XCTAssertEqual(harness.notifier.notices.count, 2)
        }
    }

    func testOnlyCommittedConfigurationChangesResetMicrophoneEpisodes() async {
        let choice = Collected<DictationNoticeConfiguration>()
        choice.values = [DictationNoticeConfiguration(microphoneUID: "first")]
        var configuration = DictationController.Configuration()
        configuration.noticeConfiguration = { choice.values[0] }
        let harness = makeHarness(configuration: configuration)
        harness.capture.openError = AudioCaptureEngineError.missingInputNodeFormat
        await failOpen(harness)
        harness.controller.noticeConfigurationMayHaveChanged()
        await failOpen(harness)
        XCTAssertEqual(harness.notifier.notices.count, 1)
        choice.values[0] = DictationNoticeConfiguration(microphoneUID: "second")
        harness.controller.noticeConfigurationMayHaveChanged()
        choice.values[0] = DictationNoticeConfiguration(microphoneUID: "first")
        harness.controller.noticeConfigurationMayHaveChanged()
        await failOpen(harness)
        XCTAssertEqual(harness.notifier.notices.count, 2)
    }

    func testNoAudioAndQuickTapAreDistinctAndActualSamplesResetBoth() async throws {
        let harness = makeHarness()
        harness.capture.samples = []
        for _ in 0..<2 {
            await harness.dictate()
            await harness.waitUntilProcessed()
        }
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.tooQuick])
        for _ in 0..<2 {
            _ = try await harness.pressAdmitted()
            await harness.waitUntilLive()
            harness.clock.advance(by: .seconds(1))
            harness.release()
            await harness.waitUntilProcessed()
        }
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.tooQuick, .noAudio])
        harness.capture.samples = [0.25]
        await harness.dictate()
        await harness.waitUntilProcessed()
        harness.capture.samples = []
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.tooQuick, .noAudio, .tooQuick])
    }

    func testBlankRecognitionOnSilenceAndRealAudioIsHonestAndRecoversIndependently() async {
        let harness = makeHarness()
        harness.transcriber.defaultText = ""
        harness.capture.signal = .analyze(interleaved: [0], channels: 1, sampleRate: 16_000)
        for _ in 0..<2 {
            await harness.dictate()
            await harness.waitUntilProcessed()
        }
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.onlySilence])
        XCTAssertEqual(harness.transcriber.calls, 0, "known silence must not be sent to the recognizer")
        harness.capture.signal = .analyze(interleaved: [0.25], channels: 1, sampleRate: 16_000)
        for _ in 0..<2 {
            await harness.dictate()
            await harness.waitUntilProcessed()
        }
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.onlySilence, .noWordsRecognized])
        harness.transcriber.defaultText = "words"
        await harness.dictate()
        await harness.waitUntilProcessed()
        harness.transcriber.defaultText = ""
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.onlySilence, .noWordsRecognized, .noWordsRecognized])
        XCTAssertEqual(harness.fakeInjector.texts, ["words "])
    }

    func testRecognitionFailuresAndMissingBackendRecoverOnlyAfterRecognizingWords() async {
        let harness = makeHarness()
        for error in [
            TranscriptionError.backendMissing(.foundryCliNotFound),
            .exitCode(3),
        ] {
            for _ in 0..<2 {
                harness.transcriber.steps = [.failure(error)]
                await harness.dictate()
                await harness.waitUntilProcessed()
            }

        }
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.recognizerMissing, .transcriptionFailed])
        await harness.dictate()
        await harness.waitUntilProcessed()
        harness.transcriber.steps = [.failure(TranscriptionError.backendMissing(.foundryCliNotFound))]
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(
            harness.notifier.notices.map(\.kind), [.recognizerMissing, .transcriptionFailed, .recognizerMissing])
    }

    func testTextIntentionallyRemovedByRulesRemainsQuiet() async {
        let harness = makeHarness()
        harness.load(dictionary: [DictionaryEntry(pattern: "remove", replacement: "", wholeWord: true)])
        harness.transcriber.defaultText = "remove"
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertTrue(harness.notifier.notices.isEmpty)
        XCTAssertTrue(harness.presenter.noticesShown().isEmpty)
        XCTAssertTrue(harness.fakeInjector.deliveries.isEmpty)
        XCTAssertTrue(harness.history.records.isEmpty)
    }

    func testTheDurationLimitIsReportedOnceForEachStoppedRecordingNotAsAServiceFault() async throws {
        var configuration = DictationController.Configuration()
        configuration.maximumDuration = .seconds(60)
        let harness = makeHarness(configuration: configuration)
        let id = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        harness.controller.handleCaptureEvent(CaptureEvent(owner: id, kind: .stopRequested(.durationLimit)))
        harness.controller.handleCaptureEvent(CaptureEvent(owner: id, kind: .stopRequested(.durationLimit)))
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.durationLimit])
        XCTAssertEqual(harness.notifier.notices.first?.title, "Dictation stopped at 1 minute")
        XCTAssertEqual(harness.fakeInjector.deliveries.count, 1)
    }

    func testFallbackIsAnnouncedFromSealedAudioEvenWithoutALiveEventAndRecoversOnSelectionSuccess() async {
        var configuration = DictationController.Configuration()
        configuration.noticeConfiguration = { DictationNoticeConfiguration(microphoneUID: "chosen") }
        let harness = makeHarness(configuration: configuration)
        harness.capture.microphoneSelection = MicrophoneSelectionOutcome(requestedUID: "chosen", result: .systemDefault)
        for _ in 0..<2 {
            await harness.dictate()
            await harness.waitUntilProcessed()
        }
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.fallbackMicrophone])
        harness.capture.microphoneSelection = MicrophoneSelectionOutcome(requestedUID: "chosen", result: .selected)
        await harness.dictate()
        await harness.waitUntilProcessed()
        harness.capture.microphoneSelection = MicrophoneSelectionOutcome(requestedUID: "chosen", result: .systemDefault)
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.fallbackMicrophone, .fallbackMicrophone])
    }

    func testLiveFallbackAndSealedOutcomeAreAnnouncedOnlyOnceAndNeverAfterQuit() async throws {
        var configuration = DictationController.Configuration()
        configuration.noticeConfiguration = { DictationNoticeConfiguration(microphoneUID: "chosen") }
        let harness = makeHarness(configuration: configuration)
        let selection = MicrophoneSelectionOutcome(requestedUID: "chosen", result: .systemDefault)
        harness.capture.microphoneSelection = selection
        let id = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        harness.controller.handleCaptureEvent(CaptureEvent(owner: id, kind: .microphoneSelection(selection)))
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.fallbackMicrophone])
        harness.release()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.notifier.notices.count, 1)
        _ = await harness.controller.shutDown()
        harness.controller.handleCaptureEvent(CaptureEvent(owner: id, kind: .microphoneSelection(selection)))
        XCTAssertEqual(harness.notifier.notices.count, 1)
    }

    func testDeviceFaultNoticesPreserveCapturedTextAndResetAfterASuccessfulRecording() async throws {
        let harness = makeHarness()
        for _ in 0..<2 {
            let id = try await harness.pressAdmitted()
            await harness.waitUntilLive()
            harness.controller.handleCaptureEvent(CaptureEvent(owner: id, kind: .stopRequested(.deviceChanged)))
            await harness.waitUntilProcessed()
            harness.release()
        }
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.microphoneDisconnected])
        XCTAssertEqual(harness.fakeInjector.texts.count, 2)
        await harness.dictate()
        await harness.waitUntilProcessed()
        let id = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        harness.controller.handleCaptureEvent(CaptureEvent(owner: id, kind: .stopRequested(.deviceChanged)))
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.microphoneDisconnected, .microphoneDisconnected])
    }

    func testEveryFailedInsertionRetainsItsOwnRecoveryRegardlessOfOtherFaultEpisodes() async {
        let harness = makeHarness()
        harness.fakeInjector.result = InjectionResult(delivery: .targetChanged)
        for text in ["first private dictation", "second private dictation"] {
            harness.transcriber.defaultText = text
            await harness.dictate()
            await harness.waitUntilProcessed()
        }
        XCTAssertEqual(
            harness.notifier.notices.map(\.recoveryText), ["first private dictation", "second private dictation"])
        XCTAssertTrue(harness.notifier.notices.allSatisfy { !$0.body.contains("private dictation") })
    }

    func testStartupRecoveryRearmsOnlyThatProblemAndQuitSuppressesLateCallbacks() {
        let posted = Collected<DictationNotice>()
        let startup = StartupNotices { posted.values.append($0) }
        startup.report(.inputMonitoringMissing)
        startup.report(.rulesUnavailable)
        startup.settle(.storage)
        startup.settle(.notifications)
        startup.report(.rulesUnavailable)
        XCTAssertEqual(posted.values.count, 1)
        startup.recover(.rulesUnavailable)
        startup.report(.rulesUnavailable)
        startup.report(.inputMonitoringMissing)
        XCTAssertEqual(posted.values.count, 2)
        startup.close()
        startup.recover(.rulesUnavailable)
        startup.report(.rulesUnavailable)
        XCTAssertEqual(posted.values.count, 2)
    }

    func testRecentCopyFeedbackChecksFailureAndResetsOnlyOnSuccessfulCopy() throws {
        let copied = Collected<Bool>()
        copied.values = [false]
        let notices = Collected<DictationNotice>()
        let store = LastTranscriptStore()
        store.set("private words")
        let recent = RecentDictationsMenu(
            store: store, copy: { _ in copied.values[0] }, notify: { notices.values.append($0) })
        let menu = try XCTUnwrap(recent.item.submenu)
        recent.populate(menu)
        let item = try XCTUnwrap(menu.items.first)
        let action = try XCTUnwrap(item.action)
        for _ in 0..<2 { _ = recent.perform(action, with: item) }
        XCTAssertEqual(notices.values.map(\.kind), [.copyFailed])
        copied.values[0] = true
        _ = recent.perform(action, with: item)
        copied.values[0] = false
        _ = recent.perform(action, with: item)
        XCTAssertEqual(notices.values.map(\.kind), [.copyFailed, .copied, .copyFailed])
        store.removeAll()
        _ = recent.perform(action, with: item)
        XCTAssertEqual(notices.values.count, 3, "a stale menu must copy and announce nothing")
        XCTAssertTrue(notices.values.allSatisfy { !$0.body.contains("private words") })
    }
}
