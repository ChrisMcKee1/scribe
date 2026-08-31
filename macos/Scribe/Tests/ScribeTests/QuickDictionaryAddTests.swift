import XCTest
@testable import Scribe

/// Ported 1:1 from `QuickDictionaryAddTests.cs` and `QuickDictionaryAddSelectionTests.cs`. The
/// quick "Add to dictionary" popup reached from the tray gets its value from the spoken form coming
/// straight from the recognizer's own output rather than being retyped from memory: a rule whose
/// pattern has a typo silently never fires, and the user has no feedback telling them why. So the
/// selection logic here is load-bearing, not cosmetic.
final class QuickDictionaryAddTests: XCTestCase {
    private let empty: [DictionaryEntry] = []

    func testTokenizeReturnsNothingForBlankInput() {
        XCTAssertTrue(QuickDictionaryAdd.tokenize(nil).isEmpty)
        XCTAssertTrue(QuickDictionaryAdd.tokenize("   \r\n ").isEmpty)
    }

    func testTokenizeKeepsPunctuationAttachedSoChipsReadLikeTheDictation() {
        let tokens = QuickDictionaryAdd.tokenize("Open cloud pilot, then run it.")
        XCTAssertEqual(tokens.map(\.text), ["Open", "cloud", "pilot,", "then", "run", "it."])
    }

    func testTokenizeSpansPointBackAtTheOriginalText() {
        let transcript = "alpha beta"
        let tokens = QuickDictionaryAdd.tokenize(transcript)
        let scalars = Array(transcript.unicodeScalars)
        for token in tokens {
            let text = String(String.UnicodeScalarView(scalars[token.start..<(token.start + token.length)]))
            XCTAssertEqual(token.text, text)
        }
    }

    func testSelectingOneChipStripsTrailingSentencePunctuation() {
        let transcript = "Open cloud pilot, then run it."
        let tokens = QuickDictionaryAdd.tokenize(transcript)
        XCTAssertEqual(QuickDictionaryAdd.select(transcript, tokens: tokens, first: 2, last: 2), "pilot")
    }

    func testSelectingARangeKeepsInnerPunctuationAndDropsTheOuter() {
        let transcript = "It said \"cloud pilot, apparently\" again."
        let tokens = QuickDictionaryAdd.tokenize(transcript)
        XCTAssertEqual(QuickDictionaryAdd.select(transcript, tokens: tokens, first: 2, last: 4), "cloud pilot, apparently")
    }

    func testSelectingRightToLeftGivesTheSameText() {
        let transcript = "one two three"
        let tokens = QuickDictionaryAdd.tokenize(transcript)
        XCTAssertEqual(
            QuickDictionaryAdd.select(transcript, tokens: tokens, first: 0, last: 2),
            QuickDictionaryAdd.select(transcript, tokens: tokens, first: 2, last: 0))
    }

    func testSelectionSpanningALineBreakKeepsTheBreakSoItCanBeRejected() {
        let transcript = "first line\r\n\r\nsecond line"
        let tokens = QuickDictionaryAdd.tokenize(transcript)
        let selected = QuickDictionaryAdd.select(transcript, tokens: tokens, first: 0, last: 3)
        // Swift merges "\r\n" into a single extended grapheme cluster, so `selected.contains("\n")`
        // (Character-level) can never match; check at the Unicode scalar level instead, which is
        // what `build(...)` itself inspects.
        XCTAssertTrue(selected.unicodeScalars.contains("\n"))

        let plan = QuickDictionaryAdd.build(pattern: selected, replacement: "anything", wholeWord: true, existing: empty)
        XCTAssertEqual(plan.kind, .invalid)
        XCTAssertTrue(plan.message.lowercased().contains("line break"))
    }

    func testSelectionCollapsesHorizontalWhitespaceRuns() {
        let transcript = "cloud   pilot writes"
        let tokens = QuickDictionaryAdd.tokenize(transcript)
        XCTAssertEqual(QuickDictionaryAdd.select(transcript, tokens: tokens, first: 0, last: 1), "cloud pilot")
    }

    func testAPatternAnotherRuleAlreadyProducesIsRejectedAsUnreachable() {
        let existing = [DictionaryEntry(id: 7, pattern: "teams", replacement: "Microsoft Teams")]
        let plan = QuickDictionaryAdd.build(pattern: "Microsoft Teams", replacement: "Teams", wholeWord: true, existing: existing)
        XCTAssertEqual(plan.kind, .invalid)
        XCTAssertTrue(plan.message.lowercased().contains("never apply"))
        XCTAssertTrue(plan.message.lowercased().contains("teams"))
    }

    func testADisabledRulesReplacementDoesNotBlockANewPattern() {
        let existing = [DictionaryEntry(id: 7, pattern: "teams", replacement: "Microsoft Teams", wholeWord: true, enabled: false)]
        let plan = QuickDictionaryAdd.build(pattern: "Microsoft Teams", replacement: "Teams", wholeWord: true, existing: existing)
        XCTAssertEqual(plan.kind, .create)
    }

    func testARuleIsNotTreatedAsBlockingItsOwnPattern() {
        let existing = [DictionaryEntry(id: 3, pattern: "github", replacement: "GitHub")]
        let plan = QuickDictionaryAdd.build(pattern: "GitHub", replacement: "GitHub Enterprise", wholeWord: true, existing: existing)
        XCTAssertEqual(plan.kind, .update)
    }

    func testInternalApostrophesAndSymbolWordsSurviveTrimming() {
        let transcript = "don't ship C++ or #tags."
        let tokens = QuickDictionaryAdd.tokenize(transcript)
        XCTAssertEqual(QuickDictionaryAdd.select(transcript, tokens: tokens, first: 0, last: 0), "don't")
        XCTAssertEqual(QuickDictionaryAdd.select(transcript, tokens: tokens, first: 2, last: 2), "C++")
        XCTAssertEqual(QuickDictionaryAdd.select(transcript, tokens: tokens, first: 4, last: 4), "#tags")
    }

    func testSelectingPurePunctuationReturnsItRatherThanClearingTheBox() {
        let transcript = "well ... maybe"
        let tokens = QuickDictionaryAdd.tokenize(transcript)
        XCTAssertEqual(QuickDictionaryAdd.select(transcript, tokens: tokens, first: 1, last: 1), "...")
    }

    func testOutOfRangeIndicesAreClampedRatherThanThrowing() {
        let transcript = "one two"
        let tokens = QuickDictionaryAdd.tokenize(transcript)
        XCTAssertEqual(QuickDictionaryAdd.select(transcript, tokens: tokens, first: -5, last: 99), "one two")
        XCTAssertEqual(QuickDictionaryAdd.select(transcript, tokens: [], first: 0, last: 0), "")
        XCTAssertEqual(QuickDictionaryAdd.select(nil, tokens: tokens, first: 0, last: 0), "")
    }

    func testBlankSpokenFormCannotBeSaved() {
        let plan = QuickDictionaryAdd.build(pattern: "   ", replacement: "Copilot", wholeWord: true, existing: empty)
        XCTAssertEqual(plan.kind, .invalid)
        XCTAssertFalse(plan.canSave)
        XCTAssertNil(plan.entry)
    }

    func testARuleThatRewritesTextToItselfCannotBeSaved() {
        let plan = QuickDictionaryAdd.build(pattern: "Copilot", replacement: " Copilot ", wholeWord: true, existing: empty)
        XCTAssertEqual(plan.kind, .invalid)
    }

    func testACaseOnlyCorrectionIsARealRule() {
        let plan = QuickDictionaryAdd.build(pattern: "copilot", replacement: "Copilot", wholeWord: true, existing: empty)
        XCTAssertEqual(plan.kind, .create)
        XCTAssertEqual(plan.entry?.pattern, "copilot")
        XCTAssertEqual(plan.entry?.replacement, "Copilot")
    }

    func testNewSpokenFormCreatesAnUnsavedEntry() {
        let plan = QuickDictionaryAdd.build(pattern: "cloud pilot", replacement: "Copilot", wholeWord: false, existing: empty)
        XCTAssertEqual(plan.kind, .create)
        XCTAssertTrue(plan.canSave)
        XCTAssertEqual(plan.entry?.id, 0)
        XCTAssertEqual(plan.entry?.wholeWord, false)
        XCTAssertEqual(plan.entry?.enabled, true)
    }

    func testAnEmptyReplacementIsAllowedAndDescribedAsARemoval() {
        let plan = QuickDictionaryAdd.build(pattern: "um", replacement: "", wholeWord: true, existing: empty)
        XCTAssertEqual(plan.kind, .create)
        XCTAssertEqual(plan.entry?.replacement, "")
        XCTAssertTrue(plan.message.lowercased().contains("leave \"um\" out"))
        XCTAssertFalse(plan.message.lowercased().contains("delete"))
        XCTAssertFalse(plan.message.lowercased().contains("saved"))
    }

    func testAPendingPlanNeverClaimsTheRuleIsAlreadySaved() {
        let create = QuickDictionaryAdd.build(pattern: "cloud pilot", replacement: "Copilot", wholeWord: true, existing: empty)
        let update = QuickDictionaryAdd.build(
            pattern: "cloud pilot", replacement: "Copilot", wholeWord: true,
            existing: [DictionaryEntry(id: 1, pattern: "cloud pilot", replacement: "CoPilot")])

        XCTAssertEqual(create.kind, .create)
        XCTAssertEqual(update.kind, .update)
        XCTAssertFalse(create.message.lowercased().contains("saved"))
        XCTAssertFalse(update.message.lowercased().contains("saved"))
    }

    func testAnExistingSpokenFormUpdatesInPlaceRegardlessOfCase() {
        let existing = [DictionaryEntry(id: 7, pattern: "Cloud Pilot", replacement: "Copilot")]
        let plan = QuickDictionaryAdd.build(pattern: "cloud pilot", replacement: "GitHub Copilot", wholeWord: true, existing: existing)
        XCTAssertEqual(plan.kind, .update)
        XCTAssertEqual(plan.entry?.id, 7)
        XCTAssertEqual(plan.entry?.pattern, "cloud pilot")
        XCTAssertEqual(plan.entry?.replacement, "GitHub Copilot")
    }

    func testUpdatingReEnablesADisabledRule() {
        let existing = [DictionaryEntry(id: 7, pattern: "cloud pilot", replacement: "Copilot", wholeWord: true, enabled: false)]
        let plan = QuickDictionaryAdd.build(pattern: "cloud pilot", replacement: "Copilot", wholeWord: true, existing: existing)
        XCTAssertEqual(plan.kind, .update)
        XCTAssertEqual(plan.entry?.enabled, true)
    }

    func testAnIdenticalExistingRuleReportsNoChange() {
        let existing = [DictionaryEntry(id: 7, pattern: "cloud pilot", replacement: "Copilot")]
        let plan = QuickDictionaryAdd.build(pattern: "cloud pilot", replacement: "Copilot", wholeWord: true, existing: existing)
        XCTAssertEqual(plan.kind, .noChange)
        XCTAssertFalse(plan.canSave)
    }

    func testChangingOnlyTheWholeWordFlagStillCountsAsAnUpdate() {
        let existing = [DictionaryEntry(id: 7, pattern: "cloud pilot", replacement: "Copilot", wholeWord: true)]
        let plan = QuickDictionaryAdd.build(pattern: "cloud pilot", replacement: "Copilot", wholeWord: false, existing: existing)
        XCTAssertEqual(plan.kind, .update)
        XCTAssertEqual(plan.entry?.wholeWord, false)
    }

    func testChipSelectionFeedsStraightIntoASaveablePlan() {
        let transcript = "I opened cloud pilot, and it worked."
        let tokens = QuickDictionaryAdd.tokenize(transcript)
        let spoken = QuickDictionaryAdd.select(transcript, tokens: tokens, first: 2, last: 3)
        let plan = QuickDictionaryAdd.build(pattern: spoken, replacement: "Copilot", wholeWord: true, existing: empty)
        XCTAssertEqual(spoken, "cloud pilot")
        XCTAssertEqual(plan.kind, .create)
    }

    // MARK: - Apply (mirrors the live post-processor exactly; see TextPostProcessor.applyRule)

    private func rule(_ pattern: String, _ replacement: String, wholeWord: Bool = true) -> DictionaryEntry {
        DictionaryEntry(id: 0, pattern: pattern, replacement: replacement, wholeWord: wholeWord)
    }

    func testApplyReplacesEveryOccurrenceNotJustTheFirst() {
        let result = QuickDictionaryAdd.apply(
            "aspire runs it, then aspire hosts it, and aspire wins.", entry: rule("aspire", "Aspire"))
        XCTAssertEqual(result, "Aspire runs it, then Aspire hosts it, and Aspire wins.")
    }

    func testApplyHonoursWholeWordExactlyLikeTheMatcher() {
        XCTAssertEqual(
            QuickDictionaryAdd.apply("aspire and aspires", entry: rule("aspire", "Aspire")),
            "Aspire and aspires")
        XCTAssertEqual(
            QuickDictionaryAdd.apply("aspire and aspires", entry: rule("aspire", "Aspire", wholeWord: false)),
            "Aspire and Aspires")
    }

    func testApplyMatchesCaseInsensitivelyLikeTheMatcher() {
        XCTAssertEqual(
            QuickDictionaryAdd.apply("asp.net and ASP.NET", entry: rule("asp.net", "ASP.NET", wholeWord: false)),
            "ASP.NET and ASP.NET")
    }

    func testApplyDoesNotReExpandTextThatIsAlreadyCanonical() {
        XCTAssertEqual(
            QuickDictionaryAdd.apply("York is different from New York", entry: rule("York", "New York")),
            "New York is different from New York")
    }

    func testApplyAbsorbsTheSpaceBeforeAPunctuationReplacement() {
        XCTAssertEqual(
            QuickDictionaryAdd.apply("hello comma world", entry: rule("comma", ",")),
            "hello, world")
    }

    func testApplyPreservesLineBreaks() {
        XCTAssertEqual(
            QuickDictionaryAdd.apply("aspire\r\nsecond line", entry: rule("aspire", "Aspire")),
            "Aspire\r\nsecond line")
    }

    func testApplyReturnsTheTranscriptUnchangedWhenTheRuleNeverFires() {
        XCTAssertEqual(
            QuickDictionaryAdd.apply("nothing to change here", entry: rule("absent", "present")),
            "nothing to change here")
    }

    func testApplyHandlesMissingInputsWithoutThrowing() {
        XCTAssertEqual(QuickDictionaryAdd.apply(nil, entry: rule("a", "b")), "")
        XCTAssertEqual(QuickDictionaryAdd.apply("", entry: rule("a", "b")), "")
        XCTAssertEqual(QuickDictionaryAdd.apply("text", entry: nil), "text")
        XCTAssertEqual(QuickDictionaryAdd.apply("text", entry: rule("   ", "b")), "text")
    }

    func testApplyTreatsADollarSignInTheReplacementAsLiteralText() {
        XCTAssertEqual(
            QuickDictionaryAdd.apply("costs five dollars today", entry: rule("five dollars", "$5")),
            "costs $5 today")
        XCTAssertEqual(
            QuickDictionaryAdd.apply("ampersand here", entry: rule("ampersand", "$&")),
            "$& here")
    }

    func testApplyEscapesRegexMetacharactersInTheSpokenForm() {
        XCTAssertEqual(QuickDictionaryAdd.apply("we use c++ here", entry: rule("c++", "C#")), "we use C# here")
        XCTAssertEqual(QuickDictionaryAdd.apply("a.c", entry: rule("a.c", "matched")), "matched")
        XCTAssertEqual(QuickDictionaryAdd.apply("a.c", entry: rule("abc", "matched")), "a.c")
    }

    /// The load-bearing guarantee: repairing a transcript must produce exactly what the live
    /// pipeline would have produced from the same input. Asserted against the real
    /// `TextPostProcessor`, not a copy.
    func testApplyAgreesWithTheLivePostProcessor() {
        let cases: [(transcript: String, pattern: String, replacement: String)] = [
            ("so um it works", "um", ""),
            ("it works, um obviously.", "um", ""),
            ("um it works", "um", ""),
            ("hello comma world", "comma", ","),
            ("York is different from New York", "York", "New York"),
            ("aspire and aspires", "aspire", "Aspire"),
            ("  padded   text  ", "padded", "Padded"),
        ]

        for testCase in cases {
            let entry = DictionaryEntry(id: 1, pattern: testCase.pattern, replacement: testCase.replacement)
            let processor = TextPostProcessor()
            processor.reload(dictionaryEntries: [entry], snippets: [])

            let live = processor.process(testCase.transcript)
            let repaired = QuickDictionaryAdd.apply(testCase.transcript, entry: entry)

            XCTAssertEqual(live, repaired, "mismatch for \(testCase.transcript)")
        }
    }

    // MARK: - Selection model (Toggle), ported from QuickDictionaryAddSelectionTests.cs

    private func range(_ first: Int, _ last: Int) -> QuickDictionaryAdd.WordRange {
        QuickDictionaryAdd.WordRange(first: first, last: last)
    }

    func testToggleFromEmptySelectsSingleWord() {
        XCTAssertEqual(QuickDictionaryAdd.toggle(.none, index: 4), range(4, 4))
    }

    func testToggleNextWordExtendsRight() {
        XCTAssertEqual(QuickDictionaryAdd.toggle(range(4, 4), index: 5), range(4, 5))
    }

    func testTogglePreviousWordExtendsLeft() {
        XCTAssertEqual(QuickDictionaryAdd.toggle(range(4, 6), index: 3), range(3, 6))
    }

    func testToggleThreeAdjacentClicksBuildsThreeWordPhrase() {
        var current = QuickDictionaryAdd.toggle(.none, index: 10)
        current = QuickDictionaryAdd.toggle(current, index: 11)
        current = QuickDictionaryAdd.toggle(current, index: 12)
        XCTAssertEqual(current, range(10, 12))
    }

    func testToggleFirstWordOfPhraseShrinksFromTheLeft() {
        XCTAssertEqual(QuickDictionaryAdd.toggle(range(4, 7), index: 4), range(5, 7))
    }

    func testToggleLastWordOfPhraseShrinksFromTheRight() {
        XCTAssertEqual(QuickDictionaryAdd.toggle(range(4, 7), index: 7), range(4, 6))
    }

    func testToggleMiddleOfPhraseCollapsesToThatWord() {
        XCTAssertEqual(QuickDictionaryAdd.toggle(range(4, 8), index: 6), range(6, 6))
    }

    func testToggleOnlySelectedWordClearsSelection() {
        XCTAssertTrue(QuickDictionaryAdd.toggle(range(4, 4), index: 4).isEmpty)
    }

    func testToggleWordAwayFromPhraseStartsAgain() {
        for index in [0, 2, 9, 40] {
            XCTAssertEqual(QuickDictionaryAdd.toggle(range(4, 7), index: index), range(index, index))
        }
    }

    func testToggleExtendThenShrinkReturnsToTheEarlierPhrase() {
        var current = QuickDictionaryAdd.toggle(.none, index: 5)
        current = QuickDictionaryAdd.toggle(current, index: 6)
        current = QuickDictionaryAdd.toggle(current, index: 4)
        XCTAssertEqual(current, range(4, 6))

        current = QuickDictionaryAdd.toggle(current, index: 4)
        XCTAssertEqual(current, range(5, 6))
    }

    func testToggleRepeatedlyFromTheEndEventuallyClears() {
        var current = range(2, 5)
        for i in stride(from: 5, through: 3, by: -1) {
            current = QuickDictionaryAdd.toggle(current, index: i)
        }
        XCTAssertEqual(current, range(2, 2))
        XCTAssertTrue(QuickDictionaryAdd.toggle(current, index: 2).isEmpty)
    }

    func testToggleNegativeIndexLeavesSelectionAlone() {
        XCTAssertEqual(QuickDictionaryAdd.toggle(range(4, 7), index: -1), range(4, 7))
    }

    func testToggleFirstWordInTranscriptExtendsLeftWithoutRunningPastTheStart() {
        XCTAssertEqual(QuickDictionaryAdd.toggle(range(1, 3), index: 0), range(0, 3))
    }
}
