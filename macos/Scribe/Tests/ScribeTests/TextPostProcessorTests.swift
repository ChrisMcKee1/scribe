import XCTest
@testable import Scribe

final class TextPostProcessorTests: XCTestCase {
    func testDictionaryWholeWordReplacement() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub")],
            snippets: [])

        XCTAssertEqual(processor.process("i love github"), "i love GitHub")
    }

    func testDictionaryWholeWordDoesNotMatchSubstring() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub")],
            snippets: [])

        // "githubbing" contains "github" but is not the whole word "github"; must not be replaced.
        XCTAssertEqual(processor.process("githubbing is fun"), "githubbing is fun")
    }

    func testDictionaryCaseInsensitiveMatching() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "sherpa onnx", replacement: "sherpa-onnx")],
            snippets: [])

        XCTAssertEqual(processor.process("we use Sherpa ONNX daily"), "we use sherpa-onnx daily")
    }

    func testSnippetExpansion() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [],
            snippets: [Snippet(phrase: "sign off block", template: "Best regards,\nScribe Team")])

        XCTAssertEqual(
            processor.process("please add sign off block at the end"),
            "please add Best regards,\nScribe Team at the end")
    }

    func testSnippetExpandsBeforeDictionaryCanonicalizesItsOutput() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub")],
            snippets: [Snippet(phrase: "my repo plug", template: "check out my github page")])

        // The snippet's own template text should benefit from dictionary canonicalization in the
        // following phase, matching Windows' documented ordering (snippets first, then dictionary).
        XCTAssertEqual(processor.process("my repo plug"), "check out my GitHub page")
    }

    func testDisabledEntriesAreIgnored() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub", enabled: false)],
            snippets: [])

        XCTAssertEqual(processor.process("i love github"), "i love github")
    }

    func testEmptyInputReturnsEmptyString() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [], snippets: [])
        XCTAssertEqual(processor.process("   "), "")
    }

    func testNormalizesWhitespaceAndSpaceBeforePunctuation() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [], snippets: [])
        XCTAssertEqual(processor.process("hello   world , how are you ?"), "hello world, how are you?")
    }

    // MARK: - processDetailed

    func testProcessDetailedReportsExactChangedDictionarySpans() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [
                DictionaryEntry(pattern: "a p i m", replacement: "APIM"),
                DictionaryEntry(pattern: "azure", replacement: "Azure"),
            ],
            snippets: [])

        let result = processor.processDetailed("deploy a p i m to azure and API")

        XCTAssertEqual(result.text, "deploy APIM to Azure and API")
        XCTAssertEqual(result.replacements.count, 2)

        XCTAssertEqual(result.replacements[0].pattern, "a p i m")
        XCTAssertEqual(result.replacements[0].kind, .dictionary)
        XCTAssertEqual(
            (result.text as NSString).substring(
                with: NSRange(location: result.replacements[0].start, length: result.replacements[0].length)),
            "APIM")

        XCTAssertEqual(result.replacements[1].pattern, "azure")
        XCTAssertEqual(result.replacements[1].kind, .dictionary)
        XCTAssertEqual(
            (result.text as NSString).substring(
                with: NSRange(location: result.replacements[1].start, length: result.replacements[1].length)),
            "Azure")
    }

    func testProcessDetailedReportsSnippetSpanAfterDictionaryCanonicalization() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub")],
            snippets: [Snippet(phrase: "my repo plug", template: "check out my github page")])

        let result = processor.processDetailed("my repo plug")

        XCTAssertEqual(result.text, "check out my GitHub page")
        let snippetReplacement = result.replacements.first { $0.kind == .snippet }
        XCTAssertNotNil(snippetReplacement)
        XCTAssertEqual(snippetReplacement?.pattern, "my repo plug")
        XCTAssertEqual(snippetReplacement?.replacement, "check out my GitHub page")
    }

    func testProcessDetailedReportsNothingWhenTextIsUnchanged() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [DictionaryEntry(pattern: "azure", replacement: "azure")], snippets: [])

        let result = processor.processDetailed("we use azure")

        XCTAssertTrue(result.replacements.isEmpty)
    }

    func testProcessDetailedReturnsEmptyResultForBlankInput() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [], snippets: [])

        let result = processor.processDetailed("   ")

        XCTAssertEqual(result.text, "")
        XCTAssertTrue(result.replacements.isEmpty)
    }

    // MARK: - Tight punctuation and surrogate pairs

    func testPunctuationRuleAbsorbsTheSpaceBeforeIt() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [DictionaryEntry(pattern: "comma", replacement: ",")], snippets: [])

        XCTAssertEqual(processor.process("hello comma world"), "hello, world")
    }

    func testAnEmojiRightBeforeAPunctuationRuleIsKeptAndDoesNotTrap() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [DictionaryEntry(pattern: "comma", replacement: ",")], snippets: [])

        XCTAssertEqual(processor.process("\u{1F642}comma"), "\u{1F642},")
    }

    func testASnippetThatEndsInAnEmojiBeforeAPunctuationRuleDoesNotTrap() {
        let processor = TextPostProcessor()
        processor.reload(
            dictionaryEntries: [DictionaryEntry(pattern: "comma", replacement: ",")],
            snippets: [Snippet(phrase: "smile", template: "\u{1F642}comma")])

        let result = processor.processDetailed("smile")

        XCTAssertEqual(result.text, "\u{1F642},")
        XCTAssertEqual(result.replacements.first { $0.kind == .snippet }?.replacement, "\u{1F642},")
    }

    // MARK: - Rules compiled once per reload

    func testRulesLoadedOnceGiveTheSameOutputAsAFreshLoadForEveryDictation() {
        let entries = [
            DictionaryEntry(pattern: "github", replacement: "GitHub"),
            DictionaryEntry(pattern: "york", replacement: "New York"),
            DictionaryEntry(pattern: "comma", replacement: ","),
            DictionaryEntry(pattern: "a p i m", replacement: "APIM"),
        ]
        let snippets = [Snippet(phrase: "sign off", template: "Best regards,\ncheck my github")]
        let corpus = [
            "i love github comma really",
            "we moved to new york from york",
            "deploy a p i m then sign off",
            "nothing to change here",
            "github github comma york",
        ]
        let loadedOnce = TextPostProcessor()
        loadedOnce.reload(dictionaryEntries: entries, snippets: snippets)

        for _ in 0..<3 {
            for text in corpus {
                let fresh = TextPostProcessor()
                fresh.reload(dictionaryEntries: entries, snippets: snippets)
                let expected = fresh.processDetailed(text)
                let actual = loadedOnce.processDetailed(text)
                XCTAssertEqual(actual.text, expected.text, text)
                XCTAssertEqual(actual.replacements, expected.replacements, text)
            }
        }
    }

    func testReloadReplacesTheCompiledRules() {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: [DictionaryEntry(pattern: "github", replacement: "GitHub")], snippets: [])
        XCTAssertEqual(processor.process("github"), "GitHub")

        processor.reload(dictionaryEntries: [DictionaryEntry(pattern: "azure", replacement: "Azure")], snippets: [])

        XCTAssertEqual(processor.process("github and azure"), "github and Azure")
    }
}
