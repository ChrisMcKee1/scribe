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

    init(glossaryEntries: [DictionaryEntry]) {
        self.glossaryEntries = glossaryEntries
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
}
