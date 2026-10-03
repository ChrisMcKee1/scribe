import Foundation
import XCTest

@testable import Scribe

/// The installed recognizer on the committed fixtures, through the production `TranscriptionEngine`: the short phrases
/// one after another, with the first run's latency (cold, while Foundry Local loads the model) reported apart from the
/// rest (warm), and Windows' word-overlap bar held on every phrase the manifests mark as asserted.
///
/// Runs only when `SCRIBE_REAL_ASR=1`. The optional `real speech recognition` job of the macOS workflow sets it after
/// installing Foundry Local; everywhere else the test is skipped, because the runners that gate a change have no
/// recognizer, and whether a model of this size fits one is part of what the optional job finds out.
final class RealRecognizerScenarioTests: XCTestCase {
    /// Short enough for one dictation; the 23-second passage is left to the long-audio characterization.
    private static let longestClipSeconds = 10.0

    func testTheShortFixturesThroughTheInstalledRecognizer() async throws {
        guard ProcessInfo.processInfo.environment["SCRIBE_REAL_ASR"] == "1" else {
            throw XCTSkip("Set SCRIBE_REAL_ASR=1, with Foundry Local installed, to run the real recognizer.")
        }
        let library = try ScenarioLibrary.shared()
        let scratch = try makeScenarioDirectory("asr-scratch")
        let engine = TranscriptionEngine(scratch: ScratchAudioDirectory(url: scratch.appendingPathComponent("asr")))
        let backend = try engine.resolveBackend()
        let report = ScenarioReport("real-asr")
        report.note("backend", backend.kind.rawValue)
        report.note("model", backend.foundryModelAlias)

        let clips = library.clips.filter { $0.seconds < Self.longestClipSeconds }
        let clock = ContinuousClock()
        var warm: [Double] = []
        var failures = 0
        for (index, clip) in clips.enumerated() {
            let started = clock.now
            do {
                let result = try await engine.transcribe(samples: clip.samples, sampleRate: Double(clip.sampleRate))
                let elapsed = ScenarioReport.milliseconds(started.duration(to: clock.now))
                let overlap = ScenarioText.wordOverlap(expected: clip.text, actual: result.text)
                if index == 0 {
                    report.note("coldMs", value: elapsed, digits: 0)
                } else {
                    warm.append(elapsed)
                }
                report.note("\(clip.name).overlap", value: overlap, digits: 2)
                report.note("\(clip.name).ms", value: elapsed, digits: 0)
                report.note("\(clip.name).realTimeFactor", value: elapsed / 1_000 / clip.seconds, digits: 3)
                report.note("\(clip.name).text", "\"\(result.text)\"")
                if clip.asserted {
                    XCTAssertGreaterThanOrEqual(overlap, ScenarioText.minimumOverlap, "\(clip.name): \(result.text)")
                }
            } catch {
                failures += 1
                report.note("\(clip.name).error", String(describing: error))
                XCTFail("\(clip.name) failed to transcribe: \(error)")
            }
        }
        let sorted = warm.sorted()
        if !sorted.isEmpty {
            report.note("warmMedianMs", value: sorted[sorted.count / 2], digits: 0)
            report.note("warmMaxMs", value: sorted[sorted.count - 1], digits: 0)
        }
        report.note("clips", count: clips.count)
        report.note("failures", count: failures)
        report.write()
    }

    func testLongCommittedFixtureUsesBoundedChunksWithTheInstalledRecognizer() async throws {
        guard ProcessInfo.processInfo.environment["SCRIBE_REAL_ASR"] == "1" else {
            throw XCTSkip("Set SCRIBE_REAL_ASR=1, with Foundry Local installed, to run the real recognizer.")
        }
        let clip = try ScenarioLibrary.shared().clip("long-passage")
        let samples = clip.samples + [Float](repeating: 0, count: clip.sampleRate) + clip.samples
        let spans = TranscriptionChunker.plan(samples: samples, sampleRate: clip.sampleRate)
        XCTAssertEqual(spans.count, 2)
        XCTAssertTrue(spans.allSatisfy { $0.count <= TranscriptionChunker.maxChunkSeconds * clip.sampleRate })

        let scratch = try makeScenarioDirectory("asr-long")
        let engine = TranscriptionEngine(
            scratch: ScratchAudioDirectory(url: scratch.appendingPathComponent("asr", isDirectory: true)))
        let backend = try engine.resolveBackend()
        XCTAssertEqual(backend.kind, .foundryLocal)
        XCTAssertEqual(backend.foundryModelAlias, TranscriptionBackendResolver.defaultFoundryModelAlias)

        let result = try await engine.transcribe(samples: samples, sampleRate: Double(clip.sampleRate))
        let expectedOverlap = ScenarioText.wordOverlap(expected: clip.text, actual: result.text)
        let recognizedWords = ScenarioPrivacy.words(result.text)

        XCTAssertGreaterThanOrEqual(expectedOverlap, ScenarioText.minimumOverlap)
        XCTAssertGreaterThanOrEqual(recognizedWords.count, ScenarioPrivacy.words(clip.text).count * 3 / 2)
    }

    func testDegradedAndStereoCapturesThroughTheInstalledRecognizer() async throws {
        guard ProcessInfo.processInfo.environment["SCRIBE_REAL_ASR"] == "1" else {
            throw XCTSkip("Set SCRIBE_REAL_ASR=1, with the cached Foundry speech model, for real degraded audio.")
        }
        let clip = try ScenarioLibrary.shared().clip("longer")
        let scratch = try makeScenarioDirectory("asr-degraded")
        let engine = TranscriptionEngine(
            scratch: ScratchAudioDirectory(url: scratch.appendingPathComponent("asr", isDirectory: true)))
        let backend = try engine.resolveBackend()
        XCTAssertEqual(backend.kind, .foundryLocal)
        let report = ScenarioReport("real-asr-degraded")
        let noisy = ScenarioAudio.added(
            clip.samples, ScenarioAudio.noiseAtSNR(clip.samples, snrDb: 10, seed: 42))
        let reverberant = ScenarioAudio.reflected(clip.samples, sampleRate: clip.sampleRate)
        let equalNoise = ScenarioAudio.added(
            clip.samples, ScenarioAudio.noiseAtSNR(clip.samples, snrDb: 0, seed: 43))
        let noisyReflections = ScenarioAudio.added(
            reverberant, ScenarioAudio.noiseAtSNR(reverberant, snrDb: 10, seed: 44))
        let signals = [
            ("noise-10db", noisy, ScenarioChannelLayout.mono, 16000.0),
            ("noise-0db", equalNoise, .mono, 16000.0),
            ("reflections", reverberant, .mono, 16000.0),
            ("reflections-noise-10db", noisyReflections, .mono, 16000.0),
            ("stereo-left", clip.samples, .stereoOne(voice: 0, otherHasFloor: false), 44100.0),
            ("stereo-right-floor", clip.samples, .stereoOne(voice: 1, otherHasFloor: true), 48000.0),
        ]
        for (name, samples, layout, rate) in signals {
            let audio = try ScenarioDeviceAudio.device(
                playing: samples, at: rate, layout: layout, encoding: .float32, seed: 42)
            let device = ScenarioCaptureDevice(audio: audio)
            let capture = AudioCaptureEngine(makeDevice: { device })
            let owner = RecordingID.next()
            let opened = try await underWatchdog("degraded microphone open") {
                try await capture.start(owner: owner, policy: .hold(maximumDuration: nil), events: { _ in })
            }
            XCTAssertEqual(opened, .live)
            let stream = device.stream(
                frameCounts: ScenarioAudio.frameCounts(total: audio.frameCount, seed: 42, within: 17...4801))
            try await underWatchdog("degraded device delivery") { try await stream.finished.wait() }
            let seal = try XCTUnwrap(capture.retire(owner: owner))
            let sealed = try await underWatchdog("degraded capture seal") { await seal.audio }
            let captured = try XCTUnwrap(sealed)
            XCTAssertEqual(captured.summary.sampleRate, 16000)
            XCTAssertEqual(captured.summary.droppedBufferCount, 0)
            let result = try await engine.transcribe(samples: captured.samples, sampleRate: 16000)
            let overlap = ScenarioText.wordOverlap(expected: clip.text, actual: result.text)
            report.note("\(name).overlap", value: overlap, digits: 3)
            report.note("\(name).samples", count: captured.samples.count)
            XCTAssertGreaterThanOrEqual(overlap, ScenarioText.minimumOverlap, name)
        }
        report.write()
    }

    func testDurationAndDegradationSweepKeepsRepeatedSpeechAcrossChunkSeams() async throws {
        guard ProcessInfo.processInfo.environment["SCRIBE_REAL_ASR"] == "1" else {
            throw XCTSkip("Set SCRIBE_REAL_ASR=1 with the cached Foundry speech model for the duration sweep.")
        }
        let clip = try ScenarioLibrary.shared().clip("sentence")
        let gap = [Float](repeating: 0, count: clip.sampleRate / 4)
        let unit = clip.samples + gap
        let scratch = try makeScenarioDirectory("asr-duration")
        let engine = TranscriptionEngine(
            scratch: ScratchAudioDirectory(url: scratch.appendingPathComponent("asr", isDirectory: true)))
        XCTAssertEqual(try engine.resolveBackend().kind, .foundryLocal)
        let report = ScenarioReport("real-asr-duration")
        for seconds in [5, 20, 45, 90] {
            let targetSamples = seconds * clip.sampleRate
            let repetitions = targetSamples / unit.count
            XCTAssertGreaterThan(repetitions, 0)
            guard repetitions > 0 else { continue }
            var clean: [Float] = []
            for _ in 0..<repetitions { clean += unit }
            clean += [Float](repeating: 0, count: targetSamples - clean.count)
            let expected = Array(repeating: clip.text, count: repetitions).joined(separator: " ")
            let reflected = ScenarioAudio.reflected(clean, sampleRate: clip.sampleRate)
            let signals = [
                ("clean", clean),
                ("noise-10db", ScenarioAudio.added(clean, ScenarioAudio.noiseAtSNR(clean, snrDb: 10, seed: 81))),
                (
                    "reflections-noise-0db",
                    ScenarioAudio.added(reflected, ScenarioAudio.noiseAtSNR(reflected, snrDb: 0, seed: 82))
                ),
            ]
            for (condition, samples) in signals {
                let name = "\(seconds)s-\(condition)"
                XCTAssertEqual(samples.count, targetSamples)
                let spans = TranscriptionChunker.plan(samples: samples, sampleRate: clip.sampleRate)
                XCTAssertEqual(spans.reduce(0) { $0 + $1.count }, samples.count)
                XCTAssertTrue(spans.allSatisfy { $0.count <= TranscriptionChunker.maxChunkSeconds * clip.sampleRate })
                XCTAssertEqual(spans.first?.lowerBound, 0)
                XCTAssertEqual(spans.last?.upperBound, samples.count)
                for (left, right) in zip(spans, spans.dropFirst()) {
                    XCTAssertEqual(left.upperBound, right.lowerBound)
                }
                let clock = ContinuousClock()
                let began = clock.now
                let result = try await engine.transcribe(samples: samples, sampleRate: Double(clip.sampleRate))
                let overlap = ScenarioText.wordOverlap(expected: expected, actual: result.text)
                let retained = ScenarioText.retainedWordShare(expected: expected, actual: result.text)
                report.note("\(name).overlap", value: overlap, digits: 3)
                report.note("\(name).retained", value: retained, digits: 3)
                report.note("\(name).chunks", count: spans.count)
                report.note("\(name).ms", value: ScenarioReport.milliseconds(began.duration(to: clock.now)), digits: 0)
                XCTAssertGreaterThanOrEqual(overlap, ScenarioText.minimumOverlap, name)
                XCTAssertGreaterThanOrEqual(retained, 0.8, "\(name): repeated speech was lost")
            }
        }
        report.write()
    }
}
