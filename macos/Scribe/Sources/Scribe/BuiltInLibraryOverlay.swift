import Foundation

enum BuiltInLibraryOverlay {
    static let editsFolderName = "edits"

    static func editsURL(root: URL, id: String) -> URL {
        root.appendingPathComponent(editsFolderName, isDirectory: true)
            .appendingPathComponent("\(id).json", isDirectory: false)
    }

    static func read(libraryID: String, data: Data) -> BuiltInEditsReadResult {
        LibraryEditsJSON.read(libraryID: libraryID, data: data)
    }

    static func write(_ edits: BuiltInLibraryEdits) throws -> Data {
        guard valid(edits) else { throw WordPackError.invalidEdits }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(edits)
    }

    static func apply(shipped: DictionaryLibrary, edits: BuiltInLibraryEdits?) -> DictionaryLibrary {
        let rows = applyRows(shipped: shipped, edits: edits)
        return DictionaryLibrary(
            id: shipped.id,
            name: shipped.name,
            category: shipped.category,
            description: shipped.description,
            builtIn: true,
            entries: rows.map { $0.values.dictionaryEntry },
            fileName: shipped.fileName,
            basedOn: shipped.basedOn,
            authoredKeys: Set(rows.filter { $0.origin != .shipped && $0.origin != .off }.map(\.key)),
            legacyMarkedKeys: shipped.legacyMarkedKeys)
    }

    static func applyRows(shipped: DictionaryLibrary, edits: BuiltInLibraryEdits?) -> [LibraryRow] {
        precondition(shipped.builtIn)
        if let edits {
            precondition(valid(edits) && LibraryTermKey.areSame(shipped.id, edits.library))
        }
        let byKey = Dictionary(uniqueKeysWithValues: (edits?.terms ?? []).map { ($0.termKey, $0) })
        var matched = Set<LibraryTermKey>()
        var rows: [LibraryRow] = []
        for entry in shipped.entries {
            let values = TermValues(entry: entry)
            let key = LibraryTermKey.from(values.spoken)
            if let edit = byKey[key], matched.insert(key).inserted {
                if let row = rowOf(shipped: values, edit: edit) { rows.append(row) }
            } else {
                rows.append(shippedRow(values))
            }
        }
        for edit in edits?.terms ?? [] where !matched.contains(edit.termKey) {
            if let row = rowOf(shipped: nil, edit: edit) { rows.append(row) }
        }
        return rows
    }

    static func edit(_ row: LibraryRow, values: TermValues) -> LibraryRow {
        precondition(canonical(row))
        guard row.values != values else { return row }
        if row.values.spoken == values.spoken && row.values.written == values.written
            && row.values.wholeWord == values.wholeWord
        {
            return setEnabled(row, enabled: values.enabled)
        }
        return editValues(row, values: values)
    }

    static func setEnabled(_ row: LibraryRow, enabled: Bool) -> LibraryRow {
        precondition(canonical(row))
        guard row.values.enabled != enabled else { return row }
        if row.origin == .shipped && !enabled {
            return rowOf(
                shipped: row.shipped,
                edit: BuiltInTermEdit(
                    key: row.key.value, intent: .off, base: row.shipped, value: nil, acknowledged: nil))!
        }
        if row.edit?.intent == .off, let shipped = row.shipped, shipped.enabled {
            return shippedRow(shipped)
        }
        var values = row.values
        values.enabled = enabled
        return editValues(row, values: values)
    }

    static func restoreShipped(_ row: LibraryRow) -> LibraryRow? {
        precondition(canonical(row))
        return row.shipped.map(shippedRow)
    }

    static func add(_ values: TermValues) -> LibraryRow {
        precondition(!LibraryTermKey.from(values.spoken).isEmpty)
        return rowOf(
            shipped: nil,
            edit: BuiltInTermEdit(
                key: LibraryTermKey.from(values.spoken).value, intent: .added,
                base: nil, value: values, acknowledged: nil))!
    }

    static func resolveReview(_ row: LibraryRow, choice: TermReviewChoice) -> LibraryRow {
        precondition(canonical(row))
        guard row.review != nil, let shipped = row.shipped, let entry = row.edit else { return row }
        let edit: BuiltInTermEdit
        switch choice {
        case .keepMine:
            edit = BuiltInTermEdit(
                key: entry.key, intent: entry.intent, base: entry.base, value: entry.value, acknowledged: shipped)
        case .useUpdated:
            edit = BuiltInTermEdit(
                key: entry.key, intent: .pinned, base: shipped, value: shipped, acknowledged: nil)
        }
        return rowOf(shipped: shipped, edit: edit)!
    }

    static func collect(
        shipped: DictionaryLibrary, committed: BuiltInLibraryEdits?, rows: [LibraryRow]
    ) throws -> BuiltInLibraryEdits? {
        let shippedValues = shipped.entries.map { TermValues(entry: $0) }
        var shippedByKey: [LibraryTermKey: TermValues] = [:]
        for value in shippedValues {
            shippedByKey[LibraryTermKey.from(value.spoken)] =
                shippedByKey[LibraryTermKey.from(value.spoken)] ?? value
        }
        var rowsByKey: [LibraryTermKey: LibraryRow] = [:]
        for row in rows {
            guard rowsByKey[row.key] == nil, canonical(row) else { throw WordPackError.invalidEdits }
            let expected = row.edit.flatMap { rowOf(shipped: shippedByKey[row.key], edit: $0) }
                ?? shippedByKey[row.key].map(shippedRow)
            guard expected == row else { throw WordPackError.invalidEdits }
            rowsByKey[row.key] = row
        }
        let kept = Dictionary(uniqueKeysWithValues: (committed?.terms ?? []).map { ($0.termKey, $0) })
        var terms: [BuiltInTermEdit] = []
        var placed = Set<LibraryTermKey>()
        for value in shippedValues {
            let key = LibraryTermKey.from(value.spoken)
            guard placed.insert(key).inserted else { continue }
            if let row = rowsByKey[key] {
                if let edit = row.edit { terms.append(edit) }
            } else if let edit = kept[key], edit.intent != .added {
                terms.append(edit)
            }
        }
        for row in rows where shippedByKey[row.key] == nil {
            if let edit = row.edit { terms.append(edit) }
        }
        for entry in committed?.terms ?? []
        where entry.intent == .off && shippedByKey[entry.termKey] == nil && rowsByKey[entry.termKey] == nil {
            terms.append(entry)
        }
        guard !terms.isEmpty else { return nil }
        return BuiltInLibraryEdits(version: 1, library: shipped.id, terms: terms)
    }

    static func valid(_ edits: BuiltInLibraryEdits) -> Bool {
        guard edits.version == 1, !edits.library.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return false
        }
        var keys = Set<LibraryTermKey>()
        return edits.terms.allSatisfy {
            !$0.termKey.isEmpty && keys.insert($0.termKey).inserted && valid($0)
        }
    }

    static func valid(_ edit: BuiltInTermEdit) -> Bool {
        switch edit.intent {
        case .edited, .pinned: return edit.base != nil && edit.value != nil
        case .off: return edit.base != nil && edit.value == nil
        case .added: return edit.base == nil && edit.value != nil
        }
    }

    private static func shippedRow(_ values: TermValues) -> LibraryRow {
        LibraryRow(values: values, origin: .shipped, shipped: values)
    }

    private static func canonical(_ row: LibraryRow) -> Bool {
        guard row.origin != .custom else { return false }
        return (row.edit.flatMap { rowOf(shipped: row.shipped, edit: $0) }
            ?? row.shipped.map(shippedRow)) == row
    }

    private static func rowOf(shipped: TermValues?, edit: BuiltInTermEdit) -> LibraryRow? {
        let values: TermValues
        let origin: TermOrigin
        var review: TermReview?
        switch edit.intent {
        case .off:
            guard var off = shipped else { return nil }
            off.enabled = false
            values = off
            origin = .off
        case .added:
            values = edit.value!
            origin = values == shipped ? .pinned : .added
        case .edited, .pinned:
            guard let shipped else {
                return LibraryRow(
                    key: edit.termKey, values: edit.value!, origin: .noLongerShipped, edit: edit)
            }
            let yours = edit.value!
            let base = edit.base!
            let changed = TermFields.differences(base, yours)
            values = edit.intent == .edited ? changed.taking(yours, otherwise: shipped) : yours
            origin = values == shipped ? .pinned : .edited
            let asks = TermFields.all.contains { field in
                let differs = !TermFields.same(field, yours, shipped)
                let unacknowledged = edit.acknowledged.map { !TermFields.same(field, $0, shipped) } ?? true
                if edit.intent == .pinned { return differs && unacknowledged }
                return differs && unacknowledged && changed.contains(field)
                    && !TermFields.same(field, base, shipped)
            }
            if asks {
                review = TermReview(
                    yours: values, updatedBuiltIn: shipped, differing: .differences(values, shipped))
            }
        }
        return LibraryRow(
            key: edit.termKey, values: values, origin: origin, shipped: shipped, edit: edit, review: review)
    }

    private static func editValues(_ row: LibraryRow, values: TermValues) -> LibraryRow {
        let entry = row.edit
        let shipped = row.shipped
        let edited: BuiltInTermEdit
        if entry == nil {
            edited = BuiltInTermEdit(
                key: row.key.value, intent: .edited, base: shipped, value: values, acknowledged: nil)
        } else if entry!.intent == .off {
            var base = shipped!
            if values.enabled == row.values.enabled { base.enabled = true }
            edited = BuiltInTermEdit(
                key: row.key.value, intent: .edited, base: base, value: values, acknowledged: nil)
        } else if let entry, entry.intent == .edited, let shipped {
            let changed = TermFields.differences(row.values, values)
            edited = BuiltInTermEdit(
                key: entry.key, intent: entry.intent,
                base: changed.taking(shipped, otherwise: entry.base!),
                value: changed.taking(values, otherwise: entry.value!), acknowledged: entry.acknowledged)
        } else if let entry, entry.intent == .pinned, let shipped {
            let changed = TermFields.differences(row.values, values)
            let acknowledged = changed.taking(shipped, otherwise: entry.acknowledged ?? entry.value!)
            edited = BuiltInTermEdit(
                key: entry.key, intent: entry.intent, base: entry.base, value: values,
                acknowledged: acknowledged == values ? nil : acknowledged)
        } else {
            edited = BuiltInTermEdit(
                key: entry!.key, intent: entry!.intent, base: entry!.base, value: values,
                acknowledged: entry!.acknowledged)
        }
        return rowOf(shipped: shipped, edit: edited)!
    }
}
