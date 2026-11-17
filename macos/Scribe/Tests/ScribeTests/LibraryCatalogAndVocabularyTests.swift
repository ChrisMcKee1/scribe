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

    // MARK: - Built-in edits that cannot be read

    /// An edits document that exists but cannot be read is not an absent one: it is where a term the user turned off is
    /// recorded, so the shipped rows must not stand in for it. The pack is paused with no rows, as for a document of a
    /// newer version, and the other built-ins are unaffected. A folder where the file belongs fails to read with
    /// something other than ENOENT.
    func testABuiltInWhoseEditsCannotBeReadIsPausedWithNoRows() async throws {
        let context = try makeContext()
        defer { context.cleanup() }
        context.defaults.set(["github", "microsoft-azure"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        let editsURL = BuiltInLibraryOverlay.editsURL(root: context.tempDirectory, id: "github")
        try FileManager.default.createDirectory(at: editsURL, withIntermediateDirectories: true)

        let catalog = try await context.service.loadCatalog()

        let github = try XCTUnwrap(catalog.find(id: "github"))
        XCTAssertEqual(github.state, .unreadable)
        XCTAssertTrue(github.library.entries.isEmpty)
        XCTAssertNil(github.contentHash)
        let azure = try XCTUnwrap(catalog.find(id: "microsoft-azure"))
        XCTAssertEqual(azure.state, .available)
        XCTAssertTrue(azure.library.entries.contains { $0.pattern == "azure" })
        let vocabulary = try await context.service.loadVocabulary()
        XCTAssertFalse(vocabulary.entries.contains { $0.pattern == "get hub" })
        XCTAssertFalse(vocabulary.aiEntries.contains { $0.pattern == "get hub" })
        XCTAssertFalse(vocabulary.aiScope.permittedLibraryIds.contains("github"))
        XCTAssertTrue(vocabulary.aiScope.permittedLibraryIds.contains("microsoft-azure"))
    }

    /// The case the finding names: the edits file is there, a term is turned off in it, and the file cannot be opened
    /// (EACCES here, EIO on a failing disk). A file its owner cannot read is readable by root, so this skips there.
    func testABuiltInWhoseEditsFileHasNoReadPermissionKeepsItsTurnedOffTermOff() async throws {
        try XCTSkipIf(geteuid() == 0, "root can read a file whose permissions forbid it")
        let context = try makeContext()
        defer { context.cleanup() }
        context.defaults.set(["github"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        let edits = BuiltInLibraryEdits(
            version: BuiltInLibraryEdits.currentVersion,
            library: "github",
            terms: [
                BuiltInTermEdit(
                    key: "get hub", intent: .off, base: TermValues("get hub", "GitHub"), value: nil,
                    acknowledged: nil)
            ])
        try writeBuiltInEdits(in: context.tempDirectory, edits, id: "github")
        let editsURL = BuiltInLibraryOverlay.editsURL(root: context.tempDirectory, id: "github")
        let readable = try await context.service.loadVocabulary()
        XCTAssertFalse(readable.entries.contains { $0.pattern == "get hub" }, "the edits document turns it off")
        XCTAssertTrue(readable.entries.contains { $0.pattern == "github" })

        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: editsURL.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: editsURL.path) }
        let unreadable = try await context.service.loadVocabulary()

        XCTAssertFalse(unreadable.entries.contains { $0.pattern == "get hub" }, "the turned off term came back")
        XCTAssertFalse(unreadable.entries.contains { $0.pattern == "github" }, "the shipped rows stood in")
        XCTAssertTrue(unreadable.aiEntries.isEmpty)
    }

    /// Nothing at all, not even the edits folder, is ENOENT: no edits, so the shipped rows are the pack.
    func testABuiltInWithNoEditsFileKeepsItsShippedRows() async throws {
        let context = try makeContext()
        defer { context.cleanup() }
        context.defaults.set(["github"], forKey: DictionaryLibrarySettings.enabledIdsKey)

        let catalog = try await context.service.loadCatalog()

        let github = try XCTUnwrap(catalog.find(id: "github"))
        XCTAssertEqual(github.state, .available)
        XCTAssertTrue(github.library.entries.contains { $0.pattern == "get hub" })
        let vocabulary = try await context.service.loadVocabulary()
        XCTAssertTrue(vocabulary.entries.contains { $0.pattern == "get hub" })
    }

    func testOnlyAMissingFileCountsAsNoEdits() {
        XCTAssertTrue(DictionaryLibraryService.isMissingFile(CocoaError(.fileReadNoSuchFile)))
        XCTAssertTrue(DictionaryLibraryService.isMissingFile(CocoaError(.fileNoSuchFile)))
        XCTAssertTrue(DictionaryLibraryService.isMissingFile(POSIXError(.ENOENT)))
        XCTAssertFalse(DictionaryLibraryService.isMissingFile(CocoaError(.fileReadNoPermission)))
        XCTAssertFalse(DictionaryLibraryService.isMissingFile(CocoaError(.fileReadUnknown)))
        XCTAssertFalse(DictionaryLibraryService.isMissingFile(POSIXError(.EACCES)))
        XCTAssertFalse(DictionaryLibraryService.isMissingFile(POSIXError(.EIO)))
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
