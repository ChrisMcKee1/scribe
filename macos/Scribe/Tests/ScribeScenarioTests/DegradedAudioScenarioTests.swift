import Foundation
import XCTest

@testable import Scribe

final class DegradedAudioScenarioTests: XCTestCase {
    func testSeededNoiseHasTheMeasuredSNRAndNeverChangesDuration() throws {
        let clip = try ScenarioLibrary.shared().clip("sentence")
        for snr in [0.0, 10, 20] {
            let noise = ScenarioAudio.noiseAtSNR(clip.samples, snrDb: snr, seed: 42)
            XCTAssertEqual(noise, ScenarioAudio.noiseAtSNR(clip.samples, snrDb: snr, seed: 42))
            XCTAssertEqual(noise.count, clip.samples.count)
            let measured = 20 * log10(ScenarioAudio.rms(clip.samples) / ScenarioAudio.rms(noise))
            XCTAssertEqual(measured, snr, accuracy: 0.001)
            let mixed = ScenarioAudio.added(clip.samples, noise)
            XCTAssertEqual(mixed.count, clip.samples.count)
            XCTAssertTrue(mixed.allSatisfy(\.isFinite))
        }
    }

    func testReflectionsHaveTheDeclaredDelaysAndGainsWithoutAnExtraTail() {
        var impulse = [Float](repeating: 0, count: 16000)
        impulse[0] = 1
        let reflected = ScenarioAudio.reflected(impulse, sampleRate: 16000)
        XCTAssertEqual(reflected.count, impulse.count)
        XCTAssertEqual(reflected[0], 1)
        XCTAssertEqual(reflected[592], 0.35)
        XCTAssertEqual(reflected[1168], 0.2)
        XCTAssertEqual(reflected[1808], 0.1)
        XCTAssertEqual(reflected.filter { $0 != 0 }.count, 4)
        XCTAssertEqual(ScenarioAudio.reflected([], sampleRate: 16000), [])
    }
}
