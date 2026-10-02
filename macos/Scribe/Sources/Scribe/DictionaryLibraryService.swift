import Foundation

/// `@unchecked Sendable` because `UserDefaults` is process-wide mutable state the compiler cannot model,
/// but the access here is only through its documented thread-safe getters and setters.
struct DictionaryLibrarySettings: @unchecked Sendable {
    static let enabledIdsKey = "ScribeEnabledDictionaryLibraryIds"

    let defaults: UserDefaults

    var enabledLibraryIds: Set<String> {
        get { Set(defaults.stringArray(forKey: Self.enabledIdsKey) ?? []) }
        nonmutating set { defaults.set(Array(newValue), forKey: Self.enabledIdsKey) }
    }

    func setEnabled(_ enabled: Bool, id: String) {
        var ids = enabledLibraryIds
        if enabled {
            ids.insert(id)
        } else {
            ids.remove(id)
        }
        enabledLibraryIds = ids
    }
}

enum DictionaryLibrarySettingsStore {
    static var standard: DictionaryLibrarySettings {
        DictionaryLibrarySettings(defaults: .standard)
    }

    static var enabledLibraryIds: Set<String> {
        get { standard.enabledLibraryIds }
        set { standard.enabledLibraryIds = newValue }
    }

    static func setEnabled(_ enabled: Bool, id: String) {
        standard.setEnabled(enabled, id: id)
    }
}

enum DictionaryLibraryServiceError: Error, LocalizedError {
    case invalidCsv(String)
    case noUsableEntries
    case builtInCannotBeRemoved
    case invalidLibraryId

    var errorDescription: String? {
        switch self {
        case .invalidCsv(let detail):
            return "That library contains invalid CSV rows:\n\(detail)"
        case .noUsableEntries:
            return "That file has no usable dictionary rows. Each row needs at least a spoken form and a replacement."
        case .builtInCannotBeRemoved:
            return "Built-in libraries can't be removed. Turn it off instead."
        case .invalidLibraryId:
            return "That library id is not valid."
        }
    }
}

/// `@unchecked Sendable` because the service only reads immutable snapshots through collaborators that are safe for
/// concurrent use in this app, but the compiler cannot prove that for `UserDefaults`, `FileManager` and the store.
final class DictionaryLibraryService: @unchecked Sendable {
    static let libraryStateKey = "word_pack_state_v1"

    let librariesDirectory: URL
    let settings: DictionaryLibrarySettings

    private let fileManager: FileManager
    private let persistenceStore: PersistenceStore?

    init(
        fileManager: FileManager = .default,
        librariesDirectory overrideDirectory: URL? = nil,
        settings: DictionaryLibrarySettings = DictionaryLibrarySettingsStore.standard,
        persistenceStore: PersistenceStore? = nil
    ) {
        self.fileManager = fileManager
        self.settings = settings
        self.persistenceStore = persistenceStore
        if let overrideDirectory {
            self.librariesDirectory = overrideDirectory
        } else {
            let applicationSupportURL = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            let scribeDirectory = applicationSupportURL.appendingPathComponent("Scribe", isDirectory: true)
            self.librariesDirectory = scribeDirectory.appendingPathComponent("Libraries", isDirectory: true)
        }
    }

    func libraries() -> [DictionaryLibrary] {
        LibraryPrecedence.order(loadCatalogLibraries().map(\.library))
    }

    func enabledLibraryEntries() -> [DictionaryEntry] {
        if let persistenceStore,
            let raw = try? persistenceStore.readStringSetting(key: Self.libraryStateKey),
            let state = try? decodeState(raw)
        {
            let catalog = LibraryCatalog(
                generation: state.generation, libraries: decorate(libraries: loadCatalogLibraries(), with: state),
                localState: state)
            return compose(catalog: catalog).entries
        }
        let enabledIDs = settings.enabledLibraryIds
        guard !enabledIDs.isEmpty else { return [] }

        let matching = LibraryPrecedence.enabled(libraries(), enabledIDs: enabledIDs)
        return DictionaryLibraryComposer.composeLibraries(matching)
    }

    @discardableResult
    func `import`(csv: String, suggestedName: String?) throws -> DictionaryLibrary {
        try `import`(data: Data(csv.utf8), suggestedName: suggestedName)
    }

    @discardableResult
    func `import`(data: Data, suggestedName: String?) throws -> DictionaryLibrary {
        let file = DictionaryLibraryCsv.parseImport(data)
        if !file.errors.isEmpty {
            throw DictionaryLibraryServiceError.invalidCsv(
                file.errors.prefix(5).map(\.legacyMessage).joined(separator: "\n"))
        }
        guard !file.terms.isEmpty else {
            throw DictionaryLibraryServiceError.noUsableEntries
        }

        let trimmedSuggestion = suggestedName?.trimmingCharacters(in: .whitespacesAndNewlines)
        let fallbackName = trimmedSuggestion?.isEmpty == false ? trimmedSuggestion : nil
        let name = file.name ?? fallbackName ?? LibraryNaming.importedLibraryBaseName
        let category = file.category ?? "Custom"

        try fileManager.createDirectory(at: librariesDirectory, withIntermediateDirectories: true)
        let id = LibraryNaming.newCustomID(name: name, takenIDs: allKnownIDs())
        let library = DictionaryLibrary(
            id: id,
            name: name,
            category: category,
            description: file.description,
            builtIn: false,
            entries: file.terms.map(\.dictionaryEntry),
            fileName: "\(id).csv",
            basedOn: file.basedOn)

        let managed = try DictionaryLibraryCsv.exportManaged(
            LibraryCsvContent(
                name: library.name,
                category: library.category,
                description: library.description,
                basedOn: library.basedOn,
                rows: file.terms))
        let url = librariesDirectory.appendingPathComponent("\(id).csv")
        try managed.write(to: url, options: .atomic)
        try updatePersistedStateAfterImport(id: id, contentHash: LibraryContentHash(data: managed))
        return library
    }

    func remove(id: String) throws {
        guard !id.isEmpty else { return }

        if BuiltInDictionaryLibraries.all.contains(where: { $0.id.caseInsensitiveCompare(id) == .orderedSame }) {
            throw DictionaryLibraryServiceError.builtInCannotBeRemoved
        }

        let fileName = "\(id).csv"
        guard fileName.rangeOfCharacter(from: .init(charactersIn: "/\\:")) == nil else {
            throw DictionaryLibraryServiceError.invalidLibraryId
        }

        let path = librariesDirectory.appendingPathComponent(fileName)
        if fileManager.fileExists(atPath: path.path) {
            try fileManager.removeItem(at: path)
        }
        settings.setEnabled(false, id: id)
        try updatePersistedStateAfterRemoval(id: id)
    }

    func loadCatalog() async throws -> LibraryCatalog {
        if let persistenceStore {
            _ = try await WordPackMaterializer.recover(store: persistenceStore, service: self)
        }
        let libraries = loadCatalogLibraries()
        let state = try await loadResolvedState(for: libraries)
        let decorated = decorate(libraries: libraries, with: state)
        return LibraryCatalog(generation: state.generation, libraries: decorated, localState: state)
    }

    func loadVocabulary() async throws -> LibraryVocabulary {
        let catalog = try await loadCatalog()
        let composition = compose(catalog: catalog)
        let permitted = catalog.libraries.reduce(into: [String: String?]()) { result, library in
            let enabled = isEnabled(library.id, in: catalog.localState)
            let usable = isUsable(library.state)
            let permitted = isAIPermitted(library, state: catalog.localState)
            guard enabled && usable && permitted else {
                return
            }
            result[library.id.lowercased()] = .some(library.contentHash?.value)
        }
        return LibraryVocabulary(
            generation: catalog.generation,
            entries: composition.entries,
            aiEntries: composition.aiEntries,
            aiScope: AiVocabularyScope(generation: catalog.generation, permittedContent: permitted))
    }

    private func loadCatalogLibraries() -> [CatalogLibrary] {
        let builtIns = loadBuiltIns() + loadRetiredBuiltIns()
        let customs = loadCustomLibraries()
        let libraries = sortCatalogLibraries(builtIns + customs)
        guard let persistenceStore else { return libraries }
        let held: Set<String>?
        do {
            held = WordPackMaterializer.heldBackIDs(try persistenceStore.readStringSetting(key: WordPackJournal.key))
        } catch {
            held = nil
        }
        return libraries.map { item in
            guard held == nil || held!.contains(item.id.lowercased()) else { return item }
            let library = DictionaryLibrary(
                id: item.id, name: item.library.name, category: item.library.category,
                description: item.library.description, builtIn: item.builtIn, entries: [],
                fileName: item.fileName, basedOn: item.library.basedOn)
            return CatalogLibrary(
                library: library, state: .awaitingRelease, contentHash: item.contentHash,
                origin: item.origin, edits: item.edits, previousEditsAvailable: item.previousEditsAvailable,
                readErrorCount: item.readErrorCount)
        }
    }

    private func loadBuiltIns() -> [CatalogLibrary] {
        let editsRoot = librariesDirectory.appendingPathComponent(
            BuiltInLibraryOverlay.editsFolderName,
            isDirectory: true)
        return BuiltInDictionaryLibraries.all.map { shipped in
            let editsURL = BuiltInLibraryOverlay.editsURL(root: librariesDirectory, id: shipped.id)
            let previousURL = editsRoot.appendingPathComponent("\(shipped.id).previous.json", isDirectory: false)
            let previousExists = fileManager.fileExists(atPath: previousURL.path)

            guard let data = try? Data(contentsOf: editsURL) else {
                // Only ENOENT means absent. A directory, permission refusal or inaccessible ancestor must pause the pack.
                let absent: Bool
                do {
                    _ = try editsURL.resourceValues(forKeys: [.isRegularFileKey])
                    absent = false
                } catch let error as NSError {
                    absent = error.domain == NSCocoaErrorDomain && error.code == NSFileReadNoSuchFileError
                }
                let library = DictionaryLibrary(
                    id: shipped.id, name: shipped.name, category: shipped.category,
                    description: shipped.description, builtIn: true, entries: absent ? shipped.entries : [],
                    fileName: shipped.fileName, basedOn: shipped.basedOn)
                return CatalogLibrary(
                    library: library,
                    state: absent ? .available : .unreadable,
                    contentHash: nil,
                    origin: .existing,
                    edits: nil,
                    previousEditsAvailable: previousExists,
                    readErrorCount: 0)
            }

            let result = BuiltInLibraryOverlay.read(libraryID: shipped.id, data: data)
            switch result.state {
            case .available:
                let applied = BuiltInLibraryOverlay.apply(shipped: shipped, edits: result.edits)
                return CatalogLibrary(
                    library: applied,
                    state: .available,
                    contentHash: LibraryContentHash(data: data),
                    origin: .existing,
                    edits: result.edits,
                    previousEditsAvailable: previousExists,
                    readErrorCount: 0)
            case .newer, .unreadable:
                let paused = DictionaryLibrary(
                    id: shipped.id,
                    name: shipped.name,
                    category: shipped.category,
                    description: shipped.description,
                    builtIn: true,
                    entries: [],
                    fileName: shipped.fileName,
                    basedOn: shipped.basedOn)
                return CatalogLibrary(
                    library: paused,
                    state: result.state,
                    contentHash: LibraryContentHash(data: data),
                    origin: .existing,
                    edits: nil,
                    previousEditsAvailable: previousExists,
                    readErrorCount: 0)
            default:
                return CatalogLibrary(
                    library: shipped,
                    state: .available,
                    contentHash: nil,
                    origin: .existing,
                    edits: nil,
                    previousEditsAvailable: previousExists,
                    readErrorCount: 0)
            }
        }
    }

    private func loadCustomLibraries() -> [CatalogLibrary] {
        let fileURLs = libraryFiles().sorted { $0.lastPathComponent < $1.lastPathComponent }
        guard !fileURLs.isEmpty else {
            return []
        }

        private func loadRetiredBuiltIns() -> [CatalogLibrary] {
            let root = librariesDirectory.appendingPathComponent("edits", isDirectory: true)
            let files = (try? fileManager.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
            let shipped = Set(BuiltInDictionaryLibraries.all.map { $0.id.lowercased() })
            return files.compactMap { url in
                let id = url.deletingPathExtension().lastPathComponent
                guard url.pathExtension == "json", !shipped.contains(id.lowercased()),
                    !id.hasSuffix(".previous"), !id.contains(".backup-"),
                    let data = try? Data(contentsOf: url)
                else { return nil }
                let result = BuiltInLibraryOverlay.read(libraryID: id, data: data)
                let entries = result.edits?.terms.compactMap { $0.value?.dictionaryEntry } ?? []
                let library = DictionaryLibrary(
                    id: id, name: BuiltInDictionaryLibraries.humanize(id), category: "Built-in",
                    description: nil, builtIn: true, entries: entries)
                return CatalogLibrary(
                    library: library, state: result.state, contentHash: LibraryContentHash(data: data),
                    origin: .retiredBuiltIn, edits: result.edits, previousEditsAvailable: false, readErrorCount: 0)
            }
        }

        var libraries: [CatalogLibrary] = []
        let builtInIDs = Set(BuiltInDictionaryLibraries.all.map { $0.id.lowercased() })
        var taken = Array(builtInIDs) + fileURLs.map { $0.deletingPathExtension().lastPathComponent }
        for fileURL in fileURLs where fileURL.pathExtension.lowercased() == "csv" {
            let stem = fileURL.deletingPathExtension().lastPathComponent
            guard !stem.isEmpty, let data = try? Data(contentsOf: fileURL) else {
                continue
            }
            let id = builtInIDs.contains(stem.lowercased()) ? LibraryNaming.remapID(stem: stem, takenIDs: taken) : stem
            taken.append(id)

            let file = DictionaryLibraryCsv.parseManaged(data)
            let state: LibraryFileState
            if file.terms.isEmpty && !file.errors.isEmpty {
                state = .unreadable
            } else if !file.errors.isEmpty {
                state = .partlyReadable
            } else {
                state = .available
            }

            let library = DictionaryLibrary(
                id: id,
                name: file.name ?? BuiltInDictionaryLibraries.humanize(id),
                category: file.category ?? "Custom",
                description: file.description,
                builtIn: false,
                entries: file.terms.map(\.dictionaryEntry),
                fileName: fileURL.lastPathComponent,
                basedOn: file.basedOn)
            libraries.append(
                CatalogLibrary(
                    library: library,
                    state: state,
                    contentHash: LibraryContentHash(data: data),
                    origin: .existing,
                    edits: nil,
                    previousEditsAvailable: false,
                    readErrorCount: file.errors.count))
        }
        return libraries
    }

    private func sortCatalogLibraries(_ libraries: [CatalogLibrary]) -> [CatalogLibrary] {
        libraries.sorted { lhs, rhs in
            LibraryPrecedence.compare(
                id: lhs.id,
                builtIn: lhs.builtIn,
                fileName: lhs.fileName,
                otherID: rhs.id,
                otherBuiltIn: rhs.builtIn,
                otherFileName: rhs.fileName) < 0
        }
    }

    private func loadResolvedState(for libraries: [CatalogLibrary]) async throws -> LibraryLocalState {
        guard let persistenceStore else {
            return migratedState(for: libraries)
        }

        let raw = try await persistenceStore.loadStringSetting(key: Self.libraryStateKey)
        var state = try decodeState(raw) ?? migratedState(for: libraries)
        state.normalize()
        let pending = try await persistenceStore.loadStringSetting(key: WordPackJournal.key)
        let synced = pending == nil && state.health == .ok && synchronizeLegacyProjection(state: &state, libraries: libraries)
        var replaced = false
        if pending == nil && state.health == .ok {
            for library in libraries where library.state == .available || library.state == .partlyReadable {
                let key = library.id.lowercased()
                let accepted = state.acceptedContent[key]
                let changed = library.builtIn
                    ? accepted != library.contentHash?.value
                    : accepted == nil || accepted != library.contentHash?.value
                if changed && (state.enabledIdSet.contains(key) || state.aiPermissions[key] == true) {
                    state.setEnabled(false, for: library.id)
                    state.setAIPermission(false, for: library.id)
                    replaced = true
                }
            }
            if replaced {
                state.generation += 1
                state.legacyEnabledIds = state.enabledIds.filter { state.aiPermissions[$0.lowercased()] != false }
                settings.enabledLibraryIds = Set(state.legacyEnabledIds)
            }
        }
        if raw == nil || synced || replaced {
            let value = String(decoding: try JSONEncoder().encode(state), as: UTF8.self)
            do {
                try await persistenceStore.commitSettingsParticipants([
                    StoredSettingsParticipant(
                        checks: [StoredSettingCheck(key: Self.libraryStateKey, expected: raw)],
                        writes: [StoredSettingWrite(key: Self.libraryStateKey, value: value)])
                ])
            } catch StoredSettingsCommitError.conflict {
                return try decodeState(await persistenceStore.loadStringSetting(key: Self.libraryStateKey))
                    ?? unreadableState()
            }
        }
        return state
    }

    private func decodeState(_ raw: String?) throws -> LibraryLocalState? {
        guard let raw else {
            return nil
        }
        guard let data = raw.data(using: .utf8) else {
            return unreadableState()
        }
        do {
            if let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                let version = object["version"] as? Int, version > 1
            {
                var newer = unreadableState()
                newer.health = .newer
                return newer
            }
            var state = try JSONDecoder().decode(LibraryLocalState.self, from: data)
            state.normalize()
            return state
        } catch {
            return unreadableState()
        }
    }

    private func unreadableState() -> LibraryLocalState {
        LibraryLocalState(
            generation: 1,
            enabledIds: [],
            legacyEnabledIds: [],
            aiPermissions: [:],
            acceptedContent: [:],
            legacyMarkers: [],
            aiUpgradeNotice: [],
            health: .unreadable,
            aiPermissionsLost: true)
    }

    private func migratedState(for libraries: [CatalogLibrary]) -> LibraryLocalState {
        var state = LibraryLocalState.absent
        state.generation = 1
        state.health = .ok
        state.enabledIds = settings.enabledLibraryIds.sorted()
        state.legacyEnabledIds = state.enabledIds
        for library in libraries where !library.builtIn {
            let stem = library.fileName.map { URL(fileURLWithPath: $0).deletingPathExtension().lastPathComponent }
            if let stem, settings.enabledLibraryIds.contains(stem), stem != library.id {
                state.enabledIds.append(library.id)
            }
        }
        for library in libraries where !library.builtIn {
            state.aiPermissions[library.id.lowercased()] = true
            state.setAcceptedContent(library.contentHash, for: library.id)
            let shipped = DictionaryLibraryComposer.composeLibraries(BuiltInDictionaryLibraries.all)
            for entry in library.library.entries {
                let key = LibraryTermKey.from(entry.pattern)
                if let original = shipped.first(where: { LibraryTermKey.from($0.pattern) == key }),
                    original.replacement != entry.replacement || original.wholeWord != entry.wholeWord
                {
                    state.legacyMarkers.append(LegacyMarker(libraryId: library.id, key: key.value))
                }
            }
        }
        for library in libraries where library.builtIn {
            state.aiPermissions[library.id.lowercased()] = true
            if library.contentHash != nil {
                state.setAcceptedContent(library.contentHash, for: library.id)
            }
        }
        state.normalize()
        return state
    }

    private func synchronizeLegacyProjection(state: inout LibraryLocalState, libraries: [CatalogLibrary]) -> Bool {
        let currentLegacy = Set(settings.enabledLibraryIds.map { $0.lowercased() })
        let previousLegacy = state.legacyEnabledIdSet
        guard currentLegacy != previousLegacy else {
            return false
        }

        let groups = Dictionary(grouping: libraries) {
            if !$0.builtIn, let file = $0.fileName {
                return URL(fileURLWithPath: file).deletingPathExtension().lastPathComponent.lowercased()
            }
            return $0.id.lowercased()
        }.mapValues { $0.map { $0.id.lowercased() } }
        var enabled = state.enabledIdSet
        for added in currentLegacy.subtracting(previousLegacy) {
            enabled.formUnion(groups[added] ?? [])
        }
        for removed in previousLegacy.subtracting(currentLegacy) {
            enabled.subtract(groups[removed] ?? [])
        }

        state.enabledIds = Array(enabled).sorted()
        state.legacyEnabledIds = Array(currentLegacy).sorted()
        state.generation += 1
        return true
    }

    private func decorate(libraries: [CatalogLibrary], with state: LibraryLocalState) -> [CatalogLibrary] {
        let markersByID = Dictionary(grouping: state.legacyMarkers) { $0.libraryId.lowercased() }
        return libraries.map { library in
            let marked = Set((markersByID[library.id.lowercased()] ?? []).map(\.termKey))
            let authored: Set<LibraryTermKey>
            if library.builtIn {
                authored = library.library.authoredKeys
            } else {
                authored = Set(library.library.entries.map { LibraryTermKey.from($0.pattern) })
            }
            let decoratedLibrary = DictionaryLibrary(
                id: library.library.id,
                name: library.library.name,
                category: library.library.category,
                description: library.library.description,
                builtIn: library.library.builtIn,
                entries: library.library.entries,
                fileName: library.library.fileName,
                basedOn: library.library.basedOn,
                authoredKeys: authored,
                legacyMarkedKeys: marked)
            return CatalogLibrary(
                library: decoratedLibrary,
                state: library.state,
                contentHash: library.contentHash,
                origin: library.origin,
                edits: library.edits,
                previousEditsAvailable: library.previousEditsAvailable,
                readErrorCount: library.readErrorCount)
        }
    }

    func compose(catalog: LibraryCatalog) -> LibraryComposition {
        WordPackComposition.compose(catalog)
    }

    private func library(named id: String, in libraries: [CatalogLibrary]) -> CatalogLibrary? {
        libraries.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }

    private func isEnabled(_ id: String, in state: LibraryLocalState) -> Bool {
        state.enabledIdSet.contains(id.lowercased())
    }

    private func isUsable(_ state: LibraryFileState) -> Bool {
        state == .available || state == .partlyReadable
    }

    private func isAIPermitted(_ library: CatalogLibrary, state: LibraryLocalState) -> Bool {
        guard state.health == .absent || state.health == .ok else {
            return false
        }

        let key = library.id.lowercased()
        guard contentIsAccepted(library, state: state) else { return false }

        if let chosen = state.aiPermissions[key] {
            return chosen
        }
        if state.aiPermissionsLost {
            return false
        }
        return defaultAIPermission(for: library.origin, builtIn: library.builtIn, sourcePermission: nil)
    }

    private func contentIsAccepted(_ library: CatalogLibrary, state: LibraryLocalState) -> Bool {
        let accepted = state.acceptedContent[library.id.lowercased()]
        if library.builtIn { return accepted == library.contentHash?.value }
        return accepted != nil && accepted == library.contentHash?.value
    }

    private func defaultAIPermission(for origin: LibraryOrigin, builtIn: Bool, sourcePermission: Bool?) -> Bool {
        if builtIn {
            return true
        }
        switch origin {
        case .existing:
            return true
        case .duplicated, .retiredBuiltIn:
            return sourcePermission ?? false
        default:
            return false
        }
    }

    private func saveState(_ state: LibraryLocalState) async throws {
        guard let persistenceStore else { return }
        let data = try JSONEncoder().encode(state)
        let value = String(data: data, encoding: .utf8)
        try await persistenceStore.saveStringSetting(key: Self.libraryStateKey, value: value)
    }

    private func updatePersistedStateAfterImport(id: String, contentHash: LibraryContentHash) throws {
        guard let persistenceStore else { return }
        let libraries = loadCatalogLibraries().filter { library in
            library.id.caseInsensitiveCompare(id) != .orderedSame
        }
        let rawState = try persistenceStore.readStringSetting(key: Self.libraryStateKey)
        var state = try decodeState(rawState) ?? migratedState(for: libraries)
        state.generation = max(1, state.generation + 1)
        state.setAcceptedContent(contentHash, for: id)
        state.setAIPermission(false, for: id)
        state.legacyEnabledIds = Array(settings.enabledLibraryIds).sorted()
        state.normalize()
        let value = String(data: try JSONEncoder().encode(state), encoding: .utf8)
        try persistenceStore.writeStringSetting(key: Self.libraryStateKey, value: value)
    }

    private func updatePersistedStateAfterRemoval(id: String) throws {
        guard let persistenceStore else { return }
        if var state = try decodeState(
            persistenceStore.readStringSetting(key: Self.libraryStateKey)
        ) {
            state.generation = max(1, state.generation + 1)
            state.removeState(for: id)
            state.normalize()
            let value = String(data: try JSONEncoder().encode(state), encoding: .utf8)
            try persistenceStore.writeStringSetting(key: Self.libraryStateKey, value: value)
        }
    }

    private func libraryFiles() -> [URL] {
        (try? fileManager.contentsOfDirectory(
            at: librariesDirectory,
            includingPropertiesForKeys: nil
        )) ?? []
    }

    private func allKnownIDs() -> [String] {
        let builtIn = BuiltInDictionaryLibraries.all.map(\.id)
        let custom = libraryFiles()
            .filter { $0.pathExtension.lowercased() == "csv" }
            .map { $0.deletingPathExtension().lastPathComponent }
        return builtIn + custom
    }
}
