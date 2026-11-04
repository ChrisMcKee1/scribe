import XCTest

@testable import Scribe

/// The formatting a dictation gets, ported from Windows' `AppAwareFormattingTests`. The Mac has no typing method
/// setting, so where Windows checks the injection method these check the delivery a format asks for.
final class DictationFormatPlanTests: XCTestCase {
    private let on = DictationFormattingSettings()
    private let off = DictationFormattingSettings(appAwareFormatting: false, defaultTextFormat: .markdownSource)

    private func plan(
        _ bundleIdentifier: String?,
        name: String? = nil,
        profile: AppProfile? = nil,
        newline: NewlineInjectionMode = .smartFlatten,
        settings: DictationFormattingSettings? = nil,
        plainOnce: Bool = false
    ) -> DictationFormatPlan {
        DictationFormatPlan.capture(
            profile: profile, globalNewlineMode: newline, settings: settings ?? on, bundleIdentifier: bundleIdentifier,
            applicationName: name, plainOnce: plainOnce)
    }

    private func profile(
        _ bundleIdentifier: String,
        format: DictationTextFormat? = nil,
        newline: NewlineInjectionMode? = nil
    ) -> AppProfile {
        AppProfile(
            name: "Profile", bundleIdentifiers: [bundleIdentifier], processNames: [], writingStylePrompt: nil,
            newlineHandling: newline, textFormat: format)
    }

    // MARK: - Stored names

    func testFormatNamesReadIgnoringCaseAndAnUnknownNameIsPlainText() {
        XCTAssertEqual(DictationTextFormat(storedName: "MarkdownSource"), .markdownSource)
        XCTAssertEqual(DictationTextFormat(storedName: "markdownwithformatting"), .markdownWithFormatting)
        XCTAssertEqual(DictationTextFormat(storedName: "PLAIN"), .plain)
        for unknown in ["", "1", "Markdown", "RichText", "MarkdownSource, Plain", " MarkdownSource"] {
            XCTAssertEqual(DictationTextFormat(storedName: unknown), .plain, unknown.debugDescription)
        }
        XCTAssertEqual(
            DictationTextFormat.allCases.map(\.rawValue), ["Plain", "MarkdownSource", "MarkdownWithFormatting"])
        XCTAssertEqual(DictationTextFormat.allCases.filter(\.isMarkdown), [.markdownSource, .markdownWithFormatting])
    }

    func testFormattingIsOnUnlessAStoredFalseTurnsItOff() throws {
        let suite = try SettingsTestDefaults()
        defer { suite.remove() }
        let store = TypingSettingsStore(defaults: suite.defaults)

        XCTAssertEqual(store.formatting, DictationFormattingSettings())
        for notFalse: Any in ["false", 0, "no", ["false"]] {
            suite.defaults.set(notFalse, forKey: "ScribeAppAwareFormatting")
            XCTAssertTrue(store.appAwareFormatting, "\(notFalse)")
        }
        store.appAwareFormatting = false
        XCTAssertFalse(store.formatting.appAwareFormatting)

        suite.defaults.set("SomethingLater", forKey: "ScribeDefaultTextFormat")
        XCTAssertEqual(store.defaultTextFormat, .plain)
        store.defaultTextFormat = .markdownWithFormatting
        XCTAssertEqual(store.formatting.defaultTextFormat, .markdownWithFormatting)
    }

    // MARK: - Off

    /// With formatting off every dictation is plain text, typed, with the line-break rule it always had, whatever the
    /// profile, the app or Plain text once says.
    func testOffKeepsThePlainTypedPathAndTheExistingLineBreakRule() {
        let profiled = profile("md.obsidian", format: .markdownWithFormatting, newline: .keepNewlines)
        let cases: [(String?, AppProfile?, NewlineInjectionMode, String)] = [
            ("com.microsoft.teams2", nil, .smartFlatten, "a\nb"),
            ("md.obsidian", profiled, .alwaysFlatten, "a\nb"),
            ("com.apple.Terminal", nil, .smartFlatten, "a b"),
            ("com.example.editor", nil, .alwaysFlatten, "a b"),
            (nil, nil, .keepNewlines, "a\nb"),
        ]
        for (bundle, profile, newline, represented) in cases {
            for plainOnce in [false, true] {
                let off = plan(bundle, profile: profile, newline: newline, settings: off, plainOnce: plainOnce)
                XCTAssertEqual(off.decision, .formattingOff, "\(bundle ?? "nil")")
                XCTAssertEqual(off.textFormat, .plain)
                XCTAssertEqual(off.delivery, .typing)
                XCTAssertFalse(off.keepsWholePasteboard)
                XCTAssertEqual(off.represent("a\nb"), represented, "\(bundle ?? "nil")")
                XCTAssertEqual(
                    off.represent("a\nb"),
                    AppProfileMatcher.applyNewlineMode(
                        AppProfileMatcher.resolveNewlineMode(
                            profile: profile, globalDefault: newline, bundleIdentifier: bundle),
                        to: "a\nb", bundleIdentifier: bundle))
            }
        }
    }

    // MARK: - Built-in apps

    func testKnownMarkdownAppsPasteLiteralMarkdownSourceWithoutAProfile() {
        for bundle in [
            "com.anthropic.claudefordesktop", "com.microsoft.VSCode", "MD.OBSIDIAN", "com.tinyspeck.slackmacgap",
        ] {
            let markdown = plan(bundle)
            XCTAssertEqual(markdown.decision, .markdownSource, bundle)
            XCTAssertEqual(markdown.textFormat, .markdownSource, bundle)
            XCTAssertEqual(markdown.delivery, .paste, bundle)
            XCTAssertTrue(markdown.keepsWholePasteboard, bundle)
            XCTAssertEqual(markdown.represent("- one\n- two"), "- one\n- two")

            let plainOnce = plan(bundle, plainOnce: true)
            XCTAssertEqual(plainOnce.decision, .plainOnce, bundle)
            XCTAssertEqual(plainOnce.textFormat, .plain)
            XCTAssertEqual(plainOnce.delivery, .typing)
            XCTAssertFalse(plainOnce.keepsWholePasteboard)

            // A one-line rule leaves the app on plain text, typed, rather than labeling flattened text Markdown.
            let flattened = plan(bundle, newline: .alwaysFlatten)
            XCTAssertEqual(flattened.decision, .noMatchingProfile, bundle)
            XCTAssertEqual(flattened.textFormat, .plain)
            XCTAssertEqual(flattened.delivery, .typing)
            XCTAssertEqual(flattened.represent("- one\n- two"), "- one - two")
        }
    }

    func testTeamsCopilotAndCodexGetMarkdownWithFormattingAndGitHubCopilotKeepsTheSource() {
        for bundle in ["com.microsoft.teams2", "com.microsoft.teams", "com.openai.codex"] {
            XCTAssertEqual(plan(bundle).textFormat, .markdownWithFormatting, bundle)
            XCTAssertEqual(plan(bundle).decision, .markdownWithFormatting, bundle)
            XCTAssertEqual(plan(bundle).delivery, .paste, bundle)
        }
        XCTAssertEqual(plan("com.example.copilot", name: "Copilot").textFormat, .markdownWithFormatting)
        XCTAssertEqual(plan(nil, name: "copilot").textFormat, .markdownWithFormatting)
        XCTAssertEqual(plan("com.example.github", name: "GitHub Copilot").textFormat, .markdownSource)
        // A known identifier wins over a name, and a name that only contains a known one is not it.
        XCTAssertEqual(plan("com.microsoft.VSCode", name: "Copilot").textFormat, .markdownSource)
        XCTAssertEqual(plan("com.example.editor", name: "Microsoft 365 Copilot Notes").textFormat, .plain)
        XCTAssertNil(
            DictationFormatPlan.builtInFormat(bundleIdentifier: "com.example.editor", applicationName: "Editor"))
        XCTAssertNil(DictationFormatPlan.builtInFormat(bundleIdentifier: nil, applicationName: nil))
        XCTAssertNil(DictationFormatPlan.builtInFormat(bundleIdentifier: "  ", applicationName: " "))
    }

    func testOtherAppsStayPlainAndTyped() {
        for bundle in ["com.example.editor", "com.apple.TextEdit", "com.apple.mail"] {
            let other = plan(bundle)
            XCTAssertEqual(other.decision, .noMatchingProfile, bundle)
            XCTAssertEqual(other.textFormat, .plain)
            XCTAssertEqual(other.delivery, .typing)
        }
    }

    // MARK: - Profiles

    func testAProfileOverridesTheBuiltInDefaultAndAPlainProfileTypes() {
        let plain = plan("com.microsoft.teams2", profile: profile("com.microsoft.teams2", format: .plain))
        XCTAssertEqual(plain.decision, .plain)
        XCTAssertEqual(plain.textFormat, .plain)
        XCTAssertEqual(plain.delivery, .typing)

        let markdown = plan("com.example.editor", profile: profile("com.example.editor", format: .markdownSource))
        XCTAssertEqual(markdown.decision, .markdownSource)
        XCTAssertEqual(markdown.delivery, .paste)
        XCTAssertTrue(markdown.keepsWholePasteboard)

        let formatted = plan(
            "com.example.editor", profile: profile("com.example.editor", format: .markdownWithFormatting))
        XCTAssertEqual(formatted.decision, .markdownWithFormatting)
        XCTAssertEqual(formatted.delivery, .paste)
    }

    /// A profile without a format of its own uses the default text format for app profiles, read when the
    /// recording captured its target: a later change to the settings does not change the plan already made.
    func testAProfileWithoutAFormatUsesTheDefaultAndThePlanIsFixedOnceMade() {
        var settings = DictationFormattingSettings(appAwareFormatting: true, defaultTextFormat: .markdownSource)
        let inherited = profile("com.example.editor")
        let admitted = plan("com.example.editor", profile: inherited, settings: settings)
        XCTAssertEqual(admitted.textFormat, .markdownSource)
        XCTAssertEqual(admitted.delivery, .paste)

        settings.appAwareFormatting = false
        XCTAssertEqual(admitted.textFormat, .markdownSource)
        XCTAssertEqual(admitted.represent("a\nb"), "a\nb")
        XCTAssertEqual(plan("com.example.editor", profile: inherited, settings: settings).textFormat, .plain)

        // The default applies only to profiles: an unmatched app is still plain.
        XCTAssertEqual(
            plan("com.example.editor", settings: DictationFormattingSettings(defaultTextFormat: .markdownSource))
                .textFormat, .plain)
    }

    func testAMarkdownProfileWhoseLineBreakRuleUsesOneLineIsRefusedAsAUnit() {
        let cases: [(String, NewlineInjectionMode?, NewlineInjectionMode)] = [
            ("md.obsidian", .alwaysFlatten, .keepNewlines),
            ("com.apple.Terminal", nil, .smartFlatten),
            ("com.googlecode.iterm2", .smartFlatten, .keepNewlines),
            ("md.obsidian", nil, .alwaysFlatten),
        ]
        for (bundle, own, global) in cases {
            for format in [DictationTextFormat.markdownSource, .markdownWithFormatting] {
                let refused = plan(bundle, profile: profile(bundle, format: format, newline: own), newline: global)
                XCTAssertEqual(refused.decision, .markdownNewlineConflict, bundle)
                XCTAssertEqual(refused.textFormat, .plain, bundle)
                XCTAssertEqual(refused.delivery, .typing, bundle)
                XCTAssertEqual(refused.represent("one\n\ntwo"), "one  two", bundle)
            }
        }
        // Keep Newlines resolves it.
        let kept = plan(
            "com.apple.Terminal",
            profile: profile("com.apple.Terminal", format: .markdownSource, newline: .keepNewlines))
        XCTAssertEqual(kept.decision, .markdownSource)
        // Plain text once takes the plain path without calling it a conflict.
        let once = plan(
            "md.obsidian", profile: profile("md.obsidian", format: .markdownSource, newline: .alwaysFlatten),
            plainOnce: true)
        XCTAssertEqual(once.decision, .plainOnce)
        XCTAssertEqual(once.delivery, .typing)
    }

    func testTheProfileEditorsConflictCheckCoversEveryListedApp() {
        XCTAssertTrue(
            DictationFormatPlan.conflictsWithNewlines(
                .markdownSource, newlineMode: .alwaysFlatten, bundleIdentifiers: []))
        XCTAssertFalse(
            DictationFormatPlan.conflictsWithNewlines(
                .markdownSource, newlineMode: .smartFlatten, bundleIdentifiers: ["md.obsidian"]))
        XCTAssertTrue(
            DictationFormatPlan.conflictsWithNewlines(
                .markdownWithFormatting, newlineMode: .smartFlatten,
                bundleIdentifiers: ["md.obsidian", "com.apple.Terminal"]))
        XCTAssertFalse(
            DictationFormatPlan.conflictsWithNewlines(
                .plain, newlineMode: .alwaysFlatten, bundleIdentifiers: ["com.apple.Terminal"]))
        XCTAssertFalse(
            DictationFormatPlan.conflictsWithNewlines(
                .markdownSource, newlineMode: .keepNewlines, bundleIdentifiers: ["com.apple.Terminal"]))
    }

    // MARK: - Representation

    /// Every format's text is an identity rendering: the characters the pipeline produced, after the target's
    /// line-break rule, with nothing parsed, added or removed.
    func testEveryFormatKeepsTheCharactersAsProduced() {
        let samples = [
            "- one\n- two", "1. first\n2. second", "C:\\Users\\me\\.config", "&copy; <b>x</b>", "**bold** `code`",
            "```swift\nlet x = 1\n```", "Caf\u{00E9} \u{1F600}", "a\r\nb", "",
        ]
        for bundle in ["com.microsoft.teams2", "md.obsidian", "com.example.editor"] {
            for sample in samples {
                XCTAssertEqual(plan(bundle).represent(sample), sample, bundle)
            }
        }
    }

    func testTheWordsUseTheMacLabels() {
        XCTAssertTrue(DictationFormattingText.newlineConflict.contains("Plain text"))
        XCTAssertTrue(DictationFormattingText.newlineConflict.contains("Keep Newlines"))
        XCTAssertEqual(
            DictationTextFormat.allCases.map(DictationFormattingText.label),
            ["Plain text", "Literal Markdown source", "Markdown with formatting"])
    }
}
