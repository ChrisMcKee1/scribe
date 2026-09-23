import XCTest
@testable import Scribe

final class StartupGateTests: XCTestCase {
    private var directory: StorageTestDirectory!

    override func setUpWithError() throws {
        directory = try StorageTestDirectory()
    }

    override func tearDownWithError() throws {
        directory.remove()
    }

    private static let storedRules = PersistenceRuleSet(
        dictionaryEntries: [DictionaryEntry(id: 1, pattern: "cube flow", replacement: "Kubeflow")],
        snippets: [],
        appProfiles: [])

    /// The refresher startup runs, applying into a real post-processor.
    @MainActor
    private func makeRefresher(
        _ processor: TextPostProcessor,
        load: @escaping @Sendable () async throws -> PersistenceRuleSet
    ) -> RuleSetRefresher {
        RuleSetRefresher(
            load: load,
            apply: { rules in
                processor.reload(dictionaryEntries: rules.dictionaryEntries, snippets: rules.snippets)
            },
            onFailure: { _ in })
    }

    /// Yields until `count` callers wait at `gate`, with a bound so a gate that never parks anyone fails the test
    /// instead of hanging it. Yields, not sleeps: each one lets the waiting tasks on the main actor run.
    @MainActor
    private func waitForWaiters(_ gate: StartupGate, count: Int) async {
        var yields = 0
        while gate.waitingCount < count, yields < 1_000 {
            await Task.yield()
            yields += 1
        }
    }

    // MARK: - Dictation waits for the first rule load

    @MainActor
    func testADictationThatFinishesBeforeTheRulesLoadIsProcessedWithThemNotWithoutThem() async {
        let processor = TextPostProcessor()
        let rulesGate = SettingsTestGate()
        let refresher = makeRefresher(processor) {
            await rulesGate.pass()
            return Self.storedRules
        }
        let gate = StartupGate()

        let startup = Task {
            await gate.open(
                afterMigrating: {},
                then: {},
                loadingRules: { await refresher.refreshUntilSettled() == .applied })
        }
        await rulesGate.waitForArrival()

        // A dictation finishes while the first rule read is still out.
        let dictation = Task {
            await gate.whenOpen { processor.processDetailed("deploy it with cube flow").text }
        }
        await waitForWaiters(gate, count: 1)
        XCTAssertEqual(gate.waitingCount, 1)
        XCTAssertFalse(gate.isOpen)

        await rulesGate.open()
        let state = await startup.value
        let processed = await dictation.value

        XCTAssertEqual(state, .ready)
        XCTAssertEqual(processed, "deploy it with Kubeflow")
    }

    @MainActor
    func testAFailedFirstRuleReadOpensTheGateSoDictationStillRuns() async {
        let processor = TextPostProcessor()
        let refresher = makeRefresher(processor) {
            throw StorageTestFailure(message: "the rules could not be read")
        }
        let gate = StartupGate()

        let state = await gate.open(
            afterMigrating: {},
            then: {},
            loadingRules: { await refresher.refreshUntilSettled() == .applied })
        let processed = await gate.whenOpen { processor.processDetailed("deploy it with cube flow").text }

        XCTAssertEqual(state, .withoutStoredRules)
        XCTAssertEqual(processed, "deploy it with cube flow")
    }

    @MainActor
    func testAFailedMigrationOpensTheGateWithoutStartingMaintenanceOrReadingRules() async {
        let started = SettingsTestCounter()
        let reads = SettingsTestCounter()
        let gate = StartupGate()

        let state = await gate.open(
            afterMigrating: { throw StorageTestFailure(message: "the database could not be opened") },
            then: { started.increment() },
            loadingRules: {
                reads.increment()
                return true
            })

        XCTAssertEqual(state, .withoutStoredRules)
        XCTAssertEqual(started.count, 0)
        XCTAssertEqual(reads.count, 0)
        let later = await gate.wait()
        XCTAssertEqual(later, .withoutStoredRules)
    }

    @MainActor
    func testTheFirstOpeningDecidesTheStateAndWakesEveryWaiter() async {
        let gate = StartupGate()
        let first = Task { await gate.wait() }
        let second = Task { await gate.wait() }
        await waitForWaiters(gate, count: 2)
        XCTAssertEqual(gate.waitingCount, 2)

        gate.open(.ready)
        gate.open(.withoutStoredRules)

        let firstState = await first.value
        let secondState = await second.value
        XCTAssertEqual(firstState, .ready)
        XCTAssertEqual(secondState, .ready)
        XCTAssertEqual(gate.state, .ready)
        XCTAssertEqual(gate.waitingCount, 0)
    }

    /// A rule change made while the first read runs starts a newer refresh; startup's answer then comes from a
    /// refresh that settles, not from the overtaken one.
    @MainActor
    func testStartupIsReadyWhenANewerRefreshOvertookTheFirstRead() async {
        let processor = TextPostProcessor()
        let gates = StorageTestCallGates(count: 3)
        let refresher = makeRefresher(processor) {
            _ = await gates.pass()
            return Self.storedRules
        }
        let gate = StartupGate()

        let startup = Task {
            await gate.open(
                afterMigrating: {},
                then: {},
                loadingRules: { await refresher.refreshUntilSettled() == .applied })
        }
        await gates.gate(0).waitForArrival()
        let edit = Task { await refresher.refresh() }
        await gates.gate(1).waitForArrival()
        await gates.gate(1).open()
        let editOutcome = await edit.value
        XCTAssertEqual(editOutcome, .applied)

        // Opened ahead of time, so the refresh startup starts after being overtaken passes straight through, and a
        // startup that does not start one fails this test instead of hanging it.
        await gates.gate(2).open()
        await gates.gate(0).open()
        let state = await startup.value

        XCTAssertEqual(state, .ready)
    }

    // MARK: - The migration is the first thing on the storage queue

    func testCallsQueuedRightAfterBeginPreparingMeetTheMigratedSchema() async throws {
        let store = PersistenceStore(databaseURL: directory.databaseURL)

        let preparation = store.beginPreparing()
        // Queued before the migration can have finished, and never waiting for it themselves.
        XCTAssertEqual(try store.historyCount(), 0)
        try store.recordDictation(startedAt: Date(), durationSeconds: 1, sampleCount: 16_000, transcriptText: "early")
        let rules = try await store.loadRuleSet()
        try await preparation.finish()

        XCTAssertTrue(rules.dictionaryEntries.isEmpty)
        XCTAssertEqual(try store.historyCount(), 1)
        XCTAssertEqual(try store.historyRetention(), .chosen(.days(HistoryRetention.defaultDays)))
    }

    func testAPreparationThatCannotCreateItsFolderFailsWithThatError() async throws {
        let blocker = directory.url.appendingPathComponent("not-a-folder", isDirectory: false)
        try Data("file".utf8).write(to: blocker)
        let store = PersistenceStore(databaseURL: blocker.appendingPathComponent("scribe.db"))

        do {
            try await store.beginPreparing().finish()
            XCTFail("expected the preparation to fail")
        } catch {
            guard case .directoryUnavailable? = error as? PersistenceError else {
                return XCTFail("expected directoryUnavailable, got \(type(of: error))")
            }
        }
    }
}
