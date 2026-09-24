import Foundation
import SQLite3
import XCTest
import os

@testable import Scribe

private let sqliteTransient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

/// History at the scale of years of daily use: 50,000 dictations over 400 days, read through the production store's
/// windowed queries, the launch seed of Recent Dictations, and a 90-day retention sweep with its reclamation, all in a
/// real database. Timings are reported, never asserted: runner disks vary too much for a bound to mean anything.
final class StorageScenarioTests: XCTestCase {
    private static let rows = 50_000
    /// The newest rows, written one at a time through `PersistenceStore.recordDictation` as `HistoryWriter` writes
    /// them. The older rows are seeded in one transaction, so the scenario takes seconds rather than minutes.
    private static let productionWrites = 1_000
    private static let day: TimeInterval = 86_400
    /// Rows are spread evenly over this span, newest first.
    private static let span: TimeInterval = 400 * day
    /// The three newest rows hold no text, which the launch seed must pass over.
    private static let blankRows = 3
    private static let apps: [String?] = [
        "com.apple.Terminal", "com.microsoft.VSCode", "com.tinyspeck.slackmacgap", "com.apple.mail", nil,
    ]

    func testFiftyThousandDictationsReadInWindowsSeedAtMostFiveAndSweepToTheRetentionLimit() async throws {
        let library = try ScenarioLibrary.shared()
        let directory = try makeScenarioDirectory("storage")
        let databaseURL = directory.appendingPathComponent("scribe.db")
        let store = PersistenceStore(databaseURL: databaseURL)
        try store.initialize()
        let report = ScenarioReport("storage-50k")
        let clock = ContinuousClock()
        let now = Date()
        let texts = library.clips.map(\.text)
        let records = (0..<Self.rows).map { Self.record($0, now: now, texts: texts) }

        // Oldest first, so row ids grow with time as they do in use.
        var started = clock.now
        try ScenarioHistorySeed.insert(records[Self.productionWrites...].reversed(), into: databaseURL)
        report.note("seed\(Self.rows - Self.productionWrites)Rows", duration: started.duration(to: clock.now))
        started = clock.now
        for record in records[..<Self.productionWrites].reversed() {
            try store.recordDictation(record)
        }
        let writing = started.duration(to: clock.now)
        report.note("write\(Self.productionWrites)Rows", duration: writing)
        report.note("perWriteMs", value: ScenarioReport.milliseconds(writing) / Double(Self.productionWrites))

        started = clock.now
        XCTAssertEqual(try store.historyCount(), Self.rows)
        report.note("count", duration: started.duration(to: clock.now))

        // The newest thousand, oldest first, as the store's default read returns them.
        started = clock.now
        let newest = try store.fetchDictationHistory()
        report.note("read1000Newest", duration: started.duration(to: clock.now))
        XCTAssertEqual(newest.count, PersistenceStore.defaultHistoryReadLimit)
        XCTAssertEqual(
            try XCTUnwrap(newest.last).startedAt.timeIntervalSince1970, records[0].startedAt.timeIntervalSince1970,
            accuracy: 0.001)
        XCTAssertEqual(
            try XCTUnwrap(newest.first).startedAt.timeIntervalSince1970,
            records[PersistenceStore.defaultHistoryReadLimit - 1].startedAt.timeIntervalSince1970, accuracy: 0.001)

        // Diagnostics reads the newest thousand of its window and says when the window holds more.
        let diagnostics = DiagnosticsSettingsAccess.live(store)
        started = clock.now
        let week = try await diagnostics.loadWindow(now.addingTimeInterval(-7 * Self.day))
        report.note("diagnostics7Days", duration: started.duration(to: clock.now))
        XCTAssertEqual(week.stats?.count, Self.rowsNewer(than: 7))
        XCTAssertFalse(week.capped)
        started = clock.now
        let month = try await diagnostics.loadWindow(now.addingTimeInterval(-30 * Self.day))
        report.note("diagnostics30Days", duration: started.duration(to: clock.now))
        XCTAssertEqual(month.stats?.count, DiagnosticsSettingsAccess.readLimit)
        XCTAssertTrue(month.capped)

        // Usage Insights reads the newest 5,000 of its period and says when the period holds more.
        let usage = UsageInsightsAccess.live(store)
        started = clock.now
        let quarter = try await usage.loadReport(now.addingTimeInterval(-90 * Self.day), now)
        report.note("usage90Days", duration: started.duration(to: clock.now))
        XCTAssertEqual(quarter.snapshot.dictations, UsageAnalyzer.historyLimit)
        XCTAssertTrue(quarter.periodCapped)
        let recentWeek = try await usage.loadReport(now.addingTimeInterval(-7 * Self.day), now)
        XCTAssertEqual(recentWeek.snapshot.dictations, Self.rowsNewer(than: 7))
        XCTAssertFalse(recentWeek.periodCapped)

        // Launch seeds Recent Dictations as the app does, reading at most five rows and passing over empty ones.
        let recovery = LastTranscriptStore()
        let loaded = OSAllocatedUnfairLock(initialState: -1)
        started = clock.now
        let seeded = await recovery.seed(from: {
            let transcripts = try await store.loadRecentTranscripts(limit: LastTranscriptStore.capacity)
            loaded.withLock { $0 = transcripts.count }
            return transcripts
        })
        report.note("launchSeed", duration: started.duration(to: clock.now))
        XCTAssertTrue(seeded)
        XCTAssertEqual(loaded.withLock { $0 }, LastTranscriptStore.capacity)
        let expectedRecent = records[Self.blankRows..<(Self.blankRows + LastTranscriptStore.capacity)].map {
            $0.transcriptText ?? ""
        }
        XCTAssertEqual(recovery.recent(), expectedRecent)

        // A 90-day limit, swept in the maintenance's own batches, then the freed pages given back.
        try store.setHistoryRetention(.days(90))
        var options = StorageMaintenance.Options()
        options.quietPeriod = 0
        let maintenance = StorageMaintenance(store: store, options: options, now: { now })
        let before = try store.pageStats()
        started = clock.now
        let pass = maintenance.runPass()
        let sweeping = started.duration(to: clock.now)
        let after = try store.pageStats()
        let kept = Self.rowsNewer(than: 90)
        XCTAssertEqual(pass.retention, .applied(days: 90, removed: Self.rows - kept))
        guard case .completed(_, let freedPages) = pass.reclaim else {
            return XCTFail("the sweep's pages were not given back: \(pass.reclaim)")
        }
        XCTAssertGreaterThan(freedPages, 0)
        XCTAssertLessThan(after.pageCount, before.pageCount)
        XCTAssertEqual(try store.historyCount(), kept)
        let survivors = try store.fetchDictationHistory(limit: Self.rows)
        XCTAssertEqual(survivors.count, kept)
        let cutoff = now.addingTimeInterval(-90 * Self.day)
        XCTAssertTrue(survivors.allSatisfy { $0.startedAt >= cutoff }, "a row older than the limit survived")
        XCTAssertEqual(try store.fetchRecentTranscripts(limit: LastTranscriptStore.capacity), expectedRecent)
        let monthAfter = try await diagnostics.loadWindow(now.addingTimeInterval(-30 * Self.day))
        XCTAssertEqual(monthAfter.stats?.count, DiagnosticsSettingsAccess.readLimit)

        let walURL = URL(fileURLWithPath: databaseURL.path(percentEncoded: false) + "-wal")
        report.note("rows", count: Self.rows)
        report.note("kept", count: kept)
        report.note("retentionPass", duration: sweeping)
        report.note("reclaim", String(describing: pass.reclaim))
        report.note("checkpoint", String(describing: pass.checkpoint))
        report.note("pagesBefore", count: Int(before.pageCount))
        report.note("pagesAfter", count: Int(after.pageCount))
        report.note("databaseBytes", count: Self.fileSize(databaseURL))
        report.note("walBytes", count: Self.fileSize(walURL))
        report.write()
    }

    // MARK: - Rows

    /// Row `index`, counted from the newest: evenly spaced over `span`, half a step off every whole-day boundary the
    /// windows use, so no row sits on a cutoff.
    private static func record(_ index: Int, now: Date, texts: [String]) -> DictationHistoryRecord {
        let seconds = 1 + index % 20
        let text: String?
        switch index {
        case 0: text = ""
        case 1: text = "  \n "
        case 2: text = nil
        default: text = texts[index % texts.count]
        }
        return DictationHistoryRecord(
            startedAt: now.addingTimeInterval(-(Double(index) + 0.5) * step),
            durationSeconds: Double(seconds),
            sampleCount: seconds * 16_000,
            decodeMilliseconds: Double(100 + index % 400),
            cleanupMilliseconds: index % 3 == 0 ? nil : Double(200 + index % 300),
            transcriptText: text,
            targetApp: apps[index % apps.count])
    }

    private static var step: TimeInterval {
        span / Double(rows)
    }

    /// How many rows started less than `days` days ago.
    private static func rowsNewer(than days: Double) -> Int {
        Int((days * day / step - 0.5).rounded(.up))
    }

    private static func fileSize(_ url: URL) -> Int {
        let size = try? FileManager.default.attributesOfItem(atPath: url.path(percentEncoded: false))[.size]
        return (size as? Int) ?? 0
    }
}

enum ScenarioSeedError: Error {
    case sqlite(Int32)
}

/// Writes history rows straight into the table `PersistenceStore` created, in one transaction and in the order given,
/// with the store's own timestamp format. Everything is read back through the store, which skips a row whose timestamp
/// it cannot parse, so a format that drifted from the store's would show as missing rows.
enum ScenarioHistorySeed {
    static func insert(_ records: some Sequence<DictationHistoryRecord>, into database: URL) throws {
        var opened: OpaquePointer?
        let openCode = sqlite3_open_v2(database.path(percentEncoded: false), &opened, SQLITE_OPEN_READWRITE, nil)
        guard openCode == SQLITE_OK, let db = opened else {
            sqlite3_close_v2(opened)
            throw ScenarioSeedError.sqlite(openCode)
        }
        defer { sqlite3_close_v2(db) }
        sqlite3_busy_timeout(db, 5_000)

        try execute(db, "BEGIN IMMEDIATE;")
        do {
            var prepared: OpaquePointer?
            let insert = """
                INSERT INTO dictation_history(
                    started_at, duration_seconds, sample_count, decode_ms, cleanup_ms, transcript_text, target_app)
                VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7);
                """
            let prepareCode = sqlite3_prepare_v2(db, insert, -1, &prepared, nil)
            guard prepareCode == SQLITE_OK, let statement = prepared else {
                throw ScenarioSeedError.sqlite(prepareCode)
            }
            defer { sqlite3_finalize(statement) }

            let timestamps = ISO8601DateFormatter()
            timestamps.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            for record in records {
                sqlite3_reset(statement)
                sqlite3_clear_bindings(statement)
                bind(timestamps.string(from: record.startedAt), at: 1, in: statement)
                sqlite3_bind_double(statement, 2, record.durationSeconds)
                sqlite3_bind_int64(statement, 3, Int64(record.sampleCount))
                bind(record.decodeMilliseconds, at: 4, in: statement)
                bind(record.cleanupMilliseconds, at: 5, in: statement)
                bind(record.transcriptText, at: 6, in: statement)
                bind(record.targetApp, at: 7, in: statement)
                let stepCode = sqlite3_step(statement)
                guard stepCode == SQLITE_DONE else {
                    throw ScenarioSeedError.sqlite(stepCode)
                }
            }
            try execute(db, "COMMIT;")
        } catch {
            sqlite3_exec(db, "ROLLBACK;", nil, nil, nil)
            throw error
        }
    }

    private static func execute(_ db: OpaquePointer, _ sql: String) throws {
        let code = sqlite3_exec(db, sql, nil, nil, nil)
        guard code == SQLITE_OK else {
            throw ScenarioSeedError.sqlite(code)
        }
    }

    private static func bind(_ value: String?, at index: Int32, in statement: OpaquePointer) {
        if let value {
            sqlite3_bind_text(statement, index, value, Int32(value.utf8.count), sqliteTransient)
        } else {
            sqlite3_bind_null(statement, index)
        }
    }

    private static func bind(_ value: Double?, at index: Int32, in statement: OpaquePointer) {
        if let value {
            sqlite3_bind_double(statement, index, value)
        } else {
            sqlite3_bind_null(statement, index)
        }
    }
}
