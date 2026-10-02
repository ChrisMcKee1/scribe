import XCTest

@testable import Scribe

private actor HistoryReadGate {
    private var reads: [String: [CheckedContinuation<[StoredDictation], any Error>]] = [:]
    private var counts: [String: Int] = [:]
    private var waiters: [(String, Int, CheckedContinuation<Void, Never>)] = []

    func read(_ query: String) async throws -> [StoredDictation] {
        try await withCheckedThrowingContinuation { continuation in
            reads[query, default: []].append(continuation)
            counts[query, default: 0] += 1
            let ready = waiters.filter { counts[$0.0, default: 0] >= $0.1 }
            waiters.removeAll { counts[$0.0, default: 0] >= $0.1 }
            for waiter in ready { waiter.2.resume() }
        }
    }

    func waitFor(_ query: String, count: Int = 1) async {
        guard counts[query, default: 0] < count else { return }
        await withCheckedContinuation { waiters.append((query, count, $0)) }
    }

    func finish(_ query: String, rows: [StoredDictation]) {
        reads[query]?.removeFirst().resume(returning: rows)
    }
}

@MainActor
final class HistoryListModelTests: XCTestCase {
    private func row(_ id: Int64, text: String = "test") -> StoredDictation {
        StoredDictation(
            id: id,
            record: DictationHistoryRecord(
                startedAt: Date(timeIntervalSince1970: Double(id)), durationSeconds: 1,
                sampleCount: 16_000, transcriptText: text))
    }

    func testNewerQueryWinsEvenWhenCanceledReadReturnsLater() async {
        let gate = HistoryReadGate()
        let model = HistoryListModel(
            access: HistoryListAccess(read: { try await gate.read($0) }, delete: { _ in }), delay: {})
        model.appear()
        await gate.waitFor("")
        let obsolete = model.inFlight
        model.query = "new"
        await gate.waitFor("new")
        await gate.finish("new", rows: [row(2)])
        await model.inFlight?.value
        await gate.finish("", rows: [row(1)])
        await obsolete?.value
        XCTAssertEqual(model.rows.map(\.id), [2])
        XCTAssertFalse(model.isLoading)
    }

    func testClearingQueryRestoresRecentAndCannotPublishStaleSearch() async {
        let gate = HistoryReadGate()
        let model = HistoryListModel(
            access: HistoryListAccess(read: { try await gate.read($0) }, delete: { _ in }), delay: {})
        model.appear()
        await gate.waitFor("")
        await gate.finish("", rows: [row(3)])
        await model.inFlight?.value
        model.query = "old"
        await gate.waitFor("old")
        let staleSearch = model.inFlight
        model.query = ""
        await gate.waitFor("", count: 2)
        await gate.finish("", rows: [row(4)])
        await model.inFlight?.value
        await gate.finish("old", rows: [row(1)])
        await staleSearch?.value
        XCTAssertEqual(model.rows.map(\.id), [4])
    }

    func testDebounceInvalidatesEarlierQueriesBeforeAnyRead() async {
        let gate = HistoryReadGate()
        let debounceGate = HistoryReadGate()
        let model = HistoryListModel(
            access: HistoryListAccess(read: { try await gate.read($0) }, delete: { _ in }),
            delay: { _ = try await debounceGate.read("delay") })
        model.appear()
        await gate.waitFor("")
        await gate.finish("", rows: [])
        await model.inFlight?.value
        model.query = "first"
        await debounceGate.waitFor("delay")
        let canceled = model.inFlight
        model.query = "second"
        await debounceGate.waitFor("delay", count: 2)
        await debounceGate.finish("delay", rows: [])
        await canceled?.value
        await debounceGate.finish("delay", rows: [])
        await gate.waitFor("second")
        await gate.finish("second", rows: [row(5)])
        await model.inFlight?.value
        XCTAssertEqual(model.rows.map(\.id), [5])
    }

    func testLeavingHistoryDropsLateResults() async {
        let gate = HistoryReadGate()
        let model = HistoryListModel(access: HistoryListAccess(read: { try await gate.read($0) }, delete: { _ in }))
        model.appear()
        await gate.waitFor("")
        let pending = model.inFlight
        model.stop()
        await gate.finish("", rows: [row(1)])
        await pending?.value
        XCTAssertTrue(model.rows.isEmpty)
        XCTAssertFalse(model.isLoading)
    }

    func testStoreSearchFindsOldEntriesBeforeApplyingLimitAndEscapesWildcards() async throws {
        let fixture = try SettingsGapStorageFixture()
        defer { fixture.remove() }
        try fixture.store.recordDictation(
            startedAt: Date(timeIntervalSince1970: 1), durationSeconds: 1, sampleCount: 1,
            transcriptText: "Old needle 100%_\\ literal", targetApp: "com.example.OldApp")
        for index in 2...250 {
            try fixture.store.recordDictation(
                startedAt: Date(timeIntervalSince1970: Double(index)), durationSeconds: 1, sampleCount: 1,
                transcriptText: "Recent entry \(index)", targetApp: "com.example.Recent")
        }
        let recent = try await fixture.store.loadHistoryRows(limit: 200)
        XCTAssertEqual(recent.count, 200)
        XCTAssertFalse(recent.contains { $0.record.transcriptText?.contains("needle") == true })
        let search = try await fixture.store.loadHistoryRows(query: " NEEDLE ", limit: 1)
        XCTAssertEqual(search.count, 1)
        let appMatches = try await fixture.store.loadHistoryRows(query: "oldapp")
        XCTAssertEqual(appMatches.count, 1)
        for query in ["100%", "_\\", "%_\\"] {
            let matches = try await fixture.store.loadHistoryRows(query: query)
            XCTAssertEqual(matches.count, 1, query)
        }
        let zero = try await fixture.store.loadHistoryRows(query: "needle", limit: 0)
        XCTAssertTrue(zero.isEmpty)
    }

    func testDeleteAndClearRefreshTheActiveSearchAgainstRealStorage() async throws {
        let fixture = try SettingsGapStorageFixture()
        defer { fixture.remove() }
        for index in 1...3 {
            try fixture.store.recordDictation(
                startedAt: Date(timeIntervalSince1970: Double(index)), durationSeconds: 1, sampleCount: 1,
                transcriptText: "matching entry \(index)")
        }
        let model = HistoryListModel(access: .live(fixture.store), delay: {})
        model.query = "matching"
        model.appear()
        await model.inFlight?.value
        XCTAssertEqual(model.rows.count, 3)
        await model.delete(model.rows[0], onDeleted: {})
        await model.inFlight?.value
        XCTAssertEqual(model.rows.count, 2)
        XCTAssertEqual(fixture.store.removedTextCount, 1)
        model.stop()
        _ = try fixture.store.clearHistory()
        model.appear()
        await model.inFlight?.value
        XCTAssertTrue(model.rows.isEmpty)
        XCTAssertEqual(model.query, "matching")
    }

    func testReadAndDeleteFailuresAreVisibleWithoutLeakingErrorText() async {
        let access = HistoryListAccess(
            read: { _ in throw SettingsDraftSaveError("private transcript") },
            delete: { _ in throw SettingsDraftSaveError("private transcript") })
        let model = HistoryListModel(access: access)
        model.appear()
        await model.inFlight?.value
        XCTAssertEqual(model.errorMessage, "Couldn't read your dictation history. Try again.")
        await model.delete(row(1), onDeleted: { XCTFail("Failed deletion must not report success") })
        XCTAssertEqual(model.errorMessage, "Couldn't delete that dictation. Try again.")
        XCTAssertFalse(model.isDeleting)
    }
}
