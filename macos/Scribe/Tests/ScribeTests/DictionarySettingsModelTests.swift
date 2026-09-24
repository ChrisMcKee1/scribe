import XCTest

@testable import Scribe

final class DictionarySettingsModelTests: XCTestCase {
    private var directory: StorageTestDirectory!

    override func setUpWithError() throws {
        directory = try StorageTestDirectory()
    }

    override func tearDownWithError() throws {
        directory.remove()
    }

    private func makeStore(_ control: StorageTestQueueControl? = nil) throws -> PersistenceStore {
        let store = PersistenceStore(
            databaseURL: directory.databaseURL, testHooks: control?.hooks ?? PersistenceStore.TestHooks())
        try store.initialize()
        return store
    }

    /// Storage the test controls through `loadEntries`; every other call fails the action that makes it.
    private static func access(
        loadEntries: @escaping @Sendable () async throws -> [DictionaryEntry]
    ) -> DictionarySettingsAccess {
        DictionarySettingsAccess(
            loadEntries: loadEntries,
            addEntry: { _ in throw StorageTestFailure(message: "unexpected add") },
            setEnabled: { _, _ in throw StorageTestFailure(message: "unexpected switch") },
            deleteEntry: { _ in throw StorageTestFailure(message: "unexpected delete") },
            importEntries: { _ in throw StorageTestFailure(message: "unexpected import") },
            learnFromHistory: { throw StorageTestFailure(message: "unexpected learn") },
            reviewUsage: { throw StorageTestFailure(message: "unexpected review") },
            disableEntries: { _ in throw StorageTestFailure(message: "unexpected disable") })
    }

    private static func rule(_ id: Int64, _ pattern: String) -> DictionaryEntry {
        DictionaryEntry(id: id, pattern: pattern, replacement: pattern.uppercased())
    }

    /// Storage with no rules whose adds go to `addEntry`; every other write fails the action that makes it.
    private static func access(
        addEntry: @escaping @Sendable (DictionaryEntry) async throws -> Void
    ) -> DictionarySettingsAccess {
        var access = Self.access(loadEntries: { [] })
        access.addEntry = addEntry
        return access
    }

    // MARK: - An add that outlives its model

    /// Leaving the section and coming back builds the tab's model again while the add it started is still waiting in
    /// storage, and the drafts still hold the rule. The new model sees that add in the drafts, so the rule is sent
    /// once. A second add, if one were sent, has its own gate already open, so it could never hang the test.
    @MainActor
    func testATabRebuiltDuringAnAddSendsTheRuleOnce() async {
        let gates = StorageTestCallGates(count: 2)
        let inserts = SettingsTestCounter()
        let access = Self.access(addEntry: { _ in
            await inserts.increment()
            _ = await gates.pass()
        })
        let drafts = SettingsDrafts()
        drafts.dictionaryPattern = "kay eight ess"
        drafts.dictionaryReplacement = "K8s"
        let first = DictionarySettingsModel(access: access, drafts: drafts, onChanged: {})
        let adding = Task { await first.addFromDrafts() }
        await gates.gate(0).waitForArrival()

        let rebuilt = DictionarySettingsModel(access: access, drafts: drafts, onChanged: {})
        XCTAssertTrue(rebuilt.isAdding)
        XCTAssertFalse(rebuilt.canAdd)
        await gates.gate(1).open()
        await rebuilt.addFromDrafts()
        await gates.gate(0).open()
        await adding.value

        XCTAssertEqual(inserts.count, 1)
        XCTAssertEqual(drafts.dictionaryPattern, "")
        XCTAssertFalse(rebuilt.isAdding)
        drafts.dictionaryPattern = "next rule"
        drafts.dictionaryReplacement = "Next rule"
        XCTAssertTrue(rebuilt.canAdd)
    }

    // MARK: - Reads that finish out of order

    @MainActor
    func testAnOlderReloadThatFinishesLastNeverReplacesTheNewerRows() async {
        let gates = StorageTestCallGates(count: 2)
        let model = DictionarySettingsModel(
            access: Self.access(loadEntries: {
                let call = await gates.pass()
                return [Self.rule(Int64(call + 1), call == 0 ? "older" : "newer")]
            }),
            drafts: SettingsDrafts(),
            onChanged: {})

        let older = Task { await model.reload() }
        await gates.gate(0).waitForArrival()
        let newer = Task { await model.reload() }
        await gates.gate(1).waitForArrival()

        await gates.gate(1).open()
        await newer.value
        await gates.gate(0).open()
        await older.value

        XCTAssertEqual(model.entries.map(\.pattern), ["newer"])
        XCTAssertTrue(model.load.isLoaded)
        XCTAssertNil(model.loadError)
    }

    @MainActor
    func testAnOlderReloadThatFailsLastNeverShowsItsError() async {
        let gates = StorageTestCallGates(count: 2)
        let model = DictionarySettingsModel(
            access: Self.access(loadEntries: {
                let call = await gates.pass()
                if call == 0 {
                    throw StorageTestFailure(message: "the older read failed")
                }
                return [Self.rule(1, "newer")]
            }),
            drafts: SettingsDrafts(),
            onChanged: {})

        let older = Task { await model.reload() }
        await gates.gate(0).waitForArrival()
        let newer = Task { await model.reload() }
        await gates.gate(1).waitForArrival()

        await gates.gate(1).open()
        await newer.value
        await gates.gate(0).open()
        await older.value

        XCTAssertNil(model.loadError)
        XCTAssertEqual(model.entries.map(\.pattern), ["newer"])
        XCTAssertTrue(model.load.isLoaded)
    }

    @MainActor
    func testANewerReloadThatFailsStaysShownWhenAnOlderOneSucceedsLater() async {
        let gates = StorageTestCallGates(count: 2)
        let model = DictionarySettingsModel(
            access: Self.access(loadEntries: {
                let call = await gates.pass()
                if call == 1 {
                    throw StorageTestFailure(message: "the newer read failed")
                }
                return [Self.rule(1, "older")]
            }),
            drafts: SettingsDrafts(),
            onChanged: {})

        let older = Task { await model.reload() }
        await gates.gate(0).waitForArrival()
        let newer = Task { await model.reload() }
        await gates.gate(1).waitForArrival()

        await gates.gate(1).open()
        await newer.value
        await gates.gate(0).open()
        await older.value

        XCTAssertEqual(model.loadError, "the newer read failed")
        XCTAssertTrue(model.entries.isEmpty)
        XCTAssertEqual(model.load.state, .failed)
    }

    // MARK: - A read never clears an action's failure

    @MainActor
    func testAFailedAddStaysShownWhenAnOlderReloadFinishesAfterIt() async {
        let gate = SettingsTestGate()
        let drafts = SettingsDrafts()
        var access = Self.access(loadEntries: {
            await gate.pass()
            return [Self.rule(1, "stored")]
        })
        access.addEntry = { _ in throw StorageTestFailure(message: "the rule was not saved") }
        let model = DictionarySettingsModel(access: access, drafts: drafts, onChanged: {})

        let loading = Task { await model.reload() }
        await gate.waitForArrival()
        drafts.dictionaryPattern = "kay eight ess"
        drafts.dictionaryReplacement = "K8s"
        await model.addFromDrafts()
        XCTAssertEqual(model.errorMessage, "the rule was not saved")

        await gate.open()
        await loading.value

        XCTAssertEqual(model.entries.map(\.pattern), ["stored"])
        XCTAssertEqual(model.errorMessage, "the rule was not saved")
        XCTAssertNil(model.loadError)
        XCTAssertEqual(drafts.dictionaryPattern, "kay eight ess")
    }

    // MARK: - An import plans against what is stored, never against the rows on screen

    @MainActor
    func testAnImportWhileTheFirstLoadIsStillWaitingUpdatesTheStoredRuleInsteadOfAddingADuplicate() async throws {
        let control = StorageTestQueueControl()
        let store = try makeStore(control)
        _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "sherpa onnx", replacement: "sherpa-onnx"))
        let model = DictionarySettingsModel(access: .live(store), drafts: SettingsDrafts(), onChanged: {})

        control.holdOperation()
        let write = StorageTestBackground.recordDictation(on: store)
        await control.holding.wait()

        let loadQueued = control.nextForegroundCaller()
        let loading = Task { await model.reload() }
        await loadQueued.wait()
        let importQueued = control.nextForegroundCaller()
        let importing = Task { await model.importCsv("pattern,replacement\nsherpa onnx,Sherpa ONNX\n") }
        await importQueued.wait()

        // Import ran while the tab still showed no rows at all.
        XCTAssertFalse(model.load.isLoaded)
        XCTAssertTrue(model.entries.isEmpty)

        control.release.signal()
        await loading.value
        await importing.value
        XCTAssertTrue(write.wait())

        let stored = try store.fetchAllDictionaryEntries()
        XCTAssertEqual(stored.map(\.pattern), ["sherpa onnx"])
        XCTAssertEqual(stored.map(\.replacement), ["Sherpa ONNX"])
        XCTAssertEqual(model.entries, stored)
        XCTAssertEqual(model.statusMessage, "Imported: 0 added, 1 updated, 0 unchanged.")
    }

    @MainActor
    func testAnImportWhileAnEarlierImportIsStillRefreshingSeesTheRuleThatImportAdded() async throws {
        let control = StorageTestQueueControl()
        let store = try makeStore(control)
        let model = DictionarySettingsModel(access: .live(store), drafts: SettingsDrafts(), onChanged: {})
        await model.reload()
        XCTAssertTrue(model.load.isLoaded)

        // The first import's transaction is the next storage operation; the re-read it then starts is held.
        control.holdOperation(afterSkipping: 1)
        let first = Task { await model.importCsv("pattern,replacement\nalpha,Alpha\n") }
        await control.holding.wait()
        XCTAssertFalse(model.isImporting)
        XCTAssertTrue(model.entries.isEmpty)

        let secondQueued = control.nextForegroundCaller()
        let second = Task { await model.importCsv("pattern,replacement\nalpha,ALPHA\n") }
        await secondQueued.wait()

        control.release.signal()
        await first.value
        await second.value

        let stored = try store.fetchAllDictionaryEntries()
        XCTAssertEqual(stored.map(\.pattern), ["alpha"])
        XCTAssertEqual(stored.map(\.replacement), ["ALPHA"])
        XCTAssertEqual(model.entries, stored)
        XCTAssertEqual(model.statusMessage, "Imported: 0 added, 1 updated, 0 unchanged.")
    }

    // MARK: - A whole action while a write holds the storage queue

    @MainActor
    func testAddingARuleWhileAWriteHoldsTheStorageQueueLeavesTheMainActorFree() async throws {
        let control = StorageTestQueueControl()
        let store = try makeStore(control)
        let drafts = SettingsDrafts()
        let refreshes = SettingsTestCounter()
        let model = DictionarySettingsModel(access: .live(store), drafts: drafts, onChanged: { refreshes.increment() })
        await model.reload()

        control.holdOperation()
        let write = StorageTestBackground.recordDictation(on: store)
        await control.holding.wait()

        drafts.dictionaryPattern = "kay eight ess"
        drafts.dictionaryReplacement = "K8s"
        let addQueued = control.nextForegroundCaller()
        let adding = Task { await model.addFromDrafts() }
        await addQueued.wait()

        // The add waits behind the held write while the main actor keeps running other work.
        XCTAssertTrue(model.isAdding)
        XCTAssertFalse(model.canAdd)
        let otherWork = Task { @MainActor in 42 }
        let otherResult = await otherWork.value
        XCTAssertEqual(otherResult, 42)
        XCTAssertTrue(model.entries.isEmpty)
        XCTAssertEqual(refreshes.count, 0)

        control.release.signal()
        await adding.value
        XCTAssertTrue(write.wait())

        XCTAssertEqual(model.entries.map(\.pattern), ["kay eight ess"])
        XCTAssertEqual(try store.fetchAllDictionaryEntries().map(\.replacement), ["K8s"])
        XCTAssertEqual(refreshes.count, 1)
        XCTAssertEqual(drafts.dictionaryPattern, "")
        XCTAssertEqual(drafts.dictionaryReplacement, "")
        XCTAssertFalse(model.isAdding)
    }

    // MARK: - Learning and Clean Up decide against what is stored

    @MainActor
    func testLearningFromATabThatNeverLoadedNeverAddsARuleTwice() async throws {
        let store = try makeStore()
        let history = [
            "the ATU owns it",
            "ask the ATU",
            "ATU signed off",
        ]
        for text in history {
            try store.recordDictation(startedAt: Date(), durationSeconds: 1, sampleCount: 16_000, transcriptText: text)
        }

        let first = DictionarySettingsModel(access: .live(store), drafts: SettingsDrafts(), onChanged: {})
        await first.learnFromHistory()
        let learned = try store.fetchAllDictionaryEntries()
        XCTAssertFalse(learned.isEmpty)
        XCTAssertEqual(first.entries, learned)

        // A second tab that never loaded learns again: the store already covers every term.
        let second = DictionarySettingsModel(access: .live(store), drafts: SettingsDrafts(), onChanged: {})
        await second.learnFromHistory()
        XCTAssertEqual(try store.fetchAllDictionaryEntries(), learned)
        XCTAssertEqual(second.statusMessage, "No new recurring terms found in your dictation history yet.")
    }

    @MainActor
    func testApplyingCleanUpTurnsOffExactlyTheChosenRules() async throws {
        let store = try makeStore()
        let keep = try store.insertDictionaryEntry(DictionaryEntry(pattern: "keep", replacement: "Keep"))
        let dropOne = try store.insertDictionaryEntry(DictionaryEntry(pattern: "drop one", replacement: "Drop one"))
        let dropTwo = try store.insertDictionaryEntry(DictionaryEntry(pattern: "drop two", replacement: "Drop two"))
        let refreshes = SettingsTestCounter()
        let model = DictionarySettingsModel(
            access: .live(store), drafts: SettingsDrafts(), onChanged: { refreshes.increment() })

        await model.applyCleanup(disabling: [dropOne, dropTwo])

        let stored = try store.fetchAllDictionaryEntries()
        XCTAssertEqual(stored.map(\.id), [keep, dropOne, dropTwo])
        XCTAssertEqual(stored.map(\.enabled), [true, false, false])
        XCTAssertEqual(model.statusMessage, "Turned off 2 unused entries.")
        XCTAssertEqual(refreshes.count, 1)
        XCTAssertNil(model.cleanupReport)
    }
}
