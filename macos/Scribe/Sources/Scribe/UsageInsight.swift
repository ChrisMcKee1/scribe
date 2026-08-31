import Foundation

/// Builds the bounded aggregate-only payload for an explicit "Usage AI" request, and parses the
/// model's reply. A faithful Swift port of Windows' `Scribe.Core.Diagnostics.UsageInsight`.
enum UsageInsight {
    static let systemPrompt =
        "Describe only the supplied aggregate dictation-usage data. Identify recurring technical "
        + "domains and terminology in 2 to 4 factual sentences. Do not infer personality, mood, "
        + "sentiment, productivity, intent, or time saved. Do not judge the user. Do not invent "
        + "terms or facts that are not present. Return plain text only."

    /// Builds the payload sent to the user's configured AI endpoint. Guarantee: only terms with
    /// `covered == true` (dictionary-canonical labels) are ever included; novel mined tokens are
    /// verbatim words from the user's dictations and never enter the payload.
    static func buildSummary(_ snapshot: UsageAnalyzer.Snapshot, maxChars: Int = 4_000) -> String {
        guard maxChars > 0 else { return "" }

        var lines: [String] = []
        lines.append("Dictations: \(snapshot.dictations)")
        lines.append("Words: \(snapshot.words)")
        lines.append("Active days: \(snapshot.activeDays)")
        lines.append("Recurring terms:")
        for term in snapshot.terms {
            // Uncovered terms are raw tokens mined from dictation text (surnames, project
            // codenames); only dictionary-canonical labels may leave the machine.
            guard term.covered else { continue }
            lines.append("- \(term.text): \(term.dictations) dictations")
        }

        return truncate(lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines), maxChars: maxChars)
    }

    static func parse(_ response: String?, maxChars: Int = 1_200) -> String? {
        guard let response, !response.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, maxChars > 0 else {
            return nil
        }

        var value = response.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.hasPrefix("```") {
            let nsValue = value as NSString
            let firstLine = nsValue.range(of: "\n").location
            let lastFence = nsValue.range(of: "```", options: .backwards).location
            if firstLine != NSNotFound, lastFence != NSNotFound, lastFence > firstLine {
                let innerRange = NSRange(location: firstLine + 1, length: lastFence - firstLine - 1)
                value = nsValue.substring(with: innerRange).trimmingCharacters(in: .whitespacesAndNewlines)
            }
        }

        return truncate(value, maxChars: maxChars)
    }

    private static func truncate(_ value: String, maxChars: Int) -> String {
        let nsValue = value as NSString
        guard nsValue.length > maxChars else { return value }

        // Never cut between the halves of a surrogate pair: a trailing lone high surrogate is
        // invalid UTF-16 and can break downstream encoding of the request or the UI text.
        var cut = maxChars
        if cut > 0, cut < nsValue.length,
           CFStringIsSurrogateHighCharacter(nsValue.character(at: cut - 1)),
           CFStringIsSurrogateLowCharacter(nsValue.character(at: cut)) {
            cut -= 1
        }

        return nsValue.substring(to: cut).trimmingTrailingWhitespace()
    }
}

private extension String {
    /// Right-trim only (mirrors C#'s `string.TrimEnd()`), used after truncation so a cut that
    /// lands mid-word doesn't also strip meaningful leading content.
    func trimmingTrailingWhitespace() -> String {
        var result = Substring(self)
        while let last = result.last, last.isWhitespace {
            result.removeLast()
        }
        return String(result)
    }
}
