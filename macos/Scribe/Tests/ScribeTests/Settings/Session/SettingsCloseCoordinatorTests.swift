import XCTest

@testable import Scribe

@MainActor
final class SettingsCloseCoordinatorTests: XCTestCase {
    func testKeepEditingDoesNotBeginQuitOrCommit() async {
        let count = SettingsTestCounter()
        let session = SettingsSession(
            initial: SettingsDocument(),
            access: SettingsSessionAccess(
                commit: { _ in
                    XCTFail("Keep editing committed the draft")
                    throw SettingsSaveFailure.storage
                },
                apply: { _ in .applied }))
        session.edit { $0.preferences.addSpaceAfterDictation = false }
        let guarder = SettingsCloseCoordinator(
            session: session,
            prompt: {
                XCTAssertEqual($0.title, "Save changes before quitting?")
                XCTAssertEqual($0.defaultChoice, .keepEditing)
                return .keepEditing
            },
            proceed: { _ in count.increment() })
        let allowed = await guarder.request(.quit)
        XCTAssertFalse(allowed)
        XCTAssertEqual(count.count, 0)
        XCTAssertTrue(session.hasUnsavedChanges)
    }

    func testDiscardSettlesSettingsBeforeRestartAndCannotUndoAnImmediateCommand() async {
        let count = SettingsTestCounter()
        let session = SettingsSession(
            initial: SettingsDocument(),
            access: SettingsSessionAccess(
                commit: { _ in throw SettingsSaveFailure.storage },
                apply: { _ in .applied }))
        session.edit { $0.preferences.aiCleanupEnabled = true }
        count.increment()
        let guarder = SettingsCloseCoordinator(
            session: session, prompt: { _ in .discard },
            proceed: { _ in
                XCTAssertFalse(session.hasUnsavedChanges)
                count.increment()
            })
        let allowed = await guarder.request(.restart)
        XCTAssertTrue(allowed)
        XCTAssertEqual(count.count, 2)
    }

    func testRepeatedCloseWhilePromptIsHeldDoesNotPresentOrProceedTwice() async {
        let gate = SettingsTestGate()
        let count = SettingsTestCounter()
        let session = SettingsSession(
            initial: SettingsDocument(),
            access: SettingsSessionAccess(
                commit: { _ in throw SettingsSaveFailure.storage },
                apply: { _ in .applied }))
        session.edit { $0.preferences.aiCleanupEnabled = true }
        let guarder = SettingsCloseCoordinator(
            session: session,
            prompt: { _ in
                await gate.pass()
                return .discard
            },
            proceed: { _ in count.increment() })
        let first = Task { await guarder.request(.close) }
        await gate.waitForArrival()
        let second = await guarder.request(.quit)
        XCTAssertFalse(second)
        await gate.open()
        let firstAllowed = await first.value
        XCTAssertTrue(firstAllowed)
        XCTAssertEqual(count.count, 1)
    }
}
