import Foundation

/// A dictionary or snippet substitution, kind-tagged (mirrors Windows' `TextReplacementKind`).
enum TextReplacementKind {
    case dictionary
    case snippet
}

/// One substitution span in the final processed text (mirrors Windows' `TextReplacement`), used by
/// the Playground to underline what changed and by what rule.
struct TextReplacement: Equatable {
    let start: Int
    let length: Int
    let pattern: String
    let replacement: String
    let kind: TextReplacementKind

    static func == (lhs: TextReplacement, rhs: TextReplacement) -> Bool {
        lhs.start == rhs.start && lhs.length == rhs.length && lhs.pattern == rhs.pattern
            && lhs.replacement == rhs.replacement && lhs.kind == rhs.kind
    }
}

extension TextReplacementKind: Equatable {}

/// Final text plus every substitution span within it, for Playground highlighting.
struct TextPostProcessingResult {
    let text: String
    let replacements: [TextReplacement]
}

/// Applies user snippets, then dictionary substitutions, to a raw transcript. Ports the shape of
/// Windows' `TextPostProcessor` (see src/Scribe.Core/PostProcessing/TextPostProcessor.cs): snippets
/// expand first so their expanded templates then benefit from dictionary canonicalization, each
/// phase matches its original input once (no re-scanning generated text within the same phase), and
/// matching is whole-word aware and case-insensitive by default.
///
/// This macOS port keeps the essential ordering, whole-word semantics, and the "replacement
/// contains pattern" double-expansion guard from Windows' `CompiledRule`; it does not yet implement
/// the AI-cleanup glossary source-text pass (see PORTING-PLAN.md).
final class TextPostProcessor {
    private var dictionaryEntries: [DictionaryEntry] = []
    private var snippets: [Snippet] = []

    /// - Parameter libraryEntries: enabled entries from any switched-on dictionary libraries
    ///   (see `DictionaryLibraryService`), already filtered to `enabled`. Merged behind the base
    ///   dictionary so a user's own entry always wins over a library's for the same spoken form.
    ///   Mirrors Windows' `DictionaryLibraryComposer.Merge` usage in `TextPostProcessor.Reload`.
    func reload(dictionaryEntries: [DictionaryEntry], snippets: [Snippet], libraryEntries: [DictionaryEntry] = []) {
        let base = dictionaryEntries.filter { $0.enabled && !$0.pattern.isEmpty }
        self.dictionaryEntries = libraryEntries.isEmpty
            ? base
            : DictionaryLibraryComposer.merge(baseEntries: base, libraryEntries: libraryEntries)
        self.snippets = snippets.filter { $0.enabled && !$0.phrase.isEmpty && !$0.template.isEmpty }
    }

    func process(_ text: String) -> String {
        processDetailed(text).text
    }

    /// Same processing as `process(_:)`, but also reports every dictionary/snippet substitution as
    /// a span in the final text, for the Playground's inline highlight view.
    func processDetailed(_ text: String) -> TextPostProcessingResult {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return TextPostProcessingResult(text: "", replacements: [])
        }

        let normalized = Self.normalizeWhitespace(text)

        let (afterSnippets, snippetReplacements) = applySinglePass(
            normalized, rules: snippets.map(SnippetRule.init), kind: .snippet)
        let (finalText, dictionaryReplacements) = applySinglePass(
            afterSnippets, rules: dictionaryEntries.map(DictionaryRule.init), kind: .dictionary)

        // Snippet spans were reported against `afterSnippets`, which then had dictionary rules
        // applied on top; re-locate each snippet's (possibly dictionary-canonicalized) template by
        // searching for it in the final text, mirroring Windows' `canonicalSnippets` pass.
        var replacements = dictionaryReplacements
        var searchStart = finalText.startIndex
        for snippetReplacement in snippetReplacements {
            let (canonicalTemplate, _) = applySinglePass(
                snippetReplacement.replacement, rules: dictionaryEntries.map(DictionaryRule.init), kind: .dictionary)
            guard !canonicalTemplate.isEmpty else { continue }
            if let range = finalText.range(of: canonicalTemplate, range: searchStart..<finalText.endIndex) {
                let nsRange = NSRange(range, in: finalText)
                replacements.append(TextReplacement(
                    start: nsRange.location,
                    length: nsRange.length,
                    pattern: snippetReplacement.pattern,
                    replacement: canonicalTemplate,
                    kind: .snippet))
                searchStart = range.upperBound
            }
        }

        replacements.sort { $0.start < $1.start }
        return TextPostProcessingResult(text: finalText, replacements: replacements)
    }

    /// Runs one dictionary rule over already-finalized text, using the same normalization and
    /// matcher as the live pipeline. Exists so the quick-add popup can repair the transcript a
    /// correction came from, without a private copy of the matcher drifting from
    /// `processDetailed(_:)`'s real behavior. Only the one rule is applied, since the text has
    /// already been through every other rule. Mirrors Windows' `TextPostProcessor.ApplyRule`.
    static func applyRule(_ text: String?, entry: DictionaryEntry?) -> String {
        guard let text, !text.isEmpty, let entry, !entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return text ?? ""
        }

        let trimmedEntry = DictionaryEntry(
            id: entry.id,
            pattern: entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines),
            replacement: entry.replacement,
            wholeWord: entry.wholeWord,
            enabled: entry.enabled)

        let normalized = normalizeWhitespace(text)
        let (result, _) = TextPostProcessor().applySinglePass(normalized, rules: [DictionaryRule(entry: trimmedEntry)], kind: .dictionary)
        return result
    }

    // MARK: - Rules

    private protocol Rule {
        var order: Int { get }
        var pattern: String { get }
        func findMatches(in text: String) -> [ReplacementCandidate]
    }

    private struct ReplacementCandidate {
        let range: NSRange
        let original: String
        let replacement: String
        let order: Int
    }

    /// A located substitution reported out of `applySinglePass`, before final sorting/merging.
    private struct AppliedReplacement {
        let pattern: String
        let replacement: String
    }

    private struct DictionaryRule: Rule {
        let entry: DictionaryEntry
        let order: Int = 0
        var pattern: String { entry.pattern }
        private let regex: NSRegularExpression?
        // Only an expansion whose replacement is strictly longer than its pattern AND embeds that
        // pattern (e.g. "york" -> "New York") can double-fire: when AI cleanup is enabled the
        // glossary biases the model to emit the canonical form first, then this deterministic stage,
        // which always runs last, would expand the embedded pattern again ("New York" -> "New New
        // York"). A same-length entry is a pure casing/punctuation fix ("azure" -> "Azure"); it must
        // keep the plain fast-path replace so the fix actually applies, so the length guard matters,
        // not just an optimization. Mirrors Windows' `CompiledRule._replacementContainsPattern`.
        private let replacementContainsPattern: Bool

        init(entry: DictionaryEntry) {
            self.entry = entry
            let escaped = NSRegularExpression.escapedPattern(for: entry.pattern)
            let pattern = entry.wholeWord ? "(?<!\\w)\(escaped)(?!\\w)" : escaped
            self.regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
            self.replacementContainsPattern = !entry.pattern.isEmpty
                && entry.replacement.count > entry.pattern.count
                && entry.replacement.range(of: entry.pattern, options: .caseInsensitive) != nil
        }

        func findMatches(in text: String) -> [ReplacementCandidate] {
            guard let regex else { return [] }
            let fullRange = NSRange(text.startIndex..., in: text)
            let nsText = text as NSString
            let canonicalStarts = replacementContainsPattern ? Self.collectReplacementStarts(entry.replacement, in: nsText) : []
            return regex.matches(in: text, range: fullRange).compactMap { match in
                guard let swiftRange = Range(match.range, in: text) else { return nil }
                let original = String(text[swiftRange])
                let replacement = !canonicalStarts.isEmpty
                    && Self.isInsideAnyReplacement(canonicalStarts, matchRange: match.range, replacementLength: (entry.replacement as NSString).length)
                    ? original
                    : entry.replacement
                return ReplacementCandidate(
                    range: match.range,
                    original: original,
                    replacement: replacement,
                    order: order)
            }
        }

        // Ascending start offsets of every existing occurrence of the replacement. Case-insensitive
        // because the AI may emit a different casing than the canonical form; that casing is left
        // as-is (never corrupted into a double expansion), which is preferable to a risky span
        // rewrite. Mirrors Windows' `CollectReplacementStarts`.
        private static func collectReplacementStarts(_ replacement: String, in nsText: NSString) -> [Int] {
            var starts: [Int] = []
            var from = 0
            let replacementLength = (replacement as NSString).length
            while from <= nsText.length - replacementLength {
                let searchRange = NSRange(location: from, length: nsText.length - from)
                let found = nsText.range(of: replacement, options: [.caseInsensitive], range: searchRange)
                guard found.location != NSNotFound else { break }
                starts.append(found.location)
                from = found.location + 1 // allow overlapping occurrences
            }
            return starts
        }

        // Mirrors Windows' `IsInsideAnyReplacement`.
        private static func isInsideAnyReplacement(_ starts: [Int], matchRange: NSRange, replacementLength: Int) -> Bool {
            let matchEnd = matchRange.location + matchRange.length
            for idx in starts {
                if idx > matchRange.location {
                    break // ascending: no later occurrence can contain this match
                }
                if matchEnd <= idx + replacementLength {
                    return true
                }
            }
            return false
        }
    }

    private struct SnippetRule: Rule {
        let snippet: Snippet
        let order: Int = -1 // snippets always take priority within their own phase; irrelevant across phases
        var pattern: String { snippet.phrase }
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
    /// mirroring Windows' `ApplySinglePass`, including its "tight punctuation" guard: a replacement
    /// that is entirely comma/period/etc. absorbs the one whitespace character immediately before
    /// it, so "hello comma world" -> "hello, world" rather than "hello , world". Replacement text is
    /// never re-scanned within this same call, only by the next phase (see `processDetailed(_:)`).
    /// Also returns every located span whose replacement text actually changed the input, tagged
    /// with the rule's pattern, for Playground highlighting.
    private func applySinglePass(_ text: String, rules: [Rule], kind: TextReplacementKind) -> (String, [TextReplacement]) {
        let candidates = rules
            .flatMap { rule in rule.findMatches(in: text).map { ($0, rule.pattern) } }
            .sorted { lhs, rhs in
                if lhs.0.range.location != rhs.0.range.location {
                    return lhs.0.range.location < rhs.0.range.location
                }
                if lhs.0.range.length != rhs.0.range.length {
                    return lhs.0.range.length > rhs.0.range.length // longest match first
                }
                return lhs.0.order < rhs.0.order
            }

        guard !candidates.isEmpty else { return (text, []) }

        let nsText = text as NSString
        var result = ""
        var position = 0
        var replacements: [TextReplacement] = []

        for (candidate, pattern) in candidates {
            guard candidate.range.location >= position else { continue } // overlap, skip
            var prefixLength = candidate.range.location - position
            if prefixLength > 0,
                !candidate.replacement.isEmpty,
                candidate.replacement.allSatisfy(Self.isTightPunctuation),
                CharacterSet.whitespacesAndNewlines.contains(UnicodeScalar(nsText.character(at: candidate.range.location - 1))!)
            {
                prefixLength -= 1
            }
            let prefixRange = NSRange(location: position, length: prefixLength)
            result += nsText.substring(with: prefixRange)
            let start = (result as NSString).length
            result += candidate.replacement
            if candidate.original != candidate.replacement {
                replacements.append(TextReplacement(
                    start: start,
                    length: (candidate.replacement as NSString).length,
                    pattern: pattern,
                    replacement: candidate.replacement,
                    kind: kind))
            }
            position = candidate.range.location + candidate.range.length
        }
        result += nsText.substring(from: position)
        return (result, replacements)
    }

    private static func isTightPunctuation(_ ch: Character) -> Bool {
        ch == "," || ch == "." || ch == "!" || ch == "?" || ch == ";" || ch == ":"
    }

    /// Collapses horizontal whitespace runs to a single space, preserving line breaks. `\v` inside
    /// an ICU character class (which `NSRegularExpression` uses) expands to the full "vertical
    /// whitespace" set (`\n`, `\r`, form feed, NEL, LS, PS), unlike .NET's `Regex`, where `\v` inside
    /// a bracket means only the literal vertical-tab byte. Using `\v` here silently collapsed every
    /// CRLF/LF in the text to a single space; `\x0B` is the literal-vertical-tab escape that actually
    /// matches Windows' `NormalizeWhitespace` behavior.
    private static func normalizeWhitespace(_ text: String) -> String {
        var result = text.replacingOccurrences(of: "[ \\t\\f\\x0B]+", with: " ", options: .regularExpression)
        result = result.replacingOccurrences(of: "[ \\t]+([,.!?;:])", with: "$1", options: .regularExpression)
        return result.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

