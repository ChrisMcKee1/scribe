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
        XCTAssertEqual(op.entry.id, 42) // existing id preserved
        XCTAssertEqual(op.entry.pattern, "azure") // original spoken form preserved
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
            existing(0, 1, "azure", "Azure"), // will be unchanged
            existing(1, 2, "cube", "cube"),   // will be updated
        ]

        let plan = DictionaryImportMerger.merge(
            existing: existingRows,
            imported: [
                DictionaryEntry(pattern: "azure", replacement: "Azure"),      // unchanged
                DictionaryEntry(pattern: "cube", replacement: "Kubernetes"),  // update
                DictionaryEntry(pattern: "net", replacement: "NET"),          // add
            ])

        XCTAssertEqual(plan.added, 1)
        XCTAssertEqual(plan.updated, 1)
        XCTAssertEqual(plan.unchanged, 1)
        XCTAssertEqual(plan.operations.count, 2) // one update, one add (unchanged emits nothing)
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
        XCTAssertEqual(plan.operations[1].index, 0) // the row the add appended
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
}
