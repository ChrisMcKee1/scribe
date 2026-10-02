import XCTest

@testable import Scribe

final class CleanupCandidateTests: XCTestCase {
    func testStatusAndRecordingRecipientCaptureDoNotWaitBehindThePublicationTransaction() async throws {
        let fixture = makeCleanupStore()
        let gate = CleanupSendGate()
        let cache = CleanupProviderCache(
            store: fixture.store, environment: [:],
            factory: .testing(session: makeStubSession { StubReply.completion($0, "Unwanted.") }),
            sendGate: gate)
        let recipient = try cache.captureRecipient()
        let held = ReadPause()
        let publishing = Task.detached {
            gate.withPublication(
                {
                    held.arrive()
                    return recipient
                },
                publication: { CleanupAuthorityPublication(vocabulary: .none, recipient: $0) })
        }
        await held.waitUntilReached()
        let read = LockedValue<CleanupRecipient>()
        let finished = await finishes(within: 30) {
            if let captured = try? cache.captureRecipient() { read.set(captured) }
        }
        held.release()
        _ = await publishing.value
        XCTAssertTrue(finished, "Metadata capture must not take the lock which covers SQLite COMMIT")
        XCTAssertEqual(read.value, recipient)
    }

    func testUnsavedCredentialChangesHaveDistinctOpaqueRecipientIdentities() throws {
        let fixture = makeCleanupStore()
        var candidate = CleanupCandidate(settings: fixture.store.snapshot())
        let first = try candidate.recipient(environment: [:])
        candidate.apiKey = .replace("synthetic-secret")
        let second = try candidate.recipient(environment: [:])
        XCTAssertNotEqual(first, second)
        XCTAssertFalse(String(describing: second).contains("synthetic-secret"))
        XCTAssertFalse(String(reflecting: candidate).contains("synthetic-secret"))
    }

    func testTheUnsavedCandidateAndSecretAreTestedWithoutChangingStoredValues() async throws {
        let fixture = makeCleanupStore()
        let store = fixture.store
        store.providerKind = .openAICompatible
        store.openAIBaseURL = "https://saved.invalid/v1"
        store.openAIModel = "saved-model"
        let original = store.snapshot()
        var draft = original
        draft.openAIBaseURL = "https://candidate.invalid/v1"
        draft.openAIModel = "candidate-model"
        var candidate = CleanupCandidate(settings: draft)
        candidate.apiKey = .replace("candidate-test-key")
        candidate.writingStyle = "Candidate writing style."
        let requests = RequestLog()
        let session = makeStubSession { request in
            requests.record(request)
            return StubReply.completion(request, "Ok.")
        }
        let cache = CleanupProviderCache(
            store: store, environment: [:], factory: .testing(session: session))
        let checked = await cache.checkCandidate(candidate)
        let result = checked.result
        XCTAssertTrue(result.reachable, result.message)
        XCTAssertEqual(checked.recipient, try candidate.recipient(environment: [:]))
        let sent = try XCTUnwrap(requests.all.first)
        XCTAssertEqual(sent.url?.host, "candidate.invalid")
        XCTAssertEqual(sent.jsonBody["model"] as? String, "candidate-model")
        XCTAssertTrue(sent.messageContents.joined().contains("Candidate writing style."))
        XCTAssertTrue(sent.messageContents.joined().contains("ok"))
        XCTAssertEqual(store.snapshot(), original)
        XCTAssertNil(try store.readOpenAIApiKey())
    }

    func testEnvironmentOverridesResolveExactlyAsTheyDoForDictation() async throws {
        let fixture = makeCleanupStore()
        var draft = fixture.store.snapshot()
        draft.providerKind = .openAICompatible
        draft.openAIBaseURL = "https://draft.invalid/v1"
        draft.openAIModel = "draft-model"
        let environment = [
            "SCRIBE_CLEANUP_PROVIDER": "openai-compatible",
            "SCRIBE_CLEANUP_BASE_URL": "https://override.invalid/v1",
            "SCRIBE_CLEANUP_MODEL": "override-model",
        ]
        let candidate = CleanupCandidate(settings: draft)
        XCTAssertEqual(
            try candidate.recipient(environment: environment).connection,
            try CleanupProviderResolver.connection(settings: draft, environment: environment))
        let requests = RequestLog()
        let cache = CleanupProviderCache(
            store: fixture.store, environment: environment,
            factory: .testing(
                session: makeStubSession { request in
                    requests.record(request)
                    return StubReply.completion(request, "Ok.")
                }))
        let checked = await cache.checkCandidate(candidate)
        let result = checked.result
        XCTAssertTrue(result.reachable, result.message)
        XCTAssertEqual(checked.recipient, try candidate.recipient(environment: environment))
        XCTAssertEqual(checked.recipient?.connection.source, .environment)
        XCTAssertEqual(requests.all.first?.url?.host, "override.invalid")
        XCTAssertFalse(String(reflecting: checked).contains("override.invalid"))
    }

    func testAnAuxiliaryConsentCannotFollowALaterEndpointChange() async throws {
        let fixture = makeCleanupStore()
        let store = fixture.store
        store.isEnabled = true
        store.providerKind = .openAICompatible
        store.openAIBaseURL = "https://consented.invalid/v1"
        store.openAIModel = "model"
        let requests = RequestLog()
        let cache = CleanupProviderCache(
            store: store, environment: [:],
            factory: .testing(
                session: makeStubSession { request in
                    requests.record(request)
                    return StubReply.completion(request, "Unwanted.")
                }))
        let consent = try cache.admitAuxiliary()
        store.openAIBaseURL = "https://not-consented.invalid/v1"
        do {
            _ = try await cache.complete(CleanupRequest(transcript: "aggregate only"), consent: consent)
            XCTFail("Consent must not follow another endpoint")
        } catch {
            XCTAssertEqual(error as? CleanupHoldback, .recipientChanged)
        }
        XCTAssertEqual(requests.count, 0)
    }

    func testARecipientChangedDuringCredentialConstructionCannotReachTheTransport() async throws {
        let fixture = makeCleanupStore()
        let store = fixture.store
        store.isEnabled = true
        store.providerKind = .openAICompatible
        store.openAIBaseURL = "https://first.invalid/v1"
        store.openAIModel = "model"
        let requests = RequestLog()
        let cache = CleanupProviderCache(
            store: store, environment: [:],
            factory: .testing(
                session: makeStubSession { request in
                    requests.record(request)
                    return StubReply.completion(request, "Unwanted.")
                }))
        let consent = try cache.admitAuxiliary()
        let reading = fixture.apiKeys.pauseNextRead()
        let work = Task.detached {
            try await cache.complete(CleanupRequest(transcript: "aggregate only"), consent: consent)
        }
        await reading.waitUntilReached()
        store.openAIBaseURL = "https://second.invalid/v1"
        reading.release()
        do {
            _ = try await work.value
            XCTFail("A built credential is not permission to send to the old recipient")
        } catch {
            XCTAssertEqual(error as? CleanupHoldback, .recipientChanged)
        }
        XCTAssertEqual(requests.count, 0)
    }

    func testAChangeAfterSendingDiscardsTheAnswerWithoutClaimingNothingWasSent() async throws {
        let fixture = makeCleanupStore()
        let store = fixture.store
        store.isEnabled = true
        store.providerKind = .openAICompatible
        store.openAIBaseURL = "https://first.invalid/v1"
        store.openAIModel = "first"
        let requests = RequestLog()
        let cache = CleanupProviderCache(
            store: store, environment: [:],
            factory: .testing(
                session: makeStubSession { request in
                    requests.record(request)
                    store.openAIModel = "second"
                    return StubReply.completion(request, "Old answer.")
                }))
        let consent = try cache.admitAuxiliary()
        do {
            _ = try await cache.complete(CleanupRequest(transcript: "aggregate only"), consent: consent)
            XCTFail("An obsolete answer must not be presented for new settings")
        } catch {
            XCTAssertEqual(error as? CleanupHoldback, .changedAfterSending)
            XCTAssertFalse(error.localizedDescription.contains("nothing was sent"))
        }
        XCTAssertEqual(requests.count, 1)
    }
}
