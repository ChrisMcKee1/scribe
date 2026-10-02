import Foundation
import XCTest

@testable import Scribe

final class LibraryCsvFixtureTests: XCTestCase {
    func testReadFixturesMatchExpectedDocuments() throws {
        let fixture = try JSONDecoder().decode(
            ReadCasesFixture.self,
            from: csvFixtureData("read-cases.json"))

        for item in fixture.cases {
            let bytes = try fixtureBytes(item.file)
            let actual: LibraryCsvDocument
            if item.read == "managed" {
                actual = DictionaryLibraryCsv.parseManaged(bytes)
            } else {
                actual = DictionaryLibraryCsv.parseImport(bytes)
            }
            XCTAssertTrue(actual.matches(item.expected), item.file)
        }
    }

    func testWriteFixturesMatchPinnedBytes() throws {
        let fixture = try JSONDecoder().decode(
            WriteCasesFixture.self,
            from: csvFixtureData("write-cases.json"))

        for item in fixture.cases {
            let managed = try DictionaryLibraryCsv.exportManaged(item.content.libraryCsvContent)
            XCTAssertEqual(managed, try fixtureBytes(item.managed), item.managed)

            let exported = DictionaryLibraryCsv.exportSharing(item.content.libraryCsvContent)
            XCTAssertEqual(exported, try fixtureBytes(item.export), item.export)

            let managedRead = DictionaryLibraryCsv.parseManaged(managed)
            XCTAssertEqual(managedRead.name, item.content.name, item.name)
            XCTAssertEqual(managedRead.category, item.content.category, item.name)
            XCTAssertEqual(managedRead.description, item.content.description, item.name)
            XCTAssertEqual(managedRead.basedOn, item.content.basedOn, item.name)
            XCTAssertEqual(managedRead.terms, item.content.rows, item.name)

            let exportRead = DictionaryLibraryCsv.parseImport(exported)
            XCTAssertEqual(exportRead.terms, item.content.rows, item.name)
        }
    }

    private func csvFixtureData(_ name: String) throws -> Data {
        try Data(contentsOf: csvFixtureURL().appendingPathComponent(name, isDirectory: false))
    }

    private func fixtureBytes(_ relativePath: String) throws -> Data {
        try Data(contentsOf: csvFixtureURL().appendingPathComponent(relativePath, isDirectory: false))
    }

    private func csvFixtureURL() -> URL {
        LibraryFixtureSupport.fixturesDirectory.appendingPathComponent("csv", isDirectory: true)
    }
}

final class LibraryCsvMetadataTests: XCTestCase {
    func testManagedAndExportMetadataFixturesRoundTrip() throws {
        let fixture = try JSONDecoder().decode(MetadataFixture.self, from: metadataFixtureData())

        for item in fixture.cases {
            let content = item.libraryCsvContent(rows: fixture.rows)

            let managed = try DictionaryLibraryCsv.exportManaged(content)
            XCTAssertEqual(managed, try metadataBytes("\(item.label).managed.csv"), item.label)

            let managedRead = DictionaryLibraryCsv.parseManaged(managed)
            XCTAssertEqual(managedRead.name, content.name, item.label)
            XCTAssertEqual(managedRead.category, content.category, item.label)
            XCTAssertEqual(managedRead.description, content.description, item.label)
            XCTAssertEqual(managedRead.basedOn, content.basedOn, item.label)
            XCTAssertEqual(managedRead.terms, content.rows, item.label)

            let exported = DictionaryLibraryCsv.exportSharing(content)
            XCTAssertEqual(exported, try metadataBytes("\(item.label).export.csv"), item.label)

            let exportRead = DictionaryLibraryCsv.parseImport(exported)
            XCTAssertEqual(exportRead.name, content.name, item.label)
            XCTAssertEqual(exportRead.category, content.category, item.label)
            XCTAssertEqual(exportRead.description, content.description, item.label)
            XCTAssertEqual(exportRead.basedOn, content.basedOn, item.label)
            XCTAssertEqual(exportRead.terms, content.rows, item.label)

            let spreadsheet = try metadataBytes("\(item.label).spreadsheet.csv")
            let spreadsheetRead = DictionaryLibraryCsv.parseImport(spreadsheet)
            XCTAssertEqual(spreadsheetRead.name, content.name, item.label)
            XCTAssertEqual(spreadsheetRead.category, content.category, item.label)
            XCTAssertEqual(spreadsheetRead.description, content.description, item.label)
            XCTAssertEqual(spreadsheetRead.basedOn, content.basedOn, item.label)
            XCTAssertEqual(spreadsheetRead.terms, content.rows, item.label)
        }
    }

    func testManagedWriterRejectsUnpairedMetadataQuotes() throws {
        let fixture = try JSONDecoder().decode(MetadataFixture.self, from: metadataFixtureData())
        let rows = fixture.rows

        for item in fixture.unpaired {
            XCTAssertFalse(
                LibraryMetadata.readsBackInOlderVersions(
                    item.name,
                    item.category,
                    item.description,
                    nil))
            let content = item.libraryCsvContent(rows: rows)
            XCTAssertThrowsError(try DictionaryLibraryCsv.exportManaged(content), item.name)

            let exported = DictionaryLibraryCsv.exportSharing(content)
            let read = DictionaryLibraryCsv.parseImport(exported)
            XCTAssertEqual(read.name, content.name, item.name)
            XCTAssertEqual(read.category, content.category, item.name)
            XCTAssertEqual(read.description, content.description, item.name)
            XCTAssertEqual(read.terms, content.rows, item.name)
        }
    }

    private func metadataFixtureData() throws -> Data {
        try Data(contentsOf: metadataURL().appendingPathComponent("cases.json", isDirectory: false))
    }

    private func metadataBytes(_ file: String) throws -> Data {
        try Data(contentsOf: metadataURL().appendingPathComponent(file, isDirectory: false))
    }

    private func metadataURL() -> URL {
        LibraryFixtureSupport.fixturesDirectory.appendingPathComponent("csv", isDirectory: true)
            .appendingPathComponent("metadata", isDirectory: true)
    }
}

private struct ReadCasesFixture: Decodable {
    let cases: [ReadCase]

    struct ReadCase: Decodable {
        let file: String
        let read: String
        let expected: ExpectedDocument
    }
}

private struct WriteCasesFixture: Decodable {
    let cases: [WriteCase]

    struct WriteCase: Decodable {
        let name: String
        let content: ContentFixture
        let managed: String
        let export: String
    }
}

private struct MetadataFixture: Decodable {
    let rows: [TermValues]
    let cases: [LabeledMetadataCase]
    let unpaired: [MetadataCase]

    struct LabeledMetadataCase: Decodable {
        let label: String
        let name: String
        let category: String
        let description: String?
        let basedOn: String?

        func libraryCsvContent(rows: [TermValues]) -> LibraryCsvContent {
            LibraryCsvContent(
                name: name,
                category: category,
                description: description,
                basedOn: basedOn,
                rows: rows)
        }
    }

    struct MetadataCase: Decodable {
        let name: String
        let category: String
        let description: String?
        let basedOn: String?

        func libraryCsvContent(rows: [TermValues]) -> LibraryCsvContent {
            LibraryCsvContent(
                name: name,
                category: category,
                description: description,
                basedOn: basedOn,
                rows: rows)
        }
    }
}

private struct ContentFixture: Decodable {
    let name: String
    let category: String
    let description: String?
    let basedOn: String?
    let rows: [TermValues]

    var libraryCsvContent: LibraryCsvContent {
        LibraryCsvContent(
            name: name,
            category: category,
            description: description,
            basedOn: basedOn,
            rows: rows)
    }
}

private struct ExpectedDocument: Decodable, Equatable {
    let name: String?
    let category: String?
    let description: String?
    let basedOn: String?
    let terms: [TermValues]
    let errors: [RowErrorFixture]
    let encoding: EncodingFixture
    let formulaGuardVersion: Int?
    let issues: [String]
}

private struct RowErrorFixture: Decodable, Equatable {
    let line: Int
    let kind: LibraryCsvRowErrorKind
    let field: String?
}

private struct EncodingFixture: Decodable, Equatable {
    let codePage: Int
    let byteOrderMark: Bool
    let ansiFallback: Bool
    let invalidBytesReplaced: Bool
}

extension LibraryCsvDocument {
    fileprivate func matches(_ rhs: ExpectedDocument) -> Bool {
        name == rhs.name
            && category == rhs.category
            && description == rhs.description
            && basedOn == rhs.basedOn
            && terms == rhs.terms
            && errors.map {
                RowErrorFixture(line: $0.line, kind: $0.kind, field: $0.field)
            } == rhs.errors
            && encoding == LibraryTextEncoding(
                codePage: rhs.encoding.codePage,
                byteOrderMark: rhs.encoding.byteOrderMark,
                ansiFallback: rhs.encoding.ansiFallback,
                invalidBytesReplaced: rhs.encoding.invalidBytesReplaced)
            && formulaGuardVersion == rhs.formulaGuardVersion
            && issues.names == rhs.issues
    }
}
