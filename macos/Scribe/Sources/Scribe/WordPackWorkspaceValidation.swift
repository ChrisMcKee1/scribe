import Foundation

extension WordPackWorkspace {
    /// Validate only content Save will write. Untouched legacy files are never normalized or rewritten.
    func validate() -> [LibraryValidationIssue] {
        state.libraries.filter { !$0.pendingDelete && contentChanged($0) }.flatMap(validate)
    }

    func validate(_ library: DraftLibrary) -> [LibraryValidationIssue] {
        var issues: [LibraryValidationIssue] = []
        if library.fileState != .available || conflictingLibraryIDs.contains(library.id.lowercased()) {
            return [LibraryValidationIssue(libraryID: library.id, rowID: nil, kind: .contentNotSaveable)]
        }
        for (value, field) in [
            (library.name, LibraryMetadataField.name), (library.category, .category),
            (library.description ?? "", .description),
        ] {
            if let issue = metadataIssue(library.id, value: value, field: field) { issues.append(issue) }
        }
        let rows = library.rows.filter { !$0.row.values.spoken.isEmpty || !$0.row.values.written.isEmpty }
        if rows.count > LibraryLimits.maxTermsPerLibrary {
            issues.append(LibraryValidationIssue(libraryID: library.id, rowID: nil, kind: .tooManyTerms))
        }
        var spoken: [LibraryTermKey: Int64] = [:]
        for draftRow in rows {
            let values = draftRow.row.values
            let key = LibraryTermKey.from(values.spoken)
            func issue(_ kind: LibraryValidationKind, _ field: TermField, _ other: Int64? = nil) {
                issues.append(
                    LibraryValidationIssue(
                        libraryID: library.id, rowID: draftRow.rowID, kind: kind, field: field, otherRowID: other))
            }
            if key.isEmpty { issue(.writtenWithoutSpoken, .spoken) }
            if let other = spoken[key], other != draftRow.rowID {
                issue(.duplicateSpoken, .spoken, other)
            } else {
                spoken[key] = draftRow.rowID
            }
            if values.written.isEmpty && !draftRow.removalIntent && !draftRow.legacyEmpty {
                issue(.emptyWrittenWithoutIntent, .written)
            }
            let old = baseline.libraries.first { $0.id == library.id }?.rows.first { $0.rowID == draftRow.rowID }
            if values.spoken.utf16.count > LibraryLimits.maxFieldLength && values.spoken != old?.row.values.spoken {
                issue(.fieldTooLong, .spoken)
            }
            if values.written.utf16.count > LibraryLimits.maxFieldLength && values.written != old?.row.values.written {
                issue(.fieldTooLong, .written)
            }
        }
        if library.builtIn {
            var owners: [LibraryTermKey: [Int64]] = [:]
            for row in rows { owners[row.row.key, default: []].append(row.rowID) }
            for row in rows {
                let met = (owners[row.row.key] ?? []) + (owners[LibraryTermKey.from(row.row.values.spoken)] ?? [])
                if let other = met.first(where: { $0 != row.rowID }) {
                    issues.append(
                        LibraryValidationIssue(
                            libraryID: library.id, rowID: row.rowID, kind: .duplicateSpoken,
                            field: .spoken, otherRowID: other))
                }
            }
        }
        return issues
    }

    func metadataIssue(_ libraryID: String, value: String, field: LibraryMetadataField) -> LibraryValidationIssue? {
        var kind: LibraryValidationKind?
        let current = draft.find(libraryID)
        let old = baseline.libraries.first { $0.id == libraryID }
        if field == .name && value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            kind = .emptyName
        } else if field == .name
            && state.libraries.contains(where: {
                $0.id != libraryID && !$0.pendingDelete && $0.name.caseInsensitiveCompare(value) == .orderedSame
            }) && value != old?.name
        {
            kind = .duplicateName
        } else if value.contains("\"") {
            let kept: String?
            switch field {
            case .name: kept = current?.name
            case .category: kept = current?.category
            case .description: kept = current?.description
            default: kept = nil
            }
            if value != kept || value.filter({ $0 == "\"" }).count % 2 != 0 {
                kind = .metadataDoubleQuote
            }
        } else if value.utf16.count > LibraryLimits.maxFieldLength {
            kind = .fieldTooLong
        }
        return kind.map {
            LibraryValidationIssue(libraryID: libraryID, rowID: nil, kind: $0, metadata: field)
        }
    }
}
