import Foundation
import XCTest

@testable import Scribe

final class SettingsSearchIndexTests: XCTestCase {
    private struct WindowsEntry {
        let id: String
        let page: String
        let label: String
        let context: String?
        let keywords: [String]
    }

    private static let labelConstants = [
        "LocalModelTuningText.WholeVocabularyTitle": "Send your whole vocabulary when it fits",
        "LocalModelTuningText.ContextSizeTitle": "Context size",
    ]

    /// Windows entries whose search label differs from the label on their own page, so the Mac page label cannot match.
    private static let windowsLabelDrift = ["dictation.startup": "Start with Windows"]

    private func windowsEntries() throws -> [WindowsEntry] {
        let source = try WindowsSources.read("src/Scribe.Core/Settings/SettingsSearchIndex.cs")
        let head =
            "Entry\\(\"([^\"]+)\", SettingsPage\\.(\\w+), \"\\w+\", (?:\"([^\"]*)\"|(LocalModelTuningText\\.\\w+)), "
        let regex = try NSRegularExpression(pattern: head + "\\[([^\\]]*)\\](?:, (null|\"[^\"]*\"))?")
        var found: [WindowsEntry] = []
        for line in source.split(separator: "\n") where line.contains("Entry(\"") && !line.contains("private static") {
            let text = String(line)
            guard let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else { continue }
            func capture(_ index: Int) -> String? {
                Range(match.range(at: index), in: text).map { String(text[$0]) }
            }
            let label = capture(3) ?? Self.labelConstants[capture(4) ?? ""] ?? ""
            let keywordText = capture(5) ?? ""
            let keywords = keywordText.split(separator: ",").map {
                $0.trimmingCharacters(in: CharacterSet(charactersIn: " \""))
            }
            var context: String?
            if let raw = capture(6), raw != "null" {
                context = raw.trimmingCharacters(in: CharacterSet(charactersIn: "\""))
            }
            found.append(
                WindowsEntry(
                    id: capture(1) ?? "", page: capture(2) ?? "", label: label, context: context, keywords: keywords))
        }
        return found
    }

    func testTheWindowsIndexIsReadInFull() throws {
        let windows = try windowsEntries()
        let source = try WindowsSources.read("src/Scribe.Core/Settings/SettingsSearchIndex.cs")
        let declared = source.components(separatedBy: "        Entry(\"").count - 1
        XCTAssertEqual(windows.count, declared, "The test could not read every Windows entry")
        XCTAssertGreaterThan(windows.count, 50)
        XCTAssertTrue(source.contains("private const int MaxResults = \(SettingsSearchIndex.maxResults);"))
    }

    func testEveryWindowsSettingIsOnTheMacOrOmittedWithAReason() throws {
        let windows = try windowsEntries()
        let macIds = Set(SettingsSearchIndex.entries.map(\.id))
        let omitted = Set(SettingsSearchIndex.omissions.map(\.id))
        for entry in windows where !macIds.contains(entry.id) {
            XCTAssertTrue(omitted.contains(entry.id), "Windows has '\(entry.id)' and the Mac neither has nor omits it")
        }
        let windowsIds = Set(windows.map(\.id))
        for id in omitted {
            XCTAssertTrue(windowsIds.contains(id), "Omission '\(id)' is not a Windows entry")
            XCTAssertFalse(macIds.contains(id), "'\(id)' is both on the Mac and omitted")
        }
    }

    func testEveryMacOnlySettingIsListedWithAReason() throws {
        let windowsIds = Set(try windowsEntries().map(\.id))
        for entry in SettingsSearchIndex.entries where !windowsIds.contains(entry.id) {
            XCTAssertNotNil(SettingsSearchIndex.macOnly[entry.id], "'\(entry.id)' is Mac only and has no reason")
        }
        for (id, reason) in SettingsSearchIndex.macOnly {
            XCTAssertFalse(reason.isEmpty)
            XCTAssertTrue(SettingsSearchIndex.entries.contains { $0.id == id }, "'\(id)' is listed but not an entry")
            XCTAssertFalse(windowsIds.contains(id), "'\(id)' is a Windows entry, not Mac only")
        }
        let ids = SettingsSearchIndex.entries.map(\.id)
        XCTAssertEqual(Set(ids).count, ids.count)
    }

    func testSharedSettingsKeepTheirPageTheirOrderAndEveryOldWord() throws {
        let windows = try windowsEntries()
        let mac = Dictionary(uniqueKeysWithValues: SettingsSearchIndex.entries.map { ($0.id, $0) })
        var lastIndex = -1
        let macOrder = SettingsSearchIndex.entries.map(\.id)
        for entry in windows {
            guard let twin = mac[entry.id] else { continue }
            XCTAssertEqual(String(describing: twin.page).lowercased(), entry.page.lowercased(), entry.id)
            let missing = Set(entry.keywords).subtracting(twin.keywords)
            XCTAssertTrue(missing.isEmpty, "\(entry.id) lost the old words \(missing.sorted())")
            let index = try XCTUnwrap(macOrder.firstIndex(of: entry.id))
            XCTAssertGreaterThan(index, lastIndex, "\(entry.id) is out of Windows order")
            lastIndex = index
        }
    }

    func testSharedLabelsAndContextsEqualWindowsOrDeviateWithARecordedReason() throws {
        let deviations = SettingsCopy.allItems.filter { $0.deviation != nil && $0.windows != nil }
        func isRecorded(_ mac: String, windows: String) -> Bool {
            deviations.contains { $0.render() == mac && $0.windows == windows }
        }
        let mac = Dictionary(uniqueKeysWithValues: SettingsSearchIndex.entries.map { ($0.id, $0) })
        for entry in try windowsEntries() {
            guard let twin = mac[entry.id] else { continue }
            if twin.label != entry.label, Self.windowsLabelDrift[entry.id] == nil {
                XCTAssertTrue(isRecorded(twin.label, windows: entry.label), "\(entry.id) label \(twin.label)")
            }
            if twin.context != entry.context, let macContext = twin.context, let windowsContext = entry.context {
                XCTAssertTrue(isRecorded(macContext, windows: windowsContext), "\(entry.id) context \(macContext)")
            } else if twin.context != entry.context {
                let have = String(describing: twin.context)
                XCTFail("\(entry.id) has context \(have), Windows has \(String(describing: entry.context))")
            }
        }
    }

    func testRequirementAnchorsPointAtASettingOrAKnownControl() {
        let ids = Set(SettingsSearchIndex.entries.map(\.id))
        let known: Set<String> = ["ai.azure.signin", "ai.azure.manual"]
        for entry in SettingsSearchIndex.entries {
            for requirement in entry.requirements {
                XCTAssertTrue(
                    ids.contains(requirement.anchor) || known.contains(requirement.anchor),
                    "\(entry.id) needs unknown \(requirement.anchor)")
            }
        }
    }

    func testSearchRanksLabelsThenKeywordsThenPagesAndShowsTheLabelOnItsPage() throws {
        let microphone = SettingsSearchIndex.search("microphone")
        XCTAssertEqual(microphone.first?.entry.id, "dictation.microphone")
        XCTAssertEqual(microphone.first?.displayText, "Microphone on Dictation")

        let old = SettingsSearchIndex.search("hotkey")
        XCTAssertEqual(old.first?.entry.id, "dictation.shortcut")
        XCTAssertEqual(SettingsSearchIndex.search("playground").first?.entry.id, "try.page")
        XCTAssertEqual(SettingsSearchIndex.search("libraries").first?.entry.id, "dictionary.word-packs")

        let byPage = SettingsSearchIndex.search("history")
        XCTAssertTrue(byPage.contains { $0.entry.id == "history.keep" })
        XCTAssertLessThanOrEqual(SettingsSearchIndex.search("a").count, SettingsSearchIndex.maxResults)
    }

    func testSearchIgnoresCaseAccentsAndPunctuationAndNeedsEveryTerm() {
        XCTAssertEqual(SettingsSearchIndex.search("MÍCROPHONE").first?.entry.id, "dictation.microphone")
        XCTAssertEqual(SettingsSearchIndex.search("push-to-talk").first?.entry.id, "dictation.shortcut")
        XCTAssertTrue(SettingsSearchIndex.search("microphone zzzz").isEmpty)
        XCTAssertTrue(SettingsSearchIndex.search("   ").isEmpty)
        XCTAssertTrue(SettingsSearchIndex.search(nil).isEmpty)
        XCTAssertTrue(SettingsSearchIndex.search("microphone", maxResults: 0).isEmpty)
    }

    func testAResultOnAHiddenControlSaysWhatToChooseOrTurnOn() throws {
        let result = try XCTUnwrap(SettingsSearchIndex.search("context size ollama").first)
        XCTAssertEqual(result.entry.id, "ai.local.ollama.context")
        XCTAssertEqual(result.displayText, "Context size (Ollama) on AI cleanup")
        let hints = result.entry.requirements.map { SettingsSearchIndex.requirementHint($0) }
        XCTAssertEqual(
            hints,
            [
                "Turn on \"Use AI cleanup\" to see this setting.",
                "Choose \"On this Mac\" to see this setting.",
                "Choose \"Ollama\" to see this setting.",
            ])
        let signIn = SettingsSearchRequirement(anchor: "ai.azure.signin", label: "Sign in to Azure", kind: .action)
        XCTAssertEqual(SettingsSearchIndex.requirementHint(signIn), "Sign in to Azure to see this setting.")
        let cli = try XCTUnwrap(SettingsSearchIndex.entries.first { $0.id == "ai.azure.auth.cli" }?.requirements.last)
        XCTAssertEqual(
            SettingsSearchIndex.requirementHint(
                SettingsSearchRequirement(anchor: "x", label: "Your Azure account (recommended)", kind: .radio)),
            "Choose \"Your Azure account\" to see this setting.")
        XCTAssertEqual(cli.kind, .radio)
    }

    func testGitHubCopilotCanBeFoundSoTheExplanationCanBeShown() {
        let copilot = SettingsSearchIndex.search("copilot")
        XCTAssertEqual(copilot.first?.entry.id, "ai.copilot")
        XCTAssertFalse(SettingsSearchIndex.entries.contains { $0.id == "ai.copilot.model" })
    }

    func testPagesCanBeFoundByNameOrOldWord() {
        XCTAssertEqual(SettingsSearchIndex.matchingPages("playground"), [.tryDictation])
        XCTAssertEqual(SettingsSearchIndex.matchingPages("snippets"), [.voiceSnippets])
        XCTAssertTrue(SettingsSearchIndex.matchingPages("diag").contains(.diagnostics))
        XCTAssertTrue(SettingsSearchIndex.matchingPages("").isEmpty)
    }
}
