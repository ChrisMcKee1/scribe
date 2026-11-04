import Foundation

/// Reads Swift source's string literals the way the compiler delimits them: plain, multi-line and raw strings (with
/// any number of `#`), with comments skipped and interpolations, which are code, left out of the text. A literal
/// inside an interpolation is read as a text of its own, since the interpolation shows it. Deliberately small: it
/// understands exactly enough Swift to find text, nothing more. Windows' counterpart is
/// `GlossarySourceTests.SourceLiterals` in `tests/Scribe.Core.Tests`.
enum SwiftSourceLiterals {
    struct Literal: Equatable, Sendable {
        let line: Int
        let text: String
    }

    /// A span of the source, in Unicode scalars from its start.
    typealias Span = Range<Int>

    private struct Token {
        let isComment: Bool
        let start: Int
        let end: Int
        let text: String
        var nested = false
    }

    /// The source with every comment and literal blanked out (line breaks kept) and every other non-ASCII scalar
    /// replaced by one ASCII character, so a regular expression's UTF-16 offsets are scalar offsets.
    private struct Code {
        let scalars: [Unicode.Scalar]
        let tokens: [Token]
        let bytes: [UInt8]
        let text: String

        init(_ source: String) {
            let scalars = Array(source.unicodeScalars)
            let tokens = SwiftSourceLiterals.tokenize(scalars)
            var bytes = scalars.map { $0.isASCII ? UInt8($0.value) : UInt8(ascii: "_") }
            for token in tokens where !token.nested {
                for index in token.start..<min(token.end, bytes.count) where bytes[index] != UInt8(ascii: "\n") {
                    bytes[index] = UInt8(ascii: " ")
                }
            }
            self.scalars = scalars
            self.tokens = tokens
            self.bytes = bytes
            text = String(decoding: bytes, as: UTF8.self)
        }

        func matches(_ pattern: NSRegularExpression) -> [NSTextCheckingResult] {
            pattern.matches(in: text, range: NSRange(location: 0, length: bytes.count))
        }
    }

    /// The literals outside comments, outside the calls `hiddenCall` finds and outside `excluded`. Literals joined
    /// with `+` are one text, so a word split across them is still read; a variable or a call between them ends it.
    static func read(_ source: String, hiddenCall: NSRegularExpression, excluded: [Span] = []) -> [Literal] {
        let code = Code(source)
        var hidden = excluded
        for call in code.matches(hiddenCall) {
            let open = call.range.location + call.range.length - 1
            hidden.append(call.range.location..<matchingClose(code.bytes, open))
        }

        var lineStarts = [0]
        for (index, scalar) in code.scalars.enumerated() where scalar == "\n" {
            lineStarts.append(index + 1)
        }
        func line(of offset: Int) -> Int {
            var low = 0
            var high = lineStarts.count
            while high - low > 1 {
                let middle = (low + high) / 2
                if lineStarts[middle] <= offset {
                    low = middle
                } else {
                    high = middle
                }
            }
            return low + 1
        }

        var literals: [Literal] = []
        // A literal inside an interpolation is joined only with others inside interpolations: an outer literal and
        // one in its interpolation never touch.
        for nestedPieces in [false, true] {
            let pieces = code.tokens
                .filter { !$0.isComment && $0.nested == nestedPieces }
                .sorted { $0.start < $1.start }
            var index = 0
            while index < pieces.count {
                let first = pieces[index]
                var text = first.text
                while index + 1 < pieces.count && joins(code.scalars, pieces[index].end, pieces[index + 1].start) {
                    index += 1
                    text += pieces[index].text
                }
                if !hidden.contains(where: { $0.contains(first.start) }) {
                    literals.append(Literal(line: line(of: first.start), text: text))
                }
                index += 1
            }
        }
        return literals
    }

    /// The spans of the declarations named `name`: a type's (`enum`, `struct`, `class`, `actor` or `extension`) to
    /// its closing brace, and a property's (`let` or `var`) to the first literal of its initializer, so the text that
    /// literal begins is excluded with it.
    static func declarationSpans(in source: String, named name: String) -> [Span] {
        let code = Code(source)
        let escaped = NSRegularExpression.escapedPattern(for: name)
        guard
            let pattern = try? NSRegularExpression(
                pattern: #"\b(enum|struct|class|actor|extension|let|var)\s+"# + escaped + #"\b"#)
        else {
            return []
        }

        var spans: [Span] = []
        for match in code.matches(pattern) {
            let start = match.range.location
            let after = start + match.range.length
            let keyword = Range(match.range(at: 1), in: code.text).map { String(code.text[$0]) }
            if keyword == "let" || keyword == "var" {
                if let literal = code.tokens.first(where: { !$0.isComment && !$0.nested && $0.start >= after }) {
                    spans.append(start..<(literal.start + 1))
                }
            } else if let brace = code.bytes[after...].firstIndex(of: UInt8(ascii: "{")) {
                spans.append(start..<matchingClose(code.bytes, brace))
            }
        }
        return spans
    }

    /// The spans of the array literals that hold string literals and nothing else, such as a list of search
    /// keywords.
    static func stringArraySpans(in source: String) -> [Span] {
        let code = Code(source)
        guard let pattern = try? NSRegularExpression(pattern: #"\[[\s,]*\]"#) else {
            return []
        }
        return code.matches(pattern)
            .map { $0.range.location..<($0.range.location + $0.range.length) }
            .filter { span in code.tokens.contains { !$0.isComment && !$0.nested && span.contains($0.start) } }
    }

    // Only white space and one plus sign between two literals: the second continues the first.
    private static func joins(_ scalars: [Unicode.Scalar], _ end: Int, _ start: Int) -> Bool {
        guard start >= end else {
            return false
        }
        var plusSigns = 0
        for scalar in scalars[end..<start] {
            if scalar == "+" {
                plusSigns += 1
            } else if !scalar.properties.isWhitespace {
                return false
            }
        }
        return plusSigns == 1
    }

    private static func matchingClose(_ bytes: [UInt8], _ open: Int) -> Int {
        var depth = 0
        var index = open
        while index < bytes.count {
            switch bytes[index] {
            case UInt8(ascii: "("), UInt8(ascii: "["), UInt8(ascii: "{"):
                depth += 1
            case UInt8(ascii: ")"), UInt8(ascii: "]"), UInt8(ascii: "}"):
                depth -= 1
                if depth == 0 {
                    return index + 1
                }
            default:
                break
            }
            index += 1
        }
        return bytes.count
    }

    private static func tokenize(_ scalars: [Unicode.Scalar]) -> [Token] {
        var tokens: [Token] = []
        var index = 0
        while index < scalars.count {
            var nested: [Token] = []
            if let token = readToken(scalars, at: index, nested: &nested) {
                tokens.append(token)
                for var inner in nested {
                    inner.nested = true
                    tokens.append(inner)
                }
                index = max(token.end, index + 1)
            } else {
                index += 1
            }
        }
        return tokens
    }

    private static func readToken(_ scalars: [Unicode.Scalar], at start: Int, nested: inout [Token]) -> Token? {
        let count = scalars.count
        func scalar(_ offset: Int) -> Unicode.Scalar? {
            offset < count ? scalars[offset] : nil
        }

        if scalars[start] == "/" && scalar(start + 1) == "/" {
            var end = start
            while end < count && scalars[end] != "\n" {
                end += 1
            }
            return Token(isComment: true, start: start, end: end, text: "")
        }

        if scalars[start] == "/" && scalar(start + 1) == "*" {
            // Swift's block comments nest.
            var depth = 0
            var end = start
            while end < count {
                if scalars[end] == "/" && scalar(end + 1) == "*" {
                    depth += 1
                    end += 2
                } else if scalars[end] == "*" && scalar(end + 1) == "/" {
                    depth -= 1
                    end += 2
                    if depth == 0 {
                        break
                    }
                } else {
                    end += 1
                }
            }
            return Token(isComment: true, start: start, end: end, text: "")
        }

        var quote = start
        while quote < count && scalars[quote] == "#" {
            quote += 1
        }
        guard quote < count, scalars[quote] == "\"" else {
            return nil
        }

        let hashes = quote - start
        let quotes = scalar(quote + 1) == "\"" && scalar(quote + 2) == "\"" ? 3 : 1
        func closes(_ offset: Int) -> Bool {
            guard offset + quotes + hashes <= count else {
                return false
            }
            return (0..<quotes).allSatisfy { scalars[offset + $0] == "\"" }
                && (0..<hashes).allSatisfy { scalars[offset + quotes + $0] == "#" }
        }
        func escapes(_ offset: Int) -> Bool {
            scalars[offset] == "\\" && offset + hashes < count
                && (0..<hashes).allSatisfy { scalars[offset + 1 + $0] == "#" }
        }

        var text = String.UnicodeScalarView()
        var index = quote + quotes
        while index < count {
            if closes(index) {
                return Token(isComment: false, start: start, end: index + quotes + hashes, text: String(text))
            }
            if escapes(index) {
                let marker = index + 1 + hashes
                guard marker < count else {
                    break
                }
                switch scalars[marker] {
                case "(":
                    index = skipInterpolation(scalars, from: marker + 1, nested: &nested)
                    text.append(" ")
                case "u" where scalar(marker + 1) == "{":
                    var end = marker + 2
                    var hex = ""
                    while end < count && scalars[end] != "}" {
                        hex.unicodeScalars.append(scalars[end])
                        end += 1
                    }
                    if let value = UInt32(hex, radix: 16), let decoded = Unicode.Scalar(value) {
                        text.append(decoded)
                    }
                    index = end + 1
                case "n", "t", "r", "0":
                    text.append(" ")
                    index = marker + 1
                case "\n":
                    // A multi-line string's line that ends in a backslash continues on the next.
                    index = marker + 1
                default:
                    text.append(scalars[marker])
                    index = marker + 1
                }
                continue
            }
            if quotes == 1 && scalars[index] == "\n" {
                break
            }
            text.append(scalars[index])
            index += 1
        }
        return Token(isComment: false, start: start, end: index, text: String(text))
    }

    // From just inside an interpolation's parenthesis to just past its closing one, nested parentheses skipped and
    // nested literals collected: `\(ready ? "Ready" : "Not ready")` shows its literals.
    private static func skipInterpolation(_ scalars: [Unicode.Scalar], from start: Int, nested: inout [Token]) -> Int {
        var depth = 0
        var index = start
        while index < scalars.count {
            if scalars[index] == "\"" || scalars[index] == "#" {
                var inner: [Token] = []
                if let literal = readToken(scalars, at: index, nested: &inner), !literal.isComment {
                    nested.append(literal)
                    nested.append(contentsOf: inner)
                    index = max(literal.end, index + 1)
                    continue
                }
            }
            if scalars[index] == "(" {
                depth += 1
            } else if scalars[index] == ")" {
                if depth == 0 {
                    return index + 1
                }
                depth -= 1
            }
            index += 1
        }
        return scalars.count
    }
}
