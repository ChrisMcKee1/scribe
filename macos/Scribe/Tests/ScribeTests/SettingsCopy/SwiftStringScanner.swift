import Foundation

/// A string literal found in a Swift source file.
struct SwiftLiteral: Equatable {
    let text: String
    let line: Int
    /// True for text that never reaches a person: a log call, a failure message or a regular expression.
    let exempt: Bool

    /// True for text that reads like words a person sees. As on Windows, a single token is a key, a path or a code
    /// (not text) when it has no capital letter or carries the punctuation of one; anything with a space is text.
    var looksLikeText: Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.contains(where: { $0.isLetter }) else { return false }
        if trimmed.contains(where: { $0.isWhitespace }) { return true }
        let keyPunctuation = CharacterSet(charactersIn: ".-_<>/\\$:@=#")
        return trimmed.contains(where: { $0.isUppercase })
            && trimmed.unicodeScalars.allSatisfy { !keyPunctuation.contains($0) }
    }
}

/// A small lexer for Swift string literals: it skips comments, reads plain, multi-line and raw strings, drops
/// interpolations, and notes whether a literal sits inside a call whose text never reaches a person.
enum SwiftStringScanner {
    private static let exemptCalls = [
        "ScribeLog.", "fatalError(", "assertionFailure(", "preconditionFailure(", "precondition(", "assert(",
        "NSRegularExpression(", "Regex(", "Logger(", "os_log(", "NSLog(", "CopyItem.", "CopyOmission(",
    ]

    static func literals(in source: String) -> [SwiftLiteral] {
        let chars = Array(source)
        var found: [SwiftLiteral] = []
        var code = ""
        var line = 1
        var index = 0

        func next(_ offset: Int) -> Character? {
            index + offset < chars.count ? chars[index + offset] : nil
        }

        while index < chars.count {
            let char = chars[index]
            if char == "\n" || char == "\r\n" {
                line += 1
                code.append("\n")
                index += 1
            } else if char == "/" && next(1) == "/" {
                while index < chars.count && chars[index] != "\n" && chars[index] != "\r\n" {
                    index += 1
                }
            } else if char == "/" && next(1) == "*" {
                var depth = 0
                while index < chars.count {
                    if chars[index] == "/" && next(1) == "*" {
                        depth += 1
                        index += 2
                    } else if chars[index] == "*" && next(1) == "/" {
                        depth -= 1
                        index += 2
                        if depth == 0 { break }
                    } else {
                        if chars[index] == "\n" || chars[index] == "\r\n" { line += 1 }
                        index += 1
                    }
                }
            } else if char == "#" || char == "\"" {
                var hashes = 0
                var quote = index
                while quote < chars.count && chars[quote] == "#" {
                    hashes += 1
                    quote += 1
                }
                if quote < chars.count && chars[quote] == "\"" {
                    let startLine = line
                    let exempt = isInsideExemptCall(code)
                    let (text, end, lines, nested) = readString(chars, quoteIndex: quote, hashes: hashes)
                    found.append(SwiftLiteral(text: text, line: startLine, exempt: exempt))
                    for inner in nested {
                        found.append(SwiftLiteral(text: inner, line: startLine, exempt: exempt))
                    }
                    line += lines
                    index = end
                    code.append("\"S\"")
                } else {
                    code.append(char)
                    index += 1
                }
            } else {
                code.append(char)
                index += 1
            }
            if code.count > 4_000 {
                code = String(code.suffix(2_000))
            }
        }
        return found
    }

    private static func isInsideExemptCall(_ code: String) -> Bool {
        for call in exemptCalls {
            guard let range = code.range(of: call, options: .backwards) else { continue }
            let tail = code[range.lowerBound...]
            let opened = tail.filter { $0 == "(" }.count
            let closed = tail.filter { $0 == ")" }.count
            if opened > closed {
                return true
            }
        }
        return false
    }

    /// Reads the string whose opening quote is at `quoteIndex`; returns its text, the index after it and the
    /// number of line breaks it spans.
    private static func readString(
        _ chars: [Character], quoteIndex: Int, hashes: Int
    ) -> (String, Int, Int, [String]) {
        let multiline =
            quoteIndex + 2 < chars.count && chars[quoteIndex + 1] == "\"" && chars[quoteIndex + 2] == "\""
        let quotes = multiline ? 3 : 1
        var index = quoteIndex + quotes
        var text = ""
        var lines = 0
        var nested: [String] = []

        func closesHere(_ at: Int) -> Bool {
            guard at + quotes + hashes <= chars.count else { return false }
            for offset in 0..<quotes where chars[at + offset] != "\"" {
                return false
            }
            for offset in 0..<hashes where chars[at + quotes + offset] != "#" {
                return false
            }
            return true
        }

        func escapeHere(_ at: Int) -> Bool {
            guard chars[at] == "\\", at + hashes < chars.count else { return false }
            for offset in 0..<hashes where chars[at + 1 + offset] != "#" {
                return false
            }
            return true
        }

        while index < chars.count {
            if closesHere(index) {
                return (text, index + quotes + hashes, lines, nested)
            }
            let char = chars[index]
            if escapeHere(index) {
                let after = index + 1 + hashes
                guard after < chars.count else { break }
                let marker = chars[after]
                if marker == "(" {
                    var depth = 0
                    var cursor = after
                    while cursor < chars.count {
                        if chars[cursor] == "(" {
                            depth += 1
                        } else if chars[cursor] == ")" {
                            depth -= 1
                            if depth == 0 { break }
                        } else if chars[cursor] == "\"" {
                            cursor += 1
                            while cursor < chars.count && chars[cursor] != "\"" {
                                if chars[cursor] == "\\" { cursor += 1 }
                                cursor += 1
                            }
                        }
                        cursor += 1
                    }
                    if cursor > after + 1 {
                        let inner = String(chars[(after + 1)..<min(cursor, chars.count)])
                        nested.append(contentsOf: literals(in: inner).map(\.text))
                    }
                    index = cursor + 1
                } else if marker == "u", after + 1 < chars.count, chars[after + 1] == "{" {
                    var cursor = after + 2
                    var hex = ""
                    while cursor < chars.count && chars[cursor] != "}" {
                        hex.append(chars[cursor])
                        cursor += 1
                    }
                    if let value = UInt32(hex, radix: 16), let scalar = Unicode.Scalar(value) {
                        text.append(Character(scalar))
                    }
                    index = cursor + 1
                } else {
                    if marker == "n" || marker == "t" || marker == "r" {
                        text.append(" ")
                    } else if marker != "0" {
                        text.append(marker)
                    }
                    index = after + 1
                }
            } else {
                if char == "\n" || char == "\r\n" { lines += 1 }
                if !multiline && (char == "\n" || char == "\r\n") { break }
                text.append(char)
                index += 1
            }
        }
        return (text, index, lines, nested)
    }
}
