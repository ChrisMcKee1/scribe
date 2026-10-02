import Foundation

enum WordPackMaterializer {
    private static let lane = WordPackMaterializationLane()

    /// Pending images are idempotent. An outside edit is never overwritten; its pack remains held back for review.
    static func recover(store: PersistenceStore, service: DictionaryLibraryService) async throws -> Bool {
        try await lane.run(root: service.librariesDirectory) {
            try await recoverInLane(store: store, service: service)
        }
    }

    private static func recoverInLane(store: PersistenceStore, service: DictionaryLibraryService) async throws -> Bool {
        guard let raw = try await store.loadStringSetting(key: WordPackJournal.key) else { return true }
        guard let journal = try? JSONDecoder().decode(WordPackJournal.self, from: Data(raw.utf8)), journal.version == 1
        else {
            return false
        }
        do {
            // A rollback build cannot read redo. Keep affected packs off in its projection until every image is installed.
            let physicalIDs = journal.images.filter {
                !$0.relativePath.contains("/") && $0.relativePath.hasSuffix(".csv")
            }.map {
                URL(fileURLWithPath: $0.relativePath).deletingPathExtension().lastPathComponent.lowercased()
            }
            let affected = Set(journal.affectedIDs.map { $0.lowercased() } + physicalIDs)
            service.settings.enabledLibraryIds = service.settings.enabledLibraryIds.filter {
                !affected.contains($0.lowercased())
            }
            for image in journal.images {
                let url = try safeURL(root: service.librariesDirectory, relativePath: image.relativePath)
                let current = try readIfPresent(url)
                let currentHash = current.map { LibraryContentHash(data: $0) }
                let desiredHash = image.data.map { LibraryContentHash(data: $0) }
                if currentHash == desiredHash { continue }
                guard currentHash == image.expectedHash else { return false }
                let parent = url.deletingLastPathComponent()
                try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
                _ = try safeURL(root: service.librariesDirectory, relativePath: image.relativePath)
                // Recheck after directory creation; this does not claim to exclude an uncooperative external writer.
                guard try readIfPresent(url).map({ LibraryContentHash(data: $0) }) == image.expectedHash else {
                    return false
                }
                if let data = image.data {
                    try data.write(to: url, options: .atomic)
                } else if current != nil {
                    try FileManager.default.removeItem(at: url)
                }
            }
            service.settings.enabledLibraryIds = Set(journal.enabledProjection)
            try await store.commitSettingsParticipants([
                StoredSettingsParticipant(
                    checks: [StoredSettingCheck(key: WordPackJournal.key, expected: raw)],
                    writes: [StoredSettingWrite(key: WordPackJournal.key, value: nil)])
            ])
            return true
        } catch {
            return false
        }
    }

    static func preserveOutsideChanges(store: PersistenceStore, service: DictionaryLibraryService) async throws -> Bool
    {
        try await lane.run(root: service.librariesDirectory) {
            try await preserveInLane(store: store, service: service)
        }
    }

    private static func preserveInLane(store: PersistenceStore, service: DictionaryLibraryService) async throws -> Bool {
        guard let raw = try await store.loadStringSetting(key: WordPackJournal.key),
            let journal = try? JSONDecoder().decode(WordPackJournal.self, from: Data(raw.utf8)), journal.version == 1
        else { return false }
        var images: [WordPackFileImage] = []
        for image in journal.images {
            let url = try safeURL(root: service.librariesDirectory, relativePath: image.relativePath)
            let current = try readIfPresent(url)
            let hash = current.map { LibraryContentHash(data: $0) }
            if hash != image.expectedHash && hash != image.data.map({ LibraryContentHash(data: $0) }) {
                if let current {
                    let archive = "conflicts/\(UUID().uuidString)/\(image.relativePath)"
                    _ = try safeURL(root: service.librariesDirectory, relativePath: archive)
                    images.append(WordPackFileImage(relativePath: archive, expectedHash: nil, data: current))
                }

                /// Actor reentrancy alone does not order file installation and the legacy projection across SQLite awaits.
                private actor WordPackMaterializationLane {
                    private var tails: [String: (UUID, Task<Void, Never>)] = [:]

                    func run<T: Sendable>(
                        root: URL, operation: @escaping @Sendable () async throws -> T
                    ) async throws -> T {
                        let key = root.standardizedFileURL.resolvingSymlinksInPath().path
                        let previous = tails[key]?.1
                        let id = UUID()
                        let task = Task {
                            await previous?.value
                            return try await operation()
                        }
                        let tail = Task { _ = try? await task.value }
                        tails[key] = (id, tail)
                        defer {
                            if tails[key]?.0 == id { tails.removeValue(forKey: key) }
                        }
                        return try await task.value
                    }
                }
                images.append(
                    WordPackFileImage(relativePath: image.relativePath, expectedHash: hash, data: image.data))
            } else {
                images.append(image)
            }
        }
        let repaired = WordPackJournal(
            version: journal.version, id: journal.id, generation: journal.generation,
            affectedIDs: journal.affectedIDs, enabledProjection: journal.enabledProjection, images: images)
        try await store.commitSettingsParticipants([
            StoredSettingsParticipant(
                checks: [StoredSettingCheck(key: WordPackJournal.key, expected: raw)],
                writes: [StoredSettingWrite(key: WordPackJournal.key, value: try repaired.encoded())])
        ])
        return true
    }

    static func heldBackIDs(_ raw: String?) -> Set<String>? {
        guard let raw else { return [] }
        guard let journal = try? JSONDecoder().decode(WordPackJournal.self, from: Data(raw.utf8)), journal.version == 1
        else {
            return nil
        }
        return Set(journal.affectedIDs.map { $0.lowercased() })
    }

    static func readIfPresent(_ url: URL) throws -> Data? {
        do {
            return try Data(contentsOf: url)
        } catch let error as NSError {
            if error.domain == NSCocoaErrorDomain && error.code == NSFileReadNoSuchFileError { return nil }
            throw error
        }
    }

    static func safeURL(root: URL, relativePath: String) throws -> URL {
        let components = relativePath.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        guard !components.isEmpty,
            components.allSatisfy({
                !$0.isEmpty && $0 != "." && $0 != ".." && !$0.contains("\\") && !$0.contains(":") && !$0.contains("\0")
            })
        else { throw WordPackError.unsafePath }
        var url = root
        var ancestors = [url]
        while url.path != "/" {
            url.deleteLastPathComponent()
            ancestors.append(url)
        }
        url = root
        for component in components {
            url.appendPathComponent(component)
            ancestors.append(url)
        }
        for ancestor in ancestors {
            do {
                let values = try ancestor.resourceValues(forKeys: [.isSymbolicLinkKey])
                guard values.isSymbolicLink != true else { throw WordPackError.unsafePath }
            } catch let error as NSError {
                guard error.domain == NSCocoaErrorDomain && error.code == NSFileReadNoSuchFileError else { throw error }
            }
        }
        return url
    }
}
