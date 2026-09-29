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
}
