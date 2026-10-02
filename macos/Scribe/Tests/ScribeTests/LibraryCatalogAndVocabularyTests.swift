import XCTest

@testable import Scribe

final class LibraryCatalogAndVocabularyTests: XCTestCase {
    private var tempDirectory: URL!
    private var defaults: StorageTestDefaults!
    private var store: PersistenceStore!
    private var service: DictionaryLibraryService!

    override func setUpWithError() throws {
        tempDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ScribeWordPackModelTests-\(UUID().uuidString)", isDirectory: true)
        defaults = StorageTestDefaults()
        store = PersistenceStore(databaseURL: tempDirectory.appendingPathComponent("scribe.db", isDirectory: false))
        try store.initialize()
        service = DictionaryLibraryService(
            librariesDirectory: tempDirectory,
            settings: DictionaryLibrarySettings(defaults: defaults.defaults),
            persistenceStore: store)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: tempDirectory)
        defaults.remove()
        service = nil
        store = nil
    }

    func testLoadCatalogMigratesLegacyEnabledIdsAndExistingCustomAIPermission() async throws {
        defaults.defaults.set(["github"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        try writeCustomLibrary(fileName: "team.csv", term: TermValues("team term", "TeamTerm"))

        let catalog = try await service.loadCatalog()

        XCTAssertEqual(catalog.generation, 1)
        XCTAssertTrue(catalog.localState.enabledIdSet.contains("github"))
        XCTAssertEqual(catalog.localState.aiPermissions["team"], true)
        let storedState = try await store.loadStringSetting(key: DictionaryLibraryService.libraryStateKey)
        XCTAssertNotNil(storedState)
    }

    func testLoadCatalogAppliesBuiltInEditsDocument() async throws {
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
        try writeBuiltInEdits(edits, id: "github")

        let catalog = try await service.loadCatalog()
        let github = try XCTUnwrap(catalog.find(id: "github"))

        XCTAssertEqual(github.state, .available)
        XCTAssertTrue(github.library.entries.contains { $0.replacement == "GitHub Enterprise" })
        XCTAssertTrue(github.library.entries.contains { $0.pattern == "gh cli" && $0.replacement == "GitHub CLI" })
    }

    func testLoadVocabularyUsesAiPermissionToFilterAiEntries() async throws {
        defaults.defaults.set(["github", "team"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        try writeCustomLibrary(fileName: "team.csv", term: TermValues("team term", "TeamTerm"))
        _ = try await service.loadCatalog()

        var state = try XCTUnwrap(try await loadPersistedState())
        state.aiPermissions["team"] = false
        state.generation += 1
        try await saveState(state)

        let vocabulary = try await service.loadVocabulary()

        XCTAssertTrue(vocabulary.entries.contains { $0.pattern == "team term" })
        XCTAssertFalse(vocabulary.aiEntries.contains { $0.pattern == "team term" })
        XCTAssertTrue(vocabulary.aiScope.permittedLibraryIds.contains("github"))
        XCTAssertFalse(vocabulary.aiScope.permittedLibraryIds.contains("team"))
    }

    func testImportStoresAcceptedContentAndStartsAiPermissionOff() async throws {
        _ = try service.import(csv: "pattern,replacement\nfoo,Foo\n", suggestedName: "Imported")

        let state = try XCTUnwrap(try await loadPersistedState())
        XCTAssertEqual(state.aiPermissions["custom-imported"], false)
        XCTAssertNotNil(state.acceptedContent["custom-imported"])
    }

    private func writeCustomLibrary(fileName: String, term: TermValues) throws {
        try FileManager.default.createDirectory(at: tempDirectory, withIntermediateDirectories: true)
        let content = LibraryCsvContent(
            name: "Team",
            category: "Custom",
            description: nil,
            basedOn: nil,
            rows: [term])
        let data = try DictionaryLibraryCsv.exportManaged(content)
        try data.write(to: tempDirectory.appendingPathComponent(fileName), options: .atomic)
    }

    private func writeBuiltInEdits(_ edits: BuiltInLibraryEdits, id: String) throws {
        let url = BuiltInLibraryOverlay.editsURL(root: tempDirectory, id: id)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try BuiltInLibraryOverlay.write(edits).write(to: url, options: .atomic)
    }

    private func loadPersistedState() async throws -> LibraryLocalState? {
        guard let raw = try await store.loadStringSetting(key: DictionaryLibraryService.libraryStateKey) else {
            return nil
        }
        return try JSONDecoder().decode(LibraryLocalState.self, from: XCTUnwrap(raw.data(using: .utf8)))
    }

    private func saveState(_ state: LibraryLocalState) async throws {
        let raw = String(data: try JSONEncoder().encode(state), encoding: .utf8)
        try await store.saveStringSetting(key: DictionaryLibraryService.libraryStateKey, value: raw)
    }
}
