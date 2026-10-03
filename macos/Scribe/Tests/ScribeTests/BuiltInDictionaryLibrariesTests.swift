import XCTest

@testable import Scribe

/// Exercises the built-in library loader against the real CSV resources bundled with the app
/// (via `Bundle.module`), so a packaging regression that drops or mis-names a resource fails a
/// test instead of surfacing as an empty Libraries tab.
final class BuiltInDictionaryLibrariesTests: XCTestCase {
    func testAllElevenBundledLibrariesLoad() {
        let libraries = BuiltInDictionaryLibraries.all

        XCTAssertEqual(libraries.count, 11, "expected every bundled CSV under Resources/Libraries to load")
        XCTAssertTrue(libraries.allSatisfy(\.builtIn))
        XCTAssertTrue(libraries.allSatisfy { !$0.entries.isEmpty })
    }

    func testLibrariesFollowFrozenPrecedenceOrder() {
        XCTAssertEqual(BuiltInDictionaryLibraries.all.map(\.id), LibraryPrecedence.builtInOrder)
    }

    func testKnownLibraryHasExpectedIdAndMetadata() {
        guard let azure = BuiltInDictionaryLibraries.all.first(where: { $0.id == "microsoft-azure" }) else {
            return XCTFail("expected a microsoft-azure library from Resources/Libraries/microsoft-azure.csv")
        }

        XCTAssertFalse(azure.name.isEmpty)
        XCTAssertFalse(azure.category.isEmpty)
        XCTAssertTrue(azure.entries.count > 5)
    }

    func testHumanizeFallsBackToTitleCasedId() {
        XCTAssertEqual(BuiltInDictionaryLibraries.humanize("dotnet-development"), "Dotnet Development")
        XCTAssertEqual(BuiltInDictionaryLibraries.humanize("github"), "Github")
    }
}
