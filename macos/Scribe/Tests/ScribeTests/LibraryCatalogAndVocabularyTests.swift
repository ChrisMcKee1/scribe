import XCTest

@testable import Scribe

final class LibraryCatalogAndVocabularyTests: XCTestCase {
    func testLoadCatalogMigratesLegacyEnabledIdsAndExistingCustomAIPermission() async throws {
        let context = try makeContext()
        defer { context.cleanup() }
        context.defaults.set(["github"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        try writeCustomLibrary(
            in: context.tempDirectory,
            fileName: "team.csv",
            term: TermValues("team term", "TeamTerm"))

        let catalog = try await context.service.loadCatalog()

        XCTAssertEqual(catalog.generation, 1)
        XCTAssertTrue(catalog.localState.enabledIdSet.contains("github"))
        XCTAssertEqual(catalog.localState.aiPermissions["team"], true)
        let storedState = try await context.store.loadStringSetting(key: DictionaryLibraryService.libraryStateKey)
        XCTAssertNotNil(storedState)
    }

    func testLoadCatalogAppliesBuiltInEditsDocument() async throws {
        let context = try makeContext()
        defer { context.cleanup() }
        let edits = BuiltInLibraryEdits(
            version: BuiltInLibraryEdits.currentVersion,
            library: "github",
            terms: [
                BuiltInTermEdit(
                    key: "get hub",
                    intent: .edited,
                    base: TermValues("get hub", "GitHub"),
                    value: TermValues("get hub", "GitHub Enterprise"),
                    acknowledged: nil),
                BuiltInTermEdit(
                    key: "gh cli",
                    intent: .added,
                    base: nil,
                    value: TermValues("gh cli", "GitHub CLI"),
                    acknowledged: nil),
            ])
        try writeBuiltInEdits(in: context.tempDirectory, edits, id: "github")

        let catalog = try await context.service.loadCatalog()
        let github = try XCTUnwrap(catalog.find(id: "github"))

        XCTAssertEqual(github.state, .available)
        XCTAssertTrue(github.library.entries.contains { $0.replacement == "GitHub Enterprise" })
        XCTAssertTrue(github.library.entries.contains { $0.pattern == "gh cli" && $0.replacement == "GitHub CLI" })
    }

    func testLoadVocabularyUsesAiPermissionToFilterAiEntries() async throws {
        let context = try makeContext()
        defer { context.cleanup() }
        context.defaults.set(["github", "team"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        try writeCustomLibrary(
            in: context.tempDirectory,
            fileName: "team.csv",
            term: TermValues("team term", "TeamTerm"))
        _ = try await context.service.loadCatalog()

        let persistedState = try await loadPersistedState(from: context.store)
        var state = try XCTUnwrap(persistedState)
        state.aiPermissions["team"] = false
        state.generation += 1
        try await saveState(state, to: context.store)

        let vocabulary = try await context.service.loadVocabulary()

        XCTAssertTrue(vocabulary.entries.contains { $0.pattern == "team term" })
        XCTAssertFalse(vocabulary.aiEntries.contains { $0.pattern == "team term" })
        XCTAssertTrue(vocabulary.aiScope.permittedLibraryIds.contains("github"))
        XCTAssertFalse(vocabulary.aiScope.permittedLibraryIds.contains("team"))
    }

    func testImportStoresAcceptedContentAndStartsAiPermissionOff() throws {
        let context = try makeContext()
        defer { context.cleanup() }
        _ = try context.service.import(
            csv: "pattern,replacement\nfoo,Foo\n",
            suggestedName: "Imported")

        let state = try XCTUnwrap(try loadPersistedStateSync(from: context.store))
        XCTAssertEqual(state.aiPermissions["custom-imported"], false)
        XCTAssertNotNil(state.acceptedContent["custom-imported"])
    }

    private func makeContext() throws -> TestContext {
        let tempDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ScribeWordPackModelTests-\(UUID().uuidString)", isDirectory: true)
        let defaultsSuiteName = "com.scribe.macos.tests.storage.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: defaultsSuiteName)!
        let store = PersistenceStore(databaseURL: tempDirectory.appendingPathComponent("scribe.db", isDirectory: false))
        try store.initialize()
        let service = DictionaryLibraryService(
            librariesDirectory: tempDirectory,
            settings: DictionaryLibrarySettings(defaults: defaults),
            persistenceStore: store)
        return TestContext(
            tempDirectory: tempDirectory,
            defaultsSuiteName: defaultsSuiteName,
            defaults: defaults,
            store: store,
            service: service)
    }

    private func writeCustomLibrary(in directory: URL, fileName: String, term: TermValues) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let content = LibraryCsvContent(
            name: "Team",
            category: "Custom",
            description: nil,
            basedOn: nil,
            rows: [term])
        let data = try DictionaryLibraryCsv.exportManaged(content)
        try data.write(to: directory.appendingPathComponent(fileName), options: .atomic)
    }

    private func writeBuiltInEdits(in directory: URL, _ edits: BuiltInLibraryEdits, id: String) throws {
        let url = BuiltInLibraryOverlay.editsURL(root: directory, id: id)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try BuiltInLibraryOverlay.write(edits).write(to: url, options: .atomic)
    }

    private func loadPersistedState(from store: PersistenceStore) async throws -> LibraryLocalState? {
        guard let raw = try await store.loadStringSetting(key: DictionaryLibraryService.libraryStateKey) else {
            return nil
        }
        return try JSONDecoder().decode(LibraryLocalState.self, from: XCTUnwrap(raw.data(using: .utf8)))
    }

    private func loadPersistedStateSync(from store: PersistenceStore) throws -> LibraryLocalState? {
        guard let raw = try store.readStringSetting(key: DictionaryLibraryService.libraryStateKey) else {
            return nil
        }
        return try JSONDecoder().decode(LibraryLocalState.self, from: XCTUnwrap(raw.data(using: .utf8)))
    }

    private func saveState(_ state: LibraryLocalState, to store: PersistenceStore) async throws {
        let raw = String(data: try JSONEncoder().encode(state), encoding: .utf8)
        try await store.saveStringSetting(key: DictionaryLibraryService.libraryStateKey, value: raw)
    }
}

private struct TestContext {
    let tempDirectory: URL
    let defaultsSuiteName: String
    let defaults: UserDefaults
    let store: PersistenceStore
    let service: DictionaryLibraryService

    func cleanup() {
        try? FileManager.default.removeItem(at: tempDirectory)
        defaults.removePersistentDomain(forName: defaultsSuiteName)
    }
}
