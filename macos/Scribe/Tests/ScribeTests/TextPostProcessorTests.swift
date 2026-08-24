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
}
