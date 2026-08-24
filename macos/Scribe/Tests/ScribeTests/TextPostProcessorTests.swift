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
}
