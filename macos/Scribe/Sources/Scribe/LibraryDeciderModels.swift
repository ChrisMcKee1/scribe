import Foundation

struct DraftTermRow: Equatable, Sendable {
    let rowID: Int64
    let row: LibraryRow
    let removalIntent: Bool
    let legacyEmpty: Bool
}

enum TermOrigin: Equatable, Sendable {
    case custom
    case shipped
    case edited
    case pinned
    case off
    case added
    case noLongerShipped
}

struct LibraryRow: Equatable, Sendable {
    let key: LibraryTermKey
    let values: TermValues
    let origin: TermOrigin
    let shipped: TermValues?
    let edit: BuiltInTermEdit?
    let review: TermReview?

    init(
        key: LibraryTermKey? = nil,
        values: TermValues,
        origin: TermOrigin,
        shipped: TermValues? = nil,
        edit: BuiltInTermEdit? = nil,
        review: TermReview? = nil
    ) {
        self.key = key ?? LibraryTermKey.from(values.spoken)
        self.values = values
        self.origin = origin
        self.shipped = shipped
        self.edit = edit
        self.review = review
    }

    static func custom(_ values: TermValues) -> LibraryRow {
        LibraryRow(values: values, origin: .custom)
    }
}

struct DraftLibrary: Equatable, Sendable {
    let id: String
    let name: String
    let category: String
    let description: String?
    let builtIn: Bool
    let rows: [DraftTermRow]
    let basedOn: String?
    let pendingDelete: Bool
    let enabled: Bool
    let aiPermitted: Bool

    init(
        id: String,
        name: String,
        category: String,
        description: String?,
        builtIn: Bool,
        rows: [DraftTermRow],
        basedOn: String? = nil,
        pendingDelete: Bool = false,
        enabled: Bool = false,
        aiPermitted: Bool = true
    ) {
        self.id = id
        self.name = name
        self.category = category
        self.description = description
        self.builtIn = builtIn
        self.rows = rows
        self.basedOn = basedOn
        self.pendingDelete = pendingDelete
        self.enabled = enabled
        self.aiPermitted = aiPermitted
    }
}

struct LibraryDraft: Equatable, Sendable {
    let revision: Int64
    var libraries: [DraftLibrary]

    func find(_ id: String?) -> DraftLibrary? {
        guard let id else {
            return nil
        }
        return libraries.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }
}

struct LibraryWrite: Equatable, Sendable {
    let libraryID: String
    let builtIn: Bool
    let content: DictionaryLibrary?
    let builtInEdits: BuiltInLibraryEdits?
    let enabled: Bool
    let aiPermitted: Bool

    init(
        libraryID: String,
        builtIn: Bool,
        content: DictionaryLibrary?,
        builtInEdits: BuiltInLibraryEdits? = nil,
        enabled: Bool,
        aiPermitted: Bool
    ) {
        self.libraryID = libraryID
        self.builtIn = builtIn
        self.content = content
        self.builtInEdits = builtInEdits
        self.enabled = enabled
        self.aiPermitted = aiPermitted
    }
}

struct LibraryDeletion: Equatable, Sendable {
    let libraryID: String
}

enum RecentlyDeletedActionKind: Sendable {
    case restore
    case deletePermanently
}

struct RecentlyDeletedLibrary: Equatable, Sendable, Identifiable {
    let entryName: String
    let originalID: String
    let name: String
    let termCount: Int
    let deletedAt: Date
    let state: LibraryFileState
    let contentHash: LibraryContentHash?

    var id: String { entryName }
}

struct RecentlyDeletedAction: Equatable, Sendable {
    let kind: RecentlyDeletedActionKind
    let entryName: String
    let restoreAsID: String?
}

struct LibraryChangeSet: Equatable, Sendable {
    let baseGeneration: Int64
    let draftRevision: Int64
    let writes: [LibraryWrite]
    let deletions: [LibraryDeletion]
    let recentlyDeletedActions: [RecentlyDeletedAction]
    let localState: LibraryLocalState
    let localStateChanged: Bool

    var isEmpty: Bool {
        writes.isEmpty && deletions.isEmpty && recentlyDeletedActions.isEmpty && !localStateChanged
    }
}

struct LibraryCaptureResult: Sendable {
    let changeSet: LibraryChangeSet?
    let issues: [LibraryValidationIssue]
}

struct LibraryEditResult: Sendable {
    let applied: Bool
    let issue: LibraryValidationIssue?
}

struct LibraryWorkspace: Sendable {
    private var base: LibraryDraft
    private var undoStack: [(String, LibraryDraft)] = []
    private var redoStack: [(String, LibraryDraft)] = []
    private var recentlyDeletedActions: [RecentlyDeletedAction] = []
    var draft: LibraryDraft

    init(revision: Int64 = 1, libraries: [DraftLibrary]) {
        let initial = LibraryDraft(revision: revision, libraries: libraries)
        base = initial
        draft = initial
    }

    init(catalog: LibraryCatalog) {
        let enabled = catalog.localState.enabledIdSet
        let ai = catalog.localState.aiPermissions
        var nextRowID: Int64 = 1
        let libraries = catalog.libraries.map { item in
            DraftLibrary(
                id: item.id,
                name: item.library.name,
                category: item.library.category,
                description: item.library.description,
                builtIn: item.builtIn,
                rows: item.library.entries.map { entry in
                    defer { nextRowID += 1 }
                    let values = TermValues(entry: entry)
                    let key = LibraryTermKey.from(entry.pattern)
                    let origin: TermOrigin =
                        item.builtIn
                        ? (item.library.authoredKeys.contains(key) ? .edited : .shipped)
                        : .custom
                    let edit = item.edits?.terms.first { $0.termKey == key }
                    let shipped = item.builtIn ? Self.shippedValues(libraryID: item.id, key: key) : nil
                    return DraftTermRow(
                        rowID: nextRowID,
                        row: LibraryRow(
                            key: key,
                            values: values,
                            origin: origin,
                            shipped: shipped ?? (item.builtIn ? values : nil),
                            edit: edit,
                            review: Self.review(values: values, shipped: shipped, edit: edit)),
                        removalIntent: false,
                        legacyEmpty: entry.replacement.isEmpty)
                },
                basedOn: item.library.basedOn,
                enabled: enabled.contains(item.id.lowercased()),
                aiPermitted: ai[item.id.lowercased()] ?? true)
        }
        let initial = LibraryDraft(revision: catalog.generation == 0 ? 1 : catalog.generation, libraries: libraries)
        base = initial
        draft = initial
    }

    func rowsOf(_ libraryID: String) -> [DraftTermRow] {
        draft.find(libraryID)?.rows ?? []
    }

    var canUndo: Bool { !undoStack.isEmpty }
    var canRedo: Bool { !redoStack.isEmpty }
    var undoLabel: String? { undoStack.last?.0 }
    var hasUnsavedChanges: Bool { draft.libraries != base.libraries || !recentlyDeletedActions.isEmpty }

    mutating func createLibrary(name: String? = nil) -> String {
        let takenNames = draft.libraries.map(\.name)
        let title = LibraryNaming.uniqueName(
            LibraryMetadata.commit(name).isEmpty ? "New word pack" : LibraryMetadata.commit(name),
            takenNames: takenNames)
        let id = LibraryNaming.newCustomID(name: title, takenIDs: draft.libraries.map(\.id))
        let library = DraftLibrary(
            id: id,
            name: title,
            category: "Custom",
            description: nil,
            builtIn: false,
            rows: [],
            enabled: true,
            aiPermitted: false)
        structural("New word pack") {
            $0.libraries.append(library)
        }
        return id
    }

    mutating func rename(_ libraryID: String, name: String) -> LibraryEditResult {
        guard let index = libraryIndex(libraryID), !draft.libraries[index].builtIn else {
            return LibraryEditResult(applied: false, issue: nil)
        }
        let committed = LibraryMetadata.commit(name)
        if committed.isEmpty {
            return LibraryEditResult(
                applied: false,
                issue: LibraryValidationIssue(
                    libraryID: libraryID,
                    rowID: nil,
                    kind: .emptyName,
                    metadata: .name))
        }
        if draft.libraries.enumerated().contains(where: { offset, library in
            offset != index && !library.pendingDelete && library.name.caseInsensitiveCompare(committed) == .orderedSame
        }) {
            return LibraryEditResult(
                applied: false,
                issue: LibraryValidationIssue(
                    libraryID: libraryID,
                    rowID: nil,
                    kind: .duplicateName,
                    metadata: .name))
        }
        change {
            $0.libraries[index] = copy($0.libraries[index], name: committed)
        }
        return LibraryEditResult(applied: true, issue: nil)
    }

    mutating func setEnabled(_ libraryID: String, enabled: Bool) {
        guard let index = libraryIndex(libraryID), draft.libraries[index].enabled != enabled else { return }
        structural(enabled ? "Turn on word pack" : "Turn off word pack") {
            $0.libraries[index] = copy($0.libraries[index], enabled: enabled)
        }
    }

    mutating func setAiPermission(_ libraryID: String, permitted: Bool) {
        guard let index = libraryIndex(libraryID), draft.libraries[index].aiPermitted != permitted else { return }
        change {
            $0.libraries[index] = copy($0.libraries[index], aiPermitted: permitted)
        }
    }

    mutating func deleteLibrary(_ libraryID: String) {
        guard let index = libraryIndex(libraryID), !draft.libraries[index].builtIn else { return }
        structural("Delete word pack") {
            $0.libraries[index] = copy($0.libraries[index], pendingDelete: true)
        }
    }

    mutating func restoreRecentlyDeleted(_ entry: RecentlyDeletedLibrary, restoreAsID: String) {
        recentlyDeletedActions.append(
            RecentlyDeletedAction(kind: .restore, entryName: entry.entryName, restoreAsID: restoreAsID))
        draft = bumped(draft)
    }

    mutating func deleteRecentlyDeletedPermanently(_ entry: RecentlyDeletedLibrary) {
        recentlyDeletedActions.append(
            RecentlyDeletedAction(kind: .deletePermanently, entryName: entry.entryName, restoreAsID: nil))
        draft = bumped(draft)
    }

    mutating func addTerm(_ libraryID: String, values: TermValues) -> LibraryEditResult {
        guard let index = libraryIndex(libraryID), !draft.libraries[index].pendingDelete else {
            return LibraryEditResult(applied: false, issue: nil)
        }
        let committed = LibraryEditor.commit(values)
        let key = LibraryTermKey.from(committed.spoken)
        if key.isEmpty {
            return LibraryEditResult(
                applied: false,
                issue: LibraryValidationIssue(
                    libraryID: libraryID, rowID: nil, kind: .writtenWithoutSpoken, field: .spoken))
        }
        if draft.libraries[index].rows.contains(where: { $0.row.key == key }) {
            return LibraryEditResult(
                applied: false,
                issue: LibraryValidationIssue(libraryID: libraryID, rowID: nil, kind: .duplicateSpoken, field: .spoken))
        }
        let row = DraftTermRow(
            rowID: nextRowID(),
            row: LibraryRow(values: committed, origin: draft.libraries[index].builtIn ? .added : .custom),
            removalIntent: committed.written.isEmpty,
            legacyEmpty: false)
        structural("Add term") {
            var rows = $0.libraries[index].rows
            rows.append(row)
            $0.libraries[index] = copy($0.libraries[index], rows: rows)
        }
        return LibraryEditResult(applied: true, issue: nil)
    }

    mutating func editTerm(_ libraryID: String, rowID: Int64, values: TermValues) -> LibraryEditResult {
        guard let libraryIndex = libraryIndex(libraryID),
            let rowIndex = draft.libraries[libraryIndex].rows.firstIndex(where: { $0.rowID == rowID })
        else {
            return LibraryEditResult(applied: false, issue: nil)
        }
        let committed = LibraryEditor.commit(values)
        let key = LibraryTermKey.from(committed.spoken)
        if key.isEmpty {
            return LibraryEditResult(
                applied: false,
                issue: LibraryValidationIssue(
                    libraryID: libraryID, rowID: rowID, kind: .writtenWithoutSpoken, field: .spoken))
        }
        if draft.libraries[libraryIndex].rows.contains(where: { $0.rowID != rowID && $0.row.key == key }) {
            return LibraryEditResult(
                applied: false,
                issue: LibraryValidationIssue(
                    libraryID: libraryID, rowID: rowID, kind: .duplicateSpoken, field: .spoken))
        }
        change {
            var rows = $0.libraries[libraryIndex].rows
            let old = rows[rowIndex]
            rows[rowIndex] = DraftTermRow(
                rowID: old.rowID,
                row: LibraryRow(
                    key: old.row.key,
                    values: committed,
                    origin: old.row.origin,
                    shipped: old.row.shipped,
                    edit: old.row.edit,
                    review: old.row.review),
                removalIntent: committed.written.isEmpty,
                legacyEmpty: old.legacyEmpty && committed.written.isEmpty)
            $0.libraries[libraryIndex] = copy($0.libraries[libraryIndex], rows: rows)
        }
        return LibraryEditResult(applied: true, issue: nil)
    }

    mutating func deleteTerm(_ libraryID: String, rowID: Int64) {
        guard let libraryIndex = libraryIndex(libraryID) else { return }
        structural("Delete term") {
            let rows = $0.libraries[libraryIndex].rows.filter { $0.rowID != rowID }
            $0.libraries[libraryIndex] = copy($0.libraries[libraryIndex], rows: rows)
        }
    }

    mutating func setTermEnabled(_ libraryID: String, rowID: Int64, enabled: Bool) {
        guard let libraryIndex = libraryIndex(libraryID),
            let rowIndex = draft.libraries[libraryIndex].rows.firstIndex(where: { $0.rowID == rowID })
        else { return }
        structural(enabled ? "Turn on term" : "Turn off term") {
            var rows = $0.libraries[libraryIndex].rows
            let old = rows[rowIndex]
            rows[rowIndex] = DraftTermRow(
                rowID: old.rowID,
                row: LibraryRow(
                    key: old.row.key,
                    values: TermValues(
                        old.row.values.spoken,
                        old.row.values.written,
                        old.row.values.wholeWord,
                        enabled),
                    origin: old.row.origin,
                    shipped: old.row.shipped,
                    edit: old.row.edit,
                    review: old.row.review),
                removalIntent: old.removalIntent,
                legacyEmpty: old.legacyEmpty)
            $0.libraries[libraryIndex] = copy($0.libraries[libraryIndex], rows: rows)
        }
    }

    mutating func resolveReview(_ libraryID: String, rowID: Int64, choice: TermReviewChoice) {
        guard let libraryIndex = libraryIndex(libraryID),
            let rowIndex = draft.libraries[libraryIndex].rows.firstIndex(where: { $0.rowID == rowID })
        else { return }
        let old = draft.libraries[libraryIndex].rows[rowIndex]
        guard let review = old.row.review else { return }
        let values: TermValues
        let intent: BuiltInTermIntent
        let acknowledged: TermValues?
        switch choice {
        case .keepMine:
            values = old.row.values
            intent = old.row.origin == .pinned ? .pinned : .edited
            acknowledged = review.updatedBuiltIn
        case .useUpdated:
            values = review.updatedBuiltIn
            intent = .pinned
            acknowledged = nil
        }
        let edit = BuiltInTermEdit(
            key: old.row.key.value,
            intent: intent,
            base: review.updatedBuiltIn,
            value: values,
            acknowledged: acknowledged)
        structural(choice == .keepMine ? "Keep my changes" : "Use built-in update") {
            var rows = $0.libraries[libraryIndex].rows
            rows[rowIndex] = DraftTermRow(
                rowID: old.rowID,
                row: LibraryRow(
                    key: old.row.key,
                    values: values,
                    origin: values == review.updatedBuiltIn ? .pinned : .edited,
                    shipped: review.updatedBuiltIn,
                    edit: edit,
                    review: nil),
                removalIntent: old.removalIntent,
                legacyEmpty: old.legacyEmpty)
            $0.libraries[libraryIndex] = copy($0.libraries[libraryIndex], rows: rows)
        }
    }

    mutating func undo() {
        guard let previous = undoStack.popLast() else { return }
        redoStack.append((previous.0, draft))
        draft = bumped(previous.1)
    }

    mutating func redo() {
        guard let next = redoStack.popLast() else { return }
        undoStack.append((next.0, draft))
        draft = bumped(next.1)
    }

    mutating func discard() {
        draft = bumped(base)
        undoStack.removeAll()
        redoStack.removeAll()
        recentlyDeletedActions.removeAll()
    }

    mutating func markSaved() {
        base = draft
        undoStack.removeAll()
        redoStack.removeAll()
        recentlyDeletedActions.removeAll()
    }

    func captureChangeSet() -> LibraryCaptureResult {
        let issues = validationIssues()
        guard issues.isEmpty else {
            return LibraryCaptureResult(changeSet: nil, issues: issues)
        }
        let baseById = Dictionary(uniqueKeysWithValues: base.libraries.map { ($0.id.lowercased(), $0) })
        let writes = draft.libraries.compactMap { library -> LibraryWrite? in
            guard !library.pendingDelete, library != baseById[library.id.lowercased()] else { return nil }
            return LibraryWrite(
                libraryID: library.id,
                builtIn: library.builtIn,
                content: dictionaryLibrary(from: library),
                builtInEdits: library.builtIn ? builtInEdits(from: library) : nil,
                enabled: library.enabled,
                aiPermitted: library.aiPermitted)
        }
        let deletions = draft.libraries.compactMap { library -> LibraryDeletion? in
            guard library.pendingDelete, !library.builtIn else { return nil }
            return LibraryDeletion(libraryID: library.id)
        }
        var state = LibraryLocalState.absent
        state.generation = draft.revision
        state.health = .ok
        state.enabledIds = draft.libraries.filter { $0.enabled && !$0.pendingDelete }.map(\.id)
        state.legacyEnabledIds = state.enabledIds
        state.aiPermissions = Dictionary(
            uniqueKeysWithValues: draft.libraries.map { ($0.id.lowercased(), $0.aiPermitted) })
        state.normalize()
        return LibraryCaptureResult(
            changeSet: LibraryChangeSet(
                baseGeneration: base.revision,
                draftRevision: draft.revision,
                writes: writes,
                deletions: deletions,
                recentlyDeletedActions: recentlyDeletedActions,
                localState: state,
                localStateChanged: localStateProjection(draft) != localStateProjection(base)),
            issues: [])
    }

    static func rowIDIn(_ draft: LibraryDraft, _ libraryID: String, _ index: Int) -> Int64? {
        guard let library = draft.find(libraryID), library.rows.indices.contains(index) else {
            return nil
        }
        return library.rows[index].rowID
    }

    private func validationIssues() -> [LibraryValidationIssue] {
        var issues: [LibraryValidationIssue] = []
        for library in draft.libraries where !library.pendingDelete {
            if !library.builtIn, LibraryMetadata.commit(library.name).isEmpty {
                issues.append(
                    LibraryValidationIssue(
                        libraryID: library.id,
                        rowID: nil,
                        kind: .emptyName,
                        metadata: .name))
            }
            var seen: [LibraryTermKey: Int64] = [:]
            for row in library.rows {
                let key = LibraryTermKey.from(row.row.values.spoken)
                if key.isEmpty {
                    issues.append(
                        LibraryValidationIssue(
                            libraryID: library.id,
                            rowID: row.rowID,
                            kind: .writtenWithoutSpoken,
                            field: .spoken))
                } else if let other = seen[key] {
                    issues.append(
                        LibraryValidationIssue(
                            libraryID: library.id,
                            rowID: row.rowID,
                            kind: .duplicateSpoken,
                            field: .spoken,
                            otherRowID: other))
                } else {
                    seen[key] = row.rowID
                }
                if row.row.values.written.isEmpty && !row.removalIntent && !row.legacyEmpty {
                    issues.append(
                        LibraryValidationIssue(
                            libraryID: library.id,
                            rowID: row.rowID,
                            kind: .emptyWrittenWithoutIntent,
                            field: .written))
                }
            }
        }
        return issues
    }

    private mutating func structural(_ label: String, update: (inout LibraryDraft) -> Void) {
        undoStack.append((label, draft))
        change(update: update)
    }

    private mutating func change(update: (inout LibraryDraft) -> Void) {
        var next = draft
        update(&next)
        guard next != draft else { return }
        draft = bumped(next)
        redoStack.removeAll()
    }

    private func bumped(_ value: LibraryDraft) -> LibraryDraft {
        LibraryDraft(revision: max(draft.revision, value.revision) + 1, libraries: value.libraries)
    }

    private func libraryIndex(_ id: String) -> Int? {
        draft.libraries.firstIndex { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }

    private func nextRowID() -> Int64 {
        (draft.libraries.flatMap(\.rows).map(\.rowID).max() ?? 0) + 1
    }

    private static func shippedValues(libraryID: String, key: LibraryTermKey) -> TermValues? {
        BuiltInDictionaryLibraries.all.first { $0.id.caseInsensitiveCompare(libraryID) == .orderedSame }?
            .entries
            .first { LibraryTermKey.from($0.pattern) == key }
            .map(TermValues.init(entry:))
    }

    private static func review(values: TermValues, shipped: TermValues?, edit: BuiltInTermEdit?) -> TermReview? {
        guard let shipped, let edit else { return nil }
        let questions: TermFields
        switch edit.intent {
        case .edited:
            guard let base = edit.base, let value = edit.value else { return nil }
            questions = editedQuestions(base: base, value: value, shipped: shipped, acknowledged: edit.acknowledged)
        case .pinned:
            guard let value = edit.value else { return nil }
            questions = pinnedQuestions(value: value, shipped: shipped, acknowledged: edit.acknowledged)
        default:
            return nil
        }
        guard !questions.isEmpty else { return nil }
        return TermReview(yours: values, updatedBuiltIn: shipped, differing: differences(values, shipped))
    }

    private static func editedQuestions(
        base: TermValues,
        value: TermValues,
        shipped: TermValues,
        acknowledged: TermValues?
    ) -> TermFields {
        var questions: TermFields = []
        for field in [TermFields.spoken, .written, .wholeWord, .enabled]
        where !same(field, value, base)
            && !same(field, shipped, base)
            && !same(field, value, shipped)
            && (acknowledged == nil || !same(field, acknowledged!, shipped))
        {
            questions.insert(field)
        }
        return questions
    }

    private static func pinnedQuestions(value: TermValues, shipped: TermValues, acknowledged: TermValues?) -> TermFields
    {
        var questions: TermFields = []
        for field in [TermFields.spoken, .written, .wholeWord, .enabled]
        where !same(field, shipped, value)
            && (acknowledged == nil || !same(field, shipped, acknowledged!))
        {
            questions.insert(field)
        }
        return questions
    }

    private static func differences(_ before: TermValues, _ after: TermValues) -> TermFields {
        var fields: TermFields = []
        for field in [TermFields.spoken, .written, .wholeWord, .enabled] where !same(field, before, after) {
            fields.insert(field)
        }
        return fields
    }

    private static func same(_ field: TermFields, _ lhs: TermValues, _ rhs: TermValues) -> Bool {
        switch field {
        case .spoken: return lhs.spoken == rhs.spoken
        case .written: return lhs.written == rhs.written
        case .wholeWord: return lhs.wholeWord == rhs.wholeWord
        case .enabled: return lhs.enabled == rhs.enabled
        default: return false
        }
    }

    private func dictionaryLibrary(from library: DraftLibrary) -> DictionaryLibrary {
        DictionaryLibrary(
            id: library.id,
            name: library.name,
            category: library.category,
            description: library.description,
            builtIn: library.builtIn,
            entries: library.rows.map { $0.row.values.dictionaryEntry },
            fileName: library.builtIn ? nil : "\(library.id).csv",
            basedOn: library.basedOn,
            authoredKeys: Set(library.rows.map { $0.row.key }),
            legacyMarkedKeys: [])
    }

    private func builtInEdits(from library: DraftLibrary) -> BuiltInLibraryEdits? {
        let terms = library.rows.compactMap { row -> BuiltInTermEdit? in
            if let edit = row.row.edit, edit.value == row.row.values, row.row.review == nil {
                return edit
            }
            guard row.row.origin != .shipped else {
                return nil
            }
            let intent: BuiltInTermIntent =
                row.row.origin == .added || row.row.origin == .noLongerShipped ? .added : .edited
            return BuiltInTermEdit(
                key: row.row.key.value,
                intent: intent,
                base: row.row.shipped,
                value: row.row.values,
                acknowledged: row.row.review?.updatedBuiltIn)
        }
        guard !terms.isEmpty else {
            return nil
        }
        return BuiltInLibraryEdits(version: BuiltInLibraryEdits.currentVersion, library: library.id, terms: terms)
    }
}

private func copy(
    _ library: DraftLibrary,
    name: String? = nil,
    rows: [DraftTermRow]? = nil,
    pendingDelete: Bool? = nil,
    enabled: Bool? = nil,
    aiPermitted: Bool? = nil
) -> DraftLibrary {
    DraftLibrary(
        id: library.id,
        name: name ?? library.name,
        category: library.category,
        description: library.description,
        builtIn: library.builtIn,
        rows: rows ?? library.rows,
        basedOn: library.basedOn,
        pendingDelete: pendingDelete ?? library.pendingDelete,
        enabled: enabled ?? library.enabled,
        aiPermitted: aiPermitted ?? library.aiPermitted)
}

private func localStateProjection(_ draft: LibraryDraft) -> [String] {
    draft.libraries.map { "\($0.id.lowercased())|\($0.enabled)|\($0.aiPermitted)" }.sorted()
}

enum TermField: Equatable, Sendable {
    case none
    case spoken
    case written
}

struct TermCommands: OptionSet, Sendable {
    let rawValue: Int

    static let turnOff = TermCommands(rawValue: 1 << 0)
    static let turnOn = TermCommands(rawValue: 1 << 1)
    static let delete = TermCommands(rawValue: 1 << 2)
    static let restoreBuiltIn = TermCommands(rawValue: 1 << 3)
    static let showOtherSources = TermCommands(rawValue: 1 << 4)
    static let copy = TermCommands(rawValue: 1 << 5)
    static let copyToDictionary = TermCommands(rawValue: 1 << 6)
}

enum LibraryMetadataField: Equatable, Sendable {
    case none
    case name
    case category
    case description
    case basedOn
}

enum LibraryValidationKind: Equatable, Sendable {
    case writtenWithoutSpoken
    case duplicateSpoken
    case emptyWrittenWithoutIntent
    case fieldTooLong
    case tooManyTerms
    case emptyName
    case duplicateName
    case metadataDoubleQuote
    case metadataUnreadableInOlder
    case contentNotSaveable
    case malformedText
}

struct LibraryValidationIssue: Equatable, Sendable {
    let libraryID: String
    let rowID: Int64?
    let kind: LibraryValidationKind
    let field: TermField
    let metadata: LibraryMetadataField
    let otherRowID: Int64?

    init(
        libraryID: String,
        rowID: Int64?,
        kind: LibraryValidationKind,
        field: TermField = .none,
        metadata: LibraryMetadataField = .none,
        otherRowID: Int64? = nil
    ) {
        self.libraryID = libraryID
        self.rowID = rowID
        self.kind = kind
        self.field = field
        self.metadata = metadata
        self.otherRowID = otherRowID
    }
}
