import Foundation

/// Renders a dictation's Markdown to the HTML fragment "Markdown with formatting" puts on the pasteboard beside the
/// Markdown text. Mirrors Windows' `MarkdownClipboardHtml`: apps that read HTML but leave pasted Markdown as typed
/// (Teams, Microsoft Copilot, Codex; measured on Windows 2026-11-04) then show real lists, emphasis, links, code and
/// tables, while an app that reads only text gets the same Markdown as before.
///
/// The rendering never changes a character the text keeps literally. Backslash escapes and HTML entities are not
/// parsed, so a dictated `C:\Users\me\.config` or `&copy;` reads exactly as dictated (CommonMark would turn `\.` into
/// `.`), and raw HTML is not parsed either, so `<script>` in a dictation is escaped text, never markup. Every line
/// break stays a line break: a single newline renders as `<br />` rather than CommonMark's space. Foundation's own
/// Markdown parser (`AttributedString(markdown:)`) has no option to turn backslash escapes or entities off, which is
/// why the Mac renders this subset itself rather than use it, and why it needs no package.
///
/// What it renders is the CommonMark and GitHub subset Windows renders through Markdig: ATX and setext headings,
/// paragraphs, block quotes, bullet and numbered lists (nested, tight or loose, with task boxes), fenced code blocks,
/// thematic breaks, pipe tables, emphasis, strong emphasis, strikethrough, code spans, inline links and web addresses.
/// Indented code blocks and images are not: an indented line stays text, and `![alt](src)` renders as `!` and a link,
/// so pasted HTML never asks an app to fetch an image. A link to a `javascript:`, `vbscript:`, `data:` or `file:`
/// address renders as its text.
///
/// Text without Markdown structure (plain sentences, with or without a bare web address) gets no HTML at all, so plain
/// prose pastes exactly as it did before this format existed. A link reference definition (`[1]: https://...`) would
/// render as nothing and drop a dictated line, so text holding one gets no HTML either; so does text nested deeper
/// than `maximumNestingDepth`.
enum MarkdownPasteboardHTML {
    /// Deep enough for any list a person dictates, and a bound on a pathological input's work (Windows' Markdig
    /// `MaximumNestingDepth`).
    static let maximumNestingDepth = 32

    /// The HTML fragment for `markdown`, or nil when it has no Markdown structure worth rendering or would lose a line.
    static func render(_ markdown: String) -> String? {
        let normalized = markdown.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(
            of: "\r", with: "\n")
        guard normalized.contains(where: { !$0.isWhitespace }) else {
            return nil
        }
        let lines = normalized.split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        var parser = MarkdownBlockParser()
        guard let blocks = try? parser.parse(lines, depth: 0) else {
            return nil
        }
        let document = blocks.map(MarkdownBlockNode.init)
        guard document.contains(where: \.hasStructure) else {
            return nil
        }
        var html = ""
        for block in document {
            block.render(into: &html, tight: false)
        }
        while let last = html.last, last.isWhitespace {
            html.removeLast()
        }
        return html.isEmpty ? nil : html
    }
}

// MARK: - Blocks

private enum MarkdownRenderRefusal: Error {
    case tooDeep
    case referenceDefinition
}

private enum TableAlignment {
    case left
    case center
    case right
}

/// A block as parsed, before its inline text is.
private indirect enum MarkdownBlock {
    case paragraph([String])
    case heading(level: Int, text: String)
    case thematicBreak
    case code(language: String, lines: [String])
    case quote([MarkdownBlock])
    case list(ordered: Bool, start: Int, tight: Bool, items: [MarkdownListItem])
    case table(alignments: [TableAlignment?], header: [String], rows: [[String]])
}

private struct MarkdownListItem {
    var blocks: [MarkdownBlock]
    /// Nil for an ordinary item, false for `[ ]`, true for `[x]`.
    var task: Bool?
}

private struct ListMarker {
    let ordered: Bool
    /// `-`, `+` or `*` for a bullet, `.` or `)` for a number.
    let symbol: Character
    let number: Int
    /// The column where the item's content starts.
    let contentIndent: Int
    let content: String
}

/// Splits lines into blocks, the way CommonMark does for the constructs this renderer knows.
private struct MarkdownBlockParser {
    mutating func parse(_ lines: [String], depth: Int) throws -> [MarkdownBlock] {
        guard depth <= MarkdownPasteboardHTML.maximumNestingDepth else {
            throw MarkdownRenderRefusal.tooDeep
        }
        var blocks: [MarkdownBlock] = []
        var paragraph: [String] = []
        func closeParagraph() {
            if !paragraph.isEmpty {
                blocks.append(.paragraph(paragraph))
                paragraph = []
            }
        }

        var index = 0
        while index < lines.count {
            let line = lines[index]
            if Self.isBlank(line) {
                closeParagraph()
                index += 1
                continue
            }
            let indent = Self.indentWidth(line)
            let rest = Self.dropIndent(line, upTo: indent)
            if indent <= 3 {
                if let fence = Self.fence(rest) {
                    closeParagraph()
                    var body: [String] = []
                    index += 1
                    while index < lines.count {
                        let candidate = lines[index]
                        if Self.indentWidth(candidate) <= 3,
                            Self.closesFence(Self.dropIndent(candidate, upTo: 3), fence.marker, fence.length)
                        {
                            index += 1
                            break
                        }
                        body.append(Self.dropIndent(candidate, upTo: indent))
                        index += 1
                    }
                    blocks.append(.code(language: fence.language, lines: body))
                    continue
                }
                if let heading = Self.atxHeading(rest) {
                    closeParagraph()
                    blocks.append(.heading(level: heading.level, text: heading.text))
                    index += 1
                    continue
                }
                if !paragraph.isEmpty, let level = Self.setextLevel(rest) {
                    blocks.append(.heading(level: level, text: paragraph.joined(separator: "\n")))
                    paragraph = []
                    index += 1
                    continue
                }
                if Self.isThematicBreak(rest) {
                    closeParagraph()
                    blocks.append(.thematicBreak)
                    index += 1
                    continue
                }
                if rest.hasPrefix(">") {
                    closeParagraph()
                    var inner: [String] = []
                    while index < lines.count {
                        let candidate = lines[index]
                        let candidateIndent = Self.indentWidth(candidate)
                        let candidateRest = Self.dropIndent(candidate, upTo: candidateIndent)
                        if candidateIndent <= 3, candidateRest.hasPrefix(">") {
                            var content = String(candidateRest.dropFirst())
                            if content.hasPrefix(" ") || content.hasPrefix("\t") {
                                content.removeFirst()
                            }
                            inner.append(content)
                        } else if !Self.isBlank(candidate), let last = inner.last, !Self.isBlank(last),
                            !Self.startsBlock(candidate)
                        {
                            // A lazy continuation line of the quoted paragraph.
                            inner.append(candidate)
                        } else {
                            break
                        }
                        index += 1
                    }
                    blocks.append(.quote(try parse(inner, depth: depth + 1)))
                    continue
                }
                if let marker = Self.listMarker(line),
                    paragraph.isEmpty || Self.mayInterruptParagraph(marker)
                {
                    closeParagraph()
                    let (list, next) = try parseList(lines, from: index, first: marker, depth: depth)
                    blocks.append(list)
                    index = next
                    continue
                }
                if index + 1 < lines.count, rest.contains("|"),
                    let alignments = Self.tableDelimiter(lines[index + 1]),
                    Self.cells(rest).count == alignments.count
                {
                    closeParagraph()
                    let header = Self.cells(rest)
                    var rows: [[String]] = []
                    index += 2
                    while index < lines.count, !Self.isBlank(lines[index]), lines[index].contains("|"),
                        !Self.startsBlock(lines[index])
                    {
                        var row = Self.cells(Self.dropIndent(lines[index], upTo: 3))
                        if row.count < alignments.count {
                            row += Array(repeating: "", count: alignments.count - row.count)
                        }
                        rows.append(Array(row.prefix(alignments.count)))
                        index += 1
                    }
                    blocks.append(.table(alignments: alignments, header: header, rows: rows))
                    continue
                }
            }
            if paragraph.isEmpty, Self.isReferenceDefinition(rest) {
                throw MarkdownRenderRefusal.referenceDefinition
            }
            paragraph.append(Self.trimmed(rest))
            index += 1
        }
        closeParagraph()
        return blocks
    }

    /// One list, from its first item to the line that ends it.
    private mutating func parseList(
        _ lines: [String], from start: Int, first: ListMarker, depth: Int
    ) throws -> (MarkdownBlock, Int) {
        guard depth + 1 <= MarkdownPasteboardHTML.maximumNestingDepth else {
            throw MarkdownRenderRefusal.tooDeep
        }
        var items: [MarkdownListItem] = []
        var tight = true
        var index = start
        while index < lines.count, let marker = Self.listMarker(lines[index]),
            marker.ordered == first.ordered, marker.symbol == first.symbol
        {
            var itemLines = [marker.content]
            index += 1
            while index < lines.count {
                let line = lines[index]
                if Self.isBlank(line) {
                    itemLines.append("")
                    index += 1
                    continue
                }
                let indent = Self.indentWidth(line)
                if indent >= marker.contentIndent {
                    itemLines.append(Self.dropIndent(line, upTo: marker.contentIndent))
                    index += 1
                    continue
                }
                if itemLines.last.map(Self.isBlank) ?? false {
                    break
                }
                if Self.startsBlock(line) || Self.listMarker(line) != nil {
                    break
                }
                // A lazy continuation line of the item's paragraph.
                itemLines.append(Self.trimmed(line))
                index += 1
            }

            var trailingBlanks = 0
            while let last = itemLines.last, Self.isBlank(last), itemLines.count > 1 {
                itemLines.removeLast()
                trailingBlanks += 1
            }
            if trailingBlanks > 0, index < lines.count, let next = Self.listMarker(lines[index]),
                next.ordered == first.ordered, next.symbol == first.symbol
            {
                tight = false
            }
            if Self.hasBlankLineBetweenBlocks(itemLines) {
                tight = false
            }

            var item = MarkdownListItem(blocks: try parse(itemLines, depth: depth + 1), task: nil)
            if case .paragraph(var paragraphLines)? = item.blocks.first, let firstLine = paragraphLines.first,
                let task = Self.taskBox(firstLine)
            {
                item.task = task.checked
                paragraphLines[0] = task.rest
                item.blocks[0] = .paragraph(paragraphLines)
            }
            items.append(item)
        }
        return (.list(ordered: first.ordered, start: first.number, tight: tight, items: items), index)
    }

    // MARK: Line tests

    static func isBlank(_ line: String) -> Bool {
        line.allSatisfy { $0 == " " || $0 == "\t" }
    }

    /// Leading white space as columns, a tab running to the next multiple of four.
    static func indentWidth(_ line: String) -> Int {
        var width = 0
        for character in line {
            if character == " " {
                width += 1
            } else if character == "\t" {
                width += 4 - width % 4
            } else {
                break
            }
        }
        return width
    }

    /// `line` without up to `columns` columns of leading white space.
    static func dropIndent(_ line: String, upTo columns: Int) -> String {
        var width = 0
        var index = line.startIndex
        while index < line.endIndex, width < columns {
            let character = line[index]
            if character == " " {
                width += 1
            } else if character == "\t" {
                let next = width + 4 - width % 4
                guard next <= columns else {
                    return String(repeating: " ", count: next - columns) + line[line.index(after: index)...]
                }
                width = next
            } else {
                break
            }
            index = line.index(after: index)
        }
        return String(line[index...])
    }

    static func trimmed(_ line: String) -> String {
        line.trimmingCharacters(in: CharacterSet(charactersIn: " \t"))
    }

    /// Whether `line` starts a block that ends a paragraph or a list item's lazy continuation.
    static func startsBlock(_ line: String) -> Bool {
        let indent = indentWidth(line)
        guard indent <= 3 else { return false }
        let rest = dropIndent(line, upTo: indent)
        return rest.hasPrefix(">") || fence(rest) != nil || atxHeading(rest) != nil || isThematicBreak(rest)
    }

    static func fence(_ text: String) -> (marker: Character, length: Int, language: String)? {
        guard let marker = text.first, marker == "`" || marker == "~" else { return nil }
        let length = text.prefix { $0 == marker }.count
        guard length >= 3 else { return nil }
        let info = trimmed(String(text.dropFirst(length)))
        if marker == "`", info.contains("`") {
            return nil
        }
        let language = info.split(whereSeparator: { $0 == " " || $0 == "\t" }).first.map(String.init) ?? ""
        return (marker, length, language)
    }

    static func closesFence(_ text: String, _ marker: Character, _ length: Int) -> Bool {
        let run = text.prefix { $0 == marker }.count
        return run >= length && isBlank(String(text.dropFirst(run)))
    }

    static func atxHeading(_ text: String) -> (level: Int, text: String)? {
        let level = text.prefix { $0 == "#" }.count
        guard (1...6).contains(level) else { return nil }
        let rest = text.dropFirst(level)
        guard rest.isEmpty || rest.first == " " || rest.first == "\t" else { return nil }
        var content = trimmed(String(rest))
        // An optional closing run of #, after a space or alone.
        let closing = content.reversed().prefix { $0 == "#" }.count
        if closing > 0 {
            let before = content.dropLast(closing)
            if before.isEmpty || before.last == " " || before.last == "\t" {
                content = trimmed(String(before))
            }
        }
        return (level, content)
    }

    static func setextLevel(_ text: String) -> Int? {
        let content = trimmed(text)
        guard let first = content.first, first == "=" || first == "-", content.allSatisfy({ $0 == first }) else {
            return nil
        }
        return first == "=" ? 1 : 2
    }

    static func isThematicBreak(_ text: String) -> Bool {
        guard let marker = text.first, marker == "-" || marker == "*" || marker == "_" else { return false }
        var count = 0
        for character in text {
            if character == marker {
                count += 1
            } else if character != " " && character != "\t" {
                return false
            }
        }
        return count >= 3
    }

    static func listMarker(_ line: String) -> ListMarker? {
        let indent = indentWidth(line)
        guard indent <= 3 else { return nil }
        let rest = dropIndent(line, upTo: indent)
        guard let first = rest.first else { return nil }
        let ordered: Bool
        let symbol: Character
        let number: Int
        let markerWidth: Int
        if first == "-" || first == "+" || first == "*" {
            ordered = false
            symbol = first
            number = 1
            markerWidth = 1
        } else if first.isASCII, first.isNumber {
            let digits = rest.prefix { $0.isASCII && $0.isNumber }
            guard digits.count <= 9, let delimiter = rest.dropFirst(digits.count).first,
                delimiter == "." || delimiter == ")", let value = Int(digits)
            else { return nil }
            ordered = true
            symbol = delimiter
            number = value
            markerWidth = digits.count + 1
        } else {
            return nil
        }

        let afterMarker = String(rest.dropFirst(markerWidth))
        if afterMarker.isEmpty {
            return ListMarker(
                ordered: ordered, symbol: symbol, number: number, contentIndent: indent + markerWidth + 1,
                content: "")
        }
        guard afterMarker.first == " " || afterMarker.first == "\t" else { return nil }
        let spaces = indentWidth(afterMarker)
        let content = dropIndent(afterMarker, upTo: spaces)
        if isBlank(content) {
            return ListMarker(
                ordered: ordered, symbol: symbol, number: number, contentIndent: indent + markerWidth + 1,
                content: "")
        }
        // Five or more spaces after the marker: the content starts one space in, and the rest is its indent.
        let padding = spaces > 4 ? 1 : spaces
        return ListMarker(
            ordered: ordered, symbol: symbol, number: number, contentIndent: indent + markerWidth + padding,
            content: dropIndent(afterMarker, upTo: padding))
    }

    /// A list may interrupt a paragraph only with an item that has content, and a numbered one only from 1.
    static func mayInterruptParagraph(_ marker: ListMarker) -> Bool {
        !isBlank(marker.content) && (!marker.ordered || marker.number == 1)
    }

    /// Whether a blank line separates two of an item's own blocks, which makes its list loose. A blank line between
    /// the items of a list nested in it is that list's concern, and one inside a fenced code block is code.
    static func hasBlankLineBetweenBlocks(_ lines: [String]) -> Bool {
        var inFence: (marker: Character, length: Int)?
        var sawBlank = false
        var inNestedList = false
        for line in lines {
            let indent = indentWidth(line)
            let rest = dropIndent(line, upTo: indent)
            if let open = inFence {
                if closesFence(rest, open.marker, open.length) {
                    inFence = nil
                }
                continue
            }
            if isBlank(line) {
                sawBlank = true
                continue
            }
            let atTop = indent == 0
            let isMarker = listMarker(line) != nil
            if sawBlank, atTop, !(inNestedList && isMarker) {
                return true
            }
            sawBlank = false
            if let fence = fence(rest) {
                inFence = (fence.marker, fence.length)
            }
            if atTop {
                inNestedList = isMarker
            }
        }
        return false
    }

    static func taskBox(_ line: String) -> (checked: Bool, rest: String)? {
        for (box, checked) in [("[ ]", false), ("[x]", true), ("[X]", true)] where line.hasPrefix(box) {
            let rest = line.dropFirst(box.count)
            guard rest.isEmpty || rest.first == " " || rest.first == "\t" else { return nil }
            return (checked, trimmed(String(rest)))
        }
        return nil
    }

    static func cells(_ line: String) -> [String] {
        var content = trimmed(line)
        if content.hasPrefix("|") {
            content.removeFirst()
        }
        if content.hasSuffix("|") {
            content.removeLast()
        }
        return content.split(separator: "|", omittingEmptySubsequences: false).map { trimmed(String($0)) }
    }

    static func tableDelimiter(_ line: String) -> [TableAlignment?]? {
        let text = trimmed(line)
        guard text.contains("-"), text.contains("|") || text.hasPrefix(":") || text.hasPrefix("-") else {
            return nil
        }
        var alignments: [TableAlignment?] = []
        for cell in cells(text) {
            let left = cell.hasPrefix(":")
            let right = cell.hasSuffix(":")
            let dashes = cell.dropFirst(left ? 1 : 0).dropLast(right && cell.count > 1 ? 1 : 0)
            guard !dashes.isEmpty, dashes.allSatisfy({ $0 == "-" }) else { return nil }
            alignments.append(left && right ? .center : left ? .left : right ? .right : nil)
        }
        guard !alignments.isEmpty, text.contains("|") else { return nil }
        return alignments
    }

    /// `[label]: destination`, which renders as nothing.
    static func isReferenceDefinition(_ text: String) -> Bool {
        guard text.hasPrefix("["), let close = text.firstIndex(of: "]"), close > text.index(after: text.startIndex)
        else { return false }
        let after = text[text.index(after: close)...]
        guard after.hasPrefix(":") else { return false }
        return !trimmed(String(after.dropFirst())).isEmpty
    }
}

// MARK: - Rendering blocks

/// A block with its inline text parsed.
private struct MarkdownBlockNode {
    enum Kind {
        case paragraph(InlineNode)
        case heading(Int, InlineNode)
        case thematicBreak
        case code(language: String, lines: [String])
        case quote([MarkdownBlockNode])
        case list(ordered: Bool, start: Int, tight: Bool, items: [(task: Bool?, blocks: [MarkdownBlockNode])])
        case table(alignments: [TableAlignment?], header: [InlineNode], rows: [[InlineNode]])
    }

    let kind: Kind

    init(_ block: MarkdownBlock) {
        switch block {
        case .paragraph(let lines):
            kind = .paragraph(InlineParser.parse(lines.joined(separator: "\n")))
        case .heading(let level, let text):
            kind = .heading(level, InlineParser.parse(text))
        case .thematicBreak:
            kind = .thematicBreak
        case .code(let language, let lines):
            kind = .code(language: language, lines: lines)
        case .quote(let blocks):
            kind = .quote(blocks.map(MarkdownBlockNode.init))
        case .list(let ordered, let start, let tight, let items):
            kind = .list(
                ordered: ordered, start: start, tight: tight,
                items: items.map { (task: $0.task, blocks: $0.blocks.map(MarkdownBlockNode.init)) })
        case .table(let alignments, let header, let rows):
            kind = .table(
                alignments: alignments, header: header.map(InlineParser.parse),
                rows: rows.map { $0.map(InlineParser.parse) })
        }
    }

    /// Any block but a paragraph is structure, and so is any inline element but text, a line break and a bare web
    /// address: apps link pasted addresses themselves, so an address alone is no reason to add HTML.
    var hasStructure: Bool {
        switch kind {
        case .paragraph(let inline):
            return inline.hasStructure
        case .heading, .thematicBreak, .code, .quote, .list, .table:
            return true
        }
    }

    func render(into html: inout String, tight: Bool) {
        switch kind {
        case .paragraph(let inline):
            if tight {
                inline.renderChildren(into: &html)
            } else {
                html += "<p>"
                inline.renderChildren(into: &html)
                html += "</p>\n"
            }
        case .heading(let level, let inline):
            html += "<h\(level)>"
            inline.renderChildren(into: &html)
            html += "</h\(level)>\n"
        case .thematicBreak:
            html += "<hr />\n"
        case .code(let language, let lines):
            html += language.isEmpty ? "<pre><code>" : "<pre><code class=\"language-\(HTMLText.escape(language))\">"
            for line in lines {
                html += HTMLText.escape(line) + "\n"
            }
            html += "</code></pre>\n"
        case .quote(let blocks):
            html += "<blockquote>\n"
            for block in blocks {
                block.render(into: &html, tight: false)
            }
            html += "</blockquote>\n"
        case .list(let ordered, let start, let tight, let items):
            let tag = ordered ? "ol" : "ul"
            let isTaskList = items.contains { $0.task != nil }
            html += "<\(tag)"
            if ordered, start != 1 {
                html += " start=\"\(start)\""
            }
            if isTaskList {
                html += " class=\"contains-task-list\""
            }
            html += ">\n"
            for item in items {
                html += item.task == nil ? "<li>" : "<li class=\"task-list-item\">"
                if let checked = item.task {
                    html +=
                        checked
                        ? "<input disabled=\"disabled\" type=\"checkbox\" checked=\"checked\" /> "
                        : "<input disabled=\"disabled\" type=\"checkbox\" /> "
                }
                for (position, block) in item.blocks.enumerated() {
                    if case .paragraph = block.kind, tight {
                        block.render(into: &html, tight: true)
                    } else {
                        if position > 0 || !tight {
                            html += "\n"
                        } else if !(html.last == "\n") {
                            html += "\n"
                        }
                        block.render(into: &html, tight: tight)
                    }
                }
                html += "</li>\n"
            }
            html += "</\(tag)>\n"
        case .table(let alignments, let header, let rows):
            html += "<table>\n<thead>\n<tr>\n"
            for (column, cell) in header.enumerated() {
                html += "<th\(Self.style(alignments[column]))>"
                cell.renderChildren(into: &html)
                html += "</th>\n"
            }
            html += "</tr>\n</thead>\n"
            if !rows.isEmpty {
                html += "<tbody>\n"
                for row in rows {
                    html += "<tr>\n"
                    for (column, cell) in row.enumerated() {
                        html += "<td\(Self.style(alignments[column]))>"
                        cell.renderChildren(into: &html)
                        html += "</td>\n"
                    }
                    html += "</tr>\n"
                }
                html += "</tbody>\n"
            }
            html += "</table>\n"
        }
    }

    private static func style(_ alignment: TableAlignment?) -> String {
        switch alignment {
        case .left:
            return " style=\"text-align: left;\""
        case .center:
            return " style=\"text-align: center;\""
        case .right:
            return " style=\"text-align: right;\""
        case nil:
            return ""
        }
    }
}

// MARK: - Inlines

/// One inline element, in a tree built from a linked list as CommonMark's emphasis algorithm needs.
private final class InlineNode {
    enum Kind {
        case container
        case text
        case delimiter
        case lineBreak
        case code
        case emphasis
        case strong
        case strikethrough
        case link
        /// A bare web address, linked but no reason on its own to add HTML.
        case bareLink
    }

    var kind: Kind
    var literal: String
    var url = ""
    var title: String?
    weak var parent: InlineNode?
    var firstChild: InlineNode?
    var lastChild: InlineNode?
    var next: InlineNode?
    weak var previous: InlineNode?

    init(_ kind: Kind, _ literal: String = "") {
        self.kind = kind
        self.literal = literal
    }

    var children: [InlineNode] {
        var result: [InlineNode] = []
        var node = firstChild
        while let current = node {
            result.append(current)
            node = current.next
        }
        return result
    }

    func append(_ child: InlineNode) {
        child.parent = self
        child.previous = lastChild
        child.next = nil
        if let last = lastChild {
            last.next = child
        } else {
            firstChild = child
        }
        lastChild = child
    }

    func insert(after sibling: InlineNode) {
        guard let parent = sibling.parent else { return }
        self.parent = parent
        previous = sibling
        next = sibling.next
        if let following = sibling.next {
            following.previous = self
        } else {
            parent.lastChild = self
        }
        sibling.next = self
    }

    func unlink() {
        if let previous {
            previous.next = next
        } else {
            parent?.firstChild = next
        }
        if let next {
            next.previous = previous
        } else {
            parent?.lastChild = previous
        }
        parent = nil
        previous = nil
        next = nil
    }

    /// Moves the siblings strictly between `start` and `end` into this node.
    func adoptSiblings(between start: InlineNode, and end: InlineNode) {
        var node = start.next
        while let current = node, current !== end {
            node = current.next
            current.unlink()
            append(current)
        }
    }

    var hasStructure: Bool {
        switch kind {
        case .code, .emphasis, .strong, .strikethrough, .link:
            return true
        case .container, .text, .delimiter, .lineBreak, .bareLink:
            return children.contains(where: \.hasStructure)
        }
    }

    func renderChildren(into html: inout String) {
        for child in children {
            child.render(into: &html)
        }
    }

    func render(into html: inout String) {
        switch kind {
        case .container:
            renderChildren(into: &html)
        case .text, .delimiter:
            html += HTMLText.escape(literal)
        case .lineBreak:
            html += "<br />\n"
        case .code:
            html += "<code>" + HTMLText.escape(literal) + "</code>"
        case .emphasis:
            html += "<em>"
            renderChildren(into: &html)
            html += "</em>"
        case .strong:
            html += "<strong>"
            renderChildren(into: &html)
            html += "</strong>"
        case .strikethrough:
            html += "<del>"
            renderChildren(into: &html)
            html += "</del>"
        case .link, .bareLink:
            html += "<a href=\"" + HTMLText.escapeURL(url) + "\""
            if let title {
                html += " title=\"" + HTMLText.escape(title) + "\""
            }
            html += ">"
            renderChildren(into: &html)
            html += "</a>"
        }
    }
}

/// A run of `*`, `_` or `~~` on CommonMark's delimiter stack.
private final class Delimiter {
    let node: InlineNode
    let character: Character
    let originalCount: Int
    var count: Int
    let canOpen: Bool
    let canClose: Bool
    /// Taken off the stack. Kept in place, so positions recorded in the stack stay true.
    var removed = false

    init(node: InlineNode, character: Character, count: Int, canOpen: Bool, canClose: Bool) {
        self.node = node
        self.character = character
        self.originalCount = count
        self.count = count
        self.canOpen = canOpen
        self.canClose = canClose
    }
}

private struct Bracket {
    let node: InlineNode
    /// The delimiter stack's size when the bracket was seen: emphasis inside the link text is processed above it.
    let delimiterBottom: Int
    var active = true
}

/// Inline text into a tree: code spans, links, web addresses, emphasis and line breaks. Backslashes, entities and
/// angle-bracket markup are text.
private struct InlineParser {
    private let characters: [Character]
    private var position = 0
    private let root = InlineNode(.container)
    private var delimiters: [Delimiter] = []
    private var brackets: [Bracket] = []
    private var pendingText = ""

    private init(_ text: String) {
        characters = Array(text)
    }

    static func parse(_ text: String) -> InlineNode {
        var parser = InlineParser(text)
        parser.run()
        return parser.root
    }

    private mutating func run() {
        while position < characters.count {
            let character = characters[position]
            switch character {
            case "\n":
                // Spaces before a line break are not text.
                while pendingText.hasSuffix(" ") || pendingText.hasSuffix("\t") {
                    pendingText.removeLast()
                }
                flushText()
                root.append(InlineNode(.lineBreak))
                position += 1
                while position < characters.count, characters[position] == " " || characters[position] == "\t" {
                    position += 1
                }
            case "`":
                parseCodeSpan()
            case "*", "_", "~":
                parseDelimiterRun(character)
            case "[":
                flushText()
                let node = InlineNode(.text, "[")
                root.append(node)
                brackets.append(Bracket(node: node, delimiterBottom: delimiters.count))
                position += 1
            case "]":
                parseCloseBracket()
            case "<":
                if !parseAngleAutolink() {
                    pendingText.append(character)
                    position += 1
                }
            default:
                if !parseBareLink() {
                    pendingText.append(character)
                    position += 1
                }
            }
        }
        flushText()
        processEmphasis(bottom: 0)
        mergeText(root)
    }

    private mutating func flushText() {
        guard !pendingText.isEmpty else { return }
        root.append(InlineNode(.text, pendingText))
        pendingText = ""
    }

    private mutating func parseCodeSpan() {
        let start = position
        let run = runLength(of: "`", at: start)
        var search = start + run
        while search < characters.count {
            if characters[search] == "`" {
                let closing = runLength(of: "`", at: search)
                if closing == run {
                    var content = String(characters[(start + run)..<search]).replacingOccurrences(of: "\n", with: " ")
                    if content.count >= 2, content.hasPrefix(" "), content.hasSuffix(" "),
                        content.contains(where: { $0 != " " })
                    {
                        content = String(content.dropFirst().dropLast())
                    }
                    flushText()
                    root.append(InlineNode(.code, content))
                    position = search + closing
                    return
                }
                search += closing
            } else {
                search += 1
            }
        }
        pendingText += String(repeating: "`", count: run)
        position = start + run
    }

    private func runLength(of character: Character, at index: Int) -> Int {
        var end = index
        while end < characters.count, characters[end] == character {
            end += 1
        }
        return end - index
    }

    private mutating func parseDelimiterRun(_ character: Character) {
        let start = position
        let count = runLength(of: character, at: start)
        position = start + count
        // Only a run of exactly two tildes marks strikethrough, as Markdig's extension reads it.
        if character == "~", count != 2 {
            pendingText += String(repeating: "~", count: count)
            return
        }
        let before: Character? = start > 0 ? characters[start - 1] : nil
        let after: Character? = position < characters.count ? characters[position] : nil
        let beforeIsSpace = before.map(Self.isWhitespace) ?? true
        let afterIsSpace = after.map(Self.isWhitespace) ?? true
        let beforeIsPunctuation = before.map(Self.isPunctuation) ?? false
        let afterIsPunctuation = after.map(Self.isPunctuation) ?? false
        let leftFlanking = !afterIsSpace && (!afterIsPunctuation || beforeIsSpace || beforeIsPunctuation)
        let rightFlanking = !beforeIsSpace && (!beforeIsPunctuation || afterIsSpace || afterIsPunctuation)
        let canOpen: Bool
        let canClose: Bool
        if character == "_" {
            canOpen = leftFlanking && (!rightFlanking || beforeIsPunctuation)
            canClose = rightFlanking && (!leftFlanking || afterIsPunctuation)
        } else {
            canOpen = leftFlanking
            canClose = rightFlanking
        }
        flushText()
        let node = InlineNode(.delimiter, String(repeating: String(character), count: count))
        root.append(node)
        if canOpen || canClose {
            delimiters.append(
                Delimiter(node: node, character: character, count: count, canOpen: canOpen, canClose: canClose))
        }
    }

    private mutating func parseCloseBracket() {
        flushText()
        position += 1
        guard let opener = brackets.popLast() else {
            root.append(InlineNode(.text, "]"))
            return
        }
        guard opener.active, let destination = parseLinkDestination() else {
            root.append(InlineNode(.text, "]"))
            return
        }
        guard Self.isSafe(destination.url) else {
            // An address an app could run rather than open: the characters stay text, and nothing links.
            root.append(InlineNode(.text, "](" + destination.source + ")"))
            return
        }

        // Emphasis inside the link text is settled before the text becomes the link's.
        processEmphasis(bottom: opener.delimiterBottom)
        let link = InlineNode(.link)
        link.url = destination.url
        link.title = destination.title
        let end = InlineNode(.text)
        root.append(end)
        link.adoptSiblings(between: opener.node, and: end)
        link.insert(after: opener.node)
        opener.node.unlink()
        end.unlink()
        // No link inside a link: an address inside the text is text there, and every earlier bracket is text.
        Self.unlinkAddresses(in: link)
        for index in brackets.indices {
            brackets[index].active = false
        }
    }

    private static func unlinkAddresses(in node: InlineNode) {
        for child in node.children {
            if child.kind == .bareLink || child.kind == .link {
                child.kind = .container
            }
            unlinkAddresses(in: child)
        }
    }

    /// `(destination "title")` right after a `]`, or nil, leaving the position where it was.
    private mutating func parseLinkDestination() -> (url: String, title: String?, source: String)? {
        guard position < characters.count, characters[position] == "(" else { return nil }
        var index = position + 1
        func skipSpaces() {
            var newlines = 0
            while index < characters.count,
                characters[index] == " " || characters[index] == "\t"
                    || (characters[index] == "\n" && newlines == 0)
            {
                if characters[index] == "\n" { newlines += 1 }
                index += 1
            }
        }
        skipSpaces()
        var url = ""
        if index < characters.count, characters[index] == "<" {
            index += 1
            while index < characters.count, characters[index] != ">", characters[index] != "\n",
                characters[index] != "<"
            {
                url.append(characters[index])
                index += 1
            }
            guard index < characters.count, characters[index] == ">" else { return nil }
            index += 1
        } else {
            var depth = 0
            while index < characters.count {
                let character = characters[index]
                if character == " " || character == "\t" || character == "\n" {
                    break
                }
                if character == "(" {
                    depth += 1
                } else if character == ")" {
                    if depth == 0 { break }
                    depth -= 1
                }
                url.append(character)
                index += 1
            }
            guard depth == 0 else { return nil }
        }
        let afterURL = index
        skipSpaces()
        var title: String?
        if index < characters.count, index > afterURL, "\"'(".contains(characters[index]) {
            let open = characters[index]
            let close: Character = open == "(" ? ")" : open
            var value = ""
            var cursor = index + 1
            while cursor < characters.count, characters[cursor] != close {
                value.append(characters[cursor])
                cursor += 1
            }
            guard cursor < characters.count else { return nil }
            title = value
            index = cursor + 1
            skipSpaces()
        }
        guard index < characters.count, characters[index] == ")" else { return nil }
        let source = String(characters[(position + 1)..<index])
        position = index + 1
        return (url, title, source)
    }

    /// `<scheme:address>` or `<name@example.com>`, linked.
    private mutating func parseAngleAutolink() -> Bool {
        guard let close = characters[position...].firstIndex(of: ">") else { return false }
        let inner = String(characters[(position + 1)..<close])
        guard !inner.isEmpty, !inner.contains(where: { $0.isWhitespace || $0 == "<" }) else { return false }
        let url: String
        if let colon = inner.firstIndex(of: ":"), Self.isScheme(inner[..<colon]) {
            url = inner
        } else if Self.isEmailAddress(inner) {
            url = "mailto:" + inner
        } else {
            return false
        }
        guard Self.isSafe(url) else { return false }
        flushText()
        let link = InlineNode(.link)
        link.url = url
        link.append(InlineNode(.text, inner))
        root.append(link)
        position = close + 1
        return true
    }

    /// A web address written out (`https://`, `http://`, `ftp://`, `mailto:` or `www.`), after a space, an opening
    /// parenthesis or an emphasis mark, as Markdig's extension finds them. Trailing punctuation is not part of it.
    private mutating func parseBareLink() -> Bool {
        let previous: Character? = position > 0 ? characters[position - 1] : nil
        if let previous, !(previous.isWhitespace || "*_~(".contains(previous)) {
            return false
        }
        let prefixes = ["https://", "http://", "ftp://", "mailto:", "www."]
        guard
            let prefix = prefixes.first(where: { prefix in
                position + prefix.count < characters.count
                    && String(characters[position..<(position + prefix.count)]).lowercased() == prefix
            })
        else { return false }

        // Inside link text, the closing bracket ends the address: the link around it is what the brackets mean.
        var end = position + prefix.count
        while end < characters.count, !characters[end].isWhitespace, characters[end] != "<",
            brackets.isEmpty || characters[end] != "]"
        {
            end += 1
        }
        // Trailing punctuation, and a closing parenthesis without its opening one, end the address.
        while end > position + prefix.count {
            let last = characters[end - 1]
            if "?!.,:;*_~'\"".contains(last) {
                end -= 1
            } else if last == ")" {
                let address = characters[position..<end]
                let opened = address.filter { $0 == "(" }.count
                let closed = address.filter { $0 == ")" }.count
                if closed > opened {
                    end -= 1
                } else {
                    break
                }
            } else {
                break
            }
        }
        let address = String(characters[position..<end])
        let body = address.dropFirst(prefix.count)
        guard let first = body.first, first.isLetter || first.isNumber else { return false }
        if prefix == "mailto:", !body.contains("@") {
            return false
        }
        if prefix == "www.", !body.contains(".") {
            return false
        }
        flushText()
        let link = InlineNode(.bareLink)
        link.url = prefix == "www." ? "http://" + address : address
        link.append(InlineNode(.text, address))
        root.append(link)
        position = end
        return true
    }

    /// CommonMark's "process emphasis", over the delimiters above `bottom`, which it then takes off the stack.
    private mutating func processEmphasis(bottom: Int) {
        // Per kind of closer, the lowest position worth searching again after a search found no opener.
        var openersBottom: [String: Int] = [:]
        var current = bottom
        while current < delimiters.count {
            let closer = delimiters[current]
            guard !closer.removed, closer.canClose, closer.count > 0 else {
                current += 1
                continue
            }
            let key = "\(closer.character)\(closer.canOpen)\(closer.originalCount % 3)"
            var openerIndex: Int?
            var search = current - 1
            let floor = max(bottom, openersBottom[key] ?? bottom)
            while search >= floor {
                let opener = delimiters[search]
                if !opener.removed, opener.count > 0, opener.character == closer.character, opener.canOpen {
                    // The rule of three: a run that can both open and close matches another only when their
                    // lengths together are not a multiple of three, unless both are.
                    let ruleOfThree =
                        (opener.canClose || closer.canOpen)
                        && (opener.originalCount + closer.originalCount) % 3 == 0
                        && !(opener.originalCount % 3 == 0 && closer.originalCount % 3 == 0)
                    if !ruleOfThree {
                        openerIndex = search
                        break
                    }
                }
                search -= 1
            }

            guard let openerIndex else {
                openersBottom[key] = current
                if !closer.canOpen {
                    closer.removed = true
                }
                current += 1
                continue
            }

            let opener = delimiters[openerIndex]
            let used = closer.character == "~" ? 2 : (opener.count >= 2 && closer.count >= 2 ? 2 : 1)
            opener.count -= used
            closer.count -= used
            opener.node.literal = String(opener.node.literal.dropLast(used))
            closer.node.literal = String(closer.node.literal.dropLast(used))

            let kind: InlineNode.Kind = closer.character == "~" ? .strikethrough : used == 2 ? .strong : .emphasis
            let wrapper = InlineNode(kind)
            wrapper.adoptSiblings(between: opener.node, and: closer.node)
            wrapper.insert(after: opener.node)

            // Delimiters between the two are inside the wrapper now, and match nothing outside it.
            for index in (openerIndex + 1)..<current {
                delimiters[index].removed = true
            }
            if opener.count == 0 {
                opener.node.unlink()
                opener.removed = true
            }
            if closer.count == 0 {
                closer.node.unlink()
                closer.removed = true
                current += 1
            }
        }
        if delimiters.count > bottom {
            delimiters.removeSubrange(bottom...)
        }
    }

    /// Joins neighboring text, so a delimiter that matched nothing reads as the characters it was.
    private func mergeText(_ node: InlineNode) {
        var child = node.firstChild
        while let current = child {
            if current.kind == .delimiter {
                current.kind = .text
            }
            if current.kind == .text, let next = current.next, next.kind == .text || next.kind == .delimiter {
                current.literal += next.literal
                next.unlink()
                continue
            }
            mergeText(current)
            child = current.next
        }
    }

    private static func isWhitespace(_ character: Character) -> Bool {
        character.isWhitespace
    }

    private static func isPunctuation(_ character: Character) -> Bool {
        character.isPunctuation || character.isSymbol
    }

    private static func isScheme(_ text: Substring) -> Bool {
        guard (2...32).contains(text.count), let first = text.first, first.isASCII, first.isLetter else {
            return false
        }
        return text.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || "+.-".contains($0)) }
    }

    private static func isEmailAddress(_ text: String) -> Bool {
        let parts = text.split(separator: "@", omittingEmptySubsequences: false)
        guard parts.count == 2, !parts[0].isEmpty, parts[1].contains("."), !parts[1].hasPrefix("."),
            !parts[1].hasSuffix(".")
        else { return false }
        return text.allSatisfy { $0.isASCII && !$0.isWhitespace }
    }

    /// Addresses an app could run rather than open never become a link in pasted HTML.
    static func isSafe(_ url: String) -> Bool {
        let scheme = url.prefix { $0 != ":" }.lowercased()
        guard scheme.count < url.count else { return true }
        return !["javascript", "vbscript", "data", "file"].contains(
            scheme.trimmingCharacters(in: .whitespaces))
    }
}

/// HTML escaping for text, attribute values and link addresses.
private enum HTMLText {
    static func escape(_ text: String) -> String {
        var escaped = ""
        escaped.reserveCapacity(text.count)
        for character in text {
            switch character {
            case "&": escaped += "&amp;"
            case "<": escaped += "&lt;"
            case ">": escaped += "&gt;"
            case "\"": escaped += "&quot;"
            default: escaped.append(character)
            }
        }
        return escaped
    }

    /// Percent-encodes what an address cannot carry as it is (spaces, controls, characters outside ASCII), keeps an
    /// existing percent escape, then escapes it for an attribute.
    static func escapeURL(_ url: String) -> String {
        let allowed = CharacterSet(
            charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~:/?#[]@!$&'()*+,;=%")
        var encoded = ""
        for scalar in url.unicodeScalars {
            if allowed.contains(scalar) {
                encoded.unicodeScalars.append(scalar)
            } else {
                for byte in String(scalar).utf8 {
                    encoded += String(format: "%%%02X", byte)
                }
            }
        }
        return escape(encoded)
    }
}
