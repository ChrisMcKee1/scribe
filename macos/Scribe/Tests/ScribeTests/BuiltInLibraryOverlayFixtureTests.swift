import XCTest

@testable import Scribe

final class BuiltInLibraryOverlayFixtureTests: XCTestCase {
    func testSupportedReadCasesMatchSharedFixtures() throws {
        let fixture = try OverlayFixture.load()
        for caseData in fixture.readCases where Self.supportedReadCases.contains(caseData.name) {
            let data = try fixture.documentData(caseData.document)
            let result = BuiltInLibraryOverlay.read(libraryID: caseData.library, data: data)

            XCTAssertEqual(result.state.rawValue, caseData.state, caseData.name)
            XCTAssertEqual(result.version, caseData.version, caseData.name)
            if caseData.state == "available" {
                XCTAssertEqual(result.edits?.terms.map { $0.termKey.value }, caseData.keys ?? [], caseData.name)
            }
        }
    }

    func testSupportedApplyCasesMatchSharedFixtures() throws {
        let fixture = try OverlayFixture.load()
        for caseData in fixture.applyCases where Self.supportedApplyCases.contains(caseData.name) {
            let shipped = try fixture.shippedLibrary(version: caseData.shippedVersion, libraryID: caseData.library)
            let edits = try caseData.document.flatMap {
                BuiltInLibraryOverlay.read(libraryID: caseData.library, data: try fixture.documentData($0)).edits
            }

            let applied = BuiltInLibraryOverlay.apply(shipped: shipped, edits: edits)
            let expected = caseData.rows.map { $0.values.dictionaryEntry }

            XCTAssertEqual(applied.entries, expected, caseData.name)
        }
    }

    private static let supportedReadCases: Set<String> = [
        "written-by-scribe",
        "unknown-members-ignored",
        "byte-order-mark",
        "key-trimmed",
        "null-values-absent",
        "library-id-case",
        "no-entries",
        "malformed-json",
        "not-an-object",
        "version-missing",
        "version-text",
        "version-fraction",
        "version-two",
        "library-other",
        "library-missing",
        "terms-missing",
        "terms-not-a-list",
        "entry-not-an-object",
        "key-missing",
        "intent-not-text",
    ]

    private static let supportedApplyCases: Set<String> = [
        "no-document",
        "edited",
        "keys-compare-without-case",
        "off",
        "added",
        "pinned",
        "order",
        "text-kept-exactly",
    ]
}

private struct OverlayFixture: Decodable {
    let shippedVersions: [String: [FixtureTermValues]]
    let applyCases: [ApplyCase]
    let readCases: [ReadCase]

    enum CodingKeys: String, CodingKey {
        case shippedVersions
        case applyCases = "apply"
        case readCases = "read"
    }

    static func load() throws -> OverlayFixture {
        try JSONDecoder().decode(OverlayFixture.self, from: LibraryFixtureSupport.json("edits/cases.json"))
    }

    func documentData(_ relativePath: String) throws -> Data {
        try Data(
            contentsOf: LibraryFixtureSupport.fixturesDirectory
                .appendingPathComponent("edits", isDirectory: true)
                .appendingPathComponent(relativePath, isDirectory: false))
    }

    func shippedLibrary(version: String, libraryID: String) throws -> DictionaryLibrary {
        guard let rows = shippedVersions[version] else {
            throw NSError(domain: "BuiltInLibraryOverlayFixtureTests", code: 1)
        }
        return DictionaryLibrary(
            id: libraryID,
            name: libraryID.capitalized,
            category: "Built-in",
            description: nil,
            builtIn: true,
            entries: rows.map(\.dictionaryEntry))
    }
}

private struct ApplyCase: Decodable {
    let name: String
    let library: String
    let shippedVersion: String
    let document: String?
    let rows: [AppliedRow]
}

private struct AppliedRow: Decodable {
    let values: FixtureTermValues
}

private struct ReadCase: Decodable {
    let name: String
    let library: String
    let document: String
    let state: String
    let version: Int?
    let keys: [String]?
}

private struct FixtureTermValues: Decodable {
    let spoken: String
    let written: String
    let wholeWord: Bool
    let enabled: Bool

    var dictionaryEntry: DictionaryEntry {
        DictionaryEntry(pattern: spoken, replacement: written, wholeWord: wholeWord, enabled: enabled)
    }
}
