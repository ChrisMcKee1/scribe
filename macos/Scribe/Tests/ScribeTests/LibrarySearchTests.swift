import Foundation
import XCTest

@testable import Scribe

final class LibrarySearchTests: XCTestCase {
    func testMatchingUsesCapturedLocaleRules() {
        let english = LibrarySearch.for(Locale(identifier: "en-US"))
        let turkish = LibrarySearch.for(Locale(identifier: "tr-TR"))
        let swedish = LibrarySearch.for(Locale(identifier: "sv-SE"))

        XCTAssertTrue(english.matches(TermValues("Résumé", "x"), query: "resume"))
        XCTAssertTrue(english.matches(TermValues("x", "İstanbul"), query: "istanbul"))
        XCTAssertTrue(turkish.matches(TermValues("IRMAK", "x"), query: "ırmak"))
        XCTAssertFalse(turkish.matches(TermValues("istanbul", "x"), query: "ISTANBUL"))
        XCTAssertTrue(swedish.matches(TermValues("malmö", "x"), query: "MALMÖ"))
        XCTAssertFalse(swedish.matches(TermValues("Ånge", "x"), query: "ange"))
    }

    func testSearchCountsMatchesPerLibraryAndLeavesSelectionUntouched() {
        let workspace = LibraryDeciderTestSupport.workspace([
            LibraryDeciderTestSupport.library(
                id: "github",
                name: "GitHub",
                builtIn: true,
                rows: [
                    LibraryDeciderTestSupport.builtInRow(
                        key: "get hub",
                        spoken: "get hub",
                        written: "GitHub",
                        origin: .shipped,
                        rowID: 1),
                ]),
            LibraryDeciderTestSupport.library(
                id: "microsoft-azure",
                name: "Microsoft Azure",
                builtIn: true,
                rows: [
                    LibraryDeciderTestSupport.builtInRow(
                        key: "a k s",
                        spoken: "a k s",
                        written: "AKS",
                        origin: .shipped,
                        rowID: 2),
                ]),
            LibraryDeciderTestSupport.library(
                id: "team-terms",
                name: "Team terms",
                rows: [
                    LibraryDeciderTestSupport.customRow("get hub", "GitHub Enterprise", rowID: 3),
                ]),
        ])
        let result = LibrarySearch.for(Locale(identifier: "en-US")).search(workspace, query: "  GITHUB ")

        XCTAssertTrue(result.isActive)
        XCTAssertEqual(result.query, "GITHUB")
        XCTAssertEqual(result.count(in: "github"), 1)
        XCTAssertEqual(result.count(in: "microsoft-azure"), 0)
        XCTAssertEqual(result.count(in: "team-terms"), 1)
        XCTAssertEqual(result.totalMatches, 2)
        XCTAssertEqual(result.libraries.map(\.libraryID), ["github", "microsoft-azure", "team-terms"])
        XCTAssertEqual(result.matches(in: "team-terms"), [3])
        XCTAssertEqual(
            result.foundElsewhere(selectedLibraryID: "microsoft-azure").map(\.libraryID),
            ["github", "team-terms"])
    }

    func testBlankQueryIsNoSearch() {
        let workspace = LibraryDeciderTestSupport.workspace([
            LibraryDeciderTestSupport.library(
                id: "team",
                name: "Team",
                rows: [LibraryDeciderTestSupport.customRow("kube", "Kubernetes", rowID: 1)]),
        ])
        let search = LibrarySearch.for(Locale(identifier: "en-US"))
        for query in [nil, "", "   ", "\u{0301}"] {
            let result = search.search(workspace, query: query)
            XCTAssertFalse(result.isActive)
            XCTAssertEqual(result.totalMatches, 0)
            XCTAssertEqual(result.count(in: "team"), 0)
        }
    }

    func testSortOrdersUseSavedOrderAsFinalTieBreaker() {
        let rows = [
            LibraryDeciderTestSupport.customRow("banana", "B2", rowID: 1),
            LibraryDeciderTestSupport.customRow("Apple", "", rowID: 2),
            LibraryDeciderTestSupport.customRow("cherry 10", "A", rowID: 3),
            LibraryDeciderTestSupport.customRow("cherry 9", "A", rowID: 4),
            LibraryDeciderTestSupport.customRow("apple", "Z", rowID: 5),
        ]
        let sort = LibraryTermSort.for(Locale(identifier: "en-US"))

        func order(_ mode: LibraryTermSortOrder) -> String {
            sort.sort(rows, by: mode).map { $0.row.values.spoken }.joined(separator: ",")
        }

        XCTAssertEqual(order(.savedOrder), "banana,Apple,cherry 10,cherry 9,apple")
        XCTAssertEqual(order(.spokenAscending), "Apple,apple,banana,cherry 9,cherry 10")
        XCTAssertEqual(order(.spokenDescending), "cherry 10,cherry 9,banana,apple,Apple")
        XCTAssertEqual(order(.writtenAscending), "cherry 9,cherry 10,banana,apple,Apple")
        XCTAssertEqual(order(.writtenDescending), "apple,banana,cherry 10,cherry 9,Apple")
    }
}
