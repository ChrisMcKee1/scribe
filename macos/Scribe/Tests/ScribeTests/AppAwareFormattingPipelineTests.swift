import XCTest

@testable import Scribe

/// The formatting a whole dictation gets through the controller, ported from Windows' `LiteralFormattingDeliveryTests`:
/// the plan is decided from what the recording captured, only the paste request reaches delivery, and history keeps
/// the text as dictated.
@MainActor
final class AppAwareFormattingPipelineTests: XCTestCase {
    static let obsidian = DictationTarget(
        injection: InjectionTarget(processIdentifier: 400, bundleIdentifier: "md.obsidian"),
        bundleIdentifier: "md.obsidian",
        processName: "Obsidian")
    static let teams = DictationTarget(
        injection: InjectionTarget(processIdentifier: 401, bundleIdentifier: "com.microsoft.teams2"),
        bundleIdentifier: "com.microsoft.teams2",
        processName: "Microsoft Teams")

    private let markdownPaste = PasteRequest(keepsWholePasteboard: true, html: nil)

    func testAMarkdownAppIsPastedWithItsPasteboardKeptAndHistoryKeepsTheDictation() async {
        for target in [Self.obsidian, Self.teams] {
            let harness = makeHarness()
            harness.targeting.next = target

            await harness.dictate()
            await harness.waitUntilProcessed()

            XCTAssertEqual(harness.fakeInjector.pasteRequests, [markdownPaste], target.processName ?? "")
            XCTAssertEqual(harness.fakeInjector.texts, ["hello from the recognizer "])
            XCTAssertEqual(harness.history.records.map(\.transcriptText), ["hello from the recognizer"])
            XCTAssertEqual(harness.recovery.recent(), ["hello from the recognizer"])
        }
    }

    /// Markdown with formatting adds an HTML rendering of the text as represented, before the space after it.
    func testTeamsGetsAFormattedCopyOfMarkdownAndNoneForPlainProse() async {
        let harness = makeHarness()
        harness.targeting.next = Self.teams
        harness.transcriber.defaultText = "- first point\n- second point"

        await harness.dictate()
        await harness.waitUntilProcessed()
        harness.transcriber.defaultText = "just a sentence"
        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertEqual(
            harness.fakeInjector.pasteRequests,
            [
                PasteRequest(
                    keepsWholePasteboard: true, html: "<ul>\n<li>first point</li>\n<li>second point</li>\n</ul>"),
                markdownPaste,
            ])
        XCTAssertEqual(harness.fakeInjector.texts, ["- first point\n- second point ", "just a sentence "])
        XCTAssertEqual(
            harness.history.records.map(\.transcriptText), ["- first point\n- second point", "just a sentence"])
    }

    func testAnOrdinaryAppIsTypedAsBefore() async {
        let harness = makeHarness()

        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.fakeInjector.pasteRequests, [nil])
        XCTAssertEqual(harness.fakeInjector.texts, ["hello from the recognizer "])
    }

    func testWithFormattingOffEveryAppIsTyped() async {
        var configuration = DictationController.Configuration()
        configuration.formatting = {
            DictationFormattingSettings(appAwareFormatting: false, defaultTextFormat: .markdownWithFormatting)
        }
        let harness = makeHarness(configuration: configuration)
        harness.targeting.next = Self.teams

        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.fakeInjector.pasteRequests, [nil])
        XCTAssertTrue(harness.notifier.notices.isEmpty)
    }

    func testAProfilesFormatAppliesToItsApps() async {
        let harness = makeHarness(rulesLoaded: false)
        harness.load(profiles: [
            AppProfile(
                name: "Editor", bundleIdentifiers: [FakeTargeting.editorBundle], processNames: [],
                writingStylePrompt: nil, newlineHandling: .keepNewlines, textFormat: .markdownSource),
            AppProfile(
                name: "Teams", bundleIdentifiers: ["com.microsoft.teams2"], processNames: [],
                writingStylePrompt: nil, newlineHandling: nil, textFormat: .plain),
        ])

        await harness.dictate()
        await harness.waitUntilProcessed()
        harness.targeting.next = Self.teams
        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.fakeInjector.pasteRequests, [markdownPaste, nil])
    }

    /// A Markdown profile whose line-break rule uses one line is typed as plain text, one line, and the user is told
    /// why, as Windows' notice does.
    func testAMarkdownProfileWhoseLineBreakRuleUsesOneLineIsTypedAsPlainTextAndSaysSo() async {
        let harness = makeHarness(rulesLoaded: false)
        harness.load(profiles: [
            AppProfile(
                name: "Editor", bundleIdentifiers: [FakeTargeting.editorBundle], processNames: [],
                writingStylePrompt: nil, newlineHandling: .alwaysFlatten, textFormat: .markdownWithFormatting)
        ])
        harness.transcriber.defaultText = "first line\nsecond line"

        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.fakeInjector.pasteRequests, [nil])
        XCTAssertEqual(harness.fakeInjector.texts, ["first line second line "])
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.formattingConflict])
    }

    /// The formatting settings are read when the recording captures its target, so a change while it records or
    /// processes applies from the next recording.
    func testTheSettingsAreReadWhenTheRecordingCapturesItsTarget() async {
        let settings = FormattingSettingsSource()
        var configuration = DictationController.Configuration()
        configuration.formatting = { settings.value }
        let harness = makeHarness(configuration: configuration)
        harness.targeting.next = Self.obsidian

        await harness.press()
        await harness.waitUntilLive()
        settings.value.appAwareFormatting = false
        harness.release()
        await harness.waitUntilProcessed()
        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.fakeInjector.pasteRequests, [markdownPaste, nil])
    }
}

/// Formatting settings a test changes while the controller reads them.
@MainActor
private final class FormattingSettingsSource: Sendable {
    var value = DictationFormattingSettings()
}
