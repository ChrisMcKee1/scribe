import XCTest

@testable import Scribe

/// The word pack save journal: a save either commits and its files are finished, by itself or by the next load, or
/// it changes nothing. A hook that throws stops a save where it runs, as the app stopping would; a fresh service on
/// the same folder and database then plays the next launch.
final class WordPackSaveJournalTests: XCTestCase {
    private struct SimulatedStop: Error {}

    func testAnUninterruptedSaveCommitsStateAndFilesAndRetiresItsJournal() async throws {
        let fixture = try JournalFixture()
        defer { fixture.remove() }
        let plan = try await fixture.planEverySaveStep()

        try fixture.service().save(changeSet: plan.changeSet)

        try fixture.assertSaved(plan)
    }

    func testAStopBeforeTheCommitChangesNothing() async throws {
        let fixture = try JournalFixture()
        defer { fixture.remove() }
        let plan = try await fixture.planEverySaveStep()
        let before = try fixture.snapshot()
        let stopping = fixture.service(WordPackSaveHooks(beforeCommit: { throw SimulatedStop() }))

        XCTAssertThrowsError(try stopping.save(changeSet: plan.changeSet))

        XCTAssertEqual(try fixture.snapshot(), before)
        _ = try await fixture.service().loadCatalog()
        XCTAssertEqual(try fixture.snapshot(), before)
        try fixture.service().save(changeSet: plan.changeSet)
        try fixture.assertSaved(plan)
    }

    func testAStopRightAfterTheCommitIsFinishedByTheNextLoad() async throws {
        let fixture = try JournalFixture()
        defer { fixture.remove() }
        let plan = try await fixture.planEverySaveStep()
        let teamBefore = try Data(contentsOf: fixture.url("team.csv"))
        let stopping = fixture.service(WordPackSaveHooks(afterCommit: { throw SimulatedStop() }))

        XCTAssertThrowsError(try stopping.save(changeSet: plan.changeSet))

        XCTAssertNotNil(try fixture.storedJournal())
        XCTAssertEqual(try Data(contentsOf: fixture.url("team.csv")), teamBefore)
        XCTAssertTrue(fixture.exists("old.csv"))
        let catalog = try await fixture.service().loadCatalog()
        try fixture.assertSaved(plan)
        let team = try XCTUnwrap(catalog.find(id: "team"))
        XCTAssertTrue(team.library.entries.contains { $0.pattern == "helm chart" })
        XCTAssertNotNil(catalog.find(id: plan.freshID))
    }

    func testAStopAfterAnyFileStepIsFinishedByTheNextLoadAndAgainChangesNothing() async throws {
        var stopsTried = 0
        for stopAfter in 0..<20 {
            let fixture = try JournalFixture()
            defer { fixture.remove() }
            let plan = try await fixture.planEverySaveStep()
            var hooks = WordPackSaveHooks()
            hooks.afterOperation = { index in
                if index == stopAfter {
                    throw SimulatedStop()
                }
            }
            let stopping = fixture.service(hooks)
            do {
                try stopping.save(changeSet: plan.changeSet)
                try fixture.assertSaved(plan)
                break
            } catch {
                stopsTried += 1
            }
            XCTAssertNotNil(try fixture.storedJournal(), "stopped after step \(stopAfter)")

            _ = try await fixture.service().loadCatalog()
            try fixture.assertSaved(plan)
            let finished = try fixture.snapshot()
            _ = try await fixture.service().loadCatalog()
            XCTAssertEqual(try fixture.snapshot(), finished, "a second load changed files after step \(stopAfter)")
        }
        // One move into Recently deleted, a permanent delete, a restore and three writes.
        XCTAssertEqual(stopsTried, 6)
    }

    func testAFileAnotherAppChangedBeforeTheFilesFinishedIsKeptRatherThanReplaced() async throws {
        let fixture = try JournalFixture()
        defer { fixture.remove() }
        let plan = try await fixture.planEverySaveStep()
        let stopping = fixture.service(WordPackSaveHooks(afterCommit: { throw SimulatedStop() }))
        XCTAssertThrowsError(try stopping.save(changeSet: plan.changeSet))
        let foreign = Data("pattern,replacement\nforeign,Foreign\n".utf8)
        try foreign.write(to: fixture.url("team.csv"))

        _ = try await fixture.service().loadCatalog()

        try fixture.assertSaved(plan, keptFiles: 1)
        let kept = try XCTUnwrap(fixture.keptFiles().first)
        XCTAssertTrue(kept.hasSuffix(".team.csv"))
        XCTAssertEqual(try Data(contentsOf: fixture.url("\(WordPackSaveJournal.keptFolder)/\(kept)")), foreign)
    }

    func testASaveIsRefusedWhileAnEarlierSaveCannotFinishAndGoesThroughOnceItCan() async throws {
        let fixture = try JournalFixture()
        defer { fixture.remove() }
        let plan = try await fixture.planEverySaveStep()
        let stopping = fixture.service(WordPackSaveHooks(afterCommit: { throw SimulatedStop() }))
        XCTAssertThrowsError(try stopping.save(changeSet: plan.changeSet))
        // A folder where the new pack's file goes: its write cannot finish until the folder is gone.
        let blocker = fixture.url("\(plan.freshID).csv")
        try FileManager.default.createDirectory(at: blocker, withIntermediateDirectories: true)

        let catalog = try await fixture.service().loadCatalog()
        XCTAssertNotNil(try fixture.storedJournal())
        let stateBefore = try fixture.store.readStringSetting(key: DictionaryLibraryService.libraryStateKey)
        var workspace = LibraryWorkspace(catalog: catalog)
        workspace.setEnabled("team", enabled: false)
        let refused = try XCTUnwrap(workspace.captureChangeSet().changeSet)
        XCTAssertThrowsError(try fixture.service().save(changeSet: refused)) { error in
            XCTAssertEqual(error as? WordPackSaveError, .earlierSaveUnfinished)
        }
        XCTAssertEqual(try fixture.store.readStringSetting(key: DictionaryLibraryService.libraryStateKey), stateBefore)

        try FileManager.default.removeItem(at: blocker)
        _ = try await fixture.service().loadCatalog()
        try fixture.assertSaved(plan)
        try fixture.service().save(changeSet: refused)
        XCTAssertFalse(try XCTUnwrap(fixture.storedState()).enabledIdSet.contains("team"))
    }

    func testAJournalThisBuildCannotTrustIsRetiredWithoutTouchingAnyFile() async throws {
        let fixture = try JournalFixture()
        defer { fixture.remove() }
        _ = try await fixture.service().loadCatalog()
        let before = try fixture.snapshot()
        let escaping = WordPackSaveJournal(
            version: WordPackSaveJournal.currentVersion,
            id: UUID().uuidString,
            generation: 99,
            enabledProjection: [],
            operations: [
                WordPackSaveOperation(
                    kind: .write, path: "../escape.csv", source: nil, preImage: nil, image: Data("x".utf8))
            ])
        try fixture.store.writeStringSetting(key: WordPackSaveJournal.key, value: try escaping.encoded())

        _ = try await fixture.service().loadCatalog()

        XCTAssertNil(try fixture.storedJournal())
        XCTAssertEqual(try fixture.snapshot(), before)
        XCTAssertFalse(
            FileManager.default.fileExists(
                atPath: fixture.libraries.deletingLastPathComponent().appendingPathComponent("escape.csv").path))
    }

    func testAJournalFromANewerBuildIsLeftForItAndRefusesSaves() async throws {
        let fixture = try JournalFixture()
        defer { fixture.remove() }
        let plan = try await fixture.planEverySaveStep()
        let newer = #"{"version":2,"id":"later","operations":[]}"#
        try fixture.store.writeStringSetting(key: WordPackSaveJournal.key, value: newer)

        _ = try await fixture.service().loadCatalog()

        XCTAssertEqual(try fixture.storedJournal(), newer)
        XCTAssertThrowsError(try fixture.service().save(changeSet: plan.changeSet)) { error in
            XCTAssertEqual(error as? WordPackSaveError, .newerSaveUnfinished)
        }
        XCTAssertTrue(fixture.exists("old.csv"))
    }

    func testASaveKeepsAIPermissionBoundToTheContentOfEveryPack() async throws {
        let fixture = try JournalFixture()
        defer { fixture.remove() }
        fixture.defaults.defaults.set(["team", "other"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        try fixture.writeCustom("team", name: "Team", terms: [TermValues("kube", "Kubernetes")])
        try fixture.writeCustom("other", name: "Other", terms: [TermValues("argo", "Argo CD")])
        var workspace = LibraryWorkspace(catalog: try await fixture.service().loadCatalog())
        _ = workspace.addTerm("other", values: TermValues("flux", "Flux CD"))

        try fixture.service().save(changeSet: try XCTUnwrap(workspace.captureChangeSet().changeSet))

        let permitted = try await fixture.service().loadVocabulary().aiScope.permittedLibraryIds
        XCTAssertTrue(permitted.contains("team"), "a pack the save did not write keeps its accepted content")
        XCTAssertTrue(permitted.contains("other"), "a pack the save wrote is accepted with its new content")
        try Data("pattern,replacement\nkube,Kube\n".utf8).write(to: fixture.url("team.csv"))
        let changedOutside = try await fixture.service().loadVocabulary().aiScope.permittedLibraryIds
        XCTAssertFalse(changedOutside.contains("team"), "content changed outside Scribe is kept from AI cleanup")
    }

    func testTemporaryFilesAStoppedWriteLeftAreRemovedByWholeNameOnly() async throws {
        let fixture = try JournalFixture()
        defer { fixture.remove() }
        let stale = WordPackDurableFiles.temporaryPrefix + UUID().uuidString + WordPackDurableFiles.temporarySuffix
        let other = WordPackDurableFiles.temporaryPrefix + "notes" + WordPackDurableFiles.temporarySuffix
        try Data("partial".utf8).write(to: fixture.url(stale))
        try Data("mine".utf8).write(to: fixture.url(other))

        _ = try await fixture.service().loadCatalog()

        XCTAssertFalse(fixture.exists(stale))
        XCTAssertTrue(fixture.exists(other))
    }

    func testJournalReadTrustsOnlyPathsInsideTheWordPackFolders() throws {
        func journal(_ operations: [WordPackSaveOperation]) throws -> String {
            let journal = WordPackSaveJournal(
                version: WordPackSaveJournal.currentVersion,
                id: "id",
                generation: 1,
                enabledProjection: [],
                operations: operations)
            return try journal.encoded()
        }
        let image = Data("x".utf8)
        let hash = LibraryContentHash(data: image)
        let allowed: [WordPackSaveOperation] = [
            .write("team.csv", preImage: nil, image: image),
            .write("edits/github.json", preImage: hash, image: image),
            .remove("deleted/20260101T000000Z.gone.csv", preImage: hash),
            .move(from: "old.csv", to: "deleted/20260101T000000Z.old.csv"),
        ]
        guard case .trusted = WordPackSaveJournal.read(try journal(allowed)) else {
            return XCTFail("a journal of allowed paths reads as trusted")
        }
        let refused: [[WordPackSaveOperation]] = [
            [.write("../escape.csv", preImage: nil, image: image)],
            [.write("/tmp/escape.csv", preImage: nil, image: image)],
            [.write("edits/../escape.json", preImage: nil, image: image)],
            [.write("edits/github.csv", preImage: nil, image: image)],
            [.write("notes.txt", preImage: nil, image: image)],
            [.remove("deleted/a/b.csv", preImage: hash)],
            [.move(from: "../old.csv", to: "deleted/old.csv")],
            [.write("team.csv", preImage: nil, image: image), .remove("TEAM.csv", preImage: hash)],
            [WordPackSaveOperation(kind: .write, path: "team.csv", source: nil, preImage: nil, image: nil)],
        ]
        for operations in refused {
            XCTAssertEqual(WordPackSaveJournal.read(try journal(operations)), .untrusted)
        }
        XCTAssertEqual(WordPackSaveJournal.read("not json"), .untrusted)
        XCTAssertEqual(WordPackSaveJournal.read(#"{"version":3}"#), .newer(version: 3))
    }

    func testAMoveNeverReplacesAFileAlreadyThere() throws {
        let directory = try StorageTestDirectory()
        defer { directory.remove() }
        let source = directory.url.appendingPathComponent("source.csv")
        let target = directory.url.appendingPathComponent("target.csv")
        try Data("source".utf8).write(to: source)
        try Data("target".utf8).write(to: target)

        XCTAssertThrowsError(try WordPackDurableFiles.move(source, to: target)) { error in
            XCTAssertEqual((error as? POSIXError)?.code, .EEXIST)
        }
        XCTAssertEqual(try Data(contentsOf: source), Data("source".utf8))
        XCTAssertEqual(try Data(contentsOf: target), Data("target".utf8))
    }

    func testAConditionalCommitWritesNothingUnlessEveryConditionHolds() throws {
        let directory = try StorageTestDirectory()
        defer { directory.remove() }
        let store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        try store.writeStringSetting(key: "a", value: "1")

        XCTAssertFalse(
            try store.commitStringSettings(
                expecting: [StoredStringSetting(key: "a", value: "2")],
                writing: [StoredStringSetting(key: "b", value: "x"), StoredStringSetting(key: "a", value: nil)]))
        XCTAssertEqual(try store.readStringSetting(key: "a"), "1")
        XCTAssertNil(try store.readStringSetting(key: "b"))

        XCTAssertTrue(
            try store.commitStringSettings(
                expecting: [StoredStringSetting(key: "a", value: "1"), StoredStringSetting(key: "b", value: nil)],
                writing: [StoredStringSetting(key: "b", value: "x"), StoredStringSetting(key: "a", value: nil)]))
        XCTAssertNil(try store.readStringSetting(key: "a"))
        XCTAssertEqual(try store.readStringSetting(key: "b"), "x")
    }
}

/// What `planEverySaveStep` stages, and the new pack's id.
private struct SavePlan {
    let changeSet: LibraryChangeSet
    let freshID: String
}

private final class JournalFixture {
    let directory: StorageTestDirectory
    let defaults = StorageTestDefaults()
    let store: PersistenceStore
    let libraries: URL

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
            saveHooks: hooks)
    }

    /// Stages a save with one of every file step: a custom pack edited, one deleted, one created, a built-in pack
    /// edited, a Recently deleted entry deleted for good and another restored.
    func planEverySaveStep() async throws -> SavePlan {
        defaults.defaults.set(["team", "old", "github"], forKey: DictionaryLibrarySettings.enabledIdsKey)
        try writeCustom("team", name: "Team", terms: [TermValues("kube", "Kubernetes")])
        try writeCustom("old", name: "Old", terms: [TermValues("legacy term", "LegacyTerm")])
        try writeCustom("deleted/20260101T000000Z.gone", name: "Gone", terms: [TermValues("gone", "Gone")])
        try writeCustom("deleted/20260102T000000Z.back", name: "Back", terms: [TermValues("back", "Back")])

        let catalog = try await service().loadCatalog()
        var workspace = LibraryWorkspace(catalog: catalog)
        _ = workspace.addTerm("team", values: TermValues("helm chart", "Helm chart"))
        workspace.deleteLibrary("old")
        let freshID = workspace.createLibrary(name: "Fresh")
        _ = workspace.addTerm(freshID, values: TermValues("fresh term", "FreshTerm"))
        _ = workspace.addTerm("github", values: TermValues("gh cli", "GitHub CLI"))
        let gone = try XCTUnwrap(catalog.recentlyDeleted.first { $0.entryName.hasSuffix(".gone.csv") })
        let back = try XCTUnwrap(catalog.recentlyDeleted.first { $0.entryName.hasSuffix(".back.csv") })
        workspace.deleteRecentlyDeletedPermanently(gone)
        workspace.restoreRecentlyDeleted(back, restoreAsID: "back")
        return SavePlan(changeSet: try XCTUnwrap(workspace.captureChangeSet().changeSet), freshID: freshID)
    }

    /// Everything `planEverySaveStep`'s save commits holds, on disk and in the store, and the journal is retired.
    func assertSaved(_ plan: SavePlan, keptFiles expectedKept: Int = 0, line: UInt = #line) throws {
        XCTAssertNil(try storedJournal(), line: line)
        let team = DictionaryLibraryCsv.parseManaged(try Data(contentsOf: url("team.csv")))
        XCTAssertTrue(team.terms.contains { $0.spoken == "helm chart" }, line: line)
        XCTAssertFalse(exists("old.csv"), line: line)
        XCTAssertEqual(deletedEntries().filter { $0.hasSuffix(".old.csv") }.count, 1, line: line)
        XCTAssertTrue(exists("\(plan.freshID).csv"), line: line)
        let github = BuiltInLibraryOverlay.read(
            libraryID: "github", data: try Data(contentsOf: url("edits/github.json")))
        XCTAssertTrue(github.edits?.terms.contains { $0.key == "gh cli" } == true, line: line)
        XCTAssertFalse(exists("deleted/20260101T000000Z.gone.csv"), line: line)
        XCTAssertFalse(exists("deleted/20260102T000000Z.back.csv"), line: line)
        XCTAssertTrue(exists("back.csv"), line: line)
        XCTAssertEqual(keptFiles().count, expectedKept, line: line)

        let state = try XCTUnwrap(try storedState(), line: line)
        XCTAssertEqual(state.enabledIdSet, ["team", "github", plan.freshID.lowercased()], line: line)
        let projection = defaults.defaults.stringArray(forKey: DictionaryLibrarySettings.enabledIdsKey) ?? []
        XCTAssertEqual(Set(projection.map { $0.lowercased() }), state.enabledIdSet, line: line)
        XCTAssertEqual(state.acceptedContent["team"], try hash("team.csv"), line: line)
        XCTAssertEqual(state.acceptedContent[plan.freshID.lowercased()], try hash("\(plan.freshID).csv"), line: line)
        XCTAssertEqual(state.acceptedContent["github"], try hash("edits/github.json"), line: line)
        XCTAssertNil(state.acceptedContent["old"], line: line)
        XCTAssertNil(state.acceptedContent["back"], line: line)

        let rawDeleted = try XCTUnwrap(
            try store.readStringSetting(key: DictionaryLibraryService.recentlyDeletedKey), line: line)
        let deleted = try JSONDecoder().decode([String: RecentlyDeletedMetadata].self, from: Data(rawDeleted.utf8))
        XCTAssertEqual(deleted.values.map(\.originalID), ["old"], line: line)
    }

    func writeCustom(_ stem: String, name: String, terms: [TermValues]) throws {
        let data = try DictionaryLibraryCsv.exportManaged(
            LibraryCsvContent(name: name, category: "Custom", description: nil, basedOn: nil, rows: terms))
        let target = url("\(stem).csv")
        try FileManager.default.createDirectory(
            at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: target)
    }

    func url(_ path: String) -> URL {
        libraries.appendingPathComponent(path, isDirectory: false)
    }

    func exists(_ path: String) -> Bool {
        FileManager.default.fileExists(atPath: url(path).path)
    }

    func hash(_ path: String) throws -> String {
        LibraryContentHash(data: try Data(contentsOf: url(path))).value
    }

    func storedJournal() throws -> String? {
        try store.readStringSetting(key: WordPackSaveJournal.key)
    }

    func storedState() throws -> LibraryLocalState? {
        guard let raw = try store.readStringSetting(key: DictionaryLibraryService.libraryStateKey) else {
            return nil
        }
        return try JSONDecoder().decode(LibraryLocalState.self, from: Data(raw.utf8))
    }

    func deletedEntries() -> [String] {
        (try? FileManager.default.contentsOfDirectory(atPath: url("deleted").path)) ?? []
    }

    func keptFiles() -> [String] {
        (try? FileManager.default.contentsOfDirectory(atPath: url(WordPackSaveJournal.keptFolder).path)) ?? []
    }

    /// Every file under the word packs folder with its bytes, the stored word pack rows and the legacy projection.
    func snapshot() throws -> FixtureSnapshot {
        var files: [String: Data] = [:]
        let fileManager = FileManager.default
        for path in try fileManager.subpathsOfDirectory(atPath: libraries.path) {
            var isDirectory: ObjCBool = false
            let file = url(path)
            if fileManager.fileExists(atPath: file.path, isDirectory: &isDirectory), !isDirectory.boolValue {
                files[path] = try Data(contentsOf: file)
            }
        }
        return FixtureSnapshot(
            files: files,
            state: try store.readStringSetting(key: DictionaryLibraryService.libraryStateKey),
            recentlyDeleted: try store.readStringSetting(key: DictionaryLibraryService.recentlyDeletedKey),
            journal: try storedJournal(),
            projection: Set(defaults.defaults.stringArray(forKey: DictionaryLibrarySettings.enabledIdsKey) ?? []))
    }
}

private struct FixtureSnapshot: Equatable {
    let files: [String: Data]
    let state: String?
    let recentlyDeleted: String?
    let journal: String?
    let projection: Set<String>
}
