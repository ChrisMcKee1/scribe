import Foundation

/// Outcome of parsing a library CSV: the header metadata (any of which may be `nil` when the file
/// omits it), the usable entries, and per-line errors.
struct DictionaryLibraryFile {
    let name: String?
    let category: String?
    let description: String?
    let entries: [DictionaryEntry]
    let errors: [String]
}

/// CSV round-tripping for a dictionary **library**: the same row format as `DictionaryCsv`
/// (`pattern,replacement,whole_word,enabled`) plus an optional metadata header carried in comment
/// lines, so a single file is self-describing when shared:
/// ```
/// # name: Microsoft Azure
/// # category: Microsoft
/// # description: Azure services and common acronyms
/// pattern,replacement
/// a p i m,APIM
/// ```
/// Files without the header still import (the caller supplies a name from the file name), so any
/// plain dictionary CSV doubles as a library. Row parsing and quoting are delegated to
/// `DictionaryCsv`; this only adds the header layer. Direct port of Windows'
/// `Scribe.Core.PostProcessing.DictionaryLibraryCsv`.
enum DictionaryLibraryCsv {
    private static let nameKey = "name"
    private static let categoryKey = "category"
    private static let descriptionKey = "description"

    /// Parses a library CSV into its metadata (from the comment header, if present) and entries.
    /// Never throws on content: unreadable rows land in `errors` with their line number while the
    /// good rows still import.
    static func parse(_ csv: String?) -> DictionaryLibraryFile {
        var name: String?
        var category: String?
        var description: String?

        if let csv, !csv.isEmpty {
            for line in splitLines(csv) {
                let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
                if trimmed.hasPrefix("#") {
                    tryReadMeta(trimmed, key: nameKey, into: &name)
                    tryReadMeta(trimmed, key: categoryKey, into: &category)
                    tryReadMeta(trimmed, key: descriptionKey, into: &description)
                    continue
                }
                if trimmed.isEmpty {
                    continue // tolerate blank lines before the header
                }
                break // reached the header/data; metadata only lives at the top
            }
        }

        let parsed = DictionaryCsv.parse(csv)
        return DictionaryLibraryFile(
            name: nullIfBlank(name),
            category: nullIfBlank(category),
            description: nullIfBlank(description),
            entries: parsed.entries,
            errors: parsed.errors)
    }

    /// Renders a library as a shareable CSV document: the metadata header followed by the entry
    /// rows in `DictionaryCsv` format.
    static func export(_ library: DictionaryLibrary) -> String {
        var text = "# name: \(singleLine(library.name))\r\n# category: \(singleLine(library.category))\r\n"
        if let description = library.description, !description.trimmingCharacters(in: .whitespaces).isEmpty {
            text += "# description: \(singleLine(description))\r\n"
        }
        text += DictionaryCsv.export(library.entries)
        return text
    }

    // Reads "# key: value" (case-insensitive key, tolerant of spacing) into value; first line wins.
    private static func tryReadMeta(_ commentLine: String, key: String, into value: inout String?) {
        guard value == nil else { return }

        var body = Substring(commentLine)
        while body.first == "#" { body = body.dropFirst() }
        body = Substring(body.trimmingCharacters(in: .whitespacesAndNewlines))

        guard
            body.count > key.count,
            body.lowercased().hasPrefix(key),
            body[body.index(body.startIndex, offsetBy: key.count)] == ":"
        else {
            return
        }

        value = body[body.index(body.startIndex, offsetBy: key.count + 1)...]
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // Unicode-scalar line split: Swift's grapheme clustering merges "\r\n" into a single
    // `Character`, so a naive `split(separator: "\n")` never matches inside a CRLF file and the
    // whole document comes back as one "line". Iterating scalars (matching `DictionaryCsv`'s
    // reader) treats CR and LF as the separate bytes they are on the wire.
    private static func splitLines(_ csv: String) -> [String] {
        var lines: [String] = []
        var current = String.UnicodeScalarView()
        for scalar in csv.unicodeScalars {
            if scalar == "\n" {
                lines.append(String(current))
                current = String.UnicodeScalarView()
            } else if scalar != "\r" {
                current.append(scalar)
            }
        }
        lines.append(String(current))
        return lines
    }

    // Metadata is single-line: flatten any control characters so a value can't spill into extra
    // header lines or break the comment convention when re-imported.
    private static func singleLine(_ value: String) -> String {
        String(value.unicodeScalars.map { CharacterSet.controlCharacters.contains($0) ? " " : Character($0) })
    }

    private static func nullIfBlank(_ value: String?) -> String? {
        guard let value, !value.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        return value.trimmingCharacters(in: .whitespaces)
    }
}
