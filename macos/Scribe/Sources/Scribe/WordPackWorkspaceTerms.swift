import Foundation

extension WordPackWorkspace {
    @discardableResult
    mutating func addTerm(_ libraryID: String, values: TermValues, removalIntent: Bool = false) -> LibraryEditResult {
        guard canEditContent(libraryID), let index = index(libraryID) else { return refused(libraryID) }
        let values = LibraryEditor.commit(values)
        if state.libraries[index].builtIn && LibraryTermKey.from(values.spoken).isEmpty {
            return LibraryEditResult(
                applied: false,
                issue: LibraryValidationIssue(
                    libraryID: libraryID, rowID: nil, kind: .writtenWithoutSpoken, field: .spoken))
        }
        let row = state.libraries[index].builtIn ? BuiltInLibraryOverlay.add(values) : .custom(values)
        let draftRow = newRow(row, removalIntent: removalIntent)
        var next = state
        next.libraries[index].rows.append(draftRow)
        change(next)
        return LibraryEditResult(applied: true, issue: validate(state.libraries[index]).last)
    }

    @discardableResult
    mutating func editTerm(
        _ libraryID: String, rowID: Int64, values: TermValues, removalIntent: Bool = false
    ) -> LibraryEditResult {
        guard canEditContent(libraryID), let index = index(libraryID),
            let rowIndex = state.libraries[index].rows.firstIndex(where: { $0.rowID == rowID })
        else { return refused(libraryID) }
        let old = state.libraries[index].rows[rowIndex]
        let values = LibraryEditor.commitChanges(displayed: old.row.values, typed: values)
        let row = state.libraries[index].builtIn ? BuiltInLibraryOverlay.edit(old.row, values: values) : .custom(values)
        var next = state
        next.libraries[index].rows[rowIndex] = DraftTermRow(
            rowID: rowID, row: row, removalIntent: removalIntent,
            legacyEmpty: old.legacyEmpty && values.written.isEmpty)
        if row != old.row {
            next.local.legacyMarkers.removeAll { $0.libraryId == libraryID && $0.termKey == old.row.key }
        }
        change(next, label: removalIntent != old.removalIntent ? "Use as a removal rule" : nil)
        return LibraryEditResult(
            applied: true, issue: validate(state.libraries[index]).first { $0.rowID == rowID })
    }

    mutating func deleteTerm(_ libraryID: String, rowID: Int64) throws {
        guard canEditContent(libraryID), let index = index(libraryID),
            let row = state.libraries[index].rows.first(where: { $0.rowID == rowID }),
            LibraryEditor.availableCommands(for: row.row, editingText: false).contains(.delete)
        else { throw WordPackError.unavailable }
        var next = state
        next.libraries[index].rows.removeAll { $0.rowID == rowID }
        next.local.legacyMarkers.removeAll { $0.libraryId == libraryID && $0.termKey == row.row.key }
        change(next, label: "Delete term")
    }

    mutating func setTermEnabled(_ libraryID: String, rowID: Int64, enabled: Bool) throws {
        try updateRow(libraryID, rowID: rowID, label: enabled ? "Turn on term" : "Turn off term") { row, builtIn in
            if builtIn { return BuiltInLibraryOverlay.setEnabled(row, enabled: enabled) }
            var values = row.values
            values.enabled = enabled
            return .custom(values)
        }
    }

    mutating func restoreBuiltInValues(_ libraryID: String, rowID: Int64) throws {
        try updateRow(libraryID, rowID: rowID, label: "Restore built-in values") { row, builtIn in
            guard builtIn else { throw WordPackError.unavailable }
            return BuiltInLibraryOverlay.restoreShipped(row)
        }
    }

    mutating func restoreAllBuiltInValues(_ libraryID: String) throws {
        guard canEditContent(libraryID), let index = index(libraryID), state.libraries[index].builtIn else {
            throw WordPackError.unavailable
        }
        var next = state
        next.libraries[index].resetEdits = true
        next.libraries[index].rows = next.libraries[index].rows.compactMap { row in
            BuiltInLibraryOverlay.restoreShipped(row.row).map {
                DraftTermRow(rowID: row.rowID, row: $0, removalIntent: false, legacyEmpty: $0.values.written.isEmpty)
            }
        }
        change(next, label: "Restore all built-in values")
    }

    /// Recovery is explicit and staged. The corrupt/newer document is backed up before installation after commit.
    mutating func recoverBuiltIn(_ libraryID: String, previous: BuiltInLibraryEdits?) throws {
        try ensureWritable()
        guard let index = index(libraryID), state.libraries[index].builtIn,
            let shipped = shipped.first(where: { $0.id == libraryID })
        else { throw WordPackError.unavailable }
        if let previous {
            guard previous.library == libraryID, BuiltInLibraryOverlay.valid(previous) else {
                throw WordPackError.invalidEdits
            }
        }
        var next = state
        next.libraries[index].rows = BuiltInLibraryOverlay.applyRows(shipped: shipped, edits: previous).map {
            newRow($0, legacyEmpty: $0.values.written.isEmpty)
        }
        next.libraries[index].fileState = .available
        next.libraries[index].resetEdits = true
        next.libraries[index].recovering = true
        change(next, label: previous == nil ? "Back up and reset" : "Restore the previous copy")
    }

    mutating func keepRetiredBuiltIn(_ libraryID: String) throws -> String {
        try ensureWritable()
        guard let item = committed.find(id: libraryID), item.origin == .retiredBuiltIn, let edits = item.edits else {
            throw WordPackError.unavailable
        }
        let id = create(
            name: LibraryNaming.uniqueName(item.library.name, takenNames: names),
            category: "Custom", description: item.library.description, basedOn: nil,
            origin: .retiredBuiltIn, permission: showsAIPermission(libraryID))
        var next = state
        next.libraries[index(id)!].rows = edits.terms.compactMap(\.value).map {
            newRow(.custom($0), legacyEmpty: $0.written.isEmpty)
        }
        next.local.setEnabled(false, for: libraryID)
        change(next, label: "Keep retired word pack")
        return id
    }

    mutating func resolveReview(_ libraryID: String, rowID: Int64, choice: TermReviewChoice) throws {
        try updateRow(libraryID, rowID: rowID, label: "Review built-in update") { row, builtIn in
            guard builtIn else { throw WordPackError.unavailable }
            return BuiltInLibraryOverlay.resolveReview(row, choice: choice)
        }
    }

    mutating func useMySpelling(_ libraryID: String, rowID: Int64) throws {
        try ensureWritable()
        guard let row = rowsOf(libraryID).first(where: { $0.rowID == rowID }) else {
            throw WordPackError.unavailable
        }
        var next = state
        next.local.legacyMarkers.removeAll { $0.libraryId == libraryID && $0.termKey == row.row.key }
        change(next, label: "Use my spelling")
    }

    mutating func turnOffInOtherLibraries(_ libraryID: String, rowID: Int64, otherIDs: [String]) throws {
        try ensureWritable()
        guard let source = rowsOf(libraryID).first(where: { $0.rowID == rowID }) else {
            throw WordPackError.unavailable
        }
        let before = state
        let historyCount = undoHistory.count
        for id in otherIDs where id != libraryID {
            for row in rowsOf(id) where LibraryTermKey.areSame(row.row.values.spoken, source.row.values.spoken) {
                try setTermEnabled(id, rowID: row.rowID, enabled: false)
            }
        }
        if state != before {
            undoHistory.removeLast(undoHistory.count - historyCount)
            undoHistory.append(WordPackUndoEntry(label: "Turn off in other word packs", before: before, after: state))
        }
    }

    @discardableResult
    mutating func applyImport(_ plan: LibraryImportPlan, choice: ImportConflictChoice) throws -> String {
        try ensureWritable()
        guard plan.draftRevision == revision else { throw WordPackError.staleDraft }
        let before = state
        let id: String
        switch plan.target {
        case .new:
            id = create(
                name: plan.suggestedName, category: plan.category ?? "Custom", description: plan.description,
                basedOn: plan.basedOnTarget, origin: .imported, permission: false)
        case .existing(let libraryID):
            guard canEditContent(libraryID) else { throw WordPackError.unavailable }
            id = libraryID
        }
        let index = index(id)!
        var next = state
        for operation in plan.operations {
            let key = LibraryTermKey.from(operation.fileRow.spoken)
            let matched = next.libraries[index].rows.firstIndex {
                $0.rowID == operation.existingRowID || $0.row.key == key
                    || LibraryTermKey.areSame($0.row.values.spoken, operation.fileRow.spoken)
            }
            if let matched {
                guard choice == .useFilesVersion, operation.kind != .alreadyHere else { continue }
                let old = next.libraries[index].rows[matched]
                let values = LibraryImportPlanner.filesVersion(existing: old.row.values, file: operation.fileRow)
                let row =
                    next.libraries[index].builtIn
                    ? BuiltInLibraryOverlay.edit(old.row, values: values) : .custom(values)
                next.libraries[index].rows[matched] = DraftTermRow(
                    rowID: old.rowID, row: row, removalIntent: false, legacyEmpty: values.written.isEmpty)
                next.local.legacyMarkers.removeAll { $0.libraryId == id && $0.termKey == old.row.key }
            } else {
                let values = operation.fileRow
                let row = next.libraries[index].builtIn ? BuiltInLibraryOverlay.add(values) : .custom(values)
                next.libraries[index].rows.append(newRow(row, legacyEmpty: values.written.isEmpty))
            }
        }
        change(next)
        if before != state {
            undoHistory.append(WordPackUndoEntry(label: "Import terms", before: before, after: state))
        }
        return id
    }

    mutating func updateRow(
        _ libraryID: String, rowID: Int64, label: String,
        update: (LibraryRow, Bool) throws -> LibraryRow?
    ) throws {
        guard canEditContent(libraryID), let index = index(libraryID),
            let rowIndex = state.libraries[index].rows.firstIndex(where: { $0.rowID == rowID })
        else { throw WordPackError.unavailable }
        let old = state.libraries[index].rows[rowIndex]
        let row = try update(old.row, state.libraries[index].builtIn)
        var next = state
        if let row {
            next.libraries[index].rows[rowIndex] = DraftTermRow(
                rowID: rowID, row: row, removalIntent: old.removalIntent, legacyEmpty: old.legacyEmpty)
        } else {
            next.libraries[index].rows.remove(at: rowIndex)
        }
        change(next, label: label)
    }
}
