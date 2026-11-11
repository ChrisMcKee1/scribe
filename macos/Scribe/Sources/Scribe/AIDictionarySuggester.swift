import Foundation

/// Turns a sample of recent dictation into dictionary suggestions with the help of the user's AI model: a port of
/// Windows' `Scribe.Core.PostProcessing.AiDictionarySuggester` (0.5.18). Where `DictionarySuggestionMiner` only spots
/// repeated, already correctly spelled jargon, this asks the model to reason about the surrounding context and how each
/// term is spoken versus how it should be written. Building the sample and reading the answer are pure; the request,
/// with its instructions (`AIDictionarySuggestionModel.systemPrompt`), is made by `AIDictionarySuggestionModel` through
/// the admitted one-off completion.
enum AIDictionarySuggester {
    /// Characters of history sent with one request, to bound cost and time.
    static let defaultMaxSampleChars = 6_000

    /// Suggestions accepted from one answer.
    static let defaultMaxSuggestions = 25

    /// Dictations read to build the sample, newest first, as Windows reads `GetRecent(1000)`.
    static let historyLimit = 1_000

    // One oversized term is data, not a rule; capped so one bad item cannot become a rule.
    static let maxTermChars = 80
    private static let maxScanCandidates = 256

    /// The user message: the most recent distinct dictations, newest first, one per line, bounded to `maxChars`
    /// characters. Repeats (ignoring case) are dropped so a phrase said often does not crowd out variety, and a first
    /// dictation that alone fills the budget is cut to it so the model still gets data. Empty when there is nothing
    /// to learn from.
    static func buildHistorySample(_ texts: [String], maxChars: Int = defaultMaxSampleChars) -> String {
        var seen = Set<String>()
        var sample = ""
        var length = 0
        for raw in texts {
            let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty, seen.insert(text.lowercased()).inserted else {
                continue
            }

            let count = text.count
            if length == 0 && count >= maxChars {
                return String(text.prefix(max(0, maxChars)))
            }

            if length + count + 1 > maxChars {
                break
            }

            if length > 0 {
                sample.append("\n")
                length += 1
            }
            sample.append(text)
            length += count
        }
        return sample
    }

    /// Reads the model's answer into dictionary entries (spoken form to written form), tolerating prose, code fences
    /// or a thinking block around the JSON array. Entries are cleaned, kept once per spoken form, filtered against
    /// `existing` and capped at `maxSuggestions`. Never throws; an answer that cannot be read gives nothing.
    ///
    /// Against `existing`: a spoken form a rule already writes the same way is a duplicate, while a different written
    /// form repairs that rule (the commit updates it in place). History keeps every dictation as it was typed,
    /// including the ones a rule was added to fix, so a model reading it can take the mistake for one of the user's
    /// terms and suggest writing it back ("m c b" as "MCB" beside "MCB" written as "MCP"). Such a suggestion is left
    /// out, unless it writes exactly what that rule writes. Only enabled rules count: a rule switched off no longer
    /// calls its spoken form a mistake.
    static func parseSuggestions(
        _ response: String?,
        existing: [DictionaryEntry],
        maxSuggestions: Int = defaultMaxSuggestions
    ) -> [DictionaryEntry] {
        var knownWrittenBySpoken: [String: String] = [:]
        var fixedAsByMistake: [String: String] = [:]
        for entry in existing {
            let pattern = normalize(entry.pattern)
            guard !pattern.isEmpty else {
                continue
            }
            let key = pattern.lowercased()
            let replacement = normalize(entry.replacement)
            if knownWrittenBySpoken[key] == nil {
                knownWrittenBySpoken[key] = replacement
            }
            if entry.enabled, fixedAsByMistake[key] == nil {
                fixedAsByMistake[key] = replacement
            }
        }

        guard maxSuggestions > 0, let raw = deserializeSuggestions(response), !raw.isEmpty else {
            return []
        }

        var results: [DictionaryEntry] = []
        var takenSpoken = Set<String>()
        for item in raw {
            let spoken = normalize(item?.spoken)
            let written = normalize(item?.written)
            guard !spoken.isEmpty, !written.isEmpty,
                spoken.count <= maxTermChars, written.count <= maxTermChars
            else {
                continue
            }

            // Identical spoken and written forms are a no-op; a spoken form a rule already writes this way is a
            // duplicate; a written form an enabled rule rewrites into something else is that rule's mistake, while
            // one it writes exactly (a casing rule's own spelling) is fine; a spoken form already taken is a repeat.
            let spokenKey = spoken.lowercased()
            if spoken == written || knownWrittenBySpoken[spokenKey] == written {
                continue
            }
            if let fixedAs = fixedAsByMistake[written.lowercased()], fixedAs != written {
                continue
            }
            guard takenSpoken.insert(spokenKey).inserted else {
                continue
            }

            results.append(DictionaryEntry(pattern: spoken, replacement: written))
            if results.count >= maxSuggestions {
                break
            }
        }
        return results
    }

    /// The key two spoken forms share when they name the same rule: cleaned as suggestions are, ignoring case.
    static func spokenKey(_ value: String) -> String {
        normalize(value).lowercased()
    }

    /// Flattens control characters and tabs (which could smuggle extra lines) to spaces, collapses runs of spaces and
    /// trims. Dictionary data, never instructions.
    static func normalize(_ value: String?) -> String {
        guard let value, value.contains(where: { !$0.isWhitespace }) else {
            return ""
        }

        var result = String.UnicodeScalarView()
        var lastWasSpace = false
        for scalar in value.unicodeScalars {
            let mapped: Unicode.Scalar =
                scalar.properties.generalCategory == .control || scalar == "\t" ? " " : scalar
            if mapped == " " {
                if lastWasSpace {
                    continue
                }
                lastWasSpace = true
            } else {
                lastWasSpace = false
            }
            result.append(mapped)
        }
        return String(result).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // The first JSON array of suggestions in the answer, ignoring prose, code fences or a thinking block (including
    // brackets such as "[1]" in a preamble or before a bare "</think>"), falling back to the individual
    // {"spoken":...,"written":...} objects when a model writes objects without a complete outer array.
    private static func deserializeSuggestions(_ response: String?) -> [RawSuggestion?]? {
        guard let response, response.contains(where: { !$0.isWhitespace }) else {
            return nil
        }

        let cleaned = Array(stripThinking(response).utf8)
        let decoder = JSONDecoder()
        decoder.allowsJSON5 = true

        var candidatesChecked = 0
        var searchFrom = 0
        while searchFrom < cleaned.count, candidatesChecked < maxScanCandidates {
            guard let start = cleaned[searchFrom...].firstIndex(of: UInt8(ascii: "[")) else {
                break
            }
            candidatesChecked += 1
            if let candidate = balancedSlice(cleaned, from: start, open: "[", close: "]"),
                let list = try? decoder.decode([RawSuggestion?].self, from: Data(candidate)),
                list.contains(where: { $0?.spoken != nil || $0?.written != nil })
            {
                return list
            }
            searchFrom = start + 1
        }

        var collected: [RawSuggestion?]?
        candidatesChecked = 0
        searchFrom = 0
        while searchFrom < cleaned.count, candidatesChecked < maxScanCandidates {
            guard let start = cleaned[searchFrom...].firstIndex(of: UInt8(ascii: "{")) else {
                break
            }
            candidatesChecked += 1
            if let candidate = balancedSlice(cleaned, from: start, open: "{", close: "}"),
                let single = try? decoder.decode(RawSuggestion.self, from: Data(candidate)),
                single.spoken != nil || single.written != nil
            {
                collected = (collected ?? []) + [single]
                searchFrom = start + candidate.count
                continue
            }
            searchFrom = start + 1
        }
        return collected
    }

    private static func stripThinking(_ response: String) -> String {
        guard let close = response.range(of: "</think>", options: [.caseInsensitive, .backwards]) else {
            return response
        }
        return String(response[close.upperBound...])
    }

    // Structural characters are ASCII, and no byte of a multi-byte UTF-8 sequence is, so scanning the UTF-8 bytes
    // finds exactly the brackets and quotes a JSON reader would.
    private static func balancedSlice(
        _ bytes: [UInt8], from start: Int, open: Unicode.Scalar, close: Unicode.Scalar
    ) -> ArraySlice<UInt8>? {
        let openByte = UInt8(ascii: open)
        let closeByte = UInt8(ascii: close)
        let quote = UInt8(ascii: "\"")
        let backslash = UInt8(ascii: "\\")
        var depth = 0
        var inString = false
        var escaped = false
        var index = start
        while index < bytes.count {
            let byte = bytes[index]
            if inString {
                if escaped {
                    escaped = false
                } else if byte == backslash {
                    escaped = true
                } else if byte == quote {
                    inString = false
                }
            } else if byte == quote {
                inString = true
            } else if byte == openByte {
                depth += 1
            } else if byte == closeByte {
                depth -= 1
                if depth == 0 {
                    return bytes[start...index]
                }
            }
            index += 1
        }
        return nil
    }

    /// One item of the answer. Keys are matched ignoring case, as Windows' web defaults match them.
    private struct RawSuggestion: Decodable {
        let spoken: String?
        let written: String?

        private struct AnyKey: CodingKey {
            let stringValue: String
            var intValue: Int? { nil }

            init?(stringValue: String) {
                self.stringValue = stringValue
            }

            init?(intValue: Int) {
                nil
            }
        }

        init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: AnyKey.self)
            var spoken: String?
            var written: String?
            for key in container.allKeys {
                switch key.stringValue.lowercased() {
                case "spoken":
                    spoken = try container.decodeIfPresent(String.self, forKey: key)
                case "written":
                    written = try container.decodeIfPresent(String.self, forKey: key)
                default:
                    continue
                }
            }
            self.spoken = spoken
            self.written = written
        }
    }
}
