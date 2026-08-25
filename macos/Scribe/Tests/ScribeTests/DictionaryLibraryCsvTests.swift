import XCTest
@testable import Scribe

final class DictionaryLibraryCsvTests: XCTestCase {
    func testParsesMetadataHeaderAndRows() {
        let csv = """
            # name: Microsoft Azure
            # category: Microsoft
            # description: Azure services and common acronyms
            pattern,replacement
            a p i m,APIM
            azure functions,Azure Functions
            """

        let file = DictionaryLibraryCsv.parse(csv)

        XCTAssertEqual(file.name, "Microsoft Azure")
        XCTAssertEqual(file.category, "Microsoft")
        XCTAssertEqual(file.description, "Azure services and common acronyms")
        XCTAssertTrue(file.errors.isEmpty)
        XCTAssertEqual(file.entries.count, 2)
        XCTAssertEqual(file.entries[0].pattern, "a p i m")
        XCTAssertEqual(file.entries[0].replacement, "APIM")
    }

    func testMissingHeaderStillImportsEntriesWithNilMetadata() {
        let csv = "pattern,replacement\nfoo,Foo\n"

        let file = DictionaryLibraryCsv.parse(csv)

        XCTAssertNil(file.name)
        XCTAssertNil(file.category)
        XCTAssertNil(file.description)
        XCTAssertEqual(file.entries.count, 1)
    }

    func testHeaderIsCaseInsensitiveAndTolerantOfSpacing() {
        let csv = "#NAME:   GitHub Terms\n# Category:Developer Tools\npattern,replacement\ngithub,GitHub\n"

        let file = DictionaryLibraryCsv.parse(csv)

        XCTAssertEqual(file.name, "GitHub Terms")
        XCTAssertEqual(file.category, "Developer Tools")
    }

    func testExportRoundTripsThroughParse() {
        let library = DictionaryLibrary(
            id: "sample",
            name: "Sample Library",
            category: "Testing",
            description: "A tiny sample",
            builtIn: false,
            entries: [
                DictionaryEntry(pattern: "foo", replacement: "Foo"),
                DictionaryEntry(pattern: "bar", replacement: "Bar", wholeWord: false, enabled: false),
            ])

        let csv = DictionaryLibraryCsv.export(library)
        let file = DictionaryLibraryCsv.parse(csv)

        XCTAssertEqual(file.name, "Sample Library")
        XCTAssertEqual(file.category, "Testing")
        XCTAssertEqual(file.description, "A tiny sample")
        XCTAssertEqual(file.entries.count, 2)
        XCTAssertEqual(file.entries[1].pattern, "bar")
        XCTAssertFalse(file.entries[1].wholeWord)
        XCTAssertFalse(file.entries[1].enabled)
    }

    func testExportOmitsDescriptionLineWhenBlank() {
        let library = DictionaryLibrary(
            id: "sample", name: "Sample", category: "Testing", description: nil, builtIn: false,
            entries: [DictionaryEntry(pattern: "foo", replacement: "Foo")])

        let csv = DictionaryLibraryCsv.export(library)

        XCTAssertFalse(csv.contains("# description:"))
    }

    func testBlankCsvProducesNoEntriesOrMetadata() {
        let file = DictionaryLibraryCsv.parse(nil)

        XCTAssertNil(file.name)
        XCTAssertTrue(file.entries.isEmpty)
        XCTAssertTrue(file.errors.isEmpty)
    }
}
