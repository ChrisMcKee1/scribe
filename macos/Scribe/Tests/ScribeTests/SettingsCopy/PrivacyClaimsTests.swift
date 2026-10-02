import Foundation
import XCTest

@testable import Scribe

/// The Settings text that says what leaves the Mac, what stays on it and what is kept, held to the code that does it.
/// Every catalog string that makes such a claim must be registered here with a check against the code's own limits,
/// so the words and the behavior cannot drift apart, and a new claim cannot be added without a check.
final class PrivacyClaimsTests: XCTestCase {
    private struct Check {
        var failures: [String] = []
        mutating func expect(_ condition: Bool, _ message: String) {
            if !condition { failures.append(message) }
        }
    }

    /// The pattern that marks a catalog string as a privacy claim.
    private static let claimPattern =
        #"(?i)\b(never leaves?|stays? on this Mac|nothing leaves|goes to|is sent|are sent|never sent|"#
        + #"not sent|sends|sent)\b"#
        + #"|\b(keychain|discarded|doesn't keep|never your dictations|reach the AI)\b"#

    private static func source(_ name: String) -> String {
        let url = GlossaryTests.sourceRoot.appendingPathComponent(name)
        return (try? String(contentsOf: url, encoding: .utf8)) ?? ""
    }

    private static func text(_ item: CopyItem) -> String { item.render() }

    private static let claims: [String: @Sendable () -> Check] = [
        "aiCleanup.onThisMacHint": {
            var check = Check()
            let copy = SettingsCopy.aiCleanup.onThisMacHint
            check.expect(text(copy).contains("stays on this Mac"), "the hint no longer says the text stays here")
            let stays = "stay on this Mac"
            for kind in [CleanupProviderKind.foundryLocal, .ollama] {
                check.expect(
                    CleanupDisclosure.summary(for: kind, endpoint: nil).contains(stays), "\(kind) does not stay local")
            }
            let local = CleanupDisclosure.summary(for: .openAICompatible, endpoint: nil, forceLocal: true)
            check.expect(local.contains(stays), "a server on this Mac does not stay local")
            let remote = CleanupDisclosure.summary(for: .openAICompatible, endpoint: nil, forceLocal: false)
            check.expect(!remote.contains(stays), "a remote server is described as local")
            return check
        },
        "aiCleanup.foundryHint": {
            var check = Check()
            check.expect(text(SettingsCopy.aiCleanup.foundryHint).contains("Azure resource"), "destination changed")
            let summary = CleanupDisclosure.summary(for: .microsoftFoundry, endpoint: nil)
            check.expect(summary.contains("Microsoft Foundry deployment"), "the disclosure names another destination")
            return check
        },
        "aiCleanup.anotherServiceHint": {
            var check = Check()
            check.expect(text(SettingsCopy.aiCleanup.anotherServiceHint).contains("goes to that address"), "text")
            let summary = CleanupDisclosure.summary(for: .openAICompatible, endpoint: nil, forceLocal: false)
            check.expect(summary.contains("to the address you enter"), "the disclosure names another destination")
            return check
        },
        "aiCleanup.disclosureTitle": {
            var check = Check()
            check.expect(text(SettingsCopy.aiCleanup.disclosureTitle) == "What AI cleanup sends", "title")
            let sends = CleanupDisclosure.whatCleanupSends
            check.expect(sends.hasPrefix("Foundry Local runs cleanup on this Mac"), "the disclosure changed")
            return check
        },
        "aiCleanup.clientSecretNote": { keychainCheck(SettingsCopy.aiCleanup.clientSecretNote) },
        "aiCleanup.apiKeyNote": { keychainCheck(SettingsCopy.aiCleanup.apiKeyNote) },
        "aiCleanup.serviceKeyHint": { keychainCheck(SettingsCopy.aiCleanup.serviceKeyHint) },
        "aiCleanup.promptCaching": {
            var check = Check()
            let title = text(SettingsCopy.aiCleanup.promptCaching)
            check.expect(source("LocalAppSettingsSection.swift").contains(title), "the toggle's title changed")
            check.expect(
                source("CleanupSettingsModel.swift").contains("var azurePromptCaching = true"), "the default changed")
            return check
        },
        "wordPacks.aiOnThisMac": {
            var check = Check()
            let said = text(SettingsCopy.wordPacks.aiOnThisMac)
            check.expect(said.contains("nothing leaves it"), "text")
            let summary = CleanupDisclosure.summary(for: .foundryLocal, endpoint: nil)
            check.expect(summary.contains("stay on this Mac"), "Foundry Local no longer stays on this Mac")
            return check
        },
        "wordPacks.aiSends": {
            var check = Check()
            check.expect(text(SettingsCopy.wordPacks.aiSends).contains("seems to mention"), "text")
            let sends = CleanupDisclosure.whatCleanupSends
            check.expect(sends.contains("the dictation appears to mention"), "the disclosure no longer says mentioned")
            check.expect(sends.contains("word packs"), "the disclosure no longer names word packs")
            return check
        },
        "wordPacks.aiNotSent": {
            var check = Check()
            check.expect(text(SettingsCopy.wordPacks.aiNotSent).contains("reach the AI service in the text"), "text")
            check.expect(CleanupDisclosure.whatCleanupSends.contains("the text Scribe recognized"), "the disclosure")
            return check
        },
        "wordPacks.notSentPermission": {
            var check = Check()
            check.expect(text(SettingsCopy.wordPacks.notSentPermission).contains("isn't used in AI cleanup"), "text")
            let service = source("DictionaryLibraryService.swift")
            check.expect(service.contains("aiPermissions"), "the library service has no AI permission")
            check.expect(service.contains("aiPermissionsLost"), "a lost permission no longer withholds")
            return check
        },
        "wordPacks.notSentWord": {
            var check = Check()
            check.expect(text(SettingsCopy.wordPacks.notSentWord).contains("not included in vocabulary"), "text")
            let sends = CleanupDisclosure.whatCleanupSends
            check.expect(sends.contains("spans more than one line"), "the disclosure drops the line rule")
            check.expect(sends.contains("past \(CleanupPrompt.maxGlossaryTermChars) characters"), "the length rule")
            return check
        },
        "about.privacyHint": {
            var check = Check()
            let said = text(SettingsCopy.about.privacyHint)
            check.expect(said.contains("your audio never leaves it"), "text")
            check.expect(said.contains("online services receive text only"), "text")
            check.expect(CleanupDisclosure.whatCleanupNeverSends.contains("audio never leaves this Mac"), "code")
            return check
        },
        "history.noRecordings": {
            var check = Check()
            let scratch = source("ScratchAudio.swift")
            check.expect(scratch.contains("0o600"), "the scratch recording is no longer private")
            check.expect(scratch.contains("unlink("), "the scratch recording is no longer deleted")
            let keepsAudio = ((try? GlossaryTests.sourceFiles()) ?? []).contains { $0.1.contains("audio_blobs") }
            check.expect(!keepsAudio, "some code stores recordings, so the text is wrong")
            return check
        },
        "usage.summarySends": { usageSummaryCheck() },
        "diagnostics.consoleHint": {
            var check = Check()
            check.expect(text(SettingsCopy.diagnostics.consoleHint).contains("never your dictations"), "text")
            check.expect(source("ScribeLog.swift").contains("StaticString"), "log messages are no longer fixed text")
            return check
        },
    ]

    private static func keychainCheck(_ item: CopyItem) -> Check {
        var check = Check()
        check.expect(text(item).contains("Keychain"), "\(item.id) no longer says Keychain")
        check.expect(source("KeychainStore.swift").contains("SecItemAdd"), "the Keychain store is gone")
        check.expect(source("CleanupSettingsStore.swift").contains("SecretStore"), "secrets no longer use the store")
        return check
    }

    private static func usageSummaryCheck() -> Check {
        var check = Check()
        let words = text(SettingsCopy.usage.summarySends)
        for phrase in ["totals", "dictionary words", "Your dictations", "not in your dictionary", "templates"] {
            check.expect(words.contains(phrase), "the text no longer says '\(phrase)'")
        }
        let snapshot = UsageAnalyzer.Snapshot(
            dictations: 3, words: 42, activeDays: 2, speechSeconds: 30, averageWords: 14,
            topApps: [UsageAnalyzer.AppUsage(name: "SecretEditor", dictations: 2, words: 30)],
            trend: [],
            terms: [
                UsageAnalyzer.TermUsage(text: "Next.js", dictations: 2, occurrences: 2, covered: true),
                UsageAnalyzer.TermUsage(text: "ProjectCodename", dictations: 2, occurrences: 2, covered: false),
                UsageAnalyzer.TermUsage(
                    text: "Kind regards, A. Person", dictations: 1, occurrences: 1, covered: true,
                    isTemplateLike: true),
            ],
            granularity: .daily)
        let payload = UsageInsight.buildSummary(snapshot)
        check.expect(payload.contains("Next.js"), "a dictionary word is no longer sent")
        check.expect(!payload.contains("ProjectCodename"), "a word that is not in the dictionary is sent")
        check.expect(!payload.contains("Kind regards"), "a template replacement is sent")
        check.expect(!payload.contains("SecretEditor"), "an app name is sent")
        return check
    }

    func testEveryPrivacyClaimInTheCatalogsIsRegisteredAndEveryRegisteredClaimIsStillMade() throws {
        let regex = try NSRegularExpression(pattern: Self.claimPattern)
        var found = Set<String>()
        for item in SettingsCopy.allItems {
            let text = item.render()
            if regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) != nil {
                found.insert(item.id)
            }
        }
        let registered = Set(Self.claims.keys)
        XCTAssertEqual(
            found.subtracting(registered).sorted(), [], "A privacy claim has no check. Register it in this test.")
        XCTAssertEqual(
            registered.subtracting(found).sorted(), [], "A registered claim is no longer made. Remove its entry.")
    }

    func testEveryPrivacyClaimMatchesTheCodeThatEnforcesIt() {
        for (id, verify) in Self.claims.sorted(by: { $0.key < $1.key }) {
            let check = verify()
            XCTAssertTrue(check.failures.isEmpty, "\(id): \(check.failures.joined(separator: "; "))")
        }
    }

    func testTheOnScreenLimitsAreTheCodesLimits() {
        let sends = CleanupDisclosure.whatCleanupSends
        func shown(_ value: Int) -> String {
            let formatter = NumberFormatter()
            formatter.numberStyle = .decimal
            return formatter.string(from: NSNumber(value: value)) ?? String(value)
        }
        for value in [
            CleanupPrompt.maxGlossaryTermsCloud, CleanupPrompt.maxGlossaryChars, CleanupPrompt.maxGlossaryTermsLocal,
        ] {
            XCTAssertTrue(sends.contains(shown(value)), "The disclosure does not state \(value)")
        }
    }

    /// A setting can stop future sends; it cannot take back what an earlier request already carried. The text may say
    /// what is withheld from now on, but never that nothing was sent or that sent data is removed.
    func testNoTextPromisesToUnsendOrSaysNothingWasSent() throws {
        let pattern =
            #"(?i)\bnothing (was|has been|had been) sent\b|\b(unsend|take back|retract|recall)\b|"#
            + #"\bremoves? what (was|has been) sent\b"#
        let regex = try NSRegularExpression(pattern: pattern)
        for item in SettingsCopy.allItems {
            let text = item.render()
            let hit = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) != nil
            XCTAssertFalse(hit, "\(item.id) promises more than a setting can do: \(text)")
        }
    }
}
