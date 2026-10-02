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
        let requirements: [String]
    }

    /// What a Windows requirement name stands for on the Mac: the anchor of the setting it needs.
    private static let requirementAnchors = [
        "RequiresAi": "ai.enabled", "RequiresLocal": "ai.local", "RequiresScribeModel": "ai.local.scribe",
        "RequiresFoundry": "ai.foundry", "RequiresCustom": "ai.custom", "RequiresCopilot": "ai.copilot",
        "RequiresOllama": "ai.local.ollama", "RequiresLmStudio": "ai.local.lmstudio",
        "RequiresAzureCli": "ai.azure.auth.cli", "RequiresAzureServicePrincipal": "ai.azure.auth.sp",
        "RequiresAzureApiKey": "ai.azure.auth.key", "RequiresAzureSignIn": "ai.azure.signin",
        "RequiresAzureManualDetails": "ai.azure.manual",
    ]

    /// A Windows string constant such as `LocalModelTuningText.ContextSizeTitle`, read from its source.
    private func windowsConstant(_ reference: String) throws -> String {
        let parts = reference.split(separator: ".").map(String.init)
        let source = try WindowsSources.read("src/Scribe.Core/Settings/\(parts[0]).cs")
        let regex = try NSRegularExpression(pattern: "const string \(parts[1]) = \"([^\"]*)\"")
        let range = NSRange(source.startIndex..., in: source)
        let match = try XCTUnwrap(regex.firstMatch(in: source, range: range), reference)
        return String(source[try XCTUnwrap(Range(match.range(at: 1), in: source))])
    }

    /// Windows entries whose search label differs from the label on their own page, so the Mac page label cannot match.
    private static let windowsLabelDrift = ["dictation.startup": "Start with Windows"]

    /// The `Requires...` names that end an entry line, in order.
    private static func requirementNames(in line: String) -> [String] {
        guard let open = line.range(of: ", [Requires", options: .backwards) else { return [] }
        let tail = line[line.index(open.lowerBound, offsetBy: 3)...]
        return tail.trimmingCharacters(in: CharacterSet(charactersIn: "[]); ,")).split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespaces) }
    }

    private func windowsEntries() throws -> [WindowsEntry] {
        try windowsEntries(from: try WindowsSources.read("src/Scribe.Core/Settings/SettingsSearchIndex.cs"))
    }

    private func windowsEntries(from source: String) throws -> [WindowsEntry] {
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
            let label = try capture(3) ?? capture(4).map { try windowsConstant($0) } ?? ""
            let keywordText = capture(5) ?? ""
            let keywords = keywordText.split(separator: ",").map {
                $0.trimmingCharacters(in: CharacterSet(charactersIn: " \""))
            }
            var context: String?
            if let raw = capture(6), raw != "null" {
                context = raw.trimmingCharacters(in: CharacterSet(charactersIn: "\""))
            }
            let requirements = Self.requirementNames(in: text)
            found.append(
                WindowsEntry(
                    id: capture(1) ?? "", page: capture(2) ?? "", label: label, context: context, keywords: keywords,
                    requirements: requirements))
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

    /// The Mac requirements that differ from a Windows source, as messages; empty when they agree or are recorded.
    private func requirementDifferences(in source: String) throws -> [String] {
        let mac = Dictionary(uniqueKeysWithValues: SettingsSearchIndex.entries.map { ($0.id, $0) })
        var messages: [String] = []
        for entry in try windowsEntries(from: source) {
            guard let twin = mac[entry.id] else { continue }
            let expected = entry.requirements.map { Self.requirementAnchors[$0] ?? "?\($0)" }
            let actual = twin.requirements.map(\.anchor)
            if SettingsSearchIndex.requirementDeviations[entry.id] != nil {
                if expected == actual { messages.append("\(entry.id) is listed as different but is not") }
            } else if expected != actual {
                messages.append("\(entry.id) needs \(expected) on Windows and \(actual) here")
            }
        }
        return messages
    }

    func testRequirementsFollowWindowsOrAreRecordedAsDifferent() throws {
        let source = try WindowsSources.read("src/Scribe.Core/Settings/SettingsSearchIndex.cs")
        XCTAssertEqual(try requirementDifferences(in: source), [])
    }

    func testAChangedWindowsDependencyIsDetected() throws {
        let source = try WindowsSources.read("src/Scribe.Core/Settings/SettingsSearchIndex.cs")
        let mutated = source.replacingOccurrences(
            of: "[RequiresAi, RequiresLocal, RequiresOllama]", with: "[RequiresAi, RequiresOllama]")
        XCTAssertNotEqual(mutated, source)
        let differences = try requirementDifferences(in: mutated)
        XCTAssertTrue(differences.contains { $0.hasPrefix("ai.local.ollama.context") }, "\(differences)")
    }

    func testRequirementLabelsAndKindsEqualTheirWindowsDefinitions() throws {
        let source = try WindowsSources.read("src/Scribe.Core/Settings/SettingsSearchIndex.cs")
        let pattern =
            "SettingsSearchRequirement (\\w+) =\\s*new\\(\"\\w+\", \"([^\"]*)\", "
            + "SettingsSearchRequirementKind\\.(\\w+)\\)"
        let regex = try NSRegularExpression(pattern: pattern)
        let range = NSRange(source.startIndex..., in: source)
        let all = SettingsSearchIndex.entries.flatMap(\.requirements)
        let deviations = SettingsCopy.allItems.filter { $0.deviation != nil && $0.windows != nil }
        var checked = 0
        for match in regex.matches(in: source, range: range) {
            func capture(_ index: Int) -> String { String(source[Range(match.range(at: index), in: source)!]) }
            let anchor = try XCTUnwrap(Self.requirementAnchors[capture(1)], capture(1))
            guard let requirement = all.first(where: { $0.anchor == anchor }) else { continue }
            checked += 1
            if requirement.label != capture(2) {
                let recorded = deviations.contains { $0.render() == requirement.label && $0.windows == capture(2) }
                XCTAssertTrue(recorded, "\(anchor): \(requirement.label) against \(capture(2))")
            }
            XCTAssertEqual(String(describing: requirement.kind).lowercased(), capture(3).lowercased(), anchor)
        }
        XCTAssertGreaterThan(checked, 8)
    }

    func testTheDeclaredWindowsLabelDriftIsStillWhatWindowsHas() throws {
        let windows = Dictionary(uniqueKeysWithValues: try windowsEntries().map { ($0.id, $0.label) })
        for (id, label) in Self.windowsLabelDrift {
            XCTAssertEqual(windows[id], label, "\(id) no longer has the label the drift table declares")
        }
    }

    func testCopilotCanBeReadWithAICleanupOff() throws {
        let entry = try XCTUnwrap(SettingsSearchIndex.entries.first { $0.id == "ai.copilot" })
        XCTAssertTrue(entry.requirements.isEmpty)
        XCTAssertNotNil(SettingsSearchIndex.requirementDeviations["ai.copilot"])
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
