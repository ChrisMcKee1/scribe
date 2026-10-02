import Foundation

/// Outcome of parsing a library CSV: the header metadata (any of which may be `nil` when the file
/// omits it), the usable entries, and per-line errors.
struct DictionaryLibraryFile {
    let name: String?
    let category: String?
    let description: String?
    let basedOn: String?
    let entries: [DictionaryEntry]
    let errors: [String]
}

/// CSV round-tripping for a dictionary library. The compatibility wrappers below keep the current
/// service and tests working while the full Swift word-pack model is still being ported.
enum DictionaryLibraryCsv {
    private static let codec = LibraryCsvCodec.shared

    static func parse(_ csv: String?) -> DictionaryLibraryFile {
        guard let csv else {
            return DictionaryLibraryFile(name: nil, category: nil, description: nil, basedOn: nil, entries: [], errors: [])
        }
        let document = codec.readManaged(Data(csv.utf8))
        return DictionaryLibraryFile(
            name: document.name,
            category: document.category,
            description: document.description,
            basedOn: document.basedOn,
            entries: document.terms.map(\.dictionaryEntry),
            errors: document.errors.map(\.legacyMessage))
    }

    static func parseManaged(_ data: Data) -> LibraryCsvDocument {
        codec.readManaged(data)
    }

    static func parseImport(_ data: Data) -> LibraryCsvDocument {
        codec.readImport(data)
    }

    static func export(_ library: DictionaryLibrary) -> String {
        let content = LibraryCsvContent(
            name: library.name,
            category: library.category,
            description: library.description,
            basedOn: nil,
            rows: library.entries.map(TermValues.init(entry:)))
        let data = try? codec.writeManaged(content)
        return String(data: data ?? Data(), encoding: .utf8) ?? ""
    }

    static func exportManaged(_ content: LibraryCsvContent) throws -> Data {
        try codec.writeManaged(content)
    }

    static func exportSharing(_ content: LibraryCsvContent) -> Data {
        codec.writeExport(content)
    }
}
