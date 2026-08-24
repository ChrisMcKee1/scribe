import XCTest
@testable import Scribe

final class UsageInsightTests: XCTestCase {
    func testBuildSummaryContainsOnlyBoundedAggregateFields() {
        let snapshot = UsageAnalyzer.Snapshot(
            dictations: 3,
            words: 42,
            activeDays: 2,
            speechSeconds: 30,
            averageWords: 14,
            topApps: [UsageAnalyzer.AppUsage(name: "Editor", dictations: 2, words: 30)],
            trend: [UsageAnalyzer.TrendPoint(start: LocalDate(year: 2026, month: 6, day: 15), dictations: 3, words: 42)],
            terms: [UsageAnalyzer.TermUsage(text: "Next.js", dictations: 2, occurrences: 2, covered: true)],
            granularity: .daily)

        let summary = UsageInsight.buildSummary(snapshot, maxChars: 200)

        XCTAssertTrue(summary.contains("Dictations: 3"))
        XCTAssertTrue(summary.contains("Next.js: 2 dictations"))
        XCTAssertFalse(summary.contains("Editor"))
        XCTAssertFalse(summary.contains("2026"))
        XCTAssertLessThanOrEqual(summary.count, 200)
    }

    func testParseStripsFencesAndEnforcesOutputBound() {
        XCTAssertEqual(UsageInsight.parse("```text\nUseful insight\n```"), "Useful insight")
        XCTAssertEqual(UsageInsight.parse("123456789", maxChars: 5), "12345")
        XCTAssertNil(UsageInsight.parse("   "))
    }

    func testBuildSummaryExcludesUncoveredTermsMinedFromDictationText() {
        let snapshot = UsageAnalyzer.Snapshot(
            dictations: 3,
            words: 42,
            activeDays: 2,
            speechSeconds: 30,
            averageWords: 14,
            topApps: [],
            trend: [],
            terms: [
                UsageAnalyzer.TermUsage(text: "Next.js", dictations: 2, occurrences: 2, covered: true),
                // Uncovered terms are verbatim user words (codenames, surnames) and must never
                // reach the AI payload.
                UsageAnalyzer.TermUsage(text: "ProjectBlackwood", dictations: 2, occurrences: 3, covered: false),
            ],
            granularity: .daily)

        let summary = UsageInsight.buildSummary(snapshot)

        XCTAssertTrue(summary.contains("Next.js: 2 dictations"))
        XCTAssertFalse(summary.contains("ProjectBlackwood"))
    }

    func testBuildSummaryTruncationNeverSplitsASurrogatePair() {
        let snapshot = UsageAnalyzer.Snapshot(
            dictations: 1,
            words: 1,
            activeDays: 1,
            speechSeconds: 0,
            averageWords: 1,
            topApps: [],
            trend: [],
            terms: [UsageAnalyzer.TermUsage(text: "Rocket\u{1F680}Lab", dictations: 1, occurrences: 1, covered: true)],
            granularity: .daily)

        let full = UsageInsight.buildSummary(snapshot)
        let nsFull = full as NSString
        var highSurrogateIndex = -1
        for index in 0..<nsFull.length where nsFull.character(at: index) == 0xD83D {
            highSurrogateIndex = index
            break
        }
        XCTAssertGreaterThanOrEqual(highSurrogateIndex, 0)

        // Force the cut to land between the emoji's two UTF-16 chars.
        let truncated = UsageInsight.buildSummary(snapshot, maxChars: highSurrogateIndex + 1)

        let expected = nsFull.substring(to: highSurrogateIndex).trimmingTrailingWhitespaceForTest()
        XCTAssertEqual(truncated, expected)
    }

    func testParseTruncationNeverSplitsASurrogatePair() {
        XCTAssertEqual(UsageInsight.parse("abc\u{1F600}def", maxChars: 4), "abc")
        XCTAssertEqual(UsageInsight.parse("abc\u{1F600}def", maxChars: 5), "abc\u{1F600}")
    }
}

private extension String {
    func trimmingTrailingWhitespaceForTest() -> String {
        var result = Substring(self)
        while let last = result.last, last.isWhitespace {
            result.removeLast()
        }
        return String(result)
    }
}
