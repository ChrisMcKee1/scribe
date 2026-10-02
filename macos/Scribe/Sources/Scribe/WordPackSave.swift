import Foundation

struct WordPackPreparedSave: Sendable {
    let id: UUID
    let changes: LibraryChangeSet
    let participant: StoredSettingsParticipant
    let journal: WordPackJournal
}

enum WordPackCommitOutcome: Equatable, Sendable {
    case saved
    case savedPendingRecovery
    case notCommitted
    case commitUnknown
}

struct WordPackFileImage: Codable, Equatable, Sendable {
    let relativePath: String
    let expectedHash: LibraryContentHash?
    let data: Data?
}

struct WordPackJournal: Codable, Equatable, Sendable {
    let version: Int
    let id: UUID
    let generation: Int64
    let affectedIDs: [String]
    let enabledProjection: [String]
    let images: [WordPackFileImage]

    static let key = "word_pack_redo_v1"
    static let receiptKey = "word_pack_save_receipt_v1"
    static let deletedKey = "word_pack_deleted_v1"

    func encoded() throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return String(decoding: try encoder.encode(self), as: UTF8.self)
    }
}

/// All I/O is off the view model. Prepare writes nothing; abort therefore changes nothing.
/// A committed redo record is never aborted, including when its file installation could not finish.
actor WordPackSaveCoordinator {
    let store: PersistenceStore
    let service: DictionaryLibraryService
    private var preparations: [UUID: WordPackPreparedSave] = [:]

    init(store: PersistenceStore, service: DictionaryLibraryService) {
        self.store = store
        self.service = service
    }

    func prepare(_ changes: LibraryChangeSet, now: Date = Date()) async throws -> WordPackPreparedSave {
        guard preparations.isEmpty else { throw WordPackError.recoveryPending }
        guard try await WordPackMaterializer.recover(store: store, service: service) else {
            throw WordPackError.recoveryPending
        }
        let catalog = try await service.loadCatalog()
        guard catalog.generation == changes.expectedGeneration, catalog.localState.health != .newer else {
            throw WordPackError.staleCatalog
        }
        let rawState = try await store.loadStringSetting(key: DictionaryLibraryService.libraryStateKey)
        let rawDeleted = try await store.loadStringSetting(key: WordPackJournal.deletedKey)
        var deleted = try Self.decodeDeleted(rawDeleted)
        let rawReceipt = try await store.loadStringSetting(key: WordPackJournal.receiptKey)
        var local = changes.localState
        local.generation = catalog.generation + 1
        var images: [WordPackFileImage] = []
        for draft in changes.libraries {
            let old = catalog.find(id: draft.id)
            guard let expected = changes.expectedContent[draft.id],
                expected.existed == (old != nil), expected.hash == old?.contentHash,
                expected.fileName == old?.fileName
            else { throw WordPackError.outsideChange }
            let path = draft.builtIn ? "edits/\(draft.id).json" : (old?.fileName ?? "\(draft.id).csv")
            let url = try WordPackMaterializer.safeURL(root: service.librariesDirectory, relativePath: path)
            let current = try WordPackMaterializer.readIfPresent(url)
            guard current.map({ LibraryContentHash(data: $0) }) == old?.contentHash else {
                throw WordPackError.outsideChange
            }
            let contentChanged: Bool
            if let old {
                let oldValues = old.library.entries.map { TermValues(entry: $0) }
                contentChanged = draft.resetEdits || draft.recovering || draft.rows.map { $0.row.values } != oldValues
                    || draft.name != old.library.name || draft.category != old.library.category
                    || draft.description != old.library.description || draft.basedOn != old.library.basedOn
                    || draft.builtIn && draft.rows.compactMap { $0.row.edit } != (old.edits?.terms ?? [])
            } else {
                contentChanged = true
            }
            if draft.pendingDelete {
                guard !draft.builtIn else { throw WordPackError.unavailable }
                deleted.append(
                    RecentlyDeletedWordPack(
                        id: UUID(), libraryID: draft.id, name: draft.name, category: draft.category,
                        description: draft.description, basedOn: draft.basedOn,
                        values: draft.rows.map { $0.row.values }, deletedAt: now))
                images.append(WordPackFileImage(relativePath: path, expectedHash: old?.contentHash, data: nil))
                local.removeState(for: draft.id)
            } else if contentChanged {
                let data: Data?
                if draft.builtIn {
                    guard let shipped = BuiltInDictionaryLibraries.all.first(where: { $0.id == draft.id }) else {
                        throw WordPackError.unavailable
                    }
                    let edits = try BuiltInLibraryOverlay.collect(
                        shipped: shipped, committed: draft.resetEdits ? nil : old?.edits, rows: draft.rows.map(\.row))
                    data = try edits.map(BuiltInLibraryOverlay.write)
                    if let current {
                        let previous = draft.recovering
                            ? "edits/\(draft.id).backup-\(UUID().uuidString).json"
                            : "edits/\(draft.id).previous.json"
                        let previousURL = try WordPackMaterializer.safeURL(root: service.librariesDirectory, relativePath: previous)
                        let prior = try WordPackMaterializer.readIfPresent(previousURL)
                        images.append(
                            WordPackFileImage(
                                relativePath: previous, expectedHash: prior.map { LibraryContentHash(data: $0) },
                                data: current))
                    }
                } else {
                    data = try DictionaryLibraryCsv.exportManaged(
                        LibraryCsvContent(
                            name: draft.name, category: draft.category, description: draft.description,
                            basedOn: draft.basedOn,
                            rows: draft.rows.filter {
                                !$0.row.values.spoken.isEmpty || !$0.row.values.written.isEmpty
                            }.map { $0.row.values }))
                }
                images.append(WordPackFileImage(relativePath: path, expectedHash: old?.contentHash, data: data))
                local.setAcceptedContent(data.map { LibraryContentHash(data: $0) }, for: draft.id)
            }
        }
        deleted.removeAll {
            changes.purgeIDs.contains($0.id) || changes.restoreIDs.contains($0.id) || $0.expired(at: now)
        }
        local.health = .ok
        local.normalize()
        // Old builds consume this projection. Withheld packs still apply locally in the new model.
        var projectionGroups: [String: [String]] = [:]
        for item in catalog.libraries where !changes.libraries.contains(where: { $0.id == item.id && $0.pendingDelete }) {
            let key = item.builtIn ? item.id : item.fileName.map {
                URL(fileURLWithPath: $0).deletingPathExtension().lastPathComponent
            } ?? item.id
            projectionGroups[key, default: []].append(item.id)
        }
        for draft in changes.libraries where !draft.pendingDelete && catalog.find(id: draft.id) == nil {
            projectionGroups[draft.id, default: []].append(draft.id)
        }
        let projection = projectionGroups.filter { _, ids in
            ids.allSatisfy { local.enabledIdSet.contains($0.lowercased()) && local.aiPermissions[$0.lowercased()] != false }
        }.map(\.key).sorted()
        local.legacyEnabledIds = projection
        let id = UUID()
        let journal = WordPackJournal(
            version: 1, id: id, generation: local.generation, affectedIDs: changes.libraries.map(\.id),
            enabledProjection: projection, images: images)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let state = String(decoding: try encoder.encode(local), as: UTF8.self)
        let deletedValue = String(decoding: try encoder.encode(deleted), as: UTF8.self)
        let participant = StoredSettingsParticipant(
            checks: [
                StoredSettingCheck(key: DictionaryLibraryService.libraryStateKey, expected: rawState),
                StoredSettingCheck(key: WordPackJournal.key, expected: nil),
                StoredSettingCheck(key: WordPackJournal.deletedKey, expected: rawDeleted),
                StoredSettingCheck(key: WordPackJournal.receiptKey, expected: rawReceipt),
            ],
            writes: [
                StoredSettingWrite(key: DictionaryLibraryService.libraryStateKey, value: state),
                StoredSettingWrite(key: WordPackJournal.key, value: try journal.encoded()),
                StoredSettingWrite(key: WordPackJournal.deletedKey, value: deletedValue),
                StoredSettingWrite(key: WordPackJournal.receiptKey, value: id.uuidString),
            ])
        let prepared = WordPackPreparedSave(id: id, changes: changes, participant: participant, journal: journal)
        preparations[id] = prepared
        return prepared
    }

    func abort(_ prepared: WordPackPreparedSave) {
        preparations.removeValue(forKey: prepared.id)
    }

    /// Convenience for a word-pack-only Save. Settings can instead include participant in its own transaction.
    func commit(
        _ prepared: WordPackPreparedSave, alongside participants: [StoredSettingsParticipant] = []
    ) async -> WordPackCommitOutcome {
        guard let held = preparations[prepared.id],
            held.participant == prepared.participant, held.changes == prepared.changes
        else { return .notCommitted }
        do {
            try await store.commitSettingsParticipants(participants + [prepared.participant])
        } catch {
            guard let receipt = try? await store.loadStringSetting(key: WordPackJournal.receiptKey) else {
                return .commitUnknown
            }
            guard receipt == prepared.id.uuidString else {
                preparations.removeValue(forKey: prepared.id)
                return .notCommitted
            }
        }
        return await complete(prepared)
    }

    /// Call after the shared SQLite commit, even after an uncertain return. It verifies the durable receipt first.
    func complete(_ prepared: WordPackPreparedSave) async -> WordPackCommitOutcome {
        do {
            guard try await store.loadStringSetting(key: WordPackJournal.receiptKey) == prepared.id.uuidString else {
                return .notCommitted
            }
            preparations.removeValue(forKey: prepared.id)
            return try await WordPackMaterializer.recover(store: store, service: service)
                ? .saved : .savedPendingRecovery
        } catch {
            return .savedPendingRecovery
        }
    }

    func recentlyDeleted() async throws -> [RecentlyDeletedWordPack] {
        try Self.decodeDeleted(await store.loadStringSetting(key: WordPackJournal.deletedKey))
    }

    /// Explicit recovery choice: keep every outside image in a separate backup, then finish the already committed Save.
    func preserveOutsideChangesAndFinish() async throws -> WordPackCommitOutcome {
        guard try await WordPackMaterializer.preserveOutsideChanges(store: store, service: service) else {
            return .savedPendingRecovery
        }
        return try await WordPackMaterializer.recover(store: store, service: service) ? .saved : .savedPendingRecovery
    }

    func previousBuiltInEdits(_ libraryID: String) throws -> BuiltInLibraryEdits? {
        let path = "edits/\(libraryID).previous.json"
        let url = try WordPackMaterializer.safeURL(root: service.librariesDirectory, relativePath: path)
        guard let data = try WordPackMaterializer.readIfPresent(url) else { return nil }
        return BuiltInLibraryOverlay.read(libraryID: libraryID, data: data).edits
    }

    static func decodeDeleted(_ raw: String?) throws -> [RecentlyDeletedWordPack] {
        guard let raw else { return [] }
        return try JSONDecoder().decode([RecentlyDeletedWordPack].self, from: Data(raw.utf8))
    }
}
