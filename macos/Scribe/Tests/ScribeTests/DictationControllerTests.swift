import AVFoundation
import XCTest
import os

@testable import Scribe

/// The recording lifecycle: admission, stops, faults, rebinding, the duration deadline and the pill's notices, driven
/// through fakes and gates on the main actor. No order here depends on time: the clock moves when a test moves it, and
/// a test checks what did not happen only after an explicit signal that the step it is about has run. The one test that
/// runs the real capture engine polls its control queue with a real deadline, and its held flush has a watchdog.
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
        let harness = makeHarness()

        XCTAssertTrue(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        XCTAssertTrue(harness.activity.isActive)
        harness.controller.setPaused(true)
        await waitUntil("the scheduled open has checked its admission") {
            harness.controller.checkpoints.admissionChecks == 1
        }

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
        let harness = makeHarness()
        harness.capture.holdsOpens = true

        let id = try await harness.pressAdmitted()
        await waitUntil("the open is pending") { harness.capture.pendingOpens == 1 }
        harness.controller.setPaused(true)
        XCTAssertEqual(harness.capture.stopCount(for: id), 1)

        // The engine answers the stopped open late.
        harness.capture.completeOpen(id, .stoppedWhileOpening)
        await waitUntil("the open's answer is handled") { harness.controller.checkpoints.openAnswers == 1 }

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
        let harness = makeHarness()
        harness.capture.holdsOpens = true

        let first = try await harness.pressAdmitted()
        await waitUntil("the open is pending") { harness.capture.pendingOpens == 1 }
        harness.release()
        harness.capture.completeOpen(first, .stoppedWhileOpening)
        await waitUntil("the open's answer is handled") { harness.controller.checkpoints.openAnswers == 1 }
        harness.release()
        XCTAssertEqual(harness.controller.checkpoints.ignoredReleases, 1, "a second release ended something")

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
        let harness = makeHarness()
        harness.capture.openError = AudioCaptureEngineError.microphoneNotAuthorized(.denied)

        _ = await harness.press()
        await waitUntil("the failed open is handled") { harness.controller.checkpoints.openAnswers == 1 }

        XCTAssertNil(harness.controller.currentRecording)
        XCTAssertFalse(harness.activity.isActive)
        XCTAssertEqual(harness.presenter.noticesShown(), [.microphoneAccessNeeded])
        XCTAssertEqual(harness.notifier.kinds, [.microphoneAccessNeeded])
        XCTAssertEqual(harness.notifier.notices.first?.settingsPane, .microphone)
    }

    // MARK: - Stopping never waits for the seal

    /// A release only retires the recording: it returns at once, the recording has given up the microphone and holds
    /// its place in both turn queues, and the main actor goes on (the next recording starts) while the samples are
    /// still being sealed. The first dictation's seal finishing last does not let the second go first.
    func testAReleaseRetiresTheRecordingAtOnceAndKeepsItsPlaceWhileItsSealIsHeld() async throws {
        let harness = makeHarness()
        harness.capture.holdsSeals = true

        harness.capture.samples = [Float](repeating: 0.1, count: 1_600)
        let first = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        harness.release()
        XCTAssertNil(harness.controller.currentRecording)
        XCTAssertEqual(harness.controller.processingCount, 1)
        XCTAssertEqual(harness.capture.stopCount(for: first), 1)
        await waitUntil("the first seal is held") { harness.capture.pendingSeals == 1 }

        harness.capture.samples = [Float](repeating: 0.1, count: 3_200)
        let second = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        harness.release()
        await waitUntil("the second seal is held") { harness.capture.pendingSeals == 2 }
        harness.transcriber.steps = [.text("first words"), .text("second words")]

        harness.capture.releaseSeal(second)
        await waitUntil("the second waits for the first") { harness.controller.dictationsWaitingToTranscribe == 1 }
        XCTAssertEqual(harness.transcriber.calls, 0, "the second dictation was transcribed before the first")

        harness.capture.releaseSeal(first)
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.transcriber.sampleCounts, [1_600, 3_200])
        XCTAssertEqual(harness.fakeInjector.texts, ["first words", "second words"])
        XCTAssertEqual(harness.history.dictationIDs, [first.rawValue, second.rawValue])
    }

    /// The same through the real capture engine and its adapter, with the release arriving through a real
    /// `HotkeyManager` as the event tap would deliver it. The engine's resampler flush is held on its control queue
    /// (`HeldFlush`): the release callback returns while the flush is still held, the main actor stays free (the next
    /// recording is admitted), and once the test lets the flush go both dictations are processed in order. A release
    /// that waited for the flush could only return once the flush's watchdog let it go, which the test sees.
    func testTheReleaseCallbackReturnsWhileTheRealEngineStillSealsTheRecording() async throws {
        var resampled = CaptureTestDevice.Configuration()
        resampled.sampleRate = 48_000
        let firstDevice = CaptureTestDevice(resampled, name: "first")
        let secondDevice = CaptureTestDevice(resampled, name: "second")
        let flush = HeldFlush()
        addTeardownBlock { _ = flush.release() }
        let engine = AudioCaptureEngine(
            resamplerTailFlush: flush.tailFlush,
            makeDevice: CaptureTestDeviceFactory([firstDevice, secondDevice]).make)
        let harness = makeHarness(capture: LiveDictationCapture(engine: engine))
        let manager = harness.wireHotkeyManager(keyCode: 61)
        harness.transcriber.steps = [.text("first words"), .text("second words")]

        manager.receive(HotkeyTestEvents.rightOption(down: true))
        await pollUntil("the first recording is live") { harness.controller.isRecordingLive }
        XCTAssertTrue(
            firstDevice.deliver(AudioTestBuffers.mono([Float](repeating: 0.25, count: 4_800), sampleRate: 48_000)))

        manager.receive(HotkeyTestEvents.rightOption(down: false))
        XCTAssertNil(flush.releasedBy, "the release callback returned only after the flush was let go")
        XCTAssertNil(harness.controller.currentRecording)
        XCTAssertEqual(harness.controller.processingCount, 1)
        let entered = await flush.entered.wait()
        XCTAssertTrue(entered, "the seal never reached the resampler flush")
        XCTAssertTrue(flush.isHeld)

        // The flush holds the control queue; the main actor admits the next recording meanwhile.
        manager.receive(HotkeyTestEvents.rightOption(down: true))
        XCTAssertNotNil(harness.controller.currentRecording, "the main actor could not admit the next recording")
        XCTAssertEqual(harness.transcriber.calls, 0)

        XCTAssertTrue(flush.release(), "the flush's watchdog let it go before the test did")
        await pollUntil("the second recording is live") { harness.controller.isRecordingLive }
        XCTAssertTrue(
            secondDevice.deliver(AudioTestBuffers.mono([Float](repeating: 0.5, count: 4_800), sampleRate: 48_000)))
        manager.receive(HotkeyTestEvents.rightOption(down: false))
        await pollUntil("both dictations are processed") { harness.controller.processingCount == 0 }
        _ = await bounded("the engine's device work to finish") { await engine.waitUntilIdle() }

        XCTAssertEqual(flush.releasedBy, .test)
        XCTAssertEqual(harness.fakeInjector.texts, ["first words", "second words"])
        XCTAssertEqual(harness.transcriber.sampleCounts.count, 2)
        XCTAssertTrue(harness.transcriber.sampleCounts.allSatisfy { $0 > 0 }, "a dictation lost its samples")
        XCTAssertEqual(firstDevice.counts.closed, 1)
        XCTAssertEqual(secondDevice.counts.closed, 1)
    }

    // MARK: - Rebinding, faults and the toggle

    /// Rebinding while the old key holds a recording settles it: that recording ends once and is processed, the old
    /// key's release (which no longer matches) ends nothing, and the new key starts the next recording.
    func testRebindingWhileTheKeyIsHeldEndsThatRecordingOnceAndTheNewKeyWorks() async throws {
        let harness = makeHarness()
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
        let ignoredBefore = harness.controller.checkpoints.ignoredReleases
        harness.release(oldKey)
        XCTAssertEqual(harness.controller.checkpoints.ignoredReleases, ignoredBefore + 1)
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
        let harness = makeHarness()

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
        let ignoredBefore = harness.controller.checkpoints.ignoredCaptureEvents
        harness.capture.post(.level(AudioLevelMeasurement(peakAmplitude: 0.9, rmsAmplitude: 0.5)), for: first)
        harness.capture.post(.stopRequested(.deviceChanged), for: first)
        await waitUntil("both late events are handled") {
            harness.controller.checkpoints.ignoredCaptureEvents == ignoredBefore + 2
        }

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

    /// A held key never stops on silence, and neither does the toggle key (Caps Lock, the default) unless the user
    /// opted in, as on Windows (`AppSettings.AutoStopOnSilence`, off by default), with the choice read at each press.
    /// The tray's test dictation always stops on silence. A toggle's recording that ended some other way than by the
    /// key tells the key's listener, so its next tap is not taken for the toggle's second tap.
    func testTheStopPolicyFollowsTheBindingThatFiredAndTheOptIn() async throws {
        let optIn = LockedValue<Bool>()
        let harness = makeHarness(
            configuration: DictationController.Configuration(toggleKeyStopsOnSilence: { optIn.value ?? false }))

        let held = try await harness.pressAdmitted(DictationHarness.holdKey)
        XCTAssertEqual(harness.capture.starts.last?.policy.stopsOnSilence, false)
        XCTAssertEqual(harness.capture.starts.last?.policy.maximumDuration, CaptureStopPolicy.defaultMaximumDuration)
        await harness.waitUntilLive()
        harness.release(DictationHarness.holdKey)
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.capture.stopCount(for: held), 1)

        // Caps Lock as installed: no silence stop, and its second tap ends the recording with nothing to settle.
        _ = try await harness.pressAdmitted(DictationHarness.toggleKey)
        XCTAssertEqual(harness.capture.starts.last?.policy.stopsOnSilence, false, "a toggle stops on silence unasked")
        XCTAssertEqual(harness.capture.starts.last?.policy.maximumDuration, CaptureStopPolicy.defaultMaximumDuration)
        await harness.waitUntilLive()
        harness.release(DictationHarness.toggleKey)
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.triggers.cancelledToggles, 0)

        // Opted in: the next press stops on silence, and a silence stop settles the key's toggle.
        optIn.set(true)
        let toggled = try await harness.pressAdmitted(DictationHarness.toggleKey)
        XCTAssertEqual(harness.capture.starts.last?.policy.stopsOnSilence, true)
        await harness.waitUntilLive()
        let silence = SilenceStopDetail(heardSpeech: true, peakLevel: 0.3, noiseFloor: 0.001, voiceThreshold: 0.01)
        harness.capture.post(.stopRequested(.silence(silence)), for: toggled)
        await waitUntil("the silence stop") { harness.controller.currentRecording == nil }
        XCTAssertEqual(harness.triggers.settledToggles, [DictationHarness.toggleKey])
        await harness.waitUntilProcessed()

        // A held key ignores the choice.
        _ = try await harness.pressAdmitted(DictationHarness.holdKey)
        XCTAssertEqual(harness.capture.starts.last?.policy.stopsOnSilence, false)
        await harness.waitUntilLive()
        harness.release(DictationHarness.holdKey)
        await harness.waitUntilProcessed()

        // The tray's test dictation stops on silence whatever the choice, and settles no key.
        optIn.set(false)
        harness.controller.toggleMenuDictation()
        await waitUntil("the menu recording opens") { harness.controller.isRecordingLive }
        XCTAssertEqual(harness.capture.starts.last?.policy.stopsOnSilence, true)
        harness.controller.toggleMenuDictation()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.reports.latest?.stopReason, .menu)
        XCTAssertEqual(harness.triggers.cancelledToggles, 1, "the tray's recording settled the key's toggle")
    }

    /// Caps Lock through a real `HotkeyManager`: the tap that turned the light on started a recording whose
    /// microphone failed to open. The toggle is settled, so the light is on with nothing recording: after the
    /// microphone recovers, the next tap turns the light off and starts nothing, the tap after it starts exactly one
    /// recording, and the one after that ends it.
    func testACapsLockTapWhoseMicrophoneFailedToOpenLeavesTheKeyFreeForTheNextTap() async throws {
        let harness = makeHarness()
        let manager = harness.wireHotkeyManager(keyCode: 57)
        harness.capture.openError = AudioCaptureEngineError.missingInputNodeFormat

        manager.receive(HotkeyTestEvents.capsLock(on: true))
        await waitUntil("the failed open is handled") { harness.controller.checkpoints.openAnswers == 1 }
        XCTAssertNil(harness.controller.currentRecording)
        XCTAssertFalse(manager.isEngaged, "the toggle stayed engaged after its recording failed to open")

        harness.capture.openError = nil
        manager.receive(HotkeyTestEvents.capsLock(on: false))
        XCTAssertFalse(manager.isEngaged, "the tap turning the light off started a recording")
        XCTAssertNil(harness.controller.currentRecording)
        manager.receive(HotkeyTestEvents.capsLock(on: true))
        XCTAssertEqual(harness.capture.starts.count, 1)
        await waitUntil("the next tap asks for the microphone") { harness.capture.starts.count == 2 }
        await harness.waitUntilLive()
        XCTAssertTrue(manager.isEngaged)

        manager.receive(HotkeyTestEvents.capsLock(on: false))
        await harness.waitUntilProcessed()
        XCTAssertNil(harness.controller.currentRecording)
        XCTAssertEqual(harness.capture.starts.count, 2, "one tap started more than one recording")
        XCTAssertEqual(harness.fakeInjector.deliveries.count, 1)
        XCTAssertEqual(harness.reports.latest?.stopReason, .hotkeyReleased)
    }

    /// The same when the engine reports that the open was stopped from elsewhere (`stoppedWhileOpening` for a
    /// recording this controller never stopped): the toggle is settled too.
    func testAToggleWhoseOpenWasStoppedFromElsewhereIsSettled() async throws {
        let harness = makeHarness()
        let manager = harness.wireHotkeyManager(keyCode: 57)
        harness.capture.holdsOpens = true

        manager.receive(HotkeyTestEvents.capsLock(on: true))
        await waitUntil("the open is pending") { harness.capture.pendingOpens == 1 }
        let first = try XCTUnwrap(harness.controller.currentRecording)
        harness.capture.completeOpen(first, .stoppedWhileOpening)
        await waitUntil("the open's answer is handled") { harness.controller.checkpoints.openAnswers == 1 }
        XCTAssertNil(harness.controller.currentRecording)
        XCTAssertFalse(manager.isEngaged)

        harness.capture.holdsOpens = false
        manager.receive(HotkeyTestEvents.capsLock(on: false))
        XCTAssertFalse(manager.isEngaged, "the tap turning the light off started a recording")
        manager.receive(HotkeyTestEvents.capsLock(on: true))
        await harness.waitUntilLive()
        XCTAssertNotEqual(harness.controller.currentRecording, first)
    }

    // MARK: - The duration deadline

    /// The deadline is the controller's own and runs on its clock, so a device that delivers no buffers at all (the
    /// case the engine's sample-count ceiling cannot see) still ends at the ceiling, exactly once.
    func testTheDurationDeadlineEndsARecordingThatReceivesNoBuffersExactlyOnce() async throws {
        let harness = makeHarness()

        let id = try await harness.pressAdmitted(DictationHarness.toggleKey)
        await harness.waitUntilLive()
        await waitUntil("the deadline is armed") { harness.clock.sleeperCount == 1 }
        harness.clock.advance(by: .seconds(10 * 60) - .milliseconds(1))
        // Not due yet: the deadline is still asleep.
        XCTAssertEqual(harness.clock.sleeperCount, 1)
        XCTAssertTrue(harness.controller.isRecordingLive, "stopped before its ceiling")
        XCTAssertEqual(harness.capture.stopCount(for: id), 0)

        harness.clock.advance(by: .milliseconds(1))
        await waitUntil("the ceiling stops the recording") { harness.controller.currentRecording == nil }
        await harness.waitUntilProcessed()
        harness.clock.advance(by: .seconds(10 * 60))

        XCTAssertEqual(harness.capture.stopCount(for: id), 1)
        XCTAssertEqual(harness.controller.checkpoints.ignoredDeadlines, 0)
        XCTAssertEqual(harness.reports.latest?.stopReason, .durationLimit)
        XCTAssertEqual(harness.triggers.cancelledToggles, 1)
        XCTAssertEqual(harness.presenter.noticesShown().last, .durationLimitReached)
    }

    /// Windows' `A_late_ceiling_tick_queued_for_one_recording_never_ends_the_next`: the first recording's deadline
    /// fell due and its tick was already queued when the key stopped it and the next recording started. The stale
    /// tick ends nothing, and the next recording ends at its own ceiling.
    func testAStaleDeadlineNeverEndsTheNextRecording() async throws {
        let harness = makeHarness()

        let first = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        await waitUntil("the first deadline is armed") { harness.clock.sleeperCount == 1 }
        // Due: the first recording's tick is queued on the main actor but has not run yet.
        harness.clock.advance(by: .seconds(10 * 60))
        harness.release()
        XCTAssertTrue(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        let second = try XCTUnwrap(harness.controller.currentRecording)
        XCTAssertNotEqual(first, second)
        await waitUntil("the first recording's tick has run") { harness.controller.checkpoints.ignoredDeadlines == 1 }
        await harness.waitUntilLive()
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.controller.currentRecording, second, "the first recording's tick ended the second")
        XCTAssertTrue(harness.controller.isRecordingLive)
        XCTAssertEqual(harness.capture.stopCount(for: second), 0)

        // A tick for the second recording that arrives before it has run its whole ceiling ends nothing either.
        harness.controller.durationDeadlineReached(for: second)
        XCTAssertEqual(harness.controller.checkpoints.ignoredDeadlines, 2)
        XCTAssertTrue(harness.controller.isRecordingLive)

        await waitUntil("the second deadline is armed") { harness.clock.sleeperCount == 1 }
        harness.clock.advance(by: .seconds(10 * 60))
        await waitUntil("the second recording's own ceiling") { harness.controller.currentRecording == nil }
        XCTAssertEqual(harness.capture.stopCount(for: second), 1)
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.reports.latest?.stopReason, .durationLimit)
    }

    // MARK: - Presentation

    /// A recording takes the pill from the notice on it and cancels that notice's timed end: once the recording is
    /// live only its own deadline sleeps, and moving the clock past the old notice's time changes nothing.
    func testANoticesLateEndNeverHidesTheRecordingThatStartedAfterIt() async throws {
        let harness = makeHarness()
        harness.fakeInjector.result = InjectionResult(delivery: .targetChanged)

        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.lastOverlay, .notice(.textKept))
        await waitUntil("the notice's end is scheduled") { harness.clock.sleeperCount == 1 }

        _ = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        XCTAssertTrue(isListening(harness.lastOverlay))
        let recordingRevision = try XCTUnwrap(harness.presenter.last?.revision)
        // The only sleeper left is the new recording's ten-minute deadline: the old notice's end was cancelled, so
        // moving the clock five seconds wakes nothing at all.
        await waitUntil("the old notice's end is cancelled and only the deadline sleeps") {
            harness.clock.sleeperCount == 1
                && harness.clock.sleeperDeadlines.allSatisfy { harness.clock.now.duration(to: $0) > .seconds(60) }
        }
        XCTAssertNil(harness.shownNotice)

        harness.clock.advance(by: .seconds(5))

        XCTAssertTrue(isListening(harness.lastOverlay), "the old notice's end hid the new recording's pill")
        XCTAssertEqual(harness.presenter.last?.revision, recordingRevision)
        XCTAssertTrue(harness.controller.isRecordingLive)
    }

    /// A notice's end that was already due when newer feedback replaced it takes nothing down: it is tagged with the
    /// revision that showed its own notice, and the newer notice has another.
    func testANoticesLateEndNeverTakesDownANewerNotice() async throws {
        var configuration = DictationController.Configuration()
        configuration.maximumDictationsInProcessing = 1
        let harness = makeHarness(configuration: configuration)
        let gate = DictationGate<String>()
        harness.transcriber.steps = [.gate(gate)]
        await harness.dictate()
        await waitUntil("the recognizer is asked") { gate.waitingCount == 1 }

        XCTAssertFalse(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        XCTAssertEqual(harness.lastOverlay, .notice(.stillProcessing))
        await waitUntil("the first notice's end is scheduled") { harness.clock.sleeperCount == 1 }
        // The first notice's end falls due and waits on the main actor; a second refusal refreshes the notice before
        // it runs.
        harness.clock.advance(by: configuration.noticeDuration)
        XCTAssertFalse(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        let newer = try XCTUnwrap(harness.presenter.last)
        await waitUntil("the first notice's end has run") { harness.controller.checkpoints.ignoredNoticeEnds == 1 }

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
        let harness = makeHarness()
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
        await waitUntil("the stale end has run") { harness.controller.checkpoints.ignoredNoticeEnds == 1 }
        XCTAssertEqual(harness.presenter.presentations.count, presentedAtPress, "the stale end presented a change")

        await waitUntil("the open is pending") { harness.capture.pendingOpens == 1 }
        harness.capture.completeOpen(id, .live)
        await harness.waitUntilLive()

        XCTAssertTrue(isListening(harness.lastOverlay))
        let revisions = harness.presenter.presentations.map(\.revision)
        XCTAssertEqual(revisions, revisions.sorted(), "revisions go up with every change")
        XCTAssertEqual(Set(revisions).count, revisions.count)
    }

    /// A notice runs its course when nothing replaces it.
    func testANoticeEndsAfterItsTimeWhenNothingReplacesIt() async {
        let harness = makeHarness()
        harness.fakeInjector.result = InjectionResult(delivery: .targetChanged)

        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.lastOverlay, .notice(.textKept))
        await waitUntil("the notice's end is scheduled") { harness.clock.sleeperCount == 1 }
        harness.clock.advance(by: DictationController.Configuration().noticeDuration)
        await waitUntil("the notice ends") { harness.lastOverlay == .hidden }
    }

    // MARK: - Who owns the pill

    /// Dictation A's cleanup fails while a newer recording B owns the pill. The pill cannot say "raw text used" now,
    /// so a notification says it instead, and the pill does not say it later as well.
    func testACleanupFallbackWhileANewerRecordingOwnsThePillIsPostedAsANotification() async throws {
        let harness = makeHarness()
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        let reply = DictationGate<String>()
        provider.reply = { _ in try await reply.wait() }

        await harness.dictate()
        await waitUntil("A's cleanup is asked") { reply.waitingCount == 1 }
        _ = try await harness.pressAdmitted()
        await harness.waitUntilLive()

        reply.fail(DictationTestFailure(code: 5))
        await waitUntil("A is delivered raw") { harness.fakeInjector.deliveries.count == 1 }
        XCTAssertEqual(harness.fakeInjector.texts, ["hello from the recognizer"])
        XCTAssertEqual(harness.notifier.kinds, [.cleanupFellBack])
        XCTAssertTrue(isListening(harness.lastOverlay), "A's fallback covered B's meter")
        XCTAssertTrue(harness.controller.noticeSchedule.waiting.isEmpty, "the fallback also waits for the pill")

        // B is dictated without cleanup, so the only fallback is A's.
        harness.cleanup.isEnabled = false
        harness.release()
        await harness.waitUntilProcessed()
        XCTAssertFalse(harness.presenter.noticesShown().contains(.cleanupFellBack), "said twice")
    }

    /// The fallback notification goes out before the dictation is delivered, so it says nothing about insertion. Here
    /// A's delivery is still held when the notification is posted, and is then refused: only the delivery's own
    /// notice says the text did not go in.
    func testTheCleanupFallbackNotificationMakesNoClaimAboutDelivery() async throws {
        let harness = makeHarness()
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        let reply = DictationGate<String>()
        provider.reply = { _ in try await reply.wait() }
        let delivery = DictationGate<Void>()
        harness.fakeInjector.holdNext = delivery
        harness.fakeInjector.result = InjectionResult(delivery: .targetChanged)

        await harness.dictate()
        await waitUntil("A's cleanup is asked") { reply.waitingCount == 1 }
        _ = try await harness.pressAdmitted()
        await harness.waitUntilLive()
        reply.fail(DictationTestFailure(code: 8))
        await waitUntil("A's delivery is held") { delivery.waitingCount == 1 }

        XCTAssertEqual(harness.notifier.kinds, [.cleanupFellBack])
        let fallback = try XCTUnwrap(harness.notifier.notices.first)
        let said = (fallback.title + " " + fallback.body).lowercased()
        for claim in ["insert", "typed", "pasted", "went in", "deliver", "was used", "used instead", "as recognized"] {
            XCTAssertFalse(said.contains(claim), "the fallback notification claims \"\(claim)\"")
        }

        delivery.open(())
        await waitUntil("A's refused delivery is reported") { harness.notifier.notices.count == 2 }
        XCTAssertEqual(harness.notifier.kinds, [.cleanupFellBack, .notInserted])
        harness.cleanup.isEnabled = false
        harness.fakeInjector.result = InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored)
        harness.release()
        await harness.waitUntilProcessed()
    }

    /// The same for a failed recognition, which the generic branch used to drop without a word.
    func testAFailedRecognitionWhileANewerRecordingOwnsThePillIsPostedAsANotification() async throws {
        let harness = makeHarness()
        let recognizer = DictationGate<String>()
        harness.transcriber.steps = [.gate(recognizer)]

        await harness.dictate()
        await waitUntil("A's recognizer runs") { recognizer.waitingCount == 1 }
        _ = try await harness.pressAdmitted()
        await harness.waitUntilLive()

        recognizer.fail(TranscriptionError.exitCode(3))
        await waitUntil("A's failure is handled") { harness.notifier.notices.count == 1 }
        XCTAssertEqual(harness.notifier.kinds, [.transcriptionFailed])
        XCTAssertTrue(isListening(harness.lastOverlay))

        harness.release()
        await harness.waitUntilProcessed()
        XCTAssertFalse(harness.presenter.noticesShown().contains(.transcriptionFailed), "said twice")
    }

    /// Dictation A's text is kept, not inserted, while a newer recording B owns the pill. The notification goes out at
    /// once; the pill's notice waits, and shows once B's recording ends: a newer recording does not make A's failure
    /// obsolete.
    func testAnOlderDictationsKeptTextWaitsForTheNewerRecordingAndThenShows() async throws {
        let harness = makeHarness()
        let recognizer = DictationGate<String>()
        harness.transcriber.steps = [.gate(recognizer)]
        harness.fakeInjector.result = InjectionResult(delivery: .targetChanged)

        let first = try await harness.dictateAdmitted()
        await waitUntil("A's recognizer runs") { recognizer.waitingCount == 1 }
        _ = try await harness.pressAdmitted()
        await harness.waitUntilLive()

        recognizer.open("words for A")
        await waitUntil("A's outcome waits for the pill") { harness.controller.noticeSchedule.waiting.count == 1 }
        XCTAssertEqual(harness.notifier.kinds, [.notInserted])
        XCTAssertTrue(isListening(harness.lastOverlay))

        harness.fakeInjector.result = InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored)
        harness.release()
        XCTAssertEqual(harness.lastOverlay, .notice(.textKept))
        XCTAssertEqual(harness.shownNotice?.source, first)
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.presenter.noticesShown(), [.textKept])
    }

    /// Dictation A waits for cleanup; recording B then fails to open its microphone and the pill says "Microphone
    /// access needed"; A's cleanup fails after that. A's older outcome never replaces B's newer, actionable failure on
    /// the pill; a notification says A's instead.
    func testAnOlderCleanupFailureNeverReplacesANewerMicrophoneFailure() async throws {
        let harness = makeHarness()
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        let reply = DictationGate<String>()
        provider.reply = { _ in try await reply.wait() }

        await harness.dictate()
        await waitUntil("A's cleanup is asked") { reply.waitingCount == 1 }
        harness.capture.openError = AudioCaptureEngineError.microphoneNotAuthorized(.denied)
        let second = try await harness.pressAdmitted()
        await waitUntil("B's failed open is handled") { harness.controller.checkpoints.openAnswers == 2 }
        XCTAssertEqual(harness.lastOverlay, .notice(.microphoneAccessNeeded))
        let failureToken = try XCTUnwrap(harness.controller.noticeSchedule.shown?.token)

        reply.fail(DictationTestFailure(code: 6))
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.lastOverlay, .notice(.microphoneAccessNeeded), "A's fallback replaced B's failure")
        XCTAssertEqual(harness.shownNotice?.source, second)
        XCTAssertEqual(harness.controller.noticeSchedule.shown?.token, failureToken)
        XCTAssertEqual(harness.notifier.kinds, [.microphoneAccessNeeded, .cleanupFellBack])
        XCTAssertEqual(harness.presenter.noticesShown(), [.microphoneAccessNeeded])
    }

    /// Clear history makes a notice about kept text obsolete: on the pill it leaves, and one that arrives for text kept
    /// before the Clear is neither shown nor posted.
    func testClearHistoryRemovesANoticeThatOffersClearedText() async throws {
        let harness = makeHarness()
        harness.fakeInjector.result = InjectionResult(delivery: .targetChanged)

        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.lastOverlay, .notice(.textKept))
        harness.recovery.removeAll()
        harness.controller.recoveryWasCleared()
        XCTAssertEqual(harness.lastOverlay, .hidden)
        XCTAssertNil(harness.shownNotice)

        // Kept before the Clear, announced after it.
        let delivery = DictationGate<Void>()
        harness.fakeInjector.holdNext = delivery
        await harness.dictate()
        await waitUntil("the delivery is under way") { delivery.waitingCount == 1 }
        harness.recovery.removeAll()
        harness.controller.recoveryWasCleared()
        let rejectedBefore = harness.controller.checkpoints.rejectedNotices
        delivery.open(())
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.controller.checkpoints.rejectedNotices, rejectedBefore + 1)
        XCTAssertEqual(harness.notifier.kinds, [.notInserted], "a notice offered text Clear history removed")
        XCTAssertEqual(harness.presenter.noticesShown(), [.textKept])
        XCTAssertEqual(harness.history.records.count, 2, "Clear keeps what was still being processed")
    }

    // MARK: - Leases and limits

    /// The foreground lease runs from admission to the end of processing, on every way a dictation ends.
    func testTheForegroundLeaseIsHeldFromAdmissionUntilProcessingEnds() async throws {
        let harness = makeHarness()
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
        let harness = makeHarness(configuration: configuration)
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
