import XCTest

@testable import Scribe

final class LibraryPrecedenceTests: XCTestCase {
    func testBuiltInListsMatchSharedFixture() throws {
        let fixture = try JSONDecoder().decode(
            BuiltInPrecedenceFixture.self,
            from: LibraryFixtureSupport.json("built-in-precedence.json"))

        XCTAssertEqual(LibraryPrecedence.builtInOrder, fixture.order)
        XCTAssertEqual(LibraryPrecedence.retiredBuiltInIDs, fixture.retired)
    }

    func testBuiltInsStayInFrozenPrecedenceOrder() {
        XCTAssertEqual(BuiltInDictionaryLibraries.all.map(\.id), LibraryPrecedence.builtInOrder)
    }

    func testCustomLibrariesFollowPhysicalFileNames() {
        let libraries = [
            library(id: "team-terms", builtIn: false),
            library(id: "Zulu Notes", builtIn: false),
            library(id: "team-terms-2", builtIn: false),
            library(id: "release-9", builtIn: false),
            library(id: "alpha", builtIn: false),
            library(id: "release-10", builtIn: false),
        ]

        let ordered = LibraryPrecedence.order(libraries).map(\.id)

        XCTAssertEqual(ordered, ["alpha", "release-10", "release-9", "team-terms-2", "team-terms", "Zulu Notes"])
    }

    func testDisplayOrderIsSeparateFromPrecedence() {
        let libraries = [
            library(id: "release-10", name: "Release 10", builtIn: false),
            library(id: "release-9", name: "Release 9", builtIn: false),
            library(id: "github", name: "GitHub", builtIn: true),
            library(id: "github-copy", name: "github", builtIn: false),
        ]

        let ordering = LibraryOrdering()
        let ordered = ordering.sort(libraries).map(\.id)

        XCTAssertEqual(ordered, ["github", "github-copy", "release-9", "release-10"])
    }

    private func library(
        id: String,
        name: String? = nil,
        category: String = "Custom",
        builtIn: Bool,
        fileName: String? = nil
    ) -> DictionaryLibrary {
        DictionaryLibrary(
            id: id,
            name: name ?? id,
            category: category,
            description: nil,
            builtIn: builtIn,
            entries: [DictionaryEntry(pattern: "a", replacement: "A")],
            fileName: fileName)
    }
}

private struct BuiltInPrecedenceFixture: Decodable {
    let order: [String]
    let retired: [String]
}
