import Darwin
import Foundation
import Security
import XCTest
import os

@testable import Scribe

// Isolation for tests. Each test gets its own defaults suite, Keychain service and directory, so
// tests can run in parallel worker processes without sharing state, and a test run never reads,
// replaces or deletes a developer's real settings or credentials.

/// A `UserDefaults` suite that belongs to one test. The name ends in a fresh UUID, so no other test,
/// not even the same test in another worker process, can see it.
struct IsolatedDefaults {
    let suiteName: String
    let defaults: UserDefaults

    init(label: String = "defaults") {
        let suiteName = "com.scribe.macos.tests.\(label).\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suiteName) else {
            preconditionFailure("UserDefaults rejected the test suite name \(suiteName)")
        }
        self.suiteName = suiteName
        self.defaults = defaults
    }

    /// Deletes everything written to the suite.
    func removePersistentDomain() {
        Self.removePersistentDomain(named: suiteName)
    }

    static func removePersistentDomain(named suiteName: String) {
        UserDefaults(suiteName: suiteName)?.removePersistentDomain(forName: suiteName)
    }
}

/// Keychain service names that belong to one test, and their cleanup.
enum TestKeychain {
    /// A service name no production code and no other test uses.
    static func uniqueService(label: String = "keychain") -> String {
        "com.scribe.macos.tests.\(label).\(UUID().uuidString)"
    }

    /// Deletes every generic password stored under `service`, whatever its account.
    static func removeAllItems(service: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
        ]
        // Repeated, and bounded, because one call can delete only the first match in the file-based
        // keychain.
        var attempts = 0
        while attempts < 64, SecItemDelete(query as CFDictionary) == errSecSuccess {
            attempts += 1
        }
    }
}

/// A directory that belongs to one test, under the user's temporary directory.
struct TemporaryDirectory {
    let url: URL

    init(label: String = "files") throws {
        url = FileManager.default.temporaryDirectory
            .appendingPathComponent("ScribeTests-\(label)-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    }

    func remove() {
        try? FileManager.default.removeItem(at: url)
    }
}

extension XCTestCase {
    /// A defaults suite for this test, removed when the test ends.
    func makeIsolatedDefaults(label: String = "defaults") -> IsolatedDefaults {
        let isolated = IsolatedDefaults(label: label)
        let suiteName = isolated.suiteName
        addTeardownBlock {
            IsolatedDefaults.removePersistentDomain(named: suiteName)
        }
        return isolated
    }

    /// A Keychain service name for this test; every item under it is deleted when the test ends.
    func makeUniqueKeychainService(label: String = "keychain") -> String {
        let service = TestKeychain.uniqueService(label: label)
        addTeardownBlock {
            TestKeychain.removeAllItems(service: service)
        }
        return service
    }

    /// A new, empty directory for this test, removed with its contents when the test ends.
    func makeTemporaryDirectory(label: String = "files") throws -> URL {
        let url = try TemporaryDirectory(label: label).url
        addTeardownBlock {
            try? FileManager.default.removeItem(at: url)
        }
        return url
    }

    /// Records every event `ScribeLog` renders until the test ends.
    func recordScribeLog() -> ScribeLogRecorder {
        let recorder = ScribeLogRecorder()
        addTeardownBlock {
            recorder.stop()
        }
        return recorder
    }
}

/// Collects what `ScribeLog` passes on while it is recording: each event's standard error line and the
/// unified log's arguments.
final class ScribeLogRecorder: Sendable {
    private let recorded: OSAllocatedUnfairLock<[ScribeLog.Rendering]>
    private let observation: ScribeLog.Observation

    init() {
        let recorded = OSAllocatedUnfairLock<[ScribeLog.Rendering]>(initialState: [])
        self.recorded = recorded
        self.observation = ScribeLog.addObserver { rendering in
            recorded.withLock { $0.append(rendering) }
        }
    }

    var renderings: [ScribeLog.Rendering] {
        recorded.withLock { $0 }
    }

    /// The standard error lines.
    var lines: [String] {
        renderings.map(\.line)
    }

    /// Every standard error line and every public unified log argument, which is all a log reader
    /// without a private-data logging profile can see.
    var publicText: String {
        renderings.flatMap { [$0.line, $0.publicText ?? ""] }.joined(separator: "\n")
    }

    func stop() {
        observation.cancel()
    }
}

/// Values that must never reach a log line. Every one contains "canary", so one check catches a leak
/// of any of them, whole or in part. Keep the word out of the names of fixture types and cases, which a
/// failure shape or an enum name field logs by design.
enum PrivacyCanary {
    static let secret = "sk-canary-7f3a9e1b5c2d"
    static let path = "/Users/dana-canary/Library/Application Support/Scribe/asr-work/canary.wav"
    static let url = "https://canary-7f3a.openai.azure.com/openai/v1/chat/completions?api-key=sk-canary"
    static let transcript = "Canary quarterly numbers for Dana are due on Friday"

    static let all = [secret, path, url, transcript]

    static func assertAbsent(from text: String, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertFalse(
            text.lowercased().contains("canary"), "A privacy canary reached: \(text)", file: file, line: line)
    }
}

/// A gate a child process opens by creating a file, which a test waits for without polling: a kqueue
/// on the directory wakes it on each change, and the file is checked after every wake.
enum FileGate {
    /// `true` once a file exists at `url`, `false` if `timeout` passes first.
    static func waitForFile(at url: URL, timeout: Duration) async -> Bool {
        await withCheckedContinuation { continuation in
            let thread = Thread {
                continuation.resume(returning: FileGate.blockUntilFileExists(at: url, timeout: timeout))
            }
            thread.name = "ScribeTests.FileGate"
            thread.start()
        }
    }

    private static func blockUntilFileExists(at url: URL, timeout: Duration) -> Bool {
        let path = url.path(percentEncoded: false)
        let directoryDescriptor = open(url.deletingLastPathComponent().path(percentEncoded: false), O_EVTONLY)
        guard directoryDescriptor >= 0 else { return FileManager.default.fileExists(atPath: path) }
        defer { close(directoryDescriptor) }

        let queue = kqueue()
        guard queue >= 0 else { return FileManager.default.fileExists(atPath: path) }
        defer { close(queue) }

        // Registered before the first check, so a file created between a check and the wait still
        // wakes the wait.
        var change = kevent(
            ident: UInt(directoryDescriptor),
            filter: Int16(EVFILT_VNODE),
            flags: UInt16(EV_ADD | EV_CLEAR),
            fflags: UInt32(NOTE_WRITE),
            data: 0,
            udata: nil)
        guard kevent(queue, &change, 1, nil, 0, nil) == 0 else {
            return FileManager.default.fileExists(atPath: path)
        }

        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: timeout)
        while !FileManager.default.fileExists(atPath: path) {
            let remaining = clock.now.duration(to: deadline)
            guard remaining > .zero else { return false }
            let (seconds, attoseconds) = remaining.components
            var wait = timespec(tv_sec: Int(seconds), tv_nsec: Int(attoseconds / 1_000_000_000))
            var event = kevent()
            _ = kevent(queue, nil, 0, &event, 1, &wait)
        }
        return true
    }
}

/// What this test process holds right now, and whether the processes a test started are still running,
/// read from the kernel, for tests that prove a run lets go of everything it opened or started.
enum ProcessResources {
    /// Open pipe and kqueue descriptors, the two kinds a process runner creates.
    static func pipesAndQueues() -> (pipes: Int, queues: Int) {
        let pid = getpid()
        let stride = MemoryLayout<proc_fdinfo>.stride
        let needed = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, nil, 0)
        guard needed > 0 else { return (-1, -1) }
        var descriptors = [proc_fdinfo](repeating: proc_fdinfo(), count: Int(needed) / stride + 64)
        let filled = descriptors.withUnsafeMutableBytes { buffer in
            proc_pidinfo(pid, PROC_PIDLISTFDS, 0, buffer.baseAddress, Int32(buffer.count))
        }
        guard filled > 0 else { return (-1, -1) }
        let open = descriptors.prefix(Int(filled) / stride)
        return (
            open.filter { $0.proc_fdtype == UInt32(PROX_FDTYPE_PIPE) }.count,
            open.filter { $0.proc_fdtype == UInt32(PROX_FDTYPE_KQUEUE) }.count
        )
    }

    /// Threads whose name starts with `prefix`.
    static func threads(named prefix: String) -> Int {
        let pid = getpid()
        var handles = [UInt64](repeating: 0, count: 4096)
        let filled = handles.withUnsafeMutableBytes { buffer in
            proc_pidinfo(pid, PROC_PIDLISTTHREADS, 0, buffer.baseAddress, Int32(buffer.count))
        }
        guard filled > 0 else { return -1 }
        var count = 0
        for handle in handles.prefix(Int(filled) / MemoryLayout<UInt64>.stride) {
            var info = proc_threadinfo()
            let size = Int32(MemoryLayout<proc_threadinfo>.size)
            guard proc_pidinfo(pid, PROC_PIDTHREADINFO, handle, &info, size) == size else { continue }
            let name = withUnsafeBytes(of: info.pth_name) { bytes in
                String(decoding: bytes.prefix { $0 != 0 }, as: UTF8.self)
            }
            if name.hasPrefix(prefix) {
                count += 1
            }
        }
        return count
    }

    /// Whether `pid` has ended within `timeout`. A process that is already gone, or is a zombie, has; for
    /// one that is still running, a kqueue waits for the kernel to report its exit, so nothing polls.
    static func waitForExit(of pid: pid_t, timeout: Duration) -> Bool {
        let queue = kqueue()
        guard queue >= 0 else { return !isAlive(pid) }
        defer { close(queue) }

        var change = kevent(
            ident: UInt(pid),
            filter: Int16(truncatingIfNeeded: EVFILT_PROC),
            flags: UInt16(truncatingIfNeeded: EV_ADD | EV_ONESHOT),
            fflags: UInt32(truncatingIfNeeded: NOTE_EXIT),
            data: 0,
            udata: nil)
        guard kevent(queue, &change, 1, nil, 0, nil) == 0 else {
            return errno == ESRCH || !isAlive(pid)
        }
        // Registered before this check, so an exit after it still wakes the wait below.
        guard isAlive(pid) else { return true }

        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: timeout)
        while true {
            let remaining = clock.now.duration(to: deadline)
            guard remaining > .zero else { return false }
            let (seconds, attoseconds) = remaining.components
            var wait = timespec(tv_sec: Int(seconds), tv_nsec: Int(attoseconds / 1_000_000_000))
            var event = kevent()
            let count = kevent(queue, nil, 0, &event, 1, &wait)
            if count > 0 {
                return true
            }
            if count < 0 && errno != EINTR {
                return !isAlive(pid)
            }
        }
    }

    /// Whether `pid` is a running process: neither gone nor a zombie.
    static func isAlive(_ pid: pid_t) -> Bool {
        var info = proc_bsdinfo()
        let size = Int32(MemoryLayout<proc_bsdinfo>.size)
        errno = 0
        if proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, size) == size {
            return info.pbi_status != UInt32(SZOMB)
        }
        return errno != ESRCH
    }
}
