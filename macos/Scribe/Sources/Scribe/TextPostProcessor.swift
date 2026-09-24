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

/// The vocabulary step of one dictation with AI cleanup on (`TextPostProcessor.correctVocabulary`): the text the
/// provider is sent, what the vocabulary rules changed in it, and the rules as they stood then. The step after cleanup
/// runs with those same rules, so a rule edited while the request was out still runs exactly once.
struct VocabularyPass {
    let result: TextPostProcessingResult
    let rules: TextPostProcessor.CompiledRules

    /// What the cleanup provider is sent.
    var text: String {
        result.text
    }
}

/// Applies user snippets, then dictionary substitutions, to a raw transcript. Ports the shape of
/// Windows' `TextPostProcessor` (see src/Scribe.Core/PostProcessing/TextPostProcessor.cs): snippets
/// expand first so their expanded templates then benefit from dictionary canonicalization, each
/// phase matches its original input once (no re-scanning generated text within the same phase), and
/// matching is whole-word aware and case-insensitive by default.
///
/// This macOS port keeps the essential ordering, whole-word semantics, and the "replacement
/// contains pattern" double-expansion guard from Windows' `CompiledRule`. It has no glossary for AI
/// cleanup; with cleanup on, the rules are split around the request instead: the vocabulary rules
/// (`isVocabulary`) run on the raw transcript, whose corrected text is what the provider is sent
/// (`correctVocabulary`), and the snippets and the template-like rules run on the accepted reply
/// (`finishAfterCleanup`). Every rule runs once, and no snippet template or template-like
/// replacement ever reaches a provider.
final class TextPostProcessor {
    /// Windows' per-term cap on what its cleanup glossary carries (`CleanupPrompt.MaxGlossaryTermChars`), counted
    /// in UTF-16 units as .NET counts a string.
    static let vocabularyReplacementLimit = 100

    // Compiled once in `reload`, not per dictation, as Windows' `CompiledRule` is: with every library
    // switched on that is about 1,700 regular expressions, and they only change when the rules do.
    private(set) var rules = CompiledRules(dictionaryEntries: [], snippets: [], libraryEntries: [])

    /// - Parameter libraryEntries: enabled entries from any switched-on dictionary libraries
    ///   (see `DictionaryLibraryService`), already filtered to `enabled`. Merged behind the base
    ///   dictionary so a user's own entry always wins over a library's for the same spoken form.
    ///   Mirrors Windows' `DictionaryLibraryComposer.Merge` usage in `TextPostProcessor.Reload`.
    func reload(dictionaryEntries: [DictionaryEntry], snippets: [Snippet], libraryEntries: [DictionaryEntry] = []) {
        rules = CompiledRules(dictionaryEntries: dictionaryEntries, snippets: snippets, libraryEntries: libraryEntries)
    }

    func process(_ text: String) -> String {
        processDetailed(text).text
    }

    /// Same processing as `process(_:)`, but also reports every dictionary/snippet substitution as
    /// a span in the final text, for the Playground's inline highlight view.
    func processDetailed(_ text: String) -> TextPostProcessingResult {
        rules.processDetailed(text)
    }

    /// With AI cleanup on, the step before the request: the vocabulary rules, once, on the raw
    /// transcript. The pass's text is what the provider is sent.
    func correctVocabulary(_ text: String) -> VocabularyPass {
        rules.correctVocabulary(text)
    }

    /// With AI cleanup on, the step after an accepted reply, with the rules `pass` ran with.
    func finishAfterCleanup(_ reply: String, after pass: VocabularyPass) -> TextPostProcessingResult {
        pass.rules.finishAfterCleanup(reply, after: pass.result)
    }

    /// Whether `entry` is a vocabulary rule: its replacement is one line of at most
    /// `vocabularyReplacementLimit` characters, a spelling rather than a template, and holds no em or
    /// en dash. With AI cleanup on, only vocabulary rules run on text a provider is sent; any other
    /// rule is a template in all but name, so it runs after cleanup with the snippets, and its
    /// replacement never leaves the Mac. A replacement with a dash runs there too, after the reply's
    /// dash normalization (`DashNormalizer`), so a dash the user wrote survives, as on Windows, where
    /// every rule runs after cleanup.
    static func isVocabulary(_ entry: DictionaryEntry) -> Bool {
        entry.replacement.utf16.count <= vocabularyReplacementLimit
            && !entry.replacement.contains(where: \.isNewline)
            && !DashNormalizer.containsDash(entry.replacement)
    }

    // MARK: - Compiled rules

    /// One load's rules, compiled once and never changed afterwards, so a dictation keeps the set its
    /// first step used while a Settings edit loads the next one.
    final class CompiledRules {
        /// Every enabled dictionary and library rule, for the pass after the snippets with cleanup off.
        private let dictionaryRules: [DictionaryRule]
        private let snippetRules: [SnippetRule]
        /// With cleanup on, the rules that run on the text the provider is sent (`isVocabulary`).
        private let vocabularyRules: [DictionaryRule]
        /// With cleanup on, the rules that run on the reply: every snippet, its template already run
        /// through the whole dictionary as it would be with cleanup off, and every rule that is not
        /// vocabulary. Each also matches its spoken form as the vocabulary rules write it, since the
        /// text it meets went through those rules before the model saw it.
        private let snippetRulesAfterCleanup: [SnippetRule]
        private let dictionaryRulesAfterCleanup: [DictionaryRule]

        init(dictionaryEntries: [DictionaryEntry], snippets: [Snippet], libraryEntries: [DictionaryEntry]) {
            let base = dictionaryEntries.filter { $0.enabled && !$0.pattern.isEmpty }
            let effective =
                libraryEntries.isEmpty
                ? base
                : DictionaryLibraryComposer.merge(baseEntries: base, libraryEntries: libraryEntries)
            let dictionary = effective.map(DictionaryRule.init)
            let snippetRules =
                snippets
                .filter { $0.enabled && !$0.phrase.isEmpty && !$0.template.isEmpty }
                .map(SnippetRule.init)
            let vocabulary = dictionary.filter { TextPostProcessor.isVocabulary($0.entry) }
            let templateLike = dictionary.filter { !TextPostProcessor.isVocabulary($0.entry) }
            dictionaryRules = dictionary
            self.snippetRules = snippetRules
            vocabularyRules = vocabulary
            dictionaryRulesAfterCleanup = templateLike.flatMap { rule in
                CompiledRules.matchedForms(of: rule.entry.pattern, vocabulary: vocabulary).map {
                    DictionaryRule(entry: rule.entry, matching: $0)
                }
            }
            snippetRulesAfterCleanup = snippetRules.flatMap { rule in
                var canonical = rule.snippet
                canonical.template =
                    TextPostProcessor.applySinglePass(canonical.template, rules: dictionary, kind: .dictionary).0
                return CompiledRules.matchedForms(of: rule.snippet.phrase, vocabulary: vocabulary).map {
                    SnippetRule(snippet: canonical, matching: $0)
                }
            }
        }

        /// A pattern as it is spoken, and also as the vocabulary rules write it when that differs by more
        /// than case.
        private static func matchedForms(of pattern: String, vocabulary: [DictionaryRule]) -> [String] {
            let written = TextPostProcessor.applySinglePass(pattern, rules: vocabulary, kind: .dictionary).0
            if written.isEmpty || written.caseInsensitiveCompare(pattern) == .orderedSame {
                return [pattern]
            }
            return [pattern, written]
        }

        /// With cleanup off: snippets, then the dictionary and the libraries, each phase in one pass.
        func processDetailed(_ text: String) -> TextPostProcessingResult {
            guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return TextPostProcessingResult(text: "", replacements: [])
            }

            let normalized = TextPostProcessor.normalizeWhitespace(text)

            let (afterSnippets, snippetReplacements) = TextPostProcessor.applySinglePass(
                normalized, rules: snippetRules, kind: .snippet)
            let (finalText, dictionaryReplacements) = TextPostProcessor.applySinglePass(
                afterSnippets, rules: dictionaryRules, kind: .dictionary)

            // Snippet spans were reported against `afterSnippets`, which then had dictionary rules
            // applied on top; re-locate each snippet's (possibly dictionary-canonicalized) template by
            // searching for it in the final text, mirroring Windows' `canonicalSnippets` pass.
            var replacements = dictionaryReplacements
            var searchStart = finalText.startIndex
            for snippetReplacement in snippetReplacements {
                let (canonicalTemplate, _) = TextPostProcessor.applySinglePass(
                    snippetReplacement.replacement, rules: dictionaryRules, kind: .dictionary)
                guard !canonicalTemplate.isEmpty else { continue }
                if let range = finalText.range(of: canonicalTemplate, range: searchStart..<finalText.endIndex) {
                    let nsRange = NSRange(range, in: finalText)
                    replacements.append(
                        TextReplacement(
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

        /// With cleanup on, before the request: the vocabulary rules, in one pass, on the normalized
        /// transcript. No snippet runs and no template-like rule does.
        func correctVocabulary(_ text: String) -> VocabularyPass {
            guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return VocabularyPass(result: TextPostProcessingResult(text: "", replacements: []), rules: self)
            }
            let (corrected, replacements) = TextPostProcessor.applySinglePass(
                TextPostProcessor.normalizeWhitespace(text), rules: vocabularyRules, kind: .dictionary)
            return VocabularyPass(
                result: TextPostProcessingResult(text: corrected, replacements: replacements), rules: self)
        }

        /// With cleanup on, after an accepted reply: the snippets, then the template-like rules, each
        /// phase in one pass. No vocabulary rule runs again, and a match that only exists because a
        /// vocabulary rule wrote it is skipped (`spokenMatches`), so no rule's output feeds another's.
        /// `corrected` is the vocabulary step's result for the text the provider was sent.
        func finishAfterCleanup(
            _ reply: String, after corrected: TextPostProcessingResult
        ) -> TextPostProcessingResult {
            guard !reply.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return TextPostProcessingResult(text: "", replacements: [])
            }
            let rewrites = VocabularyRewrite.all(in: corrected)
            let normalized = TextPostProcessor.normalizeWhitespace(reply)
            let (afterSnippets, snippetSpans) = TextPostProcessor.applySinglePass(
                normalized, rules: snippetRulesAfterCleanup, kind: .snippet,
                admitting: TextPostProcessor.spokenMatches(in: normalized, rewrites: rewrites, outside: []))
            // A template is already canonical, so no rule runs inside it again.
            let templates = snippetSpans.map { NSRange(location: $0.start, length: $0.length) }
            let (finalText, ruleSpans) = TextPostProcessor.applySinglePass(
                afterSnippets, rules: dictionaryRulesAfterCleanup, kind: .dictionary,
                admitting: TextPostProcessor.spokenMatches(in: afterSnippets, rewrites: rewrites, outside: templates))

            // For the Playground: the templates where they now sit, then what the vocabulary rules wrote,
            // wherever the reply kept it.
            let located = TextPostProcessor.relocate(snippetSpans, in: finalText, options: [], avoiding: [])
            let taken = (located + ruleSpans).map { NSRange(location: $0.start, length: $0.length) }
            let vocabulary = TextPostProcessor.relocate(
                corrected.replacements, in: finalText, options: [.caseInsensitive], avoiding: taken)
            return TextPostProcessingResult(
                text: finalText, replacements: TextPostProcessor.withoutOverlaps(ruleSpans + located + vocabulary))
        }
    }

    /// Runs one dictionary rule over already-finalized text, using the same normalization and
    /// matcher as the live pipeline. Exists so the quick-add popup can repair the transcript a
    /// correction came from, without a private copy of the matcher drifting from
    /// `processDetailed(_:)`'s real behavior. Only the one rule is applied, since the text has
    /// already been through every other rule. Mirrors Windows' `TextPostProcessor.ApplyRule`.
    static func applyRule(_ text: String?, entry: DictionaryEntry?) -> String {
        guard let text, !text.isEmpty, let entry, !entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        else {
            return text ?? ""
        }

        let trimmedEntry = DictionaryEntry(
            id: entry.id,
            pattern: entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines),
            replacement: entry.replacement,
            wholeWord: entry.wholeWord,
            enabled: entry.enabled)

        let normalized = normalizeWhitespace(text)
        let (result, _) = applySinglePass(normalized, rules: [DictionaryRule(entry: trimmedEntry)], kind: .dictionary)
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
        // pattern (e.g. "york" -> "New York") can double-fire: text that already holds the canonical
        // form, dictated that way or written so by a cleanup model, would expand the embedded pattern
        // again ("New York" -> "New New York"). A same-length entry is a pure casing/punctuation fix
        // ("azure" -> "Azure"); it must keep the plain fast-path replace so the fix actually applies,
        // so the length guard matters, not just an optimization. Mirrors Windows'
        // `CompiledRule._replacementContainsPattern`.
        private let replacementContainsPattern: Bool

        init(entry: DictionaryEntry) {
            self.init(entry: entry, matching: entry.pattern)
        }

        /// Replaces as `entry` does wherever `literal` occurs: the entry's pattern, or that pattern as the
        /// vocabulary rules write it (`CompiledRules.matchedForms`). `pattern` stays the entry's own.
        init(entry: DictionaryEntry, matching literal: String) {
            self.entry = entry
            let escaped = NSRegularExpression.escapedPattern(for: literal)
            let pattern = entry.wholeWord ? "(?<!\\w)\(escaped)(?!\\w)" : escaped
            self.regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
            self.replacementContainsPattern =
                !literal.isEmpty
                && entry.replacement.count > literal.count
                && entry.replacement.range(of: literal, options: .caseInsensitive) != nil
        }

        func findMatches(in text: String) -> [ReplacementCandidate] {
            guard let regex else { return [] }
            let fullRange = NSRange(text.startIndex..., in: text)
            let nsText = text as NSString
            let canonicalStarts =
                replacementContainsPattern ? Self.collectReplacementStarts(entry.replacement, in: nsText) : []
            return regex.matches(in: text, range: fullRange).compactMap { match in
                guard let swiftRange = Range(match.range, in: text) else { return nil }
                let original = String(text[swiftRange])
                let replacement =
                    !canonicalStarts.isEmpty
                        && Self.isInsideAnyReplacement(
                            canonicalStarts, matchRange: match.range,
                            replacementLength: (entry.replacement as NSString).length)
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
                from = found.location + 1  // allow overlapping occurrences
            }
            return starts
        }

        // Mirrors Windows' `IsInsideAnyReplacement`.
        private static func isInsideAnyReplacement(_ starts: [Int], matchRange: NSRange, replacementLength: Int) -> Bool
        {
            let matchEnd = matchRange.location + matchRange.length
            for idx in starts {
                if idx > matchRange.location {
                    break  // ascending: no later occurrence can contain this match
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
        let order: Int = -1  // snippets always take priority within their own phase; irrelevant across phases
        var pattern: String { snippet.phrase }
        private let regex: NSRegularExpression?

        init(snippet: Snippet) {
            self.init(snippet: snippet, matching: snippet.phrase)
        }

        /// Expands `snippet` wherever `literal` occurs: the trigger phrase, or that phrase as the vocabulary
        /// rules write it (`CompiledRules.matchedForms`). `pattern` stays the phrase itself.
        init(snippet: Snippet, matching literal: String) {
            self.snippet = snippet
            let escaped = NSRegularExpression.escapedPattern(for: literal)
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
    /// with the rule's pattern, for Playground highlighting. A match `admits` turns down is left as
    /// it is, as if its rule had not matched there.
    private static func applySinglePass<R: Rule>(
        _ text: String,
        rules: [R],
        kind: TextReplacementKind,
        admitting admits: (ReplacementCandidate, String) -> Bool = { _, _ in true }
    ) -> (String, [TextReplacement]) {
        let candidates =
            rules
            .flatMap { rule in rule.findMatches(in: text).map { ($0, rule.pattern) } }
            .filter { admits($0.0, $0.1) }
            .sorted { lhs, rhs in
                if lhs.0.range.location != rhs.0.range.location {
                    return lhs.0.range.location < rhs.0.range.location
                }
                if lhs.0.range.length != rhs.0.range.length {
                    return lhs.0.range.length > rhs.0.range.length  // longest match first
                }
                return lhs.0.order < rhs.0.order
            }

        guard !candidates.isEmpty else { return (text, []) }

        let nsText = text as NSString
        var result = ""
        var position = 0
        var replacements: [TextReplacement] = []

        for (candidate, pattern) in candidates {
            guard candidate.range.location >= position else { continue }  // overlap, skip
            var prefixLength = candidate.range.location - position
            if prefixLength > 0,
                !candidate.replacement.isEmpty,
                candidate.replacement.allSatisfy(Self.isTightPunctuation),
                Self.isWhitespace(unit: nsText.character(at: candidate.range.location - 1))
            {
                prefixLength -= 1
            }
            let prefixRange = NSRange(location: position, length: prefixLength)
            result += nsText.substring(with: prefixRange)
            let start = (result as NSString).length
            result += candidate.replacement
            if candidate.original != candidate.replacement {
                replacements.append(
                    TextReplacement(
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

    // MARK: - After cleanup

    /// What a vocabulary rule wrote in place of a spoken form, where that changed more than case.
    private struct VocabularyRewrite {
        let written: String
        let spoken: String

        /// The distinct rewrites of the vocabulary step. A pure casing fix is left out: it changes no letter, so
        /// whatever matches its output matched the words as spoken too.
        static func all(in corrected: TextPostProcessingResult) -> [VocabularyRewrite] {
            var rewrites: [VocabularyRewrite] = []
            for span in corrected.replacements {
                let isRewrite =
                    !span.replacement.isEmpty
                    && span.replacement.caseInsensitiveCompare(span.pattern) != .orderedSame
                let isNew = !rewrites.contains { $0.written == span.replacement && $0.spoken == span.pattern }
                if isRewrite && isNew {
                    rewrites.append(VocabularyRewrite(written: span.replacement, spoken: span.pattern))
                }
            }
            return rewrites
        }
    }

    /// Admits a match after cleanup only when it is the user's own words for its rule: every vocabulary rewrite
    /// inside it, turned back into what was spoken, gives the rule's pattern again. Anything else is one rule's
    /// output feeding another ("alpha" written as "beta" before cleanup, then "beta" expanded after it), and a match
    /// that reaches into `excluded` (the templates, already canonical) would run a second rule over a rule's output.
    /// Both are skipped. A rewrite found in the reply is taken for the vocabulary step's output wherever it is, so a
    /// dictation that also said the written form itself may lose a match there, never gain one.
    private static func spokenMatches(
        in text: String, rewrites: [VocabularyRewrite], outside excluded: [NSRange]
    ) -> (ReplacementCandidate, String) -> Bool {
        let nsText = text as NSString
        var collected: [(range: NSRange, spoken: String)] = []
        for rewrite in rewrites {
            for range in occurrences(of: rewrite.written, in: nsText) {
                collected.append((range: range, spoken: rewrite.spoken))
            }
        }
        let found = collected.sorted { lhs, rhs in
            if lhs.range.location != rhs.range.location {
                return lhs.range.location < rhs.range.location
            }
            return lhs.range.length > rhs.range.length
        }
        return { candidate, pattern in
            let match = candidate.range
            let end = match.location + match.length
            if excluded.contains(where: { NSIntersectionRange($0, match).length > 0 }) {
                return false
            }
            let inside = found.filter { NSIntersectionRange($0.range, match).length > 0 }
            guard !inside.isEmpty else { return true }
            var spoken = ""
            var position = match.location
            for occurrence in inside {
                let range = occurrence.range
                // Reaching past the match: the rewrite was not part of what the rule matched.
                guard range.location >= match.location, range.location + range.length <= end else { return false }
                guard range.location >= position else { continue }
                spoken += nsText.substring(with: NSRange(location: position, length: range.location - position))
                spoken += occurrence.spoken
                position = range.location + range.length
            }
            spoken += nsText.substring(with: NSRange(location: position, length: end - position))
            return spoken.caseInsensitiveCompare(pattern) == .orderedSame
        }
    }

    /// Every place `string` occurs in `text`, ignoring case, overlapping ones included.
    private static func occurrences(of string: String, in text: NSString) -> [NSRange] {
        let length = (string as NSString).length
        guard length > 0 else { return [] }
        var found: [NSRange] = []
        var from = 0
        while from <= text.length - length {
            let range = text.range(
                of: string, options: [.caseInsensitive], range: NSRange(location: from, length: text.length - from))
            guard range.location != NSNotFound else { break }
            found.append(range)
            from = range.location + 1
        }
        return found
    }

    /// Finds each span's replacement in `text`, in order, each after the one before it and clear of `avoiding`, so
    /// the Playground can underline where a replacement now sits. One the reply dropped or rewrote is not shown.
    private static func relocate(
        _ spans: [TextReplacement], in text: String, options: NSString.CompareOptions, avoiding: [NSRange]
    ) -> [TextReplacement] {
        let nsText = text as NSString
        var located: [TextReplacement] = []
        var from = 0
        for span in spans where !span.replacement.isEmpty {
            var searchFrom = from
            while searchFrom < nsText.length {
                let range = nsText.range(
                    of: span.replacement, options: options,
                    range: NSRange(location: searchFrom, length: nsText.length - searchFrom))
                guard range.location != NSNotFound else { break }
                if avoiding.contains(where: { NSIntersectionRange($0, range).length > 0 }) {
                    searchFrom = range.location + 1
                    continue
                }
                located.append(
                    TextReplacement(
                        start: range.location, length: range.length, pattern: span.pattern,
                        replacement: span.replacement, kind: span.kind))
                from = range.location + range.length
                break
            }
        }
        return located
    }

    /// `spans` in text order, without one that overlaps a span before it.
    private static func withoutOverlaps(_ spans: [TextReplacement]) -> [TextReplacement] {
        var kept: [TextReplacement] = []
        let ordered = spans.sorted { lhs, rhs in
            if lhs.start != rhs.start {
                return lhs.start < rhs.start
            }
            return lhs.length > rhs.length
        }
        for span in ordered {
            if let last = kept.last, span.start < last.start + last.length {
                continue
            }
            kept.append(span)
        }
        return kept
    }

    private static func isTightPunctuation(_ ch: Character) -> Bool {
        ch == "," || ch == "." || ch == "!" || ch == "?" || ch == ";" || ch == ":"
    }

    /// Whether one UTF-16 unit is whitespace. Half of a surrogate pair (an emoji just before a match,
    /// which a snippet template can produce) has no scalar of its own and is not whitespace: every
    /// whitespace character sits in the Basic Multilingual Plane.
    private static func isWhitespace(unit: unichar) -> Bool {
        guard let scalar = Unicode.Scalar(unit) else {
            return false
        }
        return CharacterSet.whitespacesAndNewlines.contains(scalar)
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
