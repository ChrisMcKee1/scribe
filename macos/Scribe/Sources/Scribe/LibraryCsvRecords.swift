import Foundation

struct CsvField: Equatable, Sendable {
    let text: String
    let quoted: Bool
}

struct CsvRecord: Equatable, Sendable {
    let line: Int
    let fields: [CsvField]
    let rawComment: Bool
}

enum LibraryCsvRecords {
    static let header = "pattern,replacement,whole_word,enabled"

    static func read(_ text: String, errors: inout [LibraryCsvRowError]) -> [CsvRecord] {
        let scalars = Array(text.unicodeScalars)
        var records: [CsvRecord] = []
        var fields: [CsvField] = []
        var field = String.UnicodeScalarView()
        var inQuotes = false
        var fieldQuoted = false
        var rawComment = false
        var firstFieldRawPrefix = String.UnicodeScalarView()
        var line = 1
        var recordStartLine = 1
        var index = 0

        while index < scalars.count {
            let scalar = scalars[index]

            if inQuotes {
                if scalar == "\"" {
                    if index + 1 < scalars.count, scalars[index + 1] == "\"" {
                        field.append(UnicodeScalar(0x22)!)
                        index += 1
                    } else {
                        inQuotes = false
                    }
                } else {
                    if scalar == "\n" {
                        line += 1
                    }
                    field.append(scalar)
                }
                index += 1
                continue
            }

            switch scalar {
            case "\"":
                inQuotes = true
                fieldQuoted = true
            case ",":
                rawComment = rawComment || endField(
                    fields: &fields,
                    field: &field,
                    quoted: fieldQuoted,
                    firstFieldRawPrefix: firstFieldRawPrefix)
                fieldQuoted = false
                firstFieldRawPrefix = String.UnicodeScalarView()
            case "\r":
                break
            case "\n":
                rawComment = rawComment || endField(
                    fields: &fields,
                    field: &field,
                    quoted: fieldQuoted,
                    firstFieldRawPrefix: firstFieldRawPrefix)
                records.append(CsvRecord(line: recordStartLine, fields: fields, rawComment: rawComment))
                fields = []
                fieldQuoted = false
                rawComment = false
                firstFieldRawPrefix = String.UnicodeScalarView()
                line += 1
                recordStartLine = line
            default:
                field.append(scalar)
                if fields.isEmpty && !fieldQuoted {
                    firstFieldRawPrefix.append(scalar)
                }
            }

            index += 1
        }

        if inQuotes {
            errors.append(LibraryCsvRowError(line: recordStartLine, kind: .unclosedQuote, field: nil))
            return records
        }

        if !field.isEmpty || !fields.isEmpty {
            rawComment = rawComment || endField(
                fields: &fields,
                field: &field,
                quoted: fieldQuoted,
                firstFieldRawPrefix: firstFieldRawPrefix)
            records.append(CsvRecord(line: recordStartLine, fields: fields, rawComment: rawComment))
        }

        return records
    }

    static func appendRow(_ text: inout String, spoken: String, written: String, wholeWord: Bool, enabled: Bool) {
        appendField(&text, value: spoken, firstField: true)
        text.append(",")
        appendField(&text, value: written, firstField: false)
        text.append(",")
        text.append(wholeWord ? "true" : "false")
        text.append(",")
        text.append(enabled ? "true" : "false")
    }

    static func appendField(_ text: inout String, value: String, firstField: Bool) {
        if needsQuotes(value, firstField: firstField) {
            appendQuoted(&text, value)
        } else {
            text.append(value)
        }
    }

    static func appendMetadataField(_ text: inout String, line: String) {
        if needsSpecialQuotes(line) {
            appendQuoted(&text, line)
        } else {
            text.append(line)
        }
    }

    static func needsQuotes(_ value: String, firstField: Bool) -> Bool {
        guard !value.isEmpty else {
            return false
        }

        if needsSpecialQuotes(value)
            || value.unicodeScalars.first?.properties.isWhitespace == true
            || value.unicodeScalars.last?.properties.isWhitespace == true
        {
            return true
        }

        return firstField && (value.first == "#" || value.caseInsensitiveCompare("pattern") == .orderedSame)
    }

    private static func needsSpecialQuotes(_ value: String) -> Bool {
        value.contains(",") || value.contains("\"") || value.contains("\r") || value.contains("\n")
    }

    private static func appendQuoted(_ text: inout String, _ value: String) {
        text.append("\"")
        for char in value {
            if char == "\"" {
                text.append("\"")
            }
            text.append(char)
        }
        text.append("\"")
    }

    private static func endField(
        fields: inout [CsvField],
        field: inout String.UnicodeScalarView,
        quoted: Bool,
        firstFieldRawPrefix: String.UnicodeScalarView
    ) -> Bool {
        let text = String(field)
        field = String.UnicodeScalarView()
        fields.append(CsvField(text: text, quoted: quoted))
        guard fields.count == 1 else {
            return false
        }
        let trimmed = String(firstFieldRawPrefix).trimmingCharacters(in: .whitespaces)
        return !trimmed.isEmpty && trimmed.first == "#"
    }
}
