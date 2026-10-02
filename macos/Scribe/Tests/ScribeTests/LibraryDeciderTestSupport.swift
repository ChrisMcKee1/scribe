import Foundation

@testable import Scribe

enum LibraryDeciderTestSupport {
    static func customRow(
        _ spoken: String,
        _ written: String,
        rowID: Int64,
        wholeWord: Bool = true,
        enabled: Bool = true
    ) -> DraftTermRow {
        DraftTermRow(
            rowID: rowID,
            row: .custom(TermValues(spoken, written, wholeWord, enabled)),
            removalIntent: false,
            legacyEmpty: false)
    }

    static func builtInRow(
        key: String,
        spoken: String,
        written: String,
        origin: TermOrigin,
        rowID: Int64,
        shipped: TermValues? = nil,
        wholeWord: Bool = true,
        enabled: Bool = true
    ) -> DraftTermRow {
        DraftTermRow(
            rowID: rowID,
            row: LibraryRow(
                key: LibraryTermKey.from(key),
                values: TermValues(spoken, written, wholeWord, enabled),
                origin: origin,
                shipped: shipped),
            removalIntent: false,
            legacyEmpty: false)
    }

    static func library(
        id: String,
        name: String,
        builtIn: Bool = false,
        rows: [DraftTermRow],
        pendingDelete: Bool = false
    ) -> DraftLibrary {
        DraftLibrary(
            id: id,
            name: name,
            category: builtIn ? "Built-in" : "Custom",
            description: nil,
            builtIn: builtIn,
            rows: rows,
            pendingDelete: pendingDelete)
    }

    static func workspace(_ libraries: [DraftLibrary], revision: Int64 = 1) -> LibraryWorkspace {
        LibraryWorkspace(revision: revision, libraries: libraries)
    }

    static func document(
        name: String? = nil,
        fileName: String? = nil,
        basedOn: String? = nil,
        terms: [TermValues],
        errors: [LibraryCsvRowError] = [],
        encoding: LibraryTextEncoding = LibraryTextEncoding(
            codePage: 65001,
            byteOrderMark: false,
            ansiFallback: false,
            invalidBytesReplaced: false)
    ) -> (LibraryCsvDocument, String?) {
        (
            LibraryCsvDocument(
                name: name,
                category: nil,
                description: nil,
                basedOn: basedOn,
                terms: terms,
                errors: errors,
                encoding: encoding,
                formulaGuardVersion: nil,
                issues: .none),
            fileName)
    }
}
