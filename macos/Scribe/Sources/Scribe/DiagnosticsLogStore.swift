import Darwin
import Foundation
import os

/// Only ScribeLog's redacted, shape-only rendering reaches disk. Legacy unshaped lines are excluded.
final class DiagnosticsLogStore: Sendable {
    static let live = DiagnosticsLogStore(
        directory: FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Scribe/Logs", isDirectory: true))

    static let retentionDays = 7
    static let dayByteLimit = 16 * 1_024 * 1_024
    static let totalByteLimit = 64 * 1_024 * 1_024
    static let queueLimit = 8_192

    let directory: URL
    private let queue = DispatchQueue(label: "com.scribe.macos.diagnostics", qos: .utility)
    private struct State: Sendable {
        var pending = 0
        var dropped = 0
        var sweptDay: String?
    }
    private let state = OSAllocatedUnfairLock(initialState: State())

    init(directory: URL) {
        self.directory = directory
    }

    func append(_ rendering: ScribeLog.Rendering, at date: Date = Date()) {
        guard rendering.publicText != nil else { return }
        let accepted = state.withLock { value in
            guard value.pending < Self.queueLimit else {
                value.dropped += 1
                return false
            }
            value.pending += 1
            return true
        }
        guard accepted else { return }
        queue.async { [self] in
            defer { state.withLock { $0.pending -= 1 } }
            let dropped = state.withLock { value in
                let count = value.dropped
                value.dropped = 0
                return count
            }
            do {
                try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                let name = Self.fileName(at: date)
                let file = directory.appendingPathComponent(name)
                if state.withLock({ $0.sweptDay }) != name {
                    try sweep(at: date, keeping: name)
                    state.withLock { $0.sweptDay = name }
                }
                let exists = FileManager.default.fileExists(atPath: file.path)
                let bytes = exists ? (try file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0) : 0
                guard bytes < Self.dayByteLimit else { return }
                let prefix = dropped > 0 ? "[Diagnostics] warning: log entries dropped count=\(dropped)\n" : ""
                let text = "\(ISO8601DateFormatter().string(from: date)) \(rendering.line)\n"
                try write(Data((prefix + text).utf8), to: file)
            } catch {
                // Do not log through ScribeLog here: that would feed the failed disk writer again.
                _ = StandardErrorSink.emit(
                    "[Diagnostics] warning: log write failed failure=[\(FailureShape(error).description)]")
            }
        }
    }

    func flush() async {
        await withCheckedContinuation { continuation in
            queue.async { continuation.resume() }
        }
    }

    func finish() {
        let finished = DispatchSemaphore(value: 0)
        queue.async { finished.signal() }
        if finished.wait(timeout: .now() + 2) == .timedOut {
            _ = StandardErrorSink.emit("[Diagnostics] warning: log drain timed out")
        }
    }

    func snapshot() async throws -> [(name: String, contents: Data)] {
        try await withCheckedThrowingContinuation { continuation in
            queue.async { [self] in
                do {
                    guard FileManager.default.fileExists(atPath: directory.path) else {
                        continuation.resume(returning: [])
                        return
                    }
                    try sweep(at: Date(), keeping: Self.fileName(at: Date()))
                    let files = try logFiles()
                    let contents = try files.map { file in
                        (name: file.lastPathComponent, contents: try Data(contentsOf: file))
                    }
                    continuation.resume(returning: contents)
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    static func fileName(at date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyyMMdd"
        return "scribe-\(formatter.string(from: date)).log"
    }

    private func write(_ data: Data, to file: URL) throws {
        let descriptor = Darwin.open(file.path, O_WRONLY | O_CREAT | O_APPEND | O_NOFOLLOW, mode_t(0o600))
        guard descriptor >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        defer {
            if Darwin.close(descriptor) != 0 {
                _ = StandardErrorSink.emit("[Diagnostics] warning: log close failed errno=\(errno)")
            }
        }
        try data.withUnsafeBytes { buffer in
            guard let base = buffer.baseAddress else { return }
            var offset = 0
            while offset < buffer.count {
                let count = Darwin.write(descriptor, base.advanced(by: offset), buffer.count - offset)
                if count > 0 {
                    offset += count
                } else if count < 0 && errno == EINTR {
                    continue
                } else {
                    throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
                }
            }
        }
    }

    private func logFiles() throws -> [URL] {
        try FileManager.default.contentsOfDirectory(
            at: directory, includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey, .isSymbolicLinkKey]
        )
        .filter {
            let name = $0.lastPathComponent
            guard name.count == 19, name.hasPrefix("scribe-"), name.hasSuffix(".log"),
                name.dropFirst(7).prefix(8).allSatisfy(\.isNumber)
            else { return false }
            let values = try $0.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            return values.isRegularFile == true && values.isSymbolicLink != true
        }
        .sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    private func sweep(at date: Date, keeping current: String) throws {
        let oldest = Self.fileName(at: date.addingTimeInterval(-Double(Self.retentionDays - 1) * 86_400))
        var files = try logFiles()
        for file in files where file.lastPathComponent < oldest && file.lastPathComponent != current {
            try FileManager.default.removeItem(at: file)
        }
        files = try logFiles()
        var bytes = try files.reduce(0) { count, file in
            count + (try file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0)
        }
        for file in files where bytes > Self.totalByteLimit && file.lastPathComponent != current {
            let size = try file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            try FileManager.default.removeItem(at: file)
            bytes -= size
        }
    }
}
