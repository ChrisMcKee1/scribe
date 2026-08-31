import Foundation

/// Pure logic behind the tray's quick "Add to dictionary" popup: splitting a finished dictation
/// into selectable word chips, turning a chip range back into the exact text Scribe produced, and
/// deciding whether the typed rule creates a new entry, updates an existing one, or changes
/// nothing. Direct port of Windows' `Scribe.Core.Settings.QuickDictionaryAdd`.
///
/// This lives alongside the other pure Settings builders (see `DictionaryEntryBuilder`,
/// `SnippetBuilder`) rather than in the popup's SwiftUI view, because the interesting parts (where
/// a word starts and ends, what counts as trailing punctuation, whether a rule already exists) are
/// exactly the parts worth testing, and none of them need a UI.
enum QuickDictionaryAdd {
    /// Characters trimmed from the ends of a chip selection. Deliberately only sentence
    /// punctuation: stripping every non-alphanumeric would turn "C++" into "C" and "#tag" into
    /// "tag", which are legitimate things to want a rule for. Trimming affects only the ends, so
    /// internal apostrophes in "don't" survive.
    private static let edgePunctuation = CharacterSet(charactersIn: ".,!?;:\"'`()[]{}<>\u{2026}\u{2014}\u{2013}\u{00AB}\u{00BB}\u{201C}\u{201D}\u{2018}\u{2019}")

    /// One selectable chip: the word as it appears, plus its span in the source transcript (as
    /// UTF-16 offsets, matching the C# `int` char-index semantics of `Token.Start`/`Length`).
    struct Token: Equatable {
        let text: String
        let start: Int
        let length: Int
    }

    /// The contiguous run of chips currently selected, or empty (`first < 0`) when nothing is
    /// picked. Contiguous because a dictionary pattern is a phrase: a gapped selection could not
    /// describe anything the matcher is able to find.
    struct WordRange: Equatable {
        let first: Int
        let last: Int

        static let none = WordRange(first: -1, last: -1)

        var isEmpty: Bool { first < 0 }
    }

    /// Applies one plain click at `index` to the selected word range.
    ///
    /// A plain click grows the phrase rather than replacing it. Joining words the recognizer split
    /// apart, "V B D" into "VBD", is the single most common reason the popup gets opened.
    ///
    /// Growth is limited to a word touching the range. Spanning to an arbitrary click would let one
    /// stray click swallow a whole sentence, and the words in between were never chosen. Clicking
    /// inside the phrase takes words back out of it, so an over-extension can be corrected without
    /// starting the selection again.
    static func toggle(_ current: WordRange, index: Int) -> WordRange {
        guard index >= 0 else { return current }

        if current.isEmpty {
            return WordRange(first: index, last: index)
        }

        let (first, last) = (current.first, current.last)

        if index == first - 1 {
            return WordRange(first: index, last: last)
        }

        if index == last + 1 {
            return WordRange(first: first, last: index)
        }

        if index >= first && index <= last {
            if first == last {
                return .none
            }

            if index == first {
                return WordRange(first: first + 1, last: last)
            }

            if index == last {
                return WordRange(first: first, last: last - 1)
            }

            // Clicking the middle of a phrase is not an unpick of one word: it would split the
            // range in two, and only one of the halves can survive. Collapsing to the clicked word
            // is the reading that keeps what the user actually pointed at.
            return WordRange(first: index, last: index)
        }

        return WordRange(first: index, last: index)
    }

    enum PlanKind {
        /// Nothing worth saving; `Plan.entry` is nil.
        case invalid
        /// No rule for this spoken form yet.
        case create
        /// A rule for this spoken form exists and would be rewritten.
        case update
        /// The rule already exists exactly as typed; saving would be a no-op.
        case noChange
    }

    /// What saving would do, plus the entry to persist and a sentence to show the user. `entry` is
    /// nil only for `.invalid`.
    struct Plan {
        let kind: PlanKind
        let entry: DictionaryEntry?
        let message: String

        /// True when there is something to write to the repository.
        var canSave: Bool { kind == .create || kind == .update }
    }

    /// Splits a transcript into whitespace-delimited chips. Punctuation stays attached to its word
    /// so the chips read exactly like the dictation the user is looking at; it is stripped later,
    /// when a selection is turned into a pattern.
    static func tokenize(_ transcript: String?) -> [Token] {
        guard let transcript, !transcript.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return []
        }

        var tokens: [Token] = []
        let scalars = Array(transcript.unicodeScalars)
        var i = 0
        while i < scalars.count {
            if CharacterSet.whitespacesAndNewlines.contains(scalars[i]) {
                i += 1
                continue
            }

            let start = i
            while i < scalars.count && !CharacterSet.whitespacesAndNewlines.contains(scalars[i]) {
                i += 1
            }

            let text = String(String.UnicodeScalarView(scalars[start..<i]))
            tokens.append(Token(text: text, start: start, length: i - start))
        }

        return tokens
    }

    /// Rebuilds the text covered by chips `first` through `last` inclusive. The indices may arrive
    /// in either order (dragging right-to-left) and are clamped, so a stale selection from a
    /// previous transcript can never throw.
    ///
    /// The span is taken from the original transcript rather than by joining chip text, which keeps
    /// the punctuation *between* selected words intact. Whitespace runs collapse to single spaces so
    /// a selection spanning a line break still produces a pattern the post-processor can match.
    static func select(_ transcript: String?, tokens: [Token], first: Int, last: Int) -> String {
        guard let transcript, !transcript.isEmpty, !tokens.isEmpty else {
            return ""
        }

        var (first, last) = first > last ? (last, first) : (first, last)
        first = min(max(first, 0), tokens.count - 1)
        last = min(max(last, 0), tokens.count - 1)

        let scalars = Array(transcript.unicodeScalars)
        let start = tokens[first].start
        let end = tokens[last].start + tokens[last].length
        guard start >= 0, end <= scalars.count, end > start else {
            return ""
        }

        let raw = collapse(String(String.UnicodeScalarView(scalars[start..<end])))
        let trimmed = raw.trimmingCharacters(in: edgePunctuation).trimmingCharacters(in: .whitespaces)

        // Selecting only punctuation would otherwise clear the box and look broken. Hand back what
        // was actually selected and let the user decide.
        return trimmed.isEmpty ? raw : trimmed
    }

    /// Works out what saving the typed rule would do against the current dictionary. Matching is
    /// by spoken form, case-insensitive, mirroring the duplicate rule the settings tab enforces so
    /// a quick add can never create a row the dictionary tab would immediately flag.
    static func build(
        pattern: String?,
        replacement: String?,
        wholeWord: Bool,
        existing: [DictionaryEntry]
    ) -> Plan {
        let spoken = (pattern ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !spoken.isEmpty else {
            return Plan(kind: .invalid, entry: nil, message: "Pick a word above, or type what Scribe wrote.")
        }

        // A pattern spanning a line break can never match. The matcher preserves CR/LF in its
        // input and compiles patterns literally, so the break would have to reappear in exactly
        // the same place in a future dictation. Rejecting it beats saving a rule that silently
        // never fires.
        // Checked at the Unicode scalar level rather than via `String.contains`: Swift merges a
        // "\r\n" pair into a single extended grapheme cluster, so a Character-level check for a
        // lone "\n" or "\r" silently misses CRLF-delimited transcripts entirely.
        if spoken.unicodeScalars.contains("\n") || spoken.unicodeScalars.contains("\r") {
            return Plan(
                kind: .invalid,
                entry: nil,
                message: "Pick words from a single line. A rule can't stretch across a line break.")
        }

        let written = (replacement ?? "").trimmingCharacters(in: .whitespacesAndNewlines)

        // A rule that rewrites text to itself never fires, and is almost always a half-finished
        // edit rather than an intent. Case-only differences ("copilot" to "Copilot") are the
        // single most common real rule, so the comparison is ordinal.
        if spoken == written {
            return Plan(kind: .invalid, entry: nil, message: "That is already what Scribe writes, so nothing would change.")
        }

        // The dictionary runs in a single pass: every rule matches the original transcript, and no
        // rule ever sees another rule's output. So a pattern that is some other rule's replacement
        // is unreachable by construction. This is easy to walk into, because the transcript shown
        // in the popup is the finished text, which is exactly where those replacements appear.
        let producer = existing.first {
            $0.enabled
                && $0.replacement.trimmingCharacters(in: .whitespacesAndNewlines).caseInsensitiveCompare(spoken) == .orderedSame
                && $0.pattern.trimmingCharacters(in: .whitespacesAndNewlines).caseInsensitiveCompare(spoken) != .orderedSame
        }

        if let producer {
            let producerPattern = producer.pattern.trimmingCharacters(in: .whitespacesAndNewlines)
            return Plan(
                kind: .invalid,
                entry: nil,
                message: "\"\(producerPattern)\" is already turned into \"\(spoken)\" by another rule, and rules "
                    + "only run once, so this would never apply. Change that rule's replacement instead.")
        }

        let match = existing.first {
            $0.pattern.trimmingCharacters(in: .whitespacesAndNewlines).caseInsensitiveCompare(spoken) == .orderedSame
        }

        if let match {
            let matchReplacement = match.replacement.trimmingCharacters(in: .whitespacesAndNewlines)
            if matchReplacement == written && match.wholeWord == wholeWord && match.enabled {
                return Plan(kind: .noChange, entry: match, message: "\"\(spoken)\" already becomes \"\(describe(written))\".")
            }

            // Re-enable on update: the user is explicitly asking for this rule right now, so a
            // previously disabled row should start working rather than silently stay off.
            let updated = DictionaryEntry(id: match.id, pattern: spoken, replacement: written, wholeWord: wholeWord, enabled: true)
            return Plan(
                kind: .update,
                entry: updated,
                message: "Replaces the existing rule: \"\(spoken)\" becomes \"\(describe(written))\" "
                    + "instead of \"\(describe(matchReplacement))\".")
        }

        let created = DictionaryEntry(id: 0, pattern: spoken, replacement: written, wholeWord: wholeWord, enabled: true)
        return Plan(
            kind: .create,
            entry: created,
            // Future tense, deliberately. This runs on every keystroke while the user is still
            // typing, so a past-tense message would announce a save that has not happened and let
            // them close the window believing the rule was stored.
            message: written.isEmpty
                ? "Scribe will leave \"\(spoken)\" out of what you dictate."
                : "Scribe will write \"\(spoken)\" as \"\(written)\".")
    }

    private static func describe(_ written: String) -> String {
        written.isEmpty ? "nothing" : written
    }

    /// Applies one just-saved rule to a transcript the user has already seen, so the copy kept for
    /// clipboard recovery reads the way they just told Scribe it should read.
    ///
    /// Only this rule is applied, never the whole dictionary. The transcript is already
    /// post-dictionary text, so re-running every rule could rewrite words the user never touched.
    ///
    /// Delegates to `TextPostProcessor.applyRule` on purpose, for the same reason Windows delegates
    /// to `TextPostProcessor.ApplyRule`: a private copy of the matcher drifts silently and would
    /// hand the user a "corrected" transcript that disagrees with what their very next dictation
    /// actually produces.
    static func apply(_ transcript: String?, entry: DictionaryEntry?) -> String {
        TextPostProcessor.applyRule(transcript, entry: entry)
    }

    /// Collapses horizontal whitespace runs to a single space, deliberately preserving line breaks.
    /// Mirrors `TextPostProcessor`'s own whitespace normalization, which collapses only horizontal
    /// whitespace and leaves CR/LF intact. Flattening line breaks here instead would build a
    /// space-separated pattern that can never match the text the matcher actually sees, producing a
    /// rule that silently never fires. Keeping them lets `build(...)` detect and reject the
    /// selection instead.
    private static func collapse(_ value: String) -> String {
        var result = ""
        var pendingSpace = false

        for ch in value.unicodeScalars {
            if ch == "\r" || ch == "\n" {
                pendingSpace = false
                result.unicodeScalars.append(ch)
                continue
            }

            if CharacterSet.whitespaces.contains(ch) {
                pendingSpace = !result.isEmpty
                continue
            }

            if pendingSpace {
                result.append(" ")
                pendingSpace = false
            }

            result.unicodeScalars.append(ch)
        }

        return result
    }
}
