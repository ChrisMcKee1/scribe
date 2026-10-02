import XCTest

@testable import Scribe

final class WordPackWorkspaceTests: XCTestCase {
    func testDraftEditsValidationAndStableIdentity() throws {
        var workspace = Self.workspace()
        let row = workspace.rowsOf("team")[0]
        XCTAssertFalse(workspace.hasUnsavedChanges)
        XCTAssertTrue(workspace.editTerm("team", rowID: row.rowID, values: TermValues(" kube ", " K8s ")).applied)
        XCTAssertEqual(workspace.rowsOf("team")[0].rowID, row.rowID)
        XCTAssertEqual(workspace.rowsOf("team")[0].row.values, TermValues("kube", "K8s"))
        XCTAssertTrue(workspace.hasUnsavedChanges)
        _ = workspace.addTerm("team", values: TermValues("KUBE", "Kubernetes"))
        XCTAssertTrue(workspace.validate().contains { $0.kind == .duplicateSpoken })
        XCTAssertNil(workspace.captureChangeSet().changeSet)
    }

    func testRemovalIntentIsRequiredButImportedRemovalIsAlreadyIntentional() throws {
        var workspace = Self.workspace()
        _ = workspace.addTerm("team", values: TermValues("um", ""))
        XCTAssertEqual(workspace.validate().first?.kind, .emptyWrittenWithoutIntent)
        let row = workspace.rowsOf("team").last!
        _ = workspace.editTerm("team", rowID: row.rowID, values: row.row.values, removalIntent: true)
        XCTAssertTrue(workspace.validate().isEmpty)
        workspace.undo()
        XCTAssertEqual(workspace.validate().first?.kind, .emptyWrittenWithoutIntent)
        let document = DictionaryLibraryCsv.parseImport(Data("pattern,replacement\nerm,\n".utf8))
        let plan = try LibraryImportPlanner.plan(
            document: document, target: .existing(libraryID: "team"), draft: workspace.draft)
        _ = try workspace.applyImport(plan, choice: .keepMine)
        XCTAssertTrue(workspace.rowsOf("team").last!.legacyEmpty)
    }

    func testUndoIsConditionalAndNeverOverwritesLaterTypedText() throws {
        var workspace = Self.workspace()
        let before = workspace.rowsOf("team")
        try workspace.deleteTerm("team", rowID: before[0].rowID)
        _ = workspace.editTerm("team", rowID: before[1].rowID, values: TermValues("get hub", "GH"))
        _ = workspace.addTerm("team", values: TermValues("vm", "VM"))
        workspace.undo()
        XCTAssertEqual(
            workspace.rowsOf("team").map { $0.row.values },
            [
                TermValues("kube", "Kubernetes"), TermValues("get hub", "GH"), TermValues("vm", "VM"),
            ])
        workspace.redo()
        XCTAssertEqual(workspace.rowsOf("team").count, 2)

        var fresh = Self.workspace()
        let row = fresh.rowsOf("team")[0]
        try fresh.setTermEnabled("team", rowID: row.rowID, enabled: false)
        _ = fresh.editTerm("team", rowID: row.rowID, values: TermValues("kube", "K8s", true, false))
        XCTAssertFalse(fresh.canUndo)
    }

    func testNewPackPlaceholderIsCleanAndDuplicateInheritsPermission() throws {
        var workspace = Self.workspace()
        let id = try workspace.createLibrary()
        XCTAssertFalse(workspace.hasUnsavedChanges)
        XCTAssertFalse(workspace.showsAIPermission(id))
        XCTAssertTrue(workspace.rename(id, name: "Terms").applied)
        XCTAssertTrue(workspace.hasUnsavedChanges)
        let copy = try workspace.duplicate("team")
        XCTAssertTrue(workspace.showsAIPermission(copy))
        XCTAssertEqual(workspace.draft.find(copy)?.basedOn, "team")
        XCTAssertEqual(workspace.rowsOf(copy).map { $0.row.values }, workspace.rowsOf("team").map { $0.row.values })
    }

    func testImportIsStagedUndoableAndStalePlanIsRefused() throws {
        var workspace = Self.workspace()
        let document = DictionaryLibraryCsv.parseImport(Data("pattern,replacement\nkube,K8s\nhelm,Helm\n".utf8))
        let plan = try LibraryImportPlanner.plan(
            document: document, target: .existing(libraryID: "team"), draft: workspace.draft)
        _ = try workspace.applyImport(plan, choice: .useFilesVersion)
        XCTAssertEqual(workspace.rowsOf("team").first?.row.values.written, "K8s")
        XCTAssertThrowsError(try workspace.applyImport(plan, choice: .keepMine))
        workspace.undo()
        XCTAssertFalse(workspace.hasUnsavedChanges)
    }

    func testNewerLocalStateRefusesEveryWrite() throws {
        var local = LibraryLocalState.absent
        local.health = .newer
        var workspace = WordPackWorkspace(catalog: LibraryCatalog(generation: 1, libraries: [], localState: local))
        XCTAssertThrowsError(try workspace.createLibrary())
        XCTAssertNil(workspace.captureChangeSet().changeSet)
    }

    func testPreviewAndExportUseDraftWithoutChangingCommittedContent() throws {
        var workspace = Self.workspace()
        let row = workspace.rowsOf("team")[0]
        _ = workspace.editTerm("team", rowID: row.rowID, values: TermValues("kube", "K8s"))
        XCTAssertEqual(try workspace.preview().entries.first?.replacement, "K8s")
        let exported = DictionaryLibraryCsv.parseImport(try workspace.exportSharing("team"))
        XCTAssertEqual(exported.terms.first?.written, "K8s")
        XCTAssertEqual(workspace.committed.find(id: "team")?.library.entries.first?.replacement, "Kubernetes")
        XCTAssertTrue(workspace.hasUnsavedChanges)
    }

    func testLegacyMarkerOnlyDemotesWhenTheBuiltInActuallyCompetes() throws {
        var workspace = Self.workspace()
        let original = workspace.committed.libraries[0]
        let builtin = DictionaryLibrary(
            id: "github", name: "GitHub", category: "Built-in", description: nil,
            builtIn: true, entries: [TermValues("get hub", "Built-in").dictionaryEntry])
        var local = workspace.localState
        local.legacyMarkers = [LegacyMarker(libraryId: "team", key: "get hub")]
        local.setEnabled(true, for: "github")
        let custom = DictionaryLibrary(
            id: original.id, name: original.library.name, category: original.library.category,
            description: nil, builtIn: false, entries: original.library.entries,
            fileName: original.fileName, legacyMarkedKeys: [LibraryTermKey.from("get hub")])
        let items = [
            CatalogLibrary(
                library: custom, state: .available, contentHash: original.contentHash, origin: .existing,
                edits: nil, previousEditsAvailable: false, readErrorCount: 0),
            CatalogLibrary(
                library: builtin, state: .available, contentHash: nil, origin: .existing,
                edits: nil, previousEditsAvailable: false, readErrorCount: 0),
        ]
        let active = WordPackComposition.compose(LibraryCatalog(generation: 1, libraries: items, localState: local))
        XCTAssertEqual(active.entries.first { $0.pattern == "get hub" }?.replacement, "Built-in")
        local.setEnabled(false, for: "github")
        let inactive = WordPackComposition.compose(LibraryCatalog(generation: 1, libraries: items, localState: local))
        XCTAssertEqual(inactive.rules.map(\.entry.pattern), ["kube", "get hub"])
        workspace.reload(LibraryCatalog(generation: 1, libraries: items, localState: local), deleted: [])
        XCTAssertEqual(workspace.rowsOf("team").count, 2)
    }

    static func workspace() -> WordPackWorkspace {
        let values = [TermValues("kube", "Kubernetes"), TermValues("get hub", "GitHub")]
        let library = DictionaryLibrary(
            id: "team", name: "Team terms", category: "Custom", description: nil,
            builtIn: false, entries: values.map(\.dictionaryEntry), fileName: "team.csv")
        let hash = LibraryContentHash(data: Data("team".utf8))
        var local = LibraryLocalState.absent
        local.generation = 1
        local.health = .ok
        local.setEnabled(true, for: "team")
        local.setAIPermission(true, for: "team")
        local.setAcceptedContent(hash, for: "team")
        let catalog = LibraryCatalog(
            generation: 1,
            libraries: [
                CatalogLibrary(
                    library: library, state: .available, contentHash: hash, origin: .existing,
                    edits: nil, previousEditsAvailable: false, readErrorCount: 0)
            ], localState: local)
        return WordPackWorkspace(catalog: catalog)
    }
}
