import Darwin
import Foundation

/// The journal of one word pack save: every file step that brings the word pack files to what the save committed,
/// with the bytes each written file gets. It is stored in the `settings` table in the same transaction as the word
/// pack state and the Recently deleted list, so that transaction is the save's one commit point. Before it, no file,
/// state or list a reader sees has changed; after it, the operations finish the files, from the next load or save if
/// the app stops first, and the journal is retired once they are done.
///
/// This is the macOS form of Windows' library journal (`LibraryJournal`, `LibraryInstaller`). Windows writes its redo
/// images and manifest as files before its settings transaction; here they travel inside the transaction, so no
/// uncommitted journal can be left behind to discard.
struct WordPackSaveJournal: Codable, Equatable, Sendable {
    static let key = "word_pack_save_journal_v1"
    static let currentVersion = 1
    /// Where a file another app changed under a save is moved instead of being replaced. Nothing reads it again.
    static let keptFolder = "journal/kept"

    let version: Int
    let id: String
    let generation: Int64
    /// The enabled list the save gives the legacy `UserDefaults` projection, written before any file step.
    let enabledProjection: [String]
    let operations: [WordPackSaveOperation]

    /// What reading a stored journal found.
    enum Read: Equatable, Sendable {
        case trusted(WordPackSaveJournal)
        /// Written by a newer build, which is left to finish it.
        case newer(version: Int)
        /// Not a journal this build can trust whole: unreadable, a missing member, a path outside the word pack
        /// folders, or two operations on one file.
        case untrusted
    }

    static func read(_ raw: String) -> Read {
        let data = Data(raw.utf8)
        if let header = try? JSONDecoder().decode(VersionHeader.self, from: data), header.version > currentVersion {
            return .newer(version: header.version)
        }
        guard let journal = try? JSONDecoder().decode(WordPackSaveJournal.self, from: data),
            journal.version == currentVersion,
            !journal.id.isEmpty,
            journal.operations.allSatisfy(\.isWellFormed),
            WordPackSavePath.claimsDistinctFiles(journal.operations)
        else {
            return .untrusted
        }
        return .trusted(journal)
    }

    func encoded() throws -> String {
        String(decoding: try JSONEncoder().encode(self), as: UTF8.self)
    }

    private struct VersionHeader: Decodable {
        let version: Int
    }
}

/// One file step of a word pack save. Paths are relative to the word packs folder, with `/`.
struct WordPackSaveOperation: Codable, Equatable, Sendable {
    enum Kind: String, Codable, Sendable {
        /// The file at `path` gets `image`. `preImage` is the hash of what it held when the save was prepared, or nil
        /// when there was no file.
        case write
        /// The file at `path` goes, when it still holds `preImage`.
        case remove
        /// The file at `source` moves to `path`, which it never replaces.
        case move
    }

    let kind: Kind
    let path: String
    let source: String?
    let preImage: String?
    let image: Data?

    static func write(_ path: String, preImage: LibraryContentHash?, image: Data) -> WordPackSaveOperation {
        WordPackSaveOperation(kind: .write, path: path, source: nil, preImage: preImage?.value, image: image)
    }

    static func remove(_ path: String, preImage: LibraryContentHash) -> WordPackSaveOperation {
        WordPackSaveOperation(kind: .remove, path: path, source: nil, preImage: preImage.value, image: nil)
    }

    static func move(from source: String, to path: String) -> WordPackSaveOperation {
        WordPackSaveOperation(kind: .move, path: path, source: source, preImage: nil, image: nil)
    }

    /// Whether the operation has exactly the members its kind needs, and names only files the journal may touch.
    var isWellFormed: Bool {
        guard WordPackSavePath.isAllowed(path) else { return false }
        switch kind {
        case .write:
            return source == nil && image != nil
        case .remove:
            return source == nil && preImage != nil && image == nil
        case .move:
            guard let source else { return false }
            return WordPackSavePath.isAllowed(source) && preImage == nil && image == nil
        }
    }
}

/// The files a word pack save may touch, relative to the word packs folder: a custom pack (`<id>.csv`), a built-in
/// pack's edits document (`edits/<id>.json`) and a Recently deleted entry (`deleted/<entry>`). Nothing else, so a
/// damaged or tampered journal cannot reach outside those folders.
enum WordPackSavePath {
    static let editsPrefix = "edits/"
    static let deletedPrefix = "deleted/"

    static func custom(_ libraryID: String) -> String {
        "\(libraryID).csv"
    }

    static func edits(_ libraryID: String) -> String {
        editsPrefix + "\(libraryID).json"
    }

    static func deleted(_ entryName: String) -> String {
        deletedPrefix + entryName
    }

    static func isAllowed(_ path: String) -> Bool {
        if path.hasPrefix(editsPrefix) {
            let name = String(path.dropFirst(editsPrefix.count))
            return isFileName(name) && name.lowercased().hasSuffix(".json")
        }
        if path.hasPrefix(deletedPrefix) {
            return isFileName(String(path.dropFirst(deletedPrefix.count)))
        }
        return isFileName(path) && path.lowercased().hasSuffix(".csv")
    }

    /// Whether no two operations name one file, compared without case, as the default macOS volume compares names.
    static func claimsDistinctFiles(_ operations: [WordPackSaveOperation]) -> Bool {
        var claimed = Set<String>()
        for operation in operations {
            var paths = [operation.path]
            if let source = operation.source {
                paths.append(source)
            }
            for path in paths {
                guard claimed.insert(path.lowercased()).inserted else { return false }
            }
        }
        return true
    }

    private static func isFileName(_ name: String) -> Bool {
        guard !name.isEmpty, name != ".", name != ".." else { return false }
        return name.rangeOfCharacter(from: CharacterSet(charactersIn: "/\\:\u{0}")) == nil
    }
}

/// Why a word pack save did not go through, worded for Settings. The log gets the underlying failure's shape.
enum WordPackSaveError: Error, Equatable, LocalizedError {
    /// An earlier save committed but its files could not be finished, so this one was not started.
    case earlierSaveUnfinished
    /// A newer build of Scribe left a save this build cannot finish.
    case newerSaveUnfinished
    /// This save is stored, but some of its files could not be written yet.
    case filesUnfinished
    /// The stored word pack state changed while this save was being prepared, so nothing was saved.
    case changedWhileSaving
    /// The save would write outside the word pack folders, or twice to one file.
    case invalidFile
    /// A save is running, and this step does not wait for it.
    case busy

    var errorDescription: String? { message }

    var message: String {
        switch self {
        case .earlierSaveUnfinished:
            return "Scribe couldn't finish saving earlier word pack changes, so nothing new was saved. "
                + "Check that Scribe can write to its word packs folder, then try again."
        case .newerSaveUnfinished:
            return "A newer version of Scribe didn't finish saving word packs, so nothing new was saved. "
                + "Open the newer version to finish it."
        case .filesUnfinished:
            return "Scribe stored your word pack changes but couldn't write every word pack file yet. "
                + "Your edits have been kept. Try again, or Scribe finishes the next time it loads word packs."
        case .changedWhileSaving:
            return "Word packs changed while saving, so nothing was saved. Your edits have been kept. Try again."
        case .invalidFile:
            return "Word packs could not be saved. Review their edits and try again."
        case .busy:
            return "Word packs are being saved. Try again in a moment."
        }
    }
}

/// Test seams for the save journal. Each runs where the app could stop; one that throws stops the save right there,
/// as a crash would, so a test can then load from a fresh service and check what recovery makes of it.
struct WordPackSaveHooks: Sendable {
    /// After the save is prepared, before anything is stored.
    var beforeCommit: (@Sendable () throws -> Void)?
    /// Right after the commit, before any file step.
    var afterCommit: (@Sendable () throws -> Void)?
    /// After the file step with this index is done, whether by a save or by recovery.
    var afterOperation: (@Sendable (Int) throws -> Void)?
}

/// Brings the word pack files to what a committed journal says, from whatever state they are in. Every step looks at
/// its file again before it acts, so running a journal twice, or after part of it ran, ends in the same files. A
/// file's bytes only ever leave by a move (into Recently deleted or `journal/kept`) or by being replaced or removed
/// while they are exactly what the save read at prepare: a file another app changed meanwhile is kept, not lost.
struct WordPackJournalApplier {
    let root: URL
    let afterOperation: (@Sendable (Int) throws -> Void)?

    /// Applies every operation and returns how many files another app had changed, which went to `journal/kept`.
    func apply(_ journal: WordPackSaveJournal) throws -> Int {
        var kept = 0
        for (index, operation) in journal.operations.enumerated() {
            if try apply(operation, index: index, journalID: journal.id) {
                kept += 1
            }
            try afterOperation?(index)
        }
        return kept
    }

    private func apply(_ operation: WordPackSaveOperation, index: Int, journalID: String) throws -> Bool {
        let target = url(operation.path)
        switch operation.kind {
        case .write:
            guard let image = operation.image else { return false }
            var kept = false
            if let current = try WordPackDurableFiles.contentHash(at: target) {
                guard current != LibraryContentHash(data: image) else { return false }
                if current.value != operation.preImage {
                    try setAside(target, index: index, journalID: journalID)
                    kept = true
                }
            }
            try WordPackDurableFiles.ensureDirectory(target.deletingLastPathComponent())
            try WordPackDurableFiles.write(image, to: target)
            return kept
        case .remove:
            guard let current = try WordPackDurableFiles.contentHash(at: target) else { return false }
            guard current.value != operation.preImage else {
                try WordPackDurableFiles.remove(target)
                return false
            }
            try setAside(target, index: index, journalID: journalID)
            return true
        case .move:
            guard let sourcePath = operation.source else { return false }
            let source = url(sourcePath)
            let fileManager = FileManager.default
            guard !fileManager.fileExists(atPath: target.path), fileManager.fileExists(atPath: source.path) else {
                return false
            }
            try WordPackDurableFiles.ensureDirectory(target.deletingLastPathComponent())
            try WordPackDurableFiles.move(source, to: target)
            return false
        }
    }

    private func setAside(_ file: URL, index: Int, journalID: String) throws {
        let folder = root.appendingPathComponent(WordPackSaveJournal.keptFolder, isDirectory: true)
        try WordPackDurableFiles.ensureDirectory(folder)
        for attempt in 1...100 {
            let name = "\(journalID)-\(index + 1)-\(attempt).\(file.lastPathComponent)"
            do {
                try WordPackDurableFiles.move(file, to: folder.appendingPathComponent(name, isDirectory: false))
                return
            } catch let error as POSIXError where error.code == .EEXIST {
                continue
            }
        }
        throw POSIXError(.EEXIST)
    }

    private func url(_ path: String) -> URL {
        root.appendingPathComponent(path, isDirectory: false)
    }
}

/// The save journal's file steps. Each written file is flushed to the disk (`F_FULLFSYNC`, or `fsync` where the file
/// system refuses that) before it replaces its target, and the folder entry a step changes is flushed after it where
/// the file system allows, so a journal is only ever retired once its files are on the disk.
enum WordPackDurableFiles {
    static let temporaryPrefix = ".scribe-save-"
    static let temporarySuffix = ".tmp"

    /// The hash of what `url` holds, or nil when there is no file there.
    static func contentHash(at url: URL) throws -> LibraryContentHash? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        return LibraryContentHash(data: try Data(contentsOf: url))
    }

    /// Writes a flushed temporary file beside `url`, then renames it over `url`, so a reader sees the old file or the
    /// new one and never part of either.
    static func write(_ data: Data, to url: URL) throws {
        let directory = url.deletingLastPathComponent()
        let temporary = directory.appendingPathComponent(
            temporaryPrefix + UUID().uuidString + temporarySuffix,
            isDirectory: false)
        let descriptor = Darwin.open(temporary.path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0o644)
        guard descriptor >= 0 else { throw lastError() }
        do {
            try writeAll(data, to: descriptor)
            try flush(descriptor)
        } catch {
            _ = Darwin.close(descriptor)
            _ = Darwin.unlink(temporary.path)
            throw error
        }
        guard Darwin.close(descriptor) == 0 else {
            let failure = lastError()
            _ = Darwin.unlink(temporary.path)
            throw failure
        }
        guard Darwin.rename(temporary.path, url.path) == 0 else {
            let failure = lastError()
            _ = Darwin.unlink(temporary.path)
            throw failure
        }
        flushFolder(directory)
    }

    /// Moves `source` to `target` in one rename that fails, with `EEXIST`, rather than replace a file at `target`.
    static func move(_ source: URL, to target: URL) throws {
        guard renamex_np(source.path, target.path, UInt32(RENAME_EXCL)) == 0 else { throw lastError() }
        flushFolder(target.deletingLastPathComponent())
        flushFolder(source.deletingLastPathComponent())
    }

    static func remove(_ url: URL) throws {
        guard Darwin.unlink(url.path) == 0 else { throw lastError() }
        flushFolder(url.deletingLastPathComponent())
    }

    static func ensureDirectory(_ url: URL) throws {
        var isDirectory: ObjCBool = false
        if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory), isDirectory.boolValue {
            return
        }
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        flushFolder(url.deletingLastPathComponent())
    }

    /// Removes the temporary files a write left when the app stopped part way, matched by their whole name only.
    static func removeTemporaryFiles(in directory: URL) {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: directory.path)) ?? []
        for name in names where isTemporaryName(name) {
            _ = Darwin.unlink(directory.appendingPathComponent(name, isDirectory: false).path)
        }
    }

    static func isTemporaryName(_ name: String) -> Bool {
        guard name.hasPrefix(temporaryPrefix), name.hasSuffix(temporarySuffix) else { return false }
        let middle = name.dropFirst(temporaryPrefix.count).dropLast(temporarySuffix.count)
        return UUID(uuidString: String(middle)) != nil
    }

    private static func writeAll(_ data: Data, to descriptor: Int32) throws {
        try data.withUnsafeBytes { (buffer: UnsafeRawBufferPointer) in
            guard var next = buffer.baseAddress else { return }
            var remaining = buffer.count
            while remaining > 0 {
                let written = Darwin.write(descriptor, next, remaining)
                if written < 0 {
                    if errno == EINTR {
                        continue
                    }
                    throw lastError()
                }
                next += written
                remaining -= written
            }
        }
    }

    private static func flush(_ descriptor: Int32) throws {
        if fcntl(descriptor, F_FULLFSYNC) == 0 {
            return
        }
        guard fsync(descriptor) == 0 else { throw lastError() }
    }

    /// Best effort: some file systems refuse to flush a folder, and the files themselves are already flushed.
    private static func flushFolder(_ directory: URL) {
        let descriptor = Darwin.open(directory.path, O_RDONLY | O_CLOEXEC)
        guard descriptor >= 0 else { return }
        if fcntl(descriptor, F_FULLFSYNC) != 0 {
            _ = fsync(descriptor)
        }
        _ = Darwin.close(descriptor)
    }

    private static func lastError() -> POSIXError {
        POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
    }
}
