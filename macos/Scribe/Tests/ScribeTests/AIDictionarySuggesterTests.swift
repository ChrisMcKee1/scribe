import XCTest

@testable import Scribe

/// Windows' `AiDictionarySuggesterTests`, case for case, for the parts the Mac has: reading the answer, the history
/// sample, and the instructions. Windows' full-context request and its high-reasoning request are not ported.
final class AIDictionarySuggesterTests: XCTestCase {
    private func parse(
        _ response: String?, _ existing: [DictionaryEntry] = [],
        maxSuggestions: Int = AIDictionarySuggester.defaultMaxSuggestions
    ) -> [DictionaryEntry] {
        AIDictionarySuggester.parseSuggestions(response, existing: existing, maxSuggestions: maxSuggestions)
    }

    // MARK: - parseSuggestions

    func testParseSuggestionsMapsSpokenToPatternAndWrittenToReplacement() {
        let json = #"[{"spoken":"a p i m","written":"APIM"},{"spoken":"cosmos db","written":"Cosmos DB"}]"#

        let result = parse(json)

        XCTAssertEqual(result.count, 2)
        XCTAssertEqual(result[0].pattern, "a p i m")
        XCTAssertEqual(result[0].replacement, "APIM")
        XCTAssertEqual(result[1].pattern, "cosmos db")
        XCTAssertEqual(result[1].replacement, "Cosmos DB")
    }

    func testParseSuggestionsExtractsTheJSONArrayFromSurroundingNoise() {
        let response =
            "<think>let me find the terms</think>\nHere are the terms:\n```json\n"
            + #"[{"spoken":"kube control","written":"kubectl"}]"#
            + "\n```\nHope that helps!"

        let result = parse(response)

        XCTAssertEqual(result, [DictionaryEntry(pattern: "kube control", replacement: "kubectl")])
    }

    func testParseSuggestionsIgnoresBracketsInThinkingOrPreambleAndAcceptsObjectEnvelopesOrSingleObjects() {
        let withBracketInThink =
            "<think>Checking [1] recent history sample [lines 1-10]</think>\n"
            + #"{"suggestions":[{"spoken":"a p i m","written":"APIM"}]}"#
        XCTAssertEqual(parse(withBracketInThink), [DictionaryEntry(pattern: "a p i m", replacement: "APIM")])

        let singleObject = #"{"spoken":"post gres","written":"PostgreSQL"}"#
        XCTAssertEqual(parse(singleObject), [DictionaryEntry(pattern: "post gres", replacement: "PostgreSQL")])

        let bareCloseThinkAndTruncatedArray =
            #"[{"spoken":"wrong","written":"Wrong"}]</think>[{"spoken":"a p i m","written":"APIM"},"#
            + #"{"spoken":"cosmos db","written":"Cosmos DB"},{"spoken":"cut"#
        let fromTruncated = parse(bareCloseThinkAndTruncatedArray)
        XCTAssertEqual(fromTruncated.map(\.replacement), ["APIM", "Cosmos DB"])
    }

    func testParseSuggestionsSkipsTermsAlreadyInTheDictionary() {
        let json = #"[{"spoken":"azure","written":"Azure"},{"spoken":"a k s","written":"AKS"}]"#
        let existing = [DictionaryEntry(pattern: "azure", replacement: "Azure")]

        XCTAssertEqual(parse(json, existing).map(\.pattern), ["a k s"])
    }

    func testParseSuggestionsSelfHealsExistingEntriesWhenWrittenFormOrCasingDiffers() {
        let json = """
            [
              {"spoken":"azure","written":"Azure"},
              {"spoken":"a p i m","written":"APIM"},
              {"spoken":"kube control","written":"kubectl"}
            ]
            """
        let existing = [
            DictionaryEntry(pattern: "azure", replacement: "Azure"),
            DictionaryEntry(pattern: "a p i m", replacement: "Apim"),
            DictionaryEntry(pattern: "kube control", replacement: "KubeControl"),
        ]

        let result = parse(json, existing)

        XCTAssertEqual(
            result,
            [
                DictionaryEntry(pattern: "a p i m", replacement: "APIM"),
                DictionaryEntry(pattern: "kube control", replacement: "kubectl"),
            ])
    }

    // History keeps a dictation as it was typed even after a rule was added to fix it, so the model can read the
    // mistake as one of the user's terms and suggest writing it back.
    func testParseSuggestionsDropsASuggestionThatWritesAMistakeAnEnabledEntryFixes() {
        let json = """
            [
              {"spoken":"m c b","written":"MCB"},
              {"spoken":"em see bee","written":"mcb"},
              {"spoken":"post gress","written":"post gres"},
              {"spoken":"a k s","written":"AKS"}
            ]
            """
        let existing = [
            DictionaryEntry(pattern: "MCB", replacement: "MCP"),
            DictionaryEntry(pattern: "post gres", replacement: "PostgreSQL", wholeWord: false),
        ]

        XCTAssertEqual(parse(json, existing), [DictionaryEntry(pattern: "a k s", replacement: "AKS")])
    }

    func testParseSuggestionsKeepsASuggestionThatWritesExactlyWhatAnEntryWrites() {
        let json = """
            [
              {"spoken":"git hub","written":"GitHub"},
              {"spoken":"get hub","written":"Github"},
              {"spoken":"a i studio","written":"AI"}
            ]
            """
        let existing = [
            DictionaryEntry(pattern: "github", replacement: "GitHub"),
            DictionaryEntry(pattern: "AI", replacement: "AI"),
        ]

        let result = parse(json, existing)

        XCTAssertEqual(result.map(\.pattern), ["git hub", "a i studio"])
        XCTAssertEqual(result.map(\.replacement), ["GitHub", "AI"])
    }

    func testParseSuggestionsDoesNotTreatADisabledEntrysSpokenFormAsAMistake() {
        let json = #"[{"spoken":"m c b","written":"MCB"}]"#
        let existing = [DictionaryEntry(id: 7, pattern: "MCB", replacement: "MCP", wholeWord: true, enabled: false)]

        XCTAssertEqual(parse(json, existing), [DictionaryEntry(pattern: "m c b", replacement: "MCB")])
    }

    func testParseSuggestionsDedupesBySpokenFormAndSkipsNoOpsAndBlanks() {
        let json = """
            [
              {"spoken":"a p i","written":"API"},
              {"spoken":"a p i","written":"API duplicate"},
              {"spoken":"same","written":"same"},
              {"spoken":"","written":"Empty"},
              {"spoken":"good","written":"Good"}
            ]
            """

        let result = parse(json)

        // "a p i" (the first wins) and "good"; the no-op and the blank are dropped.
        XCTAssertEqual(
            result,
            [
                DictionaryEntry(pattern: "a p i", replacement: "API"),
                DictionaryEntry(pattern: "good", replacement: "Good"),
            ])
    }

    func testParseSuggestionsReturnsEmptyForUnparseableResponses() {
        XCTAssertTrue(parse("no json here").isEmpty)
        XCTAssertTrue(parse(nil).isEmpty)
        XCTAssertTrue(parse("[ this is not valid json ]").isEmpty)
    }

    func testParseSuggestionsRespectsTheMaxCap() {
        let items = (0..<10).map { #"{"spoken":"term \#($0)","written":"Term\#($0)"}"# }
        let json = "[" + items.joined(separator: ",") + "]"

        XCTAssertEqual(parse(json, maxSuggestions: 3).count, 3)
    }

    // MARK: - What the Mac adds to Windows' cases

    func testParseSuggestionsKeepsACasingOnlyFixAndReadsKeysIgnoringCase() {
        // The instructions ask for the lowercase of a term that is only mis-cased, so "github" to "GitHub" is a rule.
        let json = #"[{"Spoken":"github","WRITTEN":"GitHub"}]"#

        XCTAssertEqual(parse(json), [DictionaryEntry(pattern: "github", replacement: "GitHub")])
    }

    func testParseSuggestionsFlattensControlCharactersAndCapsTermLength() {
        let long = String(repeating: "x", count: AIDictionarySuggester.maxTermChars + 1)
        let json = """
            [
              {"spoken":"multi\\nline\\tterm","written":"Multi  Line"},
              {"spoken":"\(long)","written":"Long"},
              {"spoken":"short","written":"\(long)"}
            ]
            """

        XCTAssertEqual(parse(json), [DictionaryEntry(pattern: "multi line term", replacement: "Multi Line")])
    }

    func testParseSuggestionsTakesTrailingCommasAndComments() {
        let json = """
            [
              // the one term
              {"spoken":"a k s","written":"AKS",},
            ]
            """

        XCTAssertEqual(parse(json), [DictionaryEntry(pattern: "a k s", replacement: "AKS")])
    }

    func testSpokenKeyIgnoresCaseAndSpacing() {
        XCTAssertEqual(AIDictionarySuggester.spokenKey("  A  P\tI "), "a p i")
        XCTAssertEqual(AIDictionarySuggester.spokenKey(" "), "")
    }

    // MARK: - buildHistorySample

    func testBuildHistorySampleDedupesAndJoinsRecentDictations() {
        let sample = AIDictionarySuggester.buildHistorySample([
            "deploy to azure", "deploy to azure", "configure the cluster",
        ])

        XCTAssertEqual(sample, "deploy to azure\nconfigure the cluster")
    }

    func testBuildHistorySampleReturnsEmptyForNoUsableHistory() {
        XCTAssertEqual(AIDictionarySuggester.buildHistorySample([]), "")
        XCTAssertEqual(AIDictionarySuggester.buildHistorySample(["   "]), "")
    }

    func testBuildHistorySampleBoundsTheOutputToMaxChars() {
        let entries = (0..<200).map { "dictation number \($0) about kubernetes" }

        let sample = AIDictionarySuggester.buildHistorySample(entries, maxChars: 100)

        XCTAssertLessThanOrEqual(sample.count, 100)
        XCTAssertTrue(sample.hasPrefix("dictation number 0"))
    }

    func testBuildHistorySampleTruncatesASingleOversizedDictation() {
        let sample = AIDictionarySuggester.buildHistorySample([String(repeating: "x", count: 500)], maxChars: 100)

        XCTAssertEqual(sample.count, 100)
    }

    func testBuildHistorySampleDefaultsToWindowsBounds() {
        XCTAssertEqual(AIDictionarySuggester.defaultMaxSampleChars, 6_000)
        XCTAssertEqual(AIDictionarySuggester.defaultMaxSuggestions, 25)
        XCTAssertEqual(AIDictionarySuggester.historyLimit, 1_000)
        let entries = (0..<1_000).map { "dictation \($0) about the cube cluster and the home chart" }
        XCTAssertLessThanOrEqual(AIDictionarySuggester.buildHistorySample(entries).count, 6_000)
    }
}
