import Foundation

/// Evidence gathered for a single dictionary term.
struct TermUsage: Equatable {
    let entry: DictionaryEntry
    /// Times the spoken form appears in history.
    let patternHits: Int
    /// Times the written form appears in history.
    let replacementHits: Int

    /// No trace of the term in either direction. A rule that fires erases its own pattern from the
    /// stored text, so the written form has to be checked too or working rules look dead.
    var unused: Bool { patternHits == 0 && replacementHits == 0 }
}

/// The result of scanning dictation history for dictionary decay.
struct DictionaryUsageReport: Equatable {
    let hasEnoughEvidence: Bool
    let transcriptsScanned: Int
    let wordsScanned: Int
    let termsExamined: Int
    let unusedEntries: [TermUsage]
    let summary: String

    /// Whether there is anything for the user to act on.
    var hasFindings: Bool { !unusedEntries.isEmpty }
}

/// Finds dictionary terms the user has no evidence of ever needing, so a dictionary that has
/// accumulated speculative entries can be pruned back.
///
/// This exists because dead terms are not free: once AI cleanup glossary injection lands on
/// macOS, the enabled dictionary will be rendered into the cleanup system prompt on every
/// dictation, capped for on-device models (80 terms on Windows, `CleanupPrompt.MaxGlossaryTermsLocal`).
/// Past that cap, terms the user never says actively displace the ones they do. Until then a term
/// turned off only stops being applied, so the summary claims nothing about a model.
///
/// **The inversion trap.** History stores the text that was actually typed, which is
/// *post*-dictionary. So a rule that does its job rewrites its own pattern out of the record:
/// asking "does this pattern appear in history?" answers *no* for precisely the hardest-working
/// rules. Deleting on that signal would delete the most valuable entries first. The only honest
/// signal is that **neither** the spoken form **nor** the written form has ever appeared, which
/// means the term is simply not in this user's vocabulary.
///
/// Every ambiguity resolves towards keeping a term. A false "still in use" costs the user one
/// glossary slot; a false "dead" costs them a rule they were relying on.
///
/// Only the base-entry analysis of Windows' `Scribe.Core.Settings.DictionaryUsageAnalyzer` is
/// ported. Windows also scores each enabled library as a whole (`ScoreLibrary`), so a library the
/// user never needs can be switched off; that part is not ported yet, so library terms are never
/// judged here.
enum DictionaryUsageAnalyzer {
    /// Dictations required before an "unused" verdict means anything.
    static let minimumTranscripts = 25

    /// Words required alongside the dictation count. Twenty-five two-word dictations are not a
    /// vocabulary sample, and without this a new user would be told to delete their whole dictionary.
    static let minimumWords = 1_500

    private static let wordLike: NSRegularExpression = {
        // swiftlint:disable:next force_try
        try! NSRegularExpression(pattern: "[\\p{L}\\p{N}][\\p{L}\\p{N}'\u{2019}\\-]*")
    }()

    /// Scores every base entry against the dictation corpus.
    static func analyze(
        transcripts: [String],
        baseEntries: [DictionaryEntry],
        minimumTranscripts: Int = minimumTranscripts,
        minimumWords: Int = minimumWords
    ) -> DictionaryUsageReport {
        let usable = transcripts.filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        let words = usable.reduce(0) { $0 + wordCount($1) }

        // Id 0 means the row exists only in the settings UI and has never been saved, so it cannot
        // possibly have shaped the history being searched. Judging it would let the scan offer to
        // delete an entry the user added seconds ago.
        let candidates = baseEntries.filter { $0.id != 0 && isMeasurable($0) }

        guard usable.count >= minimumTranscripts, words >= minimumWords else {
            return DictionaryUsageReport(
                hasEnoughEvidence: false,
                transcriptsScanned: usable.count,
                wordsScanned: words,
                termsExamined: candidates.count,
                unusedEntries: [],
                summary: "Not enough dictation history yet to safely recommend a cleanup. You have "
                    + "\(usable.count) of \(minimumTranscripts) dictations and about \(words) of "
                    + "\(minimumWords) words. Keep dictating and run this again.")
        }

        // One corpus, joined on newlines. A dictionary pattern can never usefully contain a newline
        // (the matcher's input preserves CR/LF), so joining cannot invent a match that spans two
        // unrelated dictations.
        let corpus = usable.joined(separator: "\n")

        let unused =
            candidates
            .map { score(corpus: corpus, entry: $0) }
            .filter { $0.unused }
            .sorted { $0.entry.pattern.localizedCaseInsensitiveCompare($1.entry.pattern) == .orderedAscending }

        return DictionaryUsageReport(
            hasEnoughEvidence: true,
            transcriptsScanned: usable.count,
            wordsScanned: words,
            termsExamined: candidates.count,
            unusedEntries: unused,
            summary: describe(unusedCount: unused.count, transcripts: usable.count, examined: candidates.count))
    }

    /// Whether a term can be judged at all.
    ///
    /// A removal rule (`"um" -> ""`) is genuinely **unmeasurable**. When it fires it deletes its
    /// own pattern from the stored text, and it has no written form to look for instead, so a
    /// working rule and a dead one leave byte-identical evidence. These are also free: an entry
    /// with an empty replacement is skipped when the glossary is built, so retiring one saves
    /// nothing. Unmeasurable and worthless to remove means it must never be proposed.
    static func isMeasurable(_ entry: DictionaryEntry) -> Bool {
        !entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && !entry.replacement.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// Counts the evidence for one term in both directions.
    static func score(corpus: String, entry: DictionaryEntry) -> TermUsage {
        // The pattern is searched exactly as the matcher compiles it, so "would this ever have
        // matched" is answered by the real rules rather than an approximation of them.
        let patternHits = count(corpus: corpus, term: entry.pattern, wholeWord: entry.wholeWord)

        // The replacement deliberately does NOT inherit the pattern's word-boundary flag. The
        // matcher only ever applies boundaries to the pattern, and the written form can land
        // somewhere boundaries would reject: "comma" -> "," produces "hello, world", where the
        // comma follows a word character. Searching that with boundaries would report a rule that
        // fires constantly as dead.
        let replacementHits =
            entry.replacement.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? 0
            : count(corpus: corpus, term: entry.replacement, wholeWord: false)

        return TermUsage(entry: entry, patternHits: patternHits, replacementHits: replacementHits)
    }

    private static func wordCount(_ text: String) -> Int {
        wordLike.numberOfMatches(in: text, range: NSRange(text.startIndex..., in: text))
    }

    private static func count(corpus: String, term: String, wholeWord: Bool) -> Int {
        let trimmed = term.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, !corpus.isEmpty else {
            return 0
        }

        // Mirrors TextPostProcessor.CompiledRule. Diverging here would report a term as dead that
        // the matcher can still fire, which is the one error this feature must not make.
        let escaped = NSRegularExpression.escapedPattern(for: trimmed)
        let pattern = wholeWord ? "(?<!\\w)\(escaped)(?!\\w)" : escaped
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else {
            return 0
        }
        return regex.numberOfMatches(in: corpus, range: NSRange(corpus.startIndex..., in: corpus))
    }

    /// Only what the scan found. Windows adds that turning the terms off frees room in the dictionary list its local
    /// cleanup models are sent; macOS sends a cleanup model no dictionary terms, so that would not be true here.
    private static func describe(unusedCount: Int, transcripts: Int, examined: Int) -> String {
        guard unusedCount > 0 else {
            return "Every term in your dictionary turned up in your last \(transcripts) dictations. "
                + "Nothing to clean up."
        }

        let terms = examined == 1 ? "term" : "terms"
        return "Checked \(examined) \(terms) against your last \(transcripts) dictations. "
            + "\(unusedCount) of your own entries did not appear."
    }
}
