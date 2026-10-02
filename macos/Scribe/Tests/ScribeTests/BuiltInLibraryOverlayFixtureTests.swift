import XCTest

@testable import Scribe

final class BuiltInLibraryOverlayFixtureTests: XCTestCase {
    func testSupportedReadCasesMatchSharedFixtures() throws {
        let fixture = try OverlayFixture.load()
        for caseData in fixture.readCases {
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
        for caseData in fixture.applyCases {
            let shipped = try fixture.shippedLibrary(version: caseData.shippedVersion, libraryID: caseData.library)
            let edits = try caseData.document.flatMap {
                BuiltInLibraryOverlay.read(libraryID: caseData.library, data: try fixture.documentData($0)).edits
            }

            let applied = BuiltInLibraryOverlay.apply(shipped: shipped, edits: edits)
            let expected = caseData.rows.map { $0.values.dictionaryEntry }

            XCTAssertEqual(applied.entries, expected, caseData.name)
            let rows = BuiltInLibraryOverlay.applyRows(shipped: shipped, edits: edits)
            XCTAssertEqual(rows.map(\.key.value), caseData.rows.map(\.key), caseData.name)
            XCTAssertEqual(rows.map(\.origin.rawValue), caseData.rows.map(\.origin), caseData.name)
            XCTAssertEqual(rows.map { $0.edit?.intent.rawValue }, caseData.rows.map(\.intent), caseData.name)
            XCTAssertEqual(rows.map(\.review), caseData.rows.map { $0.review?.model }, caseData.name)
        }
    }

    func testEveryCollectFixtureMatchesAndRoundTrips() throws {
        let fixture = try OverlayFixture.load()
        for item in fixture.collectCases {
            let shipped = try fixture.shippedLibrary(version: item.shippedVersion, libraryID: item.library)
            let committed = BuiltInLibraryOverlay.read(
                libraryID: item.library, data: try fixture.documentData(item.committed)
            ).edits
            var rows = BuiltInLibraryOverlay.applyRows(shipped: shipped, edits: committed)
            rows = rows.filter { !item.omit.contains($0.key.value) }.compactMap {
                item.restore.contains($0.key.value) ? BuiltInLibraryOverlay.restoreShipped($0) : $0
            }
            let collected = try BuiltInLibraryOverlay.collect(shipped: shipped, committed: committed, rows: rows)
            let expected = try item.expected.flatMap {
                BuiltInLibraryOverlay.read(libraryID: item.library, data: try fixture.documentData($0)).edits
            }
            XCTAssertEqual(collected, expected, item.name)
            if let collected {
                XCTAssertEqual(
                    BuiltInLibraryOverlay.read(
                        libraryID: item.library, data: try BuiltInLibraryOverlay.write(collected)
                    ).edits,
                    collected, item.name)
            }
        }
    }
}

private struct OverlayFixture: Decodable {
    let shippedVersions: [String: [FixtureTermValues]]
    let applyCases: [ApplyCase]
    let readCases: [ReadCase]
    let collectCases: [CollectCase]

    enum CodingKeys: String, CodingKey {
        case shippedVersions
        case applyCases = "apply"
        case readCases = "read"
        case collectCases = "collect"
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
    let key: String
    let origin: String
    let values: FixtureTermValues
    let intent: String?
    let review: FixtureReview?
}

private struct FixtureReview: Decodable {
    let yours: TermValues
    let updatedBuiltIn: TermValues
    let differing: [String]

    var model: TermReview {
        let fields = differing.reduce(into: TermFields()) { result, name in
            switch name {
            case "spoken": result.insert(.spoken)
            case "written": result.insert(.written)
            case "wholeWord": result.insert(.wholeWord)
            case "enabled": result.insert(.enabled)
            default: break
            }
        }
        return TermReview(yours: yours, updatedBuiltIn: updatedBuiltIn, differing: fields)
    }
}

private struct CollectCase: Decodable {
    let name: String
    let library: String
    let shippedVersion: String
    let committed: String
    let omit: [String]
    let restore: [String]
    let expected: String?
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
