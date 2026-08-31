import Foundation

/// Derives the dictionary patterns that a recognizer plausibly produces for a known-good term.
///
/// This exists because dictation history only ever records what the recognizer got *right*: it
/// is written after the dictionary has already run, so it can never show the misrecognition a
/// rule is supposed to repair. Mining it and emitting `lowercase(term) -> term` invents a
/// left-hand side that was never observed, which is how a dictionary fills up with rules that
/// provably do nothing.
///
/// So this only generates the two shapes Windows' re-decoding showed Parakeet genuinely
/// produces: spelling an unknown acronym out letter by letter ("C L I", "M C P"), and splitting a
/// closed compound ("co pilot", "power platform", "second brain"). Both are recoverable because
/// the pattern differs from the term only in case and spacing, which also makes them safe:
/// neither "c s u" nor "co pilot" occurs in ordinary prose, so a false positive cannot corrupt
/// normal text. Anything needing a real phonetic guess is not derivable and must come from the
/// user correcting a dictation. Mirrors `Scribe.Core.PostProcessing.DictionaryTermVariants`.
enum DictionaryTermVariants {
    // Two-letter acronyms are excluded on purpose: the spelled form of one is a pair of single
    // letters ("a i"), which collides with the article "a" and the pronoun "I" in ordinary prose.
    private static let minAcronymLetters = 3

    // Splitting a compound must not produce a pattern made of ordinary words, or the rule stops
    // being a rendering fix and starts rewriting prose.
    private static let commonWords: Set<String> = [
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "do", "for", "from", "go", "had",
        "has", "have", "he", "her", "his", "i", "if", "in", "is", "it", "its", "me", "my", "no",
        "not", "of", "on", "or", "our", "out", "she", "so", "that", "the", "their", "them", "then",
        "there", "they", "this", "to", "up", "us", "was", "we", "were", "what", "when", "which",
        "who", "will", "with", "would", "you", "your",
    ]

    /// The patterns worth adding for `term`, or an empty list when none is recoverable. Every
    /// returned pattern differs from the term only in case and spacing.
    static func variants(for term: String) -> [String] {
        let trimmed = term.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else {
            return []
        }

        var results: [String] = []
        if let spelled = spellOutAcronym(trimmed) {
            results.append(spelled)
        }
        if let split = splitCompound(trimmed) {
            results.append(split)
        }
        return results
    }

    /// "CSU" -> "c s u": the recognizer spells out acronyms it has no token for.
    /// Pattern: `^\.?[A-Z0-9]{2,8}$`
    private static func spellOutAcronym(_ term: String) -> String? {
        var body = Substring(term)
        if body.first == "." {
            body.removeFirst()
        }
        guard (2...8).contains(body.count),
              body.allSatisfy({ ($0.isASCII && $0.isUppercase && $0.isLetter) || $0.isNumber })
        else {
            return nil
        }
        guard body.count >= minAcronymLetters else {
            return nil
        }
        return body.map { String($0).lowercased() }.joined(separator: " ")
    }

    /// "WebIQ" -> "web iq": the recognizer hears a closed compound as separate words.
    /// Pattern: `^\.?[A-Za-z]*[a-z][A-Z][A-Za-z]*$`
    private static func splitCompound(_ term: String) -> String? {
        var body = Substring(term)
        if body.first == "." {
            body.removeFirst()
        }
        guard matchesCamelHump(body) else {
            return nil
        }

        let words = splitOnHumps(String(body))
        guard words.count >= 2, words.allSatisfy({ $0.count >= 2 }) else {
            return nil
        }

        // "AndThen" -> "and then" would rewrite ordinary prose, so a compound made of everyday
        // words is not a safe rendering fix even though it is shaped like one.
        if words.contains(where: { commonWords.contains($0.lowercased()) }) {
            return nil
        }

        let pattern = words.map { $0.lowercased() }.joined(separator: " ")
        return pattern.caseInsensitiveCompare(term) == .orderedSame ? nil : pattern
    }

    private static func matchesCamelHump(_ s: Substring) -> Bool {
        guard !s.isEmpty, s.allSatisfy({ $0.isASCII && $0.isLetter }) else { return false }
        let chars = Array(s)
        for i in 1..<chars.count {
            if chars[i - 1].isLowercase, chars[i].isUppercase {
                return true
            }
        }
        return false
    }

    private static func splitOnHumps(_ term: String) -> [String] {
        let chars = Array(term)
        var words: [String] = []
        var start = 0

        for i in 1..<max(chars.count, 1) where i < chars.count {
            guard chars[i].isUppercase, chars[i - 1].isLowercase else {
                continue
            }
            words.append(String(chars[start..<i]))
            start = i
        }

        words.append(String(chars[start...]))
        return words
    }
}
