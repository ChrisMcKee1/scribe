import XCTest

@testable import Scribe

/// The dictionary, snippet and profile operations the Settings tabs use through the store's asynchronous forms.
final class PersistenceStoreDictionaryTests: XCTestCase {
    private var directory: StorageTestDirectory!

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

    // MARK: - Import in one transaction

    func testAnImportMergesWithTheStoredRulesByTheirSpokenForm() async throws {
        let store = try makeStore()
        let azure = try store.insertDictionaryEntry(DictionaryEntry(pattern: "azure", replacement: "Azure"))
        _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow"))

        let summary = try await store.importDictionary([
            DictionaryEntry(pattern: "Azure", replacement: "AZURE"),
            DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow"),
            DictionaryEntry(pattern: "kay eight ess", replacement: "K8s"),
            DictionaryEntry(pattern: "kay eight ess", replacement: "k8s"),
        ])

        XCTAssertEqual(summary, DictionaryImportSummary(added: 1, updated: 2, unchanged: 1))
        let stored = try store.fetchAllDictionaryEntries()
        XCTAssertEqual(stored.map(\.pattern), ["azure", "cube flow", "kay eight ess"])
        XCTAssertEqual(stored.map(\.replacement), ["AZURE", "Kubeflow", "k8s"])
        XCTAssertEqual(stored.first?.id, azure)
    }

    func testAnImportThatFailsPartWayChangesNothing() async throws {
        let store = try makeStore()
        _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "azure", replacement: "Azure"))
        let raw = try StorageTestSQLite(directory.databaseURL)
        try raw.execute(
            """
            CREATE TRIGGER refuse_boom BEFORE INSERT ON dictionary_entries WHEN NEW.pattern = 'boom'
            BEGIN SELECT RAISE(ABORT, 'refused'); END;
            """)
        raw.close()

        do {
            _ = try await store.importDictionary([
                DictionaryEntry(pattern: "azure", replacement: "AZURE"),
                DictionaryEntry(pattern: "alpha", replacement: "Alpha"),
                DictionaryEntry(pattern: "boom", replacement: "Boom"),
            ])
            XCTFail("expected the import to fail")
        } catch {
            XCTAssertNotNil(error as? PersistenceError)
        }

        let stored = try store.fetchAllDictionaryEntries()
        XCTAssertEqual(stored.map(\.pattern), ["azure"])
        XCTAssertEqual(stored.map(\.replacement), ["Azure"])
    }

    // MARK: - Adding only what is new

    func testAddingIfAbsentSkipsStoredAndRepeatedSpokenForms() async throws {
        let store = try makeStore()
        _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: " Azure ", replacement: "Azure"))

        let added = try await store.addDictionaryEntriesIfAbsent([
            DictionaryEntry(pattern: "azure", replacement: "AZURE"),
            DictionaryEntry(pattern: "K8s", replacement: "K8s"),
            DictionaryEntry(pattern: "k8s", replacement: "k8s"),
            DictionaryEntry(pattern: "   ", replacement: "blank"),
        ])

        XCTAssertEqual(added.map(\.pattern), ["K8s"])
        XCTAssertNotEqual(added.first?.id, 0)
        XCTAssertEqual(try store.fetchAllDictionaryEntries().map(\.pattern), [" Azure ", "K8s"])
    }

    func testTwoLearnsAtOnceNeverAddTheSameRuleTwice() async throws {
        let store = try makeStore()
        let history = [
            "the ATU owns it",
            "ask the ATU",
            "ATU signed off",
        ]
        for text in history {
            try store.recordDictation(startedAt: Date(), durationSeconds: 1, sampleCount: 16_000, transcriptText: text)
        }

        async let first = store.learnDictionaryEntries()
        async let second = store.learnDictionaryEntries()
        let learned = try await first + second

        let stored = try store.fetchAllDictionaryEntries()
        XCTAssertFalse(stored.isEmpty)
        XCTAssertEqual(learned.count, stored.count)
        let keys = stored.map { $0.pattern.lowercased() }
        XCTAssertEqual(Set(keys).count, keys.count)
    }

    // MARK: - What dictation applies

    func testTheRuleSetHoldsOnlyEnabledRulesAndSnippetsAndEveryProfile() async throws {
        let store = try makeStore()
        _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "on", replacement: "On"))
        _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "off", replacement: "Off", enabled: false))
        _ = try store.insertSnippet(Snippet(phrase: "live", template: "Live"))
        _ = try store.insertSnippet(Snippet(phrase: "paused", template: "Paused", enabled: false))
        _ = try store.insertAppProfile(
            AppProfile(name: "Terminal", bundleIdentifiers: ["com.apple.Terminal"], processNames: []))

        let rules = try await store.loadRuleSet()

        XCTAssertEqual(rules.dictionaryEntries.map(\.pattern), ["on"])
        XCTAssertEqual(rules.snippets.map(\.phrase), ["live"])
        XCTAssertEqual(rules.appProfiles.map(\.name), ["Terminal"])
    }

    func testDisablingRulesTurnsOffExactlyThose() async throws {
        let store = try makeStore()
        let keep = try store.insertDictionaryEntry(DictionaryEntry(pattern: "keep", replacement: "Keep"))
        let drop = try store.insertDictionaryEntry(DictionaryEntry(pattern: "drop", replacement: "Drop"))

        try await store.disableDictionaryEntries(ids: [drop])

        let stored = try store.fetchAllDictionaryEntries()
        XCTAssertEqual(stored.map(\.id), [keep, drop])
        XCTAssertEqual(stored.map(\.enabled), [true, false])
    }

    // MARK: - Asynchronous forms match the synchronous ones

    func testTheAsynchronousDictionaryFormsWriteWhatTheSynchronousOnesRead() async throws {
        let store = try makeStore()

        let id = try await store.addDictionaryEntry(DictionaryEntry(pattern: "sherpa onnx", replacement: "sherpa-onnx"))
        try await store.saveDictionaryEntry(DictionaryEntry(id: id, pattern: "sherpa onnx", replacement: "Sherpa ONNX"))
        try await store.saveDictionaryEntryEnabled(id: id, enabled: false)
        XCTAssertEqual(
            try store.fetchAllDictionaryEntries(),
            [DictionaryEntry(id: id, pattern: "sherpa onnx", replacement: "Sherpa ONNX", enabled: false)])
        let loaded = try await store.loadAllDictionaryEntries()
        XCTAssertEqual(loaded, try store.fetchAllDictionaryEntries())

        try await store.removeDictionaryEntry(id: id)
        XCTAssertTrue(try store.fetchAllDictionaryEntries().isEmpty)
    }

    func testTheAsynchronousSnippetAndProfileFormsWriteWhatTheSynchronousOnesRead() async throws {
        let store = try makeStore()

        let snippetID = try await store.addSnippet(Snippet(phrase: "greeting", template: "Hello"))
        try await store.saveSnippetEnabled(id: snippetID, enabled: false)
        let snippets = try await store.loadAllSnippets()
        XCTAssertEqual(snippets, try store.fetchAllSnippets())
        XCTAssertEqual(snippets.map(\.enabled), [false])
        try await store.removeSnippet(id: snippetID)
        XCTAssertTrue(try store.fetchAllSnippets().isEmpty)

        let profileID = try await store.addAppProfile(
            AppProfile(
                name: "Chat",
                bundleIdentifiers: ["com.tinyspeck.slackmacgap"],
                processNames: [],
                writingStylePrompt: "Casual",
                newlineHandling: .alwaysFlatten))
        let profiles = try await store.loadAppProfiles()
        XCTAssertEqual(profiles, try store.fetchAppProfiles())
        XCTAssertEqual(profiles.map(\.id), [profileID])
        try await store.removeAppProfile(id: profileID)
        XCTAssertTrue(try store.fetchAppProfiles().isEmpty)
    }

    func testPreparingAsynchronouslyMigratesANewDatabase() async throws {
        let store = PersistenceStore(databaseURL: directory.databaseURL)

        try await store.prepare()

        let retention = try await store.loadHistoryRetention()
        XCTAssertEqual(retention, .chosen(.days(HistoryRetention.defaultDays)))
        let count = try await store.loadHistoryCount()
        XCTAssertEqual(count, 0)
        XCTAssertTrue(try store.fetchAllDictionaryEntries().isEmpty)
    }

    func testAUsagePeriodReadsItsDictationsAndEveryRuleTogether() async throws {
        let store = try makeStore()
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        try store.recordDictation(
            startedAt: now.addingTimeInterval(-10 * 86_400), durationSeconds: 1, sampleCount: 1, transcriptText: "old")
        try store.recordDictation(
            startedAt: now.addingTimeInterval(-60), durationSeconds: 1, sampleCount: 1, transcriptText: "recent")
        _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "off", replacement: "Off", enabled: false))

        let period = try await store.loadUsagePeriod(since: now.addingTimeInterval(-86_400), limit: 10)

        XCTAssertEqual(period.records.compactMap(\.transcriptText), ["recent"])
        XCTAssertEqual(period.knownTerms.map(\.pattern), ["off"])
    }
}
