import Foundation

/// Pure merge of imported dictionary entries into an existing set, matched by spoken form
/// (case-insensitive) to mirror the duplicate rule dictionary save enforces. This decides an ordered
/// plan and the counts; `changes(applying:to:)` then folds the plan into the rows it leaves behind,
/// which `PersistenceStore.applyDictionaryChanges` writes in one transaction. Mirrors
/// `Scribe.Core.Settings.DictionaryImportMerger` on Windows.
enum DictionaryImportMerger {
    /// An existing row's identity: its position, current spoken form, and current fields.
    struct ExistingRow {
        let index: Int
        let id: Int64
        let pattern: String?
        var replacement: String?
        var wholeWord: Bool
        var enabled: Bool
    }

    enum OperationKind {
        /// Replace the existing row at `index` with `entry`.
        case update
        /// Append `entry` as a new row.
        case add
    }

    /// A single merge step. For `.update`, `index` is the existing row position to replace; for
    /// `.add` it is -1.
    struct Operation {
        let kind: OperationKind
        let index: Int
        let entry: DictionaryEntry
    }

    /// The ordered operations to apply plus the (added, updated, unchanged) counts.
    struct Plan {
        let operations: [Operation]
        let added: Int
        let updated: Int
        let unchanged: Int
    }

    /// What an import leaves behind once its plan is applied: rows to insert and existing rows whose
    /// stored fields change. Each is the row's final state after every operation in the batch.
    struct Changes: Equatable {
        let inserts: [DictionaryEntry]
        let updates: [DictionaryEntry]
    }

    /// Applies `plan` to `existing` the way Windows applies it to the grid: an update replaces the row
    /// at its index, whether that row came from the store or from an earlier addition in the same
    /// batch. A spoken form imported twice therefore lands once, with its later values, rather than as
    /// an update aimed at a row that does not exist yet. Addition indices are assigned exactly as
    /// `merge` assigns them. An existing row that ends where it started is not rewritten.
    static func changes(applying plan: Plan, to existing: [ExistingRow]) -> Changes {
        var working: [Int: DictionaryEntry] = [:]
        var original: [Int: DictionaryEntry] = [:]
        for row in existing where working[row.index] == nil {
            let entry = DictionaryEntry(
                id: row.id,
                pattern: row.pattern ?? "",
                replacement: row.replacement ?? "",
                wholeWord: row.wholeWord,
                enabled: row.enabled)
            working[row.index] = entry
            original[row.index] = entry
        }

        var addedIndices: [Int] = []
        var nextIndex = existing.isEmpty ? 0 : (existing.map(\.index).max() ?? 0) + 1
        for operation in plan.operations {
            switch operation.kind {
            case .add:
                working[nextIndex] = operation.entry
                addedIndices.append(nextIndex)
                nextIndex += 1
            case .update:
                working[operation.index] = operation.entry
            }
        }

        let inserts = addedIndices.compactMap { working[$0] }
        let updates = original.keys.sorted().compactMap { index -> DictionaryEntry? in
            guard let current = working[index], current != original[index] else {
                return nil
            }
            return current
        }
        return Changes(inserts: inserts, updates: updates)
    }

    /// Merges `imported` into `existing` by spoken form: unchanged rows are counted only;
    /// differing rows produce an update that keeps the existing id and spoken form; unmatched
    /// imports become additions. Additions register as match targets for later imports in the same
    /// batch, matching the Windows grid's original single-pass behaviour.
    static func merge(existing: [ExistingRow], imported: [DictionaryEntry]) -> Plan {
        // First writer wins per spoken form, matching the grid's TryAdd de-dupe of existing rows.
        var indexByPattern: [String: Int] = [:]
        var byIndex: [Int: ExistingRow] = [:]
        for row in existing {
            byIndex[row.index] = row
            if let pattern = row.pattern?.trimmingCharacters(in: .whitespaces), !pattern.isEmpty {
                let key = pattern.lowercased()
                if indexByPattern[key] == nil {
                    indexByPattern[key] = row.index
                }
            }
        }

        var operations: [Operation] = []
        var added = 0
        var updated = 0
        var unchanged = 0

        // The next synthetic index an addition occupies, so a later import can update it in-batch.
        var nextIndex = existing.isEmpty ? 0 : (existing.map(\.index).max() ?? 0) + 1

        for entry in imported {
            let key = entry.pattern.lowercased()
            if let index = indexByPattern[key], let row = byIndex[index] {
                if (row.replacement?.trimmingCharacters(in: .whitespaces) ?? "") == entry.replacement,
                   row.wholeWord == entry.wholeWord, row.enabled == entry.enabled {
                    unchanged += 1
                    continue
                }

                // Keep the existing id and original spoken form; only the other fields change.
                let replacement = DictionaryEntry(
                    id: row.id,
                    pattern: row.pattern ?? entry.pattern,
                    replacement: entry.replacement,
                    wholeWord: entry.wholeWord,
                    enabled: entry.enabled)
                operations.append(Operation(kind: .update, index: index, entry: replacement))
                var updatedRow = row
                updatedRow.replacement = entry.replacement
                updatedRow.wholeWord = entry.wholeWord
                updatedRow.enabled = entry.enabled
                byIndex[index] = updatedRow
                updated += 1
            } else {
                operations.append(Operation(kind: .add, index: -1, entry: entry))

                // Register the addition so a later duplicate import updates it rather than re-adding.
                let addedIndex = nextIndex
                nextIndex += 1
                indexByPattern[key] = addedIndex
                byIndex[addedIndex] = ExistingRow(
                    index: addedIndex,
                    id: entry.id,
                    pattern: entry.pattern,
                    replacement: entry.replacement,
                    wholeWord: entry.wholeWord,
                    enabled: entry.enabled)
                added += 1
            }
        }

        return Plan(operations: operations, added: added, updated: updated, unchanged: unchanged)
    }
}
