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

    /// True when every literal fragment of `windowsText` (the parts between `{placeholders}`) is in the corpus.
    static func contains(_ windowsText: String) -> Bool {
        let fragments = fragments(of: windowsText)
        return !fragments.isEmpty && fragments.allSatisfy { corpus.contains($0) }
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
