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

/// The step before the request of one dictation with AI cleanup on (`TextPostProcessor.correctVocabulary`). Every
/// replacement is decided there, once, on the transcript as it was spoken, exactly as cleanup off decides it. The
/// vocabulary rules' replacements are made in the text the provider is sent; every other replacement is held back as
/// an edit of the words that set it off, and is made after an accepted reply where the reply kept those words
/// (`TextPostProcessor.finishAfterCleanup`). No rule is ever matched against the model's text.
struct VocabularyPass {
    /// What the provider is sent, with the spans of the replacements made in it.
    let result: TextPostProcessingResult
    /// The replacements held back until the reply, in the order their words appear in `result.text`.
    fileprivate let heldBack: [TextPostProcessor.HeldBackEdit]

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
/// cleanup. With cleanup on, every replacement is decided on the spoken transcript, as cleanup off decides it, and
/// the replacements are split around the request: the vocabulary rules' (`isVocabulary`) are made in the text the
/// provider is sent (`correctVocabulary`), and the snippets and the template-like replacements after the accepted
/// reply, each at most once and only where the reply kept the words that set it off (`finishAfterCleanup`). So no
/// snippet template or template-like replacement reaches a provider, no rule runs twice or on another rule's or the
/// model's output, and a model that returns what it was sent gets exactly the cleanup-off text.
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

    /// Replaces the rules in one step with ones compiled elsewhere, off the main actor in the app
    /// (`DictationRuleSnapshot`).
    func install(_ compiled: CompiledRules) {
        rules = compiled
    }

    func process(_ text: String) -> String {
        processDetailed(text).text
    }

    /// Same processing as `process(_:)`, but also reports every dictionary/snippet substitution as
    /// a span in the final text, for the Playground's inline highlight view.
    func processDetailed(_ text: String) -> TextPostProcessingResult {
        rules.processDetailed(text)
    }

    /// With AI cleanup on, the step before the request: every replacement decided on the transcript, and the
    /// vocabulary rules' made. The pass's text is what the provider is sent.
    func correctVocabulary(_ text: String) -> VocabularyPass {
        rules.correctVocabulary(text)
    }

    /// With AI cleanup on, the step after an accepted reply: the replacements `pass` held back, made where the reply
    /// kept their words. Nothing but `pass` decides it, so a rule edited while the request was out changes nothing.
    func finishAfterCleanup(_ reply: String, after pass: VocabularyPass) -> TextPostProcessingResult {
        Self.finish(reply, after: pass)
    }

    /// Whether `entry` is a vocabulary rule, one whose replacement may be made in text a provider is sent: a spelling
    /// of one line and 1 to `vocabularyReplacementLimit` characters, with no em or en dash, already in the form the
    /// pipeline normalizes text to (no tab, no run of spaces, no space before `, . ! ? ;` or `:`, none at either end).
    /// Every other rule is template-like, and its replacement never leaves the Mac: a template in all but name, a
    /// deletion, a replacement with a dash (made after the reply's dash normalization, so the user's own dash
    /// survives, as on Windows, where every rule runs after cleanup), and one whose spacing the normalization of the
    /// reply would change. The Usage Insights request leaves template-like replacements out by this same test.
    static func isVocabulary(_ entry: DictionaryEntry) -> Bool {
        let replacement = entry.replacement
        return !replacement.isEmpty
            && replacement.utf16.count <= vocabularyReplacementLimit
            && !replacement.contains(where: \.isNewline)
            && !DashNormalizer.containsDash(replacement)
            && normalizeWhitespace(replacement) == replacement
    }

    // MARK: - Compiled rules

    /// One load's rules, compiled once and never changed afterwards, so they can be compiled on any thread and handed
    /// to the one that uses them.
    final class CompiledRules: Sendable {
        /// Every enabled dictionary and library rule, for the pass after the snippets.
        private let dictionaryRules: [DictionaryRule]
        private let snippetRules: [SnippetRule]
        /// The rules whose replacements may be made in text a provider is sent (`isVocabulary`). With cleanup on they
        /// also write the words of a held-back replacement in the user's spellings (`writtenForm`).
        private let vocabularyRules: [DictionaryRule]

        init(dictionaryEntries: [DictionaryEntry], snippets: [Snippet], libraryEntries: [DictionaryEntry]) {
            let base = dictionaryEntries.filter { $0.enabled && !$0.pattern.isEmpty }
            let effective =
                libraryEntries.isEmpty
                ? base
                : DictionaryLibraryComposer.merge(baseEntries: base, libraryEntries: libraryEntries)
            let dictionary = effective.map(DictionaryRule.init)
            dictionaryRules = dictionary
            snippetRules =
                snippets
                .filter { $0.enabled && !$0.phrase.isEmpty && !$0.template.isEmpty }
                .map(SnippetRule.init)
            vocabularyRules = dictionary.filter(\.isVocabulary)
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

        /// With cleanup on, before the request: every replacement cleanup off would make, planned on the normalized
        /// transcript, and the text the provider is sent, with the vocabulary rules' replacements made in it and every
        /// other replacement held back.
        func correctVocabulary(_ text: String) -> VocabularyPass {
            guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return VocabularyPass(result: TextPostProcessingResult(text: "", replacements: []), heldBack: [])
            }
            let normalized = TextPostProcessor.normalizeWhitespace(text)
            let edits = plannedEdits(normalized)
            let pass = TextPostProcessor.vocabularyPass(normalized, edits: edits, vocabulary: vocabularyRules)
            // The reply is normalized before the held-back replacements are made, so the text sent has to be in that
            // form already for a reply that is the text sent to give exactly the cleanup-off text. Should a
            // replacement made in it break that form anyway, none is made: every replacement is held back with its
            // words as spoken, and the text sent is the normalized transcript itself.
            if TextPostProcessor.normalizeWhitespace(pass.text) == pass.text {
                return pass
            }
            return TextPostProcessor.vocabularyPass(normalized, edits: edits, vocabulary: nil)
        }

        /// The cleanup-off result for `normalized`, as edits of it. The snippets' pass and then the dictionary's pass
        /// over its output run exactly as in `processDetailed`; a dictionary replacement that reaches into a template
        /// joins that template's edit, since its text only exists once the template is in place.
        private func plannedEdits(_ normalized: String) -> [PlannedEdit] {
            let source = normalized as NSString
            var pieces: [ExpandedPiece] = []
            var expanded = ""
            var expandedLength = 0
            func append(_ text: String, from range: NSRange, template: Selection?) {
                let length = text.utf16.count
                let target = NSRange(location: expandedLength, length: length)
                pieces.append(ExpandedPiece(source: range, target: target, template: template))
                expanded += text
                expandedLength += length
            }
            var position = 0
            for selection in TextPostProcessor.select(normalized, rules: snippetRules) {
                if selection.start > position {
                    let range = NSRange(location: position, length: selection.start - position)
                    append(source.substring(with: range), from: range, template: nil)
                }
                let trigger = NSRange(location: selection.start, length: selection.end - selection.start)
                append(selection.replacement, from: trigger, template: selection)
                position = selection.end
            }
            if position < source.length {
                let range = NSRange(location: position, length: source.length - position)
                append(source.substring(with: range), from: range, template: nil)
            }

            // A match that changes nothing is left out: the text it would copy is the text already there.
            let replacements = TextPostProcessor.select(expanded, rules: dictionaryRules).filter(\.changesText)
            var members: [(range: NSRange, piece: Int?, replacement: Int?)] = []
            for (index, piece) in pieces.enumerated() where piece.template != nil {
                members.append((range: piece.target, piece: index, replacement: nil))
            }
            for (index, replacement) in replacements.enumerated() {
                let range = NSRange(location: replacement.start, length: replacement.end - replacement.start)
                members.append((range: range, piece: nil, replacement: index))
            }
            members.sort { lhs, rhs in
                if lhs.range.location != rhs.range.location {
                    return lhs.range.location < rhs.range.location
                }
                return lhs.range.length < rhs.range.length
            }
            var groups: [PlanGroup] = []
            for member in members {
                let end = member.range.location + member.range.length
                if groups.isEmpty || member.range.location >= groups[groups.count - 1].end {
                    groups.append(PlanGroup(start: member.range.location, end: end, pieces: [], replacements: []))
                }
                let last = groups.count - 1
                groups[last].end = max(groups[last].end, end)
                if let piece = member.piece {
                    groups[last].pieces.append(piece)
                }
                if let replacement = member.replacement {
                    groups[last].replacements.append(replacement)
                }
            }
            let text = expanded as NSString
            return groups.map { group in
                CompiledRules.edit(for: group, pieces: pieces, replacements: replacements, expanded: text)
            }
        }

        /// The edit a group makes: its stretch of the normalized transcript, and the cleanup-off text that stretch
        /// becomes, with the replacements in it for the Playground.
        private static func edit(
            for group: PlanGroup, pieces: [ExpandedPiece], replacements: [Selection], expanded: NSString
        ) -> PlannedEdit {
            let made = group.replacements.map { replacements[$0] }
            var output = ""
            var outputLength = 0
            var spans: [TextReplacement] = []
            var position = group.start
            for replacement in made {
                let kept = expanded.substring(with: NSRange(location: position, length: replacement.start - position))
                output += kept
                outputLength += kept.utf16.count
                let length = replacement.replacement.utf16.count
                if replacement.original != replacement.replacement {
                    spans.append(
                        TextReplacement(
                            start: outputLength, length: length, pattern: replacement.pattern,
                            replacement: replacement.replacement, kind: .dictionary))
                }
                output += replacement.replacement
                outputLength += length
                position = replacement.end
            }
            output += expanded.substring(with: NSRange(location: position, length: group.end - position))

            // Each template where it ended up, with the replacements made inside and around it.
            let written = output as NSString
            for index in group.pieces {
                guard let template = pieces[index].template else { continue }
                let target = pieces[index].target
                let start = outputOffset(of: target.location, in: group, made: made, towardEnd: false)
                let end = outputOffset(of: target.location + target.length, in: group, made: made, towardEnd: true)
                let range = NSRange(location: start, length: end - start)
                spans.append(
                    TextReplacement(
                        start: start, length: range.length, pattern: template.pattern,
                        replacement: written.substring(with: range), kind: .snippet))
            }
            spans.sort { $0.start < $1.start }

            let isVocabulary = group.pieces.isEmpty && made.count == 1 && made[0].isVocabulary
            return PlannedEdit(
                source: sourceRange(of: group, pieces: pieces), output: output, spans: spans,
                isVocabulary: isVocabulary)
        }

        /// Where `offset` of the expanded text lands in a group's output. An offset inside a replacement lands at the
        /// start of what it wrote, or at its end when `towardEnd`.
        private static func outputOffset(
            of offset: Int, in group: PlanGroup, made: [Selection], towardEnd: Bool
        ) -> Int {
            var shift = 0
            for replacement in made {
                if offset <= replacement.start {
                    break
                }
                let length = replacement.replacement.utf16.count
                if offset < replacement.end {
                    let written = replacement.start - group.start + shift
                    return towardEnd ? written + length : written
                }
                shift += length - (replacement.end - replacement.start)
            }
            return offset - group.start + shift
        }

        /// Where a group sits in the normalized transcript: a template stands for its trigger (with any whitespace the
        /// tight punctuation guard took before it), and the transcript's own text for itself.
        private static func sourceRange(of group: PlanGroup, pieces: [ExpandedPiece]) -> NSRange {
            var lower = 0
            var upper = 0
            for piece in pieces {
                let pieceEnd = piece.target.location + piece.target.length
                if piece.target.location <= group.start, group.start < pieceEnd {
                    let inside = group.start - piece.target.location
                    lower = piece.template == nil ? piece.source.location + inside : piece.source.location
                }
                if piece.target.location < group.end, group.end <= pieceEnd {
                    let inside = group.end - piece.target.location
                    let sourceEnd = piece.source.location + piece.source.length
                    upper = piece.template == nil ? piece.source.location + inside : sourceEnd
                }
            }
            return NSRange(location: lower, length: upper - lower)
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
        /// Whether the rule may run on text a provider is sent (`isVocabulary`); false for a snippet.
        let isVocabulary: Bool
    }

    private struct DictionaryRule: Rule {
        let entry: DictionaryEntry
        let order: Int = 0
        /// Whether the rule's replacement may be made in text a provider is sent (`TextPostProcessor.isVocabulary`).
        let isVocabulary: Bool
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
            self.entry = entry
            isVocabulary = TextPostProcessor.isVocabulary(entry)
            let escaped = NSRegularExpression.escapedPattern(for: entry.pattern)
            let pattern = entry.wholeWord ? "(?<!\\w)\(escaped)(?!\\w)" : escaped
            self.regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
            self.replacementContainsPattern =
                !entry.pattern.isEmpty
                && entry.replacement.count > entry.pattern.count
                && entry.replacement.range(of: entry.pattern, options: .caseInsensitive) != nil
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
                    order: order,
                    isVocabulary: isVocabulary)
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
                    order: order,
                    isVocabulary: false)
            }
        }
    }

    /// A candidate a single pass keeps.
    private struct Selection {
        /// Where the replacement begins: at the match, or one before it when the tight punctuation guard takes the
        /// whitespace in front of it.
        let start: Int
        let match: NSRange
        let original: String
        let replacement: String
        /// The rule's pattern, for the Playground.
        let pattern: String
        let isVocabulary: Bool

        var end: Int {
            match.location + match.length
        }

        /// Whether keeping the candidate changes the text at all.
        var changesText: Bool {
            start != match.location || original != replacement
        }
    }

    /// The candidates one pass keeps: every rule's matches against the *original* input, sorted by position (then
    /// longest first, then rule order), and those that do not overlap one kept before them. Mirrors Windows'
    /// `ApplySinglePass`, including its "tight punctuation" guard: a replacement that is entirely comma/period/etc.
    /// absorbs the one whitespace character immediately before it, so "hello comma world" -> "hello, world" rather
    /// than "hello , world".
    private static func select<R: Rule>(_ text: String, rules: [R]) -> [Selection] {
        let candidates =
            rules
            .flatMap { rule in rule.findMatches(in: text).map { ($0, rule.pattern) } }
            .sorted { lhs, rhs in
                if lhs.0.range.location != rhs.0.range.location {
                    return lhs.0.range.location < rhs.0.range.location
                }
                if lhs.0.range.length != rhs.0.range.length {
                    return lhs.0.range.length > rhs.0.range.length  // longest match first
                }
                return lhs.0.order < rhs.0.order
            }

        let nsText = text as NSString
        var selections: [Selection] = []
        var position = 0
        for (candidate, pattern) in candidates {
            guard candidate.range.location >= position else { continue }  // overlap, skip
            var start = candidate.range.location
            if start > position,
                !candidate.replacement.isEmpty,
                candidate.replacement.allSatisfy(Self.isTightPunctuation),
                Self.isWhitespace(unit: nsText.character(at: start - 1))
            {
                start -= 1
            }
            selections.append(
                Selection(
                    start: start, match: candidate.range, original: candidate.original,
                    replacement: candidate.replacement, pattern: pattern, isVocabulary: candidate.isVocabulary))
            position = candidate.range.location + candidate.range.length
        }
        return selections
    }

    /// Applies one pass's kept candidates (`select`), splicing each replacement in. Replacement text is never
    /// re-scanned within this same call, only by the next phase (see `processDetailed(_:)`). Also returns every
    /// located span whose replacement text actually changed the input, tagged with the rule's pattern, for
    /// Playground highlighting.
    private static func applySinglePass<R: Rule>(
        _ text: String,
        rules: [R],
        kind: TextReplacementKind
    ) -> (String, [TextReplacement]) {
        let selections = select(text, rules: rules)
        guard !selections.isEmpty else { return (text, []) }

        let nsText = text as NSString
        var result = ""
        var resultLength = 0
        var replacements: [TextReplacement] = []
        var position = 0
        for selection in selections {
            let kept = nsText.substring(with: NSRange(location: position, length: selection.start - position))
            result += kept
            resultLength += kept.utf16.count
            let length = selection.replacement.utf16.count
            if selection.original != selection.replacement {
                replacements.append(
                    TextReplacement(
                        start: resultLength,
                        length: length,
                        pattern: selection.pattern,
                        replacement: selection.replacement,
                        kind: kind))
            }
            result += selection.replacement
            resultLength += length
            position = selection.end
        }
        result += nsText.substring(from: position)
        return (result, replacements)
    }

    // MARK: - With AI cleanup on

    /// A stretch of the text the dictionary pass meets with cleanup off: the transcript's own text, or a template in
    /// place of its trigger.
    private struct ExpandedPiece {
        /// Where the piece comes from in the normalized transcript: its own text, or the trigger with any whitespace
        /// the tight punctuation guard took before it.
        let source: NSRange
        /// Where the piece sits in the expanded text.
        let target: NSRange
        /// The snippet's selection, for a template.
        let template: Selection?
    }

    /// What overlaps in the expanded text, as one edit: a template with every dictionary replacement that reaches into
    /// it, or a dictionary replacement on its own. Indices into the pieces and into the dictionary pass's replacements.
    private struct PlanGroup {
        var start: Int
        var end: Int
        var pieces: [Int]
        var replacements: [Int]
    }

    /// One edit of the normalized transcript that cleanup off makes: `source` becomes `output`.
    private struct PlannedEdit {
        let source: NSRange
        let output: String
        /// The replacements within `output`, for the Playground.
        let spans: [TextReplacement]
        /// One vocabulary rule's replacement in the transcript's own text, which may be made in the text a provider is
        /// sent. An edit with a template in it, or a template-like rule's replacement, is held back.
        let isVocabulary: Bool
    }

    /// A replacement held back from the text sent: the words that set it off and where they sit in the text sent, and
    /// the cleanup-off text they become, with the spans in it for the Playground.
    fileprivate struct HeldBackEdit {
        let location: Int
        let words: String
        let output: String
        let spans: [TextReplacement]
    }

    /// The text a provider is sent for `normalized`: the edits that are one vocabulary rule's replacement made in it,
    /// and every other edit held back, its words written with the vocabulary rules where that is safe (`writtenForm`)
    /// and as spoken otherwise. A replacement that begins with `, . ! ? ;` or `:` is held back after a space or tab,
    /// which the reply's normalization would take out before it. With `vocabulary` nil nothing is made and every
    /// edit's words go as spoken, so the text sent is `normalized` itself.
    private static func vocabularyPass(
        _ normalized: String, edits: [PlannedEdit], vocabulary: [DictionaryRule]?
    ) -> VocabularyPass {
        let source = normalized as NSString
        var sent = ""
        var sentLength = 0
        var endsWithSpace = false
        var spans: [TextReplacement] = []
        var heldBack: [HeldBackEdit] = []
        func append(_ piece: String) {
            guard let last = piece.utf16.last else { return }
            sent += piece
            sentLength += piece.utf16.count
            endsWithSpace = last == 0x20 || last == 0x09
        }
        var position = 0
        for edit in edits {
            if edit.source.location > position {
                append(source.substring(with: NSRange(location: position, length: edit.source.location - position)))
            }
            let pulledIn = endsWithSpace && startsWithTightPunctuation(edit.output)
            if vocabulary != nil, edit.isVocabulary, !pulledIn {
                spans += shifted(edit.spans, by: sentLength)
                append(edit.output)
            } else {
                let spoken = source.substring(with: edit.source)
                let written = vocabulary.flatMap { rules in
                    writtenForm(of: spoken, at: edit.source, in: source, rules: rules, afterSpace: endsWithSpace)
                }
                let words = written ?? spoken
                let held = HeldBackEdit(location: sentLength, words: words, output: edit.output, spans: edit.spans)
                heldBack.append(held)
                append(words)
            }
            position = edit.source.location + edit.source.length
        }
        if position < source.length {
            append(source.substring(from: position))
        }
        return VocabularyPass(result: TextPostProcessingResult(text: sent, replacements: spans), heldBack: heldBack)
    }

    /// The words of a held-back replacement as the vocabulary rules write them, so the model reads the user's
    /// spellings there too: the trigger "my k eight s notes" is sent as "my K8s notes". Only for words that stand
    /// apart from the text around them, and only when the written form stays in normal form, cannot be pulled up
    /// against a space before it, and begins and ends with a word character exactly where the words do, so it is
    /// found in the reply as the words would be. Nil otherwise, and the words go as spoken.
    private static func writtenForm(
        of spoken: String, at range: NSRange, in source: NSString, rules: [DictionaryRule], afterSpace: Bool
    ) -> String? {
        guard let first = spoken.unicodeScalars.first, let last = spoken.unicodeScalars.last,
            !isWhitespace(first), !isWhitespace(last), standsApart(range, in: source)
        else {
            return nil
        }
        let written = applySinglePass(spoken, rules: rules, kind: .dictionary).0
        guard let writtenFirst = written.unicodeScalars.first, let writtenLast = written.unicodeScalars.last,
            isWordScalar(writtenFirst) == isWordScalar(first), isWordScalar(writtenLast) == isWordScalar(last),
            normalizeWhitespace(written) == written, !(afterSpace && startsWithTightPunctuation(written))
        else {
            return nil
        }
        return written
    }

    /// After an accepted reply: each held-back replacement is made where the reply kept its words, and nowhere else.
    /// The words are looked for as the rules match, ignoring case, with a word character on either side exactly where
    /// the text sent has one, and the replacement is made at the occurrence that answers to its own: the k-th in the
    /// reply for the k-th in the text sent. When the reply holds a different number of them (the model dropped,
    /// repeated or rewrote the words), it is not made and the reply's words stay. Only replacements cleanup off would
    /// make are ever made, each at most once, and a reply that is the text sent gets exactly the cleanup-off text.
    private static func finish(_ reply: String, after pass: VocabularyPass) -> TextPostProcessingResult {
        guard !reply.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return TextPostProcessingResult(text: "", replacements: [])
        }
        let normalized = normalizeWhitespace(reply)
        let target = normalized as NSString
        let sent = pass.text as NSString
        var made: [(range: NSRange, edit: HeldBackEdit)] = []
        var searchFrom = 0
        for edit in pass.heldBack {
            let own = NSRange(location: edit.location, length: edit.words.utf16.count)
            let context = WordContext(of: own, in: sent)
            let inSent = occurrences(of: edit.words, in: sent).filter { WordContext(of: $0, in: sent) == context }
            let inReply = occurrences(of: edit.words, in: target).filter { WordContext(of: $0, in: target) == context }
            guard inSent.count == inReply.count, let index = inSent.firstIndex(of: own),
                inReply[index].location >= searchFrom
            else {
                continue
            }
            made.append((range: inReply[index], edit: edit))
            searchFrom = inReply[index].location + inReply[index].length
        }

        var text = ""
        var length = 0
        var spans: [TextReplacement] = []
        var outputs: [NSRange] = []
        var position = 0
        for (range, edit) in made {
            let kept = target.substring(with: NSRange(location: position, length: range.location - position))
            text += kept
            length += kept.utf16.count
            spans += shifted(edit.spans, by: length)
            let outputLength = edit.output.utf16.count
            outputs.append(NSRange(location: length, length: outputLength))
            text += edit.output
            length += outputLength
            position = range.location + range.length
        }
        text += target.substring(from: position)
        // For the Playground: what the vocabulary rules wrote in the text sent, wherever the reply kept it.
        let vocabulary = relocate(pass.result.replacements, in: text, options: [.caseInsensitive], avoiding: outputs)
        return TextPostProcessingResult(text: text, replacements: withoutOverlaps(spans + vocabulary))
    }

    /// Whether a word character touches a stretch of text, before it and after it.
    private struct WordContext: Equatable {
        let before: Bool
        let after: Bool

        init(of range: NSRange, in text: NSString) {
            before = TextPostProcessor.isWord(TextPostProcessor.scalar(before: range.location, in: text))
            after = TextPostProcessor.isWord(TextPostProcessor.scalar(at: range.location + range.length, in: text))
        }
    }

    /// Every place `words` occurs in `text`, ignoring case as the rules do, overlapping ones included.
    private static func occurrences(of words: String, in text: NSString) -> [NSRange] {
        let pattern = "(?=(\(NSRegularExpression.escapedPattern(for: words))))"
        guard !words.isEmpty, let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else {
            return []
        }
        let whole = NSRange(location: 0, length: text.length)
        return regex.matches(in: text as String, range: whole).map { $0.range(at: 1) }
    }

    /// Whether `range` of `text` begins and ends at a word boundary.
    private static func standsApart(_ range: NSRange, in text: NSString) -> Bool {
        let start = range.location
        let end = range.location + range.length
        let joinsBefore = isWord(scalar(before: start, in: text)) && isWord(scalar(at: start, in: text))
        let joinsAfter = isWord(scalar(before: end, in: text)) && isWord(scalar(at: end, in: text))
        return !joinsBefore && !joinsAfter
    }

    /// The Unicode scalar that ends at UTF-16 offset `offset` of `text`; nil at the start.
    private static func scalar(before offset: Int, in text: NSString) -> Unicode.Scalar? {
        guard offset > 0, offset <= text.length else { return nil }
        let isPair =
            offset > 1 && UTF16.isTrailSurrogate(text.character(at: offset - 1))
            && UTF16.isLeadSurrogate(text.character(at: offset - 2))
        let width = isPair ? 2 : 1
        return text.substring(with: NSRange(location: offset - width, length: width)).unicodeScalars.last
    }

    /// The Unicode scalar that begins at UTF-16 offset `offset` of `text`; nil at the end.
    private static func scalar(at offset: Int, in text: NSString) -> Unicode.Scalar? {
        guard offset >= 0, offset < text.length else { return nil }
        let isPair =
            offset + 1 < text.length && UTF16.isLeadSurrogate(text.character(at: offset))
            && UTF16.isTrailSurrogate(text.character(at: offset + 1))
        let width = isPair ? 2 : 1
        return text.substring(with: NSRange(location: offset, length: width)).unicodeScalars.first
    }

    private static func isWord(_ scalar: Unicode.Scalar?) -> Bool {
        guard let scalar else { return false }
        return isWordScalar(scalar)
    }

    /// Whether `scalar` is a word character as `\w` in the rules' regular expressions reads one (ICU: alphabetic, a
    /// mark, a decimal digit, connector punctuation, or a zero-width joiner or non-joiner).
    private static func isWordScalar(_ scalar: Unicode.Scalar) -> Bool {
        if scalar.value == 0x200C || scalar.value == 0x200D {
            return true
        }
        switch scalar.properties.generalCategory {
        case .nonspacingMark, .spacingMark, .enclosingMark, .decimalNumber, .connectorPunctuation:
            return true
        default:
            return scalar.properties.isAlphabetic
        }
    }

    private static func isWhitespace(_ scalar: Unicode.Scalar) -> Bool {
        CharacterSet.whitespacesAndNewlines.contains(scalar)
    }

    /// Whether `text` begins with one of `, . ! ? ;` or `:`, which the whitespace normalization pulls up against a
    /// space or tab before it.
    private static func startsWithTightPunctuation(_ text: String) -> Bool {
        guard let first = text.unicodeScalars.first else { return false }
        return ",.!?;:".unicodeScalars.contains(first)
    }

    /// `spans` moved `offset` UTF-16 units along.
    private static func shifted(_ spans: [TextReplacement], by offset: Int) -> [TextReplacement] {
        spans.map {
            TextReplacement(
                start: $0.start + offset, length: $0.length, pattern: $0.pattern, replacement: $0.replacement,
                kind: $0.kind)
        }
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

    private static let horizontalWhitespace = try! NSRegularExpression(pattern: "[ \\t\\f\\x0B]+")
    private static let spaceBeforePunctuation = try! NSRegularExpression(pattern: "[ \\t]+([,.!?;:])")

    /// Collapses horizontal whitespace runs to a single space, preserving line breaks. `\v` inside
    /// an ICU character class (which `NSRegularExpression` uses) expands to the full "vertical
    /// whitespace" set (`\n`, `\r`, form feed, NEL, LS, PS), unlike .NET's `Regex`, where `\v` inside
    /// a bracket means only the literal vertical-tab byte. Using `\v` here silently collapsed every
    /// CRLF/LF in the text to a single space; `\x0B` is the literal-vertical-tab escape that actually
    /// matches Windows' `NormalizeWhitespace` behavior. The two expressions are compiled once: every
    /// rule's replacement is checked against this form at each reload (`isVocabulary`).
    private static func normalizeWhitespace(_ text: String) -> String {
        let collapsed = horizontalWhitespace.stringByReplacingMatches(
            in: text, range: NSRange(location: 0, length: text.utf16.count), withTemplate: " ")
        let tightened = spaceBeforePunctuation.stringByReplacingMatches(
            in: collapsed, range: NSRange(location: 0, length: collapsed.utf16.count), withTemplate: "$1")
        return tightened.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
