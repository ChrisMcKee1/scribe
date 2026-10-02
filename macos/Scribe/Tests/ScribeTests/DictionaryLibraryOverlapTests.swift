import XCTest

@testable import Scribe

final class DictionaryLibraryOverlapTests: XCTestCase {
    func testAnalyzerSeparatesRedundantEntriesFromOverrides() {
        let personal = [
            DictionaryEntry(pattern: "get hub", replacement: "GitHub"),
            DictionaryEntry(pattern: "vs", replacement: "versus"),
            DictionaryEntry(pattern: "off", replacement: "Off", enabled: false),
        ]
        let library = [
            DictionaryEntry(pattern: "get hub", replacement: "GitHub"),
            DictionaryEntry(pattern: "vs", replacement: "Visual Studio"),
            DictionaryEntry(pattern: "off", replacement: "On"),
        ]

        let report = DictionaryLibraryOverlapAnalyzer.analyze(
            personal: personal,
            libraryEntries: library,
            libraryIdsByPattern: ["get hub": "GitHub", "vs": "Developer tools"])

        XCTAssertEqual(report.redundant.map(\.pattern), ["get hub"])
        XCTAssertEqual(report.overrides.map(\.pattern), ["vs"])
        XCTAssertEqual(report.overrides.first?.wordPackReplacement, "Visual Studio")
    }

    func testCoverageUsesWordPackPrecedence() {
        let builtIn = DictionaryLibrary(
            id: "github",
            name: "GitHub",
            category: "Built-in",
            description: nil,
            builtIn: true,
            entries: [DictionaryEntry(pattern: "action", replacement: "Action")])
        let custom = DictionaryLibrary(
            id: "team",
            name: "Team",
            category: "Custom",
            description: nil,
            builtIn: false,
            entries: [DictionaryEntry(pattern: "action", replacement: "Team Action")],
            fileName: "team.csv")

        let coverage = DictionaryLibraryOverlapAnalyzer.coverage(
            libraries: [custom, builtIn],
            enabledIds: ["github", "team"])

        XCTAssertEqual(coverage["action"]?.libraryId, "github")
        XCTAssertEqual(coverage["action"]?.entry.replacement, "Action")
    }
}

final class LibrarySwitchOffCopyTests: XCTestCase {
    func testSwitchOffCopiesTermsWhenNoRuleStaysInConflict() {
        let team = DictionaryLibrary(
            id: "team",
            name: "Team",
            category: "Custom",
            description: nil,
            builtIn: false,
            entries: [DictionaryEntry(pattern: "north star", replacement: "North Star")],
            fileName: "team.csv")
        let result = LibrarySwitchOffCopy.plan(
            rows: [],
            libraries: [team],
            libraryRows: [LibrarySwitchOffCopy.LibraryRow(id: "team", builtIn: false, enabled: true)],
            switchingOff: [
                LibraryUsage(
                    id: "team",
                    name: "Team",
                    copyTerms: team.entries,
                    unusedCount: 0,
                    builtIn: false)
            ])

        XCTAssertEqual(result.copies.map(\.pattern), ["north star"])
        XCTAssertTrue(result.keptOn.isEmpty)
    }

    func testSwitchOffKeepsWordPackOnWhenTermsOverlapStayingRules() {
        let staying = DictionaryLibrary(
            id: "staying",
            name: "Staying",
            category: "Custom",
            description: nil,
            builtIn: false,
            entries: [DictionaryEntry(pattern: "kilo", replacement: "Kilo")],
            fileName: "staying.csv")
        let going = DictionaryLibrary(
            id: "going",
            name: "Going",
            category: "Custom",
            description: nil,
            builtIn: false,
            entries: [DictionaryEntry(pattern: "k", replacement: "K")],
            fileName: "going.csv")

        let result = LibrarySwitchOffCopy.plan(
            rows: [],
            libraries: [staying, going],
            libraryRows: [
                LibrarySwitchOffCopy.LibraryRow(id: "staying", builtIn: false, enabled: true),
                LibrarySwitchOffCopy.LibraryRow(id: "going", builtIn: false, enabled: true),
            ],
            switchingOff: [
                LibraryUsage(id: "going", name: "Going", copyTerms: going.entries, unusedCount: 0, builtIn: false)
            ])

        XCTAssertEqual(result.keptOn.map(\.id), ["going"])
        XCTAssertTrue(result.copies.isEmpty)
    }
}
