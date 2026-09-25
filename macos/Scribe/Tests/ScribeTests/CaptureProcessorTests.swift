import AVFoundation
import XCTest
import os

@testable import Scribe

/// The recording's buffer path, fed synthetic buffers directly: no device, no engine, no clock.
final class CaptureProcessorTests: XCTestCase {
    private func makeProcessor(
        _ policy: CaptureStopPolicy = .hold(maximumDuration: nil),
        sampleRate: Double = 16_000,
        channels: Int = 1,
        commonFormat: AVAudioCommonFormat = .pcmFormatFloat32,
        interleaved: Bool = false,
        tailFlush: @escaping CaptureProcessor.TailFlush = CaptureProcessor.flushResamplerTail
    ) throws -> CaptureProcessor {
        let processor = CaptureProcessor(owner: RecordingID(rawValue: 1), policy: policy, tailFlush: tailFlush)
        try processor.configure(
            inputFormat: AudioTestBuffers.format(
                sampleRate: sampleRate, channels: channels, commonFormat: commonFormat, interleaved: interleaved))
        return processor
    }

    private func stopRequests(in events: [CaptureEvent.Kind]) -> [CaptureEndReason] {
        events.compactMap { kind in
            if case .stopRequested(let reason) = kind {
                return reason
            }
            return nil
        }
    }

    private func levels(in events: [CaptureEvent.Kind]) -> [AudioLevelMeasurement] {
        events.compactMap { kind in
            if case .level(let level) = kind {
                return level
            }
            return nil
        }
    }

    // MARK: - Samples

    func testSixteenKilohertzMonoPassesThroughSampleForSample() throws {
        let processor = try makeProcessor()
        var expected: [Float] = []
        for index in 0..<25 {
            let chunk = (0..<160).map { Float(index * 160 + $0) / 10_000 }
            expected += chunk
            _ = processor.process(AudioTestBuffers.mono(chunk, sampleRate: 16_000))
        }

        let captured = try XCTUnwrap(processor.finish())

        XCTAssertEqual(captured.samples, expected)
        XCTAssertEqual(captured.summary.sampleCount, expected.count)
        XCTAssertEqual(captured.summary.sampleRate, 16_000)
        XCTAssertEqual(captured.summary.acceptedBufferCount, 25)
        XCTAssertEqual(captured.summary.droppedBufferCount, 0)
        XCTAssertEqual(captured.summary.ending, .stoppedByOwner)
        XCTAssertEqual(captured.summary.resamplerFlush, .notNeeded)
    }

    func testEveryChannelIsAveragedIntoTheMonoSignal() throws {
        let stereo = try makeProcessor(channels: 2)
        _ = stereo.process(
            AudioTestBuffers.make(sampleRate: 16_000, channels: [[1, 1, 1, 1], [0, 0, 0, 0]]))
        XCTAssertEqual(try XCTUnwrap(stereo.finish()).samples, [0.5, 0.5, 0.5, 0.5])

        let threeChannels = try makeProcessor(channels: 3)
        _ = threeChannels.process(
            AudioTestBuffers.make(sampleRate: 16_000, channels: [[0.9, -0.3], [0.3, -0.3], [0, 0.9]]))
        let mixed = try XCTUnwrap(threeChannels.finish()).samples
        XCTAssertEqual(mixed.count, 2)
        XCTAssertEqual(mixed[0], 0.4, accuracy: 1e-6)
        XCTAssertEqual(mixed[1], 0.1, accuracy: 1e-6)
    }

    /// A two-input interface with the microphone on input 2: the old converter kept channel 1 only, so every
    /// such dictation was silence.
    func testAMicrophoneOnTheSecondInputIsHeard() throws {
        let processor = try makeProcessor(sampleRate: 48_000, channels: 2)
        let voice = AudioTestSignal.tone(seconds: 1, frequency: 440, amplitude: 0.8, sampleRate: 48_000)
        let silence = [Float](repeating: 0, count: voice.count)
        for start in stride(from: 0, to: voice.count, by: 4_800) {
            _ = processor.process(
                AudioTestBuffers.make(
                    sampleRate: 48_000,
                    channels: [Array(silence[start..<(start + 4_800)]), Array(voice[start..<(start + 4_800)])]))
        }

        let captured = try XCTUnwrap(processor.finish())

        let middle = captured.samples[4_000..<12_000]
        XCTAssertEqual(AudioTestSignal.rms(middle), 0.4 / Float(2).squareRoot(), accuracy: 0.02)
        let signal = try XCTUnwrap(captured.summary.signal)
        XCTAssertEqual(signal.channels, 2)
        XCTAssertTrue(signal.hasSilentChannel)
        XCTAssertEqual(signal.perChannel[0].peak, 0)
        XCTAssertGreaterThan(signal.perChannel[1].peak, 0.79)
    }

    func testInterleavedAndIntegerBuffersReadLikeFloatBuffers() throws {
        let left = (0..<320).map { Float(sin(Double($0) * 0.05)) * 0.5 }
        let right = (0..<320).map { Float(cos(Double($0) * 0.03)) * 0.25 }

        var outputs: [[Float]] = []
        let layouts: [(AVAudioCommonFormat, Bool)] = [
            (.pcmFormatFloat32, false), (.pcmFormatFloat32, true), (.pcmFormatInt16, false), (.pcmFormatInt16, true),
            (.pcmFormatInt32, false),
        ]
        for (commonFormat, interleaved) in layouts {
            let processor = try makeProcessor(channels: 2, commonFormat: commonFormat, interleaved: interleaved)
            _ = processor.process(
                AudioTestBuffers.make(
                    sampleRate: 16_000, channels: [left, right], commonFormat: commonFormat, interleaved: interleaved))
            outputs.append(try XCTUnwrap(processor.finish()).samples)
        }

        for output in outputs.dropFirst() {
            XCTAssertEqual(output.count, outputs[0].count)
            for (value, reference) in zip(output, outputs[0]) {
                XCTAssertEqual(value, reference, accuracy: 1e-4)
            }
        }
    }

    func testFortyEightKilohertzIsResampledToSixteenKeepingPitchAndLevel() throws {
        let processor = try makeProcessor(sampleRate: 48_000)
        let tone = AudioTestSignal.tone(seconds: 2, frequency: 440, amplitude: 0.5, sampleRate: 48_000)
        for start in stride(from: 0, to: tone.count, by: 4_800) {
            _ = processor.process(AudioTestBuffers.mono(Array(tone[start..<(start + 4_800)]), sampleRate: 48_000))
        }

        let captured = try XCTUnwrap(processor.finish())
        let samples = captured.samples

        XCTAssertEqual(captured.summary.resamplerFlush, .flushed)
        XCTAssertEqual(Double(samples.count), 32_000, accuracy: 64)
        XCTAssertEqual(AudioTestSignal.rms(samples[4_000..<28_000]), 0.5 / Float(2).squareRoot(), accuracy: 0.02)
        // 440 Hz at 16 kHz crosses zero upward 440 times a second.
        let window = samples[8_000..<24_000]
        var upwardCrossings = 0
        for index in window.indices.dropFirst() where window[index - 1] < 0 && window[index] >= 0 {
            upwardCrossings += 1
        }
        XCTAssertEqual(Double(upwardCrossings), 440, accuracy: 2)
    }

    func testAnUnreadableFormatIsRefused() {
        let processor = CaptureProcessor(owner: RecordingID(rawValue: 1), policy: .hold())
        let format = AudioTestBuffers.format(sampleRate: 48_000, channels: 1, commonFormat: .pcmFormatFloat64)
        XCTAssertThrowsError(try processor.configure(inputFormat: format)) { error in
            guard case AudioCaptureEngineError.unsupportedInputFormat = error else {
                return XCTFail("unexpected \(error)")
            }
        }
    }

    // MARK: - Ownership of the samples

    /// The owner stops the recording and flushing the resampler's tail fails: the summary and the log say so,
    /// the ending is still the owner's stop, and the capture keeps everything converted before the failure,
    /// including the part of the tail that did get through.
    func testAFailedFinalFlushIsReportedAndTheCaptureKeepsWhatWasConverted() throws {
        let tone = AudioTestSignal.tone(seconds: 0.5, frequency: 440, amplitude: 0.5, sampleRate: 48_000)
        let reference = try makeProcessor(sampleRate: 48_000, tailFlush: { _, _, _ in true })
        let failing = try makeProcessor(
            sampleRate: 48_000,
            tailFlush: { _, _, samples in
                samples.append(contentsOf: [0.125, 0.125])
                return false
            })
        for start in stride(from: 0, to: tone.count, by: 4_800) {
            let chunk = Array(tone[start..<(start + 4_800)])
            _ = reference.process(AudioTestBuffers.mono(chunk, sampleRate: 48_000))
            _ = failing.process(AudioTestBuffers.mono(chunk, sampleRate: 48_000))
        }
        let recorder = recordScribeLog()

        let converted = try XCTUnwrap(reference.finish())
        let captured = try XCTUnwrap(failing.finish())
        AudioCaptureEngine.logCompletion(captured)
        recorder.stop()

        XCTAssertFalse(converted.samples.isEmpty)
        XCTAssertEqual(captured.samples, converted.samples + [0.125, 0.125])
        XCTAssertEqual(captured.summary.ending, .stoppedByOwner)
        XCTAssertEqual(captured.summary.resamplerFlush, .failed)
        XCTAssertEqual(converted.summary.resamplerFlush, .flushed)
        XCTAssertTrue(recorder.lines.contains { $0.contains("resamplerFlush=failed") }, "\(recorder.lines)")
        XCTAssertTrue(
            recorder.lines.contains { $0.contains("Converting the end of the recording to 16 kHz failed") },
            "\(recorder.lines)")
    }

    /// A recording that ends by itself flushes once; a failed flush is reported the same way, and the one stop
    /// request keeps its reason instead of being followed by a second one.
    func testAFailedFlushWhenTheRecordingEndsItselfKeepsItsOneStopRequest() throws {
        let flushes = OSAllocatedUnfairLock(initialState: 0)
        let processor = try makeProcessor(
            .hold(maximumDuration: .milliseconds(200)),
            sampleRate: 48_000,
            tailFlush: { _, _, _ in
                flushes.withLock { $0 += 1 }
                return false
            })
        var requests: [CaptureEndReason] = []
        for _ in 0..<10 {
            let chunk = [Float](repeating: 0.1, count: 4_800)
            requests += stopRequests(in: processor.process(AudioTestBuffers.mono(chunk, sampleRate: 48_000)))
        }

        let captured = try XCTUnwrap(processor.finish())

        XCTAssertEqual(requests, [.durationLimit])
        XCTAssertEqual(flushes.withLock { $0 }, 1)
        XCTAssertEqual(captured.summary.ending, .endedItself(.durationLimit))
        XCTAssertEqual(captured.summary.resamplerFlush, .failed)
        XCTAssertFalse(captured.samples.isEmpty)
    }

    func testFinishHandsTheAudioOverOnceAndDropsEveryLaterBuffer() throws {
        let processor = try makeProcessor()
        _ = processor.process(AudioTestBuffers.mono([0.1, 0.2], sampleRate: 16_000))

        let first = try XCTUnwrap(processor.finish())
        XCTAssertNil(processor.finish())
        XCTAssertTrue(processor.process(AudioTestBuffers.mono([0.3, 0.4], sampleRate: 16_000)).isEmpty)

        XCTAssertEqual(first.samples, [0.1, 0.2])
        XCTAssertEqual(processor.bufferCounts, CaptureProcessor.BufferCounts(received: 2, accepted: 1, dropped: 1))
        XCTAssertTrue(processor.hasEnded)
    }

    func testEndingIsReportedOnceAndKeepsWhatWasCaptured() throws {
        let processor = try makeProcessor()
        _ = processor.process(AudioTestBuffers.mono([0.25, 0.5], sampleRate: 16_000))

        XCTAssertTrue(processor.end(.deviceChanged))
        XCTAssertFalse(processor.end(.deviceChanged))
        XCTAssertTrue(processor.process(AudioTestBuffers.mono([0.75], sampleRate: 16_000)).isEmpty)

        let captured = try XCTUnwrap(processor.finish())
        XCTAssertEqual(captured.samples, [0.25, 0.5])
        XCTAssertEqual(captured.summary.ending, .endedItself(.deviceChanged))
        XCTAssertEqual(captured.summary.droppedBufferCount, 1)
    }

    func testABufferInAnotherFormatEndsTheRecordingOnceAndKeepsWhatItHad() throws {
        let processor = try makeProcessor()
        _ = processor.process(AudioTestBuffers.mono([0.1, 0.2, 0.3], sampleRate: 16_000))

        let events = processor.process(AudioTestBuffers.mono([0.4, 0.5, 0.6], sampleRate: 48_000))
        let later = processor.process(AudioTestBuffers.mono([0.7], sampleRate: 16_000))

        XCTAssertEqual(stopRequests(in: events), [.formatChanged])
        XCTAssertTrue(later.isEmpty)
        let captured = try XCTUnwrap(processor.finish())
        XCTAssertEqual(captured.samples, [0.1, 0.2, 0.3])
        XCTAssertEqual(captured.summary.ending, .endedItself(.formatChanged))
    }

    // MARK: - Meter

    func testTheMeterPostsAtMostOncePerBufferAndOncePerFiftyMillisecondsOfAudio() throws {
        let processor = try makeProcessor()
        var tenMillisecondBuffers: [CaptureEvent.Kind] = []
        for _ in 0..<10 {
            tenMillisecondBuffers += processor.process(
                AudioTestBuffers.mono([Float](repeating: 0.5, count: 160), sampleRate: 16_000))
        }
        let oneLongBuffer = processor.process(
            AudioTestBuffers.mono([Float](repeating: -0.25, count: 1_600), sampleRate: 16_000))

        XCTAssertEqual(levels(in: tenMillisecondBuffers).count, 2)
        XCTAssertEqual(levels(in: oneLongBuffer), [AudioLevelMeasurement(peakAmplitude: 0.25, rmsAmplitude: 0.25)])
        XCTAssertEqual(levels(in: tenMillisecondBuffers).first?.peakAmplitude, 0.5)
    }

    // MARK: - Duration ceiling

    func testTheCeilingKeepsExactlyTheMaximumDurationAndEndsTheRecordingOnce() throws {
        let processor = try makeProcessor(.hold(maximumDuration: .seconds(1)))
        var events: [CaptureEvent.Kind] = []
        for _ in 0..<15 {
            events += processor.process(
                AudioTestBuffers.mono([Float](repeating: 0.1, count: 1_600), sampleRate: 16_000))
        }

        let captured = try XCTUnwrap(processor.finish())

        XCTAssertEqual(stopRequests(in: events), [.durationLimit])
        XCTAssertEqual(captured.samples.count, 16_000)
        XCTAssertEqual(captured.summary.ending, .endedItself(.durationLimit))
        XCTAssertEqual(captured.summary.acceptedBufferCount, 10)
        XCTAssertEqual(captured.summary.droppedBufferCount, 5)
    }

    func testTheCeilingCutsABufferThatCrossesIt() throws {
        let processor = try makeProcessor(.hold(maximumDuration: .milliseconds(150)))
        let events = processor.process(AudioTestBuffers.mono([Float](repeating: 0.1, count: 4_000), sampleRate: 16_000))

        XCTAssertEqual(stopRequests(in: events), [.durationLimit])
        XCTAssertEqual(try XCTUnwrap(processor.finish()).samples.count, 2_400)
    }

    // MARK: - Silence policy in the buffer path

    func testAHoldNeverStopsOnSilenceHoweverLongItLasts() throws {
        let processor = try makeProcessor(.hold(maximumDuration: nil))
        let outcome = feedMono(
            [Float](repeating: 0, count: 30 * 16_000), sampleRate: 16_000, framesPerBuffer: 1_600, to: processor)

        XCTAssertTrue(stopRequests(in: outcome.events).isEmpty)
        XCTAssertEqual(try XCTUnwrap(processor.finish()).samples.count, 30 * 16_000)
    }

    func testAToggleOfSilenceAloneStopsOnTheLeadInAndKeepsAudioUpToThatBlock() throws {
        let processor = try makeProcessor(.toggle(maximumDuration: nil))
        let outcome = feedMono(
            [Float](repeating: 0, count: 15 * 16_000), sampleRate: 16_000, framesPerBuffer: 1_600, to: processor)

        let reasons = stopRequests(in: outcome.events)
        XCTAssertEqual(reasons.count, 1)
        guard case .silence(let detail) = reasons.first else {
            return XCTFail("expected a silence stop, got \(reasons)")
        }
        XCTAssertFalse(detail.heardSpeech)
        XCTAssertEqual(outcome.stoppedAtMilliseconds, 10_000)
        XCTAssertEqual(try XCTUnwrap(processor.finish()).samples.count, 10 * 16_000)
    }

    /// Steady noise above the old fixed -45 dBFS threshold held a toggle open forever.
    func testAToggleInSteadyNoiseAboveMinus45DbfsEndsOnTheLeadInWithoutHearingSpeech() throws {
        let processor = try makeProcessor(.toggle(maximumDuration: nil), sampleRate: 48_000)
        let noise = AudioTestSignal.pink(seconds: 15, rmsDbfs: -35, seed: 21)

        let outcome = feedMono(noise, sampleRate: 48_000, framesPerBuffer: 4_800, to: processor)

        let reasons = stopRequests(in: outcome.events)
        guard reasons.count == 1, case .silence(let detail) = reasons[0] else {
            return XCTFail("expected one silence stop, got \(reasons)")
        }
        XCTAssertFalse(detail.heardSpeech)
        XCTAssertEqual(Double(try XCTUnwrap(outcome.stoppedAtMilliseconds)), 10_000, accuracy: 100)
    }

    /// Soft speech, then the speaker stops: a toggle ends the hold window after the last word (not at the
    /// lead-in, which a detector that never heard the speech would also do), and a hold keeps recording.
    func testSoftSpeechThenSilenceStopsAToggleAfterTheHoldAndNeverStopsAHold() throws {
        let room = AudioTestSignal.pink(seconds: 20, rmsDbfs: -70, seed: 22)
        let audio = AudioTestSignal.mix(
            room, AudioTestSignal.syntheticSpeech(seconds: 3, peakDbfs: -35, seed: 23), startSeconds: 1)

        let toggle = try makeProcessor(.toggle(maximumDuration: nil), sampleRate: 48_000)
        let toggled = feedMono(audio, sampleRate: 48_000, framesPerBuffer: 4_800, to: toggle)
        let reasons = stopRequests(in: toggled.events)
        guard reasons.count == 1, case .silence(let detail) = reasons[0] else {
            return XCTFail("expected one silence stop, got \(reasons)")
        }
        XCTAssertTrue(detail.heardSpeech)
        let stoppedAt = try XCTUnwrap(toggled.stoppedAtMilliseconds)
        XCTAssertTrue((6_000...8_200).contains(stoppedAt), "stopped at \(stoppedAt)")

        let hold = try makeProcessor(.hold(maximumDuration: nil), sampleRate: 48_000)
        let held = feedMono(audio, sampleRate: 48_000, framesPerBuffer: 4_800, to: hold)
        XCTAssertTrue(stopRequests(in: held.events).isEmpty)
        XCTAssertEqual(Double(try XCTUnwrap(hold.finish()).samples.count), 20 * 16_000, accuracy: 64)
    }

    func testSilenceIsJudgedOnTenMillisecondBlocksWhateverTheBufferSize() throws {
        let room = AudioTestSignal.pink(seconds: 12, rmsDbfs: -70, seed: 22)
        let audio = AudioTestSignal.mix(
            room, AudioTestSignal.syntheticSpeech(seconds: 3, peakDbfs: -35, seed: 23), startSeconds: 1)

        var kept: [Int] = []
        for framesPerBuffer in [480, 2_048, 4_800, 19_200] {
            let processor = try makeProcessor(.toggle(maximumDuration: nil), sampleRate: 48_000)
            let outcome = feedMono(audio, sampleRate: 48_000, framesPerBuffer: framesPerBuffer, to: processor)
            XCTAssertEqual(stopRequests(in: outcome.events).count, 1, "buffers of \(framesPerBuffer)")
            kept.append(try XCTUnwrap(processor.finish()).samples.count)
        }

        // The stop lands on the same 10 ms block, so every capture keeps the same audio, within the resampler's
        // tail.
        for count in kept.dropFirst() {
            XCTAssertEqual(Double(count), Double(kept[0]), accuracy: 32)
        }
    }
}

/// A port of Windows' `CaptureSignalAnalyzerTests`: the shapes that make captures look alike while needing
/// different fixes.
final class CaptureSignalReportTests: XCTestCase {
    private func interleaved(frames: Int, _ channels: [(Int) -> Float]) -> [Float] {
        var samples: [Float] = []
        samples.reserveCapacity(frames * channels.count)
        for frame in 0..<frames {
            for channel in channels {
                samples.append(channel(frame))
            }
        }
        return samples
    }

    private func tone(_ frame: Int, _ amplitude: Float) -> Float {
        amplitude * Float(sin(Double(frame) * 0.05))
    }

    func testASilentSecondChannelIsReported() {
        let report = CaptureSignalReport.analyze(
            interleaved: interleaved(frames: 4_800, [{ self.tone($0, 0.5) }, { _ in 0 }]), channels: 2,
            sampleRate: 48_000)

        XCTAssertEqual(report.channels, 2)
        XCTAssertTrue(report.hasSilentChannel)
        XCTAssertGreaterThan(report.perChannel[0].peak, 0.4)
        XCTAssertEqual(report.perChannel[1].peak, 0)
    }

    func testTwoChannelsCarryingTheSameMicrophoneAreNotFlagged() {
        let report = CaptureSignalReport.analyze(
            interleaved: interleaved(frames: 4_800, [{ self.tone($0, 0.5) }, { self.tone($0, 0.5) }]), channels: 2,
            sampleRate: 48_000)

        XCTAssertFalse(report.hasSilentChannel)
        XCTAssertFalse(report.channelsDiverge)
    }

    func testChannelsAtVeryDifferentLevelsAreFlaggedAsDiverging() {
        let report = CaptureSignalReport.analyze(
            interleaved: interleaved(frames: 4_800, [{ self.tone($0, 0.5) }, { self.tone($0, 0.02) }]), channels: 2,
            sampleRate: 48_000)

        XCTAssertFalse(report.hasSilentChannel)
        XCTAssertTrue(report.channelsDiverge)
    }

    func testQuietAudioIsDistinguishableFromDigitalSilence() {
        let quiet = CaptureSignalReport.analyze(
            interleaved: interleaved(frames: 4_800, [{ self.tone($0, 0.004) }]), channels: 1, sampleRate: 48_000)
        let healthy = CaptureSignalReport.analyze(
            interleaved: interleaved(frames: 4_800, [{ self.tone($0, 0.5) }]), channels: 1, sampleRate: 48_000)

        XCTAssertTrue((-60 ... -45).contains(quiet.peakDbfs), "\(quiet.peakDbfs)")
        XCTAssertTrue((-10...0).contains(healthy.peakDbfs), "\(healthy.peakDbfs)")
    }

    func testClippingIsMeasuredAsAFractionOfSamples() {
        let report = CaptureSignalReport.analyze(
            interleaved: interleaved(frames: 1_000, [{ $0 % 2 == 0 ? 1 : 0.1 }]), channels: 1, sampleRate: 48_000)

        XCTAssertEqual(report.clippedFraction, 0.5, accuracy: 0.01)
    }

    func testADcOffsetIsReported() {
        let report = CaptureSignalReport.analyze(
            interleaved: interleaved(frames: 4_800, [{ 0.3 + self.tone($0, 0.1) }]), channels: 1, sampleRate: 48_000)

        XCTAssertEqual(report.dcOffset, 0.3, accuracy: 0.05)
    }

    func testSixteenBitInputIsMeasuredThroughTheCapturePath() throws {
        let processor = CaptureProcessor(owner: RecordingID(rawValue: 1), policy: .hold())
        try processor.configure(
            inputFormat: AudioTestBuffers.format(sampleRate: 48_000, channels: 1, commonFormat: .pcmFormatInt16))
        _ = processor.process(
            AudioTestBuffers.make(
                sampleRate: 48_000, channels: [(0..<2_000).map { Float(sin(Double($0) * 0.05)) * 0.5 }],
                commonFormat: .pcmFormatInt16))

        let signal = try XCTUnwrap(try XCTUnwrap(processor.finish()).summary.signal)
        XCTAssertTrue((-8 ... -4).contains(signal.peakDbfs), "\(signal.peakDbfs)")
    }

    func testNothingMeasuredGivesAnEmptyReportRatherThanAWrongOne() {
        let report = CaptureSignalReport.analyze(interleaved: [], channels: 2, sampleRate: 48_000)

        XCTAssertTrue(report.perChannel.isEmpty)
        XCTAssertEqual(report.peak, 0)
        XCTAssertFalse(report.hasSilentChannel)
    }

    func testSilenceReadsAsANumberAndTheLogFieldsHoldOnlyShapes() {
        let report = CaptureSignalReport.analyze(
            interleaved: interleaved(frames: 4_800, [{ self.tone($0, 0.5) }, { _ in 0 }]), channels: 2,
            sampleRate: 48_000)
        let silent = CaptureSignalReport.analyze(interleaved: [0, 0, 0], channels: 1, sampleRate: 48_000)

        XCTAssertEqual(silent.peakDbfs, -99)
        let line = ScribeLog.render(.info, .audio, "Capture complete", report.logFields).line
        XCTAssertTrue(line.contains("channels=2"), line)
        XCTAssertTrue(line.contains("deviceRate=48000"), line)
        XCTAssertTrue(line.contains("silentChannel=true"), line)
        XCTAssertFalse(line.contains("=inf") || line.contains("=-inf") || line.contains("=nan"), line)
        let silentLine = ScribeLog.render(.info, .audio, "Capture complete", silent.logFields).line
        XCTAssertTrue(silentLine.contains("peakDbfs=-99.0"), silentLine)
    }
}
