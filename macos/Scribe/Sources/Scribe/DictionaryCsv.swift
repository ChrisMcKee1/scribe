import Foundation

/// CSV round-tripping for the user dictionary, so vocabularies can be built in a spreadsheet and
/// shared between people instead of typed row by row in Settings. Format is RFC 4180-style:
/// `pattern,replacement,whole_word,enabled` with a header row, quoted fields when needed, and `#`
/// comment lines (which double as instructions in the downloadable template). Mirrors
/// `Scribe.Core.PostProcessing.DictionaryCsv` on Windows exactly, including error wording, so the
/// same template/export files round-trip on either platform.
enum DictionaryCsv {
    static let header = "pattern,replacement,whole_word,enabled"

    /// The starter file behind the "Get Template" button. Comment lines explain the columns so the
    /// file is self-documenting when it opens in a spreadsheet or editor.
    static let template = """
        # Scribe dictionary template
        #
        # One row per substitution: what the transcriber usually hears, and what you
        # want written instead. Fill it in, then use Import in Scribe's Dictionary
        # settings. Lines starting with # are ignored.
        #
        # pattern     - the spoken word or phrase as it gets transcribed (required)
        # replacement - what to write instead (required)
        # whole_word  - true to match on word boundaries only, false for phrase
        #               replacement anywhere (optional, default true)
        # enabled     - false to keep the row but switch it off (optional, default true)
        #
        pattern,replacement,whole_word,enabled
        azure,Azure,true,true
        cube flow,Kubeflow,true,true
        kay eight ess,K8s,true,true

        """

    struct ParseResult {
        let entries: [DictionaryEntry]
        let errors: [String]
    }

    /// Renders entries as a CSV document (header included), ready to save or share.
    static func export(_ entries: [DictionaryEntry]) -> String {
        var lines = [header]
        for entry in entries {
            lines.append(
                "\(quote(entry.pattern)),\(quote(entry.replacement)),"
                    + "\(entry.wholeWord ? "true" : "false"),\(entry.enabled ? "true" : "false")")
        }
        return lines.map { $0 + "\r\n" }.joined()
    }

    /// Parses a dictionary CSV. Never throws on content: rows that can't be understood are
    /// reported in `errors` with their line number while the good rows still import, so one typo
    /// in a shared 300-term file doesn't reject the other 299.
    static func parse(_ csv: String?) -> ParseResult {
        var entries: [DictionaryEntry] = []
        var errors: [String] = []
        guard let csv, !csv.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return ParseResult(entries: entries, errors: errors)
        }

        for (fields, lineNumber) in readRecords(csv, errors: &errors) {
            // Skip blank lines, comment lines, and the header row wherever it appears.
            if fields.isEmpty || (fields.count == 1 && fields[0].trimmingCharacters(in: .whitespaces).isEmpty) {
                continue
            }

            if fields[0].trimmingCharacters(in: .whitespaces).hasPrefix("#") {
                continue
            }

            if fields[0].trimmingCharacters(in: .whitespaces).lowercased() == "pattern" {
                continue
            }

            if fields.count < 2 {
                errors.append("Line \(lineNumber): expected at least a pattern and a replacement.")
                continue
            }

            let pattern = fields[0].trimmingCharacters(in: .whitespaces)
            let replacement = fields[1].trimmingCharacters(in: .whitespaces)
            if pattern.isEmpty {
                errors.append("Line \(lineNumber): the pattern (spoken form) is empty.")
                continue
            }

            guard let wholeWord = parseFlag(fields.count > 2 ? fields[2] : nil, defaultValue: true) else {
                let raw = fields[2].trimmingCharacters(in: .whitespaces)
                errors.append("Line \(lineNumber): whole_word should be true or false, not \"\(raw)\".")
                continue
            }

            guard let enabled = parseFlag(fields.count > 3 ? fields[3] : nil, defaultValue: true) else {
                let raw = fields[3].trimmingCharacters(in: .whitespaces)
                errors.append("Line \(lineNumber): enabled should be true or false, not \"\(raw)\".")
                continue
            }

            entries.append(DictionaryEntry(pattern: pattern, replacement: replacement, wholeWord: wholeWord, enabled: enabled))
        }

        return ParseResult(entries: entries, errors: errors)
    }

    private static func parseFlag(_ field: String?, defaultValue: Bool) -> Bool? {
        guard let field, !field.trimmingCharacters(in: .whitespaces).isEmpty else {
            return defaultValue // optional column
        }

        switch field.trimmingCharacters(in: .whitespaces).lowercased() {
        case "true", "yes", "1":
            return true
        case "false", "no", "0":
            return false
        default:
            return nil
        }
    }

    private static func quote(_ value: String) -> String {
        guard value.contains(",") || value.contains("\"") || value.contains("\r") || value.contains("\n") else {
            return value
        }
        return "\"" + value.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }

    // Character-level RFC 4180 reader: quoted fields may contain commas, doubled quotes, and even
    // line breaks (spreadsheets emit all three), so a naive split on newline/comma is not enough.
    private static func readRecords(_ csv: String, errors: inout [String]) -> [([String], Int)] {
        var records: [([String], Int)] = []
        var fields: [String] = []
        var field = ""
        var inQuotes = false
        var line = 1
        var recordStartLine = 1

        // Iterate Unicode scalars, not `Character`: Swift's grapheme clustering merges "\r\n"
        // into a single `Character`, which would silently defeat the CR/LF handling below and
        // make every line after the header disappear. C# iterates UTF-16 code units, so scalars
        // (which agree with UTF-16 for all characters this format cares about) match that
        // behaviour exactly.
        let scalars = Array(csv.unicodeScalars)
        var i = 0
        while i < scalars.count {
            let ch = scalars[i]

            if inQuotes {
                if ch == "\"" {
                    if i + 1 < scalars.count, scalars[i + 1] == "\"" {
                        field.unicodeScalars.append("\"")
                        i += 1
                    } else {
                        inQuotes = false
                    }
                } else {
                    if ch == "\n" {
                        line += 1
                    }
                    field.unicodeScalars.append(ch)
                }
                i += 1
                continue
            }

            switch ch {
            case "\"":
                inQuotes = true
            case ",":
                fields.append(field)
                field = ""
            case "\r":
                break // handled by the following \n (or ignored for a lone \r)
            case "\n":
                fields.append(field)
                field = ""
                records.append((fields, recordStartLine))
                fields = []
                line += 1
                recordStartLine = line
            default:
                field.unicodeScalars.append(ch)
            }
            i += 1
        }

        if inQuotes {
            errors.append("Line \(recordStartLine): quoted field is missing its closing quote.")
            return records
        }

        if !field.isEmpty || !fields.isEmpty {
            fields.append(field)
            records.append((fields, recordStartLine))
        }

        return records
    }
}
