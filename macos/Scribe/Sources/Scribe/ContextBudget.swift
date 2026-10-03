import Foundation

enum ContextBudget {
    static let assumedContextTokens = 4096
    static let offeredSizes = [8192, 16384, 32768, 65536, 131072]
    static let minimumSize = 2048
    static let maximumSize = 1_048_576
    static let chatTemplateTokens = 32
    static let readyingTranscriptTokens = 512
    static let readyingOutputTokens = 1152
    static let auxiliaryMinimumOutputTokens = 512

    static func planningContext(_ state: LocalServerState, model: String, ceiling: Int) throws -> Int {
        guard state.reach == .reached else { throw CleanupProviderError.localContextUnknown }
        guard let held = state.loaded(for: model) else { return ceiling }
        guard held.contextTokens > 0 else { throw CleanupProviderError.localContextUnknown }
        return min(ceiling, held.contextTokens)
    }

    static func cleanupOutputCeiling(_ text: String) -> Int {
        let words = text.split(whereSeparator: { $0.isWhitespace }).count
        let wordEstimate = Int(Double(words) * 2.5)
        let textEstimate = Int(ceil(Double(TokenEstimate.transcript(text)) * 1.25))
        return min(max(max(wordEstimate, textEstimate) + 128, 64), 4096)
    }

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

    static func requestFits(_ request: CleanupRequest, contextTokens: Int) -> Bool {
        guard let room = outputRoom(request, contextTokens: contextTokens) else { return false }
        return max(0, request.maxOutputTokens ?? 4096) <= room
    }

    static func fitAuxiliary(_ request: CleanupRequest, contextTokens: Int) throws -> CleanupRequest {
        guard let room = outputRoom(request, contextTokens: contextTokens) else {
            throw CleanupProviderError.localRequestTooLarge
        }
        let output = min(request.maxOutputTokens ?? 4096, room)
        guard output >= auxiliaryMinimumOutputTokens else {
            throw CleanupProviderError.localRequestTooLarge
        }
        return CleanupRequest(
            transcript: request.transcript, writingStylePrompt: request.writingStylePrompt,
            singleLineMode: request.singleLineMode, timeout: request.timeout, maxOutputTokens: output)
    }

    private static func outputRoom(_ request: CleanupRequest, contextTokens: Int) -> Int? {
        let room = contextTokens - chatTemplateTokens - margin(contextTokens)
        guard room >= 0 else { return nil }
        let instructions = TokenEstimate.vocabulary(request.writingStylePrompt)
        let transcript = TokenEstimate.transcript(request.transcript)
        guard instructions <= room, transcript <= room - instructions else { return nil }
        return room - instructions - transcript
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
                contextTokens - chatTemplateTokens - TokenEstimate.vocabulary(instructions + "\n\n")
                    - worstRequestTextCost
                    - margin(contextTokens)))
    }

    private static func requestTextCost(_ transcript: String, outputCeiling: Int) -> Int {
        TokenEstimate.transcript(transcript) + max(0, outputCeiling)
    }

    private static func margin(_ contextTokens: Int) -> Int {
        max(64, contextTokens * 3 / 100)
    }
}
