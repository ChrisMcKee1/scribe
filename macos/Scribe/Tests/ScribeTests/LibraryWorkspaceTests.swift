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
}
