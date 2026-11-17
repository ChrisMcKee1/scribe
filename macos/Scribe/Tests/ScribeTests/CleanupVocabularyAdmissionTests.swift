import XCTest

@testable import Scribe

/// The word pack permission a cleanup request is held to as it leaves (`AiVocabularyGate`,
/// `CleanupVocabularyAdmission`, `CleanupSendHandoff.data`). Every test builds its own gate, so none touches the one
/// the app shares.
final class CleanupVocabularyAdmissionTests: XCTestCase {
    private func scope(_ generation: Int64, _ permitted: [String: String?]) -> AiVocabularyScope {
        AiVocabularyScope(generation: generation, permittedContent: permitted)
    }

    /// An Entra credential that waits for the test before it answers, as one that has to ask `az` or the tenant does.
    private final class SuspendedCredential: AzureCredentialProvider {
        let reached = LifecycleGate()
        let release = LifecycleGate()

        func accessToken(scope: String) async throws -> AzureAccessToken {
            reached.open()
            try await release.wait()
            return AzureAccessToken(token: "entra-token", expiresAt: .distantFuture)
        }
    }

    private func makeProvider(
        credential: any AzureCredentialProvider, _ handler: @escaping StubURLProtocol.Handler
    ) -> MicrosoftFoundryCleanupProvider {
        MicrosoftFoundryCleanupProvider(
            inferenceBase: URL(string: "https://my-res.services.ai.azure.com/openai/v1/")!,
            deployment: "gpt-5-mini",
            promptCachingEnabled: { true },
            credential: credential,
            session: makeStubSession(handler))
    }

    // MARK: - The gate

    func testTheGateCoversAnAdmissionOnlyWhileEveryPackItCarriedIsPermittedForTheSameContent() {
        let gate = AiVocabularyGate()
        gate.publish(scope(2, ["a": "h1", "b": "h2"]))
        let admitted = scope(2, ["a": "h1", "b": "h2"])
        XCTAssertTrue(gate.covers(admitted))

        // A pack permitted that the admission never carried changes nothing for it.
        gate.publish(scope(3, ["a": "h1", "b": "h2", "c": "h3"]))
        XCTAssertTrue(gate.covers(admitted))

        // The same pack with other content is not the content the permission was given for.
        gate.publish(scope(4, ["a": "h1", "b": "other"]))
        XCTAssertFalse(gate.covers(admitted))

        // A pack no longer permitted.
        gate.publish(scope(5, ["a": "h1"]))
        XCTAssertFalse(gate.covers(admitted))
        XCTAssertTrue(gate.covers(scope(2, ["a": "h1"])))
    }

    func testANarrowingWithdrawsAtOnceAndAnAbandonedSavePutsThePermissionBack() {
        let gate = AiVocabularyGate()
        gate.publish(scope(2, ["a": "h1", "b": "h2"]))
        let admitted = scope(2, ["a": "h1", "b": "h2"])

        let narrowing = gate.narrow { library, _ in library == "a" }

        XCTAssertFalse(gate.covers(admitted), "b was still sent after its permission was withdrawn")
        XCTAssertTrue(gate.covers(scope(2, ["a": "h1"])))
        gate.abandon(narrowing)
        XCTAssertTrue(gate.covers(admitted))
    }

    /// A hold judges each pack by the content its permission was published for, so a pack whose content a save changes
    /// is withdrawn although its id stays.
    func testAHoldJudgesAPackByTheContentItsPermissionWasPublishedFor() {
        let gate = AiVocabularyGate()
        gate.publish(scope(2, ["a": "h1", "b": "h2"]))

        gate.narrow { _, content in content == "h1" }

        XCTAssertTrue(gate.covers(scope(2, ["a": "h1"])))
        XCTAssertFalse(gate.covers(scope(2, ["b": "h2"])))
    }

    /// A save that commits keeps what it withdrew. A read that began before it, and finishes after it, can only
    /// narrow; one that began after it publishes what it found, wider or narrower.
    func testACommittedSaveKeepsWhatItWithdrewAgainstAReadThatBeganBeforeIt() {
        let gate = AiVocabularyGate()
        gate.publish(scope(2, ["a": "h1", "b": "h2"]))
        let earlier = gate.beginRead()
        let narrowing = gate.narrow { library, _ in library == "a" }
        gate.commit(narrowing)

        gate.publish(scope(2, ["a": "h1", "b": "h2"]), from: earlier)

        XCTAssertFalse(gate.covers(scope(2, ["b": "h2"])), "an earlier read widened what the save withdrew")
        XCTAssertTrue(gate.covers(scope(2, ["a": "h1"])))

        gate.publish(scope(3, ["a": "h1", "b": "h2"]), from: gate.beginRead())

        XCTAssertTrue(gate.covers(scope(2, ["b": "h2"])), "a read after the save could not publish what it found")
    }

    /// One persisted generation orders nothing. A read that began before a newer one, and finishes after it, can only
    /// narrow what the newer read found, so it cannot widen what that read found paused.
    func testAReadThatBeganBeforeANewerOneNeverWidensWhatTheNewerOneFound() {
        let gate = AiVocabularyGate()
        let older = gate.beginRead()
        let newer = gate.beginRead()
        gate.publish(scope(2, ["a": "h1"]), from: newer)

        gate.publish(scope(2, ["a": "h1", "b": "h2"]), from: older)

        XCTAssertFalse(gate.covers(scope(2, ["b": "h2"])), "the older read widened what the newer one found")
        XCTAssertTrue(gate.covers(scope(2, ["a": "h1"])))
    }

    /// The finding: a ticket orders where a read began, not when it looked at the files. The older read looks last and
    /// finds a pack revoked, or changed, that the newer read published first as permitted, so what it found narrows
    /// what is established instead of being dropped.
    func testAReadThatBeganBeforeANewerOneNarrowsWhatTheNewerOnePublishedWhenItFoundARevocation() {
        let gate = AiVocabularyGate()
        let older = gate.beginRead()
        let newer = gate.beginRead()
        gate.publish(scope(2, ["a": "h1", "b": "h2", "c": "h3"]), from: newer)

        gate.publish(scope(2, ["a": "h1", "b": "changed"]), from: older)

        XCTAssertTrue(gate.covers(scope(2, ["a": "h1"])))
        XCTAssertFalse(gate.covers(scope(2, ["b": "h2"])), "a pack the older read found changed kept its permission")
        XCTAssertFalse(gate.covers(scope(2, ["c": "h3"])), "a pack the older read found revoked kept its permission")
    }

    /// An older read that narrows leaves the newest read where it was: a read that began between the two and publishes
    /// later is still older than the newest, so it cannot widen either.
    func testAnOlderReadThatNarrowsLeavesTheNewestReadWhereItWas() {
        let gate = AiVocabularyGate()
        let oldest = gate.beginRead()
        let middle = gate.beginRead()
        let newest = gate.beginRead()
        gate.publish(scope(2, ["a": "h1"]), from: newest)
        gate.publish(scope(2, ["a": "h1"]), from: oldest)

        gate.publish(scope(2, ["a": "h1", "b": "h2"]), from: middle)

        XCTAssertFalse(gate.covers(scope(2, ["b": "h2"])), "a read that began between the two was taken for the newest")
        XCTAssertTrue(gate.covers(scope(2, ["a": "h1"])))
    }

    /// Narrowing by an older read is not a blanket denial: a read that begins afterwards and finds the pack permitted
    /// again, because its file was put back or its change accepted, publishes the permission.
    func testAReadThatBeginsAfterAnOlderReadNarrowedPublishesThePermissionAgain() {
        let gate = AiVocabularyGate()
        let older = gate.beginRead()
        gate.publish(scope(2, ["a": "h1", "b": "h2"]), from: gate.beginRead())
        gate.publish(scope(2, ["a": "h1"]), from: older)
        XCTAssertFalse(gate.covers(scope(2, ["b": "h2"])), "premise: the older read narrowed")

        gate.publish(scope(2, ["a": "h1", "b": "h2"]), from: gate.beginRead())

        XCTAssertTrue(gate.covers(scope(2, ["b": "h2"])), "a read that began later could not restore the permission")
    }

    /// A save's hold keeps applying to what an older read narrows to, and a save that does not commit leaves exactly
    /// what the older read left: neither its own hold nor the older read's finding is given back.
    func testAnOlderReadThatPublishesDuringASaveIsStillHeldToItsNarrowing() {
        let gate = AiVocabularyGate()
        let older = gate.beginRead()
        gate.publish(scope(2, ["a": "h1", "b": "h2", "c": "h3"]), from: gate.beginRead())
        let narrowing = gate.narrow { library, _ in library != "a" }
        gate.publish(scope(2, ["a": "h1", "b": "h2"]), from: older)
        XCTAssertFalse(gate.covers(scope(2, ["a": "h1"])), "the save's hold stopped applying")
        XCTAssertFalse(gate.covers(scope(2, ["c": "h3"])), "the older read's revocation was dropped")

        gate.abandon(narrowing)

        XCTAssertTrue(
            gate.covers(scope(2, ["a": "h1", "b": "h2"])), "the save that did not commit took more than its hold")
        XCTAssertFalse(gate.covers(scope(2, ["c": "h3"])), "the save that did not commit gave back what a read revoked")
    }

    /// An unreadable state reads as generation 1. A repaired state still restores the permission at a generation
    /// below the one published before the reset, because the order is the order the reads began in.
    func testARepairedStateBelowTheGenerationPublishedBeforeAResetRestoresThePermission() {
        let gate = AiVocabularyGate()
        gate.publish(scope(9, ["a": "h1"]))
        gate.publish(scope(1, [:]))
        XCTAssertFalse(gate.covers(scope(9, ["a": "h1"])), "the reset did not withdraw the permission")

        gate.publish(scope(2, ["a": "h1"]))

        XCTAssertTrue(gate.covers(scope(9, ["a": "h1"])), "the repaired state stayed denied")
    }

    /// A read that began, and found a pack unreadable before it published, stands when the save that withdrew the same
    /// pack does not commit. Giving back what the save narrowed would give back what the read found revoked, however
    /// equal the read's result is to the narrowing.
    func testAnAbandonedSaveNeverGivesBackWhatAnEqualValuedReadFoundRevoked() {
        let gate = AiVocabularyGate()
        gate.publish(scope(2, ["a": "h1", "b": "h2"]))
        let admitted = scope(2, ["a": "h1", "b": "h2"])
        let delayed = gate.beginRead()
        let narrowing = gate.narrow { library, _ in library == "a" }
        gate.publish(scope(2, ["a": "h1"]), from: delayed)

        gate.abandon(narrowing)

        XCTAssertFalse(gate.covers(admitted), "b came back after a read had found it unreadable")
        XCTAssertTrue(gate.covers(scope(2, ["a": "h1"])))
    }

    func testAnAbandonedSaveNeverUndoesAReadThatPublishedAfterItNarrowed() {
        let gate = AiVocabularyGate()
        gate.publish(scope(2, ["a": "h1", "b": "h2"]))
        let narrowing = gate.narrow { library, _ in library == "a" }
        gate.publish(scope(3, ["a": "h1", "c": "h3"]), from: gate.beginRead())
        XCTAssertFalse(gate.covers(scope(3, ["c": "h3"])), "the save's hold did not apply while it ran")

        gate.abandon(narrowing)

        XCTAssertTrue(gate.covers(scope(3, ["c": "h3"])))
        XCTAssertFalse(gate.covers(scope(2, ["b": "h2"])))
    }

    // MARK: - The admission

    func testNothingStartsUnderAPermissionThatWasAlreadyWithdrawn() async throws {
        let gate = AiVocabularyGate()
        let admitted = scope(2, ["a": "h1"])
        gate.publish(admitted)
        let admission = CleanupVocabularyAdmission(scope: admitted, gate: gate)
        gate.narrow { _, _ in false }
        let started = StubSwitch()

        do {
            try await admission.whileAdmitted { started.turnOn() }
            XCTFail("work started under a withdrawn permission")
        } catch {
            XCTAssertEqual(error as? CleanupSendHandoff.Refusal, .vocabularyChanged)
        }
        XCTAssertFalse(started.isOn)
    }

    /// A scope that permits no word pack has nothing a save could withdraw, so the work runs whatever the gate says.
    func testAScopeThatPermitsNoWordPackIsNeverHeldBack() async throws {
        let gate = AiVocabularyGate()
        let admission = CleanupVocabularyAdmission(scope: .none, gate: gate)
        gate.narrow { _, _ in false }
        let started = StubSwitch()

        try await admission.whileAdmitted { started.turnOn() }

        XCTAssertTrue(started.isOn)
    }

    /// The finding: a permission withdrawn while a request waits for its credential. The settings are unchanged and the
    /// lifetime is open, so only the word pack permission can stop the request at the last step before it starts.
    func testAPermissionWithdrawnWhileTheCredentialIsBeingAcquiredStopsTheRequestBeforeItLeaves() async throws {
        let gate = AiVocabularyGate()
        let admitted = scope(2, ["team": "h1"])
        gate.publish(admitted)
        let admission = CleanupVocabularyAdmission(scope: admitted, gate: gate)
        let credential = SuspendedCredential()
        let log = RequestLog()
        let provider = makeProvider(credential: credential) { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned.")
        }

        let request = Task {
            try await admission.whileAdmitted {
                try await provider.clean(CleanupRequest(transcript: "raw text", writingStylePrompt: "Style."))
            }
        }
        try await credential.reached.wait()
        // What a Settings save does before it commits.
        gate.narrow { _, _ in false }
        credential.release.open()

        do {
            _ = try await request.value
            XCTFail("a request composed under a withdrawn permission was sent")
        } catch {
            XCTAssertEqual(error as? CleanupSendHandoff.Refusal, .vocabularyChanged)
        }
        XCTAssertEqual(log.count, 0, "the request reached the transport")
    }

    /// A save that withdrew a pack and did not commit, while a read that began before it published a result equal to
    /// the narrowing, must not give the pack back: the read found it revoked, and a request that waited for its
    /// credential through all of it is still held to what the read found.
    func testAnAbandonedSaveDoesNotLetARequestThatWaitedForItsCredentialSendARevokedPack() async throws {
        let gate = AiVocabularyGate()
        let admitted = scope(2, ["team": "h1", "other": "h2"])
        gate.publish(admitted)
        let admission = CleanupVocabularyAdmission(scope: admitted, gate: gate)
        let credential = SuspendedCredential()
        let log = RequestLog()
        let provider = makeProvider(credential: credential) { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned.")
        }
        let request = Task {
            try await admission.whileAdmitted {
                try await provider.clean(CleanupRequest(transcript: "raw text", writingStylePrompt: "Style."))
            }
        }
        try await credential.reached.wait()
        let delayed = gate.beginRead()
        let narrowing = gate.narrow { library, _ in library == "team" }
        gate.publish(scope(2, ["team": "h1"]), from: delayed)
        gate.abandon(narrowing)
        credential.release.open()

        do {
            _ = try await request.value
            XCTFail("a pack a read had found revoked was sent after the save that withdrew it did not commit")
        } catch {
            XCTAssertEqual(error as? CleanupSendHandoff.Refusal, .vocabularyChanged)
        }
        XCTAssertEqual(log.count, 0, "the request reached the transport")
    }

    func testARequestUnderAPermissionStillInForceIsSent() async throws {
        let gate = AiVocabularyGate()
        let admitted = scope(2, ["team": "h1"])
        gate.publish(admitted)
        let admission = CleanupVocabularyAdmission(scope: admitted, gate: gate)
        let credential = SuspendedCredential()
        let log = RequestLog()
        let provider = makeProvider(credential: credential) { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned.")
        }

        let request = Task {
            try await admission.whileAdmitted {
                try await provider.clean(CleanupRequest(transcript: "raw text", writingStylePrompt: "Style."))
            }
        }
        try await credential.reached.wait()
        // A save that leaves this pack as it was narrows the others and nothing it carried.
        gate.narrow { library, _ in library == "team" }
        credential.release.open()

        let response = try await request.value
        XCTAssertEqual(response.cleanedText, "Cleaned.")
        XCTAssertEqual(log.count, 1)
    }
}
