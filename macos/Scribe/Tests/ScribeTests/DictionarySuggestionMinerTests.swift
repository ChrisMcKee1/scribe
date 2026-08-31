import XCTest
@testable import Scribe

final class DictionarySuggestionMinerTests: XCTestCase {
    private func h(_ text: String) -> DictationHistoryRecord {
        DictationHistoryRecord(startedAt: Date(), durationSeconds: 1.0, sampleCount: 1000, transcriptText: text)
    }

    func testJargonShapeDetection() {
        let cases: [(String, Bool)] = [
            ("ReBAC", true), // camel hump
            ("GitHub", true),
            ("ASR", true), // acronym
            (".NET", true), // leading dot + caps
            ("K8s", true), // letter+digit
            ("net10", true),
            ("hello", false), // plain word
            ("Hello", false), // sentence-case word
            ("WORD-SALAD", false), // hyphen breaks the acronym shape
            ("42", false), // pure number
        ]
        for (token, expected) in cases {
            XCTAssertEqual(DictionarySuggestionMiner.isJargonShaped(token), expected, token)
        }
    }

    func testRecurringTermsAcrossDistinctDictationsAreSuggested() {
        let history = [
            h("deploy the ReBAC rules to K8s"),
            h("ReBAC needs a review before the demo"),
            h("I updated ReBAC and the K8s manifests"),
            h("lunch at noon works for me"),
        ]

        let suggestions = DictionarySuggestionMiner.mine(entries: history, existing: [], minDictations: 3)

        XCTAssertEqual(suggestions.count, 1) // K8s only hit 2 dictations
        XCTAssertEqual(suggestions[0].term, "ReBAC")
        XCTAssertEqual(suggestions[0].dictations, 3)
    }

    func testRepeatsWithinOneDictationCountOnce() {
        let history = [h("ASR ASR ASR ASR ASR")]

        XCTAssertTrue(DictionarySuggestionMiner.mine(entries: history, existing: [], minDictations: 2).isEmpty)
    }

    func testTermsAlreadyInTheDictionaryAreNotSuggested() {
        let history = [h("use ReBAC"), h("check ReBAC"), h("love ReBAC")]
        let existing = [DictionaryEntry(pattern: "rebac", replacement: "ReBAC")]

        XCTAssertTrue(DictionarySuggestionMiner.mine(entries: history, existing: existing, minDictations: 3).isEmpty)
    }

    func testTrailingPunctuationIsStrippedAndStoplistFiltered() {
        let history = [h("migrate to K8s."), h("K8s, right?"), h("OK K8s it is OK")]

        let suggestions = DictionarySuggestionMiner.mine(entries: history, existing: [], minDictations: 3)

        XCTAssertEqual(suggestions.count, 1)
        XCTAssertEqual(suggestions[0].term, "K8s") // punctuation gone, OK never suggested
    }

    func testMostCommonSurfaceFormWins() {
        let history = [
            h("use GitHub"), h("on GitHub today"), h("github is down"), h("GitHub again"),
        ]

        let suggestions = DictionarySuggestionMiner.mine(entries: history, existing: [], minDictations: 3)

        // "github" (lowercase) isn't jargon-shaped, so only the cased form is counted anyway,
        // and the suggestion carries the shape that actually appeared.
        XCTAssertEqual(suggestions.count, 1)
        XCTAssertEqual(suggestions[0].term, "GitHub")
    }

    func testResultsAreCappedAndOrderedByFrequency() {
        var history: [DictationHistoryRecord] = []
        for _ in 0..<5 { history.append(h("ReBAC and ASR")) }
        for _ in 0..<3 { history.append(h("just K8s")) }

        let suggestions = DictionarySuggestionMiner.mine(
            entries: history, existing: [], minDictations: 3, maxSuggestions: 2)

        XCTAssertEqual(suggestions.count, 2)
        XCTAssertEqual(suggestions[0].dictations, 5)
        XCTAssertTrue(suggestions.contains { $0.term == "K8s" || $0.term == "ASR" || $0.term == "ReBAC" })
    }

    func testHistoryLearnerSpellsOutAcronymsRatherThanLowercasingThem() {
        let history = [h("the ATU owns it"), h("ask the ATU"), h("ATU signed off")]

        let entries = DictionaryHistoryLearner.buildEntries(history: history, existing: [])

        // Re-decoding retained audio showed the recognizer spells unknown acronyms out ("C L I",
        // "M C P") and never emits them lowercased, so "atu" would be a rule that can never fire.
        XCTAssertEqual(entries.count, 1)
        XCTAssertEqual(entries[0].pattern, "a t u")
        XCTAssertEqual(entries[0].replacement, "ATU")
        XCTAssertTrue(entries[0].wholeWord)
        XCTAssertTrue(entries[0].enabled)
    }

    func testHistoryLearnerSplitsCompoundsRatherThanLowercasingThem() {
        let history = [h("open WebIQ"), h("WebIQ again"), h("check WebIQ")]

        let entries = DictionaryHistoryLearner.buildEntries(history: history, existing: [])

        XCTAssertEqual(entries.count, 1)
        XCTAssertEqual(entries[0].pattern, "web iq")
        XCTAssertEqual(entries[0].replacement, "WebIQ")
    }

    func testHistoryLearnerNeverEmitsALowercasedCopyOfTheTerm() {
        let history = [
            h("the ATU and WebIQ"),
            h("ATU plus WebIQ"),
            h("ATU, WebIQ, done"),
        ]

        let entries = DictionaryHistoryLearner.buildEntries(history: history, existing: [])

        // The original defect: history is written after the dictionary runs, so lowercasing its
        // output invents a left-hand side the recognizer never produced.
        XCTAssertFalse(entries.contains {
            $0.pattern.caseInsensitiveCompare($0.replacement) == .orderedSame
        })
    }

    func testHistoryLearnerSkipsTwoLetterAcronyms() {
        let history = [h("the AI plan"), h("AI again"), h("more AI")]

        // "a i" as a pattern would collide with the article "a" and the pronoun "I".
        XCTAssertTrue(DictionaryHistoryLearner.buildEntries(history: history, existing: []).isEmpty)
    }

    func testHistoryLearnerDoesNotReaddAPatternTheUserDisabled() {
        let history = [h("the ATU owns it"), h("ask the ATU"), h("ATU signed off")]
        let disabled = DictionaryEntry(id: 7, pattern: "a t u", replacement: "ATU", wholeWord: true, enabled: false)

        XCTAssertTrue(DictionaryHistoryLearner.buildEntries(history: history, existing: [disabled]).isEmpty)
    }

    func testHistoryLearnerReturnsNothingWhenDictionaryAlreadyCoversTerm() {
        let history = [h("use ReBAC"), h("check ReBAC"), h("ship ReBAC")]

        let entries = DictionaryHistoryLearner.buildEntries(
            history: history,
            existing: [DictionaryEntry(pattern: "ree back", replacement: "ReBAC")])

        XCTAssertTrue(entries.isEmpty)
    }
}
