import Foundation

enum CleanupVocabularyMode: Sendable {
    case all
    case mentioned
    case none
}

/// The vocabulary one dictation's cleanup request may carry: the dictionary, composed with the word packs cleanup may
/// use, in the order the glossary fills its budget.
struct CleanupVocabulary: Sendable {
    static let none = CleanupVocabulary(glossaryEntries: [])

    let glossaryEntries: [DictionaryEntry]

    var wholeGlossaryTokens: Int {
        let lines = CleanupPrompt.glossaryLines(glossaryEntries)
        guard !lines.isEmpty else {
            return 0
        }
        return CleanupPrompt.glossaryHeaderTokens + CleanupPrompt.tokens(lines)
    }

    func glossary(maxTerms: Int, mode: CleanupVocabularyMode, dictation: String?) -> String? {
        switch mode {
        case .none:
            return nil
        case .all:
            let glossary = CleanupPrompt.buildGlossary(glossaryEntries, maxTerms: maxTerms)
            return glossary.isEmpty ? nil : glossary
        case .mentioned:
            guard let dictation, !dictation.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return nil
            }
            let glossary = CleanupPrompt.buildGlossary(
                VocabularyMentions.select(glossaryEntries, dictation),
                maxTerms: maxTerms)
            return glossary.isEmpty ? nil : glossary
        }
    }

    func glossary(
        mode: CleanupVocabularyMode,
        everything: Bool,
        dictation: String?,
        tokenBudget: Int,
        maxTerms: Int
    ) -> String? {
        guard mode != .none, !glossaryEntries.isEmpty else {
            return nil
        }

        let all = CleanupPrompt.glossaryLines(glossaryEntries)
        let lines: [GlossaryLineInfo]
        if mode == .all || dictation?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty != false {
            if mode != .all && !everything {
                return nil
            }
            lines = CleanupPrompt.takeWhileFits(
                all,
                room: tokenBudget - CleanupPrompt.glossaryHeaderTokens,
                maxTerms: maxTerms)
        } else {
            let mentioned = CleanupPrompt.glossaryLines(VocabularyMentions.select(glossaryEntries, dictation))
            lines = CleanupPrompt.fitGlossary(
                all,
                mentioned: mentioned,
                everything: everything,
                tokenBudget: tokenBudget,
                maxTerms: maxTerms)
        }

        guard !lines.isEmpty else {
            return nil
        }

        let glossary = CleanupPrompt.renderGlossary(lines)
        return glossary.isEmpty ? nil : glossary
    }
}
