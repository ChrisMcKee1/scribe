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
    static let recentlyDeletedKey = "word_pack_recently_deleted_v1"

    let librariesDirectory: URL
    let settings: DictionaryLibrarySettings

    private let fileManager: FileManager
    private let persistenceStore: PersistenceStore?
    private let saveHooks: WordPackSaveHooks
    /// Orders, within this process, every save, every finish of an interrupted one and every other write of the word
    /// pack state. Besides keeping two saves apart, it keeps a load from reading the state and the legacy projection
    /// between a save's commit and its writing the projection, which would read the save's own change as an older
    /// build's and undo it.
    private let saveLock = NSLock()

    init(
        fileManager: FileManager = .default,
        librariesDirectory overrideDirectory: URL? = nil,
        settings: DictionaryLibrarySettings = DictionaryLibrarySettingsStore.standard,
        persistenceStore: PersistenceStore? = nil,
        saveHooks: WordPackSaveHooks = WordPackSaveHooks()
    ) {
        self.fileManager = fileManager
        self.settings = settings
        self.persistenceStore = persistenceStore
        self.saveHooks = saveHooks
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
        return try withSaveLock {
            try finishPendingSaveIfStored()
            return try importLocked(file: file, name: name, category: category)
        }
    }

    private func importLocked(file: LibraryCsvDocument, name: String, category: String) throws -> DictionaryLibrary {
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

        try withSaveLock {
            try finishPendingSaveIfStored()
            let path = librariesDirectory.appendingPathComponent(fileName)
            if fileManager.fileExists(atPath: path.path) {
                try fileManager.removeItem(at: path)
            }
            settings.setEnabled(false, id: id)
            try updatePersistedStateAfterRemoval(id: id)
        }
    }

    /// Saves a captured change set through the save journal (`WordPackSaveJournal`). An earlier save's unfinished
    /// files are finished first; then the word pack state, the Recently deleted list and this save's journal are
    /// committed in one transaction, and only then are the files brought to it. A failure before the commit changes
    /// nothing; one after it leaves the journal, which the next load or save finishes. Without a store, which only
    /// tests use, the files are changed directly and nothing is crash safe.
    func save(changeSet: LibraryChangeSet) throws {
        guard !changeSet.isEmpty else { return }
        try withSaveLock {
            guard let persistenceStore else {
                let prepared = try prepareSave(changeSet, rawState: nil, rawDeleted: nil)
                settings.enabledLibraryIds = Set(prepared.journal.enabledProjection)
                _ = try WordPackJournalApplier(root: librariesDirectory, afterOperation: nil).apply(prepared.journal)
                return
            }
            try finishPendingSaveLocked(persistenceStore, forSave: true)
            let rawState = try persistenceStore.readStringSetting(key: Self.libraryStateKey)
            let rawDeleted = try persistenceStore.readStringSetting(key: Self.recentlyDeletedKey)
            let prepared = try prepareSave(changeSet, rawState: rawState, rawDeleted: rawDeleted)
            let rawJournal = try prepared.journal.encoded()
            try saveHooks.beforeCommit?()
            let committed = try persistenceStore.commitStringSettings(
                expecting: [
                    StoredStringSetting(key: WordPackSaveJournal.key, value: nil),
                    StoredStringSetting(key: Self.libraryStateKey, value: rawState),
                    StoredStringSetting(key: Self.recentlyDeletedKey, value: rawDeleted),
                ],
                writing: [
                    StoredStringSetting(key: Self.libraryStateKey, value: prepared.state),
                    StoredStringSetting(key: Self.recentlyDeletedKey, value: prepared.recentlyDeleted),
                    StoredStringSetting(key: WordPackSaveJournal.key, value: rawJournal),
                ])
            guard committed else { throw WordPackSaveError.changedWhileSaving }
            try saveHooks.afterCommit?()
            do {
                try completeLocked(prepared.journal, raw: rawJournal, store: persistenceStore)
            } catch {
                ScribeLog.warning(.persistence, "Word pack save stored; its files finish later", .failure(error))
                throw WordPackSaveError.filesUnfinished
            }
        }
    }

    func loadCatalog() async throws -> LibraryCatalog {
        finishPendingSaveBeforeLoad()
        let libraries = loadCatalogLibraries()
        let state = try loadResolvedState(for: libraries)
        let decorated = decorate(libraries: libraries, with: state)
        let recentlyDeleted = RecentlyDeletedStore.list(
            deletedDirectory: librariesDirectory.appendingPathComponent("deleted", isDirectory: true),
            metadata: try await loadRecentlyDeletedMetadata())
        return LibraryCatalog(
            generation: state.generation,
            libraries: decorated,
            localState: state,
            recentlyDeleted: recentlyDeleted)
    }

    func listRecentlyDeleted() async throws -> [RecentlyDeletedLibrary] {
        (try await loadCatalog()).recentlyDeleted
    }

    /// Settings calls this on the main actor, so it never waits for a running save: it reports `busy` instead.
    func pruneRecentlyDeleted(now: Date = Date()) throws -> Int {
        guard saveLock.try() else { throw WordPackSaveError.busy }
        defer { saveLock.unlock() }
        try finishPendingSaveIfStored()
        let deletedDirectory = librariesDirectory.appendingPathComponent("deleted", isDirectory: true)
        var metadata = loadRecentlyDeletedMetadataSync()
        let entries = RecentlyDeletedStore.list(deletedDirectory: deletedDirectory, metadata: metadata)
        var removed = 0
        for entryName in RecentlyDeletedStore.expiredEntryNames(entries, now: now) {
            let url = deletedDirectory.appendingPathComponent(entryName, isDirectory: false)
            if fileManager.fileExists(atPath: url.path) {
                try fileManager.removeItem(at: url)
                removed += 1
            }
            metadata.removeValue(forKey: entryName)
        }
        if removed > 0 {
            try saveRecentlyDeletedMetadataSync(metadata)
        }
        return removed
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
        let builtIns = loadBuiltIns()
        let customs = loadCustomLibraries()
        return sortCatalogLibraries(builtIns + customs)
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
                return CatalogLibrary(
                    library: shipped,
                    state: .available,
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
        let fileURLs = libraryFiles()
        guard !fileURLs.isEmpty else {
            return []
        }

        var libraries: [CatalogLibrary] = []
        for fileURL in fileURLs where fileURL.pathExtension.lowercased() == "csv" {
            let id = fileURL.deletingPathExtension().lastPathComponent
            guard !id.isEmpty, let data = try? Data(contentsOf: fileURL) else {
                continue
            }

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

    private func loadResolvedState(for libraries: [CatalogLibrary]) throws -> LibraryLocalState {
        guard let persistenceStore else {
            return migratedState(for: libraries)
        }

        return try withSaveLock {
            let raw = try persistenceStore.readStringSetting(key: Self.libraryStateKey)
            var state = try decodeState(raw) ?? migratedState(for: libraries)
            state.normalize()
            let synced = synchronizeLegacyProjection(state: &state, libraries: libraries)
            if raw == nil || synced {
                try savePersistedState(state)
            }
            return state
        }
    }

    private func decodeState(_ raw: String?) throws -> LibraryLocalState? {
        guard let raw else {
            return nil
        }
        guard let data = raw.data(using: .utf8) else {
            return unreadableState()
        }
        do {
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
            state.aiPermissions[library.id.lowercased()] = true
            state.setAcceptedContent(library.contentHash, for: library.id)
        }
        let shipped = Self.shippedValues(from: libraries.filter(\.builtIn))
        for library in libraries where !library.builtIn {
            state.legacyMarkers.append(contentsOf: Self.upgradeMarkers(for: library, shipped: shipped))
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

        let groups = Dictionary(uniqueKeysWithValues: libraries.map { ($0.id.lowercased(), [$0.id.lowercased()]) })
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

    private func compose(catalog: LibraryCatalog) -> LibraryComposition {
        let activeLibraries = catalog.libraries.filter {
            isEnabled($0.id, in: catalog.localState) && isUsable($0.state)
        }
        let activeBuiltInFolds = Self.shippedValues(from: activeLibraries.filter(\.builtIn))
        var rulesByTier: [RuleTier: [ComposedLibraryRule]] = [.authored: [], .shipped: [], .legacy: []]

        for library in activeLibraries {
            for entry in library.library.entries where entry.enabled {
                let key = LibraryTermKey.from(entry.pattern)
                guard !key.isEmpty else { continue }
                let tier: RuleTier
                if library.library.legacyMarkedKeys.contains(key),
                    activeBuiltInFolds[Self.markerFold(entry.pattern)] != nil
                {
                    tier = .legacy
                } else if !library.builtIn || library.library.authoredKeys.contains(key) {
                    tier = .authored
                } else {
                    tier = .shipped
                }
                rulesByTier[tier, default: []].append(
                    ComposedLibraryRule(entry: entry, libraryId: library.id, key: key, tier: tier))
            }
        }

        var seen = Set<LibraryTermKey>()
        var rules: [ComposedLibraryRule] = []
        for tier in [RuleTier.authored, .shipped, .legacy] {
            for rule in rulesByTier[tier, default: []] where seen.insert(rule.key).inserted {
                rules.append(rule)
            }
        }

        let aiEntries = rules.filter { rule in
            guard let library = library(named: rule.libraryId, in: activeLibraries) else {
                return false
            }
            return isAIPermitted(library, state: catalog.localState)
        }.map(\.entry)

        let aiExcluded = Set(
            activeLibraries
                .filter { !isAIPermitted($0, state: catalog.localState) }
                .map { $0.id.lowercased() }
        )
        return LibraryComposition(
            rules: rules,
            entries: rules.map(\.entry),
            aiEntries: aiEntries,
            aiExcludedLibraryIds: aiExcluded,
            enabledLibraries: activeLibraries.map(\.library))
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
        let accepted = state.acceptedContent[key]
        if library.builtIn {
            if let accepted, accepted != library.contentHash?.value {
                return false
            }
        } else {
            guard let accepted, accepted == library.contentHash?.value else {
                return false
            }
        }

        if let chosen = state.aiPermissions[key] {
            return chosen
        }
        if state.aiPermissionsLost {
            return false
        }
        return defaultAIPermission(for: library.origin, builtIn: library.builtIn, sourcePermission: nil)
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

    private func savePersistedState(_ state: LibraryLocalState) throws {
        guard let persistenceStore else { return }
        let value = String(data: try JSONEncoder().encode(state), encoding: .utf8)
        try persistenceStore.writeStringSetting(key: Self.libraryStateKey, value: value)
    }

    private func loadRecentlyDeletedMetadata() async throws -> [String: RecentlyDeletedMetadata] {
        guard let persistenceStore else { return [:] }
        return try decodeRecentlyDeletedMetadata(
            try await persistenceStore.loadStringSetting(key: Self.recentlyDeletedKey))
    }

    private func loadRecentlyDeletedMetadataSync() -> [String: RecentlyDeletedMetadata] {
        guard let persistenceStore,
            let raw = try? persistenceStore.readStringSetting(key: Self.recentlyDeletedKey),
            let decoded = try? decodeRecentlyDeletedMetadata(raw)
        else {
            return [:]
        }
        return decoded
    }

    private func saveRecentlyDeletedMetadataSync(_ metadata: [String: RecentlyDeletedMetadata]) throws {
        guard let persistenceStore else { return }
        let value = String(data: try JSONEncoder().encode(metadata), encoding: .utf8)
        try persistenceStore.writeStringSetting(key: Self.recentlyDeletedKey, value: value)
    }

    private func decodeRecentlyDeletedMetadata(_ raw: String?) throws -> [String: RecentlyDeletedMetadata] {
        guard let raw, let data = raw.data(using: .utf8) else {
            return [:]
        }
        return (try? JSONDecoder().decode([String: RecentlyDeletedMetadata].self, from: data)) ?? [:]
    }

    // MARK: - Save journal

    private struct PreparedSave {
        let journal: WordPackSaveJournal
        let state: String
        let recentlyDeleted: String
    }

    private func withSaveLock<T>(_ body: () throws -> T) rethrows -> T {
        saveLock.lock()
        defer { saveLock.unlock() }
        return try body()
    }

    /// For the steps besides a save that change word pack files or state: they wait for an interrupted save to finish.
    private func finishPendingSaveIfStored() throws {
        guard let persistenceStore else { return }
        try finishPendingSaveLocked(persistenceStore, forSave: true)
    }

    /// A load finishes an interrupted save first, so it reads the files that save committed. A failure is logged and
    /// the load goes on with the files as they are: a file not finished yet reads as it was, and a custom pack whose
    /// file does not hold what was accepted is kept from AI cleanup until it does.
    private func finishPendingSaveBeforeLoad() {
        guard let persistenceStore else { return }
        withSaveLock {
            do {
                try finishPendingSaveLocked(persistenceStore, forSave: false)
            } catch {
                ScribeLog.warning(.persistence, "Word packs load before a pending save finished", .failure(error))
            }
        }
    }

    /// Finishes the files of a save that committed but did not finish them (the app stopped, or a file could not be
    /// written), then retires its journal. A journal this build cannot trust is retired without being applied; one a
    /// newer build wrote is left for that build. With `forSave`, a journal left pending refuses the step that asked.
    private func finishPendingSaveLocked(_ store: PersistenceStore, forSave: Bool) throws {
        let folders = [
            librariesDirectory,
            librariesDirectory.appendingPathComponent(BuiltInLibraryOverlay.editsFolderName, isDirectory: true),
            librariesDirectory.appendingPathComponent("deleted", isDirectory: true),
        ]
        for folder in folders {
            WordPackDurableFiles.removeTemporaryFiles(in: folder)
        }
        guard let raw = try store.readStringSetting(key: WordPackSaveJournal.key) else { return }
        switch WordPackSaveJournal.read(raw) {
        case .newer(let version):
            ScribeLog.warning(
                .persistence, "A newer Scribe left a word pack save unfinished", .integer("version", version))
            if forSave {
                throw WordPackSaveError.newerSaveUnfinished
            }
        case .untrusted:
            ScribeLog.warning(.persistence, "Retired a word pack save journal this build cannot trust")
            _ = try store.commitStringSettings(
                expecting: [StoredStringSetting(key: WordPackSaveJournal.key, value: raw)],
                writing: [StoredStringSetting(key: WordPackSaveJournal.key, value: nil)])
        case .trusted(let journal):
            do {
                try completeLocked(journal, raw: raw, store: store)
                ScribeLog.notice(
                    .persistence, "Finished an interrupted word pack save",
                    .count("operations", journal.operations.count), .integer("generation", journal.generation))
            } catch {
                ScribeLog.warning(.persistence, "Could not finish an interrupted word pack save", .failure(error))
                if forSave {
                    throw WordPackSaveError.earlierSaveUnfinished
                }
            }
        }
    }

    /// Brings the files to a committed journal, then retires it. The legacy projection comes first, so a load that
    /// runs after a failed file step already reads the enabled list the save committed.
    private func completeLocked(_ journal: WordPackSaveJournal, raw: String, store: PersistenceStore) throws {
        settings.enabledLibraryIds = Set(journal.enabledProjection)
        let applier = WordPackJournalApplier(root: librariesDirectory, afterOperation: saveHooks.afterOperation)
        let kept = try applier.apply(journal)
        if kept > 0 {
            ScribeLog.warning(
                .persistence, "Kept word pack files another app changed during a save", .count("files", kept))
        }
        _ = try store.commitStringSettings(
            expecting: [StoredStringSetting(key: WordPackSaveJournal.key, value: raw)],
            writing: [StoredStringSetting(key: WordPackSaveJournal.key, value: nil)])
    }

    /// Plans a save without changing anything: the journal of file operations, each with the hash of what its file
    /// holds now, and the state and Recently deleted list the save commits. The committed state takes the enabled
    /// list and AI choices from the change set and keeps what the change set does not carry (accepted content, legacy
    /// markers, upgrade notices) from the stored state. It records the hash of every file the save writes and drops
    /// the hash of every file it removes, deletes or restores, so AI permission stays bound to content Scribe wrote.
    private func prepareSave(
        _ changeSet: LibraryChangeSet,
        rawState: String?,
        rawDeleted: String?
    ) throws -> PreparedSave {
        let now = Date()
        let deletedDirectory = librariesDirectory.appendingPathComponent("deleted", isDirectory: true)
        var recentlyDeleted = try decodeRecentlyDeletedMetadata(rawDeleted)
        var takenEntries = Set((try? fileManager.contentsOfDirectory(atPath: deletedDirectory.path)) ?? [])
        takenEntries.formUnion(recentlyDeleted.keys)
        var operations: [WordPackSaveOperation] = []
        var removedIDs: [String] = []
        var restoredIDs: [String] = []
        var writtenContent: [(id: String, hash: LibraryContentHash?)] = []

        for deletion in changeSet.deletions {
            let path = WordPackSavePath.custom(deletion.libraryID)
            let url = wordPackFile(path)
            guard fileManager.fileExists(atPath: url.path) else { continue }
            let data = try Data(contentsOf: url)
            let document = DictionaryLibraryCsv.parseManaged(data)
            let entryName = RecentlyDeletedStore.nextEntryName(
                originalFileName: url.lastPathComponent,
                stamp: now,
                taken: takenEntries)
            takenEntries.insert(entryName)
            operations.append(.move(from: path, to: WordPackSavePath.deleted(entryName)))
            recentlyDeleted[entryName] = RecentlyDeletedMetadata(
                entryName: entryName,
                originalID: deletion.libraryID,
                name: document.name ?? BuiltInDictionaryLibraries.humanize(deletion.libraryID),
                termCount: document.terms.count,
                deletedAt: RecentlyDeletedStore.parseEntryName(entryName)?.deletedAt ?? now,
                contentHash: LibraryContentHash(data: data).value)
            removedIDs.append(deletion.libraryID)
        }

        var actedOnEntries = Set<String>()
        for action in changeSet.recentlyDeletedActions {
            guard actedOnEntries.insert(action.entryName.lowercased()).inserted else { continue }
            let sourcePath = WordPackSavePath.deleted(action.entryName)
            let source = wordPackFile(sourcePath)
            switch action.kind {
            case .deletePermanently:
                if fileManager.fileExists(atPath: source.path) {
                    let data = try Data(contentsOf: source)
                    operations.append(.remove(sourcePath, preImage: LibraryContentHash(data: data)))
                }
                recentlyDeleted.removeValue(forKey: action.entryName)
            case .restore:
                guard let restoreAsID = action.restoreAsID else { continue }
                let targetPath = WordPackSavePath.custom(restoreAsID)
                // Two restores can ask for one id; as before the journal, the first one gets it and the other stays
                // in Recently deleted.
                guard fileManager.fileExists(atPath: source.path),
                    !fileManager.fileExists(atPath: wordPackFile(targetPath).path),
                    !operations.contains(where: { $0.path.lowercased() == targetPath.lowercased() })
                else {
                    continue
                }
                operations.append(.move(from: sourcePath, to: targetPath))
                recentlyDeleted.removeValue(forKey: action.entryName)
                restoredIDs.append(restoreAsID)
            }
        }

        for write in changeSet.writes {
            guard let content = write.content else { continue }
            if write.builtIn {
                let path = WordPackSavePath.edits(write.libraryID)
                let current = try WordPackDurableFiles.contentHash(at: wordPackFile(path))
                if let edits = write.builtInEdits, !edits.terms.isEmpty {
                    let data = try BuiltInLibraryOverlay.write(edits)
                    operations.append(.write(path, preImage: current, image: data))
                    writtenContent.append((id: write.libraryID, hash: LibraryContentHash(data: data)))
                } else {
                    if let current {
                        operations.append(.remove(path, preImage: current))
                    }
                    writtenContent.append((id: write.libraryID, hash: nil))
                }
            } else {
                let data = try DictionaryLibraryCsv.exportManaged(
                    LibraryCsvContent(
                        name: content.name,
                        category: content.category,
                        description: content.description,
                        basedOn: content.basedOn,
                        rows: content.entries.map(TermValues.init(entry:))))
                let path = WordPackSavePath.custom(write.libraryID)
                let current = try WordPackDurableFiles.contentHash(at: wordPackFile(path))
                operations.append(.write(path, preImage: current, image: data))
                writtenContent.append((id: write.libraryID, hash: LibraryContentHash(data: data)))
            }
        }
        guard operations.allSatisfy(\.isWellFormed), WordPackSavePath.claimsDistinctFiles(operations) else {
            throw WordPackSaveError.invalidFile
        }

        let stored = try decodeState(rawState)
        var state = changeSet.localState
        if let stored, stored.health == .ok || stored.health == .absent {
            state.acceptedContent = stored.acceptedContent
            state.legacyMarkers = stored.legacyMarkers
            state.aiUpgradeNotice = stored.aiUpgradeNotice
        }
        state.generation = max(state.generation, (stored?.generation ?? 0) + 1)
        for id in removedIDs {
            state.removeState(for: id)
        }
        for id in restoredIDs {
            state.setAcceptedContent(nil, for: id)
        }
        for written in writtenContent {
            state.setAcceptedContent(written.hash, for: written.id)
        }
        state.normalize()

        let journal = WordPackSaveJournal(
            version: WordPackSaveJournal.currentVersion,
            id: UUID().uuidString,
            generation: state.generation,
            enabledProjection: state.enabledIds,
            operations: operations)
        let stateData = try JSONEncoder().encode(state)
        let recentlyDeletedData = try JSONEncoder().encode(recentlyDeleted)
        return PreparedSave(
            journal: journal,
            state: String(decoding: stateData, as: UTF8.self),
            recentlyDeleted: String(decoding: recentlyDeletedData, as: UTF8.self))
    }

    private func wordPackFile(_ path: String) -> URL {
        librariesDirectory.appendingPathComponent(path, isDirectory: false)
    }

    private static func deletedStamp(now: Date = Date()) -> String {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyyMMdd'T'HHmmss'Z'"
        return formatter.string(from: now)
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

    private static func shippedValues(from libraries: [CatalogLibrary]) -> [String: [TermValues]] {
        var shipped: [String: [TermValues]] = [:]
        for library in libraries where library.builtIn {
            for entry in library.library.entries where entry.enabled {
                let values = TermValues(entry: entry)
                let fold = markerFold(values.spoken)
                guard !fold.isEmpty else { continue }
                shipped[fold, default: []].append(values)
            }
        }
        return shipped
    }

    private static func upgradeMarkers(for library: CatalogLibrary, shipped: [String: [TermValues]]) -> [LegacyMarker] {
        var seen = Set<LibraryTermKey>()
        var markers: [LegacyMarker] = []
        for entry in library.library.entries where entry.enabled {
            let values = TermValues(entry: entry)
            let key = LibraryTermKey.from(values.spoken)
            guard !key.isEmpty,
                let builtIn = shipped[markerFold(values.spoken)],
                builtIn.contains(where: { !sameResult($0, values) }),
                seen.insert(key).inserted
            else {
                continue
            }
            markers.append(LegacyMarker(libraryId: library.id, key: key.value))
        }
        return markers
    }

    private static func sameResult(_ lhs: TermValues, _ rhs: TermValues) -> Bool {
        lhs.written == rhs.written && lhs.wholeWord == rhs.wholeWord
    }

    static func markerFold(_ value: String) -> String {
        SpokenFormFold.fold(value.trimmingCharacters(in: .whitespacesAndNewlines))
    }
}
