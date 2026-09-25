import Foundation
import SQLite3

@testable import Scribe

// Helpers for the storage tests. Every name starts with StorageTest so these never collide with
// shared test support added elsewhere.

/// A unique temporary directory per test, holding the database and its WAL files; removed in tearDown.
final class StorageTestDirectory {
    let url: URL

    init() throws {
        url = FileManager.default.temporaryDirectory
            .appendingPathComponent("ScribeStorageTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    }

    var databaseURL: URL {
        url.appendingPathComponent("scribe.db", isDirectory: false)
    }

    func remove() {
        try? FileManager.default.removeItem(at: url)
    }
}

/// Timestamps exactly as the store writes them.
enum StorageTestTimestamps {
    static func string(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: date)
    }
}

struct StorageTestSQLiteError: Error {
    let code: Int32
}

/// A raw SQLite connection of the test's own, to set up and inspect a database file the way an older
/// build or another process would.
final class StorageTestSQLite {
    private var db: OpaquePointer?

    init(_ url: URL) throws {
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE
        let code = sqlite3_open_v2(url.path(percentEncoded: false), &db, flags, nil)
        guard code == SQLITE_OK else {
            sqlite3_close_v2(db)
            db = nil
            throw StorageTestSQLiteError(code: code)
        }
        sqlite3_busy_timeout(db, 5_000)
    }

    deinit {
        close()
    }

    func close() {
        if let db {
            sqlite3_close_v2(db)
        }
        db = nil
    }

    func execute(_ sql: String) throws {
        let code = sqlite3_exec(db, sql, nil, nil, nil)
        guard code == SQLITE_OK else {
            throw StorageTestSQLiteError(code: code)
        }
    }

    func scalarInt(_ sql: String) throws -> Int64 {
        try withFirstRow(sql) { statement in sqlite3_column_int64(statement, 0) } ?? 0
    }

    func scalarText(_ sql: String) throws -> String? {
        try withFirstRow(sql) { statement in
            sqlite3_column_text(statement, 0).map { String(cString: $0) }
        } ?? nil
    }

    private func withFirstRow<T>(_ sql: String, _ read: (OpaquePointer) -> T) throws -> T? {
        var statement: OpaquePointer?
        let code = sqlite3_prepare_v2(db, sql, -1, &statement, nil)
        guard code == SQLITE_OK, let statement else {
            sqlite3_finalize(statement)
            throw StorageTestSQLiteError(code: code)
        }
        defer { sqlite3_finalize(statement) }

        switch sqlite3_step(statement) {
        case SQLITE_ROW:
            return read(statement)
        case SQLITE_DONE:
            return nil
        case let failure:
            throw StorageTestSQLiteError(code: failure)
        }
    }

    /// Writes a database the way the build before this one left it: every history column, but no
    /// started_at index, no settings table, auto_vacuum NONE and a rollback journal.
    static func createLegacyDatabase(at url: URL, historyStartedAt dates: [Date]) throws {
        let raw = try StorageTestSQLite(url)
        defer { raw.close() }
        try raw.execute(
            """
            CREATE TABLE dictation_history(
                id INTEGER PRIMARY KEY, started_at TEXT NOT NULL, duration_seconds REAL NOT NULL,
                sample_count INTEGER NOT NULL, decode_ms REAL, cleanup_ms REAL, transcript_text TEXT, target_app TEXT);
            CREATE TABLE dictionary_entries(
                id INTEGER PRIMARY KEY AUTOINCREMENT, pattern TEXT NOT NULL, replacement TEXT NOT NULL,
                whole_word INTEGER NOT NULL DEFAULT 1, enabled INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE snippets(
                id INTEGER PRIMARY KEY AUTOINCREMENT, phrase TEXT NOT NULL, template TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE app_profiles(
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, bundle_identifiers TEXT NOT NULL DEFAULT '',
                process_names TEXT NOT NULL DEFAULT '', writing_style_prompt TEXT, newline_handling TEXT);
            """)
        for (index, date) in dates.enumerated() {
            let startedAt = StorageTestTimestamps.string(date)
            try raw.execute(
                """
                INSERT INTO dictation_history(started_at, duration_seconds, sample_count, transcript_text)
                VALUES ('\(startedAt)', 1.0, 16000, 'legacy \(index)');
                """)
        }
    }

    /// Adds `count` rows of about 1.2 KB of text each in one transaction, enough to leave dozens of
    /// free pages once they are deleted.
    func insertBulkHistory(count: Int, startedAt date: Date) throws {
        try execute(
            """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < \(count))
            INSERT INTO dictation_history(started_at, duration_seconds, sample_count, transcript_text)
            SELECT '\(StorageTestTimestamps.string(date))', 1.0, 16000, hex(randomblob(600)) FROM n;
            """)
    }
}

/// A monotonic clock a test moves by hand.
final class StorageTestClock: @unchecked Sendable {
    // `@unchecked Sendable`: `value` is only read or written while `lock` is held.
    private let lock = NSLock()
    private var value: TimeInterval

    init(_ start: TimeInterval = 1_000) {
        value = start
    }

    func now() -> TimeInterval {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    func advance(by seconds: TimeInterval) {
        lock.lock()
        value += seconds
        lock.unlock()
    }
}

/// A one-shot event a test waits for, with a bound, instead of sleeping.
final class StorageTestSignal: Sendable {
    private let semaphore = DispatchSemaphore(value: 0)

    func signal() {
        semaphore.signal()
    }

    /// True when the event happened within `timeout` seconds.
    func wait(timeout: TimeInterval = 10) -> Bool {
        semaphore.wait(timeout: .now() + timeout) == .success
    }
}

/// An event an async test awaits without holding its thread, so the actor it runs on stays free.
final class StorageTestAsyncSignal: @unchecked Sendable {
    // `@unchecked Sendable`: `fired` and `waiter` are only read or written while `lock` is held.
    private let lock = NSLock()
    private var fired = false
    private var waiter: CheckedContinuation<Void, Never>?

    func fire() {
        lock.lock()
        let pending = waiter
        waiter = nil
        fired = true
        lock.unlock()
        pending?.resume()
    }

    func wait() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            lock.lock()
            if fired {
                lock.unlock()
                continuation.resume()
            } else {
                waiter = continuation
                lock.unlock()
            }
        }
    }
}

/// A switch other threads read, for arming a test seam only for the part of a test that needs it.
final class StorageTestSwitch: @unchecked Sendable {
    // `@unchecked Sendable`: `on` and `claimed` are only read or written while `lock` is held.
    private let lock = NSLock()
    private var on = false
    private var claimed = false

    var isOn: Bool {
        lock.lock()
        defer { lock.unlock() }
        return on
    }

    func turnOn() {
        lock.lock()
        on = true
        lock.unlock()
    }

    /// True for exactly one caller.
    func claimOnce() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !claimed else {
            return false
        }
        claimed = true
        return true
    }
}

/// A value handed from one thread to another.
final class StorageTestBox<Value: Sendable>: @unchecked Sendable {
    // `@unchecked Sendable`: `stored` is only read or written while `lock` is held.
    private let lock = NSLock()
    private var stored: Value?

    var value: Value? {
        lock.lock()
        defer { lock.unlock() }
        return stored
    }

    func set(_ value: Value) {
        lock.lock()
        stored = value
        lock.unlock()
    }
}

/// A task executor with exactly one thread, so a test can tell whether an async call holds its
/// caller's thread while it waits: if it does, nothing else scheduled on this executor can run.
@available(macOS 15.0, *)
final class StorageTestSerialTaskExecutor: TaskExecutor {
    private let queue = DispatchQueue(label: "com.scribe.macos.tests.storage-executor")

    func enqueue(_ job: consuming ExecutorJob) {
        let unownedJob = UnownedJob(job)
        queue.async { [self] in
            unownedJob.runSynchronously(on: asUnownedTaskExecutor())
        }
    }
}

/// Drives a store's test hooks: holds one chosen storage-queue operation, the way a write waiting out another
/// process's lock holds the queue, and tells an async test when a foreground caller has queued up behind it. Nothing
/// here blocks the main actor: `holding` and the caller signals are awaited, and only the held operation, which runs
/// on the storage queue, waits on `release`.
final class StorageTestQueueControl: @unchecked Sendable {
    // `@unchecked Sendable`: `operationsToSkip` and `callerSignals` are only read or written while `lock` is held,
    // and the signals are thread-safe themselves.
    private let lock = NSLock()
    private var operationsToSkip: Int?
    private var callerSignals: [StorageTestAsyncSignal] = []

    /// Fires once the held operation has started holding the queue.
    let holding = StorageTestAsyncSignal()
    /// Lets the held operation go on.
    let release = StorageTestSignal()

    var hooks: PersistenceStore.TestHooks {
        var hooks = PersistenceStore.TestHooks()
        hooks.onOperationBegin = { [self] _ in
            operationBegan()
        }
        hooks.onForegroundWait = { [self] in
            foregroundCallerQueued()
        }
        return hooks
    }

    /// Holds the storage operation that begins after `skipping` more operations have begun.
    func holdOperation(afterSkipping skipping: Int = 0) {
        lock.lock()
        operationsToSkip = skipping
        lock.unlock()
    }

    /// A signal that fires when the next foreground caller registers that it is waiting for the queue.
    func nextForegroundCaller() -> StorageTestAsyncSignal {
        let signal = StorageTestAsyncSignal()
        lock.lock()
        callerSignals.append(signal)
        lock.unlock()
        return signal
    }

    private func operationBegan() {
        lock.lock()
        guard let remaining = operationsToSkip else {
            lock.unlock()
            return
        }
        guard remaining == 0 else {
            operationsToSkip = remaining - 1
            lock.unlock()
            return
        }
        operationsToSkip = nil
        lock.unlock()
        holding.fire()
        // Bounded, so a test that forgets to release fails instead of hanging the run.
        _ = release.wait()
    }

    private func foregroundCallerQueued() {
        lock.lock()
        let signals = callerSignals
        callerSignals.removeAll()
        lock.unlock()
        for signal in signals {
            signal.fire()
        }
    }
}

/// Background work a storage test starts on another thread.
enum StorageTestBackground {
    /// Starts a history write on another thread and returns a signal for when it finishes.
    static func recordDictation(on store: PersistenceStore, text: String = "held write") -> StorageTestSignal {
        let done = StorageTestSignal()
        DispatchQueue.global().async {
            try? store.recordDictation(startedAt: Date(), durationSeconds: 1, sampleCount: 16_000, transcriptText: text)
            done.signal()
        }
        return done
    }
}

/// Gives each call of a faked storage read its own `SettingsTestGate`, in call order, so a test can let reads finish
/// in whatever order it wants.
actor StorageTestCallGates {
    private let gates: [SettingsTestGate]
    private var calls = 0

    init(count: Int) {
        gates = (0..<count).map { _ in SettingsTestGate() }
    }

    /// Called by the fake: waits at this call's gate, then returns the call's number, counting from 0.
    func pass() async -> Int {
        let call = calls
        calls += 1
        await gates[call].pass()
        return call
    }

    func gate(_ call: Int) -> SettingsTestGate {
        gates[call]
    }
}

/// A failure a faked storage call throws.
struct StorageTestFailure: LocalizedError, Equatable {
    var message = "The storage call failed."

    var errorDescription: String? {
        message
    }
}

/// A `UserDefaults` suite of the test's own, removed in tearDown, so no test reads or writes the
/// user's real preferences.
final class StorageTestDefaults {
    let suiteName = "com.scribe.macos.tests.storage.\(UUID().uuidString)"
    let defaults: UserDefaults

    init() {
        defaults = UserDefaults(suiteName: suiteName)!
    }

    func remove() {
        defaults.removePersistentDomain(forName: suiteName)
    }
}
