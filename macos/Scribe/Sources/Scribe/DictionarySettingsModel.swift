import Foundation

/// How the Dictionary tab reaches storage. `live(_:)` goes through `PersistenceStore`'s asynchronous forms, so no call
/// waits on the main actor, and every decision that depends on what is stored (an import's merge, which learned terms
/// are new) is made inside the store's transaction, never against the rows on screen, which may not have loaded yet.
/// Tests pass their own.
struct DictionarySettingsAccess: Sendable {
    var loadEntries: @Sendable () async throws -> [DictionaryEntry]
    var addEntry: @Sendable (DictionaryEntry) async throws -> Void
    var setEnabled: @Sendable (_ id: Int64, _ enabled: Bool) async throws -> Void
    var deleteEntry: @Sendable (_ id: Int64) async throws -> Void
    /// Merges parsed CSV rows into the dictionary as stored, in one transaction.
    var importEntries: @Sendable ([DictionaryEntry]) async throws -> DictionaryImportSummary
    /// Mines recent history and adds the terms no stored rule covers, returning what it added.
    var learnFromHistory: @Sendable () async throws -> [DictionaryEntry]
    /// Scores every stored rule against recent history for the Clean Up review, off the main actor.
    var reviewUsage: @Sendable () async throws -> DictionaryUsageReport
    /// Turns the chosen rules off in one transaction.
    var disableEntries: @Sendable (Set<Int64>) async throws -> Void
}

extension DictionarySettingsAccess {
    static func live(_ store: PersistenceStore) -> DictionarySettingsAccess {
        DictionarySettingsAccess(
            loadEntries: { try await store.loadAllDictionaryEntries() },
            addEntry: { entry in _ = try await store.addDictionaryEntry(entry) },
            setEnabled: { id, enabled in try await store.saveDictionaryEntryEnabled(id: id, enabled: enabled) },
            deleteEntry: { id in try await store.removeDictionaryEntry(id: id) },
            importEntries: { entries in try await store.importDictionary(entries) },
            learnFromHistory: { try await store.learnDictionaryEntries() },
            reviewUsage: {
                let inputs = try await store.loadDictionaryReviewInputs()
                return DictionaryUsageAnalyzer.analyze(
                    transcripts: inputs.history.compactMap(\.transcriptText),
                    baseEntries: inputs.entries)
            },
            disableEntries: { ids in try await store.disableDictionaryEntries(ids: ids) })
    }
}

/// The Dictionary tab: the stored rules, adding, switching and deleting them, CSV import and export, learning from
/// history and the Clean Up review. Every storage call is asynchronous, so the tab never waits on the main actor
/// behind a history write, and only the newest read may replace the rows or report an error (`SettingsSectionLoad`),
/// whatever order reads finish in. After each change the app's rules are refreshed (`onChanged`) and the rows are
/// read again.
@MainActor
final class DictionarySettingsModel: ObservableObject {
    @Published private(set) var entries: [DictionaryEntry] = []
    /// Why the newest read failed. Kept apart from `errorMessage`, so a read that finishes after an action failed
    /// never clears the action's failure.
    @Published private(set) var loadError: String?
    /// Why the last action (an add, switch, delete, import and the like) failed.
    @Published private(set) var errorMessage: String?
    @Published private(set) var statusMessage: String?
    @Published private(set) var isAdding = false
    @Published private(set) var isImporting = false
    @Published private(set) var isLearning = false
    @Published private(set) var isCleaning = false
    /// Clean Up findings awaiting review; the review sheet shows while this is set.
    @Published var cleanupReport: DictionaryUsageReport?
    @Published private(set) var load = SettingsSectionLoad()

    let drafts: SettingsDrafts
    private let access: DictionarySettingsAccess
    private let onChanged: @MainActor () -> Void

    init(access: DictionarySettingsAccess, drafts: SettingsDrafts, onChanged: @escaping @MainActor () -> Void) {
        self.access = access
        self.drafts = drafts
        self.onChanged = onChanged
    }

    var canAdd: Bool {
        !isAdding && !Self.isBlank(drafts.dictionaryPattern) && !Self.isBlank(drafts.dictionaryReplacement)
    }

    /// Export writes the rows on screen, so it waits until they have loaded.
    var canExport: Bool {
        load.isLoaded && !entries.isEmpty
    }

    func reload() async {
        let ticket = load.begin()
        do {
            let loaded = try await access.loadEntries()
            guard load.publish(ticket) else {
                return
            }
            entries = loaded
            loadError = nil
        } catch {
            guard load.fail(ticket) else {
                return
            }
            loadError = error.localizedDescription
        }
    }

    /// Adds the rule typed into the drafts. The drafts are emptied only if they still hold what was added, so
    /// anything typed while the rule was being saved is kept.
    func addFromDrafts() async {
        let pattern = drafts.dictionaryPattern
        let replacement = drafts.dictionaryReplacement
        guard canAdd else {
            return
        }
        isAdding = true
        defer { isAdding = false }

        let added = await write {
            try await self.access.addEntry(DictionaryEntry(pattern: pattern, replacement: replacement))
        }
        if added, drafts.dictionaryPattern == pattern, drafts.dictionaryReplacement == replacement {
            drafts.dictionaryPattern = ""
            drafts.dictionaryReplacement = ""
        }
    }

    func setEnabled(_ entry: DictionaryEntry, enabled: Bool) async {
        await write {
            try await self.access.setEnabled(entry.id, enabled)
        }
    }

    func delete(_ entry: DictionaryEntry) async {
        await write {
            try await self.access.deleteEntry(entry.id)
        }
    }

    /// Imports the text of a dictionary CSV. The merge with what is stored happens inside the store's transaction,
    /// never against `entries`, which may not have loaded yet or may predate an import whose refresh is still running.
    /// Import is available again as soon as the transaction commits, before the rows have been read again.
    func importCsv(_ text: String) async {
        guard !isImporting else {
            return
        }
        isImporting = true
        errorMessage = nil
        statusMessage = nil

        let parsed = DictionaryCsv.parse(text)
        let summary: DictionaryImportSummary
        do {
            summary = try await access.importEntries(parsed.entries)
        } catch {
            isImporting = false
            errorMessage = "Couldn't import that file: \(error.localizedDescription)"
            return
        }
        isImporting = false

        let skipped =
            parsed.errors.isEmpty
            ? ""
            : " \(parsed.errors.count) row(s) skipped: \(parsed.errors.joined(separator: "; "))"
        statusMessage =
            "Imported: \(summary.added) added, \(summary.updated) updated, \(summary.unchanged) unchanged." + skipped
        onChanged()
        await reload()
    }

    /// Reads a CSV file off the main actor, then imports it.
    func importCsvFile(at url: URL) async {
        errorMessage = nil
        statusMessage = nil
        let text: String
        do {
            text = try await Self.readText(at: url)
        } catch {
            errorMessage = "Couldn't import that file: \(error.localizedDescription)"
            return
        }
        await importCsv(text)
    }

    /// Writes the rows on screen as CSV to `url`, off the main actor.
    func exportCsv(to url: URL) async {
        errorMessage = nil
        statusMessage = nil
        let csv = DictionaryCsv.export(entries)
        let count = entries.count
        do {
            try await Self.writeText(csv, to: url)
            statusMessage = "Exported \(count) entr\(count == 1 ? "y" : "ies") to \(url.lastPathComponent)."
        } catch {
            errorMessage = "Couldn't export the dictionary: \(error.localizedDescription)"
        }
    }

    func saveTemplate(to url: URL) async {
        errorMessage = nil
        statusMessage = nil
        do {
            try await Self.writeText(DictionaryCsv.template, to: url)
            statusMessage = "Saved the template to \(url.lastPathComponent)."
        } catch {
            errorMessage = "Couldn't save the template: \(error.localizedDescription)"
        }
    }

    /// Mines recent history for recurring jargon and adds the terms no stored rule covers. The store decides what is
    /// new inside the transaction that writes it, so a rule the tab has not loaded is never added twice.
    func learnFromHistory() async {
        guard !isLearning else {
            return
        }
        isLearning = true
        defer { isLearning = false }
        statusMessage = nil

        var learned: [DictionaryEntry] = []
        let succeeded = await write(failurePrefix: "Couldn't learn from history") {
            learned = try await self.access.learnFromHistory()
        }
        guard succeeded else {
            return
        }
        statusMessage =
            learned.isEmpty
            ? "No new recurring terms found in your dictation history yet."
            : "Learned \(learned.count) new entr\(learned.count == 1 ? "y" : "ies") from your dictation history."
    }

    /// Scores the stored rules against recent history and, when some never come up, opens the review. Nothing is
    /// turned off until the user confirms.
    func reviewUsage() async {
        guard !isCleaning else {
            return
        }
        isCleaning = true
        defer { isCleaning = false }
        errorMessage = nil
        statusMessage = nil

        do {
            let report = try await access.reviewUsage()
            guard report.hasFindings else {
                statusMessage =
                    report.hasEnoughEvidence
                    ? "Every term in your dictionary turned up in your recent dictations. Nothing to clean up."
                    : report.summary
                return
            }
            cleanupReport = report
        } catch {
            errorMessage = "Couldn't check dictionary usage: \(error.localizedDescription)"
        }
    }

    /// Soft-disables the confirmed entries rather than deleting them, mirroring Windows: the evidence is a sample of
    /// recent history, not proof the term will never be needed again.
    func applyCleanup(disabling ids: Set<Int64>) async {
        cleanupReport = nil
        guard !ids.isEmpty else {
            return
        }
        let succeeded = await write(failurePrefix: "Couldn't update the dictionary") {
            try await self.access.disableEntries(ids)
        }
        if succeeded {
            statusMessage = "Turned off \(ids.count) unused entr\(ids.count == 1 ? "y" : "ies")."
        }
    }

    /// Runs one write. On success the app's rules are refreshed and the rows read again. Returns whether it succeeded.
    @discardableResult
    private func write(failurePrefix: String? = nil, _ operation: @MainActor () async throws -> Void) async -> Bool {
        errorMessage = nil
        do {
            try await operation()
        } catch {
            errorMessage = failurePrefix.map { "\($0): \(error.localizedDescription)" } ?? error.localizedDescription
            return false
        }
        onChanged()
        await reload()
        return true
    }

    private static func isBlank(_ text: String) -> Bool {
        text.trimmingCharacters(in: .whitespaces).isEmpty
    }

    private nonisolated static func readText(at url: URL) async throws -> String {
        try String(contentsOf: url, encoding: .utf8)
    }

    private nonisolated static func writeText(_ text: String, to url: URL) async throws {
        try text.write(to: url, atomically: true, encoding: .utf8)
    }
}
