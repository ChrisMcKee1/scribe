import Foundation

/// Applies user snippets, then dictionary substitutions, to a raw transcript. Ports the shape of
/// Windows' `TextPostProcessor` (see src/Scribe.Core/PostProcessing/TextPostProcessor.cs): snippets
/// expand first so their expanded templates then benefit from dictionary canonicalization, each
/// phase matches its original input once (no re-scanning generated text within the same phase), and
/// matching is whole-word aware and case-insensitive by default.
///
/// This macOS port keeps the essential ordering and whole-word semantics but is intentionally
/// simpler than Windows' `CompiledRule`: it does not yet implement the "replacement contains
/// pattern" double-expansion guard (relevant once AI cleanup glossary injection exists on macOS,
/// which it does not yet; see PORTING-PLAN.md), nor the AI-cleanup glossary source-text pass.
final class TextPostProcessor {
    private var dictionaryEntries: [DictionaryEntry] = []
    private var snippets: [Snippet] = []

    func reload(dictionaryEntries: [DictionaryEntry], snippets: [Snippet]) {
        self.dictionaryEntries = dictionaryEntries.filter { $0.enabled && !$0.pattern.isEmpty }
        self.snippets = snippets.filter { $0.enabled && !$0.phrase.isEmpty && !$0.template.isEmpty }
    }

    func process(_ text: String) -> String {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return ""
        }

        var result = Self.normalizeWhitespace(text)
        result = applySinglePass(result, rules: snippets.map(SnippetRule.init))
        result = applySinglePass(result, rules: dictionaryEntries.map(DictionaryRule.init))
        return result
    }

    // MARK: - Rules

    private protocol Rule {
        var order: Int { get }
        func findMatches(in text: String) -> [ReplacementCandidate]
    }

    private struct ReplacementCandidate {
        let range: NSRange
        let original: String
        let replacement: String
        let order: Int
    }

    private struct DictionaryRule: Rule {
        let entry: DictionaryEntry
        let order: Int = 0
        private let regex: NSRegularExpression?

        init(entry: DictionaryEntry) {
            self.entry = entry
            let escaped = NSRegularExpression.escapedPattern(for: entry.pattern)
            let pattern = entry.wholeWord ? "(?<!\\w)\(escaped)(?!\\w)" : escaped
            self.regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
        }

        func findMatches(in text: String) -> [ReplacementCandidate] {
            guard let regex else { return [] }
            let fullRange = NSRange(text.startIndex..., in: text)
            return regex.matches(in: text, range: fullRange).compactMap { match in
                guard let swiftRange = Range(match.range, in: text) else { return nil }
                return ReplacementCandidate(
                    range: match.range,
                    original: String(text[swiftRange]),
                    replacement: entry.replacement,
                    order: order)
            }
        }
    }

    private struct SnippetRule: Rule {
        let snippet: Snippet
        let order: Int = -1 // snippets always take priority within their own phase; irrelevant across phases
        private let regex: NSRegularExpression?

        init(snippet: Snippet) {
            self.snippet = snippet
            let escaped = NSRegularExpression.escapedPattern(for: snippet.phrase)
            let pattern = "(?<!\\w)\(escaped)(?!\\w)"
            self.regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
        }

        func findMatches(in text: String) -> [ReplacementCandidate] {
            guard let regex else { return [] }
            let fullRange = NSRange(text.startIndex..., in: text)
            return regex.matches(in: text, range: fullRange).compactMap { match in
                guard let swiftRange = Range(match.range, in: text) else { return nil }
                return ReplacementCandidate(
                    range: match.range,
                    original: String(text[swiftRange]),
                    replacement: snippet.template,
                    order: order)
            }
        }
    }

    /// Applies every rule's matches against the *original* input in one pass: matches are sorted by
    /// position (then longest-first, then rule order) and non-overlapping matches are spliced in,
    /// mirroring Windows' `ApplySinglePass`. Replacement text is never re-scanned within this same
    /// call, only by the next phase (see `process(_:)`).
    private func applySinglePass(_ text: String, rules: [Rule]) -> String {
        let candidates = rules
            .flatMap { $0.findMatches(in: text) }
            .sorted { lhs, rhs in
                if lhs.range.location != rhs.range.location {
                    return lhs.range.location < rhs.range.location
                }
                if lhs.range.length != rhs.range.length {
                    return lhs.range.length > rhs.range.length // longest match first
                }
                return lhs.order < rhs.order
            }

        guard !candidates.isEmpty else { return text }

        let nsText = text as NSString
        var result = ""
        var position = 0

        for candidate in candidates {
            guard candidate.range.location >= position else { continue } // overlap, skip
            let prefixRange = NSRange(location: position, length: candidate.range.location - position)
            result += nsText.substring(with: prefixRange)
            result += candidate.replacement
            position = candidate.range.location + candidate.range.length
        }
        result += nsText.substring(from: position)
        return result
    }

    private static func normalizeWhitespace(_ text: String) -> String {
        var result = text.replacingOccurrences(of: "[ \\t\\f\\v]+", with: " ", options: .regularExpression)
        result = result.replacingOccurrences(of: "[ \\t]+([,.!?;:])", with: "$1", options: .regularExpression)
        return result.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
