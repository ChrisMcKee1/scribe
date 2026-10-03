import XCTest

@testable import Scribe

final class LibraryImportPlannerTests: XCTestCase {
    func testPlanCountsAddsDifferencesAndSkippedRows() throws {
        let workspace = makeStandardWorkspace()
        let invalid = LibraryCsvRowError(line: 9, kind: .invalidWholeWord, field: "maybe")
        let (document, _) = LibraryDeciderTestSupport.document(
            name: "Team terms",
            terms: [
                TermValues("kube", "Kubernetes"),
                TermValues("get hub", "GitHub"),
                TermValues("Get Hub ", "GitHub Enterprise", false),
                TermValues("vm", "VM"),
                TermValues("um", ""),
                TermValues("vm", "virtual machine"),
                TermValues("vm", "VM"),
            ],
            errors: [invalid],
            encoding: LibraryTextEncoding(
                codePage: 1252,
                byteOrderMark: false,
                ansiFallback: true,
                invalidBytesReplaced: false))

        let plan = try LibraryImportPlanner.plan(
            document: document,
            target: .existing(libraryID: "team-terms"),
            draft: workspace.draft)

        XCTAssertEqual(
            plan.operations.map(\.kind),
            [
                .alreadyHere, .writtenDifferently, .writtenDifferently, .add,
                .add, .writtenDifferently, .writtenDifferently,
            ])
        XCTAssertEqual(plan.adds, 2)
        XCTAssertEqual(plan.writtenDifferently, 4)
        XCTAssertEqual(plan.alreadyHere, 1)
        XCTAssertEqual(plan.removalRules, 1)
        XCTAssertEqual(plan.skipped, 1)
        XCTAssertEqual(plan.operations[1].existingRowID, 11)
        XCTAssertEqual(plan.operations[1].existingValues, TermValues("get hub", "GitHub Enterprise"))
        XCTAssertEqual(plan.operations[2].existingValues, TermValues("get hub", "GitHub"))
        XCTAssertNil(plan.operations[5].existingRowID)
        XCTAssertEqual(plan.operations[5].existingValues, TermValues("vm", "VM"))
        XCTAssertEqual(plan.operations[6].existingValues, TermValues("vm", "virtual machine"))
        XCTAssertEqual(plan.draftRevision, workspace.draft.revision)
        XCTAssertEqual(
            plan.encoding,
            LibraryTextEncoding(codePage: 1252, byteOrderMark: false, ansiFallback: true, invalidBytesReplaced: false))
        XCTAssertTrue(plan.nonAsciiRows.isEmpty)
    }

    func testRepeatedFileRowsMeetWhatEarlierRowsLeave() throws {
        let workspace = makeStandardWorkspace()
        let (document, _) = LibraryDeciderTestSupport.document(
            name: "Machines",
            terms: [
                TermValues("vm", "VM"),
                TermValues("vm", "virtual machine"),
                TermValues("vm", "VM"),
                TermValues("kube", "Kubernetes"),
                TermValues("kube", "K8s"),
                TermValues("kube", "Kubernetes"),
            ])

        let plan = try LibraryImportPlanner.plan(
            document: document,
            target: .existing(libraryID: "team-terms"),
            draft: workspace.draft)

        XCTAssertEqual(
            plan.operations.map(\.kind),
            [.add, .writtenDifferently, .writtenDifferently, .alreadyHere, .writtenDifferently, .writtenDifferently])
        XCTAssertEqual(plan.adds, 1)
        XCTAssertEqual(plan.writtenDifferently, 4)
        XCTAssertEqual(plan.alreadyHere, 1)
        XCTAssertEqual(plan.operations[2].existingValues, TermValues("vm", "virtual machine"))
        XCTAssertEqual(plan.operations[5].existingRowID, 10)
        XCTAssertEqual(plan.operations[5].existingValues, TermValues("kube", "K8s"))
    }

    func testRenamedBuiltInKeyStillTargetsOriginalRow() throws {
        let github = LibraryDeciderTestSupport.library(
            id: "github",
            name: "GitHub",
            builtIn: true,
            rows: [
                LibraryDeciderTestSupport.builtInRow(
                    key: "get hub",
                    spoken: "git hub",
                    written: "GitHub",
                    origin: .edited,
                    rowID: 1,
                    shipped: TermValues("get hub", "GitHub")),
                LibraryDeciderTestSupport.builtInRow(
                    key: "copilot",
                    spoken: "copilot",
                    written: "Copilot",
                    origin: .shipped,
                    rowID: 2,
                    shipped: TermValues("copilot", "Copilot")),
            ])
        let workspace = LibraryDeciderTestSupport.workspace([github])
        let (document, _) = LibraryDeciderTestSupport.document(
            terms: [
                TermValues("get hub", "GH"),
                TermValues("git hub", "GitHub"),
                TermValues("gh cli", "GitHub CLI"),
            ])

        let plan = try LibraryImportPlanner.plan(
            document: document,
            target: .existing(libraryID: "github"),
            draft: workspace.draft)

        XCTAssertEqual(plan.operations.map(\.kind), [.writtenDifferently, .writtenDifferently, .add])
        XCTAssertEqual(plan.operations[0].existingRowID, 1)
        XCTAssertEqual(plan.operations[1].existingRowID, 1)
        XCTAssertEqual(plan.operations[0].existingValues, TermValues("git hub", "GitHub"))
        XCTAssertEqual(plan.operations[1].existingValues, TermValues("get hub", "GH"))
    }

    func testSuggestedNamePrefersHeaderThenFileThenFallback() throws {
        let workspace = makeStandardWorkspace()
        let (fromHeader, fileName) = LibraryDeciderTestSupport.document(
            name: "Team terms",
            fileName: "whatever.csv",
            terms: [])
        let headerPlan = try LibraryImportPlanner.plan(
            document: fromHeader,
            target: .new(fileName: fileName),
            draft: workspace.draft)
        let (fromFile, path) = LibraryDeciderTestSupport.document(fileName: "C:/Downloads/Release notes.csv", terms: [])
        let filePlan = try LibraryImportPlanner.plan(
            document: fromFile,
            target: .new(fileName: path),
            draft: workspace.draft)
        let (fallback, noPath) = LibraryDeciderTestSupport.document(name: "  ", fileName: nil, terms: [])
        let fallbackPlan = try LibraryImportPlanner.plan(
            document: fallback,
            target: .new(fileName: noPath),
            draft: workspace.draft)

        XCTAssertEqual(headerPlan.suggestedName, "Team terms 2")
        XCTAssertEqual(filePlan.suggestedName, "Release notes")
        XCTAssertEqual(fallbackPlan.suggestedName, "Imported word pack")
    }

    private func makeStandardWorkspace() -> LibraryWorkspace {
        LibraryDeciderTestSupport.workspace([
            LibraryDeciderTestSupport.library(
                id: "github",
                name: "GitHub",
                builtIn: true,
                rows: [
                    LibraryDeciderTestSupport.builtInRow(
                        key: "get hub",
                        spoken: "get hub",
                        written: "GitHub",
                        origin: .shipped,
                        rowID: 1,
                        shipped: TermValues("get hub", "GitHub")),
                    LibraryDeciderTestSupport.builtInRow(
                        key: "copilot",
                        spoken: "copilot",
                        written: "Copilot",
                        origin: .shipped,
                        rowID: 2,
                        shipped: TermValues("copilot", "Copilot")),
                    LibraryDeciderTestSupport.builtInRow(
                        key: "octo cat",
                        spoken: "octo cat",
                        written: "Octocat",
                        origin: .shipped,
                        rowID: 3,
                        shipped: TermValues("octo cat", "Octocat")),
                ]),
            LibraryDeciderTestSupport.library(
                id: "team-terms",
                name: "Team terms",
                rows: [
                    LibraryDeciderTestSupport.customRow("kube", "Kubernetes", rowID: 10),
                    LibraryDeciderTestSupport.customRow("get hub", "GitHub Enterprise", rowID: 11),
                ]),
        ])
    }
}
