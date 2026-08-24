import XCTest
@testable import Scribe

final class DictionaryUsageAnalyzerTests: XCTestCase {
    /// Builds a corpus that clears the evidence bar without the test having to care what the bar is.
    /// Filler is deliberately bland so it cannot accidentally supply evidence for a term under test.
    private func corpus(_ lines: String...) -> [String] {
        var transcripts = lines
        func wordCount(_ s: String) -> Int {
            s.split(whereSeparator: { $0.isWhitespace }).count
        }
        while transcripts.count < DictionaryUsageAnalyzer.minimumTranscripts
            || transcripts.reduce(0, { $0 + wordCount($1) }) < DictionaryUsageAnalyzer.minimumWords {
            transcripts.append(Array(repeating: "the meeting went well today", count: 20).joined(separator: " "))
        }
        return transcripts
    }

    // MARK: - The inversion trap

    /// The whole feature turns on this case. History stores what was typed, which is
    /// post-dictionary, so a rule that works has already erased its own pattern from the record.
    /// Judging on the pattern alone would delete the hardest-working rules first.
    func testAWorkingRuleIsKeptEvenThoughItsPatternNeverAppears() {
        let entry = DictionaryEntry(id: 1, pattern: "co pilot", replacement: "Copilot")
        let report = DictionaryUsageAnalyzer.analyze(
            transcripts: corpus("Copilot wrote the change for me", "I asked Copilot again"),
            baseEntries: [entry])

        XCTAssertTrue(report.hasEnoughEvidence)
        XCTAssertTrue(report.unusedEntries.isEmpty)
    }

    func testATermWithNoTraceInEitherDirectionIsFlagged() {
        let entry = DictionaryEntry(id: 1, pattern: "kubernetes", replacement: "Kubernetes")
        let report = DictionaryUsageAnalyzer.analyze(
            transcripts: corpus("nothing relevant here"),
            baseEntries: [entry])

        XCTAssertEqual(report.unusedEntries.count, 1)
        XCTAssertEqual(report.unusedEntries[0].entry.pattern, "kubernetes")
    }

    /// A pattern still being spoken means the rule is live even if the fix never lands.
    func testATermWhoseSpokenFormStillAppearsIsKept() {
        let entry = DictionaryEntry(id: 1, pattern: "azure", replacement: "Azure", wholeWord: true, enabled: false)
        let report = DictionaryUsageAnalyzer.analyze(
            transcripts: corpus("we deployed to azure"),
            baseEntries: [entry])

        XCTAssertTrue(report.unusedEntries.isEmpty)
    }

    // MARK: - Matcher parity

    /// The written form must be searched without word boundaries even when the rule itself is
    /// whole-word. The matcher only bounds the pattern, and TextPostProcessor tightens whitespace
    /// before punctuation, so "comma" to "," lands as "hello, world" where the comma follows a word
    /// character. Bounding the search would call a rule that fires constantly dead.
    func testAPunctuationReplacementIsFoundEvenThoughBoundariesWouldRejectIt() {
        let entry = DictionaryEntry(id: 1, pattern: "comma", replacement: ",")

        let report = DictionaryUsageAnalyzer.analyze(transcripts: corpus("hello, world"), baseEntries: [entry])

        XCTAssertTrue(report.unusedEntries.isEmpty)
    }

    /// The pattern side keeps the matcher's boundaries, or the verdict is not the matcher's.
    func testWholeWordPatternsAreNotKeptAliveBySubstring() {
        let wholeWord = DictionaryEntry(id: 1, pattern: "ai", replacement: "Artificial intelligence")
        let substring = DictionaryEntry(id: 2, pattern: "ai", replacement: "Artificial intelligence", wholeWord: false)

        let text = corpus("this said nothing important")

        XCTAssertEqual(DictionaryUsageAnalyzer.analyze(transcripts: text, baseEntries: [wholeWord]).unusedEntries.count, 1)
        XCTAssertTrue(DictionaryUsageAnalyzer.analyze(transcripts: text, baseEntries: [substring]).unusedEntries.isEmpty)
    }

    func testEvidenceMatchingIgnoresCase() {
        let entry = DictionaryEntry(id: 1, pattern: "GITHUB", replacement: "GitHub")
        let report = DictionaryUsageAnalyzer.analyze(transcripts: corpus("pushed it to github"), baseEntries: [entry])

        XCTAssertTrue(report.unusedEntries.isEmpty)
    }

    // MARK: - Unmeasurable rules

    /// A removal rule leaves identical evidence whether it fires or not: it deletes its own
    /// pattern and has no written form to look for. It is also skipped when the glossary is
    /// built, so retiring one saves nothing. Proposing it would be a pure risk with no payoff.
    func testARemovalRuleIsNeverProposedBecauseItCannotBeJudged() {
        let fired = DictionaryEntry(id: 1, pattern: "um", replacement: "")
        let never = DictionaryEntry(id: 2, pattern: "erm", replacement: "")

        let report = DictionaryUsageAnalyzer.analyze(transcripts: corpus("so anyway"), baseEntries: [fired, never])

        XCTAssertTrue(report.unusedEntries.isEmpty)
        XCTAssertEqual(report.termsExamined, 0)
    }

    // MARK: - Entries that cannot have shaped history

    /// An unsaved row cannot have influenced the history being searched, so judging it would let
    /// the scan offer to delete an entry the user typed seconds earlier.
    func testAnUnsavedEntryIsNeverJudgedAgainstHistoryThatPredatesIt() {
        let report = DictionaryUsageAnalyzer.analyze(
            transcripts: corpus("nothing relevant here"),
            baseEntries: [DictionaryEntry(id: 0, pattern: "kubernetes", replacement: "Kubernetes")])

        XCTAssertTrue(report.unusedEntries.isEmpty)
        XCTAssertEqual(report.termsExamined, 0)
    }

    func testBlankPatternsAreSkippedRatherThanFlagged() {
        let report = DictionaryUsageAnalyzer.analyze(
            transcripts: corpus("some ordinary text"),
            baseEntries: [DictionaryEntry(id: 1, pattern: "   ", replacement: "something")])

        XCTAssertTrue(report.unusedEntries.isEmpty)
        XCTAssertEqual(report.termsExamined, 0)
    }

    // MARK: - The evidence bar

    /// Telling a new user to delete their dictionary because they have barely dictated is the
    /// worst possible first impression, and the verdict genuinely is not supportable on a tiny
    /// sample.
    func testACorpusBelowTheEvidenceBarReportsNothingActionable() {
        let report = DictionaryUsageAnalyzer.analyze(
            transcripts: ["a short dictation", "another short one"],
            baseEntries: [DictionaryEntry(id: 1, pattern: "kubernetes", replacement: "Kubernetes")])

        XCTAssertFalse(report.hasEnoughEvidence)
        XCTAssertFalse(report.hasFindings)
        XCTAssertTrue(report.unusedEntries.isEmpty)
        XCTAssertTrue(report.summary.localizedCaseInsensitiveContains("Not enough dictation history"))
    }

    /// Enough dictations but almost no words is still not a vocabulary sample.
    func testManyTinyDictationsDoNotClearTheEvidenceBar() {
        let transcripts = Array(repeating: "yes", count: DictionaryUsageAnalyzer.minimumTranscripts * 2)

        XCTAssertFalse(DictionaryUsageAnalyzer.analyze(transcripts: transcripts, baseEntries: []).hasEnoughEvidence)
    }

    func testBlankTranscriptsDoNotCountTowardsTheEvidenceBar() {
        let report = DictionaryUsageAnalyzer.analyze(
            transcripts: Array(repeating: "   ", count: 500),
            baseEntries: [])

        XCTAssertFalse(report.hasEnoughEvidence)
        XCTAssertEqual(report.transcriptsScanned, 0)
    }

    // MARK: - Corpus handling

    /// Transcripts are joined into one corpus for speed; the join must not let a phrase match
    /// across the seam between two unrelated dictations.
    func testEvidenceDoesNotLeakAcrossTwoDictations() {
        let entry = DictionaryEntry(id: 1, pattern: "is ready", replacement: "is ready")

        let report = DictionaryUsageAnalyzer.analyze(
            transcripts: corpus("the release is", "ready to ship"),
            baseEntries: [entry])

        XCTAssertEqual(report.unusedEntries.count, 1)
    }

    // MARK: - Reporting

    func testACleanDictionarySaysSoPlainly() {
        let report = DictionaryUsageAnalyzer.analyze(
            transcripts: corpus("Copilot wrote it"),
            baseEntries: [DictionaryEntry(id: 1, pattern: "co pilot", replacement: "Copilot")])

        XCTAssertFalse(report.hasFindings)
        XCTAssertTrue(report.summary.localizedCaseInsensitiveContains("Nothing to clean up"))
    }

    func testFindingsAreListedAlphabeticallySoTheReviewListIsPredictable() {
        let entries = [
            DictionaryEntry(id: 1, pattern: "zulu", replacement: "Zulu"),
            DictionaryEntry(id: 2, pattern: "alpha", replacement: "Alpha"),
            DictionaryEntry(id: 3, pattern: "mike", replacement: "Mike"),
        ]

        let report = DictionaryUsageAnalyzer.analyze(transcripts: corpus("unrelated content"), baseEntries: entries)

        XCTAssertEqual(report.unusedEntries.map { $0.entry.pattern }, ["alpha", "mike", "zulu"])
    }

    /// The glossary cap is only worth mentioning when the dictionary is big enough for it to
    /// bite; quoting a limit to someone nowhere near it is noise.
    func testTheGlossaryCapIsOnlyMentionedWhenTheDictionaryExceedsIt() {
        let small = DictionaryUsageAnalyzer.analyze(
            transcripts: corpus("unrelated content"),
            baseEntries: [DictionaryEntry(id: 1, pattern: "kubernetes", replacement: "Kubernetes")])

        XCTAssertFalse(small.summary.localizedCaseInsensitiveContains("frees room"))

        let many = (1...200).map {
            DictionaryEntry(id: $0, pattern: "term number \($0)", replacement: "Term\($0)")
        }
        let large = DictionaryUsageAnalyzer.analyze(transcripts: corpus("unrelated content"), baseEntries: many)

        XCTAssertTrue(large.summary.localizedCaseInsensitiveContains("frees room"))
    }

    func testEvidenceCountsBothDirectionsForALiveTerm() {
        let usage = DictionaryUsageAnalyzer.score(
            corpus: "Copilot and co pilot and Copilot",
            entry: DictionaryEntry(id: 1, pattern: "co pilot", replacement: "Copilot"))

        XCTAssertFalse(usage.unused)
        XCTAssertEqual(usage.patternHits, 1)
        XCTAssertEqual(usage.replacementHits, 2)
    }
}
