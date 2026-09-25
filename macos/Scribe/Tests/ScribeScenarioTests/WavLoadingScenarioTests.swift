import Darwin
import Foundation
import XCTest

@testable import Scribe

/// The committed fixtures themselves, and the file Scribe writes from them for the recognizer: every WAV the manifests
/// name is there, is 16 kHz mono 16-bit PCM and as long as the manifest says, and the production scratch writer turns
/// its samples into a private 32-bit float WAV that reads back sample for sample.
final class WavLoadingScenarioTests: XCTestCase {
    func testEveryManifestEntryIsAReadableSixteenKilohertzMonoFixtureOfTheLengthItRecords() throws {
        let library = try ScenarioLibrary.shared()
        let report = ScenarioReport("wav-loading")
        let roles: Set<String> = ["smoke", "dictionary", "snippet", "speech", "numbers"]

        XCTAssertEqual(library.clips.filter { $0.role == "smoke" }.count, 4, "fixtures.json lists four phrases")
        XCTAssertEqual(library.clips.count, 14, "the two manifests list fourteen phrases between them")
        for clip in library.clips {
            XCTAssertEqual(clip.sampleRate, ScenarioLibrary.fixtureRate, clip.name)
            XCTAssertTrue(roles.contains(clip.role), "\(clip.name) has the role \(clip.role)")
            XCTAssertFalse(clip.text.trimmingCharacters(in: .whitespaces).isEmpty, clip.name)
            XCTAssertGreaterThan(clip.pcm.count, ScenarioLibrary.fixtureRate, "\(clip.name) is under a second long")
            XCTAssertGreaterThan(ScenarioAudio.peak(clip.samples), ScenarioAudio.amplitude(dbfs: -20), clip.name)
            if let recorded = clip.manifestSeconds {
                // The manifest rounds to hundredths.
                XCTAssertEqual(clip.seconds, recorded, accuracy: 0.0051, clip.name)
            } else {
                XCTAssertEqual(clip.role, "smoke", "\(clip.name) records no length")
            }
            if clip.role == "smoke" {
                XCTAssertTrue(clip.asserted, "\(clip.name): every smoke phrase is held to the overlap bar")
            }
        }
        XCTAssertEqual(
            library.clips.filter { !$0.asserted }.map(\.name), ["numbers-time"],
            "only the numbers phrase is reported rather than asserted")

        report.note("clips", count: library.clips.count)
        report.note("audioSeconds", value: library.clips.reduce(0) { $0 + $1.seconds }, digits: 2)
        report.write()
    }

    func testTheScratchWriterTurnsEveryFixtureIntoAPrivateFloatWavThatReadsBackExactly() throws {
        let library = try ScenarioLibrary.shared()
        let directory = try makeScenarioDirectory("scratch")
        let scratch = ScratchAudioDirectory(url: directory.appendingPathComponent("asr", isDirectory: true))
        let report = ScenarioReport("scratch-wav-round-trip")
        let clock = ContinuousClock()
        var writing = Duration.zero
        var bytes = 0

        for clip in library.clips {
            let started = clock.now
            let file = try scratch.writeRecording(samples: clip.samples, sampleRate: Double(clip.sampleRate))
            writing += started.duration(to: clock.now)
            let path = file.url.path(percentEncoded: false)

            var status = stat()
            XCTAssertEqual(lstat(path, &status), 0, clip.name)
            XCTAssertEqual(status.st_mode & 0o777, 0o600, "\(clip.name): only its owner may read the recording")
            bytes += Int(status.st_size)

            let written = try ScenarioWav.read(file.url)
            XCTAssertEqual(written.sampleRate, clip.sampleRate, clip.name)
            XCTAssertEqual(written.channels, 1, clip.name)
            guard case .float32(let samples) = written.samples else {
                XCTFail("\(clip.name) was not written as 32-bit float")
                continue
            }
            XCTAssertTrue(samples == clip.samples, "\(clip.name) changed on its way through the scratch file")

            scratch.remove(file)
            XCTAssertNotEqual(lstat(path, &status), 0, "\(clip.name): the recording is still there after removal")
        }

        report.note("files", count: library.clips.count)
        report.note("bytes", count: bytes)
        report.note("writing", duration: writing)
        report.write()
    }
}
