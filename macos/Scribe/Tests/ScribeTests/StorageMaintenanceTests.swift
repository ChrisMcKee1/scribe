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

    // MARK: - Rules, snippets and profiles

    /// Whether `text` is in the database file, read while the store's connection is still open: closing the last
    /// connection would checkpoint on its own, and what is tested is whether maintenance did.
    private func databaseFileContains(_ text: String) throws -> Bool {
        try Data(contentsOf: directory.databaseURL).range(of: Data(text.utf8)) != nil
    }

    /// What counts as removed text outside history: a dictionary entry, snippet or app profile deleted, or an entry
    /// rewritten, by an edit or an import. Adding a row, or switching one on or off, removes nothing.
    func testTheStoreCountsTheWritesThatRemoveTextOutsideHistory() async throws {
        let store = try makeStore()
        let id = try await store.addDictionaryEntry(DictionaryEntry(pattern: "alpha", replacement: "Alpha"))
        let snippetID = try await store.addSnippet(Snippet(phrase: "my block", template: "Block"))
        let profile = AppProfile(name: "Editor", bundleIdentifiers: [], processNames: [])
        let profileID = try await store.addAppProfile(profile)
        try await store.saveDictionaryEntryEnabled(id: id, enabled: false)
        try await store.saveSnippetEnabled(id: snippetID, enabled: false)
        _ = try await store.addDictionaryEntriesIfAbsent([DictionaryEntry(pattern: "beta", replacement: "Beta")])
        _ = try await store.importDictionary([DictionaryEntry(pattern: "gamma", replacement: "Gamma")])
        XCTAssertEqual(store.removedTextCount, 0, "adding or switching a row counted as removing text")

        try await store.saveDictionaryEntry(DictionaryEntry(id: id, pattern: "alpha", replacement: "ALPHA"))
        XCTAssertEqual(store.removedTextCount, 1)
        _ = try await store.importDictionary([DictionaryEntry(pattern: "alpha", replacement: "Alpha again")])
        XCTAssertEqual(store.removedTextCount, 2)
        try await store.removeDictionaryEntry(id: id)
        try await store.removeSnippet(id: snippetID)
        try await store.removeAppProfile(id: profileID)
        XCTAssertEqual(store.removedTextCount, 5)
    }

    /// Deleting or rewriting a dictionary entry, a snippet or an app profile owes a WAL checkpoint, as a history
    /// deletion does: until one runs, the database file keeps the old page, text and all. Each write asks for a pass
    /// that only checkpoints, and once it has run the text is in neither the database file nor the WAL.
    func testDeletingOrRewritingARuleSnippetOrProfileTakesItsTextOutOfTheFiles() async throws {
        let store = try makeStore()
        let deleted = "entry-\(UUID().uuidString)"
        let edited = "edited-\(UUID().uuidString)"
        let imported = "imported-\(UUID().uuidString)"
        let template = "snippet-\(UUID().uuidString)"
        let style = "profile-\(UUID().uuidString)"
        let deletedID = try store.insertDictionaryEntry(DictionaryEntry(pattern: "alpha", replacement: deleted))
        let editedID = try store.insertDictionaryEntry(DictionaryEntry(pattern: "beta", replacement: edited))
        _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "gamma", replacement: imported))
        let snippetID = try store.insertSnippet(Snippet(phrase: "my block", template: template))
        let profile = AppProfile(
            name: "Editor", bundleIdentifiers: ["com.example.editor"], processNames: [], writingStylePrompt: style)
        let profileID = try store.insertAppProfile(profile)
        XCTAssertTrue(try store.checkpointWal())
        for text in [deleted, edited, imported, template, style] {
            XCTAssertTrue(try databaseFileContains(text), "the search missed text that is stored")
        }
        let passes = PassReports()
        var hooks = StorageMaintenance.Hooks()
        hooks.onPassFinished = { passes.record($0) }
        let maintenance = makeMaintenance(store, hooks: hooks) { $0.initialDelay = 3_600 }
        maintenance.start()

        let rewritten = DictionaryEntry(id: editedID, pattern: "beta", replacement: "B")
        let writes: [(text: String, write: () async throws -> Void)] = [
            (deleted, { try await store.removeDictionaryEntry(id: deletedID) }),
            (edited, { try await store.saveDictionaryEntry(rewritten) }),
            (imported, { _ = try await store.importDictionary([DictionaryEntry(pattern: "gamma", replacement: "G")]) }),
            (template, { try await store.removeSnippet(id: snippetID) }),
            (style, { try await store.removeAppProfile(id: profileID) }),
        ]
        for (text, write) in writes {
            try await write()
            let report = passes.next()
            XCTAssertEqual(report?.checkpoint, .completed, "no checkpoint followed the write")
            XCTAssertEqual(report?.retention, .notRun, "the write ran a whole pass")
            XCTAssertEqual(walBytes, 0)
            XCTAssertFalse(try databaseFileContains(text), "the old text is still in the database file")
        }
        XCTAssertTrue(maintenance.stop(timeout: 5))
    }

    /// The checkpoint a rule's deletion owes waits while a dictation holds a lease, and runs on a retry once it ends.
    /// Every retry is a pass that only checkpoints, so a dictation during an edit never runs the retention sweep or
    /// reclamation.
    func testTheCheckpointARuleDeletionOwesWaitsForADictation() async throws {
        let store = try makeStore()
        let text = "entry-\(UUID().uuidString)"
        let id = try store.insertDictionaryEntry(DictionaryEntry(pattern: "alpha", replacement: text))
        XCTAssertTrue(try store.checkpointWal())
        let activity = ForegroundActivity()
        let passes = PassReports()
        var hooks = StorageMaintenance.Hooks()
        hooks.onPassFinished = { passes.record($0) }
        let maintenance = makeMaintenance(store, activity: activity, hooks: hooks) {
            $0.initialDelay = 3_600
            $0.checkpointRetryDelay = 0.1
        }
        maintenance.start()

        let lease = activity.begin()
        try await store.removeDictionaryEntry(id: id)
        let first = passes.next()
        XCTAssertEqual(first?.checkpoint, .deferredForActivity)
        XCTAssertTrue(try databaseFileContains(text), "the checkpoint ran during the dictation")
        lease.end()

        // Retries that fell before the lease ended were deferred again; the first after it completes.
        var reports = [first]
        repeat {
            reports.append(passes.next())
        } while reports.last??.checkpoint == .deferredForActivity
        XCTAssertEqual(reports.last??.checkpoint, .completed)
        XCTAssertFalse(try databaseFileContains(text), "the old text is still in the database file")
        for report in reports {
            XCTAssertEqual(report?.retention, .notRun, "a retry ran the retention sweep")
            XCTAssertEqual(report?.reclaim, .notRun, "a retry ran reclamation")
        }
        XCTAssertTrue(maintenance.stop(timeout: 5))
    }

    /// A checkpoint that has to wait is retried by passes that only checkpoint, whichever pass found it owed. A rule
    /// deleted before maintenance started leaves one owed; a whole pass finds it while a dictation holds a lease, and
    /// every pass after it, until the checkpoint completes once the dictation ends, ran neither the retention sweep
    /// nor reclamation. So an edit whose checkpoint a whole pass picks up never adds a sweep either.
    func testAWholePassWhoseCheckpointMustWaitIsRetriedByPassesThatOnlyCheckpoint() async throws {
        let store = try makeStore()
        let text = "entry-\(UUID().uuidString)"
        let id = try store.insertDictionaryEntry(DictionaryEntry(pattern: "alpha", replacement: text))
        XCTAssertTrue(try store.checkpointWal())
        try await store.removeDictionaryEntry(id: id)
        let activity = ForegroundActivity()
        let passes = PassReports()
        var hooks = StorageMaintenance.Hooks()
        hooks.onPassFinished = { passes.record($0) }
        let maintenance = makeMaintenance(store, activity: activity, hooks: hooks) {
            $0.initialDelay = 3_600
            $0.quietPeriod = 3_600
            $0.checkpointRetryDelay = 0.1
        }
        maintenance.start()

        let lease = activity.begin()
        maintenance.requestPass()
        let whole = passes.next()
        XCTAssertNotEqual(whole?.retention, .notRun, "the requested pass was not a whole pass")
        XCTAssertEqual(whole?.checkpoint, .deferredForActivity)
        let retry = passes.next()
        XCTAssertEqual(retry?.checkpoint, .deferredForActivity)
        lease.end()

        var reports = [retry]
        while reports.last??.checkpoint == .deferredForActivity {
            reports.append(passes.next())
        }
        XCTAssertEqual(reports.last??.checkpoint, .completed)
        XCTAssertFalse(try databaseFileContains(text), "the old text is still in the database file")
        for report in reports {
            XCTAssertEqual(report?.retention, .notRun, "a retry ran the retention sweep")
            XCTAssertEqual(report?.reclaim, .notRun, "a retry ran reclamation")
        }
        XCTAssertTrue(maintenance.stop(timeout: 5))
    }

    /// A checkpoint the last session owed is not forgotten across a quit or a crash. A snippet is deleted while a
    /// dictation holds its checkpoint off, and the app is gone before the retry; the app never closes its connection,
    /// so nothing checkpoints on the way out. The next launch's first pass empties the log the last session left, and
    /// the old text leaves the database file.
    func testTheFirstPassOfALaunchEmptiesTheLogTheLastSessionLeft() async throws {
        let store = try makeStore()
        let template = "snippet-\(UUID().uuidString)"
        let snippetID = try store.insertSnippet(Snippet(phrase: "my block", template: template))
        XCTAssertTrue(try store.checkpointWal())
        let activity = ForegroundActivity()
        let passes = PassReports()
        var hooks = StorageMaintenance.Hooks()
        hooks.onPassFinished = { passes.record($0) }
        let lastSession = makeMaintenance(store, activity: activity, hooks: hooks) {
            $0.initialDelay = 3_600
            $0.checkpointRetryDelay = 3_600
        }
        lastSession.start()
        let lease = activity.begin()
        try await store.removeSnippet(id: snippetID)
        XCTAssertEqual(passes.next()?.checkpoint, .deferredForActivity)
        // The quit: maintenance stops before its retry, and the connection stays open, as the app's always does.
        XCTAssertTrue(lastSession.stop(timeout: 5))
        lease.end()
        XCTAssertTrue(try databaseFileContains(template), "the old text left the database file before the relaunch")

        // The relaunch: a store and maintenance of its own on the same file, with nothing removed this session.
        let relaunched = PersistenceStore(databaseURL: directory.databaseURL)
        try relaunched.initialize()
        let firstPass = makeMaintenance(relaunched).runPass()

        XCTAssertEqual(firstPass.checkpoint, .completed)
        XCTAssertEqual(walBytes, 0)
        XCTAssertFalse(try databaseFileContains(template), "the old text is still in the database file")
    }

    /// Starts a Clear history that will fail and returns once maintenance stands aside for it: one history write is
    /// held at the recorder's gate, the Clear is queued behind it, and opening the gate lets the write through and
    /// makes the Clear's own step throw. No pass is scheduled on a timer.
    private func startAClearThatWillFail(
        _ store: PersistenceStore, hooks: StorageMaintenance.Hooks
    ) -> (
        maintenance: StorageMaintenance, recorder: StorageTestGatedRecorder, writer: HistoryWriter,
        clearing: Task<Int, Error>
    ) {
        let recorder = StorageTestGatedRecorder(forwardingTo: store)
        recorder.closeGate()
        recorder.failWrite(number: 2)
        let clearQueued = StorageTestSignal()
        let writer = HistoryWriter(recorder: recorder, hooks: .init(onClearQueued: { clearQueued.signal() }))
        let maintenance = makeMaintenance(store, writer: writer, hooks: hooks) {
            $0.initialDelay = 3_600
            $0.clearTimeout = 60
        }
        maintenance.start()
        writer.enqueue(
            DictationHistoryRecord(
                startedAt: fixedNow, durationSeconds: 1, sampleCount: 16_000, transcriptText: "held"))
        XCTAssertTrue(recorder.waitUntilStarted(1))
        let clearing = Task { try await maintenance.clearHistory() }
        XCTAssertTrue(clearQueued.wait())
        return (maintenance, recorder, writer, clearing)
    }

    /// Lets the held write through, so the Clear's step runs and fails, and waits until the Clear has reported it.
    private func failTheClear(_ recorder: StorageTestGatedRecorder, _ clearing: Task<Int, Error>) async {
        recorder.openGate()
        do {
            _ = try await clearing.value
            XCTFail("expected the Clear to fail")
        } catch {
            XCTAssertTrue(
                error is StorageTestGatedRecorder.InjectedFailure, "the Clear failed with \(type(of: error))")
        }
    }

    /// A9: a rule's deletion that commits while Clear history runs asks for its checkpoint, and that pass stands aside
    /// for the Clear. When the Clear fails and lets go, the pass is asked for again and completes on its own: the test
    /// runs no pass, makes no other write and keeps the connection open.
    func testARemovalsCheckpointThatStoodAsideForAFailedClearStillRuns() async throws {
        let store = try makeStore()
        let text = "entry-\(UUID().uuidString)"
        let id = try store.insertDictionaryEntry(DictionaryEntry(pattern: "alpha", replacement: text))
        XCTAssertTrue(try store.checkpointWal())
        let passes = PassReports()
        var hooks = StorageMaintenance.Hooks()
        hooks.onPassFinished = { passes.record($0) }
        let running = startAClearThatWillFail(store, hooks: hooks)

        try await store.removeDictionaryEntry(id: id)
        XCTAssertEqual(passes.next()?.checkpoint, .notRun, "the removal's pass did not stand aside for the Clear")
        XCTAssertTrue(try databaseFileContains(text), "the search missed text that is stored")

        await failTheClear(running.recorder, running.clearing)

        XCTAssertEqual(passes.next()?.checkpoint, .completed, "nothing checkpointed after the Clear failed")
        XCTAssertEqual(walBytes, 0)
        XCTAssertFalse(try databaseFileContains(text), "the old text is still in the database file")
        XCTAssertTrue(running.writer.waitForAcceptedWrites(timeout: 10))
        running.writer.complete(timeout: 5)
        XCTAssertTrue(running.maintenance.stop(timeout: 5))
    }

    /// The same for a whole pass: one asked for while a Clear runs stands aside, its retention sweep removing nothing,
    /// and when the Clear fails it runs again, so an entry past the limit is still removed.
    func testAPassThatStoodAsideForAFailedClearRunsAgain() async throws {
        let store = try makeStore()
        try store.setHistoryRetention(.days(30))
        try record(store, daysAgo: 45, "old")
        let passes = PassReports()
        var hooks = StorageMaintenance.Hooks()
        hooks.onPassFinished = { passes.record($0) }
        let running = startAClearThatWillFail(store, hooks: hooks)

        running.maintenance.requestPass()
        let stoodAside = passes.next()
        XCTAssertEqual(stoodAside?.retention, .applied(days: 30, removed: 0))
        XCTAssertEqual(stoodAside?.checkpoint, .notRun)

        await failTheClear(running.recorder, running.clearing)

        XCTAssertEqual(passes.next()?.retention, .applied(days: 30, removed: 1), "the pass did not run again")
        XCTAssertTrue(running.writer.waitForAcceptedWrites(timeout: 10))
        XCTAssertEqual(try store.fetchDictationHistory().compactMap(\.transcriptText), ["held"])
        running.writer.complete(timeout: 5)
        XCTAssertTrue(running.maintenance.stop(timeout: 5))
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
            DictationHistoryRecord(startedAt: fixedNow, durationSeconds: 1, sampleCount: 16_000, transcriptText: "held")
        )
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

/// Every report maintenance finishes, handed to the test in order, each waited for with a bound.
private final class PassReports: @unchecked Sendable {
    // `@unchecked Sendable`: `reports` and `taken` are only read or written while `lock` is held.
    private let lock = NSLock()
    private var reports: [StorageMaintenanceReport] = []
    private var taken = 0
    private let arrived = DispatchSemaphore(value: 0)

    func record(_ report: StorageMaintenanceReport) {
        lock.lock()
        reports.append(report)
        lock.unlock()
        arrived.signal()
    }

    /// The next pass's report, or nil when none finishes within `timeout` seconds.
    func next(timeout: TimeInterval = 10) -> StorageMaintenanceReport? {
        guard arrived.wait(timeout: .now() + timeout) == .success else {
            return nil
        }
        lock.lock()
        defer { lock.unlock() }
        let report = reports[taken]
        taken += 1
        return report
    }
}
