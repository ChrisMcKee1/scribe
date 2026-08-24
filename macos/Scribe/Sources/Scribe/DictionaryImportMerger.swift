import Foundation

/// Pure merge of imported dictionary entries into an existing set, matched by spoken form
/// (case-insensitive) to mirror the duplicate rule dictionary save enforces. The Settings view
/// owns persistence (an import can still be cancelled), so this only decides an ordered plan and
/// the counts; `DictionarySettingsTab` applies the plan as a thin adapter. Mirrors
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
