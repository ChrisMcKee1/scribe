import XCTest

@testable import Scribe

final class DiagnosticsAndUsageModelTests: XCTestCase {
    private var directory: StorageTestDirectory!
    private let fixedNow = Date(timeIntervalSince1970: 1_800_000_000)

    override func setUpWithError() throws {
        directory = try StorageTestDirectory()
    }

    override func tearDownWithError() throws {
        directory.remove()
    }

    private static func records(_ count: Int, at date: Date) -> [DictationHistoryRecord] {
        (0..<count).map { index in
            DictationHistoryRecord(
                startedAt: date,
                durationSeconds: 1,
                sampleCount: 16_000,
                decodeMilliseconds: 100,
                transcriptText: "entry number \(index)")
        }
    }

    private static func window(dictations: Int, capped: Bool) -> DiagnosticsWindowSummary {
        let stats = DictationStats.compute(
            entries: records(dictations, at: Date(timeIntervalSince1970: 1_800_000_000)), since: .distantPast)
        return DiagnosticsWindowSummary(stats: stats, capped: capped)
    }

    private static func report(dictations: Int, capped: Bool) -> UsageAnalyzer.Report {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let snapshot = UsageAnalyzer.report(
            records: records(dictations, at: now),
            knownTerms: [],
            sinceUtc: now.addingTimeInterval(-86_400),
            nowUtc: now
        ).snapshot
        return UsageAnalyzer.Report(snapshot: snapshot, periodCapped: capped)
    }

    // MARK: - Diagnostics

    @MainActor
    func testAnOlderDiagnosticsWindowThatFinishesLastIsDropped() async {
        let gates = StorageTestCallGates(count: 2)
        let now = fixedNow
        let model = DiagnosticsSettingsModel(
            access: DiagnosticsSettingsAccess(loadWindow: { _ in
                let call = await gates.pass()
                return Self.window(dictations: call == 0 ? 3 : 5, capped: call == 0)
            }),
            now: { now })

        model.windowDays = 7
        let older = Task { await model.reload() }
        await gates.gate(0).waitForArrival()
        model.windowDays = 30
        let newer = Task { await model.reload() }
        await gates.gate(1).waitForArrival()

        await gates.gate(1).open()
        await newer.value
        await gates.gate(0).open()
        await older.value

        XCTAssertEqual(model.stats?.count, 5)
        XCTAssertFalse(model.capped)
        XCTAssertNil(model.coverageNote)
        XCTAssertNil(model.errorMessage)
    }

    @MainActor
    func testAnOlderDiagnosticsFailureThatArrivesLastIsDropped() async {
        let gates = StorageTestCallGates(count: 2)
        let now = fixedNow
        let model = DiagnosticsSettingsModel(
            access: DiagnosticsSettingsAccess(loadWindow: { _ in
                let call = await gates.pass()
                if call == 0 {
                    throw StorageTestFailure(message: "the older window failed")
                }
                return Self.window(dictations: 5, capped: false)
            }),
            now: { now })

        let older = Task { await model.reload() }
        await gates.gate(0).waitForArrival()
        model.windowDays = 30
        let newer = Task { await model.reload() }
        await gates.gate(1).waitForArrival()

        await gates.gate(1).open()
        await newer.value
        await gates.gate(0).open()
        await older.value

        XCTAssertNil(model.errorMessage)
        XCTAssertEqual(model.stats?.count, 5)
        XCTAssertTrue(model.load.isLoaded)
    }

    @MainActor
    func testAWindowHoldingMoreDictationsThanOneReadCoversSaysSo() async throws {
        let store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.insertBulkHistory(count: DiagnosticsSettingsAccess.readLimit + 1, startedAt: fixedNow)
        raw.close()
        let now = fixedNow
        let model = DiagnosticsSettingsModel(access: .live(store), now: { now })

        await model.reload()

        XCTAssertTrue(model.capped)
        XCTAssertEqual(model.stats?.count, DiagnosticsSettingsAccess.readLimit)
        XCTAssertEqual(
            model.coverageNote,
            "Covers the newest \(DiagnosticsSettingsAccess.readLimit.formatted()) dictations in this window.")
    }

    // MARK: - Usage Insights

    @MainActor
    func testAnOlderUsagePeriodThatFinishesLastIsDropped() async {
        let gates = StorageTestCallGates(count: 2)
        let now = fixedNow
        let model = UsageInsightsModel(
            access: UsageInsightsAccess(
                loadReport: { _, _ in
                    let call = await gates.pass()
                    return Self.report(dictations: call == 0 ? 2 : 4, capped: call == 0)
                },
                addTerm: { _ in false }),
            onChanged: {},
            now: { now })

        model.windowDays = 7
        let older = Task { await model.reload() }
        await gates.gate(0).waitForArrival()
        model.windowDays = 90
        let newer = Task { await model.reload() }
        await gates.gate(1).waitForArrival()

        await gates.gate(1).open()
        await newer.value
        await gates.gate(0).open()
        await older.value

        XCTAssertEqual(model.snapshot?.dictations, 4)
        XCTAssertFalse(model.periodCapped)
        XCTAssertNil(model.coverageNote)
        XCTAssertNil(model.loadError)
    }

    @MainActor
    func testAnOlderUsageFailureThatArrivesLastIsDropped() async {
        let gates = StorageTestCallGates(count: 2)
        let now = fixedNow
        let model = UsageInsightsModel(
            access: UsageInsightsAccess(
                loadReport: { _, _ in
                    let call = await gates.pass()
                    if call == 0 {
                        throw StorageTestFailure(message: "the older period failed")
                    }
                    return Self.report(dictations: 4, capped: false)
                },
                addTerm: { _ in false }),
            onChanged: {},
            now: { now })

        let older = Task { await model.reload() }
        await gates.gate(0).waitForArrival()
        model.windowDays = 90
        let newer = Task { await model.reload() }
        await gates.gate(1).waitForArrival()

        await gates.gate(1).open()
        await newer.value
        await gates.gate(0).open()
        await older.value

        XCTAssertNil(model.loadError)
        XCTAssertEqual(model.snapshot?.dictations, 4)
    }

    @MainActor
    func testACappedUsagePeriodSaysItCoversOnlyTheNewestDictations() async {
        let now = fixedNow
        let model = UsageInsightsModel(
            access: UsageInsightsAccess(
                loadReport: { _, _ in Self.report(dictations: 3, capped: true) },
                addTerm: { _ in false }),
            onChanged: {},
            now: { now })

        await model.reload()

        XCTAssertTrue(model.periodCapped)
        XCTAssertEqual(
            model.coverageNote,
            "Covers the newest \(UsageAnalyzer.historyLimit.formatted()) dictations in this period.")
    }

    @MainActor
    func testAFailedTermAddStaysShownWhenAnOlderReloadFinishesAfterIt() async {
        let gate = SettingsTestGate()
        let now = fixedNow
        let model = UsageInsightsModel(
            access: UsageInsightsAccess(
                loadReport: { _, _ in
                    await gate.pass()
                    return Self.report(dictations: 2, capped: false)
                },
                addTerm: { _ in throw StorageTestFailure(message: "the term was not added") }),
            onChanged: {},
            now: { now })

        let loading = Task { await model.reload() }
        await gate.waitForArrival()
        await model.addTermToDictionary(
            UsageAnalyzer.TermUsage(text: "ReBAC", dictations: 3, occurrences: 4, covered: false))
        XCTAssertEqual(model.errorMessage, "the term was not added")

        await gate.open()
        await loading.value

        XCTAssertEqual(model.snapshot?.dictations, 2)
        XCTAssertEqual(model.errorMessage, "the term was not added")
        XCTAssertNil(model.loadError)
    }

    @MainActor
    func testAddingARecurringTermTwiceStoresItOnce() async throws {
        let store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        let refreshes = SettingsTestCounter()
        let now = fixedNow
        let model = UsageInsightsModel(access: .live(store), onChanged: { refreshes.increment() }, now: { now })
        let term = UsageAnalyzer.TermUsage(text: "ReBAC", dictations: 3, occurrences: 4, covered: false)

        await model.addTermToDictionary(term)
        XCTAssertEqual(try store.fetchAllDictionaryEntries().map(\.pattern), ["ReBAC"])
        XCTAssertNil(model.statusMessage)

        await model.addTermToDictionary(term)
        XCTAssertEqual(try store.fetchAllDictionaryEntries().count, 1)
        XCTAssertEqual(model.statusMessage, "\u{201C}ReBAC\u{201D} is already in your dictionary.")
        XCTAssertEqual(refreshes.count, 1)
    }
}
