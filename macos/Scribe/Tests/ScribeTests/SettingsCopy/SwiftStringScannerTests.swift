import Foundation
import XCTest

@testable import Scribe

final class SwiftStringScannerTests: XCTestCase {
    private func texts(_ source: String) -> [String] {
        SwiftStringScanner.literals(in: source).map(\.text)
    }

    func testItReadsPlainStringsAndSkipsComments() {
        let source = """
            // let a = "in a comment"
            /* let b = "in a block /* nested */ still" */
            let c = "Hello there"
            /// "doc comment"
            """
        XCTAssertEqual(texts(source), ["Hello there"])
    }

    func testItDropsInterpolationsAndReadsEscapes() {
        let source = #"let a = "Saved \(count) words, \(name.map { "x" } ?? "y")""#
        XCTAssertEqual(texts(source), ["Saved  words, ", "x", "y"])
        XCTAssertEqual(texts(#"let a = "Say \"hi\" \u{201C}now\u{201D}""#), ["Say \"hi\" \u{201C}now\u{201D}"])
    }

    func testItReadsRawAndMultilineStrings() {
        XCTAssertEqual(texts(##"let a = #"A \b word "quoted""#"##), [#"A \b word "quoted""#])
        let source = "let a = \"\"\"\n    First line\n    Second line\n    \"\"\"\nlet b = \"after\""
        XCTAssertEqual(texts(source).count, 2)
        XCTAssertEqual(texts(source).last, "after")
        XCTAssertEqual(SwiftStringScanner.literals(in: source).last?.line, 5)
    }

    func testItMarksLogCallsAndFailuresAsExempt() {
        let source = """
            ScribeLog.warning(.app, "A hotkey was not found", .count("n", 1))
            fatalError("Not a real hotkey")
            let regex = try NSRegularExpression(pattern: "hotkey word")
            let shown = "Choose a hotkey"
            """
        let found = SwiftStringScanner.literals(in: source)
        XCTAssertEqual(found.filter { !$0.exempt }.map(\.text), ["Choose a hotkey"])
    }

    func testOnlyWordsAPersonReadsCount() {
        func looks(_ text: String) -> Bool { SwiftLiteral(text: text, line: 1, exempt: false).looksLikeText }
        XCTAssertTrue(looks("Two words"))
        XCTAssertTrue(looks("Provider"))
        XCTAssertFalse(looks("provider"))
        XCTAssertFalse(looks("Privacy_Accessibility"))
        XCTAssertFalse(looks("%d"))
        XCTAssertFalse(looks(""))
    }

    func testAcronymLabelsCountAsTextButKeysAndPathsDoNot() {
        func looks(_ text: String) -> Bool { SwiftLiteral(text: text, line: 1, exempt: false).looksLikeText }
        for label in ["P50", "P95", "RTF", "CLI", "DPAPI"] {
            XCTAssertTrue(looks(label), label)
        }
        for key in ["p50", "Scribe.Overlay.exe", "libraries.state", "Privacy_Accessibility", "<transcript>"] {
            XCTAssertFalse(looks(key), key)
        }
    }

    func testStringsInsideAnInterpolationAreScannedToo() {
        let found = texts(#"let a = "Words: \(flag ? "Provider" : "Library") left""#)
        XCTAssertTrue(found.contains("Provider"))
        XCTAssertTrue(found.contains("Library"))
        XCTAssertTrue(found.contains("Words:  left"))
    }

    func testOnlyTheCallsOfACatalogAreExemptNotTheRestOfItsFile() {
        let source = """
            let loose = "The old hotkey"
            let item = CopyItem.same("x.y", "The hotkey")
            let omission = CopyOmission(windows: "The hotkey", because: .windowsOnly)
            """
        let found = SwiftStringScanner.literals(in: source)
        XCTAssertEqual(found.filter { !$0.exempt }.map(\.text), ["The old hotkey"])
    }
}
