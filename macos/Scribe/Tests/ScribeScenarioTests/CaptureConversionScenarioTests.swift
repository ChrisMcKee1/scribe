import AVFoundation
import XCTest

@testable import Scribe

/// The committed fixtures as real input devices deliver them: at 16, 44.1 and 48 kHz, mono and stereo, float and
/// 16-bit, with the voice on either channel, in buffers of every size, from a thread of the device's own, through the
/// production capture engine and its conversion to 16 kHz mono. What comes out must be the fixture that went in: sample
/// for sample where the device already runs at 16 kHz, and to within the resampler's filters otherwise. A stop that
/// lands while the device is still delivering keeps exactly what arrived before it, and nothing delivered later reaches
/// that recording or the next one.
final class CaptureConversionScenarioTests: XCTestCase {
    /// One device the fixture is played on.
    private struct DeviceCase {
        let clip: String
        let sampleRate: Double
        let layout: ScenarioChannelLayout
        let encoding: ScenarioDeviceAudio.Encoding
        let seed: UInt64

        var name: String {
            "\(clip)-\(Int(sampleRate))Hz-\(layout)-\(encoding.rawValue)"
        }

        /// At 16 kHz nothing is resampled, so unless another channel adds its floor, the capture is exact.
        var isExact: Bool {
            guard sampleRate == 16_000 else { return false }
            if case .stereoOne(_, true) = layout {
                return false
            }
            return true
        }
    }

    func testSixteenKilohertzMonoFloatIsCapturedSampleForSample() async throws {
        try await capture(
            DeviceCase(clip: "dict-azure-devops", sampleRate: 16_000, layout: .mono, encoding: .float32, seed: 11))
    }

    func testSixteenKilohertzStereoWithTheVoiceOnTheRightIsHalfTheVoiceSampleForSample() async throws {
        try await capture(
            DeviceCase(
                clip: "sentence", sampleRate: 16_000, layout: .stereoOne(voice: 1, otherHasFloor: false),
                encoding: .int16Interleaved, seed: 12))
    }

    func testFortyFourKilohertzMonoIsResampledTransparently() async throws {
        try await capture(
            DeviceCase(clip: "dict-scribe", sampleRate: 44_100, layout: .mono, encoding: .float32, seed: 13))
    }

    func testFortyFourKilohertzStereoWithTheVoiceOnTheLeftKeepsTheVoice() async throws {
        try await capture(
            DeviceCase(
                clip: "dict-github-copilot", sampleRate: 44_100, layout: .stereoOne(voice: 0, otherHasFloor: false),
                encoding: .float32, seed: 14))
    }

    func testFortyEightKilohertzStereoWithTheVoiceOnTheRightOverALiveFloorKeepsTheVoice() async throws {
        try await capture(
            DeviceCase(
                clip: "question", sampleRate: 48_000, layout: .stereoOne(voice: 1, otherHasFloor: true),
                encoding: .float32, seed: 15))
    }

    func testFortyEightKilohertzSixteenBitStereoWithTheVoiceOnBothChannelsIsResampledTransparently() async throws {
        try await capture(
            DeviceCase(clip: "longer", sampleRate: 48_000, layout: .stereoBoth, encoding: .int16Interleaved, seed: 16))
    }

    /// Plays one fixture on one device in seeded buffers of 17 to 4,801 frames, stops the recording once the device has
    /// delivered them all, and compares what was captured with the fixture.
    private func capture(_ device: DeviceCase, file: StaticString = #filePath, line: UInt = #line) async throws {
        let library = try ScenarioLibrary.shared()
        let clip = try library.clip(device.clip)
        let audio = try ScenarioDeviceAudio.device(
            playing: clip.samples, at: device.sampleRate, layout: device.layout, encoding: device.encoding,
            seed: device.seed)
        let scripted = ScenarioCaptureDevice(audio: audio)
        let engine = AudioCaptureEngine(makeDevice: { scripted })
        let events = ScenarioEventLog()
        let owner = RecordingID.next()
        let report = ScenarioReport("capture-\(device.name)")
        let clock = ContinuousClock()
        let began = clock.now

        let opened = try await underWatchdog("the microphone to open") {
            try await engine.start(owner: owner, policy: .hold(maximumDuration: nil), events: events.sink)
        }
        XCTAssertEqual(opened, .live, file: file, line: line)
        let frames = ScenarioAudio.frameCounts(total: audio.frameCount, seed: device.seed, within: 17...4_801)
        let stream = scripted.stream(frameCounts: frames)
        try await underWatchdog("the device to deliver every buffer") { try await stream.finished.wait() }
        let seal = try XCTUnwrap(engine.retire(owner: owner), file: file, line: line)
        let sealed = try await underWatchdog("the recording to be sealed") { await seal.audio }
        let captured = try XCTUnwrap(sealed, file: file, line: line)
        let elapsed = began.duration(to: clock.now)

        let summary = captured.summary
        let expectedFlush: AudioCaptureSummary.ResamplerFlush = device.sampleRate == 16_000 ? .notNeeded : .flushed
        XCTAssertEqual(summary.acceptedBufferCount, frames.count, file: file, line: line)
        XCTAssertEqual(summary.droppedBufferCount, 0, file: file, line: line)
        XCTAssertEqual(summary.ending, .stoppedByOwner, file: file, line: line)
        XCTAssertEqual(summary.resamplerFlush, expectedFlush, file: file, line: line)
        XCTAssertEqual(summary.sampleRate, 16_000, file: file, line: line)
        XCTAssertTrue(events.stopReasons.isEmpty, "a held recording ended itself", file: file, line: line)
        XCTAssertGreaterThan(events.levelCount, 0, "the meter never moved", file: file, line: line)
        XCTAssertTrue(events.all.allSatisfy { $0.owner == owner }, file: file, line: line)

        let gain = device.layout.downmixGain
        let expected = clip.samples.map { $0 * gain }
        let match = ScenarioAudio.similarity(of: captured.samples, to: expected, maxLag: 64)
        let levelChange = ScenarioAudio.dbfs(ScenarioAudio.rms(captured.samples)) - ScenarioAudio.dbfs(
            ScenarioAudio.rms(clip.samples))
        if device.isExact {
            XCTAssertEqual(captured.samples.count, expected.count, file: file, line: line)
            XCTAssertTrue(
                captured.samples == expected, "the capture changed samples on the 16 kHz path", file: file, line: line)
        } else {
            XCTAssertLessThanOrEqual(
                abs(captured.samples.count - clip.pcm.count), ScenarioLimits.resamplerSlack,
                "\(captured.samples.count) samples captured of a \(clip.pcm.count)-sample fixture", file: file,
                line: line)
            XCTAssertGreaterThanOrEqual(match.correlation, ScenarioLimits.minimumCorrelation, file: file, line: line)
            XCTAssertEqual(
                levelChange, ScenarioAudio.dbfs(Double(gain)), accuracy: ScenarioLimits.levelSlackDb,
                "level after the downmix", file: file, line: line)
        }

        // The signal report, taken before the downmix, says which channel the voice was on.
        let signal = try XCTUnwrap(summary.signal, file: file, line: line)
        XCTAssertEqual(signal.channels, device.layout.channelCount, file: file, line: line)
        XCTAssertEqual(signal.sampleRate, device.sampleRate, file: file, line: line)
        if case .stereoOne(let voice, _) = device.layout {
            let levels = signal.perChannel
            XCTAssertEqual(levels.count, 2, file: file, line: line)
            if levels.count == 2 {
                XCTAssertGreaterThan(levels[voice].rms, levels[1 - voice].rms * 10, file: file, line: line)
            }
        }

        report.note("deviceRate", count: Int(device.sampleRate))
        report.note("buffers", count: frames.count)
        report.note("samples", count: captured.samples.count)
        report.note("sampleDelta", count: captured.samples.count - clip.pcm.count)
        report.note("correlation", value: match.correlation, digits: 5)
        report.note("lag", count: match.lag)
        report.note("levelChangeDb", value: levelChange, digits: 2)
        report.note("openToSeal", duration: elapsed)
        report.write()
    }

    /// The device is paused halfway through its buffers, as if the release landed there, and the recording is stopped.
    /// It keeps exactly the buffers delivered before the stop. The next recording opens on the same engine, and only
    /// then does the first device deliver the rest of its buffers: none of them is metered, and none reaches the next
    /// recording, which captures its own fixture sample for sample.
    func testAStopMidStreamKeepsTheBuffersBeforeItAndNothingLateReachesTheNextRecording() async throws {
        let library = try ScenarioLibrary.shared()
        let first = try library.clip("dict-kubernetes")
        let second = try library.clip("pangram")
        let firstAudio = ScenarioDeviceAudio(sampleRate: 16_000, encoding: .float32, channels: [first.samples])
        let secondAudio = ScenarioDeviceAudio(sampleRate: 16_000, encoding: .float32, channels: [second.samples])
        let firstDevice = ScenarioCaptureDevice(audio: firstAudio)
        let secondDevice = ScenarioCaptureDevice(audio: secondAudio)
        let devices = ScenarioDeviceQueue()
        devices.enqueue(firstDevice)
        devices.enqueue(secondDevice)
        let engine = AudioCaptureEngine(makeDevice: devices.makeDevice)
        let report = ScenarioReport("capture-stop-mid-stream")

        let firstEvents = ScenarioEventLog()
        let firstOwner = RecordingID.next()
        let firstOpened = try await underWatchdog("the first microphone to open") {
            try await engine.start(owner: firstOwner, policy: .hold(maximumDuration: nil), events: firstEvents.sink)
        }
        XCTAssertEqual(firstOpened, .live)
        let frames = ScenarioAudio.frameCounts(total: firstAudio.frameCount, seed: 21, within: 17...2_049)
        let cut = frames.count / 2
        let firstStream = firstDevice.stream(frameCounts: frames, pauseAt: cut)
        addTeardownBlock {
            firstStream.resume()
        }
        try await underWatchdog("the first device to pause halfway") { try await firstStream.paused.wait() }
        let meteredBeforeStop = firstEvents.levelCount

        let firstSeal = try XCTUnwrap(engine.retire(owner: firstOwner))
        let firstSealed = try await underWatchdog("the first recording to be sealed") { await firstSeal.audio }
        let firstCaptured = try XCTUnwrap(firstSealed)
        let kept = frames.prefix(cut).reduce(0, +)
        XCTAssertEqual(firstCaptured.summary.acceptedBufferCount, cut)
        XCTAssertEqual(firstCaptured.samples.count, kept)
        XCTAssertTrue(
            firstCaptured.samples == Array(first.samples.prefix(kept)),
            "the stopped recording is not the exact prefix delivered before the stop")

        let secondEvents = ScenarioEventLog()
        let secondOwner = RecordingID.next()
        let secondOpened = try await underWatchdog("the second microphone to open") {
            try await engine.start(owner: secondOwner, policy: .hold(maximumDuration: nil), events: secondEvents.sink)
        }
        XCTAssertEqual(secondOpened, .live)
        let secondStream = secondDevice.stream(
            frameCounts: ScenarioAudio.frameCounts(total: secondAudio.frameCount, seed: 22, within: 17...2_049))
        firstStream.resume()
        try await underWatchdog("the first device's late buffers") { try await firstStream.finished.wait() }
        try await underWatchdog("the second device's buffers") { try await secondStream.finished.wait() }
        let secondSeal = try XCTUnwrap(engine.retire(owner: secondOwner))
        let secondSealed = try await underWatchdog("the second recording to be sealed") { await secondSeal.audio }
        let secondCaptured = try XCTUnwrap(secondSealed)

        XCTAssertEqual(firstEvents.levelCount, meteredBeforeStop, "a buffer delivered after the stop was metered")
        XCTAssertTrue(firstEvents.all.allSatisfy { $0.owner == firstOwner })
        XCTAssertTrue(secondEvents.all.allSatisfy { $0.owner == secondOwner })
        XCTAssertTrue(
            secondCaptured.samples == second.samples,
            "the next recording holds audio other than its own fixture")
        XCTAssertEqual(firstDevice.closeCount, 1)
        _ = try await underWatchdog("the engine's device work") { await engine.waitUntilIdle() }
        XCTAssertEqual(secondDevice.closeCount, 1)

        report.note("buffers", count: frames.count)
        report.note("keptBuffers", count: cut)
        report.note("keptSamples", count: kept)
        report.note("lateBuffers", count: frames.count - cut)
        report.write()
    }

    /// The same stop at 48 kHz stereo, where the resampler holds a few milliseconds back until the recording ends: the
    /// capture is the resampled prefix, flushed, and follows the fixture's first half closely.
    func testAStopMidStreamAtFortyEightKilohertzKeepsTheResampledPrefix() async throws {
        let library = try ScenarioLibrary.shared()
        let clip = try library.clip("list")
        let audio = try ScenarioDeviceAudio.device(
            playing: clip.samples, at: 48_000, layout: .stereoBoth, encoding: .float32, seed: 23)
        let device = ScenarioCaptureDevice(audio: audio)
        let engine = AudioCaptureEngine(makeDevice: { device })
        let events = ScenarioEventLog()
        let owner = RecordingID.next()
        let report = ScenarioReport("capture-stop-mid-stream-48k")

        let opened = try await underWatchdog("the microphone to open") {
            try await engine.start(owner: owner, policy: .hold(maximumDuration: nil), events: events.sink)
        }
        XCTAssertEqual(opened, .live)
        let frames = ScenarioAudio.frameCounts(total: audio.frameCount, seed: 24, within: 17...4_801)
        let cut = frames.count / 2
        let stream = device.stream(frameCounts: frames, pauseAt: cut)
        addTeardownBlock {
            stream.resume()
        }
        try await underWatchdog("the device to pause halfway") { try await stream.paused.wait() }
        let seal = try XCTUnwrap(engine.retire(owner: owner))
        let sealed = try await underWatchdog("the recording to be sealed") { await seal.audio }
        let captured = try XCTUnwrap(sealed)
        stream.resume()
        try await underWatchdog("the late buffers") { try await stream.finished.wait() }

        let keptFrames = frames.prefix(cut).reduce(0, +)
        let expectedCount = Int((Double(keptFrames) / 3).rounded())
        XCTAssertEqual(captured.summary.acceptedBufferCount, cut)
        XCTAssertEqual(captured.summary.resamplerFlush, .flushed)
        XCTAssertLessThanOrEqual(abs(captured.samples.count - expectedCount), ScenarioLimits.resamplerSlack)
        let match = ScenarioAudio.similarity(
            of: captured.samples, to: Array(clip.samples.prefix(expectedCount)), maxLag: 64)
        XCTAssertGreaterThanOrEqual(match.correlation, ScenarioLimits.minimumCorrelation)

        report.note("keptFrames", count: keptFrames)
        report.note("samples", count: captured.samples.count)
        report.note("sampleDelta", count: captured.samples.count - expectedCount)
        report.note("correlation", value: match.correlation, digits: 5)
        report.write()
    }

    /// A stop that races the device: the device goes on delivering while the recording is stopped at a moment neither
    /// side waits for. Whichever buffer the stop lands on, the capture is a prefix of whole buffers, in order, exactly
    /// as delivered.
    func testAStopRacingTheDeviceKeepsAPrefixOfWholeBuffersInOrder() async throws {
        let library = try ScenarioLibrary.shared()
        let clip = try library.clip("long-passage")
        let audio = ScenarioDeviceAudio(sampleRate: 16_000, encoding: .float32, channels: [clip.samples])
        let device = ScenarioCaptureDevice(audio: audio)
        let engine = AudioCaptureEngine(makeDevice: { device })
        let events = ScenarioEventLog()
        let owner = RecordingID.next()
        let report = ScenarioReport("capture-stop-racing")

        let opened = try await underWatchdog("the microphone to open") {
            try await engine.start(owner: owner, policy: .hold(maximumDuration: nil), events: events.sink)
        }
        XCTAssertEqual(opened, .live)
        let frames = ScenarioAudio.frameCounts(total: audio.frameCount, seed: 25, within: 17...1_025)
        let signalAt = frames.count / 3
        let stream = device.stream(frameCounts: frames, signalAt: signalAt)
        try await underWatchdog("the device to pass a third of its buffers") { try await stream.reached.wait() }
        let seal = try XCTUnwrap(engine.retire(owner: owner))
        let sealed = try await underWatchdog("the recording to be sealed") { await seal.audio }
        let captured = try XCTUnwrap(sealed)
        try await underWatchdog("the device to finish") { try await stream.finished.wait() }

        let accepted = captured.summary.acceptedBufferCount
        XCTAssertGreaterThanOrEqual(accepted, signalAt)
        XCTAssertLessThanOrEqual(accepted, frames.count)
        let kept = frames.prefix(accepted).reduce(0, +)
        XCTAssertEqual(captured.samples.count, kept)
        XCTAssertTrue(
            captured.samples == Array(clip.samples.prefix(kept)),
            "the capture is not the prefix of the \(accepted) buffers it accepted")

        report.note("buffers", count: frames.count)
        report.note("acceptedBuffers", count: accepted)
        report.note("lateBuffers", count: frames.count - accepted)
        report.write()
    }
}
