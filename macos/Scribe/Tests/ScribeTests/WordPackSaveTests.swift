import XCTest

@testable import Scribe

final class WordPackSaveTests: XCTestCase {
    func testPrepareAbortAndSharedCommitHaveOneDurabilityBoundary() async throws {
        let fixture = try WordPackSaveFixture()
        defer { fixture.remove() }
        var workspace = WordPackWorkspace(catalog: try await fixture.service.loadCatalog())
        let id = try workspace.createLibrary()
        _ = workspace.rename(id, name: "Team")
        _ = workspace.addTerm(id, values: TermValues("kube", "Kubernetes"))
        let changes = try XCTUnwrap(workspace.captureChangeSet().changeSet)
        let prepared = try await fixture.coordinator.prepare(changes)
        XCTAssertFalse(FileManager.default.fileExists(atPath: fixture.root.appendingPathComponent("\(id).csv").path))
        XCTAssertNil(try fixture.store.readStringSetting(key: WordPackJournal.key))
        await fixture.coordinator.abort(prepared)
        expectEqual(await fixture.coordinator.commit(prepared), .notCommitted)

        let second = try await fixture.coordinator.prepare(changes)
        let settings = StoredSettingsParticipant(
            checks: [StoredSettingCheck(key: "settings-document", expected: nil)],
            writes: [StoredSettingWrite(key: "settings-document", value: "saved")])
        expectEqual(await fixture.coordinator.commit(second, alongside: [settings]), .saved)
        XCTAssertEqual(try fixture.store.readStringSetting(key: "settings-document"), "saved")
        let catalog = try await fixture.service.loadCatalog()
        XCTAssertEqual(catalog.find(id: id)?.library.entries.first?.replacement, "Kubernetes")
        workspace.markSaved(changes, catalog: catalog, deleted: try await fixture.coordinator.recentlyDeleted())
        XCTAssertFalse(workspace.hasUnsavedChanges)
    }

    func testFailedParticipantRollsBackAllWordPackStateAndFiles() async throws {
        let fixture = try WordPackSaveFixture()
        defer { fixture.remove() }
        var workspace = WordPackWorkspace(catalog: try await fixture.service.loadCatalog())
        let id = try workspace.createLibrary()
        _ = workspace.rename(id, name: "Team")
        let prepared = try await fixture.coordinator.prepare(XCTUnwrap(workspace.captureChangeSet().changeSet))
        let conflict = StoredSettingsParticipant(
            checks: [StoredSettingCheck(key: "settings-document", expected: "not-stored")],
            writes: [StoredSettingWrite(key: "settings-document", value: "bad")])
        expectEqual(await fixture.coordinator.commit(prepared, alongside: [conflict]), .notCommitted)
        XCTAssertNil(try fixture.store.readStringSetting(key: WordPackJournal.key))
        XCTAssertNil(try fixture.store.readStringSetting(key: "settings-document"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: fixture.root.appendingPathComponent("\(id).csv").path))
        XCTAssertTrue(workspace.hasUnsavedChanges)
    }

    func testRestartFinishesACommittedRedoWithoutAnotherLogicalCommit() async throws {
        let fixture = try WordPackSaveFixture()
        defer { fixture.remove() }
        var workspace = WordPackWorkspace(catalog: try await fixture.service.loadCatalog())
        let id = try workspace.createLibrary()
        _ = workspace.rename(id, name: "Restart")
        _ = workspace.addTerm(id, values: TermValues("restart", "Restart"))
        let prepared = try await fixture.coordinator.prepare(XCTUnwrap(workspace.captureChangeSet().changeSet))
        try await fixture.store.commitSettingsParticipants([prepared.participant])
        let restarted = fixture.newService()
        let catalog = try await restarted.loadCatalog()
        XCTAssertEqual(catalog.generation, prepared.journal.generation)
        XCTAssertEqual(catalog.find(id: id)?.library.entries.first?.replacement, "Restart")
        XCTAssertNil(try fixture.store.readStringSetting(key: WordPackJournal.key))
        XCTAssertEqual(try fixture.store.readStringSetting(key: WordPackJournal.receiptKey), prepared.id.uuidString)
    }

    func testOutsideWriteAfterPrepareIsPreservedAndOnlyAffectedPackIsHeldBack() async throws {
        let fixture = try WordPackSaveFixture()
        defer { fixture.remove() }
        var workspace = WordPackWorkspace(catalog: try await fixture.service.loadCatalog())
        let id = try workspace.createLibrary()
        _ = workspace.rename(id, name: "Conflict")
        _ = workspace.addTerm(id, values: TermValues("conflict", "Mine"))
        let prepared = try await fixture.coordinator.prepare(XCTUnwrap(workspace.captureChangeSet().changeSet))
        let url = fixture.root.appendingPathComponent("\(id).csv")
        let outside = Data("pattern,replacement\nconflict,Outside\n".utf8)
        try outside.write(to: url)
        expectEqual(await fixture.coordinator.commit(prepared), .savedPendingRecovery)
        XCTAssertEqual(try Data(contentsOf: url), outside)
        let catalog = try await fixture.service.loadCatalog()
        XCTAssertEqual(catalog.find(id: id)?.state, .awaitingRelease)
        XCTAssertTrue(catalog.find(id: id)!.library.entries.isEmpty)
        XCTAssertEqual(catalog.find(id: "github")?.state, .available)
        expectFalse(try await fixture.service.loadVocabulary().entries.contains { $0.pattern == "conflict" })
        try FileManager.default.removeItem(at: url)
        let recovered = try await fixture.newService().loadCatalog()
        XCTAssertEqual(recovered.find(id: id)?.library.entries.first?.replacement, "Mine")
    }

    func testDeleteRestoreAndPermanentPurgeAreStagedAndStartTheirClockAtCommit() async throws {
        let fixture = try WordPackSaveFixture()
        defer { fixture.remove() }
        let imported = try fixture.service.import(csv: "pattern,replacement\nterm,Term\n", suggestedName: "Terms")
        var workspace = WordPackWorkspace(catalog: try await fixture.service.loadCatalog())
        try workspace.deleteLibrary(imported.id)
        expectNotNil(try await fixture.service.loadCatalog().find(id: imported.id))
        let when = Date(timeIntervalSince1970: 1_800_000_000)
        let changes = try XCTUnwrap(workspace.captureChangeSet().changeSet)
        let prepared = try await fixture.coordinator.prepare(changes, now: when)
        expectEqual(await fixture.coordinator.commit(prepared), .saved)
        let deleted = try await fixture.coordinator.recentlyDeleted()
        XCTAssertEqual(deleted.count, 1)
        XCTAssertEqual(deleted[0].deletedAt, when)
        XCTAssertFalse(deleted[0].expired(at: when.addingTimeInterval(29 * 86_400)))
        XCTAssertTrue(deleted[0].expired(at: when.addingTimeInterval(30 * 86_400)))

        workspace.reload(try await fixture.service.loadCatalog(), deleted: deleted)
        let restoredID = try workspace.restoreDeleted(deleted[0].id)
        XCTAssertFalse(workspace.showsAIPermission(restoredID))
        workspace.undo()
        XCTAssertEqual(workspace.recentlyDeleted.count, 1)
        try workspace.deletePermanently(deleted[0].id)
        expectEqual(try await fixture.coordinator.recentlyDeleted().count, 1)
        let purge = try await fixture.coordinator.prepare(XCTUnwrap(workspace.captureChangeSet().changeSet), now: when)
        expectEqual(await fixture.coordinator.commit(purge), .saved)
        expectTrue(try await fixture.coordinator.recentlyDeleted().isEmpty)
    }

    func testPermissionIsContentBoundAndOutsideReplacementDoesNotInheritIt() async throws {
        let fixture = try WordPackSaveFixture()
        defer { fixture.remove() }
        let imported = try fixture.service.import(csv: "pattern,replacement\nterm,Term\n", suggestedName: "Terms")
        var workspace = WordPackWorkspace(catalog: try await fixture.service.loadCatalog())
        _ = workspace.setAIPermission(imported.id, permitted: true)
        try workspace.setEnabled(imported.id, enabled: true)
        let prepared = try await fixture.coordinator.prepare(XCTUnwrap(workspace.captureChangeSet().changeSet))
        expectEqual(await fixture.coordinator.commit(prepared), .saved)
        expectTrue(try await fixture.service.loadVocabulary().aiEntries.contains { $0.pattern == "term" })
        try Data("pattern,replacement\nterm,Changed\n".utf8).write(
            to: fixture.root.appendingPathComponent("\(imported.id).csv"), options: .atomic)
        expectFalse(try await fixture.service.loadVocabulary().aiEntries.contains { $0.pattern == "term" })
    }

    func testUnreadableAndNewerBuiltInEditsPauseOnlyThatBuiltIn() async throws {
        let fixture = try WordPackSaveFixture()
        defer { fixture.remove() }
        fixture.service.settings.enabledLibraryIds = ["github", "ai-terminology"]
        let edits = BuiltInLibraryOverlay.editsURL(root: fixture.root, id: "github")
        try FileManager.default.createDirectory(at: edits, withIntermediateDirectories: true)
        var catalog = try await fixture.service.loadCatalog()
        XCTAssertEqual(catalog.find(id: "github")?.state, .unreadable)
        XCTAssertTrue(catalog.find(id: "github")!.library.entries.isEmpty)
        XCTAssertEqual(catalog.find(id: "ai-terminology")?.state, .available)
        try FileManager.default.removeItem(at: edits)
        try Data(#"{"version":2}"#.utf8).write(to: edits)
        catalog = try await fixture.service.loadCatalog()
        XCTAssertEqual(catalog.find(id: "github")?.state, .newer)
        XCTAssertTrue(catalog.find(id: "github")!.library.entries.isEmpty)
    }

    func testBuiltInOffAndPreviousCopySurviveSaveAndRestart() async throws {
        let fixture = try WordPackSaveFixture()
        defer { fixture.remove() }
        var workspace = WordPackWorkspace(catalog: try await fixture.service.loadCatalog())
        let row = try XCTUnwrap(workspace.rowsOf("github").first { $0.row.values.spoken == "get hub" })
        try workspace.setTermEnabled("github", rowID: row.rowID, enabled: false)
        let first = try await fixture.coordinator.prepare(XCTUnwrap(workspace.captureChangeSet().changeSet))
        expectEqual(await fixture.coordinator.commit(first), .saved)
        let catalog = try await fixture.newService().loadCatalog()
        XCTAssertEqual(catalog.find(id: "github")?.edits?.terms.first?.intent, .off)
        workspace.reload(catalog)
        let off = try XCTUnwrap(workspace.rowsOf("github").first { $0.row.values.spoken == "get hub" })
        _ = workspace.editTerm("github", rowID: off.rowID, values: TermValues("get hub", "Mine", true, false))
        let second = try await fixture.coordinator.prepare(XCTUnwrap(workspace.captureChangeSet().changeSet))
        expectEqual(await fixture.coordinator.commit(second), .saved)
        let previous = fixture.root.appendingPathComponent("edits/github.previous.json")
        XCTAssertEqual(
            BuiltInLibraryOverlay.read(libraryID: "github", data: try Data(contentsOf: previous)).edits?.terms.first?.intent,
            .off)
    }

    func testEditMadeWhileSavingRemainsDirtyAfterAcknowledgement() async throws {
        let fixture = try WordPackSaveFixture()
        defer { fixture.remove() }
        var workspace = WordPackWorkspace(catalog: try await fixture.service.loadCatalog())
        let id = try workspace.createLibrary()
        _ = workspace.rename(id, name: "Team")
        _ = workspace.addTerm(id, values: TermValues("term", "A"))
        let changes = try XCTUnwrap(workspace.captureChangeSet().changeSet)
        let prepared = try await fixture.coordinator.prepare(changes)
        let row = workspace.rowsOf(id)[0]
        _ = workspace.editTerm(id, rowID: row.rowID, values: TermValues("term", "B"))
        expectEqual(await fixture.coordinator.commit(prepared), .saved)
        workspace.markSaved(changes, catalog: try await fixture.service.loadCatalog(), deleted: [])
        XCTAssertTrue(workspace.hasUnsavedChanges)
        XCTAssertEqual(workspace.rowsOf(id)[0].row.values.written, "B")
        expectEqual(try await fixture.service.loadCatalog().find(id: id)?.library.entries.first?.replacement, "A")
    }

    func testPathEscapeAndSymlinkCannotBeMaterialized() throws {
        let fixture = try WordPackSaveFixture()
        defer { fixture.remove() }
        XCTAssertThrowsError(try WordPackMaterializer.safeURL(root: fixture.root, relativePath: "../elsewhere.csv"))
        let link = fixture.root.appendingPathComponent("linked")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: fixture.root)
        XCTAssertThrowsError(try WordPackMaterializer.safeURL(root: fixture.root, relativePath: "linked/term.csv"))
    }
}

private func expectEqual<T: Equatable>(_ value: T, _ expected: T, file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertEqual(value, expected, file: file, line: line)
}

private func expectTrue(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertTrue(value, file: file, line: line)
}

private func expectFalse(_ value: Bool, file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertFalse(value, file: file, line: line)
}

private func expectNotNil<T>(_ value: T?, file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertNotNil(value, file: file, line: line)
}

private struct WordPackSaveFixture {
    let directory: StorageTestDirectory
    let defaults: StorageTestDefaults
    let root: URL
    let store: PersistenceStore
    let service: DictionaryLibraryService
    let coordinator: WordPackSaveCoordinator

    init() throws {
        directory = try StorageTestDirectory()
        defaults = StorageTestDefaults()
        root = directory.url.resolvingSymlinksInPath().appendingPathComponent("Libraries")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        service = DictionaryLibraryService(
            librariesDirectory: root, settings: DictionaryLibrarySettings(defaults: defaults.defaults),
            persistenceStore: store)
        coordinator = WordPackSaveCoordinator(store: store, service: service)
    }

    func newService() -> DictionaryLibraryService {
        DictionaryLibraryService(
            librariesDirectory: root, settings: DictionaryLibrarySettings(defaults: defaults.defaults),
            persistenceStore: store)
    }

    func remove() {
        store.closeConnection()
        directory.remove()
        defaults.remove()
    }
}
