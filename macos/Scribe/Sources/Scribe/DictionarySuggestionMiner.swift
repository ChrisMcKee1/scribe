import Foundation

/// Mines recent dictation history for recurring jargon worth adding to the user dictionary: the
/// pragmatic version of "auto-learning": Scribe never sees the user's manual corrections after
/// injection, but it can spot the technical terms they keep saying and offer to lock their
/// spelling in. Deliberately high-precision patterns only (acronyms, CamelCase, digit-words like
/// K8s), because a noisy suggestion list is worse than none. Mirrors
/// `Scribe.Core.PostProcessing.DictionarySuggestionMiner` on Windows.
enum DictionarySuggestionMiner {
    /// A recurring term and how many distinct dictations it appeared in.
    struct Suggestion: Equatable {
        let term: String
        let dictations: Int
    }

    // Boring capitalized tokens that clear the acronym bar but aren't vocabulary.
    private static let stoplist: Set<String> = ["OK", "AM", "PM", "TODO", "FYI", "ASAP", "LOL"]

    /// Returns suggested dictionary entries: terms matching a jargon pattern that occur in at
    /// least `minDictations` distinct dictations and aren't already covered by the dictionary.
    /// Ordered by frequency, capped at `maxSuggestions`.
    static func mine(
        entries: [DictationHistoryRecord],
        existing: [DictionaryEntry],
        minDictations: Int = 3,
        maxSuggestions: Int = 12
    ) -> [Suggestion] {
        var known = Set<String>()
        for entry in existing {
            known.insert(entry.pattern.trimmingCharacters(in: .whitespaces).lowercased())
            let replacement = entry.replacement.trimmingCharacters(in: .whitespaces)
            if !replacement.isEmpty {
                known.insert(replacement.lowercased())
            }
        }

        // term (lowercased key) -> (per-surface-form counts, dictation count)
        var counts: [String: [String: Int]] = [:]
        var dictations: [String: Int] = [:]

        for history in entries {
            guard let text = history.transcriptText, !text.isEmpty else {
                continue
            }

            var seenInThisDictation = Set<String>()
            for raw in text.split(whereSeparator: { $0.isWhitespace }) {
                let token = trimPunctuation(String(raw))
                let key = token.lowercased()
                if token.count < 2 || known.contains(key) || stoplist.contains(token) {
                    continue
                }

                if !isJargonShaped(token) {
                    continue
                }

                var forms = counts[key] ?? [:]
                forms[token, default: 0] += 1
                counts[key] = forms

                if seenInThisDictation.insert(key).inserted {
                    dictations[key, default: 0] += 1
                }
            }
        }

        return dictations
            .filter { $0.value >= minDictations }
            .map { key, dictationCount -> Suggestion in
                // Suggest the surface form the user's text uses most often.
                let forms = counts[key] ?? [:]
                let bestForm = forms
                    .sorted { lhs, rhs in
                        if lhs.value != rhs.value {
                            return lhs.value > rhs.value
                        }
                        return lhs.key < rhs.key
                    }
                    .first?.key ?? key
                return Suggestion(term: bestForm, dictations: dictationCount)
            }
            .sorted { lhs, rhs in
                if lhs.dictations != rhs.dictations {
                    return lhs.dictations > rhs.dictations
                }
                return lhs.term.lowercased() < rhs.term.lowercased()
            }
            .prefix(maxSuggestions)
            .map { $0 }
    }

    // High-precision "this is jargon" shapes; ordinary prose words match none of them.
    static func isJargonShaped(_ token: String) -> Bool {
        isAcronym(token) || isCamelHump(token) || isLetterDigit(token)
    }

    private static func trimPunctuation(_ token: String) -> String {
        // Trailing sentence punctuation always goes; leading quotes/brackets go but a leading dot
        // survives so ".NET" stays intact.
        var result = Substring(token)
        while let last = result.last, ",.!?;:)]}\"'".contains(last) {
            result.removeLast()
        }
        while let first = result.first, "([{\"'".contains(first) {
            result.removeFirst()
        }
        return String(result)
    }

    // ^\.?[A-Z]{2,8}$
    private static func isAcronym(_ token: String) -> Bool {
        var s = Substring(token)
        if s.first == "." {
            s.removeFirst()
        }
        guard (2...8).contains(s.count) else { return false }
        return s.allSatisfy { $0.isASCII && $0.isUppercase && $0.isLetter }
    }

    // ^\.?[A-Za-z]*[a-z][A-Z][A-Za-z]*$ : a lowercase->uppercase transition inside the word.
    private static func isCamelHump(_ token: String) -> Bool {
        var s = Substring(token)
        if s.first == "." {
            s.removeFirst()
        }
        guard !s.isEmpty, s.allSatisfy({ $0.isASCII && $0.isLetter }) else { return false }
        let chars = Array(s)
        for i in 1..<chars.count {
            if chars[i - 1].isLowercase, chars[i].isUppercase {
                return true
            }
        }
        return false
    }

    // ^[A-Za-z]+[0-9]+[A-Za-z0-9]*$ : letters and digits mixed in one token, starting with a letter.
    private static func isLetterDigit(_ token: String) -> Bool {
        let chars = Array(token)
        guard !chars.isEmpty, chars[0].isASCII, chars[0].isLetter else { return false }
        var i = 0
        while i < chars.count, chars[i].isASCII, chars[i].isLetter {
            i += 1
        }
        guard i > 0, i < chars.count else { return false }
        let digitsStart = i
        while i < chars.count, chars[i].isASCII, chars[i].isNumber {
            i += 1
        }
        guard i > digitsStart else { return false }
        while i < chars.count {
            guard chars[i].isASCII, (chars[i].isLetter || chars[i].isNumber) else { return false }
            i += 1
        }
        return true
    }
}
