import XCTest

@testable import Scribe

/// The recording lifecycle: admission, stops, faults, rebinding, the duration deadline and the pill's notices, driven
/// through fakes and gates on the main actor. Nothing here waits on time: the clock moves when a test moves it.
@MainActor
final class DictationControllerTests: XCTestCase {
    private func isListening(_ state: OverlayState?) -> Bool {
        if case .listening = state { return true }
        return false
    }

    // MARK: - Admission

    /// A press is admitted in the event tap's callback and the microphone is asked for on a later turn of the main
    /// actor. A pause that lands in between wins: the admission is checked again, and nothing opens.
    func testAPauseThatLandsBeforeTheMicrophoneIsAskedForOpensNothing() async {
        let harness = DictationHarness()

        XCTAssertTrue(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        XCTAssertTrue(harness.activity.isActive)
        harness.controller.setPaused(true)
        await drainMainActor()

        XCTAssertTrue(harness.capture.starts.isEmpty, "the microphone was asked for after the pause")
        XCTAssertNil(harness.controller.currentRecording)
        XCTAssertFalse(harness.activity.isActive)
        XCTAssertEqual(harness.transcriber.calls, 0)
        XCTAssertEqual(harness.presenter.last?.isRecording, false)
        XCTAssertEqual(harness.presenter.last?.isPaused, true)
        XCTAssertFalse(harness.presenter.overlays.contains(where: isListening))

        // Paused, a press starts nothing, so the key's listener stays free for the press after resuming.
        XCTAssertFalse(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        harness.controller.setPaused(false)
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.fakeInjector.texts, ["hello from the recognizer"])
    }

    /// A pause while the device is still opening ends the recording before it ever captured anything: the open's late
    /// answer changes nothing, nothing is processed and nothing is shown as recording.
    func testAPauseWhileTheMicrophoneOpensLeavesNothingRecordingAndNothingProcessed() async throws {
        let harness = DictationHarness()
        harness.capture.holdsOpens = true

        let id = try await harness.pressAdmitted()
        await waitUntil("the open is pending") { harness.capture.pendingOpens == 1 }
        harness.controller.setPaused(true)
        XCTAssertEqual(harness.capture.stopCount(for: id), 1)

        // The engine answers the stopped open late; so does an open that claims it went live.
        harness.capture.completeOpen(id, .stoppedWhileOpening)
        await drainMainActor()

        XCTAssertNil(harness.controller.currentRecording)
        XCTAssertFalse(harness.controller.isRecordingLive)
        XCTAssertEqual(harness.transcriber.calls, 0)
        XCTAssertEqual(harness.controller.processingCount, 0)
        XCTAssertFalse(harness.activity.isActive)
        XCTAssertFalse(harness.presenter.overlays.contains(where: isListening))
        XCTAssertEqual(harness.presenter.last?.isRecording, false)
    }

    /// A quick tap: the release arrives while the device is still opening. The recording ends once, quietly, and
    /// the next one works.
    func testAStopDuringTheOpenEndsTheRecordingOnceAndTheNextRecordingWorks() async throws {
        let harness = DictationHarness()
        harness.capture.holdsOpens = true

        let first = try await harness.pressAdmitted()
        await waitUntil("the open is pending") { harness.capture.pendingOpens == 1 }
        harness.release()
        harness.capture.completeOpen(first, .stoppedWhileOpening)
        await drainMainActor()
        harness.release()

        XCTAssertEqual(harness.capture.stopCount(for: first), 1)
        XCTAssertNil(harness.controller.currentRecording)
        XCTAssertEqual(harness.transcriber.calls, 0)
        XCTAssertFalse(harness.activity.isActive)
        XCTAssertTrue(harness.notifier.notices.isEmpty)
        XCTAssertTrue(harness.presenter.noticesShown().isEmpty)

        harness.capture.holdsOpens = false
        let second = try await harness.dictateAdmitted()
        await harness.waitUntilProcessed()
        XCTAssertNotEqual(first, second)
        XCTAssertEqual(harness.fakeInjector.deliveries.count, 1)
        XCTAssertEqual(harness.capture.stopCount(for: second), 1)
    }

    /// A microphone that cannot open ends the recording, gives up its lease and says so on the pill and in a
    /// notification that opens the privacy pane: never a modal alert over the app the user is typing in.
    func testAnOpenThatFailsEndsTheRecordingAndSaysSoWithoutAnAlert() async {
        let harness = DictationHarness()
        harness.capture.openError = AudioCaptureEngineError.microphoneNotAuthorized(.denied)

        _ = await harness.press()
        await waitUntil("the failure is handled") { harness.controller.currentRecording == nil }

        XCTAssertFalse(harness.activity.isActive)
        XCTAssertEqual(harness.presenter.noticesShown(), [.microphoneAccessNeeded])
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.microphoneAccessNeeded])
        XCTAssertEqual(harness.notifier.notices.first?.settingsPane, .microphone)
    }

    // MARK: - Rebinding, faults and the toggle

    /// Rebinding while the old key holds a recording settles it: that recording ends once and is processed, the old
    /// key's release (which no longer matches) ends nothing, and the new key starts the next recording.
    func testRebindingWhileTheKeyIsHeldEndsThatRecordingOnceAndTheNewKeyWorks() async throws {
        let harness = DictationHarness()
        let oldKey = DictationHarness.holdKey
        let newKey = HotkeyBinding(keyCode: 58)

        let first = try await harness.pressAdmitted(oldKey)
        await harness.waitUntilLive()
        harness.controller.hotkeyReleased(oldKey, cause: .bindingChanged)
        XCTAssertEqual(harness.capture.stopCount(for: first), 1)
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.reports.latest?.stopReason, .bindingChanged)

        harness.release(oldKey)
        let second = try await harness.pressAdmitted(newKey)
        await harness.waitUntilLive()
        harness.release(oldKey)
        await drainMainActor()
        XCTAssertEqual(harness.controller.currentRecording, second, "the old key's release ended the new recording")
        XCTAssertTrue(harness.controller.isRecordingLive)

        harness.release(newKey)
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.capture.stopCount(for: first), 1)
        XCTAssertEqual(harness.capture.stopCount(for: second), 1)
        XCTAssertEqual(harness.fakeInjector.deliveries.count, 2)
        XCTAssertEqual(harness.reports.latest?.stopReason, .hotkeyReleased)
    }

    /// A device fault ends the recording once and what it captured is still processed, with a notice that the
    /// microphone stopped early. A late meter reading or stop request for it never touches the next recording.
    func testADeviceFaultEndsTheRecordingOnceAndNeverReachesTheNextOne() async throws {
        let harness = DictationHarness()

        let first = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        harness.capture.post(.stopRequested(.deviceChanged), for: first)
        await waitUntil("the faulted recording stops") { harness.controller.currentRecording == nil }
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.capture.stopCount(for: first), 1)
        XCTAssertEqual(harness.fakeInjector.deliveries.count, 1)
        XCTAssertEqual(harness.reports.latest?.stopReason, .deviceFault)
        XCTAssertEqual(harness.presenter.noticesShown().last, .microphoneStoppedEarly)

        let second = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        let shownBefore = harness.presenter.presentations.count
        harness.capture.post(.level(AudioLevelMeasurement(peakAmplitude: 0.9, rmsAmplitude: 0.5)), for: first)
        harness.capture.post(.stopRequested(.deviceChanged), for: first)
        await drainMainActor()

        XCTAssertEqual(harness.presenter.presentations.count, shownBefore, "a late event of the faulted recording")
        XCTAssertEqual(harness.controller.currentRecording, second)
        XCTAssertTrue(harness.controller.isRecordingLive)
        XCTAssertEqual(harness.capture.stopCount(for: second), 0)
        XCTAssertEqual(harness.capture.stopCount(for: first), 1)

        harness.release()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.transcriber.calls, 2)
        XCTAssertEqual(harness.fakeInjector.deliveries.count, 2)
    }

    /// The toggle key (Caps Lock, the default) stops on silence and a held key never does; a toggle's recording that
    /// ended some other way than by the key tells the key's listener, so its next tap starts a new recording.
    func testTheStopPolicyFollowsTheBindingThatFired() async throws {
        let harness = DictationHarness()

        let held = try await harness.pressAdmitted(DictationHarness.holdKey)
        XCTAssertEqual(harness.capture.starts.last?.policy.stopsOnSilence, false)
        XCTAssertEqual(harness.capture.starts.last?.policy.maximumDuration, CaptureStopPolicy.defaultMaximumDuration)
        await harness.waitUntilLive()
        harness.release(DictationHarness.holdKey)
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.capture.stopCount(for: held), 1)

        let toggled = try await harness.pressAdmitted(DictationHarness.toggleKey)
        XCTAssertEqual(harness.capture.starts.last?.policy.stopsOnSilence, true)
        await harness.waitUntilLive()
        let silence = SilenceStopDetail(heardSpeech: true, peakLevel: 0.3, noiseFloor: 0.001, voiceThreshold: 0.01)
        harness.capture.post(.stopRequested(.silence(silence)), for: toggled)
        await waitUntil("the silence stop") { harness.controller.currentRecording == nil }
        XCTAssertEqual(harness.triggers.cancelledToggles, 1)
        await harness.waitUntilProcessed()

        // Ended by the key itself: nothing to cancel.
        _ = try await harness.pressAdmitted(DictationHarness.toggleKey)
        await harness.waitUntilLive()
        harness.release(DictationHarness.toggleKey)
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.triggers.cancelledToggles, 1)

        // The tray's test dictation is a toggle too.
        harness.controller.toggleMenuDictation()
        await waitUntil("the menu recording opens") { harness.controller.isRecordingLive }
        XCTAssertEqual(harness.capture.starts.last?.policy.stopsOnSilence, true)
        harness.controller.toggleMenuDictation()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.reports.latest?.stopReason, .menu)
    }

    // MARK: - The duration deadline

    /// The deadline is the controller's own and runs on its clock, so a device that delivers no buffers at all (the
    /// case the engine's sample-count ceiling cannot see) still ends at the ceiling, exactly once.
    func testTheDurationDeadlineEndsARecordingThatReceivesNoBuffersExactlyOnce() async throws {
        let harness = DictationHarness()

        let id = try await harness.pressAdmitted(DictationHarness.toggleKey)
        await harness.waitUntilLive()
        await waitUntil("the deadline is armed") { harness.clock.sleeperCount == 1 }
        harness.clock.advance(by: .seconds(10 * 60) - .milliseconds(1))
        await drainMainActor()
        XCTAssertTrue(harness.controller.isRecordingLive, "stopped before its ceiling")
        XCTAssertEqual(harness.capture.stopCount(for: id), 0)

        harness.clock.advance(by: .milliseconds(1))
        await waitUntil("the ceiling stops the recording") { harness.controller.currentRecording == nil }
        await harness.waitUntilProcessed()
        harness.clock.advance(by: .seconds(10 * 60))
        await drainMainActor()

        XCTAssertEqual(harness.capture.stopCount(for: id), 1)
        XCTAssertEqual(harness.reports.latest?.stopReason, .durationLimit)
        XCTAssertEqual(harness.triggers.cancelledToggles, 1)
        XCTAssertEqual(harness.presenter.noticesShown().last, .durationLimitReached)
    }

    /// Windows' `A_late_ceiling_tick_queued_for_one_recording_never_ends_the_next`: the first recording's deadline
    /// fell due and its tick was already queued when the key stopped it and the next recording started. The stale
    /// tick ends nothing, and the next recording ends at its own ceiling.
    func testAStaleDeadlineNeverEndsTheNextRecording() async throws {
        let harness = DictationHarness()

        let first = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        await waitUntil("the first deadline is armed") { harness.clock.sleeperCount == 1 }
        // Due: the first recording's tick is queued on the main actor but has not run yet.
        harness.clock.advance(by: .seconds(10 * 60))
        harness.release()
        XCTAssertTrue(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        let second = try XCTUnwrap(harness.controller.currentRecording)
        XCTAssertNotEqual(first, second)
        await harness.waitUntilLive()
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.controller.currentRecording, second, "the first recording's tick ended the second")
        XCTAssertTrue(harness.controller.isRecordingLive)
        XCTAssertEqual(harness.capture.stopCount(for: second), 0)

        // A tick for the second recording that arrives before it has run its whole ceiling ends nothing either.
        harness.controller.durationDeadlineReached(for: second)
        XCTAssertTrue(harness.controller.isRecordingLive)

        await waitUntil("the second deadline is armed") { harness.clock.sleeperCount == 1 }
        harness.clock.advance(by: .seconds(10 * 60))
        await waitUntil("the second recording's own ceiling") { harness.controller.currentRecording == nil }
        XCTAssertEqual(harness.capture.stopCount(for: second), 1)
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.reports.latest?.stopReason, .durationLimit)
    }

    // MARK: - Presentation

    /// A notice's timed end that falls due after a newer recording started never takes that recording's pill down:
    /// it is tagged with the revision that showed it, and the recording cleared it.
    func testANoticesLateEndNeverHidesTheRecordingThatStartedAfterIt() async throws {
        let harness = DictationHarness()
        harness.fakeInjector.result = InjectionResult(delivery: .targetChanged)

        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.lastOverlay, .notice(.textKept))
        await waitUntil("the notice's end is scheduled") { harness.clock.sleeperCount == 1 }

        _ = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        XCTAssertTrue(isListening(harness.lastOverlay))
        let recordingRevision = try XCTUnwrap(harness.presenter.last?.revision)

        harness.clock.advance(by: .seconds(5))
        await drainMainActor()

        XCTAssertTrue(isListening(harness.lastOverlay), "the old notice's end hid the new recording's pill")
        XCTAssertEqual(harness.presenter.last?.revision, recordingRevision)
        XCTAssertTrue(harness.controller.isRecordingLive)
    }

    /// A notice's end that was already due when a newer notice replaced it takes nothing down: it is tagged with the
    /// revision that showed its own notice, and the newer notice has another.
    func testANoticesLateEndNeverTakesDownANewerNotice() async throws {
        var configuration = DictationController.Configuration()
        configuration.maximumDictationsInProcessing = 1
        let harness = DictationHarness(configuration: configuration)
        let gate = DictationGate<String>()
        harness.transcriber.steps = [.gate(gate)]
        await harness.dictate()
        await waitUntil("the recognizer is asked") { gate.waitingCount == 1 }

        XCTAssertFalse(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        XCTAssertEqual(harness.lastOverlay, .notice(.stillProcessing))
        await waitUntil("the first notice's end is scheduled") { harness.clock.sleeperCount == 1 }
        // The first notice's end falls due and waits on the main actor; a second notice replaces the first before it
        // runs.
        harness.clock.advance(by: configuration.noticeDuration)
        XCTAssertFalse(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        let newer = try XCTUnwrap(harness.presenter.last)
        await drainMainActor()

        XCTAssertEqual(harness.lastOverlay, .notice(.stillProcessing), "the old end took the newer notice down")
        XCTAssertEqual(harness.presenter.last?.revision, newer.revision)

        harness.clock.advance(by: configuration.noticeDuration)
        await waitUntil("the newer notice runs its course") { harness.lastOverlay == .processing }
        gate.open("words")
        await harness.waitUntilProcessed()
    }

    /// The same, when the notice's end had already fallen due and was queued when the recording started: when it runs,
    /// while the microphone is still opening, it presents nothing at all.
    func testANoticesEndQueuedBeforeARecordingStartedChangesNothing() async throws {
        let harness = DictationHarness()
        harness.fakeInjector.result = InjectionResult(delivery: .targetChanged)

        await harness.dictate()
        await harness.waitUntilProcessed()
        await waitUntil("the notice's end is scheduled") { harness.clock.sleeperCount == 1 }
        harness.capture.holdsOpens = true
        // The end falls due and waits on the main actor; the press runs first.
        harness.clock.advance(by: .seconds(5))
        XCTAssertTrue(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        let id = try XCTUnwrap(harness.controller.currentRecording)
        let presentedAtPress = harness.presenter.presentations.count
        await waitUntil("the open is pending") { harness.capture.pendingOpens == 1 }
        await drainMainActor()
        XCTAssertEqual(harness.presenter.presentations.count, presentedAtPress, "the stale end presented a change")

        harness.capture.completeOpen(id, .live)
        await harness.waitUntilLive()
        await drainMainActor()

        XCTAssertTrue(isListening(harness.lastOverlay))
        let revisions = harness.presenter.presentations.map(\.revision)
        XCTAssertEqual(revisions, revisions.sorted(), "revisions go up with every change")
        XCTAssertEqual(Set(revisions).count, revisions.count)
    }

    /// A notice runs its course when nothing replaces it.
    func testANoticeEndsAfterItsTimeWhenNothingReplacesIt() async {
        let harness = DictationHarness()
        harness.fakeInjector.result = InjectionResult(delivery: .targetChanged)

        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.lastOverlay, .notice(.textKept))
        await waitUntil("the notice's end is scheduled") { harness.clock.sleeperCount == 1 }
        harness.clock.advance(by: DictationController.Configuration().noticeDuration)
        await waitUntil("the notice ends") { harness.lastOverlay == .hidden }
    }

    // MARK: - Leases and limits

    /// The foreground lease runs from admission to the end of processing, on every way a dictation ends.
    func testTheForegroundLeaseIsHeldFromAdmissionUntilProcessingEnds() async throws {
        let harness = DictationHarness()
        let gate = DictationGate<String>()
        harness.transcriber.steps = [.gate(gate)]

        XCTAssertFalse(harness.activity.isActive)
        await harness.dictate()
        XCTAssertTrue(harness.activity.isActive, "released when the recording stopped")
        await waitUntil("the recognizer is asked") { gate.waitingCount == 1 }
        XCTAssertTrue(harness.activity.isActive)
        gate.open("some words")
        await harness.waitUntilProcessed()
        XCTAssertFalse(harness.activity.isActive)

        // A capture with nothing in it.
        harness.capture.samples = []
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertFalse(harness.activity.isActive)

        // A recognizer failure.
        harness.capture.samples = [Float](repeating: 0.1, count: 1_600)
        harness.transcriber.steps = [.failure(TranscriptionError.exitCode(3))]
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertFalse(harness.activity.isActive)
        XCTAssertEqual(harness.presenter.noticesShown().last, .transcriptionFailed)
    }

    /// A press while earlier dictations are still processing is turned away once the limit is reached, so the audio
    /// held stays bounded, and the pill says why.
    func testAPressIsTurnedAwayWhileTooManyDictationsAreProcessing() async throws {
        var configuration = DictationController.Configuration()
        configuration.maximumDictationsInProcessing = 2
        let harness = DictationHarness(configuration: configuration)
        let gate = DictationGate<String>()
        harness.transcriber.steps = [.gate(gate), .gate(gate)]

        await harness.dictate()
        await harness.dictate()
        XCTAssertEqual(harness.controller.processingCount, 2)

        XCTAssertFalse(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        XCTAssertNil(harness.controller.currentRecording)
        XCTAssertEqual(harness.lastOverlay, .notice(.stillProcessing))

        gate.open("done")
        await harness.waitUntilProcessed()
        try await harness.dictateAdmitted()
    }
}
