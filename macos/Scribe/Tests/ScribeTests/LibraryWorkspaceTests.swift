import XCTest

@testable import Scribe

final class LibraryWorkspaceTests: XCTestCase {
    func testWorkspaceExposesRowsAndStableRowIDs() {
        let workspace = LibraryDeciderTestSupport.workspace(
            [
                LibraryDeciderTestSupport.library(
                    id: "team-terms",
                    name: "Team terms",
                    rows: [
                        LibraryDeciderTestSupport.customRow("kube", "Kubernetes", rowID: 10),
                        LibraryDeciderTestSupport.customRow("get hub", "GitHub Enterprise", rowID: 11),
                    ])
            ],
            revision: 7)

        XCTAssertEqual(workspace.draft.revision, 7)
        XCTAssertEqual(workspace.rowsOf("team-terms").map(\.rowID), [10, 11])
        XCTAssertEqual(LibraryWorkspace.rowIDIn(workspace.draft, "team-terms", 1), 11)
        XCTAssertNil(LibraryWorkspace.rowIDIn(workspace.draft, "team-terms", 3))
        XCTAssertNil(LibraryWorkspace.rowIDIn(workspace.draft, "missing", 0))
    }

    func testWorkspaceEditsUndoRedoAndCapturesChangeSet() {
        var workspace = LibraryDeciderTestSupport.workspace(
            [
                LibraryDeciderTestSupport.library(
                    id: "team-terms",
                    name: "Team terms",
                    rows: [
                        LibraryDeciderTestSupport.customRow("kube", "Kubernetes", rowID: 10)
                    ])
            ],
            revision: 7)

        XCTAssertFalse(workspace.hasUnsavedChanges)
        XCTAssertEqual(
            workspace.addTerm("team-terms", values: TermValues("get hub", "GitHub Enterprise")).applied,
            true)
        XCTAssertTrue(workspace.hasUnsavedChanges)
        XCTAssertTrue(workspace.canUndo)
        XCTAssertEqual(workspace.undoLabel, "Add term")
        XCTAssertEqual(workspace.rowsOf("team-terms").map(\.row.values.spoken), ["kube", "get hub"])

        workspace.undo()
        XCTAssertEqual(workspace.rowsOf("team-terms").map(\.row.values.spoken), ["kube"])
        XCTAssertTrue(workspace.canRedo)

        workspace.redo()
        let capture = workspace.captureChangeSet()
        XCTAssertTrue(capture.issues.isEmpty)
        XCTAssertEqual(capture.changeSet?.writes.count, 1)
        XCTAssertEqual(capture.changeSet?.writes.first?.content?.entries.map(\.pattern), ["kube", "get hub"])

        workspace.markSaved()
        XCTAssertFalse(workspace.hasUnsavedChanges)
    }

    func testWorkspaceValidationBlocksDuplicateSpokenForms() {
        var workspace = LibraryDeciderTestSupport.workspace(
            [
                LibraryDeciderTestSupport.library(
                    id: "team-terms",
                    name: "Team terms",
                    rows: [
                        LibraryDeciderTestSupport.customRow("kube", "Kubernetes", rowID: 10)
                    ])
            ])

        let result = workspace.addTerm("team-terms", values: TermValues("KUBE", "K8s"))
        XCTAssertFalse(result.applied)
        XCTAssertEqual(result.issue?.kind, .duplicateSpoken)
    }

    func testCreateRenameDeleteResetAndExportEndToEnd() throws {
        var workspace = LibraryDeciderTestSupport.workspace([])
        let id = workspace.createLibrary()

        XCTAssertEqual(workspace.draft.find(id)?.name, "New word pack")
        XCTAssertTrue(workspace.rename(id, name: "Team words").applied)
        XCTAssertTrue(workspace.addTerm(id, values: TermValues("get hub", "GitHub")).applied)

        let capture = workspace.captureChangeSet()
        let content = try XCTUnwrap(capture.changeSet?.writes.first?.content)
        let exported = DictionaryLibraryCsv.exportSharing(
            LibraryCsvContent(
                name: content.name,
                category: content.category,
                description: content.description,
                basedOn: content.basedOn,
                rows: content.entries.map(TermValues.init(entry:))))
        let parsed = DictionaryLibraryCsv.parseImport(exported)
        XCTAssertEqual(parsed.name, "Team words")
        XCTAssertEqual(parsed.terms.map(\.written), ["GitHub"])

        workspace.deleteLibrary(id)
        XCTAssertEqual(workspace.captureChangeSet().changeSet?.deletions.map(\.libraryID), [id])
    }

    func testBuiltInUpdateReviewCanKeepMineOrUseUpdate() {
        let shipped = TermValues("kube", "Kubernetes")
        let mine = TermValues("kube", "K8s")
        let row = DraftTermRow(
            rowID: 1,
            row: LibraryRow(
                key: LibraryTermKey.from("kube"),
                values: mine,
                origin: .edited,
                shipped: shipped,
                edit: BuiltInTermEdit(
                    key: LibraryTermKey.from("kube").value,
                    intent: .edited,
                    base: TermValues("kube", "Kube"),
                    value: mine,
                    acknowledged: nil),
                review: TermReview(yours: mine, updatedBuiltIn: shipped, differing: .written)),
            removalIntent: false,
            legacyEmpty: false)
        var workspace = LibraryDeciderTestSupport.workspace(
            [
                LibraryDeciderTestSupport.library(
                    id: "software-development", name: "Software", builtIn: true, rows: [row])
            ])

        workspace.resolveReview("software-development", rowID: 1, choice: .keepMine)
        XCTAssertNil(workspace.rowsOf("software-development").first?.row.review)
        XCTAssertEqual(workspace.rowsOf("software-development").first?.row.values.written, "K8s")

        workspace.undo()
        workspace.resolveReview("software-development", rowID: 1, choice: .useUpdated)
        XCTAssertEqual(workspace.rowsOf("software-development").first?.row.values.written, "Kubernetes")
        XCTAssertEqual(workspace.captureChangeSet().changeSet?.writes.first?.builtInEdits?.terms.first?.intent, .pinned)
    }

    func testRecentlyDeletedActionsAreCapturedAndClearedOnSave() {
        var workspace = LibraryDeciderTestSupport.workspace([])
        let entry = RecentlyDeletedLibrary(
            entryName: "20260924T201000Z.team-terms.csv",
            originalID: "team-terms",
            name: "Team terms",
            termCount: 1,
            deletedAt: Date(timeIntervalSince1970: 1),
            state: .available,
            contentHash: LibraryContentHash(value: "abc"))

        workspace.restoreRecentlyDeleted(entry, restoreAsID: "team-terms-2")
        XCTAssertEqual(workspace.captureChangeSet().changeSet?.recentlyDeletedActions.first?.kind, .restore)
        workspace.markSaved()
        XCTAssertFalse(workspace.hasUnsavedChanges)

        workspace.deleteRecentlyDeletedPermanently(entry)
        XCTAssertEqual(
            workspace.captureChangeSet().changeSet?.recentlyDeletedActions.first?.kind,
            .deletePermanently)
    }
}
