import XCTest

@testable import Scribe

final class CleanupCandidateTests: XCTestCase {
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
        let result = await cache.checkConnection(candidate: candidate)
        XCTAssertTrue(result.reachable, result.message)
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
        let result = await cache.checkConnection(candidate: candidate)
        XCTAssertTrue(result.reachable, result.message)
        XCTAssertEqual(requests.all.first?.url?.host, "override.invalid")
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
}
