import AVFoundation
import XCTest
import os

@testable import Scribe

/// `AudioCaptureEngine.retire(owner:)`, the stop the dictation lifecycle uses: it returns without waiting for the
/// recording's lock or its resampler, drops every buffer delivered after it, and seals the samples on the control
/// queue ahead of the next recording's open. Every await on the engine runs under a watchdog (`within`), and every hold
/// the tests put on the control queue is let go in teardown.
@MainActor
final class AudioCaptureEngineRetireTests: XCTestCase {
    private func buffer(_ value: Float, frames: Int = 160, sampleRate: Double = 16_000) -> AVAudioPCMBuffer {
        AudioTestBuffers.mono([Float](repeating: value, count: frames), sampleRate: sampleRate)
    }

    /// The control queue is held (inside a stall check the test's scheduler blocks), so the seal is queued behind it:
    /// a buffer the tap delivers after the retire, and before the seal, is dropped, and the seal keeps exactly the
    /// buffer that came before.
    func testARetiredRecordingDropsLaterBuffersAndSealsWhatCameBefore() async throws {
        let device = CaptureTestDevice()
        let stallEntered = AudioTestSignalLatch()
        let releaseStall = DispatchSemaphore(value: 0)
        addTeardownBlock { releaseStall.signal() }
        let engine = AudioCaptureEngine(
            scheduleStallCheck: { _, _ in
                stallEntered.signal()
                _ = releaseStall.wait(timeout: .now() + .seconds(60))
            },
            makeDevice: { device })
        let owner = RecordingID(rawValue: 1)
        let outcome = try await within("the recording to open") {
            try await engine.start(owner: owner, policy: .hold(), events: { _ in })
        }
        XCTAssertEqual(outcome, .live)
        device.deliver(buffer(0.25))
        device.changeConfiguration(stopsDevice: false)
        let held = await stallEntered.wait()
        XCTAssertTrue(held, "the control queue was never held")

        let seal = try XCTUnwrap(engine.retire(owner: owner))
        XCTAssertFalse(engine.isCapturing, "the microphone was not given up at once")
        device.deliver(buffer(0.5))
        XCTAssertFalse(seal.isSealed)
        releaseStall.signal()
        let sealed = try await within("the seal") { await seal.audio }
        try await within("the engine's device work") { await engine.waitUntilIdle() }

        let captured = try XCTUnwrap(sealed)
        XCTAssertEqual(captured.samples, [Float](repeating: 0.25, count: 160), "a buffer after the stop was kept")
        XCTAssertEqual(captured.summary.ending, .stoppedByOwner)
        XCTAssertEqual(captured.summary.droppedBufferCount, 1)
        XCTAssertTrue(seal.isSealed)
        XCTAssertEqual(device.counts.closed, 1)
        XCTAssertNil(engine.retire(owner: owner), "a second stop received the audio again")
        XCTAssertNil(engine.stop(owner: owner))
    }

    /// The retire returns while the resampler's flush is still held on the control queue (`HeldFlush`, whose
    /// watchdog would have let it go had the retire waited for it), and the next recording, admitted meanwhile, opens
    /// only after the retired recording's device has closed.
    func testRetireReturnsWhileTheFlushIsHeldAndTheNextOpenWaitsForTheSeal() async throws {
        let journal = CaptureDeviceJournal()
        var resampled = CaptureTestDevice.Configuration()
        resampled.sampleRate = 48_000
        let first = CaptureTestDevice(resampled, name: "first", journal: journal)
        let second = CaptureTestDevice(name: "second", journal: journal)
        let flush = HeldFlush()
        addTeardownBlock { _ = flush.release() }
        let engine = AudioCaptureEngine(
            resamplerTailFlush: flush.tailFlush,
            makeDevice: CaptureTestDeviceFactory([first, second]).make)
        let firstOwner = RecordingID(rawValue: 1)
        let secondOwner = RecordingID(rawValue: 2)
        _ = try await within("the first recording to open") {
            try await engine.start(owner: firstOwner, policy: .hold(), events: { _ in })
        }
        first.deliver(buffer(0.25, frames: 4_800, sampleRate: 48_000))

        let seal = try XCTUnwrap(engine.retire(owner: firstOwner))
        XCTAssertNil(flush.releasedBy, "the retire returned only after the flush was let go")
        let entered = await flush.entered.wait()
        XCTAssertTrue(entered, "the seal never reached the resampler flush")
        XCTAssertTrue(flush.isHeld)
        XCTAssertFalse(seal.isSealed)

        let next = Task { try await engine.start(owner: secondOwner, policy: .hold(), events: { _ in }) }
        XCTAssertTrue(flush.release(), "the flush's watchdog let it go before the test did")
        let sealed = try await within("the seal") { await seal.audio }
        let opened = try await within("the next recording to open") { try await next.value }
        try await within("the engine's device work") { await engine.waitUntilIdle() }

        XCTAssertEqual(opened, .live)
        XCTAssertFalse(try XCTUnwrap(sealed).samples.isEmpty)
        XCTAssertEqual(journal.all, ["first prepare", "first start", "first close", "second prepare", "second start"])
        engine.stop(owner: secondOwner)
        try await within("the engine's device work") { await engine.waitUntilIdle() }
        XCTAssertEqual(first.counts.closed, 1)
        XCTAssertEqual(second.counts.closed, 1)
    }

    /// A retire that arrives before its recording's start was admitted opens nothing and has nothing to seal; one
    /// that arrives while the device opens seals an empty capture once the open has closed it again.
    func testRetireBeforeOrDuringTheOpen() async throws {
        let factory = CaptureTestDeviceFactory([])
        let engine = AudioCaptureEngine(makeDevice: factory.make)
        XCTAssertNil(engine.retire(owner: RecordingID(rawValue: 3)))
        let early = try await within("the stopped start") {
            try await engine.start(owner: RecordingID(rawValue: 3), policy: .hold(), events: { _ in })
        }
        XCTAssertEqual(early, .stoppedBeforeOpen)
        XCTAssertTrue(factory.made.isEmpty)

        var held = CaptureTestDevice.Configuration()
        held.holdsPrepare = true
        let device = CaptureTestDevice(held)
        addTeardownBlock { device.releasePrepare() }
        let opening = AudioCaptureEngine(makeDevice: { device })
        let owner = RecordingID(rawValue: 4)
        let start = Task { try await opening.start(owner: owner, policy: .hold(), events: { _ in }) }
        let entered = await device.prepareEntered.wait()
        XCTAssertTrue(entered)
        let seal = try XCTUnwrap(opening.retire(owner: owner))
        device.releasePrepare()
        let outcome = try await within("the open") { try await start.value }
        let sealed = try await within("the seal") { await seal.audio }
        try await within("the engine's device work") { await opening.waitUntilIdle() }

        XCTAssertEqual(outcome, .stoppedWhileOpening)
        XCTAssertEqual(try XCTUnwrap(sealed).samples, [])
        XCTAssertEqual(device.counts.closed, 1)
    }
}
