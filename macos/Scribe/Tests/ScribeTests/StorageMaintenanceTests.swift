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
        let store = PersistenceStore(
            databaseURL: directory.databaseURL, testHooks: PersistenceStore.TestHooks(progressInterval: 1))
        try store.initialize()
        return store
    }

    private func makeMaintenance(
        _ store: PersistenceStore,
        writer: HistoryWriter? = nil,
        activity: ForegroundActivity? = nil,
        hooks: StorageMaintenance.Hooks = StorageMaintenance.Hooks(),
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
            activity: activity,
            options: options,
            hooks: hooks,
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

    private var walBytes: Int {
        let path = directory.databaseURL.path(percentEncoded: false) + "-wal"
        let attributes = try? FileManager.default.attributesOfItem(atPath: path)
        return (attributes?[.size] as? NSNumber)?.intValue ?? -1
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

    func testSetRetentionStoresTheChoiceForTheNextPass() async throws {
        let store = try makeStore()
        try record(store, daysAgo: 45, "old")
        try record(store, daysAgo: 5, "recent")
        let maintenance = makeMaintenance(store)

        try await maintenance.setRetention(.days(30))

        XCTAssertEqual(try store.historyRetention(), .chosen(.days(30)))
        XCTAssertEqual(maintenance.runPass().retention, .applied(days: 30, removed: 1))
    }

    /// Six expired rows swept two at a time, with `change` applied to the stored choice right after the
    /// first batch commits. The sweep must stop there: two rows gone, four kept.
    private func assertSweepStops(afterFirstBatch change: @escaping @Sendable (URL) -> Void) throws {
        let store = try makeStore()
        try store.setHistoryRetention(.days(7))
        for day in 10..<16 {
            try record(store, daysAgo: Double(day), "expired \(day)")
        }
        let url = directory.databaseURL
        let firstBatch = StorageTestSwitch()
        var hooks = StorageMaintenance.Hooks()
        hooks.onRetentionBatch = { _ in
            if firstBatch.claimOnce() {
                change(url)
            }
        }

        let report = makeMaintenance(store, hooks: hooks) { $0.retentionBatchSize = 2 }.runPass()

        XCTAssertEqual(report.retention, .superseded(removed: 2))
        XCTAssertEqual(try store.historyCount(), 4)
    }

    func testASweepStopsWhenTheUserChoosesForeverPartWay() throws {
        try assertSweepStops { url in
            _ = try? StorageTestSQLite(url).execute(
                "UPDATE settings SET value = '0' WHERE key = 'history_retention_days';")
        }
    }

    func testASweepStopsWhenTheChoiceMovesToAnotherLimitPartWay() throws {
        try assertSweepStops { url in
            _ = try? StorageTestSQLite(url).execute(
                "UPDATE settings SET value = '365' WHERE key = 'history_retention_days';")
        }
    }

    func testASweepStopsWhenTheChoiceGoesMissingPartWay() throws {
        try assertSweepStops { url in
            _ = try? StorageTestSQLite(url).execute("DELETE FROM settings WHERE key = 'history_retention_days';")
        }
    }

    func testASweepStopsWhenTheChoiceBecomesUnreadablePartWay() throws {
        try assertSweepStops { url in
            _ = try? StorageTestSQLite(url).execute(
                "UPDATE settings SET value = 'soon' WHERE key = 'history_retention_days';")
        }
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

    func testNoReclaimWhileADictationIsActiveEvenWithoutDatabaseTraffic() throws {
        let store = try makeStore()
        try fillAndClear(store)
        let activity = ForegroundActivity()
        let maintenance = makeMaintenance(store, activity: activity)
        clock.advance(by: 61)

        let lease = activity.begin()
        XCTAssertEqual(maintenance.runPass().reclaim, .deferredForActivity)

        // A long dictation: the quiet period passes with no database traffic and no other begin or
        // end, so only the lease itself holds the reclaim off.
        clock.advance(by: 61)
        XCTAssertEqual(maintenance.runPass().reclaim, .deferredForActivity)

        // The dictation's end is activity too, so the quiet period starts again from here.
        lease.end()
        XCTAssertEqual(maintenance.runPass().reclaim, .deferredForActivity)

        clock.advance(by: 61)
        guard case .completed = maintenance.runPass().reclaim else {
            return XCTFail("expected a reclaim once the app had been idle for the quiet period")
        }
    }

    func testAReclaimStopsAtOnceWhenADictationStarts() throws {
        try StorageTestSQLite.createLegacyDatabase(at: directory.databaseURL, historyStartedAt: [fixedNow])
        let store = try makeStore()
        try fillAndClear(store)
        let before = try store.pageStats()
        let activity = ForegroundActivity()
        let startedDictating = StorageTestSwitch()
        let lease = StorageTestBox<ForegroundActivity.Lease>()
        var hooks = StorageMaintenance.Hooks()
        // The first check inside the VACUUM begins a dictation, as a hotkey press would.
        hooks.onReclaimCheck = {
            if startedDictating.claimOnce() {
                lease.set(activity.begin())
            }
        }
        let maintenance = makeMaintenance(store, activity: activity, hooks: hooks)
        clock.advance(by: 61)

        XCTAssertEqual(maintenance.runPass().reclaim, .yielded)

        let after = try store.pageStats()
        XCTAssertEqual(after.autoVacuum, 0)
        XCTAssertEqual(after.freePages, before.freePages)
        XCTAssertTrue(activity.isActive)
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
        var testHooks = PersistenceStore.TestHooks(progressInterval: 1)
        testHooks.onForegroundWait = {
            if armed.isOn {
                waiterRegistered.signal()
            }
        }
        let store = PersistenceStore(databaseURL: directory.databaseURL, testHooks: testHooks)
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

    // MARK: - WAL checkpoint

    func testASmallClearStillEmptiesTheWal() async throws {
        let store = try makeStore()
        try record(store, daysAgo: 1, "one")
        try record(store, daysAgo: 1, "two")
        XCTAssertGreaterThan(walBytes, 0)
        let maintenance = makeMaintenance(store)

        let removed = try await maintenance.clearHistory()

        XCTAssertEqual(removed, 2)
        XCTAssertEqual(walBytes, 0)
    }

    func testABusyCheckpointIsRetriedWithBackoffUntilTheReaderLetsGo() throws {
        let store = try makeStore()
        try store.setHistoryRetention(.days(30))
        try record(store, daysAgo: 45, "old")
        let reader = try StorageTestSQLite(directory.databaseURL)
        try reader.execute("BEGIN; SELECT count(*) FROM dictation_history;")
        let maintenance = makeMaintenance(store) { $0.checkpointRetryDelay = 10 }

        let first = maintenance.runPass()
        XCTAssertEqual(first.retention, .applied(days: 30, removed: 1))
        XCTAssertEqual(first.checkpoint, .busy(retryIn: 10))

        // Nothing left to delete or reclaim, yet the owed checkpoint is still tried, with a longer wait.
        let second = maintenance.runPass()
        XCTAssertEqual(second.retention, .applied(days: 30, removed: 0))
        XCTAssertEqual(second.checkpoint, .busy(retryIn: 20))

        try reader.execute("COMMIT;")
        reader.close()
        XCTAssertEqual(maintenance.runPass().checkpoint, .completed)
        XCTAssertEqual(walBytes, 0)
        XCTAssertEqual(maintenance.runPass().checkpoint, .notNeeded)
    }

    func testNoCheckpointWhileADictationIsActive() throws {
        let store = try makeStore()
        try store.setHistoryRetention(.days(30))
        try record(store, daysAgo: 45, "old")
        let activity = ForegroundActivity()
        let maintenance = makeMaintenance(store, activity: activity)

        let lease = activity.begin()
        XCTAssertEqual(maintenance.runPass().checkpoint, .deferredForActivity)
        lease.end()

        XCTAssertEqual(maintenance.runPass().checkpoint, .completed)
    }

    // MARK: - Clear history

    func testClearHistoryRunsAfterEarlierWritesAndThenReclaims() async throws {
        let store = try makeStore()
        try record(store, daysAgo: 1, "already stored")
        let recorder = StorageTestGatedRecorder(forwardingTo: store)
        recorder.closeGate()
        let clearQueued = StorageTestSignal()
        let writer = HistoryWriter(recorder: recorder, hooks: .init(onClearQueued: { clearQueued.signal() }))
        let maintenance = makeMaintenance(store, writer: writer)

        writer.enqueue(
            DictationHistoryRecord(
                startedAt: fixedNow, durationSeconds: 1, sampleCount: 16_000, transcriptText: "just before the click"))
        XCTAssertTrue(recorder.waitUntilStarted(1))

        let clearing = Task { try await maintenance.clearHistory() }
        XCTAssertTrue(clearQueued.wait())
        recorder.openGate()
        let removed = try await clearing.value

        XCTAssertEqual(removed, 2)
        XCTAssertTrue(writer.waitForAcceptedWrites(timeout: 10))
        XCTAssertEqual(try store.historyCount(), 0)
        XCTAssertEqual(try store.pageStats().freePages, 0)
        XCTAssertEqual(walBytes, 0)
        writer.complete(timeout: 5)
    }

    func testAClearThatTimesOutFailsAndLetsMaintenanceCarryOn() async throws {
        let store = try makeStore()
        let recorder = StorageTestGatedRecorder(forwardingTo: store)
        recorder.closeGate()
        let writer = HistoryWriter(recorder: recorder)
        let maintenance = makeMaintenance(store, writer: writer) { $0.clearTimeout = 0.05 }
        writer.enqueue(
            DictationHistoryRecord(startedAt: fixedNow, durationSeconds: 1, sampleCount: 16_000, transcriptText: "held"))
        XCTAssertTrue(recorder.waitUntilStarted(1))

        do {
            _ = try await maintenance.clearHistory()
            XCTFail("expected the Clear to time out")
        } catch {
            XCTAssertEqual(error as? HistoryWriterError, .clearTimedOut)
        }

        recorder.openGate()
        XCTAssertTrue(writer.waitForAcceptedWrites(timeout: 10))
        // The failed Clear deleted nothing and never runs later, and it leaves the next pass free to do
        // its heavy steps (a Clear that left its stop request behind would hold them off for good).
        XCTAssertEqual(try store.historyCount(), 1)
        XCTAssertEqual(recorder.clearsRun, 0)
        let next = maintenance.runPass()
        XCTAssertNotEqual(next.reclaim, .notRun)
        XCTAssertNotEqual(next.checkpoint, .notRun)
        writer.complete(timeout: 5)
    }

    // MARK: - Schedule

    func testARequestedPassRunsSoonAfterStartAndNoneAfterStop() throws {
        let store = try makeStore()
        let finished = StorageTestSignal()
        var options = StorageMaintenance.Options()
        options.initialDelay = 3_600
        var hooks = StorageMaintenance.Hooks()
        hooks.onPassFinished = { _ in finished.signal() }
        let maintenance = StorageMaintenance(store: store, options: options, hooks: hooks)

        maintenance.start()
        maintenance.requestPass()
        XCTAssertTrue(finished.wait())

        XCTAssertTrue(maintenance.stop(timeout: 5))
        XCTAssertTrue(maintenance.runPass().stopped)
    }
}
