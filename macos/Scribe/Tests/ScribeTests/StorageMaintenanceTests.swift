import SQLite3
import XCTest
@testable import Scribe

final class StorageMaintenanceTests: XCTestCase {
    private var directory: StorageTestDirectory!
    private let fixedNow = Date(timeIntervalSince1970: 1_800_000_000)
    private let clock = StorageTestClock()

    override func setUpWithError() throws {
        directory = try StorageTestDirectory()
    }

    override func tearDownWithError() throws {
        directory.remove()
    }

    private func makeStore() throws -> PersistenceStore {
        let store = PersistenceStore(databaseURL: directory.databaseURL, progressInterval: 1)
        try store.initialize()
        return store
    }

    private func makeMaintenance(
        _ store: PersistenceStore,
        writer: HistoryWriter? = nil,
        configure: (inout StorageMaintenance.Options) -> Void = { _ in }
    ) -> StorageMaintenance {
        var options = StorageMaintenance.Options()
        // Any free page is worth reclaiming here, so the tests need only a little data.
        options.reclaimThresholdBytes = 1
        configure(&options)
        let fixedNow = self.fixedNow
        let clock = self.clock
        return StorageMaintenance(
            store: store,
            historyWriter: writer,
            options: options,
            now: { fixedNow },
            uptime: { clock.now() })
    }

    private func record(_ store: PersistenceStore, daysAgo: Double, _ text: String) throws {
        try store.recordDictation(
            startedAt: fixedNow.addingTimeInterval(-daysAgo * 86_400),
            durationSeconds: 1,
            sampleCount: 16_000,
            transcriptText: text)
    }

    /// Leaves dozens of free pages behind, through a foreground clear.
    private func fillAndClear(_ store: PersistenceStore) throws {
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.insertBulkHistory(count: 300, startedAt: fixedNow)
        raw.close()
        XCTAssertGreaterThanOrEqual(try store.clearHistory(), 300)
        XCTAssertGreaterThan(try store.pageStats().freePages, 10)
    }

    // MARK: - Retention

    func testRetentionRemovesOnlyEntriesPastTheChosenLimit() throws {
        let store = try makeStore()
        try store.setHistoryRetention(.days(30))
        try record(store, daysAgo: 45, "old")
        try record(store, daysAgo: 31, "just past")
        try record(store, daysAgo: 29, "recent")

        let report = makeMaintenance(store).runPass()

        XCTAssertEqual(report.retention, .applied(days: 30, removed: 2))
        XCTAssertEqual(try store.fetchDictationHistory().compactMap(\.transcriptText), ["recent"])
    }

    func testAHistoryWithNoRetentionChoiceKeepsEverything() throws {
        try StorageTestSQLite.createLegacyDatabase(
            at: directory.databaseURL,
            historyStartedAt: [400.0, 200.0, 100.0].map { fixedNow.addingTimeInterval(-$0 * 86_400) })
        let store = try makeStore()

        let report = makeMaintenance(store).runPass()

        XCTAssertEqual(report.retention, .kept(.notChosen))
        XCTAssertEqual(try store.historyCount(), 3)
    }

    func testAnUnreadableRetentionValueKeepsEverything() throws {
        let store = try makeStore()
        try record(store, daysAgo: 400, "old")
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.execute("UPDATE settings SET value = 'ninety' WHERE key = 'history_retention_days';")
        raw.close()

        XCTAssertEqual(makeMaintenance(store).runPass().retention, .kept(.unreadable))
        XCTAssertEqual(try store.historyCount(), 1)
    }

    func testARetentionSettingThatCannotBeReadAtAllKeepsEverything() throws {
        let store = try makeStore()
        try record(store, daysAgo: 400, "old")
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.execute("DROP TABLE settings;")
        raw.close()

        XCTAssertEqual(makeMaintenance(store).runPass().retention, .kept(.unreadable))
        XCTAssertEqual(try store.historyCount(), 1)
    }

    func testKeepForeverKeepsEverything() throws {
        let store = try makeStore()
        try store.setHistoryRetention(.keepForever)
        try record(store, daysAgo: 4_000, "ancient")

        XCTAssertEqual(makeMaintenance(store).runPass().retention, .kept(.keepForever))
        XCTAssertEqual(try store.historyCount(), 1)
    }

    func testALongHistoryIsSweptInBatches() throws {
        let store = try makeStore()
        try store.setHistoryRetention(.days(30))
        for day in 40..<45 {
            try record(store, daysAgo: Double(day), "old \(day)")
        }
        try record(store, daysAgo: 1, "recent")

        let report = makeMaintenance(store) { $0.retentionBatchSize = 2 }.runPass()

        XCTAssertEqual(report.retention, .applied(days: 30, removed: 5))
        XCTAssertEqual(try store.historyCount(), 1)
    }

    func testSetRetentionStoresTheChoiceForTheNextPass() throws {
        let store = try makeStore()
        try record(store, daysAgo: 45, "old")
        try record(store, daysAgo: 5, "recent")
        let maintenance = makeMaintenance(store)

        try maintenance.setRetention(.days(30))

        XCTAssertEqual(try store.historyRetention(), .chosen(.days(30)))
        XCTAssertEqual(maintenance.runPass().retention, .applied(days: 30, removed: 1))
    }

    // MARK: - Reclaiming space

    func testReclaimWaitsForAQuietDatabaseThenReturnsFreePagesIncrementally() throws {
        let store = try makeStore()
        try fillAndClear(store)
        let maintenance = makeMaintenance(store)

        XCTAssertEqual(maintenance.runPass().reclaim, .deferredForActivity)

        // Foreground use restarts the quiet period.
        clock.advance(by: 61)
        _ = try store.fetchAllDictionaryEntries()
        XCTAssertEqual(maintenance.runPass().reclaim, .deferredForActivity)

        clock.advance(by: 61)
        guard case .completed(.incremental, let freedPages) = maintenance.runPass().reclaim else {
            return XCTFail("expected an incremental reclaim")
        }
        XCTAssertGreaterThan(freedPages, 0)
        XCTAssertEqual(try store.pageStats().freePages, 0)
    }

    func testTheFirstReclaimConvertsAnOlderDatabaseToIncrementalAutoVacuum() throws {
        try StorageTestSQLite.createLegacyDatabase(at: directory.databaseURL, historyStartedAt: [fixedNow])
        let store = try makeStore()
        try fillAndClear(store)
        XCTAssertEqual(try store.pageStats().autoVacuum, 0)
        let maintenance = makeMaintenance(store)
        clock.advance(by: 61)

        guard case .completed(.convertedToIncremental, _) = maintenance.runPass().reclaim else {
            return XCTFail("expected the conversion VACUUM")
        }
        let stats = try store.pageStats()
        XCTAssertEqual(stats.autoVacuum, 2)
        XCTAssertEqual(stats.freePages, 0)
    }

    func testAReclaimAtItsTimeBudgetRollsBackAndLeavesTheDatabaseUsable() throws {
        try StorageTestSQLite.createLegacyDatabase(at: directory.databaseURL, historyStartedAt: [fixedNow])
        let store = try makeStore()
        try fillAndClear(store)
        let before = try store.pageStats()
        let maintenance = makeMaintenance(store) { $0.reclaimTimeBudget = 0 }
        clock.advance(by: 61)

        XCTAssertEqual(maintenance.runPass().reclaim, .outOfTime)

        let after = try store.pageStats()
        XCTAssertEqual(after.autoVacuum, 0)
        XCTAssertEqual(after.freePages, before.freePages)
        try record(store, daysAgo: 0, "still works")
        XCTAssertEqual(try store.fetchDictationHistory().compactMap(\.transcriptText), ["still works"])
    }

    func testAHousekeepingStatementStopsAtOnceForAForegroundCaller() throws {
        let armed = StorageTestSwitch()
        let waiterRegistered = StorageTestSignal()
        let store = PersistenceStore(
            databaseURL: directory.databaseURL,
            progressInterval: 1,
            onForegroundWait: {
                if armed.isOn {
                    waiterRegistered.signal()
                }
            })
        try store.initialize()
        _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "kept", replacement: "Kept"))

        let vacuumRunning = StorageTestSignal()
        let firstCheck = StorageTestSwitch()
        let outcome = StorageTestBox<Result<YieldingStatementOutcome, Error>>()
        let finished = StorageTestSignal()
        DispatchQueue.global().async {
            let result = Result {
                try store.runYieldingMaintenance("VACUUM;") {
                    // Hold the VACUUM at its first check until a foreground caller is waiting.
                    if firstCheck.claimOnce() {
                        vacuumRunning.signal()
                        _ = waiterRegistered.wait()
                    }
                    return false
                }
            }
            outcome.set(result)
            finished.signal()
        }

        XCTAssertTrue(vacuumRunning.wait())
        armed.turnOn()
        let entries = try store.fetchAllDictionaryEntries()

        XCTAssertTrue(finished.wait())
        XCTAssertEqual(entries.map(\.pattern), ["kept"])
        XCTAssertEqual(try outcome.value?.get(), .yieldedToForeground)
    }

    // MARK: - Clear history

    func testClearHistoryWaitsForWritesAcceptedBeforeItAndThenReclaims() async throws {
        let store = try makeStore()
        try record(store, daysAgo: 1, "already stored")
        let recorder = StorageTestGatedRecorder(forwardingTo: store)
        recorder.closeGate()
        let barrierWaiting = StorageTestSignal()
        let writer = HistoryWriter(recorder: recorder, onBarrierWait: { barrierWaiting.signal() })
        let maintenance = makeMaintenance(store, writer: writer)

        writer.enqueue(
            DictationHistoryRecord(
                startedAt: fixedNow, durationSeconds: 1, sampleCount: 16_000, transcriptText: "just before the click"))
        XCTAssertTrue(recorder.waitUntilStarted(1))

        let clearing = Task { try await maintenance.clearHistory() }
        XCTAssertTrue(barrierWaiting.wait())
        recorder.openGate()
        let removed = try await clearing.value

        XCTAssertEqual(removed, 2)
        XCTAssertEqual(try store.historyCount(), 0)
        XCTAssertEqual(try store.pageStats().freePages, 0)
        writer.complete(timeout: 5)
    }

    // MARK: - Schedule

    func testARequestedPassRunsSoonAfterStartAndNoneAfterStop() throws {
        let store = try makeStore()
        let finished = StorageTestSignal()
        var options = StorageMaintenance.Options()
        options.initialDelay = 3_600
        let maintenance = StorageMaintenance(
            store: store, options: options, onPassFinished: { _ in finished.signal() })

        maintenance.start()
        maintenance.requestPass()
        XCTAssertTrue(finished.wait())

        XCTAssertTrue(maintenance.stop(timeout: 5))
        XCTAssertTrue(maintenance.runPass().stopped)
    }
}
