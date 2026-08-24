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
}
