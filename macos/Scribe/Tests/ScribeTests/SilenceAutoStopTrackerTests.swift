import XCTest

@testable import Scribe

/// A port of Windows' `SilenceAutoStopTrackerTests`. The scenarios run real 10 ms blocks of 48 kHz audio
/// through the tracker, the block size `CaptureProcessor` feeds it at that rate. Every signal is synthetic and
/// seeded, so each case is deterministic.
final class SilenceAutoStopTrackerTests: XCTestCase {
    private let voice: Float = 0.3
    private let quiet: Float = 0.005

    private func makeTracker(
        silenceHold: Duration = .seconds(4),
        leadIn: Duration = .seconds(10)
    ) throws -> SilenceAutoStopTracker {
        let configuration = try XCTUnwrap(SilenceAutoStopConfiguration(silenceHold: silenceHold, leadInLimit: leadIn))
        return SilenceAutoStopTracker(configuration: configuration)
    }

    func testFiresAfterTheHoldWindowOfSilenceFollowingSpeech() throws {
        var tracker = try makeTracker()

        XCTAssertFalse(tracker.update(level: voice, atMilliseconds: 500))
        XCTAssertFalse(tracker.update(level: quiet, atMilliseconds: 1_000))
        XCTAssertFalse(tracker.update(level: quiet, atMilliseconds: 4_400))
        XCTAssertTrue(tracker.update(level: quiet, atMilliseconds: 4_500))
    }

    func testSpeechResetsTheSilenceWindow() throws {
        var tracker = try makeTracker()

        _ = tracker.update(level: voice, atMilliseconds: 500)
        _ = tracker.update(level: quiet, atMilliseconds: 3_000)
        XCTAssertFalse(tracker.update(level: voice, atMilliseconds: 4_000))
        XCTAssertFalse(tracker.update(level: quiet, atMilliseconds: 7_900))
        XCTAssertTrue(tracker.update(level: quiet, atMilliseconds: 8_000))
    }

    /// A level that never moves is what steady noise looks like, however loud, and steady noise must not hold
    /// the microphone open. Real speech never holds one level; the synthetic speech test pins that case.
    func testAConstantLevelIsSteadyNoiseHoweverLoudAndEndsOnTheLeadIn() throws {
        var tracker = try makeTracker()

        var stoppedAt: Int64?
        var timestamp: Int64 = 10
        while timestamp <= 30_000, stoppedAt == nil {
            if tracker.update(level: voice, atMilliseconds: timestamp) {
                stoppedAt = timestamp
            }
            timestamp += 10
        }

        XCTAssertEqual(stoppedAt, 10_000)
        XCTAssertFalse(tracker.heardSpeech)
    }

    func testPureSilenceFiresAtTheLeadInLimitSoAMutedMicrophoneCannotStayOn() throws {
        var tracker = try makeTracker()

        XCTAssertFalse(tracker.update(level: quiet, atMilliseconds: 9_900))
        XCTAssertTrue(tracker.update(level: quiet, atMilliseconds: 10_000))
    }

    func testReportsWhetherItEverHeardSpeechAndHowLoudTheInputGot() throws {
        var wentQuiet = try makeTracker()
        _ = wentQuiet.update(level: voice, atMilliseconds: 500)
        XCTAssertTrue(wentQuiet.update(level: quiet, atMilliseconds: 4_500))
        XCTAssertTrue(wentQuiet.heardSpeech)
        XCTAssertEqual(wentQuiet.peakLevel, voice)

        var neverHeard = try makeTracker()
        XCTAssertTrue(neverHeard.update(level: quiet, atMilliseconds: 10_000))
        XCTAssertFalse(neverHeard.heardSpeech)
        XCTAssertEqual(neverHeard.peakLevel, quiet)
    }

    func testPeakLevelSurvivesAQuietBlockAfterALoudOne() throws {
        var tracker = try makeTracker()

        _ = tracker.update(level: 0.42, atMilliseconds: 100)
        _ = tracker.update(level: quiet, atMilliseconds: 200)

        XCTAssertEqual(tracker.peakLevel, 0.42)
    }

    /// A thinking pause before the first word must not end the recording at the shorter hold window.
    func testTheLeadInIsLongerThanThePostSpeechHold() throws {
        var tracker = try makeTracker()

        XCTAssertFalse(tracker.update(level: quiet, atMilliseconds: 5_000))
        XCTAssertFalse(tracker.update(level: voice, atMilliseconds: 6_000))
        XCTAssertFalse(tracker.update(level: quiet, atMilliseconds: 9_900))
        XCTAssertTrue(tracker.update(level: quiet, atMilliseconds: 10_000))
    }

    func testAnAbsoluteFloorOutsideTheLevelRangeOrANegativeDurationIsRejected() {
        for floor: Float in [0, -0.1, 1.5, .nan] {
            XCTAssertNil(SilenceAutoStopConfiguration(absoluteFloor: floor), "\(floor)")
        }
        XCTAssertNil(SilenceAutoStopConfiguration(silenceHold: .seconds(-1)))
        XCTAssertNil(SilenceAutoStopConfiguration(leadInLimit: .seconds(-1)))
        XCTAssertNotNil(SilenceAutoStopConfiguration(absoluteFloor: 1))
    }

    func testAnEndlessLeadInNeverFiresWithoutSpeech() throws {
        var tracker = try makeTracker(leadIn: .seconds(Int64.max))

        XCTAssertFalse(tracker.update(level: quiet, atMilliseconds: 86_400_000))
    }

    // MARK: - Scenarios on real 10 ms blocks of 48 kHz audio

    func testQuietSpeechPeakingAtMinus40DbfsIsHeardAndStopsAfterTheHoldNotTheLeadIn() {
        let selfNoise = AudioTestSignal.pink(seconds: 24, rmsDbfs: -75, seed: 11)
        let audio = AudioTestSignal.mix(
            selfNoise, AudioTestSignal.syntheticSpeech(seconds: 15, peakDbfs: -40, seed: 5), startSeconds: 1)

        let outcome = run(audio)

        XCTAssertTrue(outcome.heardSpeech)
        XCTAssertNotNil(outcome.stoppedAt)
        assertStopped(outcome, within: 16_000...20_000)
    }

    func testLoudSpeechSurvivesATwoSecondPauseAndStopsWithinTheHoldAfterTheLastWord() {
        let room = AudioTestSignal.pink(seconds: 20, rmsDbfs: -60, seed: 3)
        var audio = AudioTestSignal.mix(
            room, AudioTestSignal.syntheticSpeech(seconds: 5, peakDbfs: -6, seed: 8), startSeconds: 1)
        audio = AudioTestSignal.mix(
            audio, AudioTestSignal.syntheticSpeech(seconds: 4, peakDbfs: -6, seed: 9), startSeconds: 8)

        let outcome = run(audio)

        XCTAssertTrue(outcome.heardSpeech)
        assertStopped(outcome, within: 12_000...16_000)
    }

    /// A fixed threshold counted every block of this noise as speech and never stopped.
    func testSteadyPinkNoiseAtMinus30DbfsIsLearnedAndEndsOnTheLeadInAsNoSpeech() {
        for seed: UInt64 in [1, 2, 3] {
            var tracker = SilenceAutoStopTracker()
            let noise = AudioTestSignal.pink(seconds: 30, rmsDbfs: -30, seed: seed)

            let outcome = run(noise, tracker: &tracker)

            XCTAssertEqual(outcome.stoppedAt, 10_000, "seed \(seed)")
            XCTAssertFalse(outcome.heardSpeech, "seed \(seed)")
            XCTAssertGreaterThan(tracker.voiceThreshold, outcome.loudestBlockPeak, "seed \(seed)")
        }
    }

    func testSpeechInSteadyNoiseIsHeardAndTheNoiseAloneThenLetsTheRecordingEnd() {
        let noise = AudioTestSignal.pink(seconds: 25, rmsDbfs: -30, seed: 4)
        let audio = AudioTestSignal.mix(
            noise, AudioTestSignal.syntheticSpeech(seconds: 10, peakDbfs: -6, seed: 6), startSeconds: 2)

        let outcome = run(audio)

        XCTAssertTrue(outcome.heardSpeech)
        assertStopped(outcome, within: 12_000...16_000)
    }

    /// Speech in a quiet room, then a fan starts. Until the floor climbs to the fan the noise reads as voice, so
    /// the recording ends later than the speech alone would have, but it ends.
    func testNoiseThatStartsMidRecordingIsLearnedSoItCannotHoldTheRecordingOpen() {
        let room = AudioTestSignal.pink(seconds: 40, rmsDbfs: -65, seed: 7)
        var audio = AudioTestSignal.mix(
            room, AudioTestSignal.syntheticSpeech(seconds: 3, peakDbfs: -6, seed: 10), startSeconds: 1)
        audio = AudioTestSignal.mix(audio, AudioTestSignal.pink(seconds: 34, rmsDbfs: -30, seed: 12), startSeconds: 6)

        let outcome = run(audio)

        XCTAssertTrue(outcome.heardSpeech)
        assertStopped(outcome, within: 6_000...20_000)
    }

    /// 1 dB/s from -60 to -20 dBFS, then level. With a long lead-in, any block that ever read as voice would show:
    /// the stop would then come 4 s after it, not exactly at the lead-in.
    func testNoiseThatRisesGraduallyIsFollowedAndNeverMistakenForSpeech() throws {
        var tracker = try makeTracker(leadIn: .seconds(60))
        let noise = AudioTestSignal.ramp(
            AudioTestSignal.pink(seconds: 62, rmsDbfs: 0, seed: 13), fromDbfs: -60, toDbfs: -20, overSeconds: 40)

        let outcome = run(noise, tracker: &tracker)

        XCTAssertEqual(outcome.stoppedAt, 60_000)
        XCTAssertFalse(outcome.heardSpeech)
    }

    /// The speaker drifts away from the microphone: -10 to -55 dBFS peak over 30 s. The absolute floor keeps the
    /// speech until it drops under about -45 dBFS, roughly 24 s in.
    func testSpeechThatFadesGraduallyIsHeardWellBelowTheOldThreshold() {
        let room = AudioTestSignal.pink(seconds: 40, rmsDbfs: -80, seed: 14)
        let speech = AudioTestSignal.ramp(
            AudioTestSignal.syntheticSpeech(seconds: 30, peakDbfs: 0, seed: 15), fromDbfs: -10, toDbfs: -55,
            overSeconds: 30)
        let audio = AudioTestSignal.mix(room, speech, startSeconds: 1)

        let outcome = run(audio)

        XCTAssertTrue(outcome.heardSpeech)
        assertStopped(outcome, within: 24_000...31_000)
    }

    func testDigitalSilenceEndsOnTheLeadInAsNoSpeech() {
        let outcome = run([Float](repeating: 0, count: 15 * AudioTestSignal.sampleRate))

        XCTAssertEqual(outcome.stoppedAt, 10_000)
        XCTAssertFalse(outcome.heardSpeech)
        XCTAssertEqual(outcome.loudestBlockPeak, 0)
    }

    func testSyntheticSpeechWithNaturalGapsNeverStopsHoweverLongItRuns() {
        let room = AudioTestSignal.pink(seconds: 60, rmsDbfs: -60, seed: 16)
        let audio = AudioTestSignal.mix(
            room, AudioTestSignal.syntheticSpeech(seconds: 60, peakDbfs: -12, seed: 17), startSeconds: 0)

        let outcome = run(audio)

        XCTAssertNil(outcome.stoppedAt)
        XCTAssertTrue(outcome.heardSpeech)
    }

    // MARK: - Helpers

    private func assertStopped(
        _ outcome: Outcome, within range: ClosedRange<Int64>, file: StaticString = #filePath, line: UInt = #line
    ) {
        guard let stoppedAt = outcome.stoppedAt else {
            return XCTFail("never stopped", file: file, line: line)
        }
        XCTAssertTrue(range.contains(stoppedAt), "stopped at \(stoppedAt)", file: file, line: line)
    }

    private struct Outcome {
        let stoppedAt: Int64?
        let heardSpeech: Bool
        let loudestBlockPeak: Float
    }

    private func run(_ samples: [Float]) -> Outcome {
        var tracker = SilenceAutoStopTracker()
        return run(samples, tracker: &tracker)
    }

    private func run(_ samples: [Float], tracker: inout SilenceAutoStopTracker) -> Outcome {
        let blockLength = AudioTestSignal.sampleRate / 100
        var loudest: Float = 0
        var block = 0
        while (block + 1) * blockLength <= samples.count {
            var peak: Float = 0
            for index in (block * blockLength)..<((block + 1) * blockLength) {
                peak = max(peak, abs(samples[index]))
            }
            loudest = max(loudest, peak)
            let timestamp = Int64(block + 1) * 10
            if tracker.update(level: peak, atMilliseconds: timestamp) {
                return Outcome(stoppedAt: timestamp, heardSpeech: tracker.heardSpeech, loudestBlockPeak: loudest)
            }
            block += 1
        }
        return Outcome(stoppedAt: nil, heardSpeech: tracker.heardSpeech, loudestBlockPeak: loudest)
    }
}

final class CaptureStopPolicyTests: XCTestCase {
    func testAHoldNeverStopsOnSilenceAndBothStopAtTheTenMinuteCeiling() {
        XCTAssertNil(CaptureStopPolicy.hold().silence)
        XCTAssertEqual(CaptureStopPolicy.toggle().silence, .standard)
        XCTAssertEqual(CaptureStopPolicy.hold().maximumDuration, .seconds(600))
        XCTAssertEqual(CaptureStopPolicy.toggle().maximumDuration, .seconds(600))
    }

    func testThePolicyFollowsTheGestureOfTheBindingThatFired() {
        XCTAssertEqual(CaptureStopPolicy(gesture: .hold), .hold())
        XCTAssertEqual(CaptureStopPolicy(gesture: .toggle), .toggle())
        XCTAssertFalse(CaptureStopPolicy(gesture: .toggle, autoStopOnSilence: false).stopsOnSilence)
        XCTAssertEqual(HotkeyBinding(keyCode: HotkeySettingsStore.capsLockKeyCode).gesture, .toggle)
    }

    func testAZeroOrNegativeCeilingMeansNoCeiling() {
        XCTAssertNil(CaptureStopPolicy.hold(maximumDuration: .zero).maximumDuration)
        XCTAssertNil(CaptureStopPolicy.toggle(maximumDuration: .seconds(-5)).maximumDuration)
        XCTAssertNil(CaptureStopPolicy.hold(maximumDuration: nil).maximumDuration)
    }

    func testTheStandardConfigurationIsWindowsDefaults() {
        let standard = SilenceAutoStopConfiguration.standard
        XCTAssertEqual(standard.absoluteFloor, 0.0056)
        XCTAssertEqual(standard.silenceHold, .seconds(4))
        XCTAssertEqual(standard.leadInLimit, .seconds(10))
    }
}
