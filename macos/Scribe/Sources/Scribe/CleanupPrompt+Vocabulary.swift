import Foundation

extension CleanupPrompt {
    static let maxGlossaryTermChars = 100

    static func isVocabularyReplacement(_ replacement: String?) -> Bool {
        guard let replacement, !replacement.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return false
        }
        return replacement.count <= maxGlossaryTermChars && !LibraryTermLint.spansLines(replacement)
    }
}
