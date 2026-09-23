import Darwin
import Foundation

#if !_endian(little)
    #error("ScratchAudioDirectory writes Float32 samples as they sit in memory; WAV needs them little-endian.")
#endif

/// Why a scratch recording could not be written.
struct ScratchAudioError: Error, Equatable {
    enum Operation: Sendable, Equatable {
        case prepareDirectory
        /// The scratch path exists but is a link, not a directory, or belongs to another user.
        case unsafeDirectory
        case createFile
        case writeFile
        case invalidAudio
    }

    let operation: Operation
    let errno: Int32
}

/// A recording written for the speech recognizer. Delete it with `ScratchAudioDirectory.remove` as soon as the
/// recognizer is done with it.
struct ScratchAudioFile: Sendable, Equatable {
    let url: URL
}

/// Where Scribe puts the recordings it hands to the speech recognizer, which only reads files.
///
/// Scribe promises that audio is transcribed and discarded, so these files are kept as briefly and as privately
/// as possible: a directory of Scribe's own under the per-user temporary directory, which Time Machine skips
/// and macOS empties on its own, with mode 0700 and excluded from backup; each file created exclusively with
/// mode 0600; and deleted by `TranscriptionEngine` when the recognizer returns, however it returns. A crash or
/// a forced quit can still leave one behind, which the next launch's `sweepAbandoned` removes.
///
/// Files are named `scribe-asr-<pid>-<uuid>.wav`, so a sweep can tell a file whose process is gone from one a
/// running Scribe (another copy, the command-line verbs) is still using, and never touches anything it did not
/// name itself. Shared model caches of Foundry Local or Ollama are never touched.
struct ScratchAudioDirectory: Sendable {
    static let filePrefix = "scribe-asr-"
    static let fileExtension = "wav"

    /// A file whose process is gone is removed only once it is at least this old.
    static let abandonedAfter: Duration = .seconds(5 * 60)

    /// Earlier builds wrote `captured-<uuid>.wav` into Application Support, with no process id in the name, so
    /// only age can say that no decode is still using one.
    static let legacyAbandonedAfter: Duration = .seconds(60 * 60)

    let url: URL
    /// The directory earlier builds wrote to, swept once they stop being written; `nil` to skip it.
    let legacyDirectory: URL?

    init(url: URL, legacyDirectory: URL? = nil) {
        self.url = url
        self.legacyDirectory = legacyDirectory
    }

    static var live: ScratchAudioDirectory {
        let applicationSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
        return ScratchAudioDirectory(
            url: FileManager.default.temporaryDirectory
                .appendingPathComponent("com.scribe.macos.asr-scratch", isDirectory: true),
            legacyDirectory: applicationSupport?
                .appendingPathComponent("Scribe", isDirectory: true)
                .appendingPathComponent("asr-work", isDirectory: true))
    }

    /// Writes `samples` (mono, `sampleRate` Hz) as a 32-bit float WAV, the format the recognizers were verified
    /// with, and returns the file. Nothing is left behind when it throws.
    func writeRecording(samples: [Float], sampleRate: Double) throws -> ScratchAudioFile {
        guard sampleRate > 0, sampleRate.isFinite, sampleRate <= Double(UInt32.max) / 4 else {
            throw ScratchAudioError(operation: .invalidAudio, errno: EINVAL)
        }
        guard samples.count <= (Int(UInt32.max) - 36) / 4 else {
            throw ScratchAudioError(operation: .invalidAudio, errno: EFBIG)
        }

        try prepareDirectory()
        let file = url.appendingPathComponent(
            "\(Self.filePrefix)\(getpid())-\(UUID().uuidString).\(Self.fileExtension)", isDirectory: false)
        let path = file.path(percentEncoded: false)

        let descriptor = open(path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard descriptor >= 0 else {
            throw ScratchAudioError(operation: .createFile, errno: errno)
        }
        var written = false
        defer {
            close(descriptor)
            if !written {
                unlink(path)
            }
        }

        // The mode passed to open is filtered through the umask; this sets it exactly.
        guard fchmod(descriptor, 0o600) == 0 else {
            throw ScratchAudioError(operation: .writeFile, errno: errno)
        }
        let header = Self.wavHeader(sampleCount: samples.count, sampleRate: UInt32(sampleRate))
        try header.withUnsafeBytes { try Self.writeAll($0, to: descriptor) }
        try samples.withUnsafeBytes { try Self.writeAll($0, to: descriptor) }
        written = true
        return ScratchAudioFile(url: file)
    }

    /// Deletes `file`. A file that is already gone is not a failure; any other failure is logged by its code.
    func remove(_ file: ScratchAudioFile) {
        guard unlink(file.url.path(percentEncoded: false)) != 0 else { return }
        let code = errno
        if code != ENOENT {
            ScribeLog.warning(.transcription, "Could not delete a scratch recording", .integer("errno", code))
        }
    }

    /// Deletes the scratch recordings this process wrote, for the last moments before it exits.
    @discardableResult
    func removeFilesOfThisProcess() -> Int {
        let ownPid = getpid()
        var removed = 0
        for entry in Self.entries(in: url) where Self.ownerPid(ofFileNamed: entry.name) == ownPid {
            if unlink(entry.url.path(percentEncoded: false)) == 0 {
                removed += 1
            }
        }
        return removed
    }

    struct SweepResult: Sendable, Equatable {
        var removed = 0
        var keptInUse = 0
        var keptRecent = 0
        var failed = 0
        var legacyRemoved = 0
    }

    /// Removes scratch recordings that nothing can still be using: files whose process no longer exists and
    /// that are older than `abandonedAfter`, and old files of earlier builds. Files of a live process, this one
    /// included, and anything Scribe did not name are left alone, as is a scratch path that is a link or
    /// belongs to someone else. Run it off the main actor at launch.
    @discardableResult
    func sweepAbandoned(
        now: Date = Date(),
        isProcessAlive: (pid_t) -> Bool = ScratchAudioDirectory.isProcessAlive
    ) -> SweepResult {
        var result = SweepResult()
        let ownPid = getpid()
        let entries = Self.ownedDirectoryState(Self.fileSystemPath(url)) == .owned ? Self.entries(in: url) : []
        for entry in entries {
            guard let pid = Self.ownerPid(ofFileNamed: entry.name) else { continue }
            if pid == ownPid || isProcessAlive(pid) {
                result.keptInUse += 1
                continue
            }
            guard now.timeIntervalSince(entry.modified) >= Self.seconds(Self.abandonedAfter) else {
                result.keptRecent += 1
                continue
            }
            if unlink(entry.url.path(percentEncoded: false)) == 0 {
                result.removed += 1
            } else {
                result.failed += 1
            }
        }

        if let legacyDirectory, Self.ownedDirectoryState(Self.fileSystemPath(legacyDirectory)) == .owned {
            for entry in Self.entries(in: legacyDirectory)
            where entry.name.hasPrefix("captured-") && entry.name.hasSuffix(".wav") {
                guard now.timeIntervalSince(entry.modified) >= Self.seconds(Self.legacyAbandonedAfter) else {
                    result.keptRecent += 1
                    continue
                }
                if unlink(entry.url.path(percentEncoded: false)) == 0 {
                    result.legacyRemoved += 1
                } else {
                    result.failed += 1
                }
            }
            // Succeeds only once the directory is empty.
            rmdir(Self.fileSystemPath(legacyDirectory))
        }

        if result.removed + result.legacyRemoved + result.failed > 0 {
            ScribeLog.info(
                .transcription, "Swept abandoned scratch recordings",
                .count("removed", result.removed), .count("legacyRemoved", result.legacyRemoved),
                .count("failed", result.failed), .count("keptInUse", result.keptInUse))
        }
        return result
    }

    /// `true` while a process with this id exists, including one that belongs to another user.
    static func isProcessAlive(_ pid: pid_t) -> Bool {
        kill(pid, 0) == 0 || errno == EPERM
    }

    /// The process id in a scratch file's name, or `nil` for a name Scribe did not write.
    static func ownerPid(ofFileNamed name: String) -> pid_t? {
        guard name.hasPrefix(filePrefix), name.hasSuffix(".\(fileExtension)") else { return nil }
        let rest = name.dropFirst(filePrefix.count)
        guard let dash = rest.firstIndex(of: "-"), let pid = pid_t(rest[rest.startIndex..<dash]), pid > 0 else {
            return nil
        }
        return pid
    }

    /// The 44-byte header of a mono 32-bit IEEE float WAV, the layout earlier builds wrote and the recognizers
    /// were verified against.
    static func wavHeader(sampleCount: Int, sampleRate: UInt32) -> [UInt8] {
        let bytesPerSample: UInt32 = 4
        let dataSize = UInt32(sampleCount) * bytesPerSample
        var header: [UInt8] = []
        header.reserveCapacity(44)
        header += Array("RIFF".utf8)
        header += littleEndianBytes(36 + dataSize)
        header += Array("WAVE".utf8)
        header += Array("fmt ".utf8)
        header += littleEndianBytes(UInt32(16))
        header += littleEndianBytes(UInt16(3))
        header += littleEndianBytes(UInt16(1))
        header += littleEndianBytes(sampleRate)
        header += littleEndianBytes(sampleRate * bytesPerSample)
        header += littleEndianBytes(UInt16(bytesPerSample))
        header += littleEndianBytes(UInt16(32))
        header += Array("data".utf8)
        header += littleEndianBytes(dataSize)
        return header
    }

    // MARK: - Private

    private struct Entry {
        let name: String
        let url: URL
        let modified: Date
    }

    /// Regular files directly in `directory`, never following a link.
    private static func entries(in directory: URL) -> [Entry] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .isSymbolicLinkKey, .contentModificationDateKey]
        guard
            let urls = try? FileManager.default.contentsOfDirectory(
                at: directory, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles])
        else {
            return []
        }
        return urls.compactMap { url -> Entry? in
            guard let values = try? url.resourceValues(forKeys: Set(keys)), values.isRegularFile == true,
                values.isSymbolicLink != true
            else {
                return nil
            }
            return Entry(
                name: url.lastPathComponent, url: url, modified: values.contentModificationDate ?? .distantPast)
        }
    }

    /// Creates the directory with mode 0700 when it is missing, and refuses one that is a link or belongs to
    /// someone else, which matters only when the temporary directory falls back to the shared /tmp.
    private func prepareDirectory() throws {
        let path = Self.fileSystemPath(url)
        if mkdir(path, 0o700) != 0 {
            let code = errno
            guard code == EEXIST else {
                throw ScratchAudioError(operation: .prepareDirectory, errno: code)
            }
        }

        switch Self.ownedDirectoryState(path) {
        case .owned:
            break
        case .missing(let code):
            throw ScratchAudioError(operation: .prepareDirectory, errno: code)
        case .unsafe:
            throw ScratchAudioError(operation: .unsafeDirectory, errno: EPERM)
        }
        var info = stat()
        if lstat(path, &info) == 0, info.st_mode & 0o777 != 0o700, chmod(path, 0o700) != 0 {
            throw ScratchAudioError(operation: .prepareDirectory, errno: errno)
        }

        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        var directory = url
        try? directory.setResourceValues(values)
    }

    private enum DirectoryState: Equatable {
        case owned
        case missing(errno: Int32)
        /// A link, something other than a directory, or another user's directory.
        case unsafe
    }

    /// Looks at the path itself, never through a link, so a link planted where the directory should be is seen
    /// as a link.
    private static func ownedDirectoryState(_ path: String) -> DirectoryState {
        var info = stat()
        guard lstat(path, &info) == 0 else {
            return .missing(errno: errno)
        }
        guard info.st_mode & S_IFMT == S_IFDIR, info.st_uid == getuid() else {
            return .unsafe
        }
        return .owned
    }

    /// The URL's path without a trailing slash: with one, the kernel resolves a final link instead of
    /// describing it.
    private static func fileSystemPath(_ url: URL) -> String {
        var path = url.path(percentEncoded: false)
        while path.count > 1, path.hasSuffix("/") {
            path.removeLast()
        }
        return path
    }

    private static func writeAll(_ bytes: UnsafeRawBufferPointer, to descriptor: Int32) throws {
        guard var cursor = bytes.baseAddress else { return }
        var remaining = bytes.count
        while remaining > 0 {
            let count = Darwin.write(descriptor, cursor, remaining)
            if count > 0 {
                cursor += count
                remaining -= count
            } else if count < 0, errno == EINTR {
                continue
            } else {
                throw ScratchAudioError(operation: .writeFile, errno: count < 0 ? errno : EIO)
            }
        }
    }

    private static func littleEndianBytes<Value: FixedWidthInteger>(_ value: Value) -> [UInt8] {
        withUnsafeBytes(of: value.littleEndian) { Array($0) }
    }

    private static func seconds(_ duration: Duration) -> Double {
        let (seconds, attoseconds) = duration.components
        return Double(seconds) + Double(attoseconds) / 1e18
    }
}
