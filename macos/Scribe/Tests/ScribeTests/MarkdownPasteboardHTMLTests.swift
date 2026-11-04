import Foundation
import XCTest

@testable import Scribe

/// The HTML "Markdown with formatting" adds to the pasteboard, ported from Windows' `MarkdownClipboardHtmlTests`:
/// rendered only for text with Markdown structure, never changing a character the text keeps literally.
final class MarkdownPasteboardHTMLTests: XCTestCase {
    private func render(_ text: String, file: StaticString = #filePath, line: UInt = #line) -> String {
        guard let html = MarkdownPasteboardHTML.render(text) else {
            XCTFail("no HTML for \(text.debugDescription)", file: file, line: line)
            return ""
        }
        return html
    }

    private func assertMatches(_ pattern: String, _ html: String, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertNotNil(html.range(of: pattern, options: .regularExpression), html, file: file, line: line)
    }

    func testTextWithoutMarkdownStructureGetsNoHTML() {
        let plain = [
            "Just a sentence.",
            "Two lines\nof plain prose.",
            "Paragraph one.\n\nParagraph two.",
            "See https://example.com for details.",
            "Five * three * two is thirty.",
            "snake_case_name and file_name.txt",
            "The path C:\\Users\\me\\.copilot\\config.json is fine.",
            "AT&T and &copy; stay as typed.",
            "A single ~tilde~ and a ~~~triple~~~ run.",
            "Brackets [like these] and a lone ] stay text.",
            "",
            "   ",
            "\n\n",
        ]
        for text in plain {
            XCTAssertNil(MarkdownPasteboardHTML.render(text), text.debugDescription)
        }
    }

    func testMarkdownStructureRendersAsHTML() {
        let cases: [(String, String, String?)] = [
            ("- one\n- two", "<ul>", "<li>one</li>"),
            ("1. first\n2. second", "<ol>", "<li>first</li>"),
            ("**Bold** words", "<strong>Bold</strong>", nil),
            ("Some *italic* words", "<em>italic</em>", nil),
            ("Some ~~gone~~ words", "<del>gone</del>", nil),
            ("Run `dotnet test` now", "<code>dotnet test</code>", nil),
            ("# Heading", "<h1", "Heading</h1>"),
            ("> A quoted line", "<blockquote>", "A quoted line"),
            ("A [link](https://example.com) here", "<a href=\"https://example.com\">link</a>", nil),
            ("```python\nprint(\"hello\")\n```", "<pre><code class=\"language-python\">", "print(&quot;hello&quot;)"),
            ("| Name | Value |\n| --- | --- |\n| a | 1 |", "<table>", "<td>a</td>"),
            ("- [ ] Open task\n- [x] Done task", "type=\"checkbox\"", "Done task"),
        ]
        for (text, expected, alsoExpected) in cases {
            let html = render(text)
            XCTAssertTrue(html.contains(expected), "\(text.debugDescription): \(html)")
            if let alsoExpected {
                XCTAssertTrue(html.contains(alsoExpected), "\(text.debugDescription): \(html)")
            }
        }
    }

    func testTheRenderingMatchesMarkdigsForCommonDictations() {
        XCTAssertEqual(render("- one\n- two"), "<ul>\n<li>one</li>\n<li>two</li>\n</ul>")
        XCTAssertEqual(render("3. third\n4. fourth"), "<ol start=\"3\">\n<li>third</li>\n<li>fourth</li>\n</ol>")
        XCTAssertEqual(
            render("# Title\n\nSome **bold** text."), "<h1>Title</h1>\n<p>Some <strong>bold</strong> text.</p>")
        XCTAssertEqual(
            render("- [ ] Open\n- [x] Done"),
            "<ul class=\"contains-task-list\">\n"
                + "<li class=\"task-list-item\"><input disabled=\"disabled\" type=\"checkbox\" /> Open</li>\n"
                + "<li class=\"task-list-item\"><input disabled=\"disabled\" type=\"checkbox\" checked=\"checked\" />"
                + " Done</li>\n</ul>")
        XCTAssertEqual(
            render("| Name | Value |\n| :--- | ---: |\n| a | 1 |"),
            "<table>\n<thead>\n<tr>\n<th style=\"text-align: left;\">Name</th>\n"
                + "<th style=\"text-align: right;\">Value</th>\n</tr>\n</thead>\n<tbody>\n<tr>\n"
                + "<td style=\"text-align: left;\">a</td>\n<td style=\"text-align: right;\">1</td>\n</tr>\n"
                + "</tbody>\n</table>")
        XCTAssertEqual(render("```\nlet x = 1\n```"), "<pre><code>let x = 1\n</code></pre>")
        XCTAssertEqual(render("Before\n\n---\n\nAfter **x**"), "<p>Before</p>\n<hr />\n<p>After <strong>x</strong></p>")
        XCTAssertEqual(render("Title\n=====\n\nBody *x*"), "<h1>Title</h1>\n<p>Body <em>x</em></p>")
    }

    func testNestedBulletsRenderAsANestedList() {
        let html = render("- First\n- Second\n  - Nested")
        assertMatches("<li>Second\\s*<ul>\\s*<li>Nested</li>", html)
        XCTAssertEqual(html, "<ul>\n<li>First</li>\n<li>Second\n<ul>\n<li>Nested</li>\n</ul>\n</li>\n</ul>")
    }

    func testABlankLineBetweenItemsMakesTheListLoose() {
        XCTAssertEqual(render("- one\n\n- two"), "<ul>\n<li>\n<p>one</p>\n</li>\n<li>\n<p>two</p>\n</li>\n</ul>")
        // A blank line between a nested list's items loosens that list only.
        let nested = render("- a\n  - b\n\n  - c")
        XCTAssertTrue(nested.hasPrefix("<ul>\n<li>a\n<ul>"), nested)
        XCTAssertTrue(nested.contains("<p>b</p>"), nested)
    }

    func testEveryLineBreakStaysALineBreak() {
        assertMatches("Line one<br\\s*/?>\\s*Line two", render("- item\n\nLine one\nLine two"))
        XCTAssertEqual(render("**a**\nb  \nc"), "<p><strong>a</strong><br />\nb<br />\nc</p>")
        XCTAssertEqual(render("- one\ncontinued"), "<ul>\n<li>one<br />\ncontinued</li>\n</ul>")
        XCTAssertEqual(render("> quoted\nlazy"), "<blockquote>\n<p>quoted<br />\nlazy</p>\n</blockquote>")
    }

    func testAWindowsPathKeepsEveryBackslash() {
        let html = render("- Open C:\\Users\\me\\.copilot\\config.json and C:\\temp\\_draft")
        XCTAssertTrue(html.contains("C:\\Users\\me\\.copilot\\config.json"), html)
        XCTAssertTrue(html.contains("C:\\temp\\_draft"), html)
        XCTAssertEqual(render("- \\*not escaped\\*"), "<ul>\n<li>\\<em>not escaped\\</em></li>\n</ul>")
        XCTAssertTrue(render("- a \\` b").contains("a \\` b"), render("- a \\` b"))
    }

    func testEntitiesStayAsTyped() {
        let html = render("- AT&T and &copy; and &#169;")
        XCTAssertTrue(html.contains("AT&amp;T"), html)
        XCTAssertTrue(html.contains("&amp;copy;"), html)
        XCTAssertTrue(html.contains("&amp;#169;"), html)
        XCTAssertFalse(html.contains("\u{00A9}"), html)
    }

    func testDictatedMarkupIsEscapedTextNeverMarkup() {
        let html = render("- <script>alert(1)</script>\n- <b>bold?</b>\n\n<div>block</div>")
        XCTAssertNil(html.range(of: "<script", options: .caseInsensitive), html)
        XCTAssertFalse(html.contains("<b>"), html)
        XCTAssertFalse(html.contains("<div>"), html)
        XCTAssertTrue(html.contains("&lt;script&gt;"), html)
        XCTAssertTrue(html.contains("&lt;div&gt;block&lt;/div&gt;"), html)
        XCTAssertTrue(render("`<b>` and **x**").contains("<code>&lt;b&gt;</code>"))
    }

    func testABareWebAddressIsLinkedInsideStructuredText() {
        let html = render("- See https://example.com/path for details")
        XCTAssertTrue(
            html.contains("<a href=\"https://example.com/path\">https://example.com/path</a>"), html)
        XCTAssertTrue(
            render("- Visit www.example.com.").contains("<a href=\"http://www.example.com\">www.example.com</a>."))
        XCTAssertTrue(
            render("- (see https://example.com/a_(b))").contains("<a href=\"https://example.com/a_(b)\">"))
        XCTAssertTrue(render("- see https://example.com/x).").contains("href=\"https://example.com/x\""))
    }

    func testLinksOfEveryKindRender() {
        // A destination with a space in it is no link.
        XCTAssertNil(MarkdownPasteboardHTML.render("[docs](https://example.com/a b)"))
        XCTAssertEqual(
            render("A [link](https://example.com \"The title\")"),
            "<p>A <a href=\"https://example.com\" title=\"The title\">link</a></p>")
        XCTAssertEqual(
            render("Mail <name@example.com> or <https://example.com>"),
            "<p>Mail <a href=\"mailto:name@example.com\">name@example.com</a> or "
                + "<a href=\"https://example.com\">https://example.com</a></p>")
        XCTAssertEqual(
            render("[**bold** link](https://example.com)"),
            "<p><a href=\"https://example.com\"><strong>bold</strong> link</a></p>")
        XCTAssertEqual(
            render("[caf\u{00E9}](https://example.com/caf\u{00E9} \"t\")"),
            "<p><a href=\"https://example.com/caf%C3%A9\" title=\"t\">caf\u{00E9}</a></p>")
        // No link inside a link.
        XCTAssertEqual(
            render("[see https://a.example](https://b.example)"),
            "<p><a href=\"https://b.example\">see https://a.example</a></p>")
    }

    /// Pasted HTML never carries an address an app could run, and never asks an app to fetch an image.
    func testUnsafeAddressesAndImagesStayText() {
        XCTAssertNil(MarkdownPasteboardHTML.render("[click](javascript:alert(1))"))
        let html = render("- [click](javascript:alert(1)) and [data](DATA:text/html,x)")
        XCTAssertFalse(html.contains("href"), html)
        XCTAssertTrue(html.contains("[click](javascript:alert(1))"), html)
        XCTAssertFalse(render("- <javascript:alert(1)>").contains("href"))
        let image = render("![logo](https://example.com/logo.png)")
        XCTAssertFalse(image.contains("<img"), image)
        XCTAssertTrue(image.contains("!<a href=\"https://example.com/logo.png\">logo</a>"), image)
    }

    func testTextWithALinkReferenceDefinitionGetsNoHTML() {
        XCTAssertNil(MarkdownPasteboardHTML.render("- See [the notes][1]\n\n[1]: https://example.com"))
    }

    func testTextOutsideMarkupReadsTheSameInTheHTML() {
        let html = render("Intro line\n- First item with AT&T\n- Second item, 5 < 6\n\nClosing line")
        let visible = html.replacingOccurrences(of: "<[^>]+>", with: " ", options: .regularExpression)
            .replacingOccurrences(of: "&lt;", with: "<")
            .replacingOccurrences(of: "&gt;", with: ">")
            .replacingOccurrences(of: "&quot;", with: "\"")
            .replacingOccurrences(of: "&amp;", with: "&")
        for words in ["Intro line", "First item with AT&T", "Second item, 5 < 6", "Closing line"] {
            XCTAssertTrue(visible.contains(words), "\(words) in \(visible)")
        }
    }

    func testEmphasisFollowsCommonMarksDelimiterRules() {
        XCTAssertEqual(render("***both***"), "<p><em><strong>both</strong></em></p>")
        XCTAssertEqual(render("**a *b* c**"), "<p><strong>a <em>b</em> c</strong></p>")
        XCTAssertEqual(render("_under_ and __double__"), "<p><em>under</em> and <strong>double</strong></p>")
        XCTAssertEqual(render("**unclosed and *em*"), "<p>**unclosed and <em>em</em></p>")
        XCTAssertEqual(render("*foo**bar**baz*"), "<p><em>foo<strong>bar</strong>baz</em></p>")
        XCTAssertEqual(render("a ~~b~~ c `d`"), "<p>a <del>b</del> c <code>d</code></p>")
        XCTAssertEqual(render("``code with ` tick`` x *y*"), "<p><code>code with ` tick</code> x <em>y</em></p>")
        XCTAssertEqual(render("`unclosed and *em*"), "<p>`unclosed and <em>em</em></p>")
    }

    func testDeepNestingIsBoundedAndNeverCrashes() {
        XCTAssertNil(MarkdownPasteboardHTML.render(String(repeating: "> ", count: 500) + "deep"))
        let lists = (0..<200).map { String(repeating: "  ", count: $0) + "- level" }.joined(separator: "\n")
        _ = MarkdownPasteboardHTML.render(lists)
        _ = MarkdownPasteboardHTML.render(
            String(repeating: "*", count: 2_000) + "x" + String(repeating: "*", count: 2_000))
        _ = MarkdownPasteboardHTML.render(
            String(repeating: "[", count: 2_000) + "x" + String(repeating: "](u)", count: 2_000))
        _ = MarkdownPasteboardHTML.render(String(repeating: "`", count: 1_000))
    }

    func testCarriageReturnsAreLineBreaks() {
        XCTAssertEqual(render("- one\r\n- two"), render("- one\n- two"))
        XCTAssertEqual(render("- one\r- two"), render("- one\n- two"))
    }
}
