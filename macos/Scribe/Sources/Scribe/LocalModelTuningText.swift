import Foundation

enum LocalModelTuningText {
    static let tuningSummary =
        "How much the model reads at once, and how much of your vocabulary it gets. Most people never need to "
        + "change these."
    static let wholeVocabularyTitle = "Send your whole vocabulary when it fits"

    private static let wholeVocabularyWhat =
        "Sends every word from your dictionary and the word packs AI cleanup may use, not just the ones a dictation "
        + "seems to mention, when they fit with your dictation. A long list can confuse a small model, "

    static let appWholeVocabularyHint =
        wholeVocabularyWhat + "and the model reads it again each time it loads."
    static let foundryWholeVocabularyHint =
        wholeVocabularyWhat + "and Foundry Local reads it again for every dictation."
    static let contextSizeTitle = "Context size"
    static let ollamaContextSizeHint =
        "How much the model reads at once, in tokens of about 3 or 4 characters. A larger size fits more of your "
        + "vocabulary and can use more memory. With a size chosen, Ollama reloads the model whenever another app "
        + "uses it at a different size. Ollama's setting leaves it to Context length in Ollama's settings."
    static let lmStudioContextSizeHint =
        "How much the model reads at once, in tokens of about 3 or 4 characters. With a size chosen, Scribe loads "
        + "the model at it and frees it after the idle time. A model you loaded yourself in LM Studio keeps its "
        + "own size."

    static func contextSizes(_ appName: String, stored: Int = 0) -> [(tokens: Int, label: String)] {
        var sizes: [(tokens: Int, label: String)] = [(0, "\(appName)'s setting")]
        var offered = ContextBudget.offeredSizes
        if stored > 0 && !offered.contains(stored) {
            offered.append(stored)
            offered.sort()
        }
        sizes.append(contentsOf: offered.map { ($0, sizeLabel($0)) })
        return sizes
    }

    static func sizeLabel(_ tokens: Int) -> String {
        if tokens.isMultiple(of: 1024) {
            return "\(tokens / 1024)K (\(count(tokens)) tokens)"
        }
        return "\(count(tokens)) tokens"
    }

    static func contextStatus(
        _ appName: String,
        inUse: Int,
        asked: Int,
        vocabularyTokens: Int,
        vocabularyRoom: Int? = nil
    ) -> String {
        let context: String
        if inUse > 0 {
            context = "The model is reading up to \(count(inUse)) tokens."
        } else if asked > 0 {
            context = "Scribe asks for \(count(asked)) tokens when the model loads."
        } else {
            context = "\(appName) sets the size when it loads the model."
        }

        guard vocabularyTokens > 0 else {
            return context + " AI cleanup has no vocabulary to send."
        }

        let needs = "\(context) Your whole vocabulary needs about \(count(vocabularyTokens)) tokens"
        switch vocabularyRoom {
        case .none:
            return needs + "."
        case let room? where room >= vocabularyTokens:
            return needs + ", which fits at this size."
        case let room? where room > 0:
            return needs + "; at this size, about \(count(room)) of them fit beside a dictation."
        default:
            return needs + "; at this size, none of it fits beside a dictation."
        }
    }

    private static func count(_ value: Int) -> String {
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        return formatter.string(from: NSNumber(value: value)) ?? String(value)
    }
}
