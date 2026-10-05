import XCTest

@testable import Scribe

final class TranscriptionChunkerTests: XCTestCase {
    private let sampleRate = 1_000

    func testCapturesAtOrBelowThirtySecondsStayWhole() {
        for seconds in [0, 0.5, 10, 29.9, 30] {
            let samples = [Float](repeating: 0.25, count: Int(seconds * Double(sampleRate)))

            XCTAssertEqual(
                TranscriptionChunker.plan(samples: samples, sampleRate: sampleRate),
                [0..<samples.count],
                "\(seconds) seconds")
        }
    }

    func testThirtySecondMultiplesUseTheWindowsChunkCountAndStayCapped() {
        for (seconds, expectedChunks) in [(31, 2), (60, 3), (180, 7), (300, 11)] {
            let samples = [Float](repeating: 0.25, count: seconds * sampleRate)
            let spans = TranscriptionChunker.plan(samples: samples, sampleRate: sampleRate)

            XCTAssertEqual(spans.count, expectedChunks, "\(seconds) seconds")
            XCTAssertEqual(spans.first?.lowerBound, 0)
            XCTAssertEqual(spans.last?.upperBound, samples.count)
            for pair in zip(spans, spans.dropFirst()) {
                XCTAssertEqual(pair.0.upperBound, pair.1.lowerBound)
            }
            for span in spans {
                XCTAssertGreaterThanOrEqual(span.count, TranscriptionChunker.minChunkSeconds * sampleRate)
                XCTAssertLessThanOrEqual(span.count, TranscriptionChunker.maxChunkSeconds * sampleRate)
            }
        }
    }

    func testJointSeamSearchUsesQuietLullsInsteadOfIndependentNearestCuts() {
        let samples = speech(seconds: 300, period: 6, lullOffset: 4.5)

        let spans = TranscriptionChunker.plan(samples: samples, sampleRate: sampleRate)

        XCTAssertEqual(spans.count, 11)
        let seams = spans.dropFirst().map(\.lowerBound)
        // Exact seam positions produced by the Windows TranscriptionChunker for this same waveform.
        XCTAssertEqual(seams, [28_573, 52_945, 82_568, 106_941, 136_564, 160_936, 190_909, 220_582, 244_905, 274_577])
        for seam in seams {
            XCTAssertTrue(isQuiet(samples, around: seam), "seam at \(seam / sampleRate) seconds")
        }
    }

    func testSeededWaveformMatchesTheWindowsPlannerReferenceSpans() {
        let samples = (0..<(180 * sampleRate)).map { index in
            Float((index * 37 % 2_003) - 1_001) / 1_001
        }
        // Exact spans produced by the Windows TranscriptionChunker for this seeded waveform.
        let expected: [Range<Int>] = [
            0..<27_014,
            27_014..<51_429,
            51_429..<77_143,
            77_143..<103_507,
            103_507..<129_221,
            129_221..<153_636,
            153_636..<180_000,
        ]

        XCTAssertEqual(TranscriptionChunker.plan(samples: samples, sampleRate: sampleRate), expected)
    }

    func testPlanPreservesEverySampleAndNeverReordersOrDuplicatesAudio() {
        let samples = (0..<(180 * sampleRate)).map { index in
            Float((index * 37 % 2_003) - 1_001) / 1_001
        }

        let spans = TranscriptionChunker.plan(samples: samples, sampleRate: sampleRate)
        let stitched = spans.flatMap { samples[$0] }

        XCTAssertEqual(stitched, samples)
    }

    private func speech(seconds: Int, period: Double, lullOffset: Double) -> [Float] {
        var samples: [Float] = (0..<(seconds * sampleRate)).map {
            $0.isMultiple(of: 2) ? 0.3 : -0.3
        }
        var start = lullOffset
        while start + 0.5 <= Double(seconds) {
            let lower = Int(start * Double(sampleRate))
            let upper = lower + sampleRate / 2
            samples.replaceSubrange(lower..<upper, with: repeatElement(0, count: upper - lower))
            start += period
        }
        return samples
    }

    private func isQuiet(_ samples: [Float], around position: Int) -> Bool {
        let halfWindow = sampleRate / 20
        let lower = max(0, position - halfWindow)
        let upper = min(samples.count, position + halfWindow)
        let energy = samples[lower..<upper].reduce(0.0) { $0 + Double($1) * Double($1) }
        return sqrt(energy / Double(max(1, upper - lower))) < 0.01
    }
}
