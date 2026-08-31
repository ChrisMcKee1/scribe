import XCTest
@testable import Scribe

final class SilenceAutoStopDetectorTests: XCTestCase {
    private func level(rmsDbfs: Float) -> AudioLevelMeasurement {
        // rmsAmplitude is derived to produce the desired dBFS via 20*log10(amplitude).
        let amplitude = powf(10, rmsDbfs / 20)
        return AudioLevelMeasurement(peakAmplitude: amplitude, rmsAmplitude: amplitude)
    }

    func testNoTriggerBeforeSpeech() {
        let detector = SilenceAutoStopDetector(silenceThresholdDbfs: -45, requiredSilenceDuration: 2.0)
        let start = Date()
        // Room silence from the very start of capture should never auto-stop; the user hasn't
        // spoken yet.
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start))
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(5)))
    }

    func testTriggersAfterSilenceFollowingSpeech() {
        let detector = SilenceAutoStopDetector(silenceThresholdDbfs: -45, requiredSilenceDuration: 2.0)
        let start = Date()

        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -20), at: start)) // speech
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(0.5))) // silence begins
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(1.5))) // still under 2s
        XCTAssertTrue(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(2.6))) // 2.1s of silence, fires
    }

    func testSpeechResumptionResetsTimer() {
        let detector = SilenceAutoStopDetector(silenceThresholdDbfs: -45, requiredSilenceDuration: 2.0)
        let start = Date()

        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -20), at: start))
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(1.0)))
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(1.9)))
        // Speech resumes just before the threshold would have fired.
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -20), at: start.addingTimeInterval(1.95)))
        // Silence again; the 2s window must restart from here, not from the original silence start.
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(3.0)))
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(4.5)))
        XCTAssertTrue(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(5.1)))
    }

    func testFiresOnlyOnce() {
        let detector = SilenceAutoStopDetector(silenceThresholdDbfs: -45, requiredSilenceDuration: 1.0)
        let start = Date()

        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -20), at: start))
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(0.1)))
        XCTAssertTrue(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(1.2)))
        // Detector keeps returning false for subsequent silent chunks without an explicit reset,
        // since the caller is expected to discard the instance on the first true result.
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(2.0)))
    }

    func testResetClearsState() {
        let detector = SilenceAutoStopDetector(silenceThresholdDbfs: -45, requiredSilenceDuration: 1.0)
        let start = Date()

        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -20), at: start))
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(0.5)))
        detector.reset()
        // After reset, silence alone (with no new speech) must not trigger, since hasObservedSpeech
        // was cleared.
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -60), at: start.addingTimeInterval(5.0)))
    }

    // MARK: - Adaptive noise floor

    func testEffectiveThresholdNeverExceedsTheFixedDefault() {
        let detector = SilenceAutoStopDetector(silenceThresholdDbfs: -45, requiredSilenceDuration: 2.0)
        let start = Date()

        // Sustained loud "speech" chunks must never push the effective threshold above the fixed
        // default: only quiet chunks are allowed to move the floor, so a long dictation can never
        // make the detector more trigger-happy than its tested baseline.
        for i in 0..<200 {
            _ = detector.observe(level: level(rmsDbfs: -15), at: start.addingTimeInterval(Double(i) * 0.05))
            XCTAssertLessThanOrEqual(detector.silenceThresholdDbfs, -45)
        }
    }

    func testAdaptiveFloorTightensInAQuietRoomAndCorrectlyRecognizesSoftSpeech() {
        let detector = SilenceAutoStopDetector(silenceThresholdDbfs: -45, requiredSilenceDuration: 2.0)
        let start = Date()

        // A very quiet capture (well below the -45 dBFS default) drives the floor down over many
        // samples, so the effective threshold tightens well below the fixed default.
        var t = 0.0
        for _ in 0..<200 {
            _ = detector.observe(level: level(rmsDbfs: -70), at: start.addingTimeInterval(t))
            t += 0.05
        }
        XCTAssertLessThan(detector.silenceThresholdDbfs, -50, "the floor should have tightened well below the fixed default")

        // A moderately quiet chunk (-50 dBFS) would have been misread as silence under the fixed
        // -45 dBFS default (since -50 < -45); with the floor tightened, it's correctly recognized
        // as real signal above the ambient floor instead.
        XCTAssertFalse(detector.observe(level: level(rmsDbfs: -50), at: start.addingTimeInterval(t)))
    }

    func testAdaptiveThresholdNeverDropsBelowItsFloorBound() {
        let detector = SilenceAutoStopDetector(silenceThresholdDbfs: -45, requiredSilenceDuration: 2.0)
        let start = Date()

        var t = 0.0
        for _ in 0..<500 {
            _ = detector.observe(level: level(rmsDbfs: -99), at: start.addingTimeInterval(t))
            t += 0.05
        }
        XCTAssertGreaterThanOrEqual(detector.silenceThresholdDbfs, -60)
    }
}
