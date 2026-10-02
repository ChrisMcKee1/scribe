import XCTest

@testable import Scribe

final class SettingsDictionaryPageLogicTests: XCTestCase {
    func testSearchMatchesSpokenAndWrittenFormsCaseInsensitively() {
        let entry = DictionaryEntry(id: 1, pattern: "kay eight ess", replacement: "K8s")

        XCTAssertTrue(SettingsDictionaryPageLogic.matchesSearch(entry, query: "KAY"))
        XCTAssertTrue(SettingsDictionaryPageLogic.matchesSearch(entry, query: "8S"))
        XCTAssertTrue(SettingsDictionaryPageLogic.matchesSearch(entry, query: "  "))
        XCTAssertFalse(SettingsDictionaryPageLogic.matchesSearch(entry, query: "azure"))
    }

    func testWordPackRowsSortByNameThenId() {
        let rows = SettingsDictionaryPageLogic.wordPackRows([
            DictionaryLibrary(id: "z", name: "Zulu", category: "Built-in", description: nil, builtIn: true, entries: []),
            DictionaryLibrary(id: "b", name: "Alpha", category: "Built-in", description: nil, builtIn: true, entries: []),
            DictionaryLibrary(id: "a", name: "Alpha", category: "Built-in", description: nil, builtIn: true, entries: []),
        ])

        XCTAssertEqual(rows.map(\.id), ["a", "b", "z"])
    }

    func testEnabledSpokenFormsUsesOnlyEnabledWordPacksAndEntries() {
        let enabled = DictionaryLibrary(
            id: "enabled",
            name: "Enabled",
            category: "Built-in",
            description: nil,
            builtIn: true,
            entries: [
                DictionaryEntry(pattern: " Dot Net ", replacement: ".NET"),
                DictionaryEntry(pattern: "off", replacement: "Off", enabled: false),
            ])
        let disabled = DictionaryLibrary(
            id: "disabled",
            name: "Disabled",
            category: "Built-in",
            description: nil,
            builtIn: true,
            entries: [DictionaryEntry(pattern: "Azure", replacement: "Azure")])

        let forms = SettingsDictionaryPageLogic.enabledSpokenForms([enabled, disabled], enabledIds: ["enabled"])

        XCTAssertEqual(forms, ["dot net"])
    }

    func testEnabledSummaryUsesSingularAndPluralNouns() {
        XCTAssertEqual(SettingsDictionaryPageLogic.enabledSummary(enabled: 1, total: 1, noun: "word"), "1 of 1 word is on.")
        XCTAssertEqual(SettingsDictionaryPageLogic.enabledSummary(enabled: 2, total: 3, noun: "word pack"), "2 of 3 word packs are on.")
    }
}
