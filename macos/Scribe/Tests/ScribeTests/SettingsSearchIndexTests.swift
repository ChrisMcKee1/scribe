import XCTest

@testable import Scribe

final class SettingsSearchIndexTests: XCTestCase {
    func testSynonymsFindExpectedSettings() {
        assertSearch("hotkey", contains: "Dictation shortcut", on: .dictation)
        assertSearch("shortcut", contains: "Dictation shortcut", on: .dictation)
        assertSearch("key", contains: "Dictation shortcut", on: .dictation)
        assertSearch("VAD", contains: "Trim silence", on: .advanced)
        assertSearch("silence", contains: "Stop when I stop talking", on: .dictation)
        assertSearch("overlay", contains: "Show the recording indicator", on: .dictation)
        assertSearch("pill", contains: "Show the recording indicator", on: .dictation)
        assertSearch("hide indicator", contains: "Show the recording indicator", on: .dictation)
        assertSearch("preview", contains: "Preview on screen", on: .dictation)
        assertSearch("library", contains: "Word packs", on: .dictionary)
        assertSearch("libraries", contains: "Word packs", on: .dictionary)
        assertSearch("vocabulary", contains: "Word packs", on: .dictionary)
        assertSearch("playground", contains: "Try dictation", on: .tryDictation)
        assertSearch("model", contains: "Model alias", on: .aiCleanup)
        assertSearch("writing style", contains: "Writing style", on: .aiCleanup)
        assertSearch("local guardrail prompt", contains: "Local guardrail prompt", on: .aiCleanup)
        assertSearch("detailed prompt", contains: "Detailed guardrail prompt", on: .aiCleanup)
        assertSearch("restore default", contains: "Restore default guardrail prompts", on: .aiCleanup)
        assertSearch("Foundry API key", contains: "Microsoft Foundry API key", on: .aiCleanup)
        assertSearch("startup", contains: "Start Scribe when you log in", on: .dictation)
        assertSearch("boot", contains: "Start Scribe when you log in", on: .dictation)
        assertSearch("input monitoring", contains: "Input Monitoring access", on: .dictation)
        assertSearch("accessibility", contains: "Accessibility insertion", on: .advanced)
    }

    func testSearchIsCaseAccentInsensitiveAndWordPrefixBased() {
        XCTAssertTrue(SettingsSearchIndex.search("mícrophone").contains { $0.label == "Microphone" })
        XCTAssertTrue(SettingsSearchIndex.search("rec ind").contains { $0.label == "Show the recording indicator" })
        XCTAssertTrue(SettingsSearchIndex.search("ÁI").contains { $0.section == .aiCleanup })
        XCTAssertTrue(SettingsSearchIndex.search("İdle").contains { $0.label == "Free memory when Scribe is not used" })
        XCTAssertTrue(SettingsSearchIndex.search("push-to-talk").contains { $0.label == "Dictation shortcut" })
        XCTAssertTrue(SettingsSearchIndex.search("e\u{301}").contains { $0.label == "Endpoint" })
        XCTAssertTrue(SettingsSearchIndex.search("zzzz").isEmpty)
    }

    func testSearchRanksLabelMatchesBeforeKeywordsAndCapsResults() {
        let results = SettingsSearchIndex.search("model", maxResults: 20)

        XCTAssertLessThanOrEqual(results.count, 8)
        XCTAssertEqual(results.first?.label, "Model alias")
        XCTAssertEqual(results.first?.context, "On this PC")
        XCTAssertTrue(results.contains { $0.displayText == "Speech model on Advanced" })
        XCTAssertTrue(results.prefix { $0.label.localizedCaseInsensitiveContains("model") }.count > 0)
    }

    func testResultsReadAsSettingContextOnPage() {
        let boot = SettingsSearchIndex.search("boot")
        XCTAssertEqual(boot.single?.displayText, "Start Scribe when you log in on Dictation")

        let foundryModel = SettingsSearchIndex.search("foundry model", maxResults: 20)
            .first { $0.entry.id == "ai.model" }
        XCTAssertEqual(foundryModel?.displayText, "Model alias (On this PC) on AI cleanup")
    }

    func testNoTwoEntriesHaveTheSameDisplayText() {
        let duplicates = Dictionary(grouping: SettingsSearchIndex.entries) { entry in
            "\(entry.displayLabel) on \(entry.pageLabel)"
        }
        .filter { $0.value.count > 1 }
        XCTAssertTrue(duplicates.isEmpty, "Duplicate search result text: \(duplicates.keys.sorted())")
    }

    func testHiddenTargetsCarryRequirementChains() {
        assertRequirements("ai.model", ["checkbox:ai.enabled", "radio:ai.provider", "radio:ai.provider"])
        assertRequirements(
            "ai.local.ollama.context", ["checkbox:ai.enabled", "radio:ai.provider", "radio:ai.local.ollama"])
        assertRequirements("ai.azure.sp.secret", ["checkbox:ai.enabled", "radio:ai.provider", "radio:ai.azure.auth"])
        assertRequirements("ai.azure.api-key", ["checkbox:ai.enabled", "radio:ai.provider"])
        assertRequirements("ai.custom.model", ["checkbox:ai.enabled", "radio:ai.provider"])
    }

    func testEveryEntryTargetsAnExistingSection() {
        let sections = Set(SettingsSection.allCases)
        for entry in SettingsSearchIndex.entries {
            XCTAssertTrue(sections.contains(entry.section), "\(entry.id) targets missing section \(entry.section)")
            XCTAssertFalse(entry.targetID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, entry.id)
            for requirement in entry.requirements {
                XCTAssertFalse(
                    requirement.controlName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, entry.id)
            }
        }
    }

    func testLimitsAndBlankQueries() {
        XCTAssertTrue(SettingsSearchIndex.search(nil).isEmpty)
        XCTAssertTrue(SettingsSearchIndex.search("").isEmpty)
        XCTAssertTrue(SettingsSearchIndex.search("   ").isEmpty)
        XCTAssertTrue(SettingsSearchIndex.search("--").isEmpty)
        XCTAssertTrue(SettingsSearchIndex.search("model", maxResults: 0).isEmpty)
        XCTAssertEqual(SettingsSearchIndex.search("model", maxResults: 1).count, 1)
        XCTAssertLessThanOrEqual(SettingsSearchIndex.search("model", maxResults: Int.max).count, 8)
    }

    func testWordsAreStableAcrossRepresentativeQueries() {
        XCTAssertEqual(SettingsSearchIndex.words("Cópi"), ["copi"])
        XCTAssertEqual(SettingsSearchIndex.words("push-to-talk"), ["push", "to", "talk"])
        XCTAssertEqual(SettingsSearchIndex.words("AI, cleanup"), ["ai", "cleanup"])
        XCTAssertEqual(SettingsSearchIndex.words("\u{FF2D}odel"), ["ｍodel"])
        XCTAssertEqual(SettingsSearchIndex.words("e\u{301}"), ["e"])
    }

    private func assertSearch(_ query: String, contains label: String, on section: SettingsSection) {
        XCTAssertTrue(
            SettingsSearchIndex.search(query).contains { $0.label == label && $0.section == section },
            "Expected '\(query)' to find '\(label)' on \(section.label)")
    }

    private func assertRequirements(_ id: String, _ expected: [String]) {
        let entry = SettingsSearchIndex.entries.single { $0.id == id }
        XCTAssertEqual(entry?.requirements.map { "\($0.kind.rawValue):\($0.controlName)" }, expected)
    }
}

extension Array {
    fileprivate var single: Element? {
        count == 1 ? first : nil
    }

    fileprivate func single(where predicate: (Element) throws -> Bool) rethrows -> Element? {
        let matches = try filter(predicate)
        return matches.count == 1 ? matches[0] : nil
    }
}
