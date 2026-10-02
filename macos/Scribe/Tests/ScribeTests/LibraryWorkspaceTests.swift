import XCTest

@testable import Scribe

final class LibraryWorkspaceTests: XCTestCase {
    func testWorkspaceExposesRowsAndStableRowIDs() {
        let workspace = LibraryDeciderTestSupport.workspace([
            LibraryDeciderTestSupport.library(
                id: "team-terms",
                name: "Team terms",
                rows: [
                    LibraryDeciderTestSupport.customRow("kube", "Kubernetes", rowID: 10),
                    LibraryDeciderTestSupport.customRow("get hub", "GitHub Enterprise", rowID: 11),
                ]),
        ], revision: 7)

        XCTAssertEqual(workspace.draft.revision, 7)
        XCTAssertEqual(workspace.rowsOf("team-terms").map(\.rowID), [10, 11])
        XCTAssertEqual(LibraryWorkspace.rowIDIn(workspace.draft, "team-terms", 1), 11)
        XCTAssertNil(LibraryWorkspace.rowIDIn(workspace.draft, "team-terms", 3))
        XCTAssertNil(LibraryWorkspace.rowIDIn(workspace.draft, "missing", 0))
    }
}
