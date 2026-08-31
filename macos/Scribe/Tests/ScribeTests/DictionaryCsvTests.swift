import XCTest
@testable import Scribe

final class DictionaryCsvTests: XCTestCase {
    func testExportThenParseRoundTripsEntries() {
        let entries = [
            DictionaryEntry(id: 1, pattern: "azure", replacement: "Azure"),
            DictionaryEntry(id: 2, pattern: "cube flow", replacement: "Kubeflow", wholeWord: false),
            DictionaryEntry(id: 3, pattern: "kay eight ess", replacement: "K8s", enabled: false),
        ]

        let result = DictionaryCsv.parse(DictionaryCsv.export(entries))

        XCTAssertTrue(result.errors.isEmpty)
        XCTAssertEqual(result.entries.count, 3)
        // Imported entries are new rows (id 0); everything else survives the trip.
        XCTAssertTrue(result.entries.allSatisfy { $0.id == 0 })
        XCTAssertEqual(result.entries[0].pattern, "azure")
        XCTAssertEqual(result.entries[0].replacement, "Azure")
        XCTAssertTrue(result.entries[0].wholeWord)
        XCTAssertTrue(result.entries[0].enabled)

        XCTAssertEqual(result.entries[1].pattern, "cube flow")
        XCTAssertEqual(result.entries[1].replacement, "Kubeflow")
        XCTAssertFalse(result.entries[1].wholeWord)
        XCTAssertTrue(result.entries[1].enabled)

        XCTAssertEqual(result.entries[2].pattern, "kay eight ess")
        XCTAssertEqual(result.entries[2].replacement, "K8s")
        XCTAssertTrue(result.entries[2].wholeWord)
        XCTAssertFalse(result.entries[2].enabled)
    }

    func testQuotedFieldsWithCommasAndQuotesRoundTrip() {
        let entries = [DictionaryEntry(id: 1, pattern: "acme, inc", replacement: "Acme \"The Best\" Inc.")]

        let result = DictionaryCsv.parse(DictionaryCsv.export(entries))

        XCTAssertTrue(result.errors.isEmpty)
        XCTAssertEqual(result.entries.count, 1)
        XCTAssertEqual(result.entries[0].pattern, "acme, inc")
        XCTAssertEqual(result.entries[0].replacement, "Acme \"The Best\" Inc.")
    }

    func testCommentsBlankLinesAndHeaderAreSkipped() {
        let csv = """
            # a comment, with a comma
            pattern,replacement,whole_word,enabled

            azure,Azure
            """

        let result = DictionaryCsv.parse(csv)

        XCTAssertTrue(result.errors.isEmpty)
        XCTAssertEqual(result.entries.count, 1)
        XCTAssertEqual(result.entries[0].pattern, "azure")
        XCTAssertTrue(result.entries[0].wholeWord) // optional column defaults
        XCTAssertTrue(result.entries[0].enabled)
    }

    func testTheShippedTemplateParsesCleanly() {
        let result = DictionaryCsv.parse(DictionaryCsv.template)

        XCTAssertTrue(result.errors.isEmpty)
        XCTAssertEqual(result.entries.count, 3)
        XCTAssertTrue(result.entries.contains { $0.pattern == "cube flow" && $0.replacement == "Kubeflow" })
    }

    func testFlagColumnsAcceptYesNoAndNumericForms() {
        let csv = """
            one,One,no,0
            two,Two,YES,1
            """

        let result = DictionaryCsv.parse(csv)

        XCTAssertTrue(result.errors.isEmpty)
        XCTAssertFalse(result.entries[0].wholeWord)
        XCTAssertFalse(result.entries[0].enabled)
        XCTAssertTrue(result.entries[1].wholeWord)
        XCTAssertTrue(result.entries[1].enabled)
    }

    func testBadRowsAreReportedWithLineNumbersAndGoodRowsStillImport() {
        let csv = """
            azure,Azure
            just-a-pattern
            ,MissingPattern
            foundry,Foundry,maybe
            rebac,ReBAC
            """

        let result = DictionaryCsv.parse(csv)

        XCTAssertEqual(result.entries.count, 2) // azure + rebac survive
        XCTAssertEqual(result.errors.count, 3)
        XCTAssertTrue(result.errors.contains { $0.hasPrefix("Line 2:") })
        XCTAssertTrue(result.errors.contains { $0.hasPrefix("Line 3:") })
        XCTAssertTrue(result.errors.contains { $0.hasPrefix("Line 4:") && $0.contains("maybe") })
    }

    func testEmptyOrNilInputYieldsNothing() {
        XCTAssertTrue(DictionaryCsv.parse(nil).entries.isEmpty)
        XCTAssertTrue(DictionaryCsv.parse("  \r\n ").entries.isEmpty)
        XCTAssertTrue(DictionaryCsv.parse("").errors.isEmpty)
    }

    func testQuotedFieldContainingALineBreakStaysOneRecord() {
        // Spreadsheets can emit embedded line breaks in quoted cells; the reader must not split
        // the record.
        let csv = "\"multi\nline\",Value"

        let result = DictionaryCsv.parse(csv)

        XCTAssertTrue(result.errors.isEmpty)
        XCTAssertEqual(result.entries.count, 1)
        XCTAssertEqual(result.entries[0].pattern, "multi\nline")
        XCTAssertEqual(result.entries[0].replacement, "Value")
    }

    func testUnterminatedQuotedFieldIsRejectedWithoutAbsorbingFollowingRows() {
        let csv = "azure,\"Azure\nrebac,ReBAC"

        let result = DictionaryCsv.parse(csv)

        XCTAssertTrue(result.entries.isEmpty)
        XCTAssertTrue(result.errors.contains { $0.contains("closing quote") })
    }
}
