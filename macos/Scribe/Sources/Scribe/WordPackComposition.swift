import Foundation

enum WordPackComposition {
    static func compose(_ catalog: LibraryCatalog) -> LibraryComposition {
        let active = catalog.libraries.filter {
            catalog.localState.enabledIdSet.contains($0.id.lowercased())
                && ($0.state == .available || $0.state == .partlyReadable)
                && (!$0.builtIn || $0.origin != .retiredBuiltIn) && accepted($0, state: catalog.localState)
        }.sorted {
            LibraryPrecedence.compare(
                id: $0.id, builtIn: $0.builtIn, fileName: $0.fileName,
                otherID: $1.id, otherBuiltIn: $1.builtIn, otherFileName: $1.fileName) < 0
        }
        var tiers: [RuleTier: [ComposedLibraryRule]] = [:]
        for item in active {
            for entry in item.library.entries where entry.enabled {
                let key = LibraryTermKey.from(entry.pattern)
                guard !key.isEmpty else { continue }
                let tier: RuleTier =
                    item.library.legacyMarkedKeys.contains(key)
                    ? .legacy
                    : (!item.builtIn || item.library.authoredKeys.contains(key) ? .authored : .shipped)
                tiers[tier, default: []].append(
                    ComposedLibraryRule(entry: entry, libraryId: item.id, key: key, tier: tier))
            }
        }
        var seen = Set<LibraryTermKey>()
        let rules = [RuleTier.authored, .shipped, .legacy].flatMap { tier in
            (tiers[tier] ?? []).filter { seen.insert($0.key).inserted }
        }
        let permittedIDs = Set(active.filter { permitted($0, state: catalog.localState) }.map(\.id))
        return LibraryComposition(
            rules: rules, entries: rules.map(\.entry),
            aiEntries: rules.filter { permittedIDs.contains($0.libraryId) }.map(\.entry),
            aiExcludedLibraryIds: Set(active.filter { !permittedIDs.contains($0.id) }.map { $0.id.lowercased() }),
            enabledLibraries: active.map(\.library))
    }

    static func accepted(_ library: CatalogLibrary, state: LibraryLocalState) -> Bool {
        let hash = state.acceptedContent[library.id.lowercased()]
        return library.builtIn ? hash == library.contentHash?.value : hash != nil && hash == library.contentHash?.value
    }

    static func permitted(_ library: CatalogLibrary, state: LibraryLocalState) -> Bool {
        guard state.health == .ok || state.health == .absent, accepted(library, state: state) else { return false }
        if let chosen = state.aiPermissions[library.id.lowercased()] { return chosen }
        return !state.aiPermissionsLost && library.builtIn
    }
}

extension WordPackWorkspace {
    /// A local preview only, never a request receipt or a replacement for the committed vocabulary authority.
    func preview() throws -> LibraryComposition {
        var local = state.local
        let libraries = try state.libraries.filter { !$0.pendingDelete && !virgin($0) }.map { draft in
            let values = draft.rows.map { $0.row.values }
            let edits: BuiltInLibraryEdits?
            let hash: LibraryContentHash?
            if draft.builtIn, let source = shipped.first(where: { $0.id == draft.id }) {
                edits = try BuiltInLibraryOverlay.collect(
                    shipped: source,
                    committed: draft.recovering
                        ? draft.recoveredEdits
                        : (draft.resetEdits ? nil : committed.find(id: draft.id)?.edits),
                    rows: draft.rows.map(\.row))
                hash = try edits.map { LibraryContentHash(data: try BuiltInLibraryOverlay.write($0)) }
            } else {
                edits = nil
                hash = LibraryContentHash(
                    data: try DictionaryLibraryCsv.exportManaged(
                        LibraryCsvContent(
                            name: draft.name, category: draft.category, description: draft.description,
                            basedOn: draft.basedOn, rows: values)))
            }
            if contentChanged(draft) { local.setAcceptedContent(hash, for: draft.id) }
            let marks = Set(state.local.legacyMarkers.filter { $0.libraryId == draft.id }.map(\.termKey))
            let library = DictionaryLibrary(
                id: draft.id, name: draft.name, category: draft.category, description: draft.description,
                builtIn: draft.builtIn, entries: values.map(\.dictionaryEntry),
                fileName: committed.find(id: draft.id)?.fileName ?? "\(draft.id).csv", basedOn: draft.basedOn,
                authoredKeys: Set(
                    draft.rows.filter { $0.row.origin != .shipped && $0.row.origin != .off }.map { $0.row.key }),
                legacyMarkedKeys: marks)
            return CatalogLibrary(
                library: library, state: draft.fileState,
                contentHash: contentChanged(draft) ? hash : committed.find(id: draft.id)?.contentHash,
                origin: draft.origin, edits: edits, previousEditsAvailable: false, readErrorCount: 0)
        }
        return WordPackComposition.compose(
            LibraryCatalog(generation: committed.generation, libraries: libraries, localState: local))
    }

    /// Exports the draft, including off rows, without applying it or writing a file.
    func exportSharing(_ libraryID: String) throws -> Data {
        guard let library = draft.find(libraryID), library.fileState == .available else {
            throw WordPackError.unavailable
        }
        return DictionaryLibraryCsv.exportSharing(
            LibraryCsvContent(
                name: library.name, category: library.category, description: library.description,
                basedOn: library.basedOn, rows: library.rows.map { $0.row.values }))
    }
}
