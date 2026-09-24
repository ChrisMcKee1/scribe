import AVFoundation
import XCTest

@testable import Scribe

/// Silence auto-stop on the committed speech, as a 48 kHz stereo device delivers it in buffers of every size: room tone
/// alone, steady noise at -45 and -35 dBFS, a microphone 20 dB too quiet, and speech followed by silence, each with the
/// toggle policy (Caps Lock and the tray) and the hold policy (a key held while talking).
///
/// Each expectation is Windows' (`tools/Scribe.AsrCheck`, `SilenceAutoStop`): a toggle stops one hold window after the
/// last voiced block, or on the lead-in limit when it never heard speech, and a hold never stops on silence. The exact
/// figures come from `ScenarioSilenceOracle`, which feeds the production tracker the same 10 ms blocks directly, so the
/// capture must also agree with it on where it stopped and on every level it reports.
final class SilencePolicyScenarioTests: XCTestCase {
    private static let rate: Double = 48_000
    /// The analogue floor of a live microphone in a quiet room: far under anything that counts as voice.
    private static let roomToneDbfs = -70.0
    private static let hold = Int64(4_000)
    private static let leadIn = Int64(10_000)
    private static let blockMilliseconds = Int64(10)
    /// The resampler's slack, as a sample count to compare with.
    private static let slack = Double(ScenarioLimits.resamplerSlack)

    private struct Outcome {
        let stops: [CaptureEndReason]
        let captured: CapturedAudio
    }

    func testRoomToneAloneEndsAToggleOnTheLeadInAndNeverEndsAHold() throws {
        let length = Int(12 * Self.rate)
        let channels = [
            ScenarioAudio.white(count: length, rmsDbfs: Self.roomToneDbfs, seed: 31),
            ScenarioAudio.white(count: length, rmsDbfs: Self.roomToneDbfs, seed: 32),
        ]
        try assertLeadInStop(channels, name: "room-tone", seed: 33)
    }

    func testSteadyNoiseAtMinus45DbfsIsLearnedAsTheRoomAndEndsAToggleOnTheLeadIn() throws {
        let length = Int(12 * Self.rate)
        let channels = [
            ScenarioAudio.pink(count: length, rmsDbfs: -45, seed: 34),
            ScenarioAudio.pink(count: length, rmsDbfs: -45, seed: 35),
        ]
        try assertLeadInStop(channels, name: "noise-45", seed: 36)
    }

    func testSteadyNoiseAtMinus35DbfsIsLearnedAsTheRoomAndEndsAToggleOnTheLeadIn() throws {
        let length = Int(12 * Self.rate)
        let channels = [
            ScenarioAudio.pink(count: length, rmsDbfs: -35, seed: 37),
            ScenarioAudio.pink(count: length, rmsDbfs: -35, seed: 38),
        ]
        try assertLeadInStop(channels, name: "noise-35", seed: 39)
    }

    func testAMicrophoneTwentyDecibelsTooQuietStillStopsAToggleOneHoldAfterTheSpeech() throws {
        let clip = try ScenarioLibrary.shared().clip("sentence")
        let (channels, speechEnd) = try speech(clip, gainDb: -20, seed: 40)
        try assertStopAfterSpeech(channels, speechEnd: speechEnd, name: "quiet-mic-20db", seed: 41)
    }

    func testSpeechThenSilenceStopsAToggleOneHoldAfterTheSpeechAndNeverStopsAHold() throws {
        let clip = try ScenarioLibrary.shared().clip("greeting")
        let (channels, speechEnd) = try speech(clip, gainDb: 0, seed: 42)
        try assertStopAfterSpeech(channels, speechEnd: speechEnd, name: "speech-then-silence", seed: 43)
    }

    /// The same toggle through the production engine: the recording ends itself on the device's thread, posts one stop
    /// request naming its recording, and closes its device; the owner's stop then hands over what it kept, up to the
    /// block that ended it, and nothing the device delivered afterwards.
    func testSpeechThenSilenceEndsAToggleByItselfThroughTheEngine() async throws {
        let clip = try ScenarioLibrary.shared().clip("dict-scribe")
        let (channels, speechEnd) = try speech(clip, gainDb: 0, seed: 44)
        let oracle = ScenarioSilenceOracle(channels: channels, sampleRate: Self.rate)
        let audio = ScenarioDeviceAudio(sampleRate: Self.rate, encoding: .float32, channels: channels)
        let device = ScenarioCaptureDevice(audio: audio)
        let engine = AudioCaptureEngine(makeDevice: { device })
        let events = ScenarioEventLog()
        let owner = RecordingID.next()
        let report = ScenarioReport("silence-toggle-through-engine")

        let opened = try await underWatchdog("the microphone to open") {
            try await engine.start(owner: owner, policy: .toggle(maximumDuration: nil), events: events.sink)
        }
        XCTAssertEqual(opened, .live)
        let stream = device.stream(
            frameCounts: ScenarioAudio.frameCounts(total: audio.frameCount, seed: 45, within: 128...2_048))
        try await underWatchdog("the recording to end itself") { try await events.stopRequested.wait() }
        try await underWatchdog("the device to finish") { try await stream.finished.wait() }
        let seal = try XCTUnwrap(engine.retire(owner: owner))
        let sealed = try await underWatchdog("the recording to be sealed") { await seal.audio }
        let captured = try XCTUnwrap(sealed)

        let stop = try XCTUnwrap(oracle.stopMilliseconds, "the oracle never stopped")
        XCTAssertEqual(events.stopReasons.count, 1)
        XCTAssertTrue(events.all.allSatisfy { $0.owner == owner })
        guard case .endedItself(.silence(let detail)) = captured.summary.ending else {
            return XCTFail("the recording did not end itself on silence: \(captured.summary.ending)")
        }
        XCTAssertTrue(detail.heardSpeech)
        XCTAssertEqual(detail.peakLevel, oracle.peakLevel)
        XCTAssertEqual(Double(captured.samples.count), Double(stop) * 16, accuracy: Self.slack)
        XCTAssertGreaterThanOrEqual(stop, speechEnd)
        XCTAssertEqual(device.closeCount, 1, "the device was not closed exactly once")

        report.note("stopMs", count: Int(stop))
        report.note("speechEndMs", count: Int(speechEnd))
        report.note("samples", count: captured.samples.count)
        report.note("droppedBuffers", count: captured.summary.droppedBufferCount)
        report.write()
    }

    // MARK: - Expectations

    private func assertLeadInStop(
        _ channels: [[Float]], name: String, seed: UInt64, file: StaticString = #filePath, line: UInt = #line
    ) throws {
        let oracle = ScenarioSilenceOracle(channels: channels, sampleRate: Self.rate)
        XCTAssertEqual(oracle.stopMilliseconds, Self.leadIn, "\(name): the tracker itself", file: file, line: line)
        XCTAssertFalse(oracle.heardSpeech, "\(name): the tracker heard speech", file: file, line: line)

        let toggle = try run(channels, policy: .toggle(maximumDuration: nil), seed: seed)
        XCTAssertEqual(toggle.stops.count, 1, "\(name): stop requests", file: file, line: line)
        guard case .silence(let detail)? = toggle.stops.first else {
            return XCTFail("\(name): expected a silence stop, got \(toggle.stops)", file: file, line: line)
        }
        assertAgrees(detail, with: oracle, name: name, file: file, line: line)
        XCTAssertEqual(
            Double(toggle.captured.samples.count), Double(Self.leadIn) * 16, accuracy: Self.slack, "\(name): captured",
            file: file, line: line)

        let hold = try run(channels, policy: .hold(maximumDuration: nil), seed: seed)
        assertNeverStopped(hold, channels: channels, name: name, file: file, line: line)

        let report = ScenarioReport("silence-\(name)")
        report.note("toggleStopMs", count: Int(Self.leadIn))
        report.note("heardSpeech", String(detail.heardSpeech))
        report.note("peakDbfs", value: ScenarioAudio.dbfs(Double(detail.peakLevel)), digits: 1)
        report.note("noiseFloorDbfs", value: ScenarioAudio.dbfs(Double(detail.noiseFloor)), digits: 1)
        report.note("voiceThresholdDbfs", value: ScenarioAudio.dbfs(Double(detail.voiceThreshold)), digits: 1)
        report.note("holdSamples", count: hold.captured.samples.count)
        report.write()
    }

    private func assertStopAfterSpeech(
        _ channels: [[Float]], speechEnd: Int64, name: String, seed: UInt64, file: StaticString = #filePath,
        line: UInt = #line
    ) throws {
        let oracle = ScenarioSilenceOracle(channels: channels, sampleRate: Self.rate)
        XCTAssertTrue(oracle.heardSpeech, "\(name): the tracker never heard the speech", file: file, line: line)
        let stop = try XCTUnwrap(oracle.stopMilliseconds, "\(name): the tracker never stopped", file: file, line: line)
        let lastVoiced = try XCTUnwrap(oracle.lastVoicedMilliseconds, "\(name): nothing voiced", file: file, line: line)
        // Windows' check: the stop lands one hold window after the last voiced block, to within a block.
        XCTAssertTrue(
            (Self.hold...(Self.hold + Self.blockMilliseconds)).contains(stop - lastVoiced),
            "\(name): stopped \(stop - lastVoiced) ms after the last voiced block", file: file, line: line)
        XCTAssertGreaterThanOrEqual(stop, speechEnd, "\(name): stopped inside the speech", file: file, line: line)

        let toggle = try run(channels, policy: .toggle(maximumDuration: nil), seed: seed)
        XCTAssertEqual(toggle.stops.count, 1, "\(name): stop requests", file: file, line: line)
        guard case .silence(let detail)? = toggle.stops.first else {
            return XCTFail("\(name): expected a silence stop, got \(toggle.stops)", file: file, line: line)
        }
        assertAgrees(detail, with: oracle, name: name, file: file, line: line)
        XCTAssertEqual(
            Double(toggle.captured.samples.count), Double(stop) * 16, accuracy: Self.slack, "\(name): captured",
            file: file, line: line)

        let hold = try run(channels, policy: .hold(maximumDuration: nil), seed: seed)
        assertNeverStopped(hold, channels: channels, name: name, file: file, line: line)

        let report = ScenarioReport("silence-\(name)")
        report.note("toggleStopMs", count: Int(stop))
        report.note("lastVoicedMs", count: Int(lastVoiced))
        report.note("speechEndMs", count: Int(speechEnd))
        report.note("latencyAfterLastVoicedMs", count: Int(stop - lastVoiced))
        report.note("peakDbfs", value: ScenarioAudio.dbfs(Double(detail.peakLevel)), digits: 1)
        report.note("voiceThresholdDbfs", value: ScenarioAudio.dbfs(Double(detail.voiceThreshold)), digits: 1)
        report.note("holdSamples", count: hold.captured.samples.count)
        report.write()
    }

    private func assertAgrees(
        _ detail: SilenceStopDetail, with oracle: ScenarioSilenceOracle, name: String, file: StaticString, line: UInt
    ) {
        XCTAssertEqual(detail.heardSpeech, oracle.heardSpeech, "\(name): heardSpeech", file: file, line: line)
        XCTAssertEqual(detail.peakLevel, oracle.peakLevel, "\(name): peak level", file: file, line: line)
        XCTAssertEqual(detail.noiseFloor, oracle.noiseFloor, "\(name): noise floor", file: file, line: line)
        XCTAssertEqual(detail.voiceThreshold, oracle.voiceThreshold, "\(name): voice threshold", file: file, line: line)
    }

    private func assertNeverStopped(
        _ outcome: Outcome, channels: [[Float]], name: String, file: StaticString, line: UInt
    ) {
        XCTAssertTrue(outcome.stops.isEmpty, "\(name): a held recording ended itself", file: file, line: line)
        XCTAssertEqual(outcome.captured.summary.ending, .stoppedByOwner, file: file, line: line)
        XCTAssertEqual(
            Double(outcome.captured.samples.count), Double(channels[0].count) / 3, accuracy: Self.slack,
            "\(name): a held recording kept less than everything", file: file, line: line)
    }

    // MARK: - Signals

    /// `clip` at 48 kHz on both channels, `gainDb` from its recorded level, after 0.3 s of room tone and followed by
    /// 6 s of it, each channel over a floor of its own. Also returns where the clip ends, in milliseconds.
    private func speech(_ clip: ScenarioClip, gainDb: Double, seed: UInt64) throws -> ([[Float]], Int64) {
        let voice = try ScenarioAudio.resampled(
            ScenarioAudio.scaled(clip.samples, by: ScenarioAudio.amplitude(dbfs: gainDb)),
            from: Double(clip.sampleRate), to: Self.rate)
        let lead = Int(0.3 * Self.rate)
        let trail = Int(6 * Self.rate)
        let timeline = [Float](repeating: 0, count: lead) + voice + [Float](repeating: 0, count: trail)
        let channels = [
            ScenarioAudio.added(
                timeline, ScenarioAudio.white(count: timeline.count, rmsDbfs: Self.roomToneDbfs, seed: seed)),
            ScenarioAudio.added(
                timeline, ScenarioAudio.white(count: timeline.count, rmsDbfs: Self.roomToneDbfs, seed: seed + 1)),
        ]
        let speechEnd = Int64((Double(lead + voice.count) * 1_000 / Self.rate).rounded(.up))
        return (channels, speechEnd)
    }

    /// Feeds `channels` to a fresh processor in seeded buffers of 128 to 2,048 frames, as a 48 kHz stereo float device
    /// would deliver them, and returns what it decided and kept.
    private func run(_ channels: [[Float]], policy: CaptureStopPolicy, seed: UInt64) throws -> Outcome {
        let audio = ScenarioDeviceAudio(sampleRate: Self.rate, encoding: .float32, channels: channels)
        let processor = CaptureProcessor(owner: RecordingID.next(), policy: policy)
        try processor.configure(inputFormat: XCTUnwrap(audio.format()))
        var stops: [CaptureEndReason] = []
        var start = 0
        for count in ScenarioAudio.frameCounts(total: audio.frameCount, seed: seed, within: 128...2_048) {
            let buffer = try XCTUnwrap(audio.buffer(frames: start..<(start + count)))
            for kind in processor.process(buffer) {
                if case .stopRequested(let reason) = kind {
                    stops.append(reason)
                }
            }
            start += count
        }
        let captured = try XCTUnwrap(processor.finish())
        return Outcome(stops: stops, captured: captured)
    }
}
