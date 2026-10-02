import XCTest

@testable import Scribe

final class CleanupSendGateTests: XCTestCase {
    private func recipient(model: String = "model") throws -> CleanupRecipient {
        let fixture = makeCleanupStore()
        var settings = fixture.store.snapshot()
        settings.foundryLocalModelAlias = model
        return CleanupRecipient(
            connection: try CleanupProviderResolver.connection(settings: settings, environment: [:]),
            settings: settings)
    }

    func testRevocationAndRestorationAtTheSameGenerationAreCheckedByContent() throws {
        let gate = CleanupSendGate()
        let recipient = try recipient()
        let scope = AiVocabularyScope(generation: 1, permittedContent: ["pack": "accepted"])
        gate.publishRecipient(recipient)
        gate.publishVocabulary(scope)
        let receipt = gate.receipt(scope: scope, recipient: recipient, kind: .dictation)
        var sends = 0
        try receipt.start { sends += 1 }
        gate.publishVocabulary(.none)
        XCTAssertThrowsError(try receipt.start { sends += 1 }) {
            XCTAssertEqual($0 as? CleanupHoldback, .vocabularyChanged)
        }
        gate.publishVocabulary(AiVocabularyScope(generation: 1, permittedContent: ["pack": "other"]))
        XCTAssertThrowsError(try receipt.start { sends += 1 })
        gate.publishVocabulary(scope)
        try receipt.start { sends += 1 }
        XCTAssertEqual(sends, 2)
    }

    func testAQueuedAttemptCannotUseAnotherRecipient() throws {
        let gate = CleanupSendGate()
        let first = try recipient()
        gate.publishRecipient(first)
        let receipt = gate.receipt(scope: .none, recipient: first, kind: .auxiliary)
        gate.publishRecipient(try recipient(model: "other"))
        XCTAssertThrowsError(try receipt.start { XCTFail("A changed recipient must not send") }) {
            XCTAssertEqual($0 as? CleanupHoldback, .recipientChanged)
        }
    }

    func testARevocationDuringTheResponseDoesNotWaitForTheResponse() async throws {
        let gate = CleanupSendGate()
        let recipient = try recipient()
        gate.publishRecipient(recipient)
        let receipt = gate.receipt(scope: .none, recipient: recipient, kind: .probe)
        let response = SettingsTestGate()
        let started = SettingsTestGate()
        let work = Task {
            try receipt.start {}
            await started.open()
            await response.pass()
        }
        await started.pass()
        gate.close()
        XCTAssertThrowsError(try receipt.check()) {
            XCTAssertEqual($0 as? CleanupHoldback, .closed)
        }
        await response.open()
        try await work.value
    }

    func testAProbeHasNoVocabularyAndEveryRetryKeepsItsReceipt() throws {
        let gate = CleanupSendGate()
        let recipient = try recipient()
        gate.publishRecipient(recipient)
        let receipt = gate.receipt(scope: .none, recipient: recipient, kind: .probe)
        let request = CleanupRequest(transcript: "ok", maxOutputTokens: 16, receipt: receipt)
        XCTAssertEqual(request.withoutOutputLimit().receipt?.scope, AiVocabularyScope.none)
        gate.close()
        XCTAssertThrowsError(try request.withoutOutputLimit().receipt?.check())
    }

    func testBuiltInContentWithoutAnEditsHashStillRequiresItsPermission() {
        let admitted = AiVocabularyScope(generation: 1, permittedContent: ["built-in": nil])
        XCTAssertTrue(admitted.covers(admitted))
        XCTAssertFalse(AiVocabularyScope.none.covers(admitted))
    }

    func testAnUncertainLogicalCommitWithholdsRequestsUntilRecoveryPublishes() throws {
        let gate = CleanupSendGate()
        let first = try recipient()
        gate.publishRecipient(first)
        let receipt = gate.receipt(scope: .none, recipient: first, kind: .dictation)
        XCTAssertThrowsError(
            try gate.committing(
                vocabulary: AiVocabularyScope(generation: 1, permittedContent: ["pack": "new"]),
                recipient: try recipient(model: "other")
            ) {
                throw CleanupHoldback.closed
            })
        XCTAssertEqual(gate.currentVocabularyScope, .none)
        XCTAssertThrowsError(try receipt.check())
        gate.publishRecipient(first)
        XCTAssertThrowsError(try receipt.check())
        gate.publish(vocabulary: .none, recipient: first)
        XCTAssertNoThrow(try receipt.check())
    }

    func testALateCatalogReadCannotPutBackPermissionAfterACommit() {
        let gate = CleanupSendGate()
        let oldScope = AiVocabularyScope(generation: 1, permittedContent: ["pack": "old"])
        gate.publishVocabulary(oldScope)
        let reading = gate.vocabularyRevision
        gate.publishVocabulary(.none)
        XCTAssertFalse(gate.publishReadVocabulary(oldScope, after: reading))
        XCTAssertEqual(gate.currentVocabularyScope, .none)
        let newRead = gate.vocabularyRevision
        XCTAssertTrue(gate.publishReadVocabulary(oldScope, after: newRead))
        XCTAssertEqual(gate.currentVocabularyScope, oldScope)
    }

    func testLegacyWriteAllowsReentrantNotificationButNoSendOrStaleCatalogPublication() throws {
        let gate = CleanupSendGate()
        let recipient = try recipient()
        let scope = AiVocabularyScope(generation: 1, permittedContent: ["pack": "accepted"])
        gate.publish(vocabulary: scope, recipient: recipient)
        let admitted = gate.receipt(scope: scope, recipient: recipient, kind: .dictation)
        var duringWrite: UInt64 = 0
        gate.changingVocabulary {
            gate.publishRecipient(recipient)
            duringWrite = gate.vocabularyRevision
            XCTAssertFalse(gate.publishReadVocabulary(scope, after: duringWrite))
            XCTAssertThrowsError(try admitted.check())
        }
        XCTAssertFalse(gate.publishReadVocabulary(scope, after: duringWrite))
        XCTAssertThrowsError(try admitted.check())
        XCTAssertTrue(gate.publishReadVocabulary(scope, after: gate.vocabularyRevision))
        XCTAssertNoThrow(try admitted.check())
    }
}
