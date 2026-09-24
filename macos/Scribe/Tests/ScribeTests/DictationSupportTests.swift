import AppKit
import XCTest

@testable import Scribe

/// The pieces the dictation lifecycle is built from: ordered turns, the cancellable wait, the revision gate and the
/// pill, the prompt for a single-line target, cleanup invalidation, the startup notice and the recovery submenu.
@MainActor
final class DictationTurnsTests: XCTestCase {
    func testTurnsFollowEnrollmentWhateverOrderTheyAreAskedFor() async {
        let turns = DictationTurns()
        let first = RecordingID.next()
        let second = RecordingID.next()
        turns.enroll(first)
        turns.enroll(second)

        let order = Collected<String>()
        let later = Task { @MainActor in
            if await turns.waitForTurn(second) { order.values.append("second") }
        }
        await waitUntil("the second waits") { turns.waitingCount == 1 }
        let firstGranted = await turns.waitForTurn(first)
        XCTAssertTrue(firstGranted)
        order.values.append("first")
        turns.finish(first)
        await later.value

        XCTAssertEqual(order.values, ["first", "second"])
        turns.finish(second)
        turns.finish(second)
        XCTAssertEqual(turns.enrolledCount, 0)
    }

    func testACancelledWaitReturnsFalseAndKeepsItsPlaceUntilFinished() async {
        let turns = DictationTurns()
        let first = RecordingID.next()
        let second = RecordingID.next()
        let third = RecordingID.next()
        turns.enroll(first)
        turns.enroll(second)
        turns.enroll(third)

        let waiting = Task { @MainActor in await turns.waitForTurn(second) }
        await waitUntil("the second waits") { turns.waitingCount == 1 }
        waiting.cancel()
        let granted = await waiting.value
        XCTAssertFalse(granted)
        XCTAssertEqual(turns.enrolledCount, 3)

        turns.finish(first)
        let thirdWaits = Task { @MainActor in await turns.waitForTurn(third) }
        await drainMainActor()
        XCTAssertEqual(turns.waitingCount, 1, "the third did not wait for the cancelled second, which keeps its place")
        turns.finish(second)
        let thirdGranted = await thirdWaits.value
        XCTAssertTrue(thirdGranted)
    }

    func testAWaitForADictationThatIsNotEnrolledReturnsFalse() async {
        let turns = DictationTurns()
        let granted = await turns.waitForTurn(RecordingID.next())
        XCTAssertFalse(granted)
    }

    func testAwaitUnlessCancelledReturnsTheValueOrNilOnCancellation() async {
        let gate = StartupGate()
        let answered = Task { @MainActor in await awaitUnlessCancelled { await gate.wait() } }
        await waitUntil("the wait parks at the gate") { gate.waitingCount == 1 }
        gate.open(.ready)
        let value = await answered.value
        XCTAssertEqual(value, .ready)

        let neverOpens = StartupGate()
        let cancelled = Task { @MainActor in await awaitUnlessCancelled { await neverOpens.wait() } }
        await waitUntil("the wait parks at the gate") { neverOpens.waitingCount == 1 }
        cancelled.cancel()
        let nothing = await cancelled.value
        XCTAssertNil(nothing)
    }
}

@MainActor
final class DictationPresentationTests: XCTestCase {
    func testTheRevisionGateAdmitsOnlyNewerRevisions() {
        var gate = PresentationRevisionGate()
        XCTAssertTrue(gate.admit(1))
        XCTAssertTrue(gate.admit(3))
        XCTAssertFalse(gate.admit(2))
        XCTAssertFalse(gate.admit(3))
        XCTAssertEqual(gate.lastAdmitted, 3)
    }

    /// A late, older change never undoes a newer one on the pill.
    func testThePillDropsAChangeOlderThanTheOneItShows() {
        let pill = OverlayPanelController()

        XCTAssertTrue(pill.render(.processing, revision: 5))
        XCTAssertFalse(pill.render(.hidden, revision: 4))
        XCTAssertEqual(pill.displayedState, .processing)
        XCTAssertTrue(pill.render(.hidden, revision: 6))
        XCTAssertEqual(pill.displayedState, .hidden)
        XCTAssertEqual(pill.lastRenderedRevision, 6)
    }

    /// The pill can share a full-screen app's space. Whether it actually appears there needs a real Mac.
    func testThePillJoinsFullScreenSpaces() throws {
        let pill = OverlayPanelController()
        pill.render(.processing, revision: 1)
        defer { pill.render(.hidden, revision: 2) }

        let behavior = try XCTUnwrap(pill.panelCollectionBehavior)
        XCTAssertTrue(behavior.contains(.fullScreenAuxiliary))
        XCTAssertTrue(behavior.contains(.canJoinAllSpaces))
    }

    /// The tray keeps only the newest presentation too: a stale one changes neither the pill nor the menu.
    func testTheTrayKeepsOnlyTheNewestPresentation() {
        let pill = OverlayPanelController()
        let tray = TrayPresenter(overlay: pill)
        let item = NSMenuItem(title: "Start Test Dictation", action: nil, keyEquivalent: "")
        let pause = NSMenuItem(title: "Pause Dictation", action: nil, keyEquivalent: "")
        tray.dictationMenuItem = item
        tray.pauseMenuItem = pause

        tray.present(DictationPresentation(revision: 2, overlay: .hidden, isRecording: true, isPaused: true))
        tray.present(DictationPresentation(revision: 1, overlay: .processing, isRecording: false, isPaused: false))

        XCTAssertEqual(item.title, "Stop Test Dictation")
        XCTAssertEqual(pause.state, .on)
        XCTAssertEqual(pill.displayedState, .hidden)
        XCTAssertEqual(pill.lastRenderedRevision, 2)
    }

    /// The live target adapter hands delivery the application and element `TextInjector` confirms focus against.
    func testTheLiveTargetIsWhatTheInjectorCaptured() {
        let injection = InjectionHarness()
        defer { injection.releasePasteboard() }

        let target = LiveDictationTargeting(injector: injection.injector).captureTarget()

        XCTAssertEqual(target.injection?.processIdentifier, InjectionHarness.editorProcess)
        XCTAssertEqual(target.injection?.hasFocusedElement, true)
        XCTAssertEqual(target.bundleIdentifier, InjectionHarness.editorBundle)
    }

    /// Every notice names its stage, differently from every other, without a dash.
    func testEveryNoticeHasItsOwnDashFreeLabel() {
        let labels = OverlayNotice.allCases.map(\.label)
        XCTAssertEqual(Set(labels).count, labels.count)
        for label in labels {
            XCTAssertFalse(label.isEmpty)
            XCTAssertFalse(label.contains("\u{2014}") || label.contains("\u{2013}"), label)
        }
        XCTAssertNotEqual(OverlayNotice.cleanupFellBack.label, OverlayNotice.textKept.label)
        XCTAssertTrue(OverlayNotice.cleanupFellBack.label.contains("raw text"))
    }
}

final class DictationPromptTests: XCTestCase {
    func testTheWritingStyleIsTheProfilesOrTheDefaultWithTheSingleLineContractWhenNeeded() {
        XCTAssertEqual(
            CleanupPrompt.writingStyle(profileStyle: nil, requireSingleLine: false), CleanupPrompt.defaultWritingStyle)
        XCTAssertEqual(
            CleanupPrompt.writingStyle(profileStyle: "   ", requireSingleLine: false),
            CleanupPrompt.defaultWritingStyle)
        XCTAssertEqual(CleanupPrompt.writingStyle(profileStyle: " Be terse. ", requireSingleLine: false), "Be terse.")
        XCTAssertEqual(
            CleanupPrompt.writingStyle(profileStyle: "Be terse.", requireSingleLine: true),
            "Be terse. " + CleanupPrompt.singleLineWritingStyle)
        XCTAssertTrue(
            CleanupPrompt.writingStyle(profileStyle: nil, requireSingleLine: true)
                .hasPrefix(CleanupPrompt.defaultWritingStyle))
        XCTAssertFalse(CleanupPrompt.singleLineWritingStyle.contains("\u{2014}"))
        XCTAssertFalse(CleanupPrompt.singleLineWritingStyle.contains("\u{2013}"))
    }

    func testFlatteningFollowsTheModeAndTheTarget() {
        XCTAssertTrue(AppProfileMatcher.flattensNewlines(.smartFlatten, bundleIdentifier: "com.apple.Terminal"))
        XCTAssertFalse(AppProfileMatcher.flattensNewlines(.smartFlatten, bundleIdentifier: "com.apple.TextEdit"))
        XCTAssertFalse(AppProfileMatcher.flattensNewlines(.smartFlatten, bundleIdentifier: nil))
        XCTAssertTrue(AppProfileMatcher.flattensNewlines(.alwaysFlatten, bundleIdentifier: nil))
        XCTAssertFalse(AppProfileMatcher.flattensNewlines(.keepNewlines, bundleIdentifier: "com.apple.Terminal"))
        XCTAssertEqual(
            AppProfileMatcher.applyNewlineMode(.smartFlatten, to: "a\nb", bundleIdentifier: "com.apple.Terminal"),
            "a b")
    }
}

final class CleanupInvalidationTests: XCTestCase {
    private func snapshot(enabled: Bool = true, deployment: String = "gpt-6") -> CleanupSettingsSnapshot {
        CleanupSettingsSnapshot(
            isEnabled: enabled, providerKind: .microsoftFoundry, foundryLocalModelAlias: "qwen", ollamaModel: "qwen",
            openAIBaseURL: "", openAIModel: "", azureEndpoint: "https://example.openai.azure.com",
            azureDeployment: deployment, azureAuthMode: .azureCli, azureTenantId: "", azureClientId: "",
            secretRevision: "1")
    }

    func testTurningCleanupOffOrChangingItsProviderDropsTheCache() {
        XCTAssertTrue(CleanupInvalidation.shouldInvalidate(from: snapshot(), to: snapshot(enabled: false)))
        XCTAssertTrue(CleanupInvalidation.shouldInvalidate(from: snapshot(), to: snapshot(deployment: "other")))
        XCTAssertFalse(CleanupInvalidation.shouldInvalidate(from: snapshot(enabled: false), to: snapshot()))
        XCTAssertFalse(CleanupInvalidation.shouldInvalidate(from: snapshot(), to: snapshot()))
    }
}

@MainActor
final class StartupNoticeTests: XCTestCase {
    func testOneNoticeForEveryProblemAfterBothStepsSettle() {
        let posted = Collected<DictationNotice>()
        let notices = StartupNotices { posted.values.append($0) }

        notices.report(.inputMonitoringMissing)
        notices.report(.accessibilityMissing)
        notices.settle(.notifications)
        XCTAssertTrue(posted.values.isEmpty, "posted before the storage step settled")
        notices.report(.rulesUnavailable)
        notices.settle(.storage)

        XCTAssertEqual(posted.values.count, 1)
        let notice = posted.values.first
        XCTAssertEqual(notice?.kind, .startup)
        XCTAssertEqual(notice?.settingsPane, .inputMonitoring)
        XCTAssertEqual(notice?.body.contains("Input Monitoring"), true)
        XCTAssertEqual(notice?.body.contains("Accessibility"), true)
        XCTAssertEqual(notice?.body.contains("dictionary rules"), true)

        notices.report(.accessibilityMissing)
        notices.settle(.storage)
        notices.settle(.notifications)
        XCTAssertEqual(posted.values.count, 1, "a second startup notice")
    }

    func testNothingIsPostedWhenNothingWentWrong() {
        let posted = Collected<DictationNotice>()
        let notices = StartupNotices { posted.values.append($0) }
        notices.settle(.storage)
        notices.settle(.notifications)
        XCTAssertTrue(posted.values.isEmpty)
        XCTAssertTrue(notices.hasPosted)
    }

    func testTheNoticeOpensThePaneThatNeedsAChange() {
        XCTAssertEqual(DictationNotice.startup([.accessibilityMissing])?.settingsPane, .accessibility)
        XCTAssertNil(DictationNotice.startup([.rulesUnavailable])?.settingsPane)
        XCTAssertNil(DictationNotice.startup([]))
        for pane in [PrivacyPane.accessibility, .inputMonitoring, .microphone] {
            XCTAssertNotNil(pane.settingsURL, pane.rawValue)
        }
    }

    func testRecoveryTextsAreBoundedAndFoundByNotice() {
        var texts = NotificationRecoveryTexts()
        for index in 0..<(NotificationRecoveryTexts.capacity + 2) {
            texts.remember("text \(index)", for: "notice \(index)")
        }
        XCTAssertNil(texts.text(for: "notice 0"))
        XCTAssertNil(texts.text(for: "notice 1"))
        XCTAssertEqual(texts.text(for: "notice 2"), "text 2")
        XCTAssertEqual(
            texts.text(for: "notice \(NotificationRecoveryTexts.capacity + 1)"),
            "text \(NotificationRecoveryTexts.capacity + 1)")
        texts.removeAll()
        XCTAssertNil(texts.text(for: "notice 2"), "Clear history left a notice's transcript behind")
    }

    /// Every notice about a dictation that did not go in carries its transcript for Copy Transcript; none says
    /// anything with a dash.
    func testDeliveryNoticesCarryTheirTranscript() {
        let notices = [
            DictationNotice.notInserted("words"), .partlyInserted("words"), .mayNotBeInserted("words"),
            .accessibilityNeeded("words"),
        ]
        for notice in notices {
            XCTAssertEqual(notice.recoveryText, "words", notice.kind.rawValue)
            XCTAssertFalse(notice.body.contains("\u{2014}") || notice.title.contains("\u{2014}"))
        }
        XCTAssertEqual(DictationNotice.accessibilityNeeded("words").settingsPane, .accessibility)
    }
}

/// The Recent Dictations submenu is its own delegate's menu, so it fills itself as it opens: the top-level menu's
/// delegate never hears about a submenu.
@MainActor
final class RecentDictationsMenuTests: XCTestCase {
    func testTheSubmenuFillsItselfFromTheRingWhenItOpens() throws {
        let store = LastTranscriptStore()
        let recent = RecentDictationsMenu(store: store)
        let submenu = try XCTUnwrap(recent.item.submenu)
        XCTAssertTrue(submenu.delegate === recent)

        recent.menuNeedsUpdate(submenu)
        XCTAssertEqual(submenu.items.map(\.title), ["No recent dictations"])
        XCTAssertFalse(submenu.items[0].isEnabled)

        store.set("first dictation")
        store.set("second dictation")
        recent.menuNeedsUpdate(submenu)
        XCTAssertEqual(submenu.items.map(\.title), ["second dictation", "first dictation"])
        XCTAssertTrue(submenu.items.allSatisfy { $0.target === recent && $0.action != nil })
        XCTAssertEqual(submenu.items.first?.representedObject as? String, "second dictation")
    }
}
