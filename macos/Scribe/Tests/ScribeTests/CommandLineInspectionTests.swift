import SQLite3
import XCTest

@testable import Scribe

/// The command-line verbs that look at a user's data leave it as it was: they read the database without changing its
/// contents, never create one, and run their verification fixtures in a temporary database of their own, removed
/// afterwards, never in the user's.
final class CommandLineInspectionTests: XCTestCase {
    private var directory: StorageTestDirectory!
    private var scratch: URL!
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    override func setUpWithError() throws {
        directory = try StorageTestDirectory()
        scratch = try makeTemporaryDirectory(label: "verbs")
    }

    override func tearDownWithError() throws {
        directory.remove()
    }

    /// The user's database with whatever `fill` adds, closed again so its file is complete, and that file's bytes.
    private func makeUserDatabase(_ fill: (PersistenceStore) throws -> Void) throws -> Data {
        let store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        try fill(store)
        store.closeConnection()
        return try Data(contentsOf: directory.databaseURL)
    }

    private func userDatabaseBytes() throws -> Data {
        try Data(contentsOf: directory.databaseURL)
    }

    /// What the verbs left behind in their scratch directory.
    private func scratchContents() throws -> [String] {
        try FileManager.default.contentsOfDirectory(atPath: scratch.path(percentEncoded: false))
    }

    // MARK: - --post-process-text

    func testPostProcessUsesTheUsersRulesAndLeavesTheirDatabaseAsItWas() throws {
        let before = try makeUserDatabase { store in
            _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "kay eight ess", replacement: "K8s"))
        }

        let outcome = try CommandLineInspection.postProcess(
            "kay eight ess and github", database: directory.databaseURL, scratch: scratch)

        XCTAssertEqual(outcome, .init(text: "K8s and github", usedFixtures: false))
        XCTAssertEqual(try userDatabaseBytes(), before)
        XCTAssertEqual(try scratchContents(), [])
    }

    /// A user with no rules gets the fixtures from a temporary database. The verb used to write them into the user's
    /// own database, where every later dictation would have applied them.
    func testPostProcessWithNoRulesUsesFixturesAndNeverWritesThemToTheUsersDatabase() throws {
        let before = try makeUserDatabase { _ in }

        let outcome = try CommandLineInspection.postProcess(
            "i love github", database: directory.databaseURL, scratch: scratch)

        XCTAssertEqual(outcome, .init(text: "i love GitHub", usedFixtures: true))
        XCTAssertEqual(try userDatabaseBytes(), before)
        let store = PersistenceStore(databaseURL: directory.databaseURL, access: .readOnly)
        defer { store.closeConnection() }
        XCTAssertEqual(try store.fetchAllDictionaryEntries(), [])
        XCTAssertEqual(try store.fetchAllSnippets(), [])
        XCTAssertEqual(try scratchContents(), [])
    }

    /// The app may be running with its own connection open: a read-only look sees what it has committed, even while
    /// that is still in the WAL.
    func testPostProcessSeesWhatTheRunningAppCommitted() throws {
        let app = PersistenceStore(databaseURL: directory.databaseURL)
        try app.initialize()
        defer { app.closeConnection() }
        _ = try app.insertDictionaryEntry(DictionaryEntry(pattern: "kay eight ess", replacement: "K8s"))

        let outcome = try CommandLineInspection.postProcess(
            "kay eight ess", database: directory.databaseURL, scratch: scratch)

        XCTAssertEqual(outcome, .init(text: "K8s", usedFixtures: false))
    }

    // MARK: - --resolve-profile

    func testResolveProfileUsesTheUsersProfilesAndLeavesTheirDatabaseAsItWas() throws {
        let before = try makeUserDatabase { store in
            _ = try store.insertAppProfile(
                AppProfile(
                    name: "Notes", bundleIdentifiers: ["com.example.notes"], processNames: [],
                    newlineHandling: .keepNewlines))
        }

        let outcome = try CommandLineInspection.resolveProfile(
            bundleIdentifier: "com.example.notes", text: "one\ntwo", database: directory.databaseURL, scratch: scratch)

        XCTAssertEqual(outcome.profile?.name, "Notes")
        XCTAssertEqual(outcome.newlineMode, .keepNewlines)
        XCTAssertEqual(outcome.text, "one\ntwo")
        XCTAssertFalse(outcome.usedFixtures)
        XCTAssertEqual(try userDatabaseBytes(), before)
        XCTAssertEqual(try scratchContents(), [])
    }

    /// A user with no profiles gets the fixtures from a temporary database, never written into the user's own.
    func testResolveProfileWithNoProfilesUsesFixturesAndNeverWritesThemToTheUsersDatabase() throws {
        let before = try makeUserDatabase { _ in }

        let outcome = try CommandLineInspection.resolveProfile(
            bundleIdentifier: "com.apple.Terminal", text: "one\ntwo", database: directory.databaseURL, scratch: scratch)

        XCTAssertEqual(outcome.profile?.name, "Terminal")
        XCTAssertEqual(outcome.newlineMode, .alwaysFlatten)
        XCTAssertEqual(outcome.text, "one two")
        XCTAssertTrue(outcome.usedFixtures)
        XCTAssertEqual(try userDatabaseBytes(), before)
        let store = PersistenceStore(databaseURL: directory.databaseURL, access: .readOnly)
        defer { store.closeConnection() }
        XCTAssertEqual(try store.fetchAppProfiles(), [])
        XCTAssertEqual(try scratchContents(), [])
    }

    // MARK: - No database, and read-only access

    /// On a Mac where Scribe never ran, a verb reads nothing and leaves no database behind.
    func testTheVerbsCreateNoDatabaseWhereThereWasNone() throws {
        let processed = try CommandLineInspection.postProcess(
            "i love github", database: directory.databaseURL, scratch: scratch)
        let profile = try CommandLineInspection.resolveProfile(
            bundleIdentifier: "com.apple.mail", text: "Hi", database: directory.databaseURL, scratch: scratch)
        let window = try CommandLineInspection.diagnostics(database: directory.databaseURL, since: now)
        let report = CommandLineInspection.diagnosticsReport(window, days: 7)

        XCTAssertTrue(processed.usedFixtures)
        XCTAssertEqual(profile.profile?.name, "Email")
        XCTAssertEqual(report, "No dictations in the last 7.0 day(s).")
        XCTAssertFalse(FileManager.default.fileExists(atPath: directory.databaseURL.path(percentEncoded: false)))
        XCTAssertEqual(try scratchContents(), [])
    }

    /// A read-only store refuses every write, and does not create a file that is not there.
    func testAReadOnlyStoreRefusesWritesAndCreatesNothing() throws {
        let missing = PersistenceStore(databaseURL: directory.databaseURL, access: .readOnly)
        XCTAssertThrowsError(try missing.fetchAllDictionaryEntries())
        missing.closeConnection()
        XCTAssertFalse(FileManager.default.fileExists(atPath: directory.databaseURL.path(percentEncoded: false)))

        let before = try makeUserDatabase { store in
            _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "kay eight ess", replacement: "K8s"))
        }
        let store = PersistenceStore(databaseURL: directory.databaseURL, access: .readOnly)
        let github = DictionaryEntry(pattern: "github", replacement: "GitHub")
        XCTAssertEqual(try store.fetchAllDictionaryEntries().map(\.replacement), ["K8s"])
        XCTAssertThrowsError(try store.insertDictionaryEntry(github)) {
            XCTAssertEqual(($0 as? PersistenceError)?.sqliteCode.map { $0 & 0xFF }, SQLITE_READONLY)
        }
        store.closeConnection()
        XCTAssertEqual(try userDatabaseBytes(), before)
    }

    // MARK: - --diagnostics

    /// A window with more dictations than one read covers is read to its newest ones, as the Diagnostics tab reads
    /// it, and the report says so in the tab's words. Rows before the window are not read at all.
    func testDiagnosticsReadsTheWindowsNewestAndSaysWhenItHoldsMore() throws {
        let limit = DiagnosticsSettingsAccess.readLimit
        _ = try makeUserDatabase { _ in }
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.insertBulkHistory(count: limit + 1, startedAt: now.addingTimeInterval(-86_400))
        try raw.insertBulkHistory(count: 3, startedAt: now.addingTimeInterval(-30 * 86_400))
        raw.close()

        let window = try CommandLineInspection.diagnostics(
            database: directory.databaseURL, since: now.addingTimeInterval(-7 * 86_400))
        let report = CommandLineInspection.diagnosticsReport(window, days: 7)

        XCTAssertTrue(window.capped)
        XCTAssertEqual(window.stats?.count, limit)
        XCTAssertTrue(report.hasPrefix("Dictations: \(limit)\n"), report)
        XCTAssertTrue(report.hasSuffix("\n" + DiagnosticsWindowSummary.coverageNote), report)
    }

    func testDiagnosticsWithinOneReadCarriesNoNote() throws {
        _ = try makeUserDatabase { store in
            try store.recordDictation(
                startedAt: now.addingTimeInterval(-3_600), durationSeconds: 2, sampleCount: 32_000,
                transcriptText: "hello")
        }

        let window = try CommandLineInspection.diagnostics(
            database: directory.databaseURL, since: now.addingTimeInterval(-7 * 86_400))

        XCTAssertFalse(window.capped)
        XCTAssertEqual(window.stats?.count, 1)
        let report = CommandLineInspection.diagnosticsReport(window, days: 7)
        XCTAssertFalse(report.contains(DiagnosticsWindowSummary.coverageNote), report)
    }
}
