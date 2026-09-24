import AppKit
import XCTest

@testable import Scribe

/// The pieces the dictation lifecycle is built from: ordered turns, the cancellable wait, the notice schedule, the
/// revision gate and the pill, the prompt for a single-line target, cleanup invalidation, the startup notice, recovery
/// texts and the Recent Dictations submenu.
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
        let firstGranted = await bounded("the first's turn") { await turns.waitForTurn(first) }
        XCTAssertEqual(firstGranted, true)
        order.values.append("first")
        turns.finish(first)
        _ = await bounded("the second's turn") { await later.value }

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
        let granted = await bounded("the cancelled wait") { await waiting.value }
        XCTAssertEqual(granted, false)
        XCTAssertEqual(turns.enrolledCount, 3)

        turns.finish(first)
        let thirdWaits = Task { @MainActor in await turns.waitForTurn(third) }
        await waitUntil("the third waits for the cancelled second, which keeps its place") {
            turns.waitingCount == 1
        }
        turns.finish(second)
        let thirdGranted = await bounded("the third's turn") { await thirdWaits.value }
        XCTAssertEqual(thirdGranted, true)
    }

    func testAWaitForADictationThatIsNotEnrolledReturnsFalse() async {
        let turns = DictationTurns()
        let granted = await bounded("the wait") { await turns.waitForTurn(RecordingID.next()) }
        XCTAssertEqual(granted, false)
    }

    func testAwaitUnlessCancelledReturnsTheValueOrNilOnCancellation() async {
        let gate = StartupGate()
        let answered = Task { @MainActor in await awaitUnlessCancelled { await gate.wait() } }
        await waitUntil("the wait parks at the gate") { gate.waitingCount == 1 }
        gate.open(.ready)
        let value = await bounded("the answered wait") { await answered.value }
        XCTAssertEqual(value ?? nil, .ready)

        let neverOpens = StartupGate()
        // The wait's own task stays parked at the gate until it opens; the test opens it at the end.
        addTeardownBlock { await MainActor.run { neverOpens.open(.withoutStoredRules) } }
        let cancelled = Task { @MainActor in await awaitUnlessCancelled { await neverOpens.wait() } }
        await waitUntil("the wait parks at the gate") { neverOpens.waitingCount == 1 }
        cancelled.cancel()
        let nothing = await bounded("the cancelled wait") { await cancelled.value }
        guard let returned = nothing else {
            XCTFail("the cancelled wait never returned")
            return
        }
        XCTAssertNil(returned)
    }
}

/// The notice schedule on its own: one notice at a time, outcomes that wait in arrival order, a newer failure never
/// replaced, and the rejections.
final class DictationNoticeScheduleTests: XCTestCase {
    private var nextID: UInt64 = 0

    private func outcome(
        _ kind: OverlayNotice, source: UInt64? = 1, stage: DictationNoticeStage = .delivery,
        generation: UInt64? = nil
    ) -> DictationOutcomeNotice {
        nextID += 1
        return DictationOutcomeNotice(
            id: nextID, kind: kind, source: source.map { RecordingID(rawValue: $0) }, stage: stage,
            recoveryGeneration: generation)
    }

    private func arrive(
        _ notice: DictationOutcomeNotice, in schedule: inout DictationNoticeSchedule, owned: Bool = false,
        closing: Bool = false, generation: UInt64 = 0
    ) -> DictationNoticeSchedule.Arrival {
        schedule.arrive(notice, pillIsOwned: owned, isClosing: closing, recoveryGeneration: generation)
    }

    func testAFreePillShowsAnOutcomeAndAShownOneIsNeverReplaced() {
        var schedule = DictationNoticeSchedule()
        let failure = outcome(.microphoneAccessNeeded, source: 2, stage: .capture)
        XCTAssertEqual(arrive(failure, in: &schedule), .show)
        schedule.didShow(failure, token: 10)

        let olderKept = outcome(.textKept, source: 1)
        XCTAssertEqual(arrive(olderKept, in: &schedule), .waiting)
        let olderFallback = outcome(.cleanupFellBack, source: 1, stage: .cleanup)
        XCTAssertEqual(arrive(olderFallback, in: &schedule), .notify)
        XCTAssertEqual(schedule.shown?.notice, failure)
        XCTAssertEqual(schedule.waiting, [olderKept])

        XCTAssertFalse(schedule.expire(token: 9), "a stale end took the notice down")
        XCTAssertEqual(schedule.shown?.notice, failure)
        XCTAssertTrue(schedule.expire(token: 10))
        XCTAssertEqual(schedule.takeNext(pillIsOwned: false), olderKept)
        XCTAssertNil(schedule.takeNext(pillIsOwned: false))
    }

    func testARecordingOwnsThePillAndWhatArrivesMeanwhileWaitsInOrder() {
        var schedule = DictationNoticeSchedule()
        let first = outcome(.textKept, source: 1)
        let second = outcome(.microphoneStoppedEarly, source: 2, stage: .capture)
        XCTAssertEqual(arrive(first, in: &schedule, owned: true), .waiting)
        XCTAssertEqual(arrive(second, in: &schedule, owned: true), .waiting)
        XCTAssertEqual(arrive(outcome(.transcriptionFailed, source: 3, stage: .recognition), in: &schedule, owned: true), .notify)

        XCTAssertNil(schedule.takeNext(pillIsOwned: true))
        XCTAssertEqual(schedule.takeNext(pillIsOwned: false), first)
        schedule.didShow(first, token: 4)
        XCTAssertNil(schedule.takeNext(pillIsOwned: false), "two notices at once")
        schedule.yieldToRecording()
        XCTAssertNil(schedule.shown)
        XCTAssertEqual(schedule.takeNext(pillIsOwned: false), second)
    }

    func testFeedbackShowsOnlyOnAFreePillAndRefreshesItself() {
        var schedule = DictationNoticeSchedule()
        let busy = outcome(.stillProcessing, source: nil, stage: .admission)
        XCTAssertEqual(arrive(busy, in: &schedule), .show)
        schedule.didShow(busy, token: 1)
        let again = outcome(.stillProcessing, source: nil, stage: .admission)
        XCTAssertEqual(arrive(again, in: &schedule), .show, "a second refusal did not refresh the notice")
        schedule.didShow(again, token: 2)

        let kept = outcome(.textKept, source: 7)
        XCTAssertEqual(arrive(kept, in: &schedule), .show, "feedback held off an outcome")
        schedule.didShow(kept, token: 3)
        XCTAssertEqual(
            arrive(outcome(.stillProcessing, source: nil, stage: .admission), in: &schedule), .rejected(.pillBusy))
        XCTAssertEqual(schedule.waiting, [], "feedback was kept for later")
    }

    func testDuplicatesWorkAfterShutdownAndClearedTextAreRejected() {
        var schedule = DictationNoticeSchedule()
        let kept = outcome(.textKept, source: 5, generation: 3)
        XCTAssertEqual(arrive(kept, in: &schedule, owned: true, generation: 3), .waiting)
        XCTAssertEqual(arrive(outcome(.textKept, source: 5, generation: 3), in: &schedule, owned: true, generation: 3),
            .rejected(.duplicate))
        XCTAssertEqual(arrive(kept, in: &schedule, owned: true, generation: 3), .rejected(.duplicate))
        XCTAssertEqual(arrive(outcome(.textKept, source: 6, generation: 2), in: &schedule, generation: 3),
            .rejected(.cleared))
        XCTAssertEqual(arrive(outcome(.recognizerMissing, source: 9), in: &schedule, closing: true),
            .rejected(.closing))
        schedule.close()
        XCTAssertNil(schedule.shown)
        XCTAssertTrue(schedule.waiting.isEmpty)
    }

    func testClearHistoryRemovesTheNoticesThatOfferClearedText() {
        var schedule = DictationNoticeSchedule()
        let shownKept = outcome(.partlyInserted, source: 1, generation: 0)
        XCTAssertEqual(arrive(shownKept, in: &schedule), .show)
        schedule.didShow(shownKept, token: 1)
        let waitingKept = outcome(.textKept, source: 2, generation: 0)
        let waitingOther = outcome(.recognizerMissing, source: 3, stage: .recognition)
        XCTAssertEqual(arrive(waitingKept, in: &schedule), .waiting)
        XCTAssertEqual(arrive(waitingOther, in: &schedule), .waiting)

        XCTAssertTrue(schedule.recoveryCleared(current: 1))
        XCTAssertNil(schedule.shown)
        XCTAssertEqual(schedule.waiting, [waitingOther])
        XCTAssertFalse(schedule.recoveryCleared(current: 1))
    }

    func testEveryNoticeHasARoleAndOnlyTheSilentFallbacksNotifyInstead() {
        XCTAssertEqual(OverlayNotice.stillProcessing.role, .feedback)
        XCTAssertEqual(
            OverlayNotice.allCases.filter(\.notifiesWhenThePillIsBusy), [.cleanupFellBack, .transcriptionFailed])
        for notice in OverlayNotice.allCases where notice.notifiesWhenThePillIsBusy {
            XCTAssertEqual(notice.role, .informational)
        }
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
            texts.remember("text \(index)", generation: 0, for: "notice \(index)")
        }
        XCTAssertNil(texts.text(for: "notice 0", currentGeneration: 0))
        XCTAssertNil(texts.text(for: "notice 1", currentGeneration: 0))
        XCTAssertEqual(texts.text(for: "notice 2", currentGeneration: 0), "text 2")
        XCTAssertEqual(
            texts.text(for: "notice \(NotificationRecoveryTexts.capacity + 1)", currentGeneration: 0),
            "text \(NotificationRecoveryTexts.capacity + 1)")
        texts.removeAll()
        XCTAssertNil(texts.text(for: "notice 2", currentGeneration: 0), "Clear history left a notice's transcript behind")
    }

    /// A notice's Copy Transcript copies nothing once Clear history has started another recovery generation, even
    /// when the text is still remembered.
    func testACopyTranscriptAfterClearHistoryFindsNothing() {
        var texts = NotificationRecoveryTexts()
        texts.remember("kept before the Clear", generation: 4, for: "notice")
        XCTAssertEqual(texts.text(for: "notice", currentGeneration: 4), "kept before the Clear")
        XCTAssertNil(texts.text(for: "notice", currentGeneration: 5))
    }

    /// Every notice about a dictation that did not go in carries its transcript and the recovery generation it was
    /// kept in, for Copy Transcript; none says anything with a dash, and neither do the fallbacks for the pill.
    func testDeliveryNoticesCarryTheirTranscriptAndGeneration() {
        let notices = [
            DictationNotice.notInserted("words", recoveryGeneration: 2),
            .partlyInserted("words", recoveryGeneration: 2), .mayNotBeInserted("words", recoveryGeneration: 2),
            .accessibilityNeeded("words", recoveryGeneration: 2),
        ]
        for notice in notices {
            XCTAssertEqual(notice.recoveryText, "words", notice.kind.rawValue)
            XCTAssertEqual(notice.recoveryGeneration, 2, notice.kind.rawValue)
        }
        for notice in notices + [.cleanupFellBack, .transcriptionFailed] {
            XCTAssertFalse(notice.body.contains("\u{2014}") || notice.title.contains("\u{2014}"), notice.kind.rawValue)
            XCTAssertFalse(notice.body.contains("\u{2013}") || notice.title.contains("\u{2013}"), notice.kind.rawValue)
        }
        XCTAssertNil(DictationNotice.cleanupFellBack.recoveryText)
        XCTAssertNil(DictationNotice.transcriptionFailed.recoveryText)
        XCTAssertEqual(DictationNotice.accessibilityNeeded("words", recoveryGeneration: 2).settingsPane, .accessibility)
    }
}

/// The Recent Dictations submenu is its own delegate's menu, so it fills itself as it opens: the top-level menu's
/// delegate never hears about a submenu. Its entries copy nothing once Clear history has run.
@MainActor
final class RecentDictationsMenuTests: XCTestCase {
    /// A private pasteboard; the test releases it when it ends.
    private func makePasteboard() -> NSPasteboard {
        NSPasteboard(name: NSPasteboard.Name("com.scribe.tests.recent.\(UUID().uuidString)"))
    }

    private func choose(_ item: NSMenuItem) {
        guard let action = item.action, let target = item.target as? NSObject else {
            XCTFail("the entry has no action")
            return
        }
        _ = target.perform(action, with: item)
    }

    func testTheSubmenuFillsItselfFromTheRingWhenItOpens() throws {
        let store = LastTranscriptStore()
        let pasteboard = makePasteboard()
        defer { pasteboard.releaseGlobally() }
        let recent = RecentDictationsMenu(store: store, pasteboard: pasteboard)
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

        choose(submenu.items[0])
        XCTAssertEqual(pasteboard.string(forType: .string), "second dictation")
    }

    /// The submenu is open when Clear history runs: an entry chosen from it afterwards copies nothing, and the
    /// submenu is refilled at once so the deleted text is no longer on show.
    func testAnEntryShownBeforeClearHistoryCopiesNothingAfterIt() throws {
        let store = LastTranscriptStore()
        let pasteboard = makePasteboard()
        defer { pasteboard.releaseGlobally() }
        let recent = RecentDictationsMenu(store: store, pasteboard: pasteboard)
        let submenu = try XCTUnwrap(recent.item.submenu)
        store.set("text the user deletes")
        recent.menuNeedsUpdate(submenu)
        let stale = submenu.items[0]
        pasteboard.clearContents()
        pasteboard.setString("the user's own clipboard", forType: .string)

        store.removeAll()
        choose(stale)
        XCTAssertEqual(pasteboard.string(forType: .string), "the user's own clipboard", "cleared text was copied")

        recent.invalidate()
        XCTAssertEqual(submenu.items.map(\.title), ["No recent dictations"])

        store.set("dictated after the Clear")
        recent.menuNeedsUpdate(submenu)
        choose(submenu.items[0])
        XCTAssertEqual(pasteboard.string(forType: .string), "dictated after the Clear")
    }
}

/// The test support's own guarantees: a gate settled at teardown stays settled, so a waiter that arrives afterwards goes
/// on instead of parking for good, a gate the test opened keeps its value, and a closed clock refuses every later sleep.
@MainActor
final class DictationTestSupportTests: XCTestCase {
    func testAGateSettledAtTeardownLetsALaterWaiterGoOn() async {
        let left = DictationGate<Int>()
        let opened = DictationGate<Int>()
        opened.open(3)
        HeldGates.releaseAll()

        let refused = await bounded("a wait that arrives after teardown", within: 5) { () -> Bool in
            do {
                _ = try await left.wait()
                return false
            } catch {
                return error is CancellationError
            }
        }
        XCTAssertEqual(refused, true)
        let kept = await bounded("a wait on a gate the test opened", within: 5) { () -> Int? in try? await opened.wait() }
        XCTAssertEqual(kept ?? nil, 3)
    }

    func testAClosedClockRefusesEveryLaterSleep() async {
        let clock = ManualDictationClock()
        clock.closeForTeardown()

        let refused = await bounded("a sleep on a closed clock", within: 5) { () -> Bool in
            do {
                try await clock.sleep(until: clock.now.advanced(by: .seconds(1)))
                return false
            } catch {
                return error is CancellationError
            }
        }
        XCTAssertEqual(refused, true)
    }
}
