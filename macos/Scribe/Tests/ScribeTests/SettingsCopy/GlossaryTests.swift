import Foundation
import XCTest

@testable import Scribe

final class GlossaryTests: XCTestCase {
    /// Files whose text is never Scribe's words to a person, with why. Paths are relative to Sources/Scribe.
    static let excludedFiles: [String: String] = [
        "Settings/Copy/CopyGlossary.swift": "The retired words themselves, as patterns.",
        "Settings/Copy/SettingsPage.swift": "Page keywords keep the old names, so a search for \"hotkey\" still works.",
        "Settings/Copy/SettingsSearchIndex.swift": "Entry keywords keep the old names on purpose, as on Windows.",
    ]

    func testCatalogTextUsesNoRetiredName() throws {
        for item in SettingsCopy.allItems {
            let text = withoutPlaceholders(item.text)
            let names = CopyGlossary.violations(in: text).map(\.name)
            XCTAssertTrue(names.isEmpty, "\(item.id) uses retired \(names): \(item.text)")
        }
    }

    func testTheWindowsRulesAreTheMacRulesWordForWord() throws {
        let source = try WindowsSources.read("tests/Scribe.Core.Tests/GlossarySourceTests.cs")
        let regex = try NSRegularExpression(pattern: "new\\(\"([^\"]+)\", @\"([^\"]*)\", \"[^\"]*\"\\)")
        let range = NSRange(source.startIndex..., in: source)
        var windows: [(String, String)] = []
        for match in regex.matches(in: source, range: range) {
            let name = Range(match.range(at: 1), in: source).map { String(source[$0]) } ?? ""
            let pattern = Range(match.range(at: 2), in: source).map { String(source[$0]) } ?? ""
            windows.append((name, pattern))
        }
        XCTAssertGreaterThan(windows.count, 30)
        XCTAssertEqual(windows.map(\.0), CopyGlossary.windowsRules.map(\.name), "Windows changed its retired words")
        XCTAssertEqual(windows.map(\.1), CopyGlossary.windowsRules.map(\.pattern))
    }

    func testTheRulesFindTheirWordsAndLeaveProductNamesAlone() {
        func flagged(_ text: String) -> [String] { CopyGlossary.violations(in: text).map(\.name) }
        XCTAssertEqual(flagged("Choose a new hotkey"), ["hotkey"])
        XCTAssertEqual(flagged("Open the Playground"), ["playground"])
        XCTAssertEqual(flagged("Runs on this PC"), ["PC"])
        XCTAssertEqual(flagged("Find it in the tray"), ["tray"])
        XCTAssertEqual(flagged("Press Ctrl and Alt"), ["Ctrl and Alt"])
        XCTAssertEqual(flagged("Choose Forever"), ["Forever"])
        XCTAssertTrue(flagged("Sign in with the Azure CLI").isEmpty)
        XCTAssertTrue(flagged("Scribe is under the MIT License").isEmpty)
        XCTAssertTrue(flagged("Open Terminal from the menu bar on this Mac").isEmpty)
        XCTAssertEqual(flagged("Run it in a terminal"), ["terminal"])
        XCTAssertTrue(flagged("Changes saved in your word packs").isEmpty)
    }

    func testTheAllowlistIsExplicitAndNotStale() throws {
        let sourceRoot = Self.sourceRoot
        var seen = Set<String>()
        for entry in GlossaryAllowlist.entries {
            XCTAssertTrue(seen.insert(entry.file + "|" + entry.rule).inserted, "Duplicate \(entry.file) \(entry.rule)")
            XCTAssertFalse(entry.reason.isEmpty, "\(entry.file) \(entry.rule) has no reason")
            XCTAssertGreaterThan(entry.count, 0, "\(entry.file) \(entry.rule) is allowed zero")
            let path = sourceRoot.appendingPathComponent(entry.file).path
            XCTAssertTrue(FileManager.default.fileExists(atPath: path), "\(entry.file) no longer exists")
            XCTAssertTrue(
                CopyGlossary.retired.contains { $0.name == entry.rule }, "\(entry.rule) is not a rule")
        }
        for (file, reason) in Self.excludedFiles {
            XCTAssertFalse(reason.isEmpty)
            let path = sourceRoot.appendingPathComponent(file).path
            XCTAssertTrue(FileManager.default.fileExists(atPath: path), "Excluded \(file) no longer exists")
        }
    }

    /// The ratchet: every string in the Mac sources that a person can read is held to the glossary. Existing
    /// retired words are counted in `GlossaryAllowlist`, exactly; the count can only go down.
    func testExistingMacTextOnlyUsesRetiredNamesTheAllowlistCountsExactly() throws {
        var actual: [String: Int] = [:]
        for (relative, source) in try Self.sourceFiles() where Self.excludedFiles[relative] == nil {
            for literal in SwiftStringScanner.literals(in: source) where !literal.exempt && literal.looksLikeText {
                for rule in CopyGlossary.violations(in: literal.text) {
                    actual[relative + "|" + rule.name, default: 0] += 1
                }
            }
        }
        var allowed: [String: Int] = [:]
        for entry in GlossaryAllowlist.entries {
            allowed[entry.file + "|" + entry.rule] = entry.count
        }
        var problems: [String] = []
        for key in Set(actual.keys).union(allowed.keys).sorted() {
            let found = actual[key] ?? 0
            let permitted = allowed[key] ?? 0
            if found > permitted {
                problems.append("\(key): \(found) found, \(permitted) allowed. New text must not use the retired word.")
            } else if found < permitted {
                problems.append("\(key): \(found) found, \(permitted) allowed. The allowlist is stale, lower it.")
            }
        }
        XCTAssertTrue(problems.isEmpty, "\n" + problems.joined(separator: "\n"))
    }

    private func withoutPlaceholders(_ text: String) -> String {
        var result = ""
        var depth = 0
        for character in text {
            if character == "{" {
                depth += 1
                result.append(" ")
            } else if character == "}" && depth > 0 {
                depth -= 1
            } else if depth == 0 {
                result.append(character)
            }
        }
        return result
    }

    static var sourceRoot: URL {
        WindowsSources.repositoryRoot
            .appendingPathComponent("macos", isDirectory: true)
            .appendingPathComponent("Scribe", isDirectory: true)
            .appendingPathComponent("Sources", isDirectory: true)
            .appendingPathComponent("Scribe", isDirectory: true)
    }

    /// Every Swift file under Sources/Scribe with its path relative to it, in a stable order.
    static func sourceFiles() throws -> [(String, String)] {
        let root = sourceRoot
        let walker = try XCTUnwrap(FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil))
        var files: [(String, String)] = []
        for case let url as URL in walker where url.pathExtension == "swift" {
            let relative = String(url.path.dropFirst(root.path.count + 1))
            files.append((relative, try String(contentsOf: url, encoding: .utf8)))
        }
        return files.sorted { $0.0 < $1.0 }
    }
}
