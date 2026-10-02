import Foundation

enum LibraryImportTarget: Sendable, Equatable {
    case new(fileName: String?)
    case existing(libraryID: String)
}

enum ImportConflictChoice: Sendable {
    case keepMine
    case useFilesVersion
}

enum LibraryImportOperationKind: Sendable, Equatable {
    case add
    case writtenDifferently
    case alreadyHere
}

struct LibraryImportOperation: Sendable, Equatable {
    let kind: LibraryImportOperationKind
    let fileRow: TermValues
    let existingRowID: Int64?
    let existingValues: TermValues?
}

struct LibraryImportPlan: Sendable, Equatable {
    let target: LibraryImportTarget
    let draftRevision: Int64
    let suggestedName: String
    let category: String?
    let description: String?
    let basedOnTarget: String?
    let operations: [LibraryImportOperation]
    let adds: Int
    let writtenDifferently: Int
    let alreadyHere: Int
    let removalRules: Int
    let skipped: Int
    let skippedRows: [LibraryCsvRowError]
    let encoding: LibraryTextEncoding
    let nonAsciiRows: [TermValues]
}

enum LibraryImportPlanner {
    static func plan(
        document: LibraryCsvDocument,
        target: LibraryImportTarget,
        draft: LibraryDraft
    ) throws -> LibraryImportPlan {
        let existing: DraftLibrary?
        switch target {
        case .new:
            existing = nil
        case .existing(let libraryID):
            guard let library = draft.find(libraryID), !library.pendingDelete else {
                throw NSError(domain: "LibraryImportPlanner", code: 1)
            }
            existing = library
        }

        let rows = existing?.rows ?? []
        var targets: [(values: TermValues, rowID: Int64?)] = []
        var known: [LibraryTermKey: Int] = [:]
        for row in rows {
            targets.append((row.row.values, row.rowID))
            let key = LibraryTermKey.from(row.row.values.spoken)
            if !key.isEmpty {
                known[key] = known[key] ?? (targets.count - 1)
            }
            if existing?.builtIn == true {
                known[row.row.key] = known[row.row.key] ?? (targets.count - 1)
            }
        }

        var operations: [LibraryImportOperation] = []
        var skippedRows = document.errors
        var nonAsciiRows: [TermValues] = []

        for term in document.terms {
            let key = LibraryTermKey.from(term.spoken)
            if key.isEmpty {
                skippedRows.append(LibraryCsvRowError(line: 0, kind: .emptySpoken, field: nil))
                continue
            }
            if hasNonAsciiLetter(term.spoken) || hasNonAsciiLetter(term.written) {
                nonAsciiRows.append(term)
            }
            if let met = known[key] {
                let targetRow = targets[met]
                if same(term, targetRow.values) {
                    operations.append(
                        LibraryImportOperation(
                            kind: .alreadyHere,
                            fileRow: term,
                            existingRowID: targetRow.rowID,
                            existingValues: targetRow.values))
                } else {
                    operations.append(
                        LibraryImportOperation(
                            kind: .writtenDifferently,
                            fileRow: term,
                            existingRowID: targetRow.rowID,
                            existingValues: targetRow.values))
                    targets[met] = (filesVersion(existing: targetRow.values, file: term), targetRow.rowID)
                }
            } else {
                operations.append(
                    LibraryImportOperation(
                        kind: .add,
                        fileRow: term,
                        existingRowID: nil,
                        existingValues: nil))
                known[key] = targets.count
                targets.append((term, nil))
            }
        }

        let takenNames = draft.libraries.filter { !$0.pendingDelete }.map(\.name)
        let suggestedName: String
        switch target {
        case .existing(let libraryID):
            suggestedName = draft.find(libraryID)?.name ?? ""
        case .new(let fileName):
            let header = committed(document.name)
            if !header.isEmpty {
                suggestedName = LibraryNaming.uniqueName(header, takenNames: takenNames)
            } else {
                let file = committed(
                    fileName.flatMap { URL(fileURLWithPath: $0).deletingPathExtension().lastPathComponent }
                )
                let base = file.isEmpty ? LibraryNaming.importedLibraryBaseName : file
                suggestedName = LibraryNaming.uniqueName(base, takenNames: takenNames)
            }
        }

        let basedOnTarget = committed(document.basedOn).isEmpty ? nil : draft.find(committed(document.basedOn))?.id
        return LibraryImportPlan(
            target: target,
            draftRevision: draft.revision,
            suggestedName: suggestedName,
            category: committed(document.category).isEmpty ? nil : committed(document.category),
            description: committed(document.description).isEmpty ? nil : committed(document.description),
            basedOnTarget: basedOnTarget,
            operations: operations,
            adds: operations.filter { $0.kind == .add }.count,
            writtenDifferently: operations.filter { $0.kind == .writtenDifferently }.count,
            alreadyHere: operations.filter { $0.kind == .alreadyHere }.count,
            removalRules: operations.filter { $0.kind == .add && $0.fileRow.written.isEmpty }.count,
            skipped: skippedRows.count,
            skippedRows: skippedRows,
            encoding: document.encoding,
            nonAsciiRows: nonAsciiRows)
    }

    static func filesVersion(existing: TermValues, file: TermValues) -> TermValues {
        var values = TermValues(existing.spoken, file.written, file.wholeWord, file.enabled)
        if !LibraryTermKey.areSame(existing.spoken, file.spoken) {
            values.spoken = file.spoken
        }
        return values
    }

    private static func committed(_ value: String?) -> String {
        LibraryMetadata.commit(value)
    }

    private static func same(_ file: TermValues, _ existing: TermValues) -> Bool {
        LibraryTermKey.areSame(file.spoken, existing.spoken)
            && file.written == existing.written
            && file.wholeWord == existing.wholeWord
            && file.enabled == existing.enabled
    }

    private static func hasNonAsciiLetter(_ value: String) -> Bool {
        value.unicodeScalars.contains { $0.value > 0x7f && CharacterSet.letters.contains($0) }
    }
}
