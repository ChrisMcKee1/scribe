import SQLite3
import XCTest
@testable import Scribe

/// Schema, retention setting, bounded reads and failure handling of `PersistenceStore`.
final class PersistenceStoreHistoryTests: XCTestCase {
    private var directory: StorageTestDirectory!
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    override func setUpWithError() throws {
        directory = try StorageTestDirectory()
    }

    override func tearDownWithError() throws {
        directory.remove()
    }

    private func makeStore(busyTimeoutMilliseconds: Int32 = PersistenceStore.defaultBusyTimeoutMilliseconds) throws
        -> PersistenceStore
    {
        let store = PersistenceStore(databaseURL: directory.databaseURL, busyTimeoutMilliseconds: busyTimeoutMilliseconds)
        try store.initialize()
        return store
    }

    private func record(_ store: PersistenceStore, secondsAgo: TimeInterval, _ text: String?) throws {
        try store.recordDictation(
            startedAt: now.addingTimeInterval(-secondsAgo), durationSeconds: 1, sampleCount: 16_000, transcriptText: text)
    }

    private func primaryCode(_ error: Error) -> Int32? {
        (error as? PersistenceError)?.sqliteCode.map { $0 & 0xFF }
    }

    // MARK: - Schema and retention setting

    func testANewDatabaseRecordsTheDefaultRetentionAndUsesWalWithIncrementalVacuum() throws {
        let store = try makeStore()

        XCTAssertEqual(try store.historyRetention(), .chosen(.days(HistoryRetention.defaultDays)))
        let raw = try StorageTestSQLite(directory.databaseURL)
        XCTAssertEqual(try raw.scalarText("PRAGMA journal_mode;")?.lowercased(), "wal")
        XCTAssertEqual(try raw.scalarInt("PRAGMA auto_vacuum;"), 2)
    }

    func testAnExistingHistoryWithoutAChoiceIsNotGivenTheDefault() throws {
        try StorageTestSQLite.createLegacyDatabase(
            at: directory.databaseURL, historyStartedAt: [now.addingTimeInterval(-400 * 86_400)])

        let store = try makeStore()

        XCTAssertEqual(try store.historyRetention(), .notChosen)
        XCTAssertEqual(try store.historyRetention().effective, .keepForever)
        XCTAssertEqual(try store.historyCount(), 1)
    }

    func testRetentionValuesThatAreNotWholeDaysReadAsUnreadable() throws {
        let store = try makeStore()
        let raw = try StorageTestSQLite(directory.databaseURL)

        let cases: [(stored: String, expected: HistoryRetentionSetting)] = [
            ("ninety", .unreadable),
            ("-3", .unreadable),
            ("0", .chosen(.keepForever)),
            ("999999", .chosen(.days(HistoryRetention.maximumDays))),
            ("30", .chosen(.days(30))),
        ]
        for (stored, expected) in cases {
            try raw.execute("UPDATE settings SET value = '\(stored)' WHERE key = 'history_retention_days';")
            XCTAssertEqual(try store.historyRetention(), expected, "stored value \(stored)")
        }
    }

    func testSetHistoryRetentionRoundTrips() throws {
        let store = try makeStore()

        try store.setHistoryRetention(.days(7))
        XCTAssertEqual(try store.historyRetention(), .chosen(.days(7)))
        try store.setHistoryRetention(.keepForever)
        XCTAssertEqual(try store.historyRetention(), .chosen(.keepForever))
    }

    func testMigrationBringsAnOldDatabaseUpToDateAndKeepsItsRows() throws {
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.execute(
            """
            CREATE TABLE dictation_history(
                id INTEGER PRIMARY KEY, started_at TEXT NOT NULL, duration_seconds REAL NOT NULL,
                sample_count INTEGER NOT NULL);
            INSERT INTO dictation_history(started_at, duration_seconds, sample_count)
            VALUES ('\(StorageTestTimestamps.string(now))', 2.0, 32000);
            """)
        raw.close()

        let store = try makeStore()

        let row = try XCTUnwrap(store.fetchDictationHistory().first)
        XCTAssertEqual(row.durationSeconds, 2.0)
        XCTAssertNil(row.decodeMilliseconds)
        XCTAssertNil(row.transcriptText)
        XCTAssertNil(row.targetApp)
        let check = try StorageTestSQLite(directory.databaseURL)
        XCTAssertEqual(
            try check.scalarInt(
                "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_dictation_history_started_at';"),
            1)
        XCTAssertEqual(try store.historyRetention(), .notChosen)
    }

    func testAFailedMigrationLeavesNoPartOfTheNewSchemaBehind() throws {
        // A view where the settings table belongs makes the last migration step fail after every
        // table before it was created.
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.execute("CREATE VIEW settings AS SELECT 'history_retention_days' AS key, '1' AS value;")
        raw.close()

        let store = PersistenceStore(databaseURL: directory.databaseURL)
        XCTAssertThrowsError(try store.initialize())
        store.closeConnection()

        let check = try StorageTestSQLite(directory.databaseURL)
        XCTAssertEqual(try check.scalarInt("SELECT count(*) FROM sqlite_master WHERE name = 'dictation_history';"), 0)
        XCTAssertEqual(try check.scalarInt("SELECT count(*) FROM sqlite_master WHERE name = 'dictionary_entries';"), 0)
    }

    // MARK: - Failures

    func testAReadThatFailsPartWayThrowsInsteadOfReturningTheRowsSoFar() throws {
        let store = try makeStore()
        // abs() of the smallest 64-bit integer overflows at run time, so the third row fails to step
        // after two good ones.
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.execute(
            """
            DROP TABLE dictionary_entries;
            CREATE TABLE raw_entries(
                id INTEGER PRIMARY KEY, pattern TEXT NOT NULL, replacement TEXT NOT NULL,
                whole_word INTEGER NOT NULL, enabled INTEGER NOT NULL, bomb INTEGER NOT NULL);
            INSERT INTO raw_entries VALUES
                (1, 'alpha', 'Alpha', 1, 1, 0),
                (2, 'beta', 'Beta', 1, 1, 0),
                (3, 'gamma', 'Gamma', 1, 1, -9223372036854775807 - 1);
            CREATE VIEW dictionary_entries AS
                SELECT id, pattern, replacement, whole_word, enabled + abs(bomb) * 0 AS enabled FROM raw_entries;
            """)
        raw.close()

        XCTAssertThrowsError(try store.fetchAllDictionaryEntries()) { error in
            XCTAssertEqual(self.primaryCode(error), SQLITE_ERROR)
        }
        XCTAssertThrowsError(try store.fetchEnabledDictionaryEntries())
    }

    func testAWriteMeetingALockHeldElsewhereFailsAfterTheBoundedTimeout() throws {
        let store = try makeStore(busyTimeoutMilliseconds: 100)
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.execute("BEGIN IMMEDIATE;")
        defer {
            try? raw.execute("ROLLBACK;")
            raw.close()
        }

        XCTAssertThrowsError(try store.insertDictionaryEntry(DictionaryEntry(pattern: "a", replacement: "b"))) { error in
            XCTAssertEqual(self.primaryCode(error), SQLITE_BUSY)
        }
        // In WAL mode a writer elsewhere never blocks a read.
        XCTAssertNoThrow(try store.fetchAllDictionaryEntries())
    }

    func testErrorsDescribeTheirShapeOnly() {
        let error = PersistenceError.sqlite(operation: .read, code: SQLITE_BUSY)
        XCTAssertEqual(error.errorDescription, "Scribe could not read its database (SQLite error 5: database is locked).")
    }

    // MARK: - Bounded reads

    func testHistoryReadsReturnTheNewestWindowOldestFirst() throws {
        let store = try makeStore()
        for index in 0..<10 {
            try record(store, secondsAgo: Double(10 - index) * 60, "entry \(index)")
        }

        XCTAssertEqual(
            try store.fetchDictationHistory(limit: 3).compactMap(\.transcriptText), ["entry 7", "entry 8", "entry 9"])
        let since = now.addingTimeInterval(-5 * 60)
        XCTAssertEqual(
            try store.fetchDictationHistory(since: since).compactMap(\.transcriptText),
            ["entry 5", "entry 6", "entry 7", "entry 8", "entry 9"])
        XCTAssertEqual(
            try store.fetchDictationHistory(since: since, limit: 2).compactMap(\.transcriptText), ["entry 8", "entry 9"])
    }

    func testRecentTranscriptsAreNewestFirstNonBlankAndWithinTheChosenLimit() throws {
        let store = try makeStore()
        try record(store, secondsAgo: 100 * 86_400, "too old")
        try record(store, secondsAgo: 3_000, "first")
        try record(store, secondsAgo: 2_000, nil)
        try record(store, secondsAgo: 1_000, " \n\t ")
        try record(store, secondsAgo: 500, "second")

        XCTAssertEqual(try store.fetchRecentTranscripts(limit: 5, now: now), ["second", "first"])

        try store.setHistoryRetention(.keepForever)
        XCTAssertEqual(try store.fetchRecentTranscripts(limit: 5, now: now), ["second", "first", "too old"])
        XCTAssertEqual(try store.fetchRecentTranscripts(limit: 1, now: now), ["second"])
    }

    func testTheRecoverySeedReadsOnlyTheNewestFive() throws {
        let store = try makeStore()
        for index in 0..<50 {
            try record(store, secondsAgo: Double(50 - index), "dictation \(index)")
        }

        XCTAssertEqual(
            try store.fetchRecentTranscripts(limit: LastTranscriptStore.capacity, now: now),
            ["dictation 49", "dictation 48", "dictation 47", "dictation 46", "dictation 45"])
    }

    func testRetentionBatchesDeleteOnlyOlderRowsUpToTheLimit() throws {
        let store = try makeStore()
        try store.setHistoryRetention(.days(5))
        for day in 6...10 {
            try record(store, secondsAgo: Double(day) * 86_400, "old \(day)")
        }
        try record(store, secondsAgo: 86_400, "recent one")
        try record(store, secondsAgo: 60, "recent two")

        XCTAssertEqual(try store.deleteExpiredHistoryBatch(authorizedDays: 5, now: now, limit: 3), .deleted(3))
        XCTAssertEqual(try store.deleteExpiredHistoryBatch(authorizedDays: 5, now: now, limit: 3), .deleted(2))
        XCTAssertEqual(try store.deleteExpiredHistoryBatch(authorizedDays: 5, now: now, limit: 3), .deleted(0))
        XCTAssertEqual(try store.fetchDictationHistory().compactMap(\.transcriptText), ["recent one", "recent two"])
    }

    func testARetentionBatchDeletesNothingUnlessTheStoredChoiceStillMatches() throws {
        let store = try makeStore()
        try store.setHistoryRetention(.days(5))
        try record(store, secondsAgo: 40 * 86_400, "old")

        XCTAssertEqual(
            try store.deleteExpiredHistoryBatch(authorizedDays: 30, now: now, limit: 10),
            .authorizationChanged(.chosen(.days(5))))
        try store.setHistoryRetention(.keepForever)
        XCTAssertEqual(
            try store.deleteExpiredHistoryBatch(authorizedDays: 5, now: now, limit: 10),
            .authorizationChanged(.chosen(.keepForever)))
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.execute("DELETE FROM settings WHERE key = 'history_retention_days';")
        raw.close()
        XCTAssertEqual(
            try store.deleteExpiredHistoryBatch(authorizedDays: 5, now: now, limit: 10),
            .authorizationChanged(.notChosen))
        XCTAssertEqual(try store.historyCount(), 1)
    }

    func testClearHistoryRemovesEveryRowAndReportsTheCount() throws {
        let store = try makeStore()
        try record(store, secondsAgo: 10, "one")
        try record(store, secondsAgo: 5, "two")

        XCTAssertEqual(try store.clearHistory(), 2)
        XCTAssertEqual(try store.historyCount(), 0)
    }

    func testTranscriptTextRoundTripsUnicode() throws {
        let store = try makeStore()
        let text = "caf\u{E9} \u{1F642} \u{4E2D}\u{6587}"
        try record(store, secondsAgo: 1, text)

        XCTAssertEqual(try store.fetchDictationHistory().first?.transcriptText, text)
    }

    // MARK: - Dictionary import transaction

    func testADictionaryImportIsAllOrNothing() throws {
        let store = try makeStore()
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.execute(
            """
            CREATE TRIGGER refuse_boom BEFORE INSERT ON dictionary_entries WHEN NEW.pattern = 'boom'
            BEGIN SELECT RAISE(ABORT, 'refused'); END;
            """)
        raw.close()

        XCTAssertThrowsError(
            try store.applyDictionaryChanges(
                inserts: [
                    DictionaryEntry(pattern: "fine", replacement: "Fine"),
                    DictionaryEntry(pattern: "boom", replacement: "Boom"),
                ],
                updates: []))
        XCTAssertTrue(try store.fetchAllDictionaryEntries().isEmpty)
    }

    func testADictionaryImportFailsWholeWhenAnUpdatedRowIsGone() throws {
        let store = try makeStore()

        XCTAssertThrowsError(
            try store.applyDictionaryChanges(
                inserts: [DictionaryEntry(pattern: "new", replacement: "New")],
                updates: [DictionaryEntry(id: 999, pattern: "gone", replacement: "Gone")])
        ) { error in
            XCTAssertEqual(error as? PersistenceError, .rowMissing(operation: .write))
        }
        XCTAssertTrue(try store.fetchAllDictionaryEntries().isEmpty)
    }

    // MARK: - Asynchronous forms

    func testTheAsyncFormsReadAndWriteTheSameData() async throws {
        let store = try makeStore()
        try record(store, secondsAgo: 60, "first")
        try record(store, secondsAgo: 30, "second")

        let history = try await store.loadDictationHistory(limit: 10)
        let recent = try await store.loadRecentTranscripts(limit: 5, now: now)
        XCTAssertEqual(history, try store.fetchDictationHistory(limit: 10))
        XCTAssertEqual(recent, ["second", "first"])

        _ = try await store.addDictionaryEntry(DictionaryEntry(pattern: "a", replacement: "A"))
        let entries = try await store.loadAllDictionaryEntries()
        XCTAssertEqual(entries, try store.fetchAllDictionaryEntries())
        XCTAssertEqual(entries.map(\.pattern), ["a"])

        try await store.saveHistoryRetention(.days(30))
        let retention = try await store.loadHistoryRetention()
        XCTAssertEqual(retention, .chosen(.days(30)))
    }

    /// What `makeStoreWithAHeldWrite()` hands back.
    private struct StorageTestHeldWrite {
        let store: PersistenceStore
        /// Fires when the next foreground caller registers that it is waiting for the storage queue.
        let readQueued: StorageTestAsyncSignal
        let releaseWrite: StorageTestSignal
        let writeDone: StorageTestSignal
    }

    /// A store holding one dictionary entry, whose storage queue is then held by a background write
    /// until `releaseWrite` fires, the way a write waiting out another process's lock holds it.
    private func makeStoreWithAHeldWrite() throws -> StorageTestHeldWrite {
        let holdNextOperation = StorageTestSwitch()
        let writeInside = StorageTestSignal()
        let releaseWrite = StorageTestSignal()
        let watchForWaiters = StorageTestSwitch()
        let readQueued = StorageTestAsyncSignal()
        var hooks = PersistenceStore.TestHooks()
        hooks.onOperationBegin = { _ in
            if holdNextOperation.isOn, holdNextOperation.claimOnce() {
                writeInside.signal()
                _ = releaseWrite.wait()
            }
        }
        hooks.onForegroundWait = {
            if watchForWaiters.isOn {
                readQueued.fire()
            }
        }
        let store = PersistenceStore(databaseURL: directory.databaseURL, testHooks: hooks)
        try store.initialize()
        _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "kept", replacement: "Kept"))

        holdNextOperation.turnOn()
        let writeDone = StorageTestSignal()
        DispatchQueue.global().async {
            try? store.recordDictation(
                startedAt: Date(), durationSeconds: 1, sampleCount: 16_000, transcriptText: "held write")
            writeDone.signal()
        }
        XCTAssertTrue(writeInside.wait())
        watchForWaiters.turnOn()
        return StorageTestHeldWrite(
            store: store, readQueued: readQueued, releaseWrite: releaseWrite, writeDone: writeDone)
    }

    @MainActor
    func testAMainActorReadWaitsOnTheStorageQueueWithoutHoldingTheMainActor() async throws {
        let held = try makeStoreWithAHeldWrite()
        let store = held.store
        let readFinished = StorageTestSwitch()
        let read = Task { @MainActor in
            let entries = try await store.loadAllDictionaryEntries()
            readFinished.turnOn()
            return entries
        }
        await held.readQueued.wait()

        // The read is queued behind the held write, and the main actor is free meanwhile: this test
        // runs on it, and so does another main-actor job started now.
        XCTAssertTrue(store.hasForegroundWaiters)
        XCTAssertFalse(readFinished.isOn)
        let otherMainActorWork = Task { @MainActor in 42 }
        let otherResult = await otherMainActorWork.value
        XCTAssertEqual(otherResult, 42)
        XCTAssertFalse(readFinished.isOn)

        held.releaseWrite.signal()
        let entries = try await read.value
        XCTAssertEqual(entries.map(\.pattern), ["kept"])
        XCTAssertTrue(held.writeDone.wait())
        XCTAssertEqual(try store.historyCount(), 1)
    }

    /// Stronger than the main-actor test, which a hidden `queue.sync` would also pass (a nonisolated
    /// async method already runs off the main actor): the read's own executor has one thread, and
    /// another job on it still runs while the read waits, so the wait holds no thread at all.
    func testAnAsyncReadHoldsNoThreadWhileItWaitsForTheStorageQueue() async throws {
        guard #available(macOS 15.0, *) else {
            throw XCTSkip("Task executor preferences need macOS 15.")
        }
        let held = try makeStoreWithAHeldWrite()
        let store = held.store
        let executor = StorageTestSerialTaskExecutor()
        let readFinished = StorageTestSwitch()
        let read = Task(executorPreference: executor) {
            let entries = try await store.loadAllDictionaryEntries()
            readFinished.turnOn()
            return entries
        }
        await held.readQueued.wait()

        let probe = Task(executorPreference: executor) { 42 }
        let probeResult = await probe.value
        XCTAssertEqual(probeResult, 42)
        XCTAssertFalse(readFinished.isOn)

        held.releaseWrite.signal()
        let entries = try await read.value
        XCTAssertEqual(entries.map(\.pattern), ["kept"])
        XCTAssertTrue(held.writeDone.wait())
    }
}
