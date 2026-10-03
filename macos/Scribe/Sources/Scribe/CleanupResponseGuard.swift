import Foundation

enum CleanupResponseGuardResult: Equatable {
    case accepted(String)
    case rejected(CleanupResponseGuard.RejectionReason)
}

enum CleanupResponseGuard {
    enum RejectionReason: String, Equatable {
        case emptyCandidate
        case emptyAfterStripping
        case overlongRamble
        case refusalLike
        case replyLike

        var logMessage: String {
            switch self {
            case .emptyCandidate, .emptyAfterStripping:
                return "AI cleanup produced an empty response after sanitization; using post-processed transcription."
            case .overlongRamble:
                return "AI cleanup rejected an over-long response; using post-processed transcription."
            case .refusalLike:
                return "AI cleanup rejected a refusal-like response; using post-processed transcription."
            case .replyLike:
                return "AI cleanup rejected a reply-like response; using post-processed transcription."
            }
        }
    }

    static func sanitize(candidate: String?, original: String) -> CleanupResponseGuardResult {
        guard let candidate, !candidate.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return .rejected(.emptyCandidate)
        }

        var cleaned = stripMatches(in: candidate, using: thinkBlock).trimmingCharacters(in: .whitespacesAndNewlines)
        cleaned = stripMatches(in: cleaned, using: leadingThinkTag).trimmingCharacters(in: .whitespacesAndNewlines)

        if !matches(announcementOpening, in: original) {
            cleaned = stripMatches(in: cleaned, using: rewriteAnnouncement)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            cleaned = stripMatches(in: cleaned, using: leadingSeparators)
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }
        if !matches(labelOpening, in: original) {
            cleaned = stripMatches(in: cleaned, using: rewriteLabel).trimmingCharacters(in: .whitespacesAndNewlines)
        }

        cleaned = stripWrappingTag(cleaned, original: original, regex: leadingWrapperTag, trailing: false)
        cleaned = stripWrappingTag(cleaned, original: original, regex: trailingWrapperTag, trailing: true)

        if cleaned.hasPrefix("```"), let firstNewline = cleaned.firstIndex(of: "\n") {
            cleaned = String(cleaned[cleaned.index(after: firstNewline)...])
            if cleaned.hasSuffix("```") {
                cleaned.removeLast(3)
            }

            cleaned = cleaned.trimmingCharacters(in: .whitespacesAndNewlines)
        }

        cleaned = CleanupPrompt.stripTranscriptTags(cleaned)
        cleaned = stripMatches(in: cleaned, using: trailingCommentary).trimmingCharacters(in: .whitespacesAndNewlines)

        if hasWrappingQuotePair(cleaned) && !hasMatchingOuterQuotes(original) {
            cleaned = String(cleaned.dropFirst().dropLast()).trimmingCharacters(in: .whitespacesAndNewlines)
            // A model that quotes its whole answer can quote the echoed delimiters with it, so they
            // only come into reach once the quotes are gone.
            cleaned = CleanupPrompt.stripTranscriptTags(cleaned)
            cleaned = stripWrappingTag(cleaned, original: original, regex: leadingWrapperTag, trailing: false)
            cleaned = stripWrappingTag(cleaned, original: original, regex: trailingWrapperTag, trailing: true)
        }

        guard !cleaned.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return .rejected(.emptyAfterStripping)
        }

        if Double(cleaned.count) > (Double(original.count) * 2.5) + 80.0 {
            return .rejected(.overlongRamble)
        }

        if looksLikeRefusal(cleaned) && !looksLikeRefusal(original) {
            return .rejected(.refusalLike)
        }

        if looksLikeInventedReply(cleaned, original: original) {
            return .rejected(.replyLike)
        }

        // Last, because the guards above compare the model's own answer with the text it was sent. The dashes the
        // writing style forbids come out of the model's prose here. The replacements that can hold the user's own dash
        // are made after the guard: the snippets', and those of every dictionary rule that is not vocabulary, which
        // includes each replacement with a dash (`TextPostProcessor.isVocabulary`). So a dash in the user's own text
        // survives.
        return .accepted(DashNormalizer.normalize(cleaned))
    }

    static func looksLikeRefusal(_ text: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return !trimmed.isEmpty && (matches(refusalPreamble, in: text) || matches(refusalInability, in: text))
    }

    static func looksLikeInventedReply(_ candidate: String?, original: String) -> Bool {
        guard let candidate, !candidate.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return false
        }

        if matches(replyOffer, in: candidate) && !matches(replyOffer, in: original) {
            return true
        }

        if matches(replyOpener, in: candidate) && !matches(affirmationAnywhere, in: original) {
            return true
        }

        let candidateWords = wordSet(candidate)
        if (1...3).contains(candidateWords.count)
            && !candidate.trimmingCharacters(in: .whitespacesAndNewlines).hasSuffix("?")
        {
            if looksLikeQuestion(original) {
                return true
            }

            let originalWords = wordSet(original)
            if originalWords.count >= 4 && !candidate.contains(where: \.isNumber) {
                let shared = candidateWords.filter { originalWords.contains($0) }.count
                if shared * 2 < candidateWords.count {
                    return true
                }
            }
        }

        return false
    }

    static func looksLikeQuestion(_ text: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return !trimmed.isEmpty && (trimmed.hasSuffix("?") || matches(questionOpener, in: text))
    }

    private static func hasMatchingOuterQuotes(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return hasWrappingQuotePair(trimmed)
    }

    private static func hasWrappingQuotePair(_ value: String) -> Bool {
        guard value.count >= 2, let first = value.first, let last = value.last else {
            return false
        }

        return (first == "\"" && last == "\"") || (first == "'" && last == "'")
    }

    private static func wordSet(_ text: String) -> Set<String> {
        let range = NSRange(text.startIndex..., in: text)
        return Set(
            wordToken.matches(in: text, options: [], range: range).compactMap { match in
                guard let range = Range(match.range, in: text) else { return nil }
                return text[range].lowercased()
            })
    }

    private static func matches(_ regex: NSRegularExpression, in text: String) -> Bool {
        let range = NSRange(text.startIndex..., in: text)
        return regex.firstMatch(in: text, options: [], range: range) != nil
    }

    private static func stripMatches(in text: String, using regex: NSRegularExpression) -> String {
        let range = NSRange(text.startIndex..., in: text)
        return regex.stringByReplacingMatches(in: text, options: [], range: range, withTemplate: "")
    }

    private static func stripWrappingTag(
        _ text: String,
        original: String,
        regex: NSRegularExpression,
        trailing: Bool
    ) -> String {
        let range = NSRange(text.startIndex..., in: text)
        guard let match = regex.firstMatch(in: text, options: [], range: range),
            let tagRange = Range(match.range(at: 1), in: text)
        else {
            return text
        }

        let tag = text[tagRange].lowercased()
        if original.localizedCaseInsensitiveContains("<\(tag)")
            || original.localizedCaseInsensitiveContains("</\(tag)")
        {
            return text
        }

        if trailing {
            guard let matchRange = Range(match.range, in: text) else {
                return text
            }
            return String(text[..<matchRange.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
        }

        guard let matchRange = Range(match.range, in: text) else {
            return text
        }
        return String(text[matchRange.upperBound...]).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static let thinkBlock = try! NSRegularExpression(
        pattern: #"<think>.*?</think>"#,
        options: [.caseInsensitive, .dotMatchesLineSeparators])

    private static let leadingThinkTag = try! NSRegularExpression(
        pattern: #"^\s*</?think>\s*"#,
        options: [.caseInsensitive])

    private static let rewriteAnnouncement = try! NSRegularExpression(
        pattern:
            #"^[ \t]*(?:\*\*)?(?:(?:sure|okay|ok|certainly|of course)[,!.]?[ \t]+)?"#
            + #"(?:here(?:'s|[ \t]+is|[ \t]+are)|below[ \t]+is)\b[^\r\n]{0,120}?\b"#
            + #"(?:rewrit\w*|revis\w*|clean\w*|correct\w*|edit\w*|polish\w*|version|transcript\w*"#
            + #"|text|dictation)\b"#
            + #"[^\r\n]{0,120}:[ \t]*(?:\*\*)?[ \t]*(?:\r?\n|\z)"#,
        options: [.caseInsensitive])

    private static let leadingSeparators = try! NSRegularExpression(
        pattern: #"^(?:[ \t]*(?:-{3,}|\*{3,}|_{3,})?[ \t]*\r?\n)+"#,
        options: [])

    private static let rewriteLabel = try! NSRegularExpression(
        pattern:
            #"^[ \t]*(?:\*\*)?(?:[\w-]+[ \t]+){0,2}?"#
            + #"(?:(?:rewrit|revis|clean|correct|edit|polish)[\w-]*[ \t]+(?:[\w-]+[ \t]+)??"#
            + #"(?:transcript|text|version|dictation)\w*|(?:transcript|text|version|dictation)\w*[ \t]+"#
            + #"(?:[\w-]+[ \t]+)??(?:rewrit|revis|clean|correct|edit|polish)[\w-]*|transcript)"#
            + #"[ \t]*(?:\*\*)?[ \t]*:[ \t]*(?:\*\*)?[ \t]*(?:\r?\n|\z)"#,
        options: [.caseInsensitive])

    private static let trailingCommentary = try! NSRegularExpression(
        pattern:
            #"\r?\n[ \t]*(?:-{3,}|\*{3,}|_{3,})[ \t]*\r?\n\s*(?:\*\*)?"#
            + #"(?:notes?\b|explanation\b|key (?:changes|corrections)\b|changes(?: made)?\b|summary of changes\b"#
            + #"|corrections\b|this (?:is the (?:final|cleaned|rewritten|revised|corrected) )?"#
            + #"(?:version|rewrite|rewritten|revision|revised|text|transcript)\b"#
            + #"|i(?:'ve| have)? (?:made|kept|removed|corrected|fixed|changed|rewrote|preserved|maintained)\b"#
            + #"|let me know\b|if (?:you|this) (?:need|want|still)\b)[\s\S]*$"#,
        options: [.caseInsensitive])

    private static let leadingWrapperTag = try! NSRegularExpression(
        pattern:
            #"^\s*(?:\*\*)?<(transcript|rewritten_transcript|rewritten_text|rewritten|text|output|answer|result"#
            + #"|corrected|cleaned|cleaned_text)>(?:\*\*)?\s*"#,
        options: [.caseInsensitive])

    private static let trailingWrapperTag = try! NSRegularExpression(
        pattern:
            #"\s*(?:\*\*)?</(transcript|rewritten_transcript|rewritten_text|rewritten|text|output|answer|result"#
            + #"|corrected|cleaned|cleaned_text)>(?:\*\*)?\s*$"#,
        options: [.caseInsensitive])

    private static let announcementOpening = try! NSRegularExpression(
        pattern: #"^\W*(?:\w+\W+){0,6}?(?:here|below)\b"#,
        options: [.caseInsensitive])

    private static let labelOpening = try! NSRegularExpression(
        pattern: #"^\W*(?:\w+\W+){0,6}?(?:rewrit|revis|clean|correct|edit|polish|transcript)"#,
        options: [.caseInsensitive])

    private static let refusalPreamble = try! NSRegularExpression(
        pattern:
            #"^\s*(?:i(?:'m| am)\s+(?:sorry|afraid)\b|i apologi[sz]e\b|my apologies\b|as an ai\b|"#
            + #"as a language model\b)"#,
        options: [.caseInsensitive])

    private static let refusalInability = try! NSRegularExpression(
        pattern:
            #"\b(?:can'?t|cannot|could\s*n'?t|unable to|not able to|won'?t|will not)\s+"#
            + #"(?:assist|help|comply|fulfil|fulfill|provide|process|complete|continue)\b"#,
        options: [.caseInsensitive])

    private static let replyOpener = try! NSRegularExpression(
        pattern:
            #"^\s*["']?\s*(?:yes|yeah|yep|yup|sure\s+thing|sure|absolutely|definitely|certainly|of\s+course|"#
            + #"no\s+problem|nope|nah|no|okay|ok|alright|all\s+right|indeed|agreed|understood|got\s+it|"#
            + #"sounds\s+good|will\s+do|affirmative|you\s+bet|my\s+pleasure)\b"#,
        options: [.caseInsensitive])

    private static let affirmationAnywhere = try! NSRegularExpression(
        pattern:
            #"\b(?:yes|yeah|yep|yup|sure|absolutely|definitely|certainly|of\s+course|no\s+problem|nope|nah|no|"#
            + #"okay|ok|alright|all\s+right|indeed|agreed|understood|got\s+it|sounds\s+good|will\s+do|"#
            + #"affirmative|you\s+bet)\b"#,
        options: [.caseInsensitive])

    private static let replyOffer = try! NSRegularExpression(
        pattern:
            #"\b(?:i\s+can\s+(?:help|assist)|i(?:'d|\s+would)\s+be\s+(?:happy|glad)\s+to|"#
            + #"(?:happy|glad)\s+to\s+(?:help|assist)|how\s+(?:can|may)\s+i\s+(?:help|assist)|"#
            + #"let\s+me\s+(?:help|assist)|i(?:'m|\s+am)\s+here\s+to\s+(?:help|assist)|"#
            + #"is\s+there\s+anything\s+else\s+i)\b"#,
        options: [.caseInsensitive])

    private static let questionOpener = try! NSRegularExpression(
        pattern:
            #"^\s*(?:who|what|what'?s|when|where|why|how|how'?s|which|whose|whom|do|does|did|is|are|am|was|were|"#
            + #"can|could|will|would|should|shall|may|might|have|has|had|must)\b"#,
        options: [.caseInsensitive])

    private static let wordToken = try! NSRegularExpression(
        pattern: #"[\p{L}\p{Nd}]+(?:'[\p{L}\p{Nd}]+)*"#)
}
