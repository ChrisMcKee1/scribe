import XCTest

@testable import Scribe

final class RecentlyDeletedStoreTests: XCTestCase {
    func testServiceMovesDeletedPackAndRestoresThroughSQLiteBackedMetadata() async throws {
        let directory = try StorageTestDirectory()
        defer { directory.remove() }
        let defaults = StorageTestDefaults()
        defer { defaults.remove() }
        let store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        let librariesDirectory = directory.url.appendingPathComponent("Libraries", isDirectory: true)
        try FileManager.default.createDirectory(at: librariesDirectory, withIntermediateDirectories: true)
        try """
        # name: Team terms
        pattern,replacement
        kube,Kubernetes
        """.write(
            to: librariesDirectory.appendingPathComponent("team-terms.csv", isDirectory: false),
            atomically: true,
            encoding: .utf8)

        let service = DictionaryLibraryService(
            librariesDirectory: librariesDirectory,
            settings: DictionaryLibrarySettings(defaults: defaults.defaults),
            persistenceStore: store)
        let catalog = try await service.loadCatalog()
        var workspace = LibraryWorkspace(catalog: catalog)
        workspace.deleteLibrary("team-terms")
        try service.save(changeSet: try XCTUnwrap(workspace.captureChangeSet().changeSet))

        let deleted = try await service.listRecentlyDeleted()
        let entry = try XCTUnwrap(deleted.first)
        XCTAssertEqual(entry.originalID, "team-terms")
        XCTAssertEqual(entry.name, "Team terms")
        XCTAssertEqual(entry.termCount, 1)
        XCTAssertNotNil(try store.readStringSetting(key: DictionaryLibraryService.recentlyDeletedKey))

        var restoreWorkspace = LibraryWorkspace(catalog: try await service.loadCatalog())
        restoreWorkspace.restoreRecentlyDeleted(entry, restoreAsID: "team-terms-2")
        try service.save(changeSet: try XCTUnwrap(restoreWorkspace.captureChangeSet().changeSet))

        let restored = try await service.loadCatalog()
        XCTAssertNotNil(restored.find(id: "team-terms-2"))
        XCTAssertTrue(restored.recentlyDeleted.isEmpty)
    }

    func testEntryNamesParseAndNextNameSkipsTakenSameSecond() throws {
        let parsed = try XCTUnwrap(RecentlyDeletedStore.parseEntryName("20260924T201000Z.team-terms.csv"))
        XCTAssertEqual(parsed.sequence, 1)
        XCTAssertEqual(parsed.originalFileName, "team-terms.csv")

        let sequenced = try XCTUnwrap(RecentlyDeletedStore.parseEntryName("20260924T201000Z-2.Zulu Notes.csv"))
        XCTAssertEqual(sequenced.sequence, 2)
        XCTAssertEqual(sequenced.originalFileName, "Zulu Notes.csv")

        XCTAssertNil(RecentlyDeletedStore.parseEntryName("20260924T201000Z-1.team-terms.csv"))
        XCTAssertNil(RecentlyDeletedStore.parseEntryName("20260924T201000Z-02.team-terms.csv"))
        XCTAssertNil(RecentlyDeletedStore.parseEntryName("20260924T201000Z.team-terms.txt"))
        XCTAssertNil(RecentlyDeletedStore.parseEntryName("2026-09-24.team-terms.csv"))
        XCTAssertEqual(RecentlyDeletedStore.originalID(originalFileName: "github.csv"), "custom-github")

        XCTAssertEqual(
            RecentlyDeletedStore.nextEntryName(
                originalFileName: "a.csv",
                stamp: parsed.deletedAt,
                taken: ["20260924T201000Z.a.csv"]),
            "20260924T201000Z-2.a.csv")
    }

    func testRetentionRemovesOnlyParsedEntriesAfterThirtyDays() throws {
        let deleted = try XCTUnwrap(RecentlyDeletedStore.parseEntryName("20260924T201000Z.team-terms.csv"))
        let entry = RecentlyDeletedLibrary(
            entryName: "20260924T201000Z.team-terms.csv",
            originalID: "team-terms",
            name: "Team terms",
            termCount: 1,
            deletedAt: deleted.deletedAt,
            state: .available,
            contentHash: nil)
        let unreadable = RecentlyDeletedLibrary(
            entryName: "not-a-stamp.lost.csv",
            originalID: "lost",
            name: "Lost",
            termCount: 1,
            deletedAt: deleted.deletedAt.addingTimeInterval(-400 * 24 * 60 * 60),
            state: .unreadable,
            contentHash: nil)

        XCTAssertTrue(
            RecentlyDeletedStore.expiredEntryNames(
                [entry],
                now: deleted.deletedAt.addingTimeInterval(29 * 24 * 60 * 60)
            )
            .isEmpty)
        XCTAssertEqual(
            RecentlyDeletedStore.expiredEntryNames(
                [entry, unreadable],
                now: deleted.deletedAt.addingTimeInterval(31 * 24 * 60 * 60)),
            [entry.entryName])
    }
}
