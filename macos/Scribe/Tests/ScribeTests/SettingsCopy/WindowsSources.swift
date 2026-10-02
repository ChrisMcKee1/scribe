import Foundation

/// Reads the Windows sources in this repository so tests can hold Mac copy and Mac pages to what Windows ships.
enum WindowsSources {
    static var repositoryRoot: URL {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<6 {
            root.deleteLastPathComponent()
        }
        return root
    }

    static func read(_ relativePath: String) throws -> String {
        let url = repositoryRoot.appendingPathComponent(relativePath, isDirectory: false)
        return try String(contentsOf: url, encoding: .utf8)
    }

    /// Every C# and XAML file of the Windows app and its Core, joined and normalized (see `normalize`).
    static let corpus: String = {
        var parts: [String] = []
        for folder in ["src/Scribe.Core", "src/Scribe.App"] {
            let base = repositoryRoot.appendingPathComponent(folder, isDirectory: true)
            guard let walker = FileManager.default.enumerator(at: base, includingPropertiesForKeys: nil) else {
                continue
            }
            for case let url as URL in walker {
                let path = url.path
                guard url.pathExtension == "cs" || url.pathExtension == "xaml",
                    !path.contains("/obj/"), !path.contains("/bin/")
                else {
                    continue
                }
                if let text = try? String(contentsOf: url, encoding: .utf8) {
                    parts.append(text)
                }
            }
        }
        return normalize(parts.joined(separator: "\n"))
    }()

    /// Decodes XML entities and C# escapes, joins `"a" + "b"` literals and collapses runs of white space, so a
    /// string written across lines in the source reads as the one a person sees.
    static func normalize(_ source: String) -> String {
        var text = source
        for (entity, plain) in [("&amp;", "&"), ("&quot;", "\""), ("&lt;", "<"), ("&gt;", ">"), ("&apos;", "'")] {
            text = text.replacingOccurrences(of: entity, with: plain)
        }
        text = text.replacingOccurrences(of: "\\\"", with: "\"")
        if let join = try? NSRegularExpression(pattern: "\"\\s*\\+\\s*\\$?@?\"") {
            let range = NSRange(text.startIndex..., in: text)
            text = join.stringByReplacingMatches(in: text, range: range, withTemplate: "")
        }
        return collapse(text)
    }

    static func collapse(_ text: String) -> String {
        text.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
    }

    /// True when `windowsText` is in the Windows sources as one run, in order: its literal parts, with each
    /// `{placeholder}` standing for a `{...}` interpolation or a `" + value + "` join. A sentence that merely shares
    /// words with other strings does not pass, and neither does one with its parts reordered or its closing
    /// punctuation changed.
    static func contains(_ windowsText: String) -> Bool {
        let parts = literalParts(windowsText)
        let normalized = parts.map(squeeze)
        if normalized.count == 1 {
            return corpus.contains(normalized[0])
        }
        let gap = #"(?:\{.{0,80}?\}|"\s?\+\s?.{0,60}?\+\s?")"#
        let pattern = normalized.map { NSRegularExpression.escapedPattern(for: $0) }.joined(separator: gap)
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.dotMatchesLineSeparators]) else {
            return false
        }
        return regex.firstMatch(in: corpus, range: NSRange(corpus.startIndex..., in: corpus)) != nil
    }

    /// The text between `{...}` placeholders, empty parts kept, so `a{x}b` gives `a` and `b`.
    static func literalParts(_ text: String) -> [String] {
        var parts: [String] = []
        var current = ""
        var inside = false
        for character in text {
            if !inside && character == "{" {
                parts.append(current)
                current = ""
                inside = true
            } else if inside && character == "}" {
                inside = false
            } else if !inside {
                current.append(character)
            }
        }
        parts.append(current)
        return parts
    }

    /// Runs of white space become one space; an edge space stays, so a placeholder keeps its neighbours.
    static func squeeze(_ text: String) -> String {
        var result = ""
        var lastWasSpace = false
        for character in text {
            if character.isWhitespace {
                if !lastWasSpace { result.append(" ") }
                lastWasSpace = true
            } else {
                result.append(character)
                lastWasSpace = false
            }
        }
        return result
    }

    /// True when `text` is a whole string literal or a whole element text in the Windows sources.
    static func isWholeLiteral(_ text: String) -> Bool {
        let flat = collapse(text)
        return corpus.contains("\"" + flat + "\"") || corpus.contains(">" + flat + "<")
    }
    /// The pieces of text outside `{...}` placeholders, collapsed, that are long enough to mean something.
    static func fragments(of windowsText: String) -> [String] {
        var pieces: [String] = []
        var current = ""
        var depth = 0
        for character in windowsText {
            if character == "{" {
                depth += 1
                pieces.append(current)
                current = ""
            } else if character == "}" && depth > 0 {
                depth -= 1
            } else if depth == 0 {
                current.append(character)
            }
        }
        pieces.append(current)
        return pieces.map { collapse($0) }.filter { $0.count >= 2 }
    }
}
