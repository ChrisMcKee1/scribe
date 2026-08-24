import Foundation
import OSLog
import SQLite3

private let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

final class PersistenceStore {
    private let logger = Logger(subsystem: "com.scribe.macos", category: "Persistence")
    private let fileManager: FileManager
    private let iso8601Formatter: ISO8601DateFormatter

    let databaseURL: URL

    init(fileManager: FileManager = .default) {
        self.fileManager = fileManager

        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        self.iso8601Formatter = formatter

        let applicationSupportURL = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let scribeDirectoryURL = applicationSupportURL.appendingPathComponent("Scribe", isDirectory: true)
        self.databaseURL = scribeDirectoryURL.appendingPathComponent("scribe.db", isDirectory: false)
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
                    sample_count INTEGER NOT NULL
                );
                """,
                database: database)
        }

        logger.info("SQLite store ready at \(self.databaseURL.path(percentEncoded: false), privacy: .public)")
    }

    func recordDictation(startedAt: Date, durationSeconds: Double, sampleCount: Int) throws {
        try withConnection { database in
            var statement: OpaquePointer?
            let prepareResult = sqlite3_prepare_v2(
                database,
                "INSERT INTO dictation_history(started_at, duration_seconds, sample_count) VALUES (?, ?, ?);",
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

            guard sqlite3_step(statement) == SQLITE_DONE else {
                throw PersistenceError.sqlite(message: sqliteMessage(from: database))
            }
        }

        let startedAtText = iso8601Formatter.string(from: startedAt)
        logger.info(
            "Saved dictation history row for \(startedAtText, privacy: .public) with \(sampleCount) samples.")
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
