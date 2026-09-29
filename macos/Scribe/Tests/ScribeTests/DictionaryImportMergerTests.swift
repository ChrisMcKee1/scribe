import XCTest

@testable import Scribe

final class DictionaryImportMergerTests: XCTestCase {
    private func existing(
        _ index: Int, _ id: Int64, _ pattern: String?, _ replacement: String?,
        wholeWord: Bool = true, enabled: Bool = true
    ) -> DictionaryImportMerger.ExistingRow {
        DictionaryImportMerger.ExistingRow(
            index: index, id: id, pattern: pattern, replacement: replacement, wholeWord: wholeWord, enabled: enabled)
    }

    func testMergeAddsNewPattern() {
        let plan = DictionaryImportMerger.merge(
            existing: [],
            imported: [DictionaryEntry(pattern: "azure", replacement: "Azure")])

        XCTAssertEqual(plan.added, 1)
        XCTAssertEqual(plan.updated, 0)
        XCTAssertEqual(plan.unchanged, 0)
        XCTAssertEqual(plan.operations.count, 1)
        XCTAssertEqual(plan.operations[0].kind, .add)
        XCTAssertEqual(plan.operations[0].entry.pattern, "azure")
    }

    func testMergeCountsIdenticalRowAsUnchanged() {
        let existingRows = [existing(0, 3, "azure", "Azure")]

        let plan = DictionaryImportMerger.merge(
            existing: existingRows,
            imported: [DictionaryEntry(pattern: "azure", replacement: "Azure")])

        XCTAssertEqual(plan.added, 0)
        XCTAssertEqual(plan.updated, 0)
        XCTAssertEqual(plan.unchanged, 1)
        XCTAssertTrue(plan.operations.isEmpty)
    }

    func testMergeUpdatesDifferingRowPreservingIdAndPattern() {
        let existingRows = [existing(2, 42, "azure", "azure")]

        let plan = DictionaryImportMerger.merge(
            existing: existingRows,
            imported: [DictionaryEntry(pattern: "AZURE", replacement: "Azure", wholeWord: false, enabled: false)])

        XCTAssertEqual(plan.added, 0)
        XCTAssertEqual(plan.updated, 1)
        XCTAssertEqual(plan.unchanged, 0)
        XCTAssertEqual(plan.operations.count, 1)
        let op = plan.operations[0]
        XCTAssertEqual(op.kind, .update)
        XCTAssertEqual(op.index, 2)
        XCTAssertEqual(op.entry.id, 42)  // existing id preserved
        XCTAssertEqual(op.entry.pattern, "azure")  // original spoken form preserved
        XCTAssertEqual(op.entry.replacement, "Azure")
        XCTAssertFalse(op.entry.wholeWord)
        XCTAssertFalse(op.entry.enabled)
    }

    func testMergeMatchesCaseInsensitivelyByPattern() {
        let existingRows = [existing(0, 1, "azure", "old")]

        let plan = DictionaryImportMerger.merge(
            existing: existingRows,
            imported: [DictionaryEntry(pattern: "AZURE", replacement: "new")])

        XCTAssertEqual(plan.updated, 1)
    }

    func testMergeIgnoresWhitespaceAroundExistingPatternWhenMatching() {
        let existingRows = [existing(0, 1, "  azure  ", "Azure")]

        let plan = DictionaryImportMerger.merge(
            existing: existingRows,
            imported: [DictionaryEntry(pattern: "azure", replacement: "Azure")])

        XCTAssertEqual(plan.added, 0)
        XCTAssertEqual(plan.updated, 0)
        XCTAssertEqual(plan.unchanged, 1)
    }

    func testMergeMixedBatchReportsAllCounts() {
        let existingRows = [
            existing(0, 1, "azure", "Azure"),  // will be unchanged
            existing(1, 2, "cube", "cube"),  // will be updated
        ]

        let plan = DictionaryImportMerger.merge(
            existing: existingRows,
            imported: [
                DictionaryEntry(pattern: "azure", replacement: "Azure"),  // unchanged
                DictionaryEntry(pattern: "cube", replacement: "Kubernetes"),  // update
                DictionaryEntry(pattern: "net", replacement: "NET"),  // add
            ])

        XCTAssertEqual(plan.added, 1)
        XCTAssertEqual(plan.updated, 1)
        XCTAssertEqual(plan.unchanged, 1)
        XCTAssertEqual(plan.operations.count, 2)  // one update, one add (unchanged emits nothing)
    }

    func testMergeLaterDuplicateImportUpdatesTheJustAddedRow() {
        // Two imports share a spoken form not present in the existing set: first adds, second updates.
        let plan = DictionaryImportMerger.merge(
            existing: [],
            imported: [
                DictionaryEntry(pattern: "term", replacement: "First"),
                DictionaryEntry(pattern: "term", replacement: "Second"),
            ])

        XCTAssertEqual(plan.added, 1)
        XCTAssertEqual(plan.updated, 1)
        XCTAssertEqual(plan.unchanged, 0)
        XCTAssertEqual(plan.operations.count, 2)
        XCTAssertEqual(plan.operations[0].kind, .add)
        XCTAssertEqual(plan.operations[1].kind, .update)
        XCTAssertEqual(plan.operations[1].index, 0)  // the row the add appended
        XCTAssertEqual(plan.operations[1].entry.replacement, "Second")
    }

    func testMergeFirstWriterWinsForDuplicateExistingPatterns() {
        // Set can hold two rows with the same spoken form; the first is the match target.
        let existingRows = [
            existing(0, 1, "dup", "one"),
            existing(1, 2, "dup", "two"),
        ]

        let plan = DictionaryImportMerger.merge(
            existing: existingRows,
            imported: [DictionaryEntry(pattern: "dup", replacement: "three")])

        XCTAssertEqual(plan.operations.count, 1)
        let op = plan.operations[0]
        XCTAssertEqual(op.index, 0)
        XCTAssertEqual(op.entry.id, 1)
    }

    // MARK: - Applying the plan

    func testChangesFoldADuplicateInTheSameImportIntoOneInsertWithTheLaterValue() {
        let imported = [
            DictionaryEntry(pattern: "term", replacement: "First"),
            DictionaryEntry(pattern: "term", replacement: "Second"),
        ]
        let plan = DictionaryImportMerger.merge(existing: [], imported: imported)

        let changes = DictionaryImportMerger.changes(applying: plan, to: [])

        XCTAssertEqual(changes.inserts, [DictionaryEntry(pattern: "term", replacement: "Second")])
        XCTAssertTrue(changes.updates.isEmpty)
    }

    func testChangesKeepTheLastUpdateOfAnExistingRowAndItsIdentity() {
        let existingRows = [existing(0, 7, "cube", "cube"), existing(1, 8, "azure", "Azure")]
        let plan = DictionaryImportMerger.merge(
            existing: existingRows,
            imported: [
                DictionaryEntry(pattern: "cube", replacement: "Kube"),
                DictionaryEntry(pattern: "CUBE", replacement: "Kubernetes"),
                DictionaryEntry(pattern: "azure", replacement: "Azure"),
            ])

        let changes = DictionaryImportMerger.changes(applying: plan, to: existingRows)

        XCTAssertTrue(changes.inserts.isEmpty)
        XCTAssertEqual(changes.updates, [DictionaryEntry(id: 7, pattern: "cube", replacement: "Kubernetes")])
    }

    func testChangesSkipAnExistingRowThatEndsWhereItStarted() {
        let existingRows = [existing(0, 3, "cube", "cube")]
        let plan = DictionaryImportMerger.merge(
            existing: existingRows,
            imported: [
                DictionaryEntry(pattern: "cube", replacement: "Kubernetes"),
                DictionaryEntry(pattern: "cube", replacement: "cube"),
            ])

        XCTAssertEqual(DictionaryImportMerger.changes(applying: plan, to: existingRows).updates, [])
    }

    // MARK: - Final stored rows

    private func importCsvRows(_ imported: [DictionaryEntry], into store: PersistenceStore) throws {
        let existingRows = try store.fetchAllDictionaryEntries().enumerated().map { index, entry in
            DictionaryImportMerger.ExistingRow(
                index: index, id: entry.id, pattern: entry.pattern, replacement: entry.replacement,
                wholeWord: entry.wholeWord, enabled: entry.enabled)
        }
        let plan = DictionaryImportMerger.merge(existing: existingRows, imported: imported)
        let changes = DictionaryImportMerger.changes(applying: plan, to: existingRows)
        try store.applyDictionaryChanges(inserts: changes.inserts, updates: changes.updates)
    }

    func testADuplicateSpokenFormInOneImportIsStoredOnceWithItsLaterValue() throws {
        let directory = try StorageTestDirectory()
        defer { directory.remove() }
        let store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()

        try importCsvRows(
            [
                DictionaryEntry(pattern: "term", replacement: "First"),
                DictionaryEntry(pattern: "term", replacement: "Second"),
            ],
            into: store)

        let stored = try store.fetchAllDictionaryEntries()
        XCTAssertEqual(stored.map(\.pattern), ["term"])
        XCTAssertEqual(stored.map(\.replacement), ["Second"])
    }

    func testAnImportUpdatesExistingRowsInPlaceAndAddsNewOnes() throws {
        let directory = try StorageTestDirectory()
        defer { directory.remove() }
        let store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        let cubeID = try store.insertDictionaryEntry(DictionaryEntry(pattern: "cube", replacement: "cube"))

        try importCsvRows(
            [
                DictionaryEntry(pattern: "cube", replacement: "Kubernetes"),
                DictionaryEntry(pattern: "net", replacement: "NET"),
                DictionaryEntry(pattern: "net", replacement: ".NET"),
            ],
            into: store)

        let stored = try store.fetchAllDictionaryEntries()
        XCTAssertEqual(stored.map(\.pattern), ["cube", "net"])
        XCTAssertEqual(stored.map(\.replacement), ["Kubernetes", ".NET"])
        XCTAssertEqual(stored.first?.id, cubeID)
    }
}
