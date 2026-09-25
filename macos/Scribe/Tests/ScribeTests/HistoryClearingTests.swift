import XCTest

@testable import Scribe

/// A successful Clear history leaves none of the deleted text on screen: the Playground's last report, the Recent
/// Dictations ring, an open Quick Add window, the texts earlier notifications would copy and a pill notice that offers
/// one. A dictation still being processed when the Clear ran is not fenced out: its text was not part of what was
/// deleted.
@MainActor
final class HistoryClearingTests: XCTestCase {
    /// How often each effect that lives outside the stores ran.
    @MainActor
    private final class Effects {
        var notificationTextsForgotten = 0
        var pillRecoveriesWithdrawn = 0
        var menusInvalidated = 0
        var quickAddsClosed = 0
    }

    func testAClearEmptiesThePlaygroundAndClosesQuickAddAndARunningDictationRecordsAfterwards() async throws {
        let harness = makeHarness()
        harness.transcriber.defaultText = "words the user is about to clear"
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.reports.latest?.finalText, "words the user is about to clear")
        XCTAssertEqual(harness.recovery.recent(), ["words the user is about to clear"])

        // A second dictation is still being recognized when the Clear runs.
        let recognizer = DictationGate<String>()
        harness.transcriber.steps = [.gate(recognizer)]
        await harness.dictate()
        await waitUntil("the second recognizer runs") { recognizer.waitingCount == 1 }

        let effects = Effects()
        let clearing = HistoryClearedEffects(
            recovery: harness.recovery,
            reports: harness.reports,
            forgetNotificationTexts: { effects.notificationTextsForgotten += 1 },
            withdrawPillRecovery: {
                effects.pillRecoveriesWithdrawn += 1
                harness.controller.recoveryWasCleared()
            },
            invalidateRecentDictationsMenu: { effects.menusInvalidated += 1 },
            closeQuickAdd: { effects.quickAddsClosed += 1 })
        clearing.apply()

        XCTAssertNil(harness.reports.latest, "the Playground still shows the cleared text")
        XCTAssertTrue(harness.recovery.recent().isEmpty)
        XCTAssertEqual(effects.notificationTextsForgotten, 1)
        XCTAssertEqual(effects.pillRecoveriesWithdrawn, 1)
        XCTAssertEqual(effects.menusInvalidated, 1)
        XCTAssertEqual(effects.quickAddsClosed, 1, "an open Quick Add window keeps showing the cleared text")

        recognizer.open("words spoken before the clear")
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.reports.latest?.finalText, "words spoken before the clear")
        XCTAssertEqual(harness.recovery.recent(), ["words spoken before the clear"])
        XCTAssertEqual(harness.history.records.last?.transcriptText, "words spoken before the clear")
    }
}
