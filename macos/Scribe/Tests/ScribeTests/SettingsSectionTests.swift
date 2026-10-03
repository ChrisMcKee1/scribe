import XCTest

@testable import Scribe

final class SettingsSectionTests: XCTestCase {
    func testSettingsSectionsMatchWindowsOrderLabelsAndGroups() {
        XCTAssertEqual(
            SettingsSection.allCases,
            [
                .dictation, .tryDictation, .aiCleanup, .dictionary, .voiceSnippets, .appProfiles, .history, .usage,
                .advanced, .diagnostics, .about,
            ])
        XCTAssertEqual(
            SettingsSection.allCases.map(\.label),
            [
                "Dictation",
                "Try dictation",
                "AI cleanup",
                "Dictionary",
                "Voice snippets",
                "App profiles",
                "History",
                "Usage",
                "Advanced",
                "Diagnostics",
                "About",
            ])
        XCTAssertEqual(SettingsSection.topLevel, [.dictation, .tryDictation, .aiCleanup])
        XCTAssertEqual(SettingsSection.personalize, [.dictionary, .voiceSnippets, .appProfiles])
        XCTAssertEqual(SettingsSection.review, [.history, .usage])
        XCTAssertEqual(SettingsSection.more, [.advanced, .diagnostics, .about])
        XCTAssertEqual(SettingsSection.topLevel.map(\.group), [nil, nil, nil])
        XCTAssertEqual(SettingsSection.personalize.map(\.group), Array(repeating: "Personalize", count: 3))
        XCTAssertEqual(SettingsSection.review.map(\.group), Array(repeating: "Review", count: 2))
        XCTAssertEqual(SettingsSection.more.map(\.group), Array(repeating: "More", count: 3))
    }

    func testSettingsSectionParserKeepsOldDeepLinksWorking() {
        XCTAssertEqual(SettingsSection.parse("try dictation"), .tryDictation)
        XCTAssertEqual(SettingsSection.parse("AI cleanup"), .aiCleanup)
        XCTAssertEqual(SettingsSection.parse("word-packs"), .dictionary)
        XCTAssertEqual(SettingsSection.parse("playground"), .tryDictation)
        XCTAssertEqual(SettingsSection.parse("hotkey"), .dictation)
        XCTAssertEqual(SettingsSection.parse("overlay"), .dictation)
        XCTAssertEqual(SettingsSection.parse("libraries"), .dictionary)
        XCTAssertEqual(SettingsSection.parse("usageInsights"), .usage)
    }
}
