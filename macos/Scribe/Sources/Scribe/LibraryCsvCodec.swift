import Foundation

enum LibraryCsvCodecError: LocalizedError {
    case metadataUnreadableInOlderVersions

    var errorDescription: String? {
        switch self {
        case .metadataUnreadableInOlderVersions:
            return "The library metadata does not read back in older versions."
        }
    }
}

struct LibraryCsvCodec: Sendable {
    static let shared = LibraryCsvCodec(ansiCodePage: 1252)

    private let ansiCodePage: Int

    init(ansiCodePage: Int) {
        self.ansiCodePage = ansiCodePage
    }

    func readManaged(_ data: Data) -> LibraryCsvDocument {
        let decoded = decodeManaged(data)
        let header = readRawHeader(decoded.text)

        if header.format == "2" {
            let parsed = readStrictManagedRows(decoded.text)
            return LibraryCsvDocument(
                name: nullIfBlank(header.name),
                category: nullIfBlank(header.category),
                description: nullIfBlank(header.description),
                basedOn: nullIfBlank(header.basedOn),
                terms: parsed.terms,
                errors: parsed.errors,
                encoding: decoded.encoding,
                formulaGuardVersion: nil,
                issues: .none)
        }

        let parsed = readLegacyRows(decoded.text)
        return LibraryCsvDocument(
            name: nullIfBlank(header.name),
            category: nullIfBlank(header.category),
            description: nullIfBlank(header.description),
            basedOn: nil,
            terms: parsed.terms,
            errors: parsed.errors,
            encoding: decoded.encoding,
            formulaGuardVersion: nil,
            issues: .none)
    }

    func writeManaged(_ content: LibraryCsvContent) throws -> Data {
        let readsBack = LibraryMetadata.readsBackInOlderVersions(
            content.name,
            content.category,
            content.description,
            content.basedOn
        )
        guard readsBack else {
            throw LibraryCsvCodecError.metadataUnreadableInOlderVersions
        }

        var text = "# name: \(content.name)\r\n# category: \(content.category)\r\n"
        if let description = content.description, !description.isEmpty {
            text += "# description: \(description)\r\n"
        }
        if let basedOn = content.basedOn, !basedOn.isEmpty {
            text += "# based-on: \(basedOn)\r\n"
        }
        text += "# scribe-format: 2\r\n"
        appendRows(to: &text, rows: content.rows, guardValues: false)
        return Data(text.utf8)
    }

    func readImport(_ data: Data) -> LibraryCsvDocument {
        if data.count > LibraryLimits.maxImportBytes {
            let declared = decodeDeclaredEncoding(Array(data.prefix(4)))
            let encoding = declared ?? makeEncoding(codePage: 65001)
            return LibraryCsvDocument(
                name: nil,
                category: nil,
                description: nil,
                basedOn: nil,
                terms: [],
                errors: [],
                encoding: encoding,
                formulaGuardVersion: nil,
                issues: [.sizeLimitExceeded])
        }

        let decoded = decodeImport(data)
        return readImportText(decoded.text, encoding: decoded.encoding)
    }

    func writeExport(_ content: LibraryCsvContent) -> Data {
        var text = ""
        appendMetadataLine(&text, "# name: \(content.name)")
        appendMetadataLine(&text, "# category: \(content.category)")
        if let description = content.description, !description.isEmpty {
            appendMetadataLine(&text, "# description: \(description)")
        }
        if let basedOn = content.basedOn, !basedOn.isEmpty {
            appendMetadataLine(&text, "# based-on: \(basedOn)")
        }
        text += "# formula-guard: \(LibraryFormulaGuard.version)\r\n"
        appendRows(to: &text, rows: content.rows, guardValues: true)
        return Data([0xEF, 0xBB, 0xBF] + Array(text.utf8))
    }

    private func readLegacyRows(_ text: String) -> (terms: [TermValues], errors: [LibraryCsvRowError]) {
        var errors: [LibraryCsvRowError] = []
        var terms: [TermValues] = []

        for record in LibraryCsvRecords.read(text, errors: &errors) {
            let fields = record.fields
            guard let first = fields.first else {
                continue
            }
            if isLegacyIgnored(fields, firstField: first) {
                continue
            }
            if fields.count < 2 {
                errors.append(LibraryCsvRowError(line: record.line, kind: .missingFields, field: nil))
                continue
            }

            let spoken = fields[0].text.trimmingCharacters(in: .whitespaces)
            if spoken.isEmpty {
                errors.append(LibraryCsvRowError(line: record.line, kind: .emptySpoken, field: nil))
                continue
            }
            guard let wholeWord = parseFlag(fields.count > 2 ? fields[2].text : nil) else {
                errors.append(
                    LibraryCsvRowError(
                        line: record.line,
                        kind: .invalidWholeWord,
                        field: fields[2].text.trimmingCharacters(in: .whitespaces)))
                continue
            }
            guard let enabled = parseFlag(fields.count > 3 ? fields[3].text : nil) else {
                errors.append(
                    LibraryCsvRowError(
                        line: record.line,
                        kind: .invalidEnabled,
                        field: fields[3].text.trimmingCharacters(in: .whitespaces)))
                continue
            }

            let written = fields[1].text.trimmingCharacters(in: .whitespaces)
            terms.append(TermValues(spoken, written, wholeWord, enabled))
        }

        errors.sort { $0.line < $1.line }
        return (terms, errors)
    }

    private func isLegacyIgnored(_ fields: [CsvField], firstField: CsvField) -> Bool {
        (fields.count == 1 && firstField.text.trimmingCharacters(in: .whitespaces).isEmpty)
            || firstField.text.trimmingCharacters(in: .whitespaces).hasPrefix("#")
            || firstField.text.trimmingCharacters(in: .whitespaces)
                .caseInsensitiveCompare("pattern") == .orderedSame
    }

    private func readStrictManagedRows(_ text: String) -> (terms: [TermValues], errors: [LibraryCsvRowError]) {
        var errors: [LibraryCsvRowError] = []
        var terms: [TermValues] = []
        var firstDataRecord = true

        for record in LibraryCsvRecords.read(text, errors: &errors) {
            if record.rawComment || isBlank(record) {
                continue
            }
            if firstDataRecord {
                firstDataRecord = false
                if isHeader(record) {
                    continue
                }
            }
            readStrictRow(
                record,
                reverseGuard: false,
                capFields: false,
                terms: &terms,
                errors: &errors)
        }

        errors.sort { $0.line < $1.line }
        return (terms, errors)
    }

    private func readImportText(_ text: String, encoding: LibraryTextEncoding) -> LibraryCsvDocument {
        var header = HeaderValues()
        var terms: [TermValues] = []
        var errors: [LibraryCsvRowError] = []
        var issues: LibraryCsvIssues = .none
        var beforeData = true
        var reverseGuard = false
        var dataRecords = 0

        for record in LibraryCsvRecords.read(text, errors: &errors) {
            if isBlank(record) {
                continue
            }

            if beforeData {
                if record.fields.first?.text.trimmingCharacters(in: .whitespaces).hasPrefix("#") == true {
                    if takeMetadataRecord(&header, record: record) {
                        issues.insert(.headerPaddingRemoved)
                    }
                    continue
                }

                beforeData = false
                reverseGuard = parseVersion(header.formulaGuard) == LibraryFormulaGuard.version
                if isHeader(record) {
                    continue
                }
            } else if record.rawComment {
                continue
            }

            if dataRecords == LibraryLimits.maxTermsPerLibrary {
                issues.insert(.rowLimitExceeded)
                break
            }

            dataRecords += 1
            readStrictRow(
                record,
                reverseGuard: reverseGuard,
                capFields: true,
                terms: &terms,
                errors: &errors)
        }

        errors.sort { $0.line < $1.line }
        return LibraryCsvDocument(
            name: nullIfBlank(header.name),
            category: nullIfBlank(header.category),
            description: nullIfBlank(header.description),
            basedOn: nullIfBlank(header.basedOn),
            terms: terms,
            errors: errors,
            encoding: encoding,
            formulaGuardVersion: parseVersion(header.formulaGuard),
            issues: issues)
    }

    private func readStrictRow(
        _ record: CsvRecord,
        reverseGuard: Bool,
        capFields: Bool,
        terms: inout [TermValues],
        errors: inout [LibraryCsvRowError]
    ) {
        let fields = record.fields
        if fields.count < 2 {
            errors.append(LibraryCsvRowError(line: record.line, kind: .missingFields, field: nil))
            return
        }

        var spoken = strictValue(fields[0])
        var written = strictValue(fields[1])
        if reverseGuard {
            spoken = LibraryFormulaGuard.decode(spoken)
            written = LibraryFormulaGuard.decode(written)
        }
        if spoken.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            errors.append(LibraryCsvRowError(line: record.line, kind: .emptySpoken, field: nil))
            return
        }
        if capFields && (spoken.count > LibraryLimits.maxFieldLength || written.count > LibraryLimits.maxFieldLength) {
            let field = spoken.count > LibraryLimits.maxFieldLength ? spoken : written
            errors.append(LibraryCsvRowError(line: record.line, kind: .fieldTooLong, field: field))
            return
        }
        if let wholeWordField = field(at: 2, in: fields), parseFlag(wholeWordField.text) == nil {
            errors.append(
                LibraryCsvRowError(
                    line: record.line,
                    kind: .invalidWholeWord,
                    field: strictValue(wholeWordField)))
            return
        }
        if let enabledField = field(at: 3, in: fields), parseFlag(enabledField.text) == nil {
            errors.append(
                LibraryCsvRowError(
                    line: record.line,
                    kind: .invalidEnabled,
                    field: strictValue(enabledField)))
            return
        }

        let wholeWord = parseFlag(field(at: 2, in: fields)?.text) ?? true
        let enabled = parseFlag(field(at: 3, in: fields)?.text) ?? true
        terms.append(TermValues(spoken, written, wholeWord, enabled))
    }

    private func field(at index: Int, in fields: [CsvField]) -> CsvField? {
        index < fields.count ? fields[index] : nil
    }

    private func strictValue(_ field: CsvField) -> String {
        field.quoted ? field.text : field.text.trimmingCharacters(in: .whitespaces)
    }

    private func isBlank(_ record: CsvRecord) -> Bool {
        for field in record.fields {
            if field.quoted || !field.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return false
            }
        }
        return true
    }

    private func isHeader(_ record: CsvRecord) -> Bool {
        var fields = record.fields
        while let last = fields.last, !last.quoted && last.text.isEmpty {
            fields.removeLast()
        }
        guard fields.count >= 2, fields.count <= 4 else {
            return false
        }

        let expected = ["pattern", "replacement", "whole_word", "enabled"]
        for index in fields.indices {
            let text = fields[index].text.trimmingCharacters(in: .whitespaces)
            if text.caseInsensitiveCompare(expected[index]) != .orderedSame {
                return false
            }
        }

        return fields.count > 2 || (!fields[0].quoted && !fields[1].quoted)
    }

    private func takeMetadataRecord(_ header: inout HeaderValues, record: CsvRecord) -> Bool {
        var fields = record.fields
        while fields.count > 1, let last = fields.last, !last.quoted && last.text.isEmpty {
            fields.removeLast()
        }

        let line = fields.count == 1 ? fields[0].text : fields.map(\.text).joined(separator: ",")
        header.take(line.trimmingCharacters(in: .whitespaces))
        return fields.count < record.fields.count
    }

    private func appendRows(to text: inout String, rows: [TermValues], guardValues: Bool) {
        text += LibraryCsvRecords.header + "\r\n"
        for row in rows {
            let spoken = guardValues ? LibraryFormulaGuard.encode(row.spoken) : row.spoken
            let written = guardValues ? LibraryFormulaGuard.encode(row.written) : row.written
            LibraryCsvRecords.appendRow(
                &text,
                spoken: spoken,
                written: written,
                wholeWord: row.wholeWord,
                enabled: row.enabled)
            text += "\r\n"
        }
    }

    private func appendMetadataLine(_ text: inout String, _ line: String) {
        LibraryCsvRecords.appendMetadataField(&text, line: line)
        text += "\r\n"
    }

    private func parseFlag(_ field: String?) -> Bool? {
        guard let field, !field.trimmingCharacters(in: .whitespaces).isEmpty else {
            return true
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

    private func parseVersion(_ value: String?) -> Int? {
        guard let value else {
            return nil
        }
        return Int(value)
    }

    private func nullIfBlank(_ value: String?) -> String? {
        guard let value, !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return nil
        }
        return value.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func decodeManaged(_ data: Data) -> (text: String, encoding: LibraryTextEncoding) {
        let bytes = Array(data)
        if bytes.starts(with: [0xEF, 0xBB, 0xBF]) {
            return decoded(
                Data(bytes.dropFirst(3)),
                using: .utf8,
                codePage: 65001,
                byteOrderMark: true)
        }
        if bytes.starts(with: [0xFF, 0xFE, 0x00, 0x00]) {
            return decoded(
                Data(bytes.dropFirst(4)),
                using: .utf32LittleEndian,
                codePage: 12000,
                byteOrderMark: true)
        }
        if bytes.starts(with: [0x00, 0x00, 0xFE, 0xFF]) {
            return decoded(
                Data(bytes.dropFirst(4)),
                using: .utf32BigEndian,
                codePage: 12001,
                byteOrderMark: true)
        }
        if bytes.starts(with: [0xFF, 0xFE]) {
            return decoded(
                Data(bytes.dropFirst(2)),
                using: .utf16LittleEndian,
                codePage: 1200,
                byteOrderMark: true)
        }
        if bytes.starts(with: [0xFE, 0xFF]) {
            return decoded(
                Data(bytes.dropFirst(2)),
                using: .utf16BigEndian,
                codePage: 1201,
                byteOrderMark: true)
        }
        return decoded(data, using: .utf8, codePage: 65001, byteOrderMark: false)
    }

    private func decoded(
        _ data: Data,
        using encoding: String.Encoding,
        codePage: Int,
        byteOrderMark: Bool
    ) -> (text: String, encoding: LibraryTextEncoding) {
        if let text = String(data: data, encoding: encoding) {
            return (
                text,
                makeEncoding(codePage: codePage, byteOrderMark: byteOrderMark)
            )
        }

        let replacement = encoding == .utf8 ? String(decoding: data, as: UTF8.self) : ""
        return (
            replacement,
            LibraryTextEncoding(
                codePage: codePage,
                byteOrderMark: byteOrderMark,
                ansiFallback: false,
                invalidBytesReplaced: true))
    }

    private func decodeImport(_ data: Data) -> (text: String, encoding: LibraryTextEncoding) {
        let managed = decodeManaged(data)
        if managed.encoding.byteOrderMark || !managed.encoding.invalidBytesReplaced {
            return managed
        }

        let text = String(data: data, encoding: .windowsCP1252)
            ?? String(decoding: data, as: UTF8.self)
        return (
            text,
            makeEncoding(codePage: ansiCodePage, ansiFallback: true)
        )
    }

    private func decodeDeclaredEncoding(_ prefix: [UInt8]) -> LibraryTextEncoding? {
        if prefix.starts(with: [0xEF, 0xBB, 0xBF]) {
            return makeEncoding(codePage: 65001, byteOrderMark: true)
        }
        if prefix.starts(with: [0xFF, 0xFE, 0x00, 0x00]) {
            return makeEncoding(codePage: 12000, byteOrderMark: true)
        }
        if prefix.starts(with: [0x00, 0x00, 0xFE, 0xFF]) {
            return makeEncoding(codePage: 12001, byteOrderMark: true)
        }
        if prefix.starts(with: [0xFF, 0xFE]) {
            return makeEncoding(codePage: 1200, byteOrderMark: true)
        }
        if prefix.starts(with: [0xFE, 0xFF]) {
            return makeEncoding(codePage: 1201, byteOrderMark: true)
        }
        return nil
    }

    private func makeEncoding(
        codePage: Int,
        byteOrderMark: Bool = false,
        ansiFallback: Bool = false,
        invalidBytesReplaced: Bool = false
    ) -> LibraryTextEncoding {
        LibraryTextEncoding(
            codePage: codePage,
            byteOrderMark: byteOrderMark,
            ansiFallback: ansiFallback,
            invalidBytesReplaced: invalidBytesReplaced)
    }

    private struct HeaderValues {
        var name: String?
        var category: String?
        var description: String?
        var basedOn: String?
        var format: String?
        var formulaGuard: String?

        mutating func take(_ line: String) {
            guard let pair = metadataPair(from: line) else {
                return
            }

            switch pair.key {
            case "name" where name == nil:
                name = pair.value
            case "category" where category == nil:
                category = pair.value
            case "description" where description == nil:
                description = pair.value
            case "based-on" where basedOn == nil:
                basedOn = pair.value
            case "scribe-format" where format == nil:
                format = pair.value
            case "formula-guard" where formulaGuard == nil:
                formulaGuard = pair.value
            default:
                break
            }
        }

        private func metadataPair(from line: String) -> (key: String, value: String)? {
            var body = line
            while body.first == "#" {
                body.removeFirst()
            }
            body = body.trimmingCharacters(in: .whitespacesAndNewlines)
            guard let colon = body.firstIndex(of: ":") else {
                return nil
            }

            let key = String(body[..<colon]).lowercased()
            let value = String(body[body.index(after: colon)...])
                .trimmingCharacters(in: .whitespacesAndNewlines)
            return key.isEmpty ? nil : (key, value)
        }
    }

    private func readRawHeader(_ text: String) -> HeaderValues {
        var header = HeaderValues()
        let normalized = normalizeLineEndings(in: text)

        for rawLine in normalized.split(separator: "\n", omittingEmptySubsequences: false) {
            let line = String(rawLine)
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("#") {
                header.take(trimmed)
                continue
            }
            if trimmed.isEmpty {
                continue
            }
            break
        }

        return header
    }

    private func normalizeLineEndings(in text: String) -> String {
        text.replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
    }
}
