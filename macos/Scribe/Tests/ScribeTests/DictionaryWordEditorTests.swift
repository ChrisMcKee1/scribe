import XCTest

@testable import Scribe

final class DictionaryWordEditorTests: XCTestCase {
    private func entry(
        _ id: Int64,
        _ pattern: String,
        _ replacement: String,
        wholeWord: Bool = true,
        enabled: Bool = true
    ) -> DictionaryEntry {
        DictionaryEntry(id: id, pattern: pattern, replacement: replacement, wholeWord: wholeWord, enabled: enabled)
    }

    func testAddingSeveralWaysPreservesEachPhraseAndUsesTheNormalDefaults() {
        let existing = [entry(17, "old words", "Old", wholeWord: false, enabled: false)]
        let forms = ["co pilot", "copilot", "co, pile it", "two  spaces", "line\nbreak"]

        let result = DictionaryWordEditor.build(
            existing: existing,
            editedIndex: nil,
            replacement: " Copilot\r\ntext ",
            forms: forms)

        XCTAssertTrue(result.succeeded)
        XCTAssertNil(result.editedEntry)
        XCTAssertEqual(result.addedEntries.map(\.pattern), forms)
        XCTAssertTrue(
            result.addedEntries.allSatisfy {
                $0.id == 0 && $0.replacement == " Copilot\r\ntext " && $0.wholeWord && $0.enabled
            })
    }

    func testEditingIsScopedToTheSelectedRowEvenWhenOthersWriteTheSameText() {
        let existing = [
            entry(7, "sequel", "SQL"),
            entry(12, "unrelated", "Elsewhere"),
            entry(19, "ess queue ell", "SQL", wholeWord: false, enabled: false),
        ]

        let result = DictionaryWordEditor.build(
            existing: existing,
            editedIndex: 2,
            replacement: "T-SQL",
            forms: ["ess queue ell", "another way"])

        XCTAssertTrue(result.succeeded)
        XCTAssertEqual(result.editedEntry, entry(19, "ess queue ell", "T-SQL", wholeWord: false, enabled: false))
        XCTAssertEqual(result.addedEntries, [entry(0, "another way", "T-SQL")])
    }

    func testBlankNewWaysAreIgnored() {
        let result = DictionaryWordEditor.build(
            existing: [],
            editedIndex: nil,
            replacement: "Written",
            forms: [" \t", "one way", ""])
        XCTAssertTrue(result.succeeded)
        XCTAssertEqual(result.addedEntries.map(\.pattern), ["one way"])
    }

    func testExistingSpokenFormsConflictEvenWhenTheOtherRuleIsOff() {
        let existing = [entry(8, "first", "Elsewhere", wholeWord: false, enabled: false)]

        let result = DictionaryWordEditor.build(
            existing: existing,
            editedIndex: nil,
            replacement: "Written",
            forms: ["unique", " first "])

        XCTAssertFalse(result.succeeded)
        XCTAssertEqual(result.errorFormIndex, 1)
        XCTAssertEqual(result.addedEntries, [])
        XCTAssertNil(result.editedEntry)
        XCTAssertTrue(result.error?.contains("already in your dictionary") == true)
    }

    func testAnUnchangedLegacyDuplicateCanBeOpenedWithoutTighteningSaveValidation() {
        let existing = [entry(1, "legacy", "Same"), entry(2, "LEGACY", "Same", wholeWord: false, enabled: false)]

        let result = DictionaryWordEditor.build(
            existing: existing,
            editedIndex: 0,
            replacement: "Same",
            forms: ["legacy"])

        XCTAssertTrue(result.succeeded)
        XCTAssertEqual(result.editedEntry, existing[0])
    }

    func testAddingNothingReportsAnError() {
        let empty = DictionaryWordEditor.build(
            existing: [],
            editedIndex: nil,
            replacement: "Written",
            forms: [])
        XCTAssertFalse(empty.succeeded)

        let existing = [entry(1, "old", "Written")]
        let blankEdit = DictionaryWordEditor.build(
            existing: existing,
            editedIndex: 0,
            replacement: "Written",
            forms: [" "])
        XCTAssertEqual(blankEdit.error, DictionaryWordEditor.spokenEmptyMessage)
    }

    func testPreserveUnchangedTextKeepsTheOriginalValue() {
        XCTAssertEqual(
            DictionaryWordEditor.preserveUnchangedText(
                original: "first\nsecond",
                displayedOriginal: "first\r\nsecond",
                current: "first\r\nsecond"),
            "first\nsecond")
    }
}
