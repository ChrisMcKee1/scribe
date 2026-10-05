import Foundation

/// Splits only at whitespace and keeps each boundary verbatim, so an echoed plan reconstructs the input exactly.
struct LocalCleanupPlan: Sendable {
    static let answerTimeLimit: TimeInterval = 30

    struct Segment: Sendable, Equatable {
        let text: String
        let separator: String
    }

    let segments: [Segment]

    static func make(text: String, instructions: String, contextTokens: Int) throws -> Self {
        if fits(text, instructions: instructions, contextTokens: contextTokens) {
            return Self(segments: [Segment(text: text, separator: "")])
        }
        var size = 2400
        while true {
            if let segments = split(text, maximumCharacters: size),
                segments.allSatisfy({ fits($0.text, instructions: instructions, contextTokens: contextTokens) })
            {
                return Self(segments: segments)
            }
            guard size > 300 else { throw CleanupProviderError.localRequestTooLarge }
            size = max(300, size / 2)
        }
    }

    private static func fits(_ text: String, instructions: String, contextTokens: Int) -> Bool {
        ContextBudget.requestFits(
            CleanupRequest(
                transcript: CleanupPrompt.wrapTranscript(text), writingStylePrompt: instructions,
                maxOutputTokens: ContextBudget.cleanupOutputCeiling(text)),
            contextTokens: contextTokens)
    }

    private static func split(_ text: String, maximumCharacters: Int) -> [Segment]? {
        var segments: [Segment] = []
        var start = text.startIndex
        while text.distance(from: start, to: text.endIndex) > maximumCharacters {
            let end = text.index(start, offsetBy: maximumCharacters)
            guard var boundary = text[start..<end].lastIndex(where: { $0.isWhitespace }) else { return nil }
            while boundary > start, text[text.index(before: boundary)].isWhitespace {
                boundary = text.index(before: boundary)
            }
            guard boundary > start else { return nil }
            var separatorEnd = boundary
            while separatorEnd < text.endIndex, text[separatorEnd].isWhitespace {
                separatorEnd = text.index(after: separatorEnd)
            }
            segments.append(
                Segment(text: String(text[start..<boundary]), separator: String(text[boundary..<separatorEnd])))
            start = separatorEnd
        }
        if start < text.endIndex {
            segments.append(Segment(text: String(text[start...]), separator: ""))
        }
        return segments.isEmpty ? nil : segments
    }
}
