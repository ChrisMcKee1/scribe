import Foundation
import XCTest

@testable import Scribe

final class SettingsNavigationTests: XCTestCase {
    func testTheMacPagesAreTheWindowsPagesInTheSameOrderGroupsAndPositions() throws {
        let source = try WindowsSources.read("src/Scribe.Core/Settings/SettingsNavigation.cs")
        let pattern =
            "new\\(SettingsPage\\.(\\w+), \"([^\"]*)\", (?:string\\.Empty|\"([^\"]*)\"), (\\d+), (true|false)\\)"
        let regex = try NSRegularExpression(pattern: pattern)
        let range = NSRange(source.startIndex..., in: source)
        var windows: [(name: String, label: String, group: String, position: Int, first: Bool)] = []
        for match in regex.matches(in: source, range: range) {
            func capture(_ index: Int) -> String {
                Range(match.range(at: index), in: source).map { String(source[$0]) } ?? ""
            }
            let name = capture(1)
            let camel = name.prefix(1).lowercased() + name.dropFirst()
            windows.append((camel, capture(2), capture(3), Int(capture(4)) ?? 0, capture(5) == "true"))
        }

        XCTAssertEqual(windows.count, SettingsNavigation.items.count, "Windows has \(windows.count) pages")
        for (windowsItem, item) in zip(windows, SettingsNavigation.items) {
            XCTAssertEqual(String(describing: item.page), windowsItem.name)
            XCTAssertEqual(item.label, windowsItem.label)
            XCTAssertEqual(item.group, windowsItem.group)
            XCTAssertEqual(item.position, windowsItem.position)
            XCTAssertEqual(item.isFirstInGroup, windowsItem.first)
        }
    }

    func testEveryWindowsPageIsDeclaredInTheWindowsEnumSoANewPageCannotBeMissed() throws {
        let source = try WindowsSources.read("src/Scribe.Core/Settings/SettingsNavigation.cs")
        let regex = try NSRegularExpression(pattern: "public enum SettingsPage\\s*\\{([^}]*)\\}")
        let range = NSRange(source.startIndex..., in: source)
        let match = try XCTUnwrap(regex.firstMatch(in: source, range: range))
        let bodyRange = try XCTUnwrap(Range(match.range(at: 1), in: source))
        let names = source[bodyRange].split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        XCTAssertEqual(names.count, SettingsPage.allCases.count)
    }

    func testTheFirstGroupHasNoHeadingAndTheOthersAreNamed() {
        XCTAssertTrue(SettingsPageGroup.primary.title.isEmpty)
        XCTAssertTrue(SettingsPageGroup.allCases.dropFirst().allSatisfy { !$0.title.isEmpty })
        XCTAssertEqual(SettingsNavigation.items.filter(\.isFirstInGroup).count, SettingsPageGroup.allCases.count)
    }

    func testEveryPageHasADistinctSymbolATitleAndKeywords() {
        XCTAssertEqual(Set(SettingsPage.allCases.map(\.symbol)).count, SettingsPage.allCases.count)
        XCTAssertEqual(Set(SettingsPage.allCases.map(\.title)).count, SettingsPage.allCases.count)
        XCTAssertTrue(SettingsPage.allCases.allSatisfy { !$0.keywords.isEmpty })
        XCTAssertEqual(SettingsPage.allCases.map(\.position), Array(1...SettingsPage.allCases.count))
    }

    func testOnlyDictionaryHasTabsYourWordsThenWordPacks() {
        XCTAssertEqual(SettingsPage.dictionary.tabs, [.yourWords, .wordPacks])
        XCTAssertEqual(DictionaryTab.allCases.map(\.title), ["Your words", "Word packs"])
        XCTAssertEqual(SettingsPage.allCases.filter { !$0.tabs.isEmpty }, [.dictionary])
    }

    func testAPageIsReadFromItsTitleOrItsNameIgnoringCaseAndSpacing() {
        XCTAssertEqual(SettingsNavigation.parsePage("Try dictation"), .tryDictation)
        XCTAssertEqual(SettingsNavigation.parsePage("trydictation"), .tryDictation)
        XCTAssertEqual(SettingsNavigation.parsePage("  AI-cleanup "), .aiCleanup)
        XCTAssertEqual(SettingsNavigation.parsePage("voice snippets"), .voiceSnippets)
        XCTAssertNil(SettingsNavigation.parsePage("nonsense"))
        XCTAssertNil(SettingsNavigation.parsePage("  "))
        XCTAssertNil(SettingsNavigation.parsePage(nil))
    }

    func testSettingsOpensDictationAndSettingsEqualsPageOpensThatPage() {
        XCTAssertEqual(SettingsNavigation.parseSettingsArgument(["--settings"]), .dictation)
        XCTAssertEqual(SettingsNavigation.parseSettingsArgument(["x", "--settings=History"]), .history)
        XCTAssertNil(SettingsNavigation.parseSettingsArgument(["--settings=nope"]))
        XCTAssertNil(SettingsNavigation.parseSettingsArgument(["--other"]))
    }
}
