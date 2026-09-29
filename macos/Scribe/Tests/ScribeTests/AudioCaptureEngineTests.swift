import AVFoundation
import Darwin
import XCTest
import os

@testable import Scribe

/// Hands the engine one scripted device per capture, in order, and remembers which it handed out.
final class CaptureTestDeviceFactory: Sendable {
    private let queued: OSAllocatedUnfairLock<[CaptureTestDevice]>
    private let handedOut = OSAllocatedUnfairLock<[CaptureTestDevice]>(initialState: [])

    init(_ devices: [CaptureTestDevice]) {
        queued = OSAllocatedUnfairLock(initialState: devices)
    }

    var make: @Sendable () -> any CaptureDevice {
        { [queued, handedOut] in
            let device = queued.withLock { devices -> CaptureTestDevice in
                devices.isEmpty ? CaptureTestDevice() : devices.removeFirst()
            }
            handedOut.withLock { $0.append(device) }
            return device
        }
    }

    var made: [CaptureTestDevice] {
        handedOut.withLock { $0 }
    }
}

/// The engine with a scripted device: buffers arrive on threads the tests choose while the owner stops on the
/// main actor, so ownership, generations and the control queue are exercised without a microphone.
final class AudioCaptureEngineTests: XCTestCase {
    private func buffer(_ value: Float, frames: Int = 160, sampleRate: Double = 16_000) -> AVAudioPCMBuffer {
        AudioTestBuffers.mono([Float](repeating: value, count: frames), sampleRate: sampleRate)
    }

    func testAStartedRecordingReturnsWhatWasDeliveredWhenStoppedAndClosesItsDevice() async throws {
        let device = CaptureTestDevice()
        let engine = AudioCaptureEngine(makeDevice: { device })
        let owner = RecordingID(rawValue: 1)

        let outcome = try await engine.start(owner: owner, policy: .hold(), events: { _ in })
        XCTAssertEqual(outcome, .live)
        XCTAssertTrue(engine.isCapturing)
        XCTAssertEqual(engine.activeRecording, owner)
        device.deliver(buffer(0.25))
        device.deliver(buffer(0.5))

        let captured = try XCTUnwrap(engine.stop(owner: owner))
        await engine.waitUntilIdle()

        XCTAssertEqual(captured.owner, owner)
        XCTAssertEqual(captured.samples, [Float](repeating: 0.25, count: 160) + [Float](repeating: 0.5, count: 160))
        XCTAssertEqual(captured.summary.ending, .stoppedByOwner)
        XCTAssertFalse(engine.isCapturing)
        XCTAssertNil(engine.stop(owner: owner))
        XCTAssertEqual(device.counts.prepared, 1)
        XCTAssertEqual(device.counts.started, 1)
        XCTAssertEqual(device.counts.closed, 1)
    }

    /// The race the old capture lost: the tap appended samples on its own thread while the main actor copied
    /// and cleared them. Every iteration stops at an arbitrary point after the twentieth buffer; whatever the
    /// interleaving, the capture holds whole buffers, in order, from the first, each once, and nothing after.
    @MainActor
    func testAStopOnTheMainActorWhileBuffersArriveKeepsAWholePrefixOfBuffersAndNothingAfter() async throws {
        let bufferCount = 200
        let framesPerBuffer = 160
        for iteration in 1...40 {
            let late = CaptureTestDevice()
            let next = CaptureTestDevice()
            let factory = CaptureTestDeviceFactory([late, next])
            let engine = AudioCaptureEngine(makeDevice: factory.make)
            let owner = RecordingID(rawValue: UInt64(iteration * 2))
            let log = CaptureEventLog()
            let outcome = try await engine.start(owner: owner, policy: .hold(maximumDuration: nil), events: log.sink)
            XCTAssertEqual(outcome, .live)

            let reachedTwenty = AudioTestSignalLatch()
            let pushedAll = AudioTestSignalLatch()
            DispatchQueue.global(qos: .userInitiated).async {
                for index in 0..<bufferCount {
                    late.deliver(
                        AudioTestBuffers.mono(
                            [Float](repeating: Float(index + 1), count: framesPerBuffer), sampleRate: 16_000))
                    if index == 19 {
                        reachedTwenty.signal()
                    }
                }
                pushedAll.signal()
            }

            let reached = await reachedTwenty.wait()
            XCTAssertTrue(reached)
            let captured = try XCTUnwrap(engine.stop(owner: owner))
            let eventsAtStop = log.all.count
            let finished = await pushedAll.wait()
            XCTAssertTrue(finished)

            let samples = captured.samples
            XCTAssertEqual(samples.count % framesPerBuffer, 0, "iteration \(iteration)")
            let kept = samples.count / framesPerBuffer
            XCTAssertGreaterThanOrEqual(kept, 20, "iteration \(iteration)")
            XCTAssertLessThanOrEqual(kept, bufferCount, "iteration \(iteration)")
            for index in 0..<kept {
                let slice = samples[(index * framesPerBuffer)..<((index + 1) * framesPerBuffer)]
                XCTAssertTrue(slice.allSatisfy { $0 == Float(index + 1) }, "buffer \(index), iteration \(iteration)")
            }
            XCTAssertEqual(captured.summary.acceptedBufferCount, kept)
            // An event the tap computed from a buffer accepted before the stop can land just after it, tagged
            // with the stopped recording; nothing from a later buffer ever does.
            let lateEvents = log.all.dropFirst(eventsAtStop)
            XCTAssertLessThanOrEqual(lateEvents.count, 1, "iteration \(iteration)")
            XCTAssertTrue(lateEvents.allSatisfy { $0.owner == owner })

            // The late device's buffers can never reach the next recording.
            let nextOwner = RecordingID(rawValue: UInt64(iteration * 2 + 1))
            _ = try await engine.start(owner: nextOwner, policy: .hold(maximumDuration: nil), events: { _ in })
            late.deliver(AudioTestBuffers.mono([Float](repeating: 9_999, count: framesPerBuffer), sampleRate: 16_000))
            next.deliver(AudioTestBuffers.mono([Float](repeating: -1, count: framesPerBuffer), sampleRate: 16_000))
            let nextCapture = try XCTUnwrap(engine.stop(owner: nextOwner))
            XCTAssertEqual(nextCapture.samples, [Float](repeating: -1, count: framesPerBuffer))
            await engine.waitUntilIdle()
            XCTAssertEqual(late.counts.closed, 1)
            XCTAssertEqual(next.counts.closed, 1)
        }
    }

    func testAStopThatArrivesBeforeItsStartOpensNothingAndRefusesEarlierRecordingsToo() async throws {
        let factory = CaptureTestDeviceFactory([])
        let engine = AudioCaptureEngine(makeDevice: factory.make)

        XCTAssertNil(engine.stop(owner: RecordingID(rawValue: 5)))
        let fifth = try await engine.start(owner: RecordingID(rawValue: 5), policy: .hold(), events: { _ in })
        let fourth = try await engine.start(owner: RecordingID(rawValue: 4), policy: .hold(), events: { _ in })
        XCTAssertEqual(fifth, .stoppedBeforeOpen)
        XCTAssertEqual(fourth, .stoppedBeforeOpen)
        XCTAssertTrue(factory.made.isEmpty)
        XCTAssertFalse(engine.isCapturing)

        let sixth = try await engine.start(owner: RecordingID(rawValue: 6), policy: .hold(), events: { _ in })
        XCTAssertEqual(sixth, .live)
        engine.stop(owner: RecordingID(rawValue: 6))
    }

    func testAStopWhileTheMicrophoneOpensHandsOverAnEmptyCaptureAndClosesTheDeviceRightAfter() async throws {
        var configuration = CaptureTestDevice.Configuration()
        configuration.holdsPrepare = true
        let journal = CaptureDeviceJournal()
        let device = CaptureTestDevice(configuration, journal: journal)
        let engine = AudioCaptureEngine(makeDevice: { device })
        let owner = RecordingID(rawValue: 7)

        let start = Task {
            try await engine.start(owner: owner, policy: .hold(), events: { _ in })
        }
        let entered = await device.prepareEntered.wait()
        XCTAssertTrue(entered)

        let captured = try XCTUnwrap(engine.stop(owner: owner))
        device.releasePrepare()
        let outcome = try await start.value
        await engine.waitUntilIdle()

        XCTAssertEqual(outcome, .stoppedWhileOpening)
        XCTAssertTrue(captured.samples.isEmpty)
        XCTAssertEqual(captured.summary.ending, .stoppedByOwner)
        // The stop's close waits on the control queue behind the open, so it closes the engine the open started.
        XCTAssertEqual(journal.all, ["device prepare", "device start", "device close"])
        XCTAssertEqual(device.counts.started, 1)
        XCTAssertEqual(device.counts.closed, 1)
        XCTAssertFalse(engine.isCapturing)
        XCTAssertFalse(device.isRunning)
    }

    /// A start admitted while another thread's stop is still sealing the previous recording, held here inside its
    /// resampler flush, where a stop spends the time between releasing the microphone and queueing its device's
    /// close. The new device must not open until the old one has closed, and each device closes exactly once.
    func testAStartAdmittedWhileTheLastStopStillSealsOpensOnlyOnceTheOldDeviceHasClosed() async throws {
        let journal = CaptureDeviceJournal()
        var resampled = CaptureTestDevice.Configuration()
        resampled.sampleRate = 48_000
        let first = CaptureTestDevice(resampled, name: "first", journal: journal)
        let second = CaptureTestDevice(name: "second", journal: journal)
        let flushEntered = AudioTestSignalLatch()
        let releaseFlush = DispatchSemaphore(value: 0)
        let flushes = OSAllocatedUnfairLock(initialState: 0)
        let engine = AudioCaptureEngine(
            resamplerTailFlush: { converter, target, samples in
                if flushes.withLock({ count -> Bool in
                    count += 1
                    return count == 1
                }) {
                    flushEntered.signal()
                    _ = releaseFlush.wait(timeout: .now() + .seconds(30))
                }
                return CaptureProcessor.flushResamplerTail(converter, target, &samples)
            },
            makeDevice: CaptureTestDeviceFactory([first, second]).make)
        let firstOwner = RecordingID(rawValue: 1)
        let secondOwner = RecordingID(rawValue: 2)
        let opened = try await engine.start(owner: firstOwner, policy: .hold(), events: { _ in })
        XCTAssertEqual(opened, .live)
        first.deliver(buffer(0.25, frames: 4_800, sampleRate: 48_000))

        let firstCapture = OSAllocatedUnfairLock<CapturedAudio?>(initialState: nil)
        let stopReturned = AudioTestSignalLatch()
        DispatchQueue.global(qos: .userInitiated).async {
            let captured = engine.stop(owner: firstOwner)
            firstCapture.withLock { $0 = captured }
            stopReturned.signal()
        }
        let entered = await flushEntered.wait()
        XCTAssertTrue(entered, "the stop never reached its resampler flush")

        let next = try await engine.start(owner: secondOwner, policy: .hold(), events: { _ in })
        releaseFlush.signal()
        let returned = await stopReturned.wait()
        XCTAssertTrue(returned)
        await engine.waitUntilIdle()

        XCTAssertEqual(next, .live)
        XCTAssertEqual(journal.all, ["first prepare", "first start", "first close", "second prepare", "second start"])
        XCTAssertEqual(first.counts.closed, 1)
        XCTAssertEqual(second.counts.closed, 0)
        XCTAssertFalse(first.isRunning)
        let sealed = try XCTUnwrap(firstCapture.withLock { $0 })
        XCTAssertEqual(sealed.summary.ending, .stoppedByOwner)
        XCTAssertFalse(sealed.samples.isEmpty)

        engine.stop(owner: secondOwner)
        await engine.waitUntilIdle()
        XCTAssertEqual(first.counts.closed, 1)
        XCTAssertEqual(second.counts.closed, 1)
    }

    /// A recording stopped while an earlier one still holds the control queue never opens a device at all.
    func testAStopForARecordingWhoseOpenIsStillQueuedMeansItsOpenDoesNothing() async throws {
        var configuration = CaptureTestDevice.Configuration()
        configuration.holdsPrepare = true
        let blocking = CaptureTestDevice(configuration)
        let factory = CaptureTestDeviceFactory([blocking])
        let engine = AudioCaptureEngine(makeDevice: factory.make)
        let first = RecordingID(rawValue: 10)
        let second = RecordingID(rawValue: 11)

        let firstStart = Task {
            try await engine.start(owner: first, policy: .hold(), events: { _ in })
        }
        let entered = await blocking.prepareEntered.wait()
        XCTAssertTrue(entered)
        _ = engine.stop(owner: first)
        let secondStart = Task {
            try await engine.start(owner: second, policy: .hold(), events: { _ in })
        }
        let admitted = await Self.eventually { engine.activeRecording == second }
        XCTAssertTrue(admitted)
        let secondCapture = try XCTUnwrap(engine.stop(owner: second))
        blocking.releasePrepare()

        let firstOutcome = try await firstStart.value
        let secondOutcome = try await secondStart.value
        await engine.waitUntilIdle()

        XCTAssertEqual(firstOutcome, .stoppedWhileOpening)
        XCTAssertEqual(secondOutcome, .stoppedBeforeOpen)
        XCTAssertTrue(secondCapture.samples.isEmpty)
        XCTAssertEqual(factory.made.count, 1)
        XCTAssertEqual(blocking.counts.closed, 1)
    }

    func testAFailedOpenLeavesNothingOpenAndTheNextRecordingStarts() async throws {
        var failing = CaptureTestDevice.Configuration()
        failing.startError = AudioCaptureEngineError.engineStartFailed(underlying: NSError(domain: "test", code: 1))
        let broken = CaptureTestDevice(failing)
        let working = CaptureTestDevice()
        let engine = AudioCaptureEngine(makeDevice: CaptureTestDeviceFactory([broken, working]).make)

        do {
            _ = try await engine.start(owner: RecordingID(rawValue: 1), policy: .hold(), events: { _ in })
            XCTFail("the start should have thrown")
        } catch AudioCaptureEngineError.engineStartFailed {
        }
        XCTAssertFalse(engine.isCapturing)
        XCTAssertEqual(broken.counts.closed, 1)

        let outcome = try await engine.start(owner: RecordingID(rawValue: 2), policy: .hold(), events: { _ in })
        XCTAssertEqual(outcome, .live)
        working.deliver(buffer(0.5))
        XCTAssertEqual(engine.stop(owner: RecordingID(rawValue: 2))?.samples.count, 160)
    }

    func testASecondRecordingIsRefusedWhileTheFirstHoldsTheMicrophone() async throws {
        let engine = AudioCaptureEngine(makeDevice: CaptureTestDeviceFactory([]).make)
        _ = try await engine.start(owner: RecordingID(rawValue: 1), policy: .hold(), events: { _ in })

        do {
            _ = try await engine.start(owner: RecordingID(rawValue: 2), policy: .hold(), events: { _ in })
            XCTFail("the second start should have thrown")
        } catch AudioCaptureEngineError.alreadyCapturing {
        }
        XCTAssertEqual(engine.activeRecording, RecordingID(rawValue: 1))
        XCTAssertNotNil(engine.stop(owner: RecordingID(rawValue: 1)))
    }

    func testAStopThatNamesAnotherRecordingTouchesNothing() async throws {
        let device = CaptureTestDevice()
        let engine = AudioCaptureEngine(makeDevice: { device })
        let owner = RecordingID(rawValue: 20)
        _ = try await engine.start(owner: owner, policy: .hold(), events: { _ in })

        XCTAssertNil(engine.stop(owner: RecordingID(rawValue: 19)))
        device.deliver(buffer(0.5))

        XCTAssertTrue(engine.isCapturing)
        XCTAssertEqual(engine.stop(owner: owner)?.samples.count, 160)
    }

    // MARK: - Configuration changes

    func testAConfigurationChangeThatStopsTheDeviceEndsTheRecordingOnceAndKeepsItsAudio() async throws {
        let device = CaptureTestDevice()
        let engine = AudioCaptureEngine(makeDevice: { device })
        let owner = RecordingID(rawValue: 30)
        let log = CaptureEventLog()
        _ = try await engine.start(owner: owner, policy: .hold(), events: log.sink)
        for value: Float in [0.1, 0.2, 0.3] {
            device.deliver(buffer(value))
        }

        device.changeConfiguration(stopsDevice: true)
        device.changeConfiguration(stopsDevice: true)
        await engine.waitUntilIdle()
        device.deliver(buffer(0.9))

        XCTAssertEqual(log.stopRequests, [.deviceChanged])
        XCTAssertTrue(log.all.allSatisfy { $0.owner == owner })
        XCTAssertEqual(device.counts.closed, 1)
        XCTAssertTrue(engine.isCapturing, "the owner still has to collect the audio")
        let captured = try XCTUnwrap(engine.stop(owner: owner))
        XCTAssertEqual(captured.samples.count, 480)
        XCTAssertFalse(captured.samples.contains(0.9))
        XCTAssertEqual(captured.summary.ending, .endedItself(.deviceChanged))
    }

    /// Apple says the engine stops itself on a real change. A notification that leaves audio flowing must never
    /// end a good recording.
    func testAConfigurationChangeThatLeavesAudioFlowingKeepsTheRecording() async throws {
        let device = CaptureTestDevice()
        let scheduler = CaptureTestScheduler()
        let engine = AudioCaptureEngine(scheduleStallCheck: scheduler.schedule, makeDevice: { device })
        let owner = RecordingID(rawValue: 31)
        let log = CaptureEventLog()
        _ = try await engine.start(owner: owner, policy: .hold(), events: log.sink)
        device.deliver(buffer(0.1))

        device.changeConfiguration(stopsDevice: false)
        await engine.waitUntilIdle()
        XCTAssertEqual(scheduler.count, 1)
        device.deliver(buffer(0.2))
        scheduler.runAll()
        await engine.waitUntilIdle()

        XCTAssertTrue(log.stopRequests.isEmpty)
        XCTAssertEqual(device.counts.closed, 0)
        XCTAssertEqual(engine.stop(owner: owner)?.summary.ending, .stoppedByOwner)
    }

    /// A device that claims to run but delivers nothing after a change is the capture that "looks live but is
    /// dead"; the stall check ends it.
    func testAConfigurationChangeFollowedByNoAudioEndsTheRecordingAfterTheStallCheck() async throws {
        let device = CaptureTestDevice()
        let scheduler = CaptureTestScheduler()
        let engine = AudioCaptureEngine(scheduleStallCheck: scheduler.schedule, makeDevice: { device })
        let owner = RecordingID(rawValue: 32)
        let log = CaptureEventLog()
        _ = try await engine.start(owner: owner, policy: .hold(), events: log.sink)
        device.deliver(buffer(0.1))

        device.changeConfiguration(stopsDevice: false)
        await engine.waitUntilIdle()
        scheduler.runAll()
        await engine.waitUntilIdle()

        XCTAssertEqual(log.stopRequests, [.deviceChanged])
        XCTAssertEqual(device.counts.closed, 1)
        XCTAssertEqual(engine.stop(owner: owner)?.samples.count, 160)
    }

    func testAStallCheckForAnEarlierRecordingCannotEndALaterOne() async throws {
        let first = CaptureTestDevice()
        let second = CaptureTestDevice()
        let scheduler = CaptureTestScheduler()
        let engine = AudioCaptureEngine(
            scheduleStallCheck: scheduler.schedule, makeDevice: CaptureTestDeviceFactory([first, second]).make)
        _ = try await engine.start(owner: RecordingID(rawValue: 40), policy: .hold(), events: { _ in })
        first.changeConfiguration(stopsDevice: false)
        await engine.waitUntilIdle()
        engine.stop(owner: RecordingID(rawValue: 40))

        let log = CaptureEventLog()
        _ = try await engine.start(owner: RecordingID(rawValue: 41), policy: .hold(), events: log.sink)
        scheduler.runAll()
        await engine.waitUntilIdle()

        XCTAssertTrue(log.stopRequests.isEmpty)
        XCTAssertEqual(second.counts.closed, 0)
        XCTAssertNotNil(engine.stop(owner: RecordingID(rawValue: 41)))
    }

    // MARK: - Recordings that end themselves

    func testARecordingThatReachesItsCeilingClosesTheDeviceBeforeItsOwnerStops() async throws {
        let device = CaptureTestDevice()
        let engine = AudioCaptureEngine(makeDevice: { device })
        let owner = RecordingID(rawValue: 50)
        let log = CaptureEventLog()
        _ = try await engine.start(owner: owner, policy: .hold(maximumDuration: .milliseconds(500)), events: log.sink)

        for _ in 0..<10 {
            device.deliver(buffer(0.1, frames: 1_600))
        }
        await engine.waitUntilIdle()

        XCTAssertEqual(log.stopRequests, [.durationLimit])
        XCTAssertEqual(device.counts.closed, 1)
        XCTAssertTrue(engine.isCapturing)
        let captured = try XCTUnwrap(engine.stop(owner: owner))
        XCTAssertEqual(captured.samples.count, 8_000)
        XCTAssertEqual(captured.summary.ending, .endedItself(.durationLimit))
    }

    func testAToggleStopsOnSilenceFromTheBufferPathWithOneTaggedRequest() async throws {
        let device = CaptureTestDevice()
        let engine = AudioCaptureEngine(makeDevice: { device })
        let owner = RecordingID(rawValue: 60)
        let log = CaptureEventLog()
        _ = try await engine.start(owner: owner, policy: .toggle(), events: log.sink)

        for _ in 0..<150 {
            device.deliver(buffer(0, frames: 1_600))
        }

        let reasons = log.stopRequests
        XCTAssertEqual(reasons.count, 1)
        guard case .silence(let detail) = reasons.first else {
            return XCTFail("expected a silence stop, got \(reasons)")
        }
        XCTAssertFalse(detail.heardSpeech)
        XCTAssertTrue(log.all.allSatisfy { $0.owner == owner })
        XCTAssertEqual(engine.stop(owner: owner)?.samples.count, 160_000)
    }

    // MARK: - Helpers

    /// Waits for `condition`, checking after each yield; for state another task reaches without a callback.
    private static func eventually(_ condition: @Sendable () -> Bool) async -> Bool {
        for _ in 0..<100_000 {
            if condition() {
                return true
            }
            await Task.yield()
        }
        return condition()
    }
}

/// The relay the app uses to bring capture events to the main actor.
final class CaptureEventRelayTests: XCTestCase {
    @MainActor
    func testLevelsCoalesceWhileTheMainActorIsBusyAndStopRequestsAllArriveInOrder() async {
        let received = MainActorEventList()
        let delivered = AudioTestSignalLatch()
        let relay = CaptureEventRelay { event in
            received.append(event)
            if case .stopRequested(.durationLimit) = event.kind {
                delivered.signal()
            }
        }
        let owner = RecordingID(rawValue: 1)

        // Posted while this test holds the main actor, so no delivery can run in between.
        for index in 1...1_000 {
            relay.post(
                CaptureEvent(
                    owner: owner,
                    kind: .level(AudioLevelMeasurement(peakAmplitude: Float(index) / 1_000, rmsAmplitude: 0))))
        }
        relay.post(CaptureEvent(owner: owner, kind: .stopRequested(.deviceChanged)))
        relay.post(CaptureEvent(owner: owner, kind: .stopRequested(.durationLimit)))

        let arrived = await delivered.wait()
        XCTAssertTrue(arrived)

        let levels = received.events.compactMap { event -> Float? in
            if case .level(let level) = event.kind {
                return level.peakAmplitude
            }
            return nil
        }
        let stops = received.events.compactMap { event -> CaptureEndReason? in
            if case .stopRequested(let reason) = event.kind {
                return reason
            }
            return nil
        }
        XCTAssertEqual(levels, [1])
        XCTAssertEqual(stops, [.deviceChanged, .durationLimit])
    }

    func testEventsPostedFromAnotherThreadArriveOnTheMainActor() async {
        let delivered = AudioTestSignalLatch()
        let onMain = OSAllocatedUnfairLock(initialState: false)
        let relay = CaptureEventRelay { _ in
            onMain.withLock { $0 = Thread.isMainThread }
            delivered.signal()
        }

        DispatchQueue.global().async {
            relay.post(CaptureEvent(owner: RecordingID(rawValue: 1), kind: .stopRequested(.durationLimit)))
        }

        let arrived = await delivered.wait()
        XCTAssertTrue(arrived)
        XCTAssertTrue(onMain.withLock { $0 })
    }
}

@MainActor
private final class MainActorEventList {
    private(set) var events: [CaptureEvent] = []

    func append(_ event: CaptureEvent) {
        events.append(event)
    }
}
