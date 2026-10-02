import Foundation
import XCTest

@testable import Scribe

@MainActor
final class SettingsSessionTests: XCTestCase {
    func testEditingDoesNotWriteAndCancelDiscardsEveryPage() {
        let state = SessionCommitRecorder()
        let session = makeSession(state: state)
        session.edit {
            $0.preferences.addSpaceAfterDictation = false
            $0.historyRetentionValue = "7"
        }
        XCTAssertEqual(session.dirtyPages, [.dictation, .history])
        XCTAssertEqual(session.footerText, "Unsaved changes: Dictation, History")
        XCTAssertEqual(state.commits, 0)
        XCTAssertTrue(session.cancel())
        XCTAssertEqual(session.footerText, "All changes saved")
        XCTAssertEqual(state.commits, 0)
    }

    func testKeepEditingNeverSavesAndCredentialRemovalIsStaged() async {
        let state = SessionCommitRecorder()
        let session = makeSession(state: state)
        session.editCredential(
            SettingsCredentialID(slot: .customApiKey, account: "default"), .remove)
        XCTAssertEqual(session.dirtyPages, [.aiCleanup])
        let closed = await session.resolveClose(.keepEditing)
        XCTAssertFalse(closed)
        XCTAssertEqual(state.commits, 0)
        XCTAssertTrue(session.cancel())
        XCTAssertTrue(session.credentialEdits.isEmpty)
    }

    func testPlaceholdersAndExplicitMissingDefaultsDoNotDirtyTheFooter() {
        let state = SessionCommitRecorder()
        var initial = SettingsDocument()
        initial.dictionary = []
        let session = SettingsSession(
            initial: initial,
            access: SettingsSessionAccess(
                commit: { _ in throw SettingsSaveFailure.storage },
                apply: { _ in .applied }))
        session.edit {
            $0.preferences.addSpaceAfterDictation = true
            $0.dictionary = [SettingsDictionaryRow(DictionaryEntry(id: -1, pattern: "", replacement: ""))]
        }
        XCTAssertEqual(session.footerText, "All changes saved")
        XCTAssertEqual(state.commits, 0)
    }

    func testFailedSaveKeepsDraftBaselineAndNeverApplies() async {
        let state = SessionCommitRecorder()
        state.failure = .storage
        let session = makeSession(state: state)
        session.edit { $0.preferences.aiCleanupEnabled = true }
        let result = await session.save()
        XCTAssertEqual(result, .notCommitted(.storage))
        XCTAssertFalse(session.baseline.preferences.aiCleanupEnabled)
        XCTAssertTrue(session.draft.preferences.aiCleanupEnabled)
        XCTAssertEqual(state.applies, 0)
    }

    func testEditsDuringSaveRemainUnsavedAndPreventClose() async {
        let gate = SettingsTestGate()
        let state = SessionCommitRecorder()
        let session = makeSession(state: state) {
            await gate.pass()
            return $0
        }
        session.edit { $0.preferences.addSpaceAfterDictation = false }
        let task = Task { await session.saveAndClose() }
        await gate.waitForArrival()
        session.edit { $0.preferences.addSpaceAfterDictation = true }
        await gate.open()
        let closed = await task.value
        XCTAssertFalse(closed)
        XCTAssertFalse(session.baseline.preferences.addSpaceAfterDictation)
        XCTAssertTrue(session.draft.preferences.addSpaceAfterDictation)
        XCTAssertTrue(session.hasUnsavedChanges)
    }

    func testSaveAndCloseRequiresPublicationAndRetriesWithoutAnotherCommit() async {
        let state = SessionCommitRecorder()
        state.application = .notApplied
        let session = makeSession(state: state)
        session.edit { $0.preferences.aiCleanupEnabled = true }
        let first = await session.saveAndClose()
        XCTAssertFalse(first)
        XCTAssertEqual(state.commits, 1)
        state.application = .applied
        let second = await session.saveAndClose()
        XCTAssertTrue(second)
        XCTAssertEqual(state.commits, 1)
        XCTAssertEqual(state.applies, 2)
    }

    func testLateLoadedRowsAreNotEditsAndCannotOverwriteShownRows() {
        let state = SessionCommitRecorder()
        let session = makeSession(state: state)
        var loaded = SettingsDocument()
        loaded.dictionary = [SettingsDictionaryRow(DictionaryEntry(id: 1, pattern: "one", replacement: "One"))]
        session.adoptLoadedRows(from: loaded)
        XCTAssertFalse(session.hasUnsavedChanges)
        session.edit { $0.dictionary?[0].replacement = "Changed" }
        session.adoptLoadedRows(from: loaded)
        XCTAssertEqual(session.draft.dictionary?.first?.replacement, "Changed")
    }

    func testExternalCompletionCannotUndoNewerWindowIntent() {
        let state = SessionCommitRecorder()
        let session = makeSession(state: state)
        let tray = SettingsIntentRevision.next()
        session.adoptExternal(
            .aiCleanup, values: ["ScribeAiCleanupEnabled": .bool(true)], revision: tray)
        session.edit { $0.preferences.aiCleanupEnabled = false }
        session.adoptExternal(
            .aiCleanup, values: ["ScribeAiCleanupEnabled": .bool(true)], revision: tray)
        XCTAssertFalse(session.draft.preferences.aiCleanupEnabled)
        XCTAssertTrue(session.hasUnsavedChanges)
    }

    func testExternalSettingsHaveIndependentRevisions() {
        let state = SessionCommitRecorder()
        let session = makeSession(state: state)
        let earlier = SettingsIntentRevision.next()
        session.edit { $0.preferences.aiCleanupEnabled = true }
        session.adoptExternal(.overlayAnchor, values: ["ScribeOverlayAnchor": .string("topLeft")], revision: earlier)
        XCTAssertEqual(session.draft.preferences.overlayAnchor, "topLeft")
        XCTAssertTrue(session.draft.preferences.aiCleanupEnabled)
    }

    func testAnOutsideChangeDeferredDuringCaptureIsNotAnUnsavedEdit() {
        let state = SessionCommitRecorder()
        let session = makeSession(state: state)
        session.adoptExternal(
            .aiCleanup,
            values: ["ScribeAiCleanupEnabled": .bool(true)],
            revision: SettingsIntentRevision.next(),
            canShowNow: false)
        XCTAssertFalse(session.hasUnsavedChanges)
        session.releaseExternalChanges()
        XCTAssertTrue(session.draft.preferences.aiCleanupEnabled)
        XCTAssertFalse(session.hasUnsavedChanges)
    }

    func testReleasingDeferredOutsideChoicesNeverDiscardsATypedCredential() {
        let state = SessionCommitRecorder()
        let session = makeSession(state: state)
        let id = SettingsCredentialID(slot: .customApiKey, account: "default")
        session.editCredential(id, .replace("typed-but-unsaved"))
        session.adoptExternal(
            .overlayAnchor,
            values: ["ScribeOverlayAnchor": .string("topLeft")],
            revision: SettingsIntentRevision.next(),
            canShowNow: false)
        session.releaseExternalChanges()
        XCTAssertEqual(session.credentialEdits[id], .replace("typed-but-unsaved"))
        XCTAssertEqual(session.dirtyPages, [.aiCleanup])
    }

    func testStoredOnlyReapplyAfterFailedSaveCannotSendTheEditingProviderLive() async {
        let state = SessionCommitRecorder()
        state.failure = .storage
        let session = makeSession(state: state)
        session.edit { $0.preferences["ScribeCleanupOpenAIBaseURL"] = .string("https://unsaved.example/v1") }
        _ = await session.save()
        let reapply = SettingsStoredReapply(
            load: { session.baseline },
            publish: {
                XCTAssertNil($0.preferences["ScribeCleanupOpenAIBaseURL"])
                state.applies += 1
                return .applied
            })
        let result = await reapply.run()
        XCTAssertEqual(result, .applied)
        XCTAssertEqual(state.applies, 1)
        XCTAssertTrue(session.hasUnsavedChanges)
    }

    func testUncertainCommitIsRecoveredWithoutAnotherWriteOrDiscardingPreparedCredentials() async {
        let state = SessionCommitRecorder()
        let holder = UncertainReceiptHolder()
        let session = SettingsSession(
            initial: SettingsDocument(),
            access: SettingsSessionAccess(
                commit: {
                    state.commits += 1
                    holder.receipt = SettingsCommitReceipt(id: $0.id, revision: $0.revision, document: $0.document)
                    throw SettingsCommitUncertain(id: $0.id)
                },
                recover: { _ in holder.receipt },
                apply: { _ in .applied },
                discardPreparation: { _ in
                    XCTFail("Credentials were discarded before the commit outcome was known")
                }))
        session.edit { $0.preferences.addSpaceAfterDictation = false }
        let unknown = await session.save()
        if case .outcomeUnknown = unknown {
            XCTAssertTrue(session.hasUnresolvedCommit)
        } else {
            XCTFail("An uncertain write was reported as a pre-commit failure")
        }
        XCTAssertFalse(session.cancel())
        let recovered = await session.saveAndClose()
        XCTAssertTrue(recovered)
        XCTAssertEqual(state.commits, 1)
        XCTAssertFalse(session.hasUnresolvedCommit)
    }

    private func makeSession(
        state: SessionCommitRecorder,
        prepare: @escaping @MainActor (SettingsSubmission) async throws -> SettingsSubmission = { $0 }
    ) -> SettingsSession {
        SettingsSession(
            initial: SettingsDocument(),
            access: SettingsSessionAccess(
                prepare: prepare,
                commit: {
                    state.commits += 1
                    if let failure = state.failure { throw failure }
                    return SettingsCommitReceipt(id: $0.id, revision: $0.revision, document: $0.document)
                },
                apply: { _ in
                    state.applies += 1
                    return state.application
                }))
    }
}

@MainActor
private final class SessionCommitRecorder {
    var commits = 0
    var applies = 0
    var failure: SettingsSaveFailure?
    var application = SettingsApplicationOutcome.applied
}

@MainActor
private final class UncertainReceiptHolder {
    var receipt: SettingsCommitReceipt?
}
