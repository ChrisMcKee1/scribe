import Foundation

struct TermHints: OptionSet, Equatable, Sendable {
    let rawValue: Int

    static let ordinaryWord = TermHints(rawValue: 1 << 0)
    static let wholeWordOff = TermHints(rawValue: 1 << 1)
    static let forcesLowercase = TermHints(rawValue: 1 << 2)
    static let longForGlossary = TermHints(rawValue: 1 << 3)
    static let multiLine = TermHints(rawValue: 1 << 4)
    static let irregularSpacing = TermHints(rawValue: 1 << 5)
}

enum LibraryTermLint {
    static let commonWords = Set([
        "il", "di", "la", "le", "les", "de", "du", "des", "un", "une", "et", "en", "au", "ce",
        "se", "si", "su", "da", "del", "che", "non", "per", "con", "una", "el", "los", "las",
        "es", "als", "das", "der", "die", "den", "und", "ist", "im", "am", "an", "zu", "so",
        "no", "na", "os", "as", "em", "ao", "ou", "je", "tu", "me", "te", "ne", "on", "ma",
        "a", "i", "an", "as", "at", "be", "by", "do", "go", "he", "if", "in", "is", "it", "me",
        "my", "no", "of", "on", "or", "so", "to", "up", "us", "we",
    ].map { $0.lowercased() })

    static let alwaysLowercaseNames = Set([
        "npm", "pnpm", "kubectl", "webpack", "pandas", "conda", "dbt", "htmx",
        "statsmodels", "torchvision", "torchaudio",
    ])

    static func check(_ values: TermValues) -> TermHints {
        let spoken = values.spoken
        let written = values.written
        var hints: TermHints = []

        if commonWords.contains(spoken.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()) {
            hints.insert(.ordinaryWord)
        }
        if !values.wholeWord {
            hints.insert(.wholeWordOff)
        }
        if forcesLowercase(spoken.trimmingCharacters(in: .whitespacesAndNewlines), written.trimmingCharacters(in: .whitespacesAndNewlines)) {
            hints.insert(.forcesLowercase)
        }
        if !CleanupPrompt.isVocabularyReplacement(written) {
            if written.count > CleanupPrompt.maxGlossaryTermChars {
                hints.insert(.longForGlossary)
            }
            if spansLines(written) {
                hints.insert(.multiLine)
            }
        }
        if !LibraryTermKey.isInCommitForm(spoken) {
            hints.insert(.irregularSpacing)
        }

        return hints
    }

    static func spansLines(_ written: String) -> Bool {
        let lineBreaks = CharacterSet(charactersIn: "\r\n\u{000B}\u{000C}\u{0085}\u{2028}\u{2029}")
        return written.unicodeScalars.contains { lineBreaks.contains($0) }
    }

    private static func forcesLowercase(_ spoken: String, _ written: String) -> Bool {
        guard !written.isEmpty,
            spoken.compare(written, options: [.caseInsensitive, .literal]) == .orderedSame,
            written == written.lowercased(),
            !alwaysLowercaseNames.contains(written)
        else {
            return false
        }

        for char in written {
            if String(char).uppercased() != String(char) {
                return true
            }
        }

        return false
    }
}
