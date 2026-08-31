import XCTest
@testable import Scribe

/// Direct port of Windows' `DictationStatsTests`, verifying the aggregation math is unchanged
/// (percentile interpolation, RTF, decode/cleanup/combined summaries, window and zero-audio
/// filtering).
final class DictationStatsTests: XCTestCase {
    private func entry(
        audioMs: Double,
        decodeMs: Double?,
        cleanupMs: Double? = nil,
        ageHours: Double = 1
    ) -> DictationHistoryRecord {
        DictationHistoryRecord(
            startedAt: Date().addingTimeInterval(-ageHours * 3600),
            durationSeconds: audioMs / 1000.0,
            sampleCount: 0,
            decodeMilliseconds: decodeMs,
            cleanupMilliseconds: cleanupMs)
    }

    func testPercentileInterpolatesBetweenSamples() {
        let sorted: [Double] = [100, 200, 300, 400]

        XCTAssertEqual(DictationStats.percentile(sorted, 0.50), 250)
        XCTAssertEqual(DictationStats.percentile(sorted, 0.0), 100)
        XCTAssertEqual(DictationStats.percentile(sorted, 1.0), 400)
        XCTAssertEqual(DictationStats.percentile(sorted, 0.95), 385, accuracy: 1e-6)
    }

    func testPercentileOfASingleSampleIsThatSample() {
        XCTAssertEqual(DictationStats.percentile([42.0], 0.95), 42)
    }

    func testComputeAggregatesDecodeAndRtf() {
        let entries = [
            entry(audioMs: 10_000, decodeMs: 1_000, cleanupMs: 400), // RTF 0.10
            entry(audioMs: 20_000, decodeMs: 3_000, cleanupMs: 700), // RTF 0.15
            entry(audioMs: 5_000, decodeMs: 1_000),                  // RTF 0.20
        ]

        let stats = DictationStats.compute(entries: entries, since: Date().addingTimeInterval(-7 * 86400))

        XCTAssertNotNil(stats)
        XCTAssertEqual(stats?.count, 3)
        XCTAssertEqual(stats?.totalAudioSeconds, 35.0)
        XCTAssertEqual(stats?.decodeCount, 3)
        XCTAssertEqual(stats?.decodeMs?.average ?? -1, 5.0 / 3.0 * 1000.0, accuracy: 1e-6)
        XCTAssertEqual(stats?.decodeMs?.min, 1_000)
        XCTAssertEqual(stats?.decodeMs?.max, 3_000)
        XCTAssertEqual(stats?.decodeMs?.p50, 1_000)
        XCTAssertEqual(stats?.fastestRtf ?? -1, 0.10, accuracy: 1e-6)
        XCTAssertEqual(stats?.rtfP50 ?? -1, 0.15, accuracy: 1e-6)
        XCTAssertEqual(stats?.longestAudioSeconds, 20)
        XCTAssertEqual(stats?.cleanupCount, 2)
        XCTAssertNotNil(stats?.cleanupMs)
        XCTAssertEqual(stats?.cleanupMs?.average ?? -1, 550, accuracy: 1e-6)
        XCTAssertEqual(stats?.cleanupMs?.min, 400)
        XCTAssertEqual(stats?.cleanupMs?.max, 700)
        XCTAssertEqual(stats?.combinedCount, 2)
        XCTAssertEqual(stats?.combinedMs?.average ?? -1, 2_550, accuracy: 1e-6)
        XCTAssertEqual(stats?.combinedMs?.min, 1_400)
        XCTAssertEqual(stats?.combinedMs?.max, 3_700)
    }

    func testComputeExcludesEntriesOutsideTheWindowAndZeroLengthAudio() {
        let entries = [
            entry(audioMs: 10_000, decodeMs: 1_000, ageHours: 1),
            entry(audioMs: 10_000, decodeMs: 9_999, ageHours: 24 * 30), // too old
            entry(audioMs: 0, decodeMs: 50),                             // undefined RTF
        ]

        let stats = DictationStats.compute(entries: entries, since: Date().addingTimeInterval(-7 * 86400))

        XCTAssertNotNil(stats)
        XCTAssertEqual(stats?.count, 1)
        XCTAssertEqual(stats?.decodeMs?.p95, 1_000)
        XCTAssertNil(stats?.cleanupMs)
        XCTAssertNil(stats?.combinedMs)
    }

    func testComputeReturnsNilWhenNothingQualifies() {
        XCTAssertNil(DictationStats.compute(entries: [], since: Date().addingTimeInterval(-7 * 86400)))
        XCTAssertNil(
            DictationStats.compute(
                entries: [entry(audioMs: 0, decodeMs: 10)],
                since: Date().addingTimeInterval(-7 * 86400)))
    }

    func testComputeSkipsEntriesWithNoDecodeTimeForDecodeMetricsButCountsThemOverall() {
        // macOS has no per-model catalog (single ASR backend), so the equivalent Windows case of
        // "other model" / "unstamped model" reduces to: rows without a decode time still count
        // toward the overall total but are excluded from decode/RTF aggregation.
        let entries = [
            entry(audioMs: 10_000, decodeMs: 1_000),
            entry(audioMs: 10_000, decodeMs: nil),
        ]

        let stats = DictationStats.compute(entries: entries, since: Date().addingTimeInterval(-7 * 86400))

        XCTAssertNotNil(stats)
        XCTAssertEqual(stats?.count, 2)
        XCTAssertEqual(stats?.decodeCount, 1)
        XCTAssertEqual(stats?.decodeMs?.average, 1_000)
        XCTAssertEqual(stats?.rtfP50 ?? -1, 0.10, accuracy: 1e-6)
    }
}
