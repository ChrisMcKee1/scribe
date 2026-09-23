import Foundation
import OSLog
import SQLite3

private let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

/// What the store was doing when storage failed. The case name is safe to log and to show.
enum PersistenceOperation: String, Sendable {
    case open
    case migrate
    case read
    case write
    case maintenance
}

/// A storage failure described by its shape only: the operation and SQLite's result code. It never
/// carries SQL text, bound values or `sqlite3_errmsg`, which can quote either, so it is safe to log
/// and to show in Settings.
enum PersistenceError: LocalizedError, Equatable {
    /// SQLite returned `code` (an extended result code) during `operation`.
    case sqlite(operation: PersistenceOperation, code: Int32)
    /// A change that must touch exactly one existing row found none: it went away underneath.
    case rowMissing(operation: PersistenceOperation)
    /// The folder that holds the database could not be created (`NSError` code).
    case directoryUnavailable(code: Int)

    /// The SQLite result code, when SQLite produced the failure.
    var sqliteCode: Int32? {
        if case .sqlite(_, let code) = self {
            return code
        }
        return nil
    }

    var errorDescription: String? {
        switch self {
        case .sqlite(let operation, let code):
            // sqlite3_errstr describes the code itself, a fixed English phrase with no dynamic content.
            let reason = String(cString: sqlite3_errstr(code))
            return "Scribe could not \(operation.verb) its database (SQLite error \(code): \(reason))."
        case .rowMissing:
            return "An entry changed while this was being saved, so nothing was saved. Try again."
        case .directoryUnavailable(let code):
            return "Scribe could not create the folder for its database (error \(code))."
        }
    }
}

private extension PersistenceOperation {
    var verb: String {
        switch self {
        case .open: return "open"
        case .migrate: return "update"
        case .read: return "read"
        case .write: return "save to"
        case .maintenance: return "clean up"
        }
    }
}

/// Who is using the connection. Foreground work (the app, Settings, history writes) makes a running
/// housekeeping statement stop at once; housekeeping never counts as foreground activity.
enum ConnectionPurpose: Sendable {
    case foreground
    case maintenance
}

/// Page accounting for space reclamation, read with SQLite's own pragmas.
struct PersistencePageStats: Equatable, Sendable {
    let pageSize: Int64
    let pageCount: Int64
    let freePages: Int64
    /// `PRAGMA auto_vacuum`: 0 none, 1 full, 2 incremental.
    let autoVacuum: Int64

    var freeBytes: Int64 { freePages * pageSize }
}

/// How a yielding housekeeping statement ended. SQLite rolls an interrupted statement back.
enum YieldingStatementOutcome: Equatable, Sendable {
    case completed
    /// A foreground caller was waiting for the connection.
    case yieldedToForeground
    /// The caller's `shouldStop` answered true (shutdown, Clear history, a dictation, or its budget).
    case stopped
}

/// What one retention batch did.
enum RetentionBatchOutcome: Equatable, Sendable {
    case deleted(Int)
    /// The stored choice no longer authorizes the limit the sweep started with (it changed, went
    /// missing or became unreadable), so nothing was deleted.
    case authorizationChanged(HistoryRetentionSetting)
}

/// What a dictionary import changed, by count.
struct DictionaryImportSummary: Equatable, Sendable {
    let added: Int
    let updated: Int
    let unchanged: Int
}

/// What every dictation applies: the enabled dictionary rules, the enabled snippets and the app
/// profiles, read together so they agree with each other.
struct PersistenceRuleSet: Equatable, Sendable {
    let dictionaryEntries: [DictionaryEntry]
    let snippets: [Snippet]
    let appProfiles: [AppProfile]
}

/// Recent dictations and every stored dictionary rule, read together for mining and the Clean Up
/// review.
struct DictionaryReviewInputs: Sendable {
    let history: [DictationHistoryRecord]
    let entries: [DictionaryEntry]
}

/// One Usage Insights period: its newest dictations, oldest first, and every stored dictionary rule,
/// read together.
struct UsagePeriodInputs: Sendable {
    let records: [DictationHistoryRecord]
    let knownTerms: [DictionaryEntry]
}

/// The app's one SQLite database: dictation history, the dictionary, snippets, app profiles and the
/// history retention choice.
///
/// Every call goes through one connection held by `ConnectionOwner`, so Settings reads, the
/// background history writer and storage maintenance never race each other on separate connections.
/// The database runs in WAL mode with a bounded busy timeout, so the CLI verbs can read while the app
/// writes, and a writer that meets another process's lock waits a bounded time instead of failing at
/// once. Every read steps its statement to `SQLITE_DONE`: an error midway is thrown, never returned as
/// a short or empty result that a caller would take for the truth.
///
/// The synchronous methods run on the caller's thread and wait for the storage queue, so a write
/// waiting out another process's lock can hold them for up to the busy timeout. Main-actor code uses
/// the asynchronous `load...` and `save...` forms instead: they hop to the storage queue and resume
/// the caller when done, so the main actor is free while they wait.
///
/// Logs carry shapes only (counts, operations, SQLite result codes), never transcript text,
/// dictionary patterns, snippet bodies, profile names or paths.
final class PersistenceStore: Sendable {
    /// Newest dictations a history read returns by default. Windows reads `GetRecent(1000)` for the
    /// Diagnostics panel, learning from history and dictionary cleanup; the same bound keeps these
    /// reads proportional to what a user asked for, not to a lifetime of history.
    static let defaultHistoryReadLimit = 1_000

    /// How long a statement waits for another process's lock before failing with `SQLITE_BUSY`.
    static let defaultBusyTimeoutMilliseconds: Int32 = 5_000

    /// Bytecode steps between checks while a housekeeping statement runs: often enough that a waiting
    /// caller gets the connection within microseconds, rarely enough that checking costs almost nothing.
    static let defaultProgressInterval: Int32 = 1_000

    static let retentionSettingKey = "history_retention_days"

    /// Test seams. The app uses the defaults.
    struct TestHooks: Sendable {
        /// A small database can finish a VACUUM in fewer steps than the default interval, so tests
        /// that must see a check use 1.
        var progressInterval: Int32 = PersistenceStore.defaultProgressInterval
        /// Called on the calling thread when a foreground operation registers that it is waiting for
        /// the storage queue, before it is queued.
        var onForegroundWait: (@Sendable () -> Void)?
        /// Called on the storage queue as each operation begins, before it touches the connection.
        var onOperationBegin: (@Sendable (ConnectionPurpose) -> Void)?
    }

    let databaseURL: URL
    private let owner: ConnectionOwner
    private let logger = Logger(subsystem: "com.scribe.macos", category: "Persistence")

    init(
        fileManager: FileManager = .default,
        databaseURL overrideDatabaseURL: URL? = nil,
        busyTimeoutMilliseconds: Int32 = PersistenceStore.defaultBusyTimeoutMilliseconds,
        testHooks: TestHooks = TestHooks()
    ) {
        let resolvedURL: URL
        if let overrideDatabaseURL {
            resolvedURL = overrideDatabaseURL
        } else {
            let applicationSupportURL = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            let scribeDirectoryURL = applicationSupportURL.appendingPathComponent("Scribe", isDirectory: true)
            resolvedURL = scribeDirectoryURL.appendingPathComponent("scribe.db", isDirectory: false)
        }

        databaseURL = resolvedURL
        owner = ConnectionOwner(
            path: resolvedURL.path(percentEncoded: false),
            busyTimeoutMilliseconds: busyTimeoutMilliseconds,
            hooks: testHooks)
    }

    // MARK: - Schema

    /// Creates or updates the schema. The whole migration is one `BEGIN IMMEDIATE` transaction, so a
    /// failure part way leaves the previous schema intact, and a second process migrating at the same
    /// moment waits and then finds the work done.
    func initialize() throws {
        try createDatabaseDirectory()
        let createdHistory = try owner.withSession(.foreground) { session in
            try session.transaction(.migrate) {
                try Self.migrate(session)
            }
        }

        logger.info("Database ready (new history table: \(createdHistory)).")
    }

    /// `initialize()` for main-actor callers: the migration runs on the storage queue, not the
    /// caller's thread. The queue runs operations in the order they reach it, so anything queued on
    /// the store after this call runs against the migrated schema.
    func prepare() async throws {
        try createDatabaseDirectory()
        let createdHistory = try await owner.withSessionAsync(.foreground) { session in
            try session.transaction(.migrate) {
                try Self.migrate(session)
            }
        }

        logger.info("Database ready (new history table: \(createdHistory)).")
    }

    private func createDatabaseDirectory() throws {
        do {
            try FileManager.default.createDirectory(
                at: databaseURL.deletingLastPathComponent(),
                withIntermediateDirectories: true)
        } catch {
            throw PersistenceError.directoryUnavailable(code: (error as NSError).code)
        }
    }

    /// Returns whether the history table was created by this call, which is what makes the default
    /// retention safe to record.
    private static func migrate(_ session: SQLiteSession) throws -> Bool {
        let hadHistory = try session.tableExists("dictation_history", .migrate)

        try session.execute(
            """
            CREATE TABLE IF NOT EXISTS dictation_history(
                id INTEGER PRIMARY KEY,
                started_at TEXT NOT NULL,
                duration_seconds REAL NOT NULL,
                sample_count INTEGER NOT NULL,
                decode_ms REAL,
                cleanup_ms REAL,
                transcript_text TEXT,
                target_app TEXT
            );
            """,
            .migrate)

        // Older databases predate these columns, and SQLite has no ADD COLUMN IF NOT EXISTS, so probe.
        // decode_ms and cleanup_ms back the Diagnostics panel, transcript_text history mining and
        // recovery, target_app the Usage Insights app ranking. Older rows read them as nil.
        let columns = try session.columnNames(of: "dictation_history", .migrate)
        let additions = [
            ("decode_ms", "REAL"), ("cleanup_ms", "REAL"), ("transcript_text", "TEXT"), ("target_app", "TEXT"),
        ]
        for (name, sqlType) in additions where !columns.contains(name) {
            try session.execute("ALTER TABLE dictation_history ADD COLUMN \(name) \(sqlType);", .migrate)
        }

        // started_at is always the same fixed-width UTC form (ISO8601DateFormatter with fractional
        // seconds), so text order is time order: retention and the period reads use this index.
        try session.execute(
            "CREATE INDEX IF NOT EXISTS ix_dictation_history_started_at ON dictation_history(started_at);",
            .migrate)

        try session.execute(
            """
            CREATE TABLE IF NOT EXISTS dictionary_entries(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pattern TEXT NOT NULL,
                replacement TEXT NOT NULL,
                whole_word INTEGER NOT NULL DEFAULT 1,
                enabled INTEGER NOT NULL DEFAULT 1
            );
            """,
            .migrate)

        try session.execute(
            """
            CREATE TABLE IF NOT EXISTS snippets(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                phrase TEXT NOT NULL,
                template TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1
            );
            """,
            .migrate)

        try session.execute(
            """
            CREATE TABLE IF NOT EXISTS app_profiles(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                bundle_identifiers TEXT NOT NULL DEFAULT '',
                process_names TEXT NOT NULL DEFAULT '',
                writing_style_prompt TEXT,
                newline_handling TEXT
            );
            """,
            .migrate)

        try session.execute(
            "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);",
            .migrate)

        if !hadHistory {
            // A history this build creates is empty, so the default limit costs nothing and applies
            // from the first dictation. A history that already exists keeps every entry until the user
            // picks a limit: a default they never saw must not delete text they already have.
            let seed = "INSERT OR IGNORE INTO settings(key, value) VALUES (?1, ?2);"
            try session.withStatement(seed, .migrate) { statement in
                try statement.bind(retentionSettingKey, at: 1)
                try statement.bind(String(HistoryRetention.defaultDays), at: 2)
                try statement.run()
            }
        }

        return !hadHistory
    }

    // MARK: - Dictation history

    /// Inserts one history row. The dictation path goes through `HistoryWriter`, which calls this off
    /// the main actor after the text has been delivered.
    func recordDictation(_ record: DictationHistoryRecord) throws {
        try owner.withSession(.foreground) { session in
            try session.withStatement(
                """
                INSERT INTO dictation_history(
                    started_at, duration_seconds, sample_count, decode_ms, cleanup_ms, transcript_text, target_app)
                VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7);
                """,
                .write
            ) { statement in
                try statement.bind(session.timestamp(record.startedAt), at: 1)
                try statement.bind(record.durationSeconds, at: 2)
                try statement.bind(Int64(record.sampleCount), at: 3)
                try statement.bind(record.decodeMilliseconds, at: 4)
                try statement.bind(record.cleanupMilliseconds, at: 5)
                try statement.bind(record.transcriptText, at: 6)
                try statement.bind(record.targetApp, at: 7)
                try statement.run()
            }
        }
    }

    func recordDictation(
        startedAt: Date,
        durationSeconds: Double,
        sampleCount: Int,
        decodeMilliseconds: Double? = nil,
        cleanupMilliseconds: Double? = nil,
        transcriptText: String? = nil,
        targetApp: String? = nil
    ) throws {
        try recordDictation(
            DictationHistoryRecord(
                startedAt: startedAt,
                durationSeconds: durationSeconds,
                sampleCount: sampleCount,
                decodeMilliseconds: decodeMilliseconds,
                cleanupMilliseconds: cleanupMilliseconds,
                transcriptText: transcriptText,
                targetApp: targetApp))
    }

    /// The newest `limit` history rows that started at or after `since` (every retained row when
    /// `since` is nil), returned oldest first. Rows with a null decode or cleanup time or target app
    /// (older schema) surface as nil, matching Windows' `HistoryEntry`.
    func fetchDictationHistory(
        since: Date? = nil,
        limit: Int = PersistenceStore.defaultHistoryReadLimit
    ) throws -> [DictationHistoryRecord] {
        try owner.withSession(.foreground) { session in
            try Self.readDictationHistory(session, since: since, limit: limit)
        }
    }

    /// `fetchDictationHistory(since:limit:)` for main-actor callers: the read waits on the storage
    /// queue, not on the caller's thread.
    func loadDictationHistory(
        since: Date? = nil,
        limit: Int = PersistenceStore.defaultHistoryReadLimit
    ) async throws -> [DictationHistoryRecord] {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.readDictationHistory(session, since: since, limit: limit)
        }
    }

    /// The newest `limit` non-blank transcripts, newest first, for the tray's recovery ring. The
    /// chosen retention applies even before the next sweep runs, so text past the user's limit is
    /// never shown again; a missing or unreadable choice keeps everything, exactly as the sweep does.
    func fetchRecentTranscripts(limit: Int, now: Date = Date()) throws -> [String] {
        try owner.withSession(.foreground) { session in
            try Self.readRecentTranscripts(session, limit: limit, now: now)
        }
    }

    /// `fetchRecentTranscripts(limit:now:)` for main-actor callers.
    func loadRecentTranscripts(limit: Int, now: Date = Date()) async throws -> [String] {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.readRecentTranscripts(session, limit: limit, now: now)
        }
    }

    private static func readDictationHistory(
        _ session: SQLiteSession,
        since: Date?,
        limit: Int
    ) throws -> [DictationHistoryRecord] {
        try session.withStatement(
            """
            SELECT started_at, duration_seconds, sample_count, decode_ms, cleanup_ms, transcript_text, target_app
            FROM (
                SELECT id, started_at, duration_seconds, sample_count, decode_ms, cleanup_ms, transcript_text,
                    target_app
                FROM dictation_history
                WHERE started_at >= ?1
                ORDER BY id DESC
                LIMIT ?2
            )
            ORDER BY id ASC;
            """,
            .read
        ) { statement in
            try statement.bind(since.map { session.timestamp($0) } ?? "", at: 1)
            try statement.bind(Int64(max(0, limit)), at: 2)

            var records: [DictationHistoryRecord] = []
            while try statement.step() {
                guard let startedAtText = statement.text(at: 0),
                      let startedAt = session.date(from: startedAtText)
                else {
                    continue
                }

                records.append(
                    DictationHistoryRecord(
                        startedAt: startedAt,
                        durationSeconds: statement.double(at: 1) ?? 0,
                        sampleCount: Int(statement.int64(at: 2) ?? 0),
                        decodeMilliseconds: statement.double(at: 3),
                        cleanupMilliseconds: statement.double(at: 4),
                        transcriptText: statement.text(at: 5),
                        targetApp: statement.text(at: 6)))
            }
            return records
        }
    }

    private static func readRecentTranscripts(_ session: SQLiteSession, limit: Int, now: Date) throws -> [String] {
        let cutoff = (try? readRetention(session))?.cutoff(before: now)
        return try session.withStatement(
            """
            SELECT transcript_text
            FROM dictation_history
            WHERE started_at >= ?1
                AND transcript_text IS NOT NULL
                AND trim(transcript_text, ' ' || char(9) || char(10) || char(13)) <> ''
            ORDER BY id DESC
            LIMIT ?2;
            """,
            .read
        ) { statement in
            try statement.bind(cutoff.map { session.timestamp($0) } ?? "", at: 1)
            try statement.bind(Int64(max(0, limit)), at: 2)

            var transcripts: [String] = []
            while try statement.step() {
                if let text = statement.text(at: 0) {
                    transcripts.append(text)
                }
            }
            return transcripts
        }
    }

    func historyCount() throws -> Int {
        try owner.withSession(.foreground) { session in
            try Self.countHistory(session)
        }
    }

    /// `historyCount()` for main-actor callers.
    func loadHistoryCount() async throws -> Int {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.countHistory(session)
        }
    }

    /// The newest `limit` dictations that started at or after `since`, oldest first, and every stored
    /// dictionary rule, read in one visit to the storage queue for a Usage Insights period.
    func loadUsagePeriod(since: Date, limit: Int) async throws -> UsagePeriodInputs {
        try await owner.withSessionAsync(.foreground) { session in
            let records = try Self.readDictationHistory(session, since: since, limit: limit)
            let knownTerms = try Self.readDictionaryEntries(enabledOnly: false, session)
            return UsagePeriodInputs(records: records, knownTerms: knownTerms)
        }
    }

    private static func countHistory(_ session: SQLiteSession) throws -> Int {
        try Int(session.scalarInt64("SELECT count(*) FROM dictation_history;", .read))
    }

    /// Deletes every history row and returns how many went. This is not ordered against the
    /// background history writer, so an entry it has not committed yet lands afterwards; the app
    /// clears through `StorageMaintenance.clearHistory()`, which runs the clear in the writer's line.
    func clearHistory() throws -> Int {
        try owner.withSession(.foreground) { session in
            try session.execute("DELETE FROM dictation_history;", .write)
            return session.changes
        }
    }

    /// Deletes up to `limit` of the oldest rows past a `days` limit, but only while the stored choice
    /// still says exactly `days`. The check and the deletion are one `BEGIN IMMEDIATE` transaction,
    /// so a choice changed, removed or made unreadable while a sweep runs stops the sweep at its next
    /// batch instead of deleting further against the old limit.
    func deleteExpiredHistoryBatch(authorizedDays days: Int, now: Date, limit: Int) throws -> RetentionBatchOutcome {
        try owner.withSession(.maintenance) { session in
            try session.transaction(.maintenance) { () -> RetentionBatchOutcome in
                let current = try Self.readRetention(session)
                guard current == .chosen(HistoryRetention(days: days)),
                      let cutoff = current.cutoff(before: now)
                else {
                    return .authorizationChanged(current)
                }

                return try session.withStatement(
                    """
                    DELETE FROM dictation_history
                    WHERE id IN (
                        SELECT id FROM dictation_history WHERE started_at < ?1 ORDER BY started_at LIMIT ?2
                    );
                    """,
                    .maintenance
                ) { statement -> RetentionBatchOutcome in
                    try statement.bind(session.timestamp(cutoff), at: 1)
                    try statement.bind(Int64(max(1, limit)), at: 2)
                    try statement.run()
                    return .deleted(session.changes)
                }
            }
        }
    }

    // MARK: - History retention

    func historyRetention(purpose: ConnectionPurpose = .foreground) throws -> HistoryRetentionSetting {
        try owner.withSession(purpose) { session in
            try Self.readRetention(session)
        }
    }

    /// `historyRetention()` for main-actor callers.
    func loadHistoryRetention() async throws -> HistoryRetentionSetting {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.readRetention(session)
        }
    }

    func setHistoryRetention(_ retention: HistoryRetention) throws {
        let normalized = HistoryRetention(days: retention.storedDays)
        try owner.withSession(.foreground) { session in
            try Self.writeRetention(normalized, session)
        }
        logger.info("History retention set to \(normalized.storedDays) day(s); 0 keeps text forever.")
    }

    /// `setHistoryRetention(_:)` for main-actor callers.
    func saveHistoryRetention(_ retention: HistoryRetention) async throws {
        let normalized = HistoryRetention(days: retention.storedDays)
        try await owner.withSessionAsync(.foreground) { session in
            try Self.writeRetention(normalized, session)
        }
        logger.info("History retention set to \(normalized.storedDays) day(s); 0 keeps text forever.")
    }

    private static func readRetention(_ session: SQLiteSession) throws -> HistoryRetentionSetting {
        try session.withStatement("SELECT value FROM settings WHERE key = ?1;", .read) { statement in
            try statement.bind(retentionSettingKey, at: 1)
            guard try statement.step() else {
                return .notChosen
            }
            return HistoryRetentionSetting(storedValue: statement.text(at: 0))
        }
    }

    private static func writeRetention(_ retention: HistoryRetention, _ session: SQLiteSession) throws {
        let upsert = "INSERT OR REPLACE INTO settings(key, value) VALUES (?1, ?2);"
        try session.withStatement(upsert, .write) { statement in
            try statement.bind(retentionSettingKey, at: 1)
            try statement.bind(String(retention.storedDays), at: 2)
            try statement.run()
        }
    }

    // MARK: - Housekeeping (StorageMaintenance)

    /// Whether a foreground caller is waiting for the connection right now.
    var hasForegroundWaiters: Bool { owner.hasForegroundWaiters }

    /// Counts foreground uses of the connection, so maintenance can tell whether the app has been
    /// quiet since it last looked.
    var foregroundActivityCount: UInt64 { owner.foregroundActivityCount }

    func pageStats() throws -> PersistencePageStats {
        try owner.withSession(.maintenance) { session in
            let pageSize = try session.scalarInt64("PRAGMA page_size;", .maintenance)
            let pageCount = try session.scalarInt64("PRAGMA page_count;", .maintenance)
            let freePages = try session.scalarInt64("PRAGMA freelist_count;", .maintenance)
            let autoVacuum = try session.scalarInt64("PRAGMA auto_vacuum;", .maintenance)
            return PersistencePageStats(
                pageSize: pageSize, pageCount: pageCount, freePages: freePages, autoVacuum: autoVacuum)
        }
    }

    /// Runs one heavy housekeeping statement (VACUUM, incremental_vacuum) that gets out of the way. A
    /// progress handler interrupts it as soon as a foreground caller is waiting for the connection or
    /// `shouldStop` answers true, and SQLite rolls the interrupted statement back, so nothing is left
    /// half done. SQLite cannot interrupt VACUUM's final copy back into the database file, so a caller
    /// arriving in exactly that window still waits for it; for a text-only database that is brief.
    /// - Parameter prelude: runs first under the same hold of the connection but outside the progress
    ///   handler, for setup such as the `auto_vacuum` pragma a VACUUM consumes.
    func runYieldingMaintenance(
        _ sql: String,
        prelude: String? = nil,
        shouldStop: @escaping @Sendable () -> Bool
    ) throws -> YieldingStatementOutcome {
        try owner.withSession(.maintenance) { session in
            if let prelude {
                try session.execute(prelude, .maintenance)
            }
            return try owner.runYielding(sql, in: session, shouldStop: shouldStop)
        }
    }

    /// Copies the WAL back into the database and truncates it, so deleted history leaves the WAL file
    /// too. The short busy timeout makes a reader in another process that still needs old frames end
    /// this attempt quickly (a later pass retries) instead of holding the connection. Returns whether
    /// the checkpoint completed.
    func checkpointWal() throws -> Bool {
        try owner.withSession(.maintenance) { session in
            sqlite3_busy_timeout(session.db, 250)
            defer { sqlite3_busy_timeout(session.db, self.owner.busyTimeoutMilliseconds) }

            var logFrames: Int32 = 0
            var checkpointedFrames: Int32 = 0
            let code = sqlite3_wal_checkpoint_v2(
                session.db, nil, SQLITE_CHECKPOINT_TRUNCATE, &logFrames, &checkpointedFrames)
            // Extended result codes are on, so compare the primary code: a busy reader can surface as
            // one of SQLITE_BUSY's extended forms.
            switch code & 0xFF {
            case SQLITE_OK:
                return true
            case SQLITE_BUSY, SQLITE_LOCKED:
                return false
            default:
                throw PersistenceError.sqlite(operation: .maintenance, code: code)
            }
        }
    }

    /// Closes the connection; the next call reopens it. For tests that inspect the files directly.
    func closeConnection() {
        owner.close()
    }

    // MARK: - Dictionary entries
    //
    // The synchronous forms serve the CLI verbs, the tests and code already off the main actor.
    // Main-actor callers use the asynchronous `load`, `add`, `save` and `remove` forms. Every decision
    // that depends on what is stored (an import's merge, whether a learned or added term is new) is
    // made inside one transaction here, never against rows a Settings tab happens to have on screen,
    // which may not have loaded yet.

    func insertDictionaryEntry(_ entry: DictionaryEntry) throws -> Int64 {
        try owner.withSession(.foreground) { session in
            try Self.insertDictionaryEntry(entry, in: session)
        }
    }

    /// `insertDictionaryEntry(_:)` for main-actor callers.
    func addDictionaryEntry(_ entry: DictionaryEntry) async throws -> Int64 {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.insertDictionaryEntry(entry, in: session)
        }
    }

    /// Adds each of `entries` whose spoken form (trimmed, ignoring case) no stored rule has and no earlier
    /// entry in the batch repeats, deciding and writing in one transaction. Returns what it added, with
    /// the new ids.
    func addDictionaryEntriesIfAbsent(_ entries: [DictionaryEntry]) async throws -> [DictionaryEntry] {
        guard !entries.isEmpty else {
            return []
        }
        return try await owner.withSessionAsync(.foreground) { session in
            try session.transaction(.write) { () -> [DictionaryEntry] in
                let stored = try Self.readDictionaryEntries(enabledOnly: false, session)
                var known = Set(stored.map { Self.spokenFormKey($0.pattern) })
                var added: [DictionaryEntry] = []
                for entry in entries {
                    let key = Self.spokenFormKey(entry.pattern)
                    guard !key.isEmpty, known.insert(key).inserted else {
                        continue
                    }
                    let id = try Self.insertDictionaryEntry(entry, in: session)
                    added.append(
                        DictionaryEntry(
                            id: id,
                            pattern: entry.pattern,
                            replacement: entry.replacement,
                            wholeWord: entry.wholeWord,
                            enabled: entry.enabled))
                }
                return added
            }
        }
    }

    func fetchEnabledDictionaryEntries() throws -> [DictionaryEntry] {
        try fetchDictionaryEntries(enabledOnly: true)
    }

    /// Returns every dictionary entry, including disabled ones, for display in the Settings UI
    /// (where the user needs to see and re-enable disabled rows, not just what's actively applied).
    func fetchAllDictionaryEntries() throws -> [DictionaryEntry] {
        try fetchDictionaryEntries(enabledOnly: false)
    }

    /// `fetchAllDictionaryEntries()` for main-actor callers.
    func loadAllDictionaryEntries() async throws -> [DictionaryEntry] {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.readDictionaryEntries(enabledOnly: false, session)
        }
    }

    func setDictionaryEntryEnabled(id: Int64, enabled: Bool) throws {
        try owner.withSession(.foreground) { session in
            try Self.setDictionaryEntryEnabled(id: id, enabled: enabled, in: session)
        }
    }

    /// `setDictionaryEntryEnabled(id:enabled:)` for main-actor callers.
    func saveDictionaryEntryEnabled(id: Int64, enabled: Bool) async throws {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.setDictionaryEntryEnabled(id: id, enabled: enabled, in: session)
        }
    }

    /// Turns every entry in `ids` off in one transaction, so a Clean Up applies whole or not at all.
    func disableDictionaryEntries(ids: Set<Int64>) async throws {
        guard !ids.isEmpty else {
            return
        }
        let ordered = ids.sorted()
        try await owner.withSessionAsync(.foreground) { session in
            try session.transaction(.write) {
                for id in ordered {
                    try Self.setDictionaryEntryEnabled(id: id, enabled: false, in: session)
                }
            }
        }
    }

    /// Full-row update, used where an existing entry's replacement, whole-word flag or enabled state
    /// changed but its id and pattern are kept (Quick Add).
    func updateDictionaryEntry(_ entry: DictionaryEntry) throws {
        try owner.withSession(.foreground) { session in
            try Self.updateDictionaryEntry(entry, in: session)
        }
    }

    /// `updateDictionaryEntry(_:)` for main-actor callers.
    func saveDictionaryEntry(_ entry: DictionaryEntry) async throws {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.updateDictionaryEntry(entry, in: session)
        }
    }

    func deleteDictionaryEntry(id: Int64) throws {
        try owner.withSession(.foreground) { session in
            try Self.deleteDictionaryEntry(id: id, in: session)
        }
    }

    /// `deleteDictionaryEntry(id:)` for main-actor callers.
    func removeDictionaryEntry(id: Int64) async throws {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.deleteDictionaryEntry(id: id, in: session)
        }
    }

    /// Persists planned dictionary changes in one transaction, so either every added and updated row
    /// lands or none does. An update that no longer matches a row fails the whole batch rather than
    /// reporting a change that never happened.
    func applyDictionaryChanges(inserts: [DictionaryEntry], updates: [DictionaryEntry]) throws {
        guard !inserts.isEmpty || !updates.isEmpty else {
            return
        }
        try owner.withSession(.foreground) { session in
            try session.transaction(.write) {
                try Self.writeDictionaryChanges(inserts: inserts, updates: updates, session)
            }
        }
    }

    /// Imports parsed CSV rows against the dictionary as it is stored at that moment: the stored rows
    /// are read, merged with `imported` by spoken form (`DictionaryImportMerger`) and the result is
    /// written, all in one `BEGIN IMMEDIATE` transaction. So an import never plans against rows a
    /// Settings tab has not loaded yet or against rows an earlier import has just changed, and nothing
    /// can write in between. All or nothing.
    func importDictionary(_ imported: [DictionaryEntry]) async throws -> DictionaryImportSummary {
        try await owner.withSessionAsync(.foreground) { session in
            try session.transaction(.write) { () -> DictionaryImportSummary in
                try Self.importDictionary(imported, session)
            }
        }
    }

    /// Mines recent history for recurring terms and adds the ones no stored rule covers. History and
    /// rules are read together, the mining runs off the storage queue, and the additions are checked
    /// again against what is stored in the transaction that writes them
    /// (`addDictionaryEntriesIfAbsent`), so a rule a Settings tab has not loaded is never added twice.
    /// Returns what it added.
    func learnDictionaryEntries(
        historyLimit: Int = PersistenceStore.defaultHistoryReadLimit
    ) async throws -> [DictionaryEntry] {
        let inputs = try await loadDictionaryReviewInputs(historyLimit: historyLimit)
        let candidates = DictionaryHistoryLearner.buildEntries(history: inputs.history, existing: inputs.entries)
        return try await addDictionaryEntriesIfAbsent(candidates)
    }

    /// The newest `historyLimit` dictations and every stored rule, read in one visit to the storage
    /// queue, for mining and the dictionary Clean Up review.
    func loadDictionaryReviewInputs(
        historyLimit: Int = PersistenceStore.defaultHistoryReadLimit
    ) async throws -> DictionaryReviewInputs {
        try await owner.withSessionAsync(.foreground) { session in
            let history = try Self.readDictationHistory(session, since: nil, limit: historyLimit)
            let entries = try Self.readDictionaryEntries(enabledOnly: false, session)
            return DictionaryReviewInputs(history: history, entries: entries)
        }
    }

    private static func importDictionary(
        _ imported: [DictionaryEntry],
        _ session: SQLiteSession
    ) throws -> DictionaryImportSummary {
        let stored = try readDictionaryEntries(enabledOnly: false, session)
        let existing = stored.enumerated().map { index, entry in
            DictionaryImportMerger.ExistingRow(
                index: index,
                id: entry.id,
                pattern: entry.pattern,
                replacement: entry.replacement,
                wholeWord: entry.wholeWord,
                enabled: entry.enabled)
        }
        let plan = DictionaryImportMerger.merge(existing: existing, imported: imported)
        let changes = DictionaryImportMerger.changes(applying: plan, to: existing)
        try writeDictionaryChanges(inserts: changes.inserts, updates: changes.updates, session)
        return DictionaryImportSummary(added: plan.added, updated: plan.updated, unchanged: plan.unchanged)
    }

    /// Writes planned changes. The caller holds the transaction.
    private static func writeDictionaryChanges(
        inserts: [DictionaryEntry],
        updates: [DictionaryEntry],
        _ session: SQLiteSession
    ) throws {
        for entry in inserts {
            _ = try insertDictionaryEntry(entry, in: session)
        }
        for entry in updates {
            try updateDictionaryEntry(entry, in: session)
            guard session.changes == 1 else {
                throw PersistenceError.rowMissing(operation: .write)
            }
        }
    }

    /// The duplicate rule the import merger and the learner use: a spoken form, trimmed, ignoring case.
    private static func spokenFormKey(_ pattern: String) -> String {
        pattern.trimmingCharacters(in: .whitespaces).lowercased()
    }

    private func fetchDictionaryEntries(enabledOnly: Bool) throws -> [DictionaryEntry] {
        try owner.withSession(.foreground) { session in
            try Self.readDictionaryEntries(enabledOnly: enabledOnly, session)
        }
    }

    private static func readDictionaryEntries(enabledOnly: Bool, _ session: SQLiteSession) throws -> [DictionaryEntry] {
        let sql = enabledOnly
            ? "SELECT id, pattern, replacement, whole_word, enabled FROM dictionary_entries WHERE enabled = 1 ORDER BY id;"
            : "SELECT id, pattern, replacement, whole_word, enabled FROM dictionary_entries ORDER BY id;"
        return try session.withStatement(sql, .read) { statement in
            var entries: [DictionaryEntry] = []
            while try statement.step() {
                entries.append(
                    DictionaryEntry(
                        id: statement.int64(at: 0) ?? 0,
                        pattern: statement.text(at: 1) ?? "",
                        replacement: statement.text(at: 2) ?? "",
                        wholeWord: statement.bool(at: 3),
                        enabled: statement.bool(at: 4)))
            }
            return entries
        }
    }

    private static func insertDictionaryEntry(_ entry: DictionaryEntry, in session: SQLiteSession) throws -> Int64 {
        try session.withStatement(
            "INSERT INTO dictionary_entries(pattern, replacement, whole_word, enabled) VALUES (?1, ?2, ?3, ?4);",
            .write
        ) { statement in
            try statement.bind(entry.pattern, at: 1)
            try statement.bind(entry.replacement, at: 2)
            try statement.bind(entry.wholeWord, at: 3)
            try statement.bind(entry.enabled, at: 4)
            try statement.run()
            return session.lastInsertRowID
        }
    }

    private static func updateDictionaryEntry(_ entry: DictionaryEntry, in session: SQLiteSession) throws {
        try session.withStatement(
            "UPDATE dictionary_entries SET pattern = ?1, replacement = ?2, whole_word = ?3, enabled = ?4 WHERE id = ?5;",
            .write
        ) { statement in
            try statement.bind(entry.pattern, at: 1)
            try statement.bind(entry.replacement, at: 2)
            try statement.bind(entry.wholeWord, at: 3)
            try statement.bind(entry.enabled, at: 4)
            try statement.bind(entry.id, at: 5)
            try statement.run()
        }
    }

    private static func setDictionaryEntryEnabled(id: Int64, enabled: Bool, in session: SQLiteSession) throws {
        try runUpdate("UPDATE dictionary_entries SET enabled = ?1 WHERE id = ?2;", in: session) { statement in
            try statement.bind(enabled, at: 1)
            try statement.bind(id, at: 2)
        }
    }

    private static func deleteDictionaryEntry(id: Int64, in session: SQLiteSession) throws {
        try runUpdate("DELETE FROM dictionary_entries WHERE id = ?1;", in: session) { statement in
            try statement.bind(id, at: 1)
        }
    }

    // MARK: - Snippets

    func insertSnippet(_ snippet: Snippet) throws -> Int64 {
        try owner.withSession(.foreground) { session in
            try Self.insertSnippet(snippet, in: session)
        }
    }

    /// `insertSnippet(_:)` for main-actor callers.
    func addSnippet(_ snippet: Snippet) async throws -> Int64 {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.insertSnippet(snippet, in: session)
        }
    }

    func fetchEnabledSnippets() throws -> [Snippet] {
        try fetchSnippets(enabledOnly: true)
    }

    /// Returns every snippet, including disabled ones, for the Settings UI.
    func fetchAllSnippets() throws -> [Snippet] {
        try fetchSnippets(enabledOnly: false)
    }

    /// `fetchAllSnippets()` for main-actor callers.
    func loadAllSnippets() async throws -> [Snippet] {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.readSnippets(enabledOnly: false, session)
        }
    }

    func setSnippetEnabled(id: Int64, enabled: Bool) throws {
        try owner.withSession(.foreground) { session in
            try Self.setSnippetEnabled(id: id, enabled: enabled, in: session)
        }
    }

    /// `setSnippetEnabled(id:enabled:)` for main-actor callers.
    func saveSnippetEnabled(id: Int64, enabled: Bool) async throws {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.setSnippetEnabled(id: id, enabled: enabled, in: session)
        }
    }

    func deleteSnippet(id: Int64) throws {
        try owner.withSession(.foreground) { session in
            try Self.deleteSnippet(id: id, in: session)
        }
    }

    /// `deleteSnippet(id:)` for main-actor callers.
    func removeSnippet(id: Int64) async throws {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.deleteSnippet(id: id, in: session)
        }
    }

    private func fetchSnippets(enabledOnly: Bool) throws -> [Snippet] {
        try owner.withSession(.foreground) { session in
            try Self.readSnippets(enabledOnly: enabledOnly, session)
        }
    }

    private static func readSnippets(enabledOnly: Bool, _ session: SQLiteSession) throws -> [Snippet] {
        let sql = enabledOnly
            ? "SELECT id, phrase, template, enabled FROM snippets WHERE enabled = 1 ORDER BY id;"
            : "SELECT id, phrase, template, enabled FROM snippets ORDER BY id;"
        return try session.withStatement(sql, .read) { statement in
            var snippets: [Snippet] = []
            while try statement.step() {
                snippets.append(
                    Snippet(
                        id: statement.int64(at: 0) ?? 0,
                        phrase: statement.text(at: 1) ?? "",
                        template: statement.text(at: 2) ?? "",
                        enabled: statement.bool(at: 3)))
            }
            return snippets
        }
    }

    private static func insertSnippet(_ snippet: Snippet, in session: SQLiteSession) throws -> Int64 {
        let insert = "INSERT INTO snippets(phrase, template, enabled) VALUES (?1, ?2, ?3);"
        return try session.withStatement(insert, .write) { statement in
            try statement.bind(snippet.phrase, at: 1)
            try statement.bind(snippet.template, at: 2)
            try statement.bind(snippet.enabled, at: 3)
            try statement.run()
            return session.lastInsertRowID
        }
    }

    private static func setSnippetEnabled(id: Int64, enabled: Bool, in session: SQLiteSession) throws {
        try runUpdate("UPDATE snippets SET enabled = ?1 WHERE id = ?2;", in: session) { statement in
            try statement.bind(enabled, at: 1)
            try statement.bind(id, at: 2)
        }
    }

    private static func deleteSnippet(id: Int64, in session: SQLiteSession) throws {
        try runUpdate("DELETE FROM snippets WHERE id = ?1;", in: session) { statement in
            try statement.bind(id, at: 1)
        }
    }

    // MARK: - App profiles

    func insertAppProfile(_ profile: AppProfile) throws -> Int64 {
        try owner.withSession(.foreground) { session in
            try Self.insertAppProfile(profile, in: session)
        }
    }

    /// `insertAppProfile(_:)` for main-actor callers.
    func addAppProfile(_ profile: AppProfile) async throws -> Int64 {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.insertAppProfile(profile, in: session)
        }
    }

    func fetchAppProfiles() throws -> [AppProfile] {
        try owner.withSession(.foreground) { session in
            try Self.readAppProfiles(session)
        }
    }

    /// `fetchAppProfiles()` for main-actor callers.
    func loadAppProfiles() async throws -> [AppProfile] {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.readAppProfiles(session)
        }
    }

    func deleteAppProfile(id: Int64) throws {
        try owner.withSession(.foreground) { session in
            try Self.deleteAppProfile(id: id, in: session)
        }
    }

    /// `deleteAppProfile(id:)` for main-actor callers.
    func removeAppProfile(id: Int64) async throws {
        try await owner.withSessionAsync(.foreground) { session in
            try Self.deleteAppProfile(id: id, in: session)
        }
    }

    private static func insertAppProfile(_ profile: AppProfile, in session: SQLiteSession) throws -> Int64 {
        try session.withStatement(
            """
            INSERT INTO app_profiles(name, bundle_identifiers, process_names, writing_style_prompt, newline_handling)
            VALUES (?1, ?2, ?3, ?4, ?5);
            """,
            .write
        ) { statement in
            try statement.bind(profile.name, at: 1)
            try statement.bind(profile.bundleIdentifiers.joined(separator: ","), at: 2)
            try statement.bind(profile.processNames.joined(separator: ","), at: 3)
            try statement.bind(profile.writingStylePrompt, at: 4)
            try statement.bind(profile.newlineHandling?.rawValue, at: 5)
            try statement.run()
            return session.lastInsertRowID
        }
    }

    private static func readAppProfiles(_ session: SQLiteSession) throws -> [AppProfile] {
        try session.withStatement(
            """
            SELECT id, name, bundle_identifiers, process_names, writing_style_prompt, newline_handling
            FROM app_profiles
            ORDER BY id;
            """,
            .read
        ) { statement in
            var profiles: [AppProfile] = []
            while try statement.step() {
                profiles.append(
                    AppProfile(
                        id: statement.int64(at: 0) ?? 0,
                        name: statement.text(at: 1) ?? "",
                        bundleIdentifiers: splitList(statement.text(at: 2)),
                        processNames: splitList(statement.text(at: 3)),
                        writingStylePrompt: statement.text(at: 4),
                        newlineHandling: statement.text(at: 5).flatMap { NewlineInjectionMode(rawValue: $0) }))
            }
            return profiles
        }
    }

    private static func deleteAppProfile(id: Int64, in session: SQLiteSession) throws {
        try runUpdate("DELETE FROM app_profiles WHERE id = ?1;", in: session) { statement in
            try statement.bind(id, at: 1)
        }
    }

    private static func splitList(_ text: String?) -> [String] {
        (text ?? "").split(separator: ",").map(String.init).filter { !$0.isEmpty }
    }

    // MARK: - Rule set

    /// What every dictation applies (the enabled dictionary rules, the enabled snippets and the app
    /// profiles), read in one visit to the storage queue. For main-actor callers.
    func loadRuleSet() async throws -> PersistenceRuleSet {
        try await owner.withSessionAsync(.foreground) { session in
            let dictionaryEntries = try Self.readDictionaryEntries(enabledOnly: true, session)
            let snippets = try Self.readSnippets(enabledOnly: true, session)
            let appProfiles = try Self.readAppProfiles(session)
            return PersistenceRuleSet(dictionaryEntries: dictionaryEntries, snippets: snippets, appProfiles: appProfiles)
        }
    }

    /// One-shot UPDATE or DELETE that binds parameters and returns no rows.
    private static func runUpdate(
        _ sql: String,
        in session: SQLiteSession,
        bind: (SQLiteStatement) throws -> Void
    ) throws {
        try session.withStatement(sql, .write) { statement in
            try bind(statement)
            try statement.run()
        }
    }
}

// MARK: - Connection ownership

/// The one SQLite connection and the state that may only be touched while holding it.
///
/// `@unchecked Sendable` because the compiler cannot see the confinement. `handle` and `timestamps`
/// are only used on `queue`, a serial queue, so by one operation at a time. The queue is FIFO, so a
/// foreground caller that arrives while housekeeping runs goes next rather than competing with the
/// housekeeping's next step. `waiters` and `activity` are only used while `countersLock` is held; they
/// have their own lock because a heavy housekeeping statement polls them from its progress handler
/// while it runs on `queue`.
///
/// No re-entrancy: nothing that runs on `queue` (an operation's body, the progress handler, a test
/// hook) may call back into `PersistenceStore` or this owner. The queue is serial, so a nested call
/// would wait for itself; libdispatch traps on a nested `sync` rather than deadlocking quietly, and
/// the helpers below take the `SQLiteSession` they need instead of reaching for the store.
private final class ConnectionOwner: @unchecked Sendable {
    let path: String
    let busyTimeoutMilliseconds: Int32
    let progressInterval: Int32

    private let hooks: PersistenceStore.TestHooks
    private let logger = Logger(subsystem: "com.scribe.macos", category: "Persistence")
    private let queue = DispatchQueue(label: "com.scribe.macos.persistence")
    private var handle: OpaquePointer?
    private let timestamps: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()

    private let countersLock = NSLock()
    private var waiters = 0
    private var activity: UInt64 = 0

    init(path: String, busyTimeoutMilliseconds: Int32, hooks: PersistenceStore.TestHooks) {
        self.path = path
        self.busyTimeoutMilliseconds = busyTimeoutMilliseconds
        self.progressInterval = max(1, hooks.progressInterval)
        self.hooks = hooks
    }

    deinit {
        if let handle {
            sqlite3_close_v2(handle)
        }
    }

    var hasForegroundWaiters: Bool {
        countersLock.lock()
        defer { countersLock.unlock() }
        return waiters > 0
    }

    var foregroundActivityCount: UInt64 {
        countersLock.lock()
        defer { countersLock.unlock() }
        return activity
    }

    /// Runs `body` on the storage queue and waits for it on the calling thread.
    func withSession<T>(_ purpose: ConnectionPurpose, _ body: (SQLiteSession) throws -> T) throws -> T {
        registerWaiter(purpose)
        return try queue.sync {
            try runSession(purpose, body)
        }
    }

    /// Runs `body` on the storage queue without holding the calling thread: the caller suspends until
    /// the queue gets to it and resumes on its own executor, so a main-actor caller never waits behind
    /// a write that is waiting out another process's lock.
    func withSessionAsync<T: Sendable>(
        _ purpose: ConnectionPurpose,
        _ body: @escaping @Sendable (SQLiteSession) throws -> T
    ) async throws -> T {
        registerWaiter(purpose)
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<T, Error>) in
            queue.async { [self] in
                continuation.resume(with: Result { try runSession(purpose, body) })
            }
        }
    }

    func close() {
        queue.sync {
            if let handle {
                sqlite3_close_v2(handle)
                self.handle = nil
            }
        }
    }

    private func registerWaiter(_ purpose: ConnectionPurpose) {
        guard purpose == .foreground else {
            return
        }
        countersLock.lock()
        waiters += 1
        countersLock.unlock()
        hooks.onForegroundWait?()
    }

    /// Runs on `queue`.
    private func runSession<T>(_ purpose: ConnectionPurpose, _ body: (SQLiteSession) throws -> T) throws -> T {
        if purpose == .foreground {
            countersLock.lock()
            waiters -= 1
            activity &+= 1
            countersLock.unlock()
        }
        hooks.onOperationBegin?(purpose)

        let db = try openIfNeeded()
        return try body(SQLiteSession(db: db, timestamps: timestamps))
    }

    /// Runs on `queue` (through `withSession`).
    func runYielding(
        _ sql: String,
        in session: SQLiteSession,
        shouldStop: @escaping @Sendable () -> Bool
    ) throws -> YieldingStatementOutcome {
        let gate = ProgressGate(owner: self, shouldStop: shouldStop)
        let code: Int32 = withExtendedLifetime(gate) {
            sqlite3_progress_handler(
                session.db,
                progressInterval,
                { context in
                    guard let context else {
                        return 0
                    }
                    return Unmanaged<ProgressGate>.fromOpaque(context).takeUnretainedValue().shouldInterrupt() ? 1 : 0
                },
                Unmanaged.passUnretained(gate).toOpaque())
            defer { sqlite3_progress_handler(session.db, 0, nil, nil) }
            return sqlite3_exec(session.db, sql, nil, nil, nil)
        }

        switch code & 0xFF {
        case SQLITE_OK:
            return .completed
        case SQLITE_INTERRUPT:
            return gate.interruptedForForeground ? .yieldedToForeground : .stopped
        default:
            throw PersistenceError.sqlite(operation: .maintenance, code: code)
        }
    }

    /// Runs inside `queue`.
    private func openIfNeeded() throws -> OpaquePointer {
        if let handle {
            return handle
        }

        var opened: OpaquePointer?
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX
        let code = sqlite3_open_v2(path, &opened, flags, nil)
        guard code == SQLITE_OK, let opened else {
            if let opened {
                sqlite3_close_v2(opened)
            }
            throw PersistenceError.sqlite(operation: .open, code: code)
        }

        do {
            let journalMode = try Self.configure(opened, busyTimeoutMilliseconds: busyTimeoutMilliseconds, timestamps: timestamps)
            if journalMode.lowercased() == "wal" {
                logger.info("Opened the database in WAL mode.")
            } else {
                // Reported by name only: a mode SQLite itself chose, such as "delete" on a volume
                // without shared memory support.
                logger.warning("Opened the database, but it stayed in \(journalMode, privacy: .public) journal mode.")
            }
        } catch {
            sqlite3_close_v2(opened)
            throw error
        }

        handle = opened
        return opened
    }

    private static func configure(
        _ db: OpaquePointer,
        busyTimeoutMilliseconds: Int32,
        timestamps: ISO8601DateFormatter
    ) throws -> String {
        sqlite3_extended_result_codes(db, 1)
        sqlite3_busy_timeout(db, busyTimeoutMilliseconds)

        let session = SQLiteSession(db: db, timestamps: timestamps)

        // auto_vacuum can only leave NONE while a database has no pages, and journal_mode=WAL writes
        // the first one, so a brand-new file takes incremental mode here, first. StorageMaintenance
        // converts an older file once, with a VACUUM.
        if try session.scalarInt64("PRAGMA page_count;", .open) == 0 {
            try session.execute("PRAGMA auto_vacuum = INCREMENTAL;", .open)
        }

        let journalMode = try session.scalarText("PRAGMA journal_mode = WAL;", .open) ?? "unknown"

        // FULL is one fsync per commit in WAL mode. History writes are rare and small, and a dictation
        // that reached the user's document should survive a power cut, not only a crash.
        try session.execute("PRAGMA synchronous = FULL;", .open)
        return journalMode
    }
}

/// Decides, from inside SQLite's progress handler, whether a heavy statement must stop. Lives only
/// for one statement and is only touched on the thread running it.
private final class ProgressGate {
    private let owner: ConnectionOwner
    private let shouldStop: @Sendable () -> Bool
    private(set) var interruptedForForeground = false

    init(owner: ConnectionOwner, shouldStop: @escaping @Sendable () -> Bool) {
        self.owner = owner
        self.shouldStop = shouldStop
    }

    func shouldInterrupt() -> Bool {
        if owner.hasForegroundWaiters {
            interruptedForForeground = true
            return true
        }
        return shouldStop()
    }
}

// MARK: - Statement helpers

/// Everything a statement needs from the connection. Only exists inside `ConnectionOwner.withSession`.
private struct SQLiteSession {
    let db: OpaquePointer
    let timestamps: ISO8601DateFormatter

    var changes: Int { Int(sqlite3_changes(db)) }

    var lastInsertRowID: Int64 { sqlite3_last_insert_rowid(db) }

    func timestamp(_ date: Date) -> String {
        timestamps.string(from: date)
    }

    func date(from text: String) -> Date? {
        timestamps.date(from: text)
    }

    func execute(_ sql: String, _ operation: PersistenceOperation) throws {
        let code = sqlite3_exec(db, sql, nil, nil, nil)
        guard code == SQLITE_OK else {
            throw PersistenceError.sqlite(operation: operation, code: code)
        }
    }

    func withStatement<T>(
        _ sql: String,
        _ operation: PersistenceOperation,
        _ body: (SQLiteStatement) throws -> T
    ) throws -> T {
        var handle: OpaquePointer?
        let code = sqlite3_prepare_v2(db, sql, -1, &handle, nil)
        guard code == SQLITE_OK, let handle else {
            sqlite3_finalize(handle)
            throw PersistenceError.sqlite(operation: operation, code: code)
        }
        defer { sqlite3_finalize(handle) }
        return try body(SQLiteStatement(handle: handle, operation: operation))
    }

    /// Runs `body` in one `BEGIN IMMEDIATE` transaction: the write lock is taken up front, so a
    /// concurrent writer in another process makes this wait at the start (bounded by the busy
    /// timeout) instead of failing half way through.
    func transaction<T>(_ operation: PersistenceOperation, _ body: () throws -> T) throws -> T {
        try execute("BEGIN IMMEDIATE;", operation)
        do {
            let result = try body()
            try execute("COMMIT;", operation)
            return result
        } catch {
            // After some failures SQLite has already rolled back; the extra ROLLBACK is then a no-op.
            sqlite3_exec(db, "ROLLBACK;", nil, nil, nil)
            throw error
        }
    }

    func scalarInt64(_ sql: String, _ operation: PersistenceOperation) throws -> Int64 {
        try withStatement(sql, operation) { statement in
            guard try statement.step() else {
                return 0
            }
            return statement.int64(at: 0) ?? 0
        }
    }

    func scalarText(_ sql: String, _ operation: PersistenceOperation) throws -> String? {
        try withStatement(sql, operation) { statement in
            guard try statement.step() else {
                return nil
            }
            return statement.text(at: 0)
        }
    }

    func tableExists(_ name: String, _ operation: PersistenceOperation) throws -> Bool {
        try withStatement("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?1;", operation) { statement in
            try statement.bind(name, at: 1)
            return try statement.step()
        }
    }

    /// Column names of a table in this schema; `table` is always one of the store's own names.
    func columnNames(of table: String, _ operation: PersistenceOperation) throws -> Set<String> {
        try withStatement("PRAGMA table_info(\(table));", operation) { statement in
            var names: Set<String> = []
            while try statement.step() {
                if let name = statement.text(at: 1) {
                    names.insert(name)
                }
            }
            return names
        }
    }
}

private struct SQLiteStatement {
    let handle: OpaquePointer
    let operation: PersistenceOperation

    /// Steps once: true for a row, false once the statement is done. Anything else (a lock, an I/O
    /// error, a constraint) throws, so an error part way through a read can never pass for its end.
    func step() throws -> Bool {
        let code = sqlite3_step(handle)
        switch code {
        case SQLITE_ROW:
            return true
        case SQLITE_DONE:
            return false
        default:
            throw PersistenceError.sqlite(operation: operation, code: code)
        }
    }

    /// Steps to completion.
    func run() throws {
        while try step() {}
    }

    func bind(_ value: String?, at index: Int32) throws {
        let code: Int32
        if let value {
            // The byte count rather than -1, so text containing a NUL is stored whole.
            code = sqlite3_bind_text(handle, index, value, Int32(clamping: value.utf8.count), SQLITE_TRANSIENT)
        } else {
            code = sqlite3_bind_null(handle, index)
        }
        try check(code)
    }

    func bind(_ value: Double?, at index: Int32) throws {
        try check(value.map { sqlite3_bind_double(handle, index, $0) } ?? sqlite3_bind_null(handle, index))
    }

    func bind(_ value: Int64?, at index: Int32) throws {
        try check(value.map { sqlite3_bind_int64(handle, index, $0) } ?? sqlite3_bind_null(handle, index))
    }

    func bind(_ value: Bool, at index: Int32) throws {
        try check(sqlite3_bind_int64(handle, index, value ? 1 : 0))
    }

    func text(at column: Int32) -> String? {
        guard sqlite3_column_type(handle, column) != SQLITE_NULL,
              let pointer = sqlite3_column_text(handle, column)
        else {
            return nil
        }
        let count = Int(sqlite3_column_bytes(handle, column))
        return String(decoding: UnsafeRawBufferPointer(start: pointer, count: count), as: UTF8.self)
    }

    func double(at column: Int32) -> Double? {
        sqlite3_column_type(handle, column) == SQLITE_NULL ? nil : sqlite3_column_double(handle, column)
    }

    func int64(at column: Int32) -> Int64? {
        sqlite3_column_type(handle, column) == SQLITE_NULL ? nil : sqlite3_column_int64(handle, column)
    }

    func bool(at column: Int32) -> Bool {
        (int64(at: column) ?? 0) != 0
    }

    private func check(_ code: Int32) throws {
        guard code == SQLITE_OK else {
            throw PersistenceError.sqlite(operation: operation, code: code)
        }
    }
}
