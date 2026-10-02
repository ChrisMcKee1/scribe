import Foundation

extension WordPackWorkspace {
    mutating func undo() {
        guard !isReadOnly else { return }
        while let entry = undoHistory.popLast() {
            let next = applying(entry, undoing: true)
            guard next != state else { continue }
            let before = state
            state = next
            redoHistory.append(WordPackUndoEntry(label: entry.label, before: next, after: before))
            revision += 1
            editRevision += 1
            return
        }
    }

    mutating func redo() {
        guard !isReadOnly else { return }
        while let entry = redoHistory.popLast() {
            let next = applying(entry, undoing: false)
            guard next != state else { continue }
            let before = state
            state = next
            undoHistory.append(WordPackUndoEntry(label: entry.label, before: before, after: next))
            revision += 1
            editRevision += 1
            return
        }
    }

    /// A stale structural undo changes only objects still equal to what that operation left.
    func applying(_ entry: WordPackUndoEntry, undoing: Bool) -> WordPackWorkspaceState {
        Self.merge(
            from: undoing ? entry.after : entry.before,
            to: undoing ? entry.before : entry.after, into: state)
    }

    static func merge(
        from: WordPackWorkspaceState, to: WordPackWorkspaceState, into current: WordPackWorkspaceState
    ) -> WordPackWorkspaceState {
        var result = current
        let ids = Set(from.libraries.map(\.id) + to.libraries.map(\.id))
        for id in ids {
            let old = from.libraries.first { $0.id == id }
            let desired = to.libraries.first { $0.id == id }
            let index = result.libraries.firstIndex { $0.id == id }
            if let index, result.libraries[index] == old {
                if let desired { result.libraries[index] = desired } else { result.libraries.remove(at: index) }
            } else if index == nil && old == nil, let desired {
                let desiredIndex = to.libraries.firstIndex { $0.id == id } ?? result.libraries.count
                result.libraries.insert(desired, at: min(desiredIndex, result.libraries.count))
            } else if index == nil, let desired, desired != old, !desired.pendingDelete {
                result.libraries.append(desired)
            } else if let index, let old, desired == nil,
                sameContent(result.libraries[index], old)
            {
                result.libraries[index].pendingDelete = true
            } else if let index, let old, let desired {
                var library = result.libraries[index]
                if library.name == old.name { library.name = desired.name }
                if library.category == old.category { library.category = desired.category }
                if library.description == old.description { library.description = desired.description }
                if library.basedOn == old.basedOn { library.basedOn = desired.basedOn }
                if library.pendingDelete == old.pendingDelete { library.pendingDelete = desired.pendingDelete }
                if library.resetEdits == old.resetEdits { library.resetEdits = desired.resetEdits }
                if library.recovering == old.recovering { library.recovering = desired.recovering }
                if library.recoveredEdits == old.recoveredEdits { library.recoveredEdits = desired.recoveredEdits }
                let rowIDs = Set(old.rows.map(\.rowID) + desired.rows.map(\.rowID))
                for rowID in rowIDs {
                    let oldRow = old.rows.first { $0.rowID == rowID }
                    let desiredRow = desired.rows.first { $0.rowID == rowID }
                    let rowIndex = library.rows.firstIndex { $0.rowID == rowID }
                    if let rowIndex, library.rows[rowIndex] == oldRow {
                        if let desiredRow {
                            library.rows[rowIndex] = desiredRow
                        } else {
                            library.rows.remove(at: rowIndex)
                        }
                    } else if rowIndex == nil && oldRow == nil, let desiredRow {
                        let position = desired.rows.firstIndex { $0.rowID == rowID } ?? library.rows.count
                        library.rows.insert(desiredRow, at: min(position, library.rows.count))
                    }
                }
                result.libraries[index] = library
            }
            let key = id.lowercased()
            if result.local.enabledIdSet.contains(key) == from.local.enabledIdSet.contains(key) {
                result.local.setEnabled(to.local.enabledIdSet.contains(key), for: id)
            }
            if result.local.aiPermissions[key] == from.local.aiPermissions[key] {
                result.local.aiPermissions[key] = to.local.aiPermissions[key]
            }
            if result.local.acceptedContent[key] == from.local.acceptedContent[key] {
                result.local.acceptedContent[key] = to.local.acceptedContent[key]
            }
        }
        // Markers belong to the row they downgrade. Undo must not downgrade a spelling edited after an import.
        let markerKeys = Set(from.local.legacyMarkers + to.local.legacyMarkers)
        for marker in markerKeys {
            let old = from.libraries.first { $0.id == marker.libraryId }?.rows.first { $0.row.key == marker.termKey }
            let currentRow = current.libraries.first { $0.id == marker.libraryId }?.rows.first {
                $0.row.key == marker.termKey
            }
            guard old == currentRow else { continue }
            result.local.legacyMarkers.removeAll { $0 == marker }
            if to.local.legacyMarkers.contains(marker) { result.local.legacyMarkers.append(marker) }
        }
        if result.local.aiPermissionsLost == from.local.aiPermissionsLost {
            result.local.aiPermissionsLost = to.local.aiPermissionsLost
            result.local.health = to.local.health
        }
        if result.local.aiUpgradeNotice == from.local.aiUpgradeNotice {
            result.local.aiUpgradeNotice = to.local.aiUpgradeNotice
        }
        if result.purgeIDs == from.purgeIDs { result.purgeIDs = to.purgeIDs }
        if result.restoreIDs == from.restoreIDs { result.restoreIDs = to.restoreIDs }
        return result
    }

    private static func sameContent(_ left: DraftLibrary, _ right: DraftLibrary) -> Bool {
        left.id == right.id && left.name == right.name && left.category == right.category
            && left.description == right.description && left.basedOn == right.basedOn
            && left.rows.map { $0.row.values } == right.rows.map { $0.row.values }
    }

    mutating func captureChangeSet() -> LibraryCaptureResult {
        let issues = validate()
        guard !isReadOnly, conflictingLibraryIDs.isEmpty, issues.isEmpty else {
            return LibraryCaptureResult(changeSet: nil, issues: issues)
        }
        let ids = Set(unsavedLibraryIDs)
        let libraries = state.libraries.filter { ids.contains($0.id) }
        reservedIDs.formUnion(libraries.map(\.id))
        var local = state.local
        for library in state.libraries where virgin(library) { local.removeState(for: library.id) }
        let changes = LibraryChangeSet(
            draftRevision: revision, expectedGeneration: committed.generation,
            libraries: libraries,
            expectedContent: Dictionary(
                uniqueKeysWithValues: libraries.map {
                    let item = committed.find(id: $0.id)
                    return (
                        $0.id,
                        WordPackExpectedContent(existed: item != nil, fileName: item?.fileName, hash: item?.contentHash))
                }),
            localState: local, recentlyDeleted: state.deleted,
            purgeIDs: state.purgeIDs, restoreIDs: state.restoreIDs,
            hasLocalChanges: local != baseline.local)
        return LibraryCaptureResult(changeSet: changes, issues: [])
    }

    /// Acknowledge the exact submitted draft, not the draft now on screen. Later edits remain unsaved.
    mutating func markSaved(_ changes: LibraryChangeSet, catalog: LibraryCatalog, deleted: [RecentlyDeletedWordPack]) {
        var submitted = baseline
        for library in changes.libraries {
            if let index = submitted.libraries.firstIndex(where: { $0.id == library.id }) {
                submitted.libraries[index] = library
            } else {
                submitted.libraries.append(library)
            }
        }
        submitted.local = changes.localState
        submitted.purgeIDs = changes.purgeIDs
        submitted.restoreIDs = changes.restoreIDs
        let later = state
        reload(catalog, deleted: deleted)
        state = Self.merge(from: submitted, to: later, into: state)
        let retained = Set(deleted.map(\.id))
        state.purgeIDs.formIntersection(retained)
        state.restoreIDs.formIntersection(retained)
        state.local.generation = catalog.generation
        revision = max(revision, changes.draftRevision + 1)
    }

    mutating func reload(_ catalog: LibraryCatalog, deleted: [RecentlyDeletedWordPack] = []) {
        let old = state
        let oldRevision = revision
        let oldEditRevision = editRevision
        let nextID = nextRowID
        self = WordPackWorkspace(catalog: catalog, recentlyDeleted: deleted, shipped: shipped)
        nextRowID = max(nextID, nextRowID)
        for index in state.libraries.indices {
            let library = state.libraries[index]
            state.libraries[index].rows = library.rows.map { row in
                if let kept = old.libraries.first(where: { $0.id == library.id })?.rows.first(where: {
                    $0.row.key == row.row.key
                }) {
                    return DraftTermRow(
                        rowID: kept.rowID, row: row.row, removalIntent: false, legacyEmpty: row.legacyEmpty)
                }
                return newRow(row.row, legacyEmpty: row.legacyEmpty)
            }
        }
        baseline = state
        revision = oldRevision + 1
        editRevision = oldEditRevision
    }

    mutating func rebase(_ catalog: LibraryCatalog, deleted: [RecentlyDeletedWordPack] = []) {
        let oldBaseline = baseline
        let current = state
        let dirty = Set(unsavedLibraryIDs)
        let conflicts = Set(
            dirty.filter {
                committed.find(id: $0)?.contentHash != catalog.find(id: $0)?.contentHash
                    || committed.find(id: $0)?.edits != catalog.find(id: $0)?.edits
            }.map { $0.lowercased() })
        reload(catalog, deleted: deleted)
        state = Self.merge(from: oldBaseline, to: current, into: state)
        conflictingLibraryIDs = conflicts
    }
}
