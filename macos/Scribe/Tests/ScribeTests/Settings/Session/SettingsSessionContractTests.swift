import XCTest

@testable import Scribe

final class SettingsSessionContractTests: XCTestCase {
    func testPageOrderAndTitlesMatchWindows() {
        XCTAssertEqual(
            SettingsSessionPage.allCases.map(\.title),
            [
                "Dictation", "Try dictation", "AI cleanup", "Dictionary", "Voice snippets", "App profiles",
                "History", "Usage", "Advanced", "Diagnostics", "About",
            ])
    }

    func testMissingPreferencesPreserveLegacyDefaults() {
        let preferences = SettingsPreferences()
        XCTAssertFalse(preferences.aiCleanupEnabled)
        XCTAssertTrue(preferences.addSpaceAfterDictation)
        XCTAssertEqual(preferences.shortcutKeyCode, 57)
        XCTAssertNil(preferences.overlayAnchor)
        XCTAssertTrue(preferences.values.isEmpty)
    }

    func testImmediateCommandTableIsExhaustiveAndPrivacyEditsStayStaged() {
        XCTAssertEqual(Set(SettingsCommandPolicy.dispositions.keys), Set(SettingsCommand.allCases))
        for command in [
            SettingsCommand.removeCredential, .deleteWordPackPermanently, .learnFromHistory, .restoreDefaultShortcuts,
        ] {
            if case .staged? = SettingsCommandPolicy.dispositions[command] {
                continue
            }
            XCTFail("A draft operation was classified as immediate")
        }
    }

    func testEveryDirtyPageUsesCloseGuardAndKeepEditingIsDefault() {
        for _ in SettingsSessionPage.allCases {
            for trigger in [SettingsCloseTrigger.close, .quit, .restart] {
                let decision = SettingsCloseGuard.decide(dirty: true, trigger: trigger)
                XCTAssertTrue(decision.ask)
                XCTAssertEqual(decision.defaultChoice, .keepEditing)
            }
        }
        XCTAssertFalse(SettingsCloseGuard.decide(dirty: true, trigger: .systemTermination).ask)
        XCTAssertFalse(SettingsCloseGuard.decide(dirty: false, trigger: .close).ask)
        XCTAssertTrue(SettingsCloseGuard.decide(dirty: false, saving: true, trigger: .close).ask)
    }

    func testEscapeNeverClosesTheMainWindow() {
        XCTAssertEqual(SettingsCloseGuard.escape(SettingsKeyboardOwnership()), .none)
        XCTAssertEqual(
            SettingsCloseGuard.escape(SettingsKeyboardOwnership(shortcutCapture: true, composing: true)),
            .cancelCapture)
        XCTAssertFalse(SettingsCloseGuard.canRunCommand(SettingsKeyboardOwnership(composing: true)))
    }

    func testExternalChangeDuringCaptureIsIncludedInSaveThenShownOnce() {
        var sync = SettingsExternalSwitch()
        XCTAssertTrue(sync.adopt(true, revision: SettingsIntentRevision.next(), canShowNow: false))
        XCTAssertTrue(sync.forSave(false))
        XCTAssertEqual(sync.release(), true)
        XCTAssertNil(sync.release())
    }

    func testDelayedTrayCompletionCannotUndoANewerWindowClick() {
        var sync = SettingsExternalSwitch()
        let tray = SettingsIntentRevision.next()
        XCTAssertTrue(sync.adopt(true, revision: tray, canShowNow: true))
        sync.userChanged()
        XCTAssertFalse(sync.adopt(true, revision: tray, canShowNow: true))
        XCTAssertFalse(sync.forSave(false))
    }

    func testSaveOnlyClearsIntentThroughItsSubmission() {
        var sync = SettingsExternalSwitch()
        sync.userChanged()
        let submitted = sync.newestRevision
        sync.userChanged()
        sync.savedThrough(submitted)
        XCTAssertGreaterThan(sync.newestRevision, submitted)
        sync.saved()
        XCTAssertEqual(sync.newestRevision, 0)
        XCTAssertTrue(sync.adopt(true, revision: submitted, canShowNow: true))
    }

    func testLaterOutsideChangeReplacesAWaitingOneAndOlderCompletionIsIgnored() {
        var sync = SettingsExternalSwitch()
        let older = SettingsIntentRevision.next()
        let newer = SettingsIntentRevision.next()
        XCTAssertTrue(sync.adopt(true, revision: older, canShowNow: false))
        XCTAssertTrue(sync.adopt(false, revision: newer, canShowNow: true))
        XCTAssertNil(sync.release())
        XCTAssertFalse(sync.adopt(true, revision: older, canShowNow: true))
        XCTAssertFalse(sync.forSave(false))
    }
}
