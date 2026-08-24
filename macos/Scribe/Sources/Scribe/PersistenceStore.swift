import Foundation
import OSLog
import SQLite3

private let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

final class PersistenceStore {
    private let logger = Logger(subsystem: "com.scribe.macos", category: "Persistence")
    private let fileManager: FileManager
    private let iso8601Formatter: ISO8601DateFormatter

    let databaseURL: URL

    init(fileManager: FileManager = .default, databaseURL overrideDatabaseURL: URL? = nil) {
        self.fileManager = fileManager

        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        self.iso8601Formatter = formatter

        if let overrideDatabaseURL {
            self.databaseURL = overrideDatabaseURL
        } else {
            let applicationSupportURL = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            let scribeDirectoryURL = applicationSupportURL.appendingPathComponent("Scribe", isDirectory: true)
            self.databaseURL = scribeDirectoryURL.appendingPathComponent("scribe.db", isDirectory: false)
        }
    }

    func initialize() throws {
        try fileManager.createDirectory(
            at: databaseURL.deletingLastPathComponent(),
            withIntermediateDirectories: true)

        try withConnection { database in
            try execute(
                """
                CREATE TABLE IF NOT EXISTS dictation_history(
                    id INTEGER PRIMARY KEY,
                    started_at TEXT NOT NULL,
                    duration_seconds REAL NOT NULL,
                    sample_count INTEGER NOT NULL,
                    decode_ms REAL,
                    cleanup_ms REAL
                );
                """,
                database: database)

            // Older databases predate the decode/cleanup timing columns used by DictationStats
            // (the Diagnostics panel). SQLite has no "ADD COLUMN IF NOT EXISTS", so probe first.
            let existingColumns = try tableColumns("dictation_history", database: database)
            if !existingColumns.contains("decode_ms") {
                try execute("ALTER TABLE dictation_history ADD COLUMN decode_ms REAL;", database: database)
            }
            if !existingColumns.contains("cleanup_ms") {
                try execute("ALTER TABLE dictation_history ADD COLUMN cleanup_ms REAL;", database: database)
            }
            // Backs dictionary history-mining (DictionarySuggestionMiner): the final,
            // post-processed transcript text is what a recurring-jargon miner needs to see, and
            // older rows predate this column so it must be probed for like the timing columns.
            if !existingColumns.contains("transcript_text") {
                try execute("ALTER TABLE dictation_history ADD COLUMN transcript_text TEXT;", database: database)
            }

            try execute(
                """
                CREATE TABLE IF NOT EXISTS dictionary_entries(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    pattern TEXT NOT NULL,
                    replacement TEXT NOT NULL,
                    whole_word INTEGER NOT NULL DEFAULT 1,
                    enabled INTEGER NOT NULL DEFAULT 1
                );
                """,
                database: database)

            try execute(
                """
                CREATE TABLE IF NOT EXISTS snippets(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    phrase TEXT NOT NULL,
                    template TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1
                );
                """,
                database: database)

            try execute(
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
                database: database)
        }

        logger.info("SQLite store ready at \(self.databaseURL.path(percentEncoded: false), privacy: .public)")
    }

    func recordDictation(
        startedAt: Date,
        durationSeconds: Double,
        sampleCount: Int,
        decodeMilliseconds: Double? = nil,
        cleanupMilliseconds: Double? = nil,
        transcriptText: String? = nil
    ) throws {
        try withConnection { database in
            var statement: OpaquePointer?
            let prepareResult = sqlite3_prepare_v2(
                database,
                "INSERT INTO dictation_history(started_at, duration_seconds, sample_count, decode_ms, cleanup_ms, transcript_text) VALUES (?, ?, ?, ?, ?, ?);",
                -1,
                &statement,
                nil)

            guard prepareResult == SQLITE_OK, let statement else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }

            defer {
                sqlite3_finalize(statement)
            }

            let startedAtText = iso8601Formatter.string(from: startedAt)
            sqlite3_bind_text(statement, 1, startedAtText, -1, SQLITE_TRANSIENT)
            sqlite3_bind_double(statement, 2, durationSeconds)
            sqlite3_bind_int64(statement, 3, sqlite3_int64(sampleCount))
            if let decodeMilliseconds {
                sqlite3_bind_double(statement, 4, decodeMilliseconds)
            } else {
                sqlite3_bind_null(statement, 4)
            }
            if let cleanupMilliseconds {
                sqlite3_bind_double(statement, 5, cleanupMilliseconds)
            } else {
                sqlite3_bind_null(statement, 5)
            }
            if let transcriptText {
                sqlite3_bind_text(statement, 6, transcriptText, -1, SQLITE_TRANSIENT)
            } else {
                sqlite3_bind_null(statement, 6)
            }

            guard sqlite3_step(statement) == SQLITE_DONE else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
        }

        let startedAtText = iso8601Formatter.string(from: startedAt)
        logger.info(
            "Saved dictation history row for \(startedAtText, privacy: .public) with \(sampleCount) samples.")
    }

    /// Fetches dictation history rows for the Diagnostics panel (`DictationStats`). Rows with a
    /// null decode/cleanup time (older schema, or a run before this feature existed) surface as
    /// `nil`, matching the Windows `HistoryEntry` shape.
    func fetchDictationHistory() throws -> [DictationHistoryRecord] {
        var records: [DictationHistoryRecord] = []

        try withConnection { database in
            var statement: OpaquePointer?
            let prepareResult = sqlite3_prepare_v2(
                database,
                "SELECT started_at, duration_seconds, sample_count, decode_ms, cleanup_ms, transcript_text FROM dictation_history ORDER BY id ASC;",
                -1,
                &statement,
                nil)

            guard prepareResult == SQLITE_OK, let statement else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }

            defer {
                sqlite3_finalize(statement)
            }

            while sqlite3_step(statement) == SQLITE_ROW {
                guard let startedAtCString = sqlite3_column_text(statement, 0) else {
                    continue
                }
                let startedAtText = String(cString: startedAtCString)
                guard let startedAt = iso8601Formatter.date(from: startedAtText) else {
                    continue
                }

                let durationSeconds = sqlite3_column_double(statement, 1)
                let sampleCount = Int(sqlite3_column_int64(statement, 2))
                let decodeMs: Double? = sqlite3_column_type(statement, 3) == SQLITE_NULL
                    ? nil
                    : sqlite3_column_double(statement, 3)
                let cleanupMs: Double? = sqlite3_column_type(statement, 4) == SQLITE_NULL
                    ? nil
                    : sqlite3_column_double(statement, 4)
                let transcriptText: String? = sqlite3_column_type(statement, 5) == SQLITE_NULL
                    ? nil
                    : sqlite3_column_text(statement, 5).map { String(cString: $0) }

                records.append(
                    DictationHistoryRecord(
                        startedAt: startedAt,
                        durationSeconds: durationSeconds,
                        sampleCount: sampleCount,
                        decodeMilliseconds: decodeMs,
                        cleanupMilliseconds: cleanupMs,
                        transcriptText: transcriptText))
            }
        }

        return records
    }

    /// Returns the column names currently present on `table`, used to add missing columns to an
    /// existing SQLite database without a destructive migration (SQLite lacks
    /// `ADD COLUMN IF NOT EXISTS`).
    private func tableColumns(_ table: String, database: OpaquePointer?) throws -> Set<String> {
        var columns: Set<String> = []
        var statement: OpaquePointer?
        let prepareResult = sqlite3_prepare_v2(database, "PRAGMA table_info(\(table));", -1, &statement, nil)
        guard prepareResult == SQLITE_OK, let statement else {
            throw PersistenceError.sqlite(message: sqliteMessage(from: database))
        }
        defer {
            sqlite3_finalize(statement)
        }

        while sqlite3_step(statement) == SQLITE_ROW {
            if let nameCString = sqlite3_column_text(statement, 1) {
                columns.insert(String(cString: nameCString))
            }
        }

        return columns
    }

    // MARK: - Dictionary entries

    func insertDictionaryEntry(_ entry: DictionaryEntry) throws -> Int64 {
        var insertedID: Int64 = 0
        try withConnection { database in
            var statement: OpaquePointer?
            let prepareResult = sqlite3_prepare_v2(
                database,
                "INSERT INTO dictionary_entries(pattern, replacement, whole_word, enabled) VALUES (?, ?, ?, ?);",
                -1,
                &statement,
                nil)
            guard prepareResult == SQLITE_OK, let statement else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
            defer { sqlite3_finalize(statement) }

            sqlite3_bind_text(statement, 1, entry.pattern, -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(statement, 2, entry.replacement, -1, SQLITE_TRANSIENT)
            sqlite3_bind_int(statement, 3, entry.wholeWord ? 1 : 0)
            sqlite3_bind_int(statement, 4, entry.enabled ? 1 : 0)

            guard sqlite3_step(statement) == SQLITE_DONE else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
            insertedID = sqlite3_last_insert_rowid(database)
        }
        logger.info("Inserted dictionary entry \(insertedID): '\(entry.pattern, privacy: .private)'")
        return insertedID
    }

    func fetchEnabledDictionaryEntries() throws -> [DictionaryEntry] {
        try fetchDictionaryEntries(enabledOnly: true)
    }

    /// Returns every dictionary entry, including disabled ones, for display in the Settings UI
    /// (where the user needs to see and re-enable disabled rows, not just what's actively applied).
    func fetchAllDictionaryEntries() throws -> [DictionaryEntry] {
        try fetchDictionaryEntries(enabledOnly: false)
    }

    private func fetchDictionaryEntries(enabledOnly: Bool) throws -> [DictionaryEntry] {
        var results: [DictionaryEntry] = []
        try withConnection { database in
            var statement: OpaquePointer?
            let sql = enabledOnly
                ? "SELECT id, pattern, replacement, whole_word, enabled FROM dictionary_entries WHERE enabled = 1 ORDER BY id;"
                : "SELECT id, pattern, replacement, whole_word, enabled FROM dictionary_entries ORDER BY id;"
            let prepareResult = sqlite3_prepare_v2(database, sql, -1, &statement, nil)
            guard prepareResult == SQLITE_OK, let statement else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
            defer { sqlite3_finalize(statement) }

            while sqlite3_step(statement) == SQLITE_ROW {
                let id = sqlite3_column_int64(statement, 0)
                let pattern = String(cString: sqlite3_column_text(statement, 1))
                let replacement = String(cString: sqlite3_column_text(statement, 2))
                let wholeWord = sqlite3_column_int(statement, 3) != 0
                let enabled = sqlite3_column_int(statement, 4) != 0
                results.append(DictionaryEntry(id: id, pattern: pattern, replacement: replacement, wholeWord: wholeWord, enabled: enabled))
            }
        }
        return results
    }

    func setDictionaryEntryEnabled(id: Int64, enabled: Bool) throws {
        try executeStatement("UPDATE dictionary_entries SET enabled = ? WHERE id = ?;") { statement in
            sqlite3_bind_int(statement, 1, enabled ? 1 : 0)
            sqlite3_bind_int64(statement, 2, id)
        }
    }

    /// Full-row update used by CSV import merges, where an existing entry's replacement,
    /// whole-word flag, or enabled state changed but its id and pattern are kept.
    func updateDictionaryEntry(_ entry: DictionaryEntry) throws {
        try executeStatement(
            "UPDATE dictionary_entries SET pattern = ?, replacement = ?, whole_word = ?, enabled = ? WHERE id = ?;"
        ) { statement in
            sqlite3_bind_text(statement, 1, entry.pattern, -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(statement, 2, entry.replacement, -1, SQLITE_TRANSIENT)
            sqlite3_bind_int(statement, 3, entry.wholeWord ? 1 : 0)
            sqlite3_bind_int(statement, 4, entry.enabled ? 1 : 0)
            sqlite3_bind_int64(statement, 5, entry.id)
        }
    }

    func deleteDictionaryEntry(id: Int64) throws {
        try executeStatement("DELETE FROM dictionary_entries WHERE id = ?;") { statement in
            sqlite3_bind_int64(statement, 1, id)
        }
    }

    // MARK: - Snippets

    func insertSnippet(_ snippet: Snippet) throws -> Int64 {
        var insertedID: Int64 = 0
        try withConnection { database in
            var statement: OpaquePointer?
            let prepareResult = sqlite3_prepare_v2(
                database,
                "INSERT INTO snippets(phrase, template, enabled) VALUES (?, ?, ?);",
                -1,
                &statement,
                nil)
            guard prepareResult == SQLITE_OK, let statement else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
            defer { sqlite3_finalize(statement) }

            sqlite3_bind_text(statement, 1, snippet.phrase, -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(statement, 2, snippet.template, -1, SQLITE_TRANSIENT)
            sqlite3_bind_int(statement, 3, snippet.enabled ? 1 : 0)

            guard sqlite3_step(statement) == SQLITE_DONE else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
            insertedID = sqlite3_last_insert_rowid(database)
        }
        logger.info("Inserted snippet \(insertedID): '\(snippet.phrase, privacy: .private)'")
        return insertedID
    }

    func fetchEnabledSnippets() throws -> [Snippet] {
        try fetchSnippets(enabledOnly: true)
    }

    /// Returns every snippet, including disabled ones, for the Settings UI.
    func fetchAllSnippets() throws -> [Snippet] {
        try fetchSnippets(enabledOnly: false)
    }

    private func fetchSnippets(enabledOnly: Bool) throws -> [Snippet] {
        var results: [Snippet] = []
        try withConnection { database in
            var statement: OpaquePointer?
            let sql = enabledOnly
                ? "SELECT id, phrase, template, enabled FROM snippets WHERE enabled = 1 ORDER BY id;"
                : "SELECT id, phrase, template, enabled FROM snippets ORDER BY id;"
            let prepareResult = sqlite3_prepare_v2(database, sql, -1, &statement, nil)
            guard prepareResult == SQLITE_OK, let statement else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
            defer { sqlite3_finalize(statement) }

            while sqlite3_step(statement) == SQLITE_ROW {
                let id = sqlite3_column_int64(statement, 0)
                let phrase = String(cString: sqlite3_column_text(statement, 1))
                let template = String(cString: sqlite3_column_text(statement, 2))
                let enabled = sqlite3_column_int(statement, 3) != 0
                results.append(Snippet(id: id, phrase: phrase, template: template, enabled: enabled))
            }
        }
        return results
    }

    func setSnippetEnabled(id: Int64, enabled: Bool) throws {
        try executeStatement("UPDATE snippets SET enabled = ? WHERE id = ?;") { statement in
            sqlite3_bind_int(statement, 1, enabled ? 1 : 0)
            sqlite3_bind_int64(statement, 2, id)
        }
    }

    func deleteSnippet(id: Int64) throws {
        try executeStatement("DELETE FROM snippets WHERE id = ?;") { statement in
            sqlite3_bind_int64(statement, 1, id)
        }
    }

    // MARK: - App profiles

    func insertAppProfile(_ profile: AppProfile) throws -> Int64 {
        var insertedID: Int64 = 0
        try withConnection { database in
            var statement: OpaquePointer?
            let prepareResult = sqlite3_prepare_v2(
                database,
                """
                INSERT INTO app_profiles(name, bundle_identifiers, process_names, writing_style_prompt, newline_handling)
                VALUES (?, ?, ?, ?, ?);
                """,
                -1,
                &statement,
                nil)
            guard prepareResult == SQLITE_OK, let statement else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
            defer { sqlite3_finalize(statement) }

            sqlite3_bind_text(statement, 1, profile.name, -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(statement, 2, profile.bundleIdentifiers.joined(separator: ","), -1, SQLITE_TRANSIENT)
            sqlite3_bind_text(statement, 3, profile.processNames.joined(separator: ","), -1, SQLITE_TRANSIENT)
            if let writingStylePrompt = profile.writingStylePrompt {
                sqlite3_bind_text(statement, 4, writingStylePrompt, -1, SQLITE_TRANSIENT)
            } else {
                sqlite3_bind_null(statement, 4)
            }
            if let newlineHandling = profile.newlineHandling {
                sqlite3_bind_text(statement, 5, newlineHandling.rawValue, -1, SQLITE_TRANSIENT)
            } else {
                sqlite3_bind_null(statement, 5)
            }

            guard sqlite3_step(statement) == SQLITE_DONE else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
            insertedID = sqlite3_last_insert_rowid(database)
        }
        logger.info("Inserted app profile \(insertedID): '\(profile.name, privacy: .public)'")
        return insertedID
    }

    func fetchAppProfiles() throws -> [AppProfile] {
        var results: [AppProfile] = []
        try withConnection { database in
            var statement: OpaquePointer?
            let prepareResult = sqlite3_prepare_v2(
                database,
                "SELECT id, name, bundle_identifiers, process_names, writing_style_prompt, newline_handling FROM app_profiles ORDER BY id;",
                -1,
                &statement,
                nil)
            guard prepareResult == SQLITE_OK, let statement else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
            defer { sqlite3_finalize(statement) }

            while sqlite3_step(statement) == SQLITE_ROW {
                let id = sqlite3_column_int64(statement, 0)
                let name = String(cString: sqlite3_column_text(statement, 1))
                let bundleIdentifiers = String(cString: sqlite3_column_text(statement, 2))
                    .split(separator: ",").map(String.init).filter { !$0.isEmpty }
                let processNames = String(cString: sqlite3_column_text(statement, 3))
                    .split(separator: ",").map(String.init).filter { !$0.isEmpty }
                let writingStylePrompt = sqlite3_column_text(statement, 4).map { String(cString: $0) }
                let newlineHandling = sqlite3_column_text(statement, 5)
                    .map { String(cString: $0) }
                    .flatMap { NewlineInjectionMode(rawValue: $0) }

                results.append(AppProfile(
                    id: id,
                    name: name,
                    bundleIdentifiers: bundleIdentifiers,
                    processNames: processNames,
                    writingStylePrompt: writingStylePrompt,
                    newlineHandling: newlineHandling))
            }
        }
        return results
    }

    func deleteAppProfile(id: Int64) throws {
        try executeStatement("DELETE FROM app_profiles WHERE id = ?;") { statement in
            sqlite3_bind_int64(statement, 1, id)
        }
    }

    /// Shared helper for one-shot mutating statements (UPDATE/DELETE) that only need parameter
    /// binding, no result rows. Cuts the prepare/bind/step/finalize boilerplate that was
    /// previously repeated per-table.
    private func executeStatement(_ sql: String, bind: (OpaquePointer?) -> Void) throws {
        try withConnection { database in
            var statement: OpaquePointer?
            let prepareResult = sqlite3_prepare_v2(database, sql, -1, &statement, nil)
            guard prepareResult == SQLITE_OK, let statement else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
            defer { sqlite3_finalize(statement) }

            bind(statement)

            guard sqlite3_step(statement) == SQLITE_DONE else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
        }
    }

    private func withConnection(_ body: (OpaquePointer?) throws -> Void) throws {

        var database: OpaquePointer?
        let openResult = sqlite3_open(databaseURL.path(percentEncoded: false), &database)
        guard openResult == SQLITE_OK else {
            let message = sqliteMessage(from: database)
            sqlite3_close(database)
            throw PersistenceError.sqlite(message: message)
        }

        defer {
            sqlite3_close(database)
        }

        try body(database)
    }

    private func execute(_ sql: String, database: OpaquePointer?) throws {
        guard sqlite3_exec(database, sql, nil, nil, nil) == SQLITE_OK else {
            throw PersistenceError.sqlite(message: sqliteMessage(from: database))
        }
    }

    private func sqliteMessage(from database: OpaquePointer?) -> String {
        guard let database, let message = sqlite3_errmsg(database) else {
            return "Unknown SQLite error"
        }

        return String(cString: message)
    }
}

enum PersistenceError: LocalizedError {
    case sqlite(message: String)

    var errorDescription: String? {
        switch self {
        case .sqlite(let message):
            return message
        }
    }
}
