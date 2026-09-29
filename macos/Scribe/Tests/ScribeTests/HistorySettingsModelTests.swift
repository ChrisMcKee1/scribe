import XCTest

@testable import Scribe

final class HistorySettingsModelTests: XCTestCase {
    private var directory: StorageTestDirectory!
    private let fixedNow = Date(timeIntervalSince1970: 1_800_000_000)

    override func setUpWithError() throws {
        directory = try StorageTestDirectory()
    }

    override func tearDownWithError() throws {
        directory.remove()
    }

    private func makeStore() throws -> PersistenceStore {
        let store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        return store
    }

    private func record(_ store: PersistenceStore, _ text: String) throws {
        try store.recordDictation(startedAt: fixedNow, durationSeconds: 1, sampleCount: 16_000, transcriptText: text)
    }

    /// Storage the test fakes: a fixed state, and a Clear the test supplies.
    private static func access(
        state: HistoryStorageState,
        clearHistory: @escaping @Sendable () async throws -> Int = { 0 }
    ) -> HistorySettingsAccess {
        HistorySettingsAccess(
            load: { state },
            setRetention: { _ in },
            clearHistory: clearHistory)
    }

    // MARK: - Retention

    @MainActor
    func testAHistoryThisBuildCreatedShowsNinetyDays() async throws {
        let store = try makeStore()
        let model = HistorySettingsModel(
            access: .live(store: store, maintenance: StorageMaintenance(store: store)), onCleared: {})

        await model.reload()

        XCTAssertEqual(model.state?.retention, .chosen(.days(HistoryRetention.defaultDays)))
        XCTAssertEqual(model.selection, .days(90))
        XCTAssertEqual(model.hint, HistoryRetention.hint)
        XCTAssertEqual(model.options, HistoryRetention.presets)
        XCTAssertTrue(model.canChooseRetention)
    }

    @MainActor
    func testAHistoryFromAnEarlierBuildShowsForeverAndSaysNoLimitWasChosen() async throws {
        try StorageTestSQLite.createLegacyDatabase(at: directory.databaseURL, historyStartedAt: [fixedNow])
        let store = try makeStore()
        let model = HistorySettingsModel(
            access: .live(store: store, maintenance: StorageMaintenance(store: store)), onCleared: {})

        await model.reload()

        XCTAssertEqual(model.state?.retention, .notChosen)
        XCTAssertEqual(model.selection, .keepForever)
        XCTAssertEqual(model.hint, HistoryRetention.notChosenHint)
        XCTAssertEqual(model.storedCountText, "1 dictation is stored.")
    }

    @MainActor
    func testAnUnreadableLimitShowsForeverWithItsOwnHint() async {
        let model = HistorySettingsModel(
            access: Self.access(state: HistoryStorageState(retention: .unreadable, storedCount: 3)), onCleared: {})

        await model.reload()

        XCTAssertEqual(model.selection, .keepForever)
        XCTAssertEqual(model.hint, HistoryRetention.unreadableHint)
    }

    @MainActor
    func testAStoredLimitThatIsNotAPresetIsStillOffered() async {
        let model = HistorySettingsModel(
            access: Self.access(state: HistoryStorageState(retention: .chosen(.days(45)), storedCount: 0)),
            onCleared: {})

        await model.reload()

        XCTAssertEqual(model.selection, .days(45))
        XCTAssertEqual(model.options, [.days(7), .days(30), .days(45), .days(90), .days(365), .keepForever])
    }

    @MainActor
    func testChoosingALimitStoresItThroughMaintenanceAndShowsItAtOnce() async throws {
        let store = try makeStore()
        let model = HistorySettingsModel(
            access: .live(store: store, maintenance: StorageMaintenance(store: store)), onCleared: {})
        await model.reload()

        let saving = model.choose(.days(30))
        XCTAssertNotNil(saving)
        XCTAssertEqual(model.selection, .days(30))
        XCTAssertFalse(model.canChooseRetention)
        await saving?.value

        XCTAssertEqual(try store.historyRetention(), .chosen(.days(30)))
        XCTAssertEqual(model.state?.retention, .chosen(.days(30)))
        XCTAssertNil(model.pendingRetention)
        XCTAssertTrue(model.canChooseRetention)
        XCTAssertNil(model.errorMessage)
    }

    @MainActor
    func testChoosingForeverOnAHistoryWithNoLimitRecordsTheChoice() async throws {
        try StorageTestSQLite.createLegacyDatabase(at: directory.databaseURL, historyStartedAt: [fixedNow])
        let store = try makeStore()
        let model = HistorySettingsModel(
            access: .live(store: store, maintenance: StorageMaintenance(store: store)), onCleared: {})
        await model.reload()

        await model.choose(.keepForever)?.value

        XCTAssertEqual(try store.historyRetention(), .chosen(.keepForever))
        XCTAssertEqual(model.hint, HistoryRetention.hint)
    }

    // MARK: - Clear history

    @MainActor
    func testClearIsUnavailableBeforeTheCountLoadsAndWhileNothingIsStored() async {
        let model = HistorySettingsModel(
            access: Self.access(state: HistoryStorageState(retention: .chosen(.days(90)), storedCount: 0)),
            onCleared: {})
        XCTAssertFalse(model.canClear)

        await model.reload()
        XCTAssertFalse(model.canClear)
        model.requestClear()
        XCTAssertFalse(model.isConfirmingClear)
    }

    @MainActor
    func testAConfirmedClearDeletesTheHistoryAndEmptiesTheRecentDictations() async throws {
        let store = try makeStore()
        try record(store, "first dictation")
        try record(store, "second dictation")
        let writer = HistoryWriter(recorder: store)
        let ring = LastTranscriptStore()
        ring.set("first dictation")
        ring.set("second dictation")
        let model = HistorySettingsModel(
            access: .live(store: store, maintenance: StorageMaintenance(store: store, historyWriter: writer)),
            onCleared: { ring.removeAll() })
        await model.reload()
        XCTAssertTrue(model.canClear)

        model.requestClear()
        XCTAssertTrue(model.isConfirmingClear)
        await model.confirmClear()

        XCTAssertFalse(model.isConfirmingClear)
        XCTAssertEqual(try store.historyCount(), 0)
        XCTAssertTrue(ring.recent().isEmpty)
        XCTAssertEqual(model.state?.storedCount, 0)
        XCTAssertEqual(model.statusMessage, "Cleared 2 dictations.")
        XCTAssertNil(model.errorMessage)
        XCTAssertFalse(model.canClear)
        writer.complete(timeout: 5)
    }

    /// A startup or Quick Add seed that read the old transcripts before the Clear, and finishes after the Clear's
    /// callback emptied the ring, must not put the deleted text back.
    @MainActor
    func testARecoveryReadBegunBeforeASuccessfulClearDoesNotRestoreTheClearedText() async throws {
        let store = try makeStore()
        try record(store, "text the user clears")
        let writer = HistoryWriter(recorder: store)
        let ring = LastTranscriptStore()
        let readGate = SettingsTestGate()
        let model = HistorySettingsModel(
            access: .live(store: store, maintenance: StorageMaintenance(store: store, historyWriter: writer)),
            onCleared: { ring.removeAll() })
        await model.reload()

        let seeding = Task {
            await ring.seed(from: {
                let transcripts = try await store.loadRecentTranscripts(limit: LastTranscriptStore.capacity)
                await readGate.pass()
                return transcripts
            })
        }
        await readGate.waitForArrival()

        model.requestClear()
        await model.confirmClear()
        XCTAssertEqual(model.statusMessage, "Cleared 1 dictation.")
        XCTAssertEqual(try store.historyCount(), 0)

        await readGate.open()
        let seeded = await seeding.value

        XCTAssertFalse(seeded)
        XCTAssertTrue(ring.recent().isEmpty)
        writer.complete(timeout: 5)
    }

    @MainActor
    func testASecondClearWhileOneRunsDoesNothing() async {
        let gate = SettingsTestGate()
        let clears = SettingsTestCounter()
        let model = HistorySettingsModel(
            access: Self.access(
                state: HistoryStorageState(retention: .chosen(.days(90)), storedCount: 4),
                clearHistory: {
                    await clears.increment()
                    await gate.pass()
                    return 4
                }),
            onCleared: {})
        await model.reload()

        let clearing = Task { await model.confirmClear() }
        await gate.waitForArrival()
        XCTAssertTrue(model.isClearing)
        XCTAssertFalse(model.canClear)
        XCTAssertFalse(model.canChooseRetention)
        model.requestClear()
        XCTAssertFalse(model.isConfirmingClear)
        await model.confirmClear()

        await gate.open()
        await clearing.value
        XCTAssertEqual(clears.count, 1)
        XCTAssertFalse(model.isClearing)
    }

    @MainActor
    func testAClearThatTimesOutKeepsTheRecentDictationsAndSaysSo() async {
        let ring = LastTranscriptStore()
        ring.set("still here")
        let model = HistorySettingsModel(
            access: Self.access(
                state: HistoryStorageState(retention: .chosen(.days(90)), storedCount: 1),
                clearHistory: { throw HistoryWriterError.clearTimedOut }),
            onCleared: { ring.removeAll() })
        await model.reload()

        await model.confirmClear()

        XCTAssertEqual(model.errorMessage, HistoryWriterError.clearTimedOut.errorDescription)
        XCTAssertEqual(ring.recent(), ["still here"])
        XCTAssertNil(model.statusMessage)
    }

    /// Only the store's and the history writer's errors, which describe shapes, are shown as they are; anything
    /// else, which might quote a path or content, becomes a fixed sentence.
    @MainActor
    func testClearFailuresNeverShowAnUnknownErrorsOwnText() {
        let revealing = NSError(
            domain: "test", code: 1, userInfo: [NSLocalizedDescriptionKey: "/Users/someone/private/file.db"])
        let unknownMessage = HistorySettingsModel.clearFailureMessage(revealing)
        XCTAssertEqual(unknownMessage, "History was not cleared. Try again in a moment.")
        XCTAssertFalse(unknownMessage.contains("/Users"))

        let storeError = PersistenceError.sqlite(operation: .write, code: 5)
        XCTAssertEqual(
            HistorySettingsModel.clearFailureMessage(storeError),
            "History was not cleared. \(storeError.errorDescription ?? "")")
    }
}
