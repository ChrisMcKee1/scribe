import XCTest
@testable import Scribe

final class UsageAnalyzerTests: XCTestCase {
    private static let now = ISO8601DateFormatter().date(from: "2026-06-15T12:00:00Z")!

    private func entry(
        daysAgo: Double = 0,
        hoursAgo: Double = 0,
        text: String,
        audioMilliseconds: Double,
        targetApp: String?
    ) -> UsageAnalyzer.Entry {
        let timestamp = Self.now.addingTimeInterval(-daysAgo * 86_400 - hoursAgo * 3_600)
        return UsageAnalyzer.Entry(
            timestampUtc: timestamp, text: text, audioMilliseconds: audioMilliseconds, targetApp: targetApp)
    }

    func testComputeUsesOnePeriodForAllMetricsAndOrdersAppsDeterministically() {
        let entries = [
            entry(daysAgo: 1, text: "one two three", audioMilliseconds: 3_000, targetApp: "Visual Studio Code"),
            entry(daysAgo: 2, text: "four five", audioMilliseconds: 2_000, targetApp: "Terminal"),
            entry(daysAgo: 3, text: "six", audioMilliseconds: 1_000, targetApp: "terminal"),
            entry(daysAgo: 40, text: "excluded words", audioMilliseconds: 9_000, targetApp: "Excluded"),
        ]

        let snapshot = UsageAnalyzer.compute(
            entries: entries,
            knownTerms: [],
            sinceUtc: Self.now.addingTimeInterval(-30 * 86_400),
            nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)

        XCTAssertEqual(snapshot.dictations, 3)
        XCTAssertEqual(snapshot.words, 6)
        XCTAssertEqual(snapshot.activeDays, 3)
        XCTAssertEqual(snapshot.speechSeconds, 6, accuracy: 0.001)
        XCTAssertEqual(snapshot.averageWords, 2, accuracy: 0.001)
        XCTAssertEqual(snapshot.topApps, [
            UsageAnalyzer.AppUsage(name: "Terminal", dictations: 2, words: 3),
            UsageAnalyzer.AppUsage(name: "Visual Studio Code", dictations: 1, words: 3),
        ])
        XCTAssertEqual(snapshot.trend.count, 31)
        XCTAssertEqual(snapshot.trend.reduce(0) { $0 + $1.dictations }, 3)
    }

    func testComputeCountsUnicodeWordsAndNormalizesBlankAppNames() {
        let snapshot = UsageAnalyzer.compute(
            entries: [entry(text: "naïve café 東京 don't state-of-the-art", audioMilliseconds: 1_000, targetApp: "  ")],
            knownTerms: [],
            sinceUtc: Self.now.addingTimeInterval(-1 * 86_400),
            nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)

        XCTAssertEqual(snapshot.words, 5)
        XCTAssertEqual(snapshot.topApps.count, 1)
        XCTAssertEqual(snapshot.topApps.first?.name, "Unknown app")
    }

    func testComputeRecognizesPatternsAndCanonicalMultiwordReplacements() {
        let entries = [
            entry(text: "Deploy with Tailwind CSS and Next.js.", audioMilliseconds: 1_000, targetApp: nil),
            entry(hoursAgo: 1, text: "Use tailwind css with next js.", audioMilliseconds: 1_000, targetApp: nil),
        ]
        let terms = [
            DictionaryEntry(pattern: "tailwind css", replacement: "Tailwind CSS"),
            DictionaryEntry(pattern: "next js", replacement: "Next.js"),
        ]

        let snapshot = UsageAnalyzer.compute(
            entries: entries, knownTerms: terms,
            sinceUtc: Self.now.addingTimeInterval(-86_400), nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)

        XCTAssertTrue(snapshot.terms.contains(UsageAnalyzer.TermUsage(text: "Tailwind CSS", dictations: 2, occurrences: 2, covered: true)))
        XCTAssertTrue(snapshot.terms.contains(UsageAnalyzer.TermUsage(text: "Next.js", dictations: 2, occurrences: 2, covered: true)))
    }

    func testComputeSuggestsOnlyRecurringJargonShapes() {
        let entries = [
            entry(text: "Hello CloudThing from ProjectAlpha", audioMilliseconds: 1_000, targetApp: nil),
            entry(hoursAgo: 1, text: "Hello CloudThing again", audioMilliseconds: 1_000, targetApp: nil),
            entry(hoursAgo: 2, text: "Hello ordinary prose", audioMilliseconds: 1_000, targetApp: nil),
        ]

        let snapshot = UsageAnalyzer.compute(
            entries: entries, knownTerms: [],
            sinceUtc: Self.now.addingTimeInterval(-86_400), nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)

        XCTAssertEqual(snapshot.terms, [UsageAnalyzer.TermUsage(text: "CloudThing", dictations: 2, occurrences: 2, covered: false)])
    }

    func testComputeUsesWeekBucketsForLongPeriodsAndFillsGaps() {
        let snapshot = UsageAnalyzer.compute(
            entries: [entry(daysAgo: 40, text: "one", audioMilliseconds: 1_000, targetApp: nil)],
            knownTerms: [],
            sinceUtc: Self.now.addingTimeInterval(-90 * 86_400), nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)

        XCTAssertTrue((13...14).contains(snapshot.trend.count))
        XCTAssertEqual(snapshot.trend.reduce(0) { $0 + $1.dictations }, 1)
        XCTAssertEqual(snapshot.trend, snapshot.trend.sorted { $0.start < $1.start })
    }

    func testComputeReportsTrendGranularityInsteadOfLeavingCallersToGuess() {
        let daily = UsageAnalyzer.compute(
            entries: [entry(daysAgo: 1, text: "one", audioMilliseconds: 1_000, targetApp: nil)],
            knownTerms: [],
            sinceUtc: Self.now.addingTimeInterval(-30 * 86_400), nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)
        let weekly = UsageAnalyzer.compute(
            entries: [entry(daysAgo: 1, text: "one", audioMilliseconds: 1_000, targetApp: nil)],
            knownTerms: [],
            sinceUtc: Self.now.addingTimeInterval(-90 * 86_400), nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)

        XCTAssertEqual(daily.granularity, .daily)
        XCTAssertEqual(weekly.granularity, .weekly)
        XCTAssertLessThanOrEqual(weekly.trend.count, 31)
    }

    func testComputeSkipsDictionaryEntriesWithNoUsableForms() {
        let snapshot = UsageAnalyzer.compute(
            entries: [entry(text: "a short note", audioMilliseconds: 1_000, targetApp: nil)],
            knownTerms: [DictionaryEntry(pattern: " a ", replacement: " b ")],
            sinceUtc: Self.now.addingTimeInterval(-86_400), nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)

        XCTAssertFalse(snapshot.terms.contains { $0.covered })
    }

    func testComputeCountsDottedAndLeadingDotFormsWithWordBoundaries() {
        let entries = [
            entry(text: "Ship Next.js and .NET apps.", audioMilliseconds: 1_000, targetApp: nil),
            entry(hoursAgo: 1, text: "The dotnet CLI targets .net today.", audioMilliseconds: 1_000, targetApp: nil),
        ]
        let terms = [
            DictionaryEntry(pattern: "next js", replacement: "Next.js"),
            DictionaryEntry(pattern: "dot net", replacement: ".NET"),
        ]

        let snapshot = UsageAnalyzer.compute(
            entries: entries, knownTerms: terms,
            sinceUtc: Self.now.addingTimeInterval(-86_400), nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)

        XCTAssertTrue(snapshot.terms.contains(UsageAnalyzer.TermUsage(text: "Next.js", dictations: 1, occurrences: 1, covered: true)))
        XCTAssertTrue(snapshot.terms.contains(UsageAnalyzer.TermUsage(text: ".NET", dictations: 2, occurrences: 2, covered: true)))
    }

    func testComputeDoesNotMatchFormsInsideLargerWords() {
        let entries = [
            entry(text: "Trusted setups crust and thrust", audioMilliseconds: 1_000, targetApp: nil),
            entry(hoursAgo: 1, text: "Rust is fine", audioMilliseconds: 1_000, targetApp: nil),
        ]

        let snapshot = UsageAnalyzer.compute(
            entries: entries, knownTerms: [DictionaryEntry(pattern: "rust", replacement: "Rust")],
            sinceUtc: Self.now.addingTimeInterval(-86_400), nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)

        let covered = snapshot.terms.filter { $0.covered }
        XCTAssertEqual(covered, [UsageAnalyzer.TermUsage(text: "Rust", dictations: 1, occurrences: 1, covered: true)])
    }

    func testComputeMatchesMultiwordFormsCaseInsensitivelyAndTakesMaxAcrossForms() {
        let entries = [
            entry(text: "TAILWIND CSS everywhere", audioMilliseconds: 1_000, targetApp: nil),
            entry(hoursAgo: 1, text: "Use next js and Next.js side by side", audioMilliseconds: 1_000, targetApp: nil),
        ]
        let terms = [
            DictionaryEntry(pattern: "tailwind css", replacement: "Tailwind CSS"),
            DictionaryEntry(pattern: "next js", replacement: "Next.js"),
        ]

        let snapshot = UsageAnalyzer.compute(
            entries: entries, knownTerms: terms,
            sinceUtc: Self.now.addingTimeInterval(-86_400), nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)

        XCTAssertTrue(snapshot.terms.contains(UsageAnalyzer.TermUsage(text: "Tailwind CSS", dictations: 1, occurrences: 1, covered: true)))
        XCTAssertTrue(snapshot.terms.contains(UsageAnalyzer.TermUsage(text: "Next.js", dictations: 1, occurrences: 1, covered: true)))
    }

    func testComputePreservesNonOverlappingCountsForSingleTokenForms() {
        let snapshot = UsageAnalyzer.compute(
            entries: [entry(text: "a-a-a", audioMilliseconds: 1_000, targetApp: nil)],
            knownTerms: [DictionaryEntry(pattern: "a-a", replacement: "A-A")],
            sinceUtc: Self.now.addingTimeInterval(-86_400), nowUtc: Self.now,
            timeZone: TimeZone(identifier: "UTC")!)

        let covered = snapshot.terms.filter { $0.covered }
        XCTAssertEqual(covered, [UsageAnalyzer.TermUsage(text: "A-A", dictations: 1, occurrences: 1, covered: true)])
    }
}
