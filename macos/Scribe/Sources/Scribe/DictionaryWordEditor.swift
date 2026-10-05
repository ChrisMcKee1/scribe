import Foundation

/// A text-only edit of one dictionary entry, with any new ways that write the same text.
enum DictionaryWordEditor {
    static let spokenEmptyMessage = "Type the words Scribe hears, or delete this row."

    struct Result: Equatable {
        let editedEntry: DictionaryEntry?
        let addedEntries: [DictionaryEntry]
        let error: String?
        let errorFormIndex: Int

        var succeeded: Bool {
            error == nil
        }

        var canSave: Bool {
            editedEntry != nil || !addedEntries.isEmpty
        }
    }

    static func build(
        existing: [DictionaryEntry],
        editedIndex: Int?,
        replacement: String,
        forms: [String]
    ) -> Result {
        if let index = editedIndex, !(existing.indices.contains(index)) {
            preconditionFailure("editedIndex out of range")
        }

        if forms.isEmpty || (editedIndex != nil && forms[0].trimmingCharacters(in: .whitespacesAndNewlines).isEmpty) {
            return Result(editedEntry: nil, addedEntries: [], error: spokenEmptyMessage, errorFormIndex: 0)
        }

        var seen = Set<String>()
        for (index, entry) in existing.enumerated() where index != editedIndex {
            let pattern = entry.pattern.trimmingCharacters(in: .whitespacesAndNewlines)
            if !pattern.isEmpty {
                seen.insert(pattern.lowercased())
            }
        }

        var editedEntry: DictionaryEntry?
        var addedEntries: [DictionaryEntry] = []

        for (index, form) in forms.enumerated() {
            if form.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                continue
            }

            let trimmed = form.trimmingCharacters(in: .whitespacesAndNewlines)
            let unchanged =
                index == 0
                && editedIndex.flatMap { existing.indices.contains($0) ? existing[$0] : nil }.map {
                    form == $0.pattern && replacement == $0.replacement
                } == true

            if !seen.insert(trimmed.lowercased()).inserted && !unchanged {
                return Result(
                    editedEntry: nil,
                    addedEntries: [],
                    error: "\"\(trimmed)\" is already in your dictionary. Keep one of the two ways.",
                    errorFormIndex: index)
            }

            if index == 0, let rowIndex = editedIndex {
                var updated = existing[rowIndex]
                updated.pattern = form
                updated.replacement = replacement
                editedEntry = updated
            } else {
                addedEntries.append(
                    DictionaryEntry(
                        id: 0,
                        pattern: form,
                        replacement: replacement,
                        wholeWord: true,
                        enabled: true))
            }
        }

        if editedEntry == nil && addedEntries.isEmpty {
            return Result(
                editedEntry: nil,
                addedEntries: [],
                error: "Type at least one way Scribe hears this word.",
                errorFormIndex: 0)
        }

        return Result(editedEntry: editedEntry, addedEntries: addedEntries, error: nil, errorFormIndex: 0)
    }

    static func preserveUnchangedText(original: String, displayedOriginal: String, current: String) -> String {
        displayedOriginal == current ? original : current
    }
}
