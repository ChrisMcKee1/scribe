import XCTest
import os

@testable import Scribe

/// What the word pack service tells `AiVocabularyGate`: each load publishes what it read, and a save narrows the
/// permission before it commits, so a request composed under a permission the save withdraws is stopped as it leaves
/// rather than at the next load. Every test uses a gate of its own.
final class WordPackAiPermissionGateTests: XCTestCase {
    private final class Fixture {
        let directory: StorageTestDirectory
        let defaults = StorageTestDefaults()
        let store: PersistenceStore
        let libraries: URL
        let gate = AiVocabularyGate()

        init() throws {
            directory = try StorageTestDirectory()
            store = PersistenceStore(databaseURL: directory.databaseURL)
            try store.initialize()
            libraries = directory.url.appendingPathComponent("Libraries", isDirectory: true)
            try FileManager.default.createDirectory(at: libraries, withIntermediateDirectories: true)
        }

        func remove() {
            defaults.remove()
            directory.remove()
        }

        func service(_ hooks: WordPackSaveHooks = WordPackSaveHooks()) -> DictionaryLibraryService {
            DictionaryLibraryService(
                librariesDirectory: libraries,
                settings: DictionaryLibrarySettings(defaults: defaults.defaults),
                persistenceStore: store,
                saveHooks: hooks,
                vocabularyGate: gate)
        }

        /// Two custom packs that are on and that AI cleanup may use, as packs that existed before the upgrade are.
        func writeTwoPermittedPacks() throws {
            try writeCustom("team", name: "Team", terms: [TermValues("kube", "Kubernetes")])
            try writeCustom("other", name: "Other", terms: [TermValues("helm", "Helm")])
            defaults.defaults.set(["team", "other"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        }

        func writeCustom(_ stem: String, name: String, terms: [TermValues]) throws {
            let data = try DictionaryLibraryCsv.exportManaged(
                LibraryCsvContent(name: name, category: "Custom", description: nil, basedOn: nil, rows: terms))
            try data.write(to: libraries.appendingPathComponent("\(stem).csv", isDirectory: false))
        }

        /// The content hash of what is saved for the custom pack `stem` now.
        func hash(_ stem: String) throws -> String {
            LibraryContentHash(data: try Data(contentsOf: libraries.appendingPathComponent("\(stem).csv"))).value
        }
    }

    func testALoadPublishesWhatItReadToTheGate() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.writeTwoPermittedPacks()

        let vocabulary = try await fixture.service().loadVocabulary()

        XCTAssertEqual(Set(vocabulary.aiScope.permittedContent.keys), ["team", "other"])
        XCTAssertTrue(fixture.gate.covers(vocabulary.aiScope), "the load did not publish its scope")
        let otherContent = AiVocabularyScope(generation: 1, permittedContent: ["team": "not what was accepted"])
        XCTAssertFalse(fixture.gate.covers(otherContent))
    }

    /// The finding's order: the permission is already gone from the gate when the save is ready to commit, and stays
    /// gone when the commit is done, with no load in between to publish it.
    func testASaveThatWithdrawsAPermissionNarrowsTheGateBeforeItCommits() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.writeTwoPermittedPacks()
        let admitted = try await fixture.service().loadVocabulary().aiScope
        XCTAssertEqual(Set(admitted.permittedContent.keys), ["team", "other"])
        XCTAssertTrue(fixture.gate.covers(admitted))
        let gate = fixture.gate
        // Both start true, so a hook that never ran fails the check on what it saw.
        let seen = OSAllocatedUnfairLock(initialState: (before: true, after: true))
        var hooks = WordPackSaveHooks()
        hooks.beforeCommit = { seen.withLock { $0.before = gate.covers(admitted) } }
        hooks.afterCommit = { seen.withLock { $0.after = gate.covers(admitted) } }
        let service = fixture.service(hooks)
        var workspace = LibraryWorkspace(catalog: try await service.loadCatalog())
        workspace.setAiPermission("team", permitted: false)

        try service.save(changeSet: try XCTUnwrap(workspace.captureChangeSet().changeSet))

        let observed = seen.withLock { $0 }
        XCTAssertFalse(observed.before, "the permission was still in force when the save was ready to commit")
        XCTAssertFalse(observed.after, "the permission was still in force when the commit was done")
        XCTAssertFalse(gate.covers(admitted))
        // What was not withdrawn stays: a request that carried only the other pack is still sent.
        let other = AiVocabularyScope(generation: 1, permittedContent: ["other": try fixture.hash("other")])
        XCTAssertTrue(gate.covers(other))
    }

    /// A save that changes one pack's content withdraws that pack for the content it was permitted for, and leaves a
    /// pack it did not touch as it was.
    func testASaveThatRewritesAPackWithdrawsOnlyThatPack() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.writeTwoPermittedPacks()
        let admitted = try await fixture.service().loadVocabulary().aiScope
        let service = fixture.service()
        var workspace = LibraryWorkspace(catalog: try await service.loadCatalog())
        _ = workspace.addTerm("other", values: TermValues("flux", "Flux"))

        try service.save(changeSet: try XCTUnwrap(workspace.captureChangeSet().changeSet))

        XCTAssertFalse(fixture.gate.covers(admitted), "a pack that was rewritten was still sent")
        let team = AiVocabularyScope(generation: 1, permittedContent: ["team": try fixture.hash("team")])
        XCTAssertTrue(fixture.gate.covers(team), "a pack the save did not touch was withdrawn")
    }

    /// A save that does not commit changes nothing, the permission included.
    func testASaveThatDoesNotCommitPutsThePermissionBack() async throws {
        let fixture = try Fixture()
        defer { fixture.remove() }
        try fixture.writeTwoPermittedPacks()
        let admitted = try await fixture.service().loadVocabulary().aiScope
        let store = fixture.store
        var hooks = WordPackSaveHooks()
        // Another writer changes the stored state between the save's read of it and its commit, so the commit is
        // refused.
        hooks.beforeCommit = {
            try store.writeStringSetting(key: DictionaryLibraryService.libraryStateKey, value: "{\"changed\":true}")
        }
        let service = fixture.service(hooks)
        var workspace = LibraryWorkspace(catalog: try await fixture.service().loadCatalog())
        workspace.setAiPermission("team", permitted: false)
        let changeSet = try XCTUnwrap(workspace.captureChangeSet().changeSet)

        XCTAssertThrowsError(try service.save(changeSet: changeSet)) { error in
            XCTAssertEqual(error as? WordPackSaveError, .changedWhileSaving)
        }

        XCTAssertTrue(fixture.gate.covers(admitted), "a save that changed nothing withdrew a permission")
    }
}
