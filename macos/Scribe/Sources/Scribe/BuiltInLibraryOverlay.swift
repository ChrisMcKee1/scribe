import Foundation

enum BuiltInLibraryOverlay {
    static let editsFolderName = "edits"

    static func editsURL(root: URL, id: String) -> URL {
        root.appendingPathComponent(editsFolderName, isDirectory: true)
            .appendingPathComponent("\(id).json", isDirectory: false)
    }

    static func read(libraryID: String, data: Data) -> BuiltInEditsReadResult {
        do {
            let edits = try JSONDecoder().decode(BuiltInLibraryEdits.self, from: data)
            guard edits.version == BuiltInLibraryEdits.currentVersion else {
                return BuiltInEditsReadResult(state: .newer, edits: nil, version: edits.version)
            }
            guard edits.library.caseInsensitiveCompare(libraryID) == .orderedSame else {
                return BuiltInEditsReadResult(state: .unreadable, edits: nil, version: edits.version)
            }
            return BuiltInEditsReadResult(state: .available, edits: edits, version: edits.version)
        } catch {
            return BuiltInEditsReadResult(state: .unreadable, edits: nil, version: nil)
        }
    }

    static func write(_ edits: BuiltInLibraryEdits) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(edits)
    }

    static func apply(shipped: DictionaryLibrary, edits: BuiltInLibraryEdits?) -> DictionaryLibrary {
        guard shipped.builtIn, let edits else {
            return shipped
        }

        let entryByKey = Dictionary(uniqueKeysWithValues: edits.terms.map { ($0.termKey, $0) })
        var entries: [DictionaryEntry] = []
        var authored = Set<LibraryTermKey>()
        var seen = Set<LibraryTermKey>()

        for entry in shipped.entries {
            let key = LibraryTermKey.from(entry.pattern)
            if let edit = entryByKey[key] {
                seen.insert(key)
                switch edit.intent {
                case .off:
                    continue
                case .edited, .pinned:
                    if let value = edit.value {
                        entries.append(value.dictionaryEntry)
                        authored.insert(key)
                    } else {
                        entries.append(entry)
                    }
                case .added:
                    entries.append(entry)
                }
            } else {
                entries.append(entry)
            }
        }

        for edit in edits.terms where !seen.contains(edit.termKey) {
            switch edit.intent {
            case .added, .edited, .pinned:
                if let value = edit.value {
                    entries.append(value.dictionaryEntry)
                    authored.insert(edit.termKey)
                }
            case .off:
                continue
            }
        }

        return DictionaryLibrary(
            id: shipped.id,
            name: shipped.name,
            category: shipped.category,
            description: shipped.description,
            builtIn: true,
            entries: entries,
            fileName: shipped.fileName,
            basedOn: shipped.basedOn,
            authoredKeys: authored,
            legacyMarkedKeys: shipped.legacyMarkedKeys)
    }
}
