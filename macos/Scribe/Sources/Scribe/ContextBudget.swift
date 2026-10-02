import Foundation

enum ContextBudget {
    static let assumedContextTokens = 4096
    static let offeredSizes = [8192, 16384, 32768, 65536, 131072]
    static let minimumSize = 2048
    static let maximumSize = 1_048_576
    static let chatTemplateTokens = 32
    static let readyingTranscriptTokens = 512
    static let readyingOutputTokens = 1152

    static func sanitize(_ contextTokens: Int) -> Int {
        guard contextTokens > 0 else {
            return 0
        }
        return min(max(contextTokens, minimumSize), maximumSize)
    }

    static func vocabularyTokens(
        _ contextTokens: Int,
        instructions: String,
        transcript: String,
        outputCeiling: Int
    ) -> Int {
        vocabularyTokensFor(
            contextTokens,
            instructions: instructions,
            worstRequestTextCost: requestTextCost(transcript, outputCeiling: outputCeiling))
    }

    static func readingVocabularyTokens(_ contextTokens: Int, instructions: String) -> Int {
        vocabularyTokensFor(
            contextTokens,
            instructions: instructions,
            worstRequestTextCost: readyingTranscriptTokens + readyingOutputTokens)
    }

    static func vocabularyRoom(_ contextTokens: Int, instructions: String) -> Int {
        readingVocabularyTokens(contextTokens, instructions: instructions)
    }

    private static func vocabularyTokensFor(
        _ contextTokens: Int,
        instructions: String,
        worstRequestTextCost: Int
    ) -> Int {
        max(
            Int.min,
            min(
                Int.max,
                contextTokens - chatTemplateTokens - TokenEstimate.prose(instructions) - worstRequestTextCost
                    - margin(contextTokens)))
    }

    private static func requestTextCost(_ transcript: String, outputCeiling: Int) -> Int {
        TokenEstimate.transcript(transcript) + max(0, outputCeiling)
    }

    private static func margin(_ contextTokens: Int) -> Int {
        max(64, contextTokens * 3 / 100)
    }
}
