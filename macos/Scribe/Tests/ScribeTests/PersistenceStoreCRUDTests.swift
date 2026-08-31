import XCTest
@testable import Scribe

final class PersistenceStoreCRUDTests: XCTestCase {
    private var store: PersistenceStore!
    private var tempDatabaseURL: URL!

    override func setUpWithError() throws {
        tempDatabaseURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("scribe-test-\(UUID().uuidString).db")
        store = PersistenceStore(databaseURL: tempDatabaseURL)
        try store.initialize()
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: tempDatabaseURL)
    }

    // MARK: - Dictionary entries

    func testInsertAndFetchDictionaryEntry() throws {
        let id = try store.insertDictionaryEntry(DictionaryEntry(pattern: "foo", replacement: "bar"))
        XCTAssertGreaterThan(id, 0)

        let entries = try store.fetchEnabledDictionaryEntries()
        XCTAssertEqual(entries.count, 1)
        XCTAssertEqual(entries.first?.pattern, "foo")
        XCTAssertEqual(entries.first?.replacement, "bar")
    }

    func testFetchAllDictionaryEntriesIncludesDisabled() throws {
        _ = try store.insertDictionaryEntry(DictionaryEntry(pattern: "enabled-one", replacement: "x", enabled: true))
        let disabledID = try store.insertDictionaryEntry(DictionaryEntry(pattern: "disabled-one", replacement: "y", enabled: false))

        XCTAssertEqual(try store.fetchEnabledDictionaryEntries().count, 1)
        XCTAssertEqual(try store.fetchAllDictionaryEntries().count, 2)

        try store.setDictionaryEntryEnabled(id: disabledID, enabled: true)
        XCTAssertEqual(try store.fetchEnabledDictionaryEntries().count, 2)
    }

    func testDeleteDictionaryEntry() throws {
        let id = try store.insertDictionaryEntry(DictionaryEntry(pattern: "temp", replacement: "gone"))
        try store.deleteDictionaryEntry(id: id)
        XCTAssertTrue(try store.fetchAllDictionaryEntries().isEmpty)
    }

    // MARK: - Snippets

    func testInsertAndFetchSnippet() throws {
        let id = try store.insertSnippet(Snippet(phrase: "sign off", template: "Best,\nMe"))
        XCTAssertGreaterThan(id, 0)

        let snippets = try store.fetchEnabledSnippets()
        XCTAssertEqual(snippets.count, 1)
        XCTAssertEqual(snippets.first?.template, "Best,\nMe")
    }

    func testSetSnippetEnabledTogglesVisibility() throws {
        let id = try store.insertSnippet(Snippet(phrase: "x", template: "y"))
        try store.setSnippetEnabled(id: id, enabled: false)
        XCTAssertTrue(try store.fetchEnabledSnippets().isEmpty)
        XCTAssertEqual(try store.fetchAllSnippets().count, 1)
    }

    func testDeleteSnippet() throws {
        let id = try store.insertSnippet(Snippet(phrase: "x", template: "y"))
        try store.deleteSnippet(id: id)
        XCTAssertTrue(try store.fetchAllSnippets().isEmpty)
    }

    // MARK: - App profiles

    func testInsertAndFetchAppProfileRoundTripsAllFields() throws {
        let id = try store.insertAppProfile(AppProfile(
            name: "Terminal",
            bundleIdentifiers: ["com.apple.Terminal", "com.googlecode.iterm2"],
            processNames: ["Terminal"],
            writingStylePrompt: "Be terse.",
            newlineHandling: .alwaysFlatten))
        XCTAssertGreaterThan(id, 0)

        let profiles = try store.fetchAppProfiles()
        XCTAssertEqual(profiles.count, 1)
        let profile = try XCTUnwrap(profiles.first)
        XCTAssertEqual(profile.id, id)
        XCTAssertEqual(profile.name, "Terminal")
        XCTAssertEqual(profile.bundleIdentifiers, ["com.apple.Terminal", "com.googlecode.iterm2"])
        XCTAssertEqual(profile.processNames, ["Terminal"])
        XCTAssertEqual(profile.writingStylePrompt, "Be terse.")
        XCTAssertEqual(profile.newlineHandling, .alwaysFlatten)
    }

    func testAppProfileWithNilOverridesRoundTrips() throws {
        _ = try store.insertAppProfile(AppProfile(
            name: "Minimal",
            bundleIdentifiers: ["com.example.app"],
            processNames: [],
            writingStylePrompt: nil,
            newlineHandling: nil))

        let profile = try XCTUnwrap(try store.fetchAppProfiles().first)
        XCTAssertNil(profile.writingStylePrompt)
        XCTAssertNil(profile.newlineHandling)
    }

    func testDeleteAppProfile() throws {
        let id = try store.insertAppProfile(AppProfile(name: "Temp", bundleIdentifiers: ["com.example.temp"], processNames: []))
        try store.deleteAppProfile(id: id)
        XCTAssertTrue(try store.fetchAppProfiles().isEmpty)
    }

    // MARK: - Dictation history

    func testRecordAndFetchDictationHistoryRoundTripsTargetApp() throws {
        let startedAt = Date(timeIntervalSince1970: 1_700_000_000)
        try store.recordDictation(
            startedAt: startedAt,
            durationSeconds: 3.5,
            sampleCount: 56_000,
            decodeMilliseconds: 120,
            cleanupMilliseconds: nil,
            transcriptText: "deploy to azure",
            targetApp: "com.apple.Terminal")

        let record = try XCTUnwrap(try store.fetchDictationHistory().first)
        XCTAssertEqual(record.targetApp, "com.apple.Terminal")
        XCTAssertEqual(record.transcriptText, "deploy to azure")
    }

    func testFetchDictationHistorySurfacesNilTargetAppForOlderRows() throws {
        try store.recordDictation(startedAt: Date(), durationSeconds: 1, sampleCount: 16_000)

        let record = try XCTUnwrap(try store.fetchDictationHistory().first)
        XCTAssertNil(record.targetApp)
    }
}
