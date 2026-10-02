import Foundation

/// Pure, single-owner draft. A view model keeps this value; search, sorting and selection never mutate it.
/// Capture, prepare, commit and acknowledge are separate so Settings can commit all participants together.
struct WordPackWorkspace: Equatable, Sendable {
    var committed: LibraryCatalog
    var revision: Int64
    var editRevision: Int64 = 0
    var conflictingLibraryIDs: Set<String> = []
    var state: WordPackWorkspaceState
    var baseline: WordPackWorkspaceState
    var nextRowID: Int64 = 1
    var undoHistory: [WordPackUndoEntry] = []
    var redoHistory: [WordPackUndoEntry] = []
    var reservedIDs: Set<String> = []
    let shipped: [DictionaryLibrary]

    init(
        catalog: LibraryCatalog,
        recentlyDeleted: [RecentlyDeletedWordPack] = [],
        shipped: [DictionaryLibrary] = BuiltInDictionaryLibraries.all
    ) {
        committed = catalog
        revision = 1
        self.shipped = shipped
        state = WordPackWorkspaceState(libraries: [], local: catalog.localState, deleted: recentlyDeleted)
        baseline = state
        state.libraries = catalog.libraries.map { item in
            let rows: [LibraryRow]
            if item.builtIn && item.state == .available {
                let source = shipped.first { LibraryTermKey.areSame($0.id, item.id) } ?? item.library
                rows = BuiltInLibraryOverlay.applyRows(shipped: source, edits: item.edits)
            } else {
                rows = item.library.entries.map { LibraryRow.custom(TermValues(entry: $0)) }
            }
            return DraftLibrary(
                id: item.id, name: item.library.name, category: item.library.category,
                description: item.library.description, builtIn: item.builtIn,
                rows: rows.map {
                    defer { nextRowID += 1 }
                    return DraftTermRow(
                        rowID: nextRowID, row: $0, removalIntent: false, legacyEmpty: $0.values.written.isEmpty)
                },
                basedOn: item.library.basedOn, fileState: item.state, origin: item.origin)
        }
        baseline = state
    }

    /// Compatibility initializer for pure search/import deciders; live editors start from a catalog.
    init(revision: Int64 = 1, libraries: [DraftLibrary]) {
        committed = LibraryCatalog(generation: 0, libraries: [], localState: .absent)
        self.revision = revision
        shipped = []
        state = WordPackWorkspaceState(libraries: libraries, local: .absent, deleted: [])
        baseline = state
        nextRowID = (libraries.flatMap(\.rows).map(\.rowID).max() ?? 0) + 1
    }

    var draft: LibraryDraft { LibraryDraft(revision: revision, libraries: state.libraries) }
    var localState: LibraryLocalState { state.local }
    var recentlyDeleted: [RecentlyDeletedWordPack] {
        state.deleted.filter { !state.purgeIDs.contains($0.id) && !state.restoreIDs.contains($0.id) }
    }
    var isReadOnly: Bool { committed.localState.health == .newer }
    var unsavedLibraryIDs: [String] {
        state.libraries.filter { library in
            if let old = baseline.libraries.first(where: { $0.id == library.id }) {
                return old != library
                    || enabled(library.id) != baseline.local.enabledIdSet.contains(library.id.lowercased())
                    || state.local.aiPermissions[library.id.lowercased()]
                        != baseline.local.aiPermissions[library.id.lowercased()]
                    || state.local.legacyMarkers != baseline.local.legacyMarkers
            }
            return !virgin(library)
        }.map(\.id)
    }
    var hasUnsavedChanges: Bool {
        !unsavedLibraryIDs.isEmpty || state.local.aiPermissionsLost != baseline.local.aiPermissionsLost
            || state.local.aiUpgradeNotice != baseline.local.aiUpgradeNotice
            || state.purgeIDs != baseline.purgeIDs || state.restoreIDs != baseline.restoreIDs
    }
    var canUndo: Bool { undoHistory.contains { applying($0, undoing: true) != state } }
    var canRedo: Bool { redoHistory.contains { applying($0, undoing: false) != state } }
    var undoLabel: String? { undoHistory.last { applying($0, undoing: true) != state }?.label }

    func rowsOf(_ libraryID: String) -> [DraftTermRow] { draft.find(libraryID)?.rows ?? [] }
    func enabled(_ libraryID: String) -> Bool { state.local.enabledIdSet.contains(libraryID.lowercased()) }
    func canEditContent(_ libraryID: String) -> Bool {
        guard let library = draft.find(libraryID) else { return false }
        return !isReadOnly && !library.pendingDelete && library.fileState == .available
            && !conflictingLibraryIDs.contains(libraryID.lowercased())
            && (!library.builtIn || library.origin != .retiredBuiltIn)
    }

    func showsAIPermission(_ libraryID: String) -> Bool {
        guard state.local.health == .ok || state.local.health == .absent, let library = draft.find(libraryID) else {
            return false
        }
        let id = libraryID.lowercased()
        if let item = committed.find(id: libraryID), !contentChanged(library) {
            if library.builtIn {
                if let accepted = state.local.acceptedContent[id], accepted != item.contentHash?.value { return false }
            } else if state.local.acceptedContent[id] != item.contentHash?.value || item.contentHash == nil {
                return false
            }
        }
        return state.local.aiPermissions[id] ?? (!state.local.aiPermissionsLost && library.builtIn)
    }

    @discardableResult
    mutating func createLibrary() throws -> String {
        try ensureWritable()
        let name = LibraryNaming.uniqueName(LibraryNaming.newLibraryBaseName, takenNames: names)
        return create(
            name: name, category: "Custom", description: nil, basedOn: nil, origin: .created, permission: false)
    }

    @discardableResult
    mutating func rename(_ libraryID: String, name: String) -> LibraryEditResult {
        guard let index = index(libraryID), !state.libraries[index].builtIn else { return refused(libraryID) }
        guard canEditContent(libraryID) else { return refused(libraryID) }
        let value = LibraryMetadata.commit(name)
        if let issue = metadataIssue(libraryID, value: value, field: .name) {
            return LibraryEditResult(applied: false, issue: issue)
        }
        var next = state
        next.libraries[index].name = value
        change(next)
        return LibraryEditResult(applied: true, issue: nil)
    }

    @discardableResult
    mutating func setDetails(_ libraryID: String, category: String, description: String?) -> LibraryEditResult {
        guard let index = index(libraryID), !state.libraries[index].builtIn, canEditContent(libraryID) else {
            return refused(libraryID)
        }
        let category = LibraryMetadata.commit(category)
        let description = description.map(LibraryMetadata.commit)
        if let issue = metadataIssue(libraryID, value: category, field: .category)
            ?? metadataIssue(libraryID, value: description ?? "", field: .description)
        {
            return LibraryEditResult(applied: false, issue: issue)
        }
        var next = state
        next.libraries[index].category = category
        next.libraries[index].description = description
        change(next)
        return LibraryEditResult(applied: true, issue: nil)
    }

    mutating func setEnabled(_ libraryID: String, enabled: Bool) throws {
        try ensureWritable()
        guard draft.find(libraryID) != nil else { throw WordPackError.unavailable }
        var next = state
        next.local.setEnabled(enabled, for: libraryID)
        change(next, label: enabled ? "Turn on word pack" : "Turn off word pack")
    }

    @discardableResult
    mutating func setAIPermission(_ libraryID: String, permitted: Bool) -> LibraryEditResult {
        guard !isReadOnly, let library = draft.find(libraryID), library.fileState == .available else {
            return refused(libraryID)
        }
        var next = state
        next.local.setAIPermission(permitted, for: libraryID)
        if next.local.health == .unreadable { next.local.health = .ok }
        if let item = committed.find(id: libraryID) {
            next.local.setAcceptedContent(item.contentHash, for: libraryID)
        }
        change(next, label: "Change AI cleanup choice")
        return LibraryEditResult(applied: true, issue: nil)
    }

    mutating func confirmAIPermissions() throws {
        try ensureWritable()
        var next = state
        for library in state.libraries {
            next.local.setAIPermission(showsAIPermission(library.id), for: library.id)
        }
        next.local.health = .ok
        next.local.aiPermissionsLost = false
        change(next, label: "Use these AI cleanup choices")
    }

    @discardableResult
    mutating func duplicate(_ libraryID: String) throws -> String {
        try ensureWritable()
        guard let source = draft.find(libraryID), canEditContent(libraryID) else { throw WordPackError.unavailable }
        let before = state
        let name = LibraryNaming.uniqueName(source.name + " - Copy", takenNames: names)
        let id = create(
            name: name, category: source.category, description: source.description, basedOn: source.id,
            origin: .duplicated, permission: showsAIPermission(source.id))
        let rows = source.rows.map { newRow(.custom($0.row.values), legacyEmpty: $0.row.values.written.isEmpty) }
        var next = state
        next.libraries[index(id)!].rows = rows
        change(next)
        undoHistory.append(WordPackUndoEntry(label: "Duplicate word pack", before: before, after: state))
        return id
    }

    mutating func useCopyInstead(_ libraryID: String) throws {
        try ensureWritable()
        guard let copy = draft.find(libraryID), let original = copy.basedOn, draft.find(original) != nil else {
            throw WordPackError.unavailable
        }
        var next = state
        next.local.setEnabled(false, for: original)
        next.local.setEnabled(true, for: copy.id)
        change(next, label: "Use this copy instead")
    }

    mutating func deleteLibrary(_ libraryID: String) throws {
        try ensureWritable()
        guard let index = index(libraryID), !state.libraries[index].builtIn, canEditContent(libraryID) else {
            throw WordPackError.unavailable
        }
        var next = state
        if !baseline.libraries.contains(where: { $0.id == libraryID }) && !reservedIDs.contains(libraryID) {
            next.libraries.remove(at: index)
        } else {
            next.libraries[index].pendingDelete = true
        }
        next.local.removeState(for: libraryID)
        change(next, label: "Delete word pack")
    }

    @discardableResult
    mutating func restoreDeleted(_ id: UUID) throws -> String {
        try ensureWritable()
        guard let deleted = recentlyDeleted.first(where: { $0.id == id }) else { throw WordPackError.unavailable }
        let before = state
        let name = LibraryNaming.uniqueName(deleted.name, takenNames: names)
        let libraryID = create(
            name: name, category: deleted.category, description: deleted.description,
            basedOn: deleted.basedOn, origin: .restored, permission: false)
        var next = state
        next.libraries[index(libraryID)!].rows = deleted.values.map {
            newRow(.custom($0), legacyEmpty: $0.written.isEmpty)
        }
        next.restoreIDs.insert(id)
        change(next)
        undoHistory.append(WordPackUndoEntry(label: "Restore word pack", before: before, after: state))
        return libraryID
    }

    mutating func deletePermanently(_ id: UUID) throws {
        try ensureWritable()
        guard recentlyDeleted.contains(where: { $0.id == id }) else { throw WordPackError.unavailable }
        var next = state
        next.purgeIDs.insert(id)
        change(next, label: "Delete permanently")
    }

    mutating func dismissAIUpgradeNotice() throws {
        try ensureWritable()
        var next = state
        next.local.aiUpgradeNotice = []
        change(next, label: "Dismiss notice")
    }

    mutating func discardLibrary(_ libraryID: String) throws {
        try ensureWritable()
        var next = state
        if let old = baseline.libraries.first(where: { $0.id == libraryID }), let index = index(libraryID) {
            next.libraries[index] = old
            next.local.setEnabled(baseline.local.enabledIdSet.contains(libraryID.lowercased()), for: libraryID)
            next.local.aiPermissions[libraryID.lowercased()] = baseline.local.aiPermissions[libraryID.lowercased()]
        } else {
            next.libraries.removeAll { $0.id == libraryID }
            next.local.removeState(for: libraryID)
        }
        conflictingLibraryIDs.remove(libraryID.lowercased())
        change(next, label: "Discard word pack changes")
    }

    func contentChanged(_ library: DraftLibrary) -> Bool {
        guard let old = baseline.libraries.first(where: { $0.id == library.id }) else { return !virgin(library) }
        return old.name != library.name || old.category != library.category || old.description != library.description
            || old.rows != library.rows || old.basedOn != library.basedOn || old.pendingDelete != library.pendingDelete
            || old.resetEdits != library.resetEdits || old.recovering != library.recovering
            || old.recoveredEdits != library.recoveredEdits
    }

    static func rowIDIn(_ draft: LibraryDraft, _ libraryID: String, _ index: Int) -> Int64? {
        guard let rows = draft.find(libraryID)?.rows, rows.indices.contains(index) else { return nil }
        return rows[index].rowID
    }

    var names: [String] { state.libraries.filter { !$0.pendingDelete }.map(\.name) }

    func index(_ id: String) -> Int? {
        state.libraries.firstIndex { LibraryTermKey.areSame($0.id, id) }
    }

    func ensureWritable() throws {
        if isReadOnly { throw WordPackError.readOnly }
    }

    func virgin(_ library: DraftLibrary) -> Bool {
        library.origin == .created && library.name == library.creationName
            && library.rows.allSatisfy { $0.row.values.spoken.isEmpty && $0.row.values.written.isEmpty }
            && library.category == "Custom" && library.description == nil && !library.pendingDelete
            && state.local.aiPermissions[library.id.lowercased()] != true && enabled(library.id)
    }

    mutating func create(
        name: String, category: String, description: String?, basedOn: String?, origin: LibraryOrigin, permission: Bool
    ) -> String {
        let taken = state.libraries.map(\.id) + state.deleted.map(\.libraryID) + Array(reservedIDs)
        let id = LibraryNaming.newCustomID(name: name, takenIDs: taken)
        var next = state
        next.libraries.append(
            DraftLibrary(
                id: id, name: name, category: category, description: description,
                builtIn: false, rows: [], basedOn: basedOn, origin: origin))
        if origin == .created { next.libraries[next.libraries.count - 1].creationName = name }
        next.local.setEnabled(true, for: id)
        next.local.setAIPermission(permission, for: id)
        change(next)
        return id
    }

    mutating func newRow(_ row: LibraryRow, removalIntent: Bool = false, legacyEmpty: Bool = false) -> DraftTermRow {
        defer { nextRowID += 1 }
        return DraftTermRow(rowID: nextRowID, row: row, removalIntent: removalIntent, legacyEmpty: legacyEmpty)
    }

    mutating func change(_ next: WordPackWorkspaceState, label: String? = nil) {
        guard state != next else { return }
        if let label { undoHistory.append(WordPackUndoEntry(label: label, before: state, after: next)) }
        redoHistory.removeAll()
        state = next
        revision += 1
        editRevision += 1
    }

    func refused(_ id: String) -> LibraryEditResult {
        LibraryEditResult(
            applied: false,
            issue: LibraryValidationIssue(libraryID: id, rowID: nil, kind: .contentNotSaveable))
    }
}

typealias LibraryWorkspace = WordPackWorkspace
