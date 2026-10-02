import XCTest

@testable import Scribe

final class CleanupAdmissionWireTests: XCTestCase {
    private struct HeldCredential: AzureCredentialProvider {
        let gate: SettingsTestGate

        func accessToken(scope: String) async throws -> AzureAccessToken {
            await gate.pass()
            return AzureAccessToken(token: "synthetic-token", expiresAt: .distantFuture)
        }
    }

    private func receipt(
        gate: CleanupSendGate, model: String = "selected", app: LocalServerApp = .none,
        endpoint: String = "http://127.0.0.1:1234/v1", apiStyle: CustomAPIStyle = .chatCompletions
    ) throws -> CleanupRequestReceipt {
        let fixture = makeCleanupStore()
        var settings = fixture.store.snapshot()
        settings.providerKind = .openAICompatible
        settings.openAIBaseURL = endpoint
        settings.openAIModel = model
        settings.openAIApiStyle = apiStyle
        settings.selectedLocalApp = app
        settings.ollamaContextTokens = 4096
        settings.lmStudioContextTokens = 4096
        let recipient = CleanupRecipient(
            connection: try CleanupProviderResolver.connection(settings: settings, environment: [:]),
            settings: settings)
        let scope = AiVocabularyScope(generation: 4, permittedContent: ["pack": "accepted"])
        gate.publishRecipient(recipient)
        gate.publishVocabulary(scope)
        return gate.receipt(scope: scope, recipient: recipient, kind: .dictation)
    }

    func testEveryAPIChecksPermissionAtItsActualTransportStart() async throws {
        for variant in 0..<3 {
            let gate = CleanupSendGate()
            let app: LocalServerApp = variant == 2 ? .ollama : .none
            let admitted = try receipt(gate: gate, app: app)
            let requests = RequestLog()
            let session = makeStubSession { request in
                requests.record(request)
                return StubReply.completion(request, "Must not run.")
            }

            func testChangingPermissionAfterPromptCompositionStillPreventsEveryAPIFromStarting() async throws {
                for variant in 0..<3 {
                    let gate = CleanupSendGate()
                    let admitted = try receipt(gate: gate, app: variant == 2 ? .ollama : .none)
                    let barrier = SettingsTestGate()
                    let requests = RequestLog()
                    let session = makeStubSession { request in
                        requests.record(request)
                        return StubReply.completion(request, "Unwanted.")
                    }
                    let provider = OpenAICompatibleCleanupProvider(
                        model: "selected", serviceURL: URL(string: "http://127.0.0.1:1234/v1")!,
                        apiStyle: variant == 1 ? .responses : .chatCompletions,
                        localServerApp: variant == 2 ? .ollama : .none, session: session)
                    let work = Task {
                        try await CleanupSendContext.$beforeTransportStart.withValue({ await barrier.pass() }) {
                            try await provider.clean(
                                CleanupRequest(
                                    transcript: "dictation canary", writingStylePrompt: "vocabulary canary",
                                    receipt: admitted))
                        }
                    }
                    await barrier.waitForArrival()
                    gate.publishVocabulary(.none)
                    await barrier.open()
                    do {
                        _ = try await work.value
                        XCTFail("Encoded JSON is not an authorization to send")
                    } catch {
                        XCTAssertEqual(error as? CleanupHoldback, .vocabularyChanged)
                    }
                    XCTAssertEqual(requests.count, 0)
                }
            }
            let provider = OpenAICompatibleCleanupProvider(
                model: "selected", serviceURL: URL(string: "http://127.0.0.1:1234/v1")!,
                apiStyle: variant == 1 ? .responses : .chatCompletions,
                localServerApp: app, session: session)
            gate.publishVocabulary(.none)
            do {
                _ = try await provider.clean(
                    CleanupRequest(
                        transcript: "private dictation", writingStylePrompt: "private words", receipt: admitted))
                XCTFail("Variant \(variant) must be held back")
            } catch {
                XCTAssertEqual(error as? CleanupHoldback, .vocabularyChanged)
            }
            XCTAssertEqual(requests.count, 0)
        }
    }

    func testAPlainRetryCannotReuseThePermissionOfItsFirstAttempt() async throws {
        let gate = CleanupSendGate()
        let admitted = try receipt(gate: gate)
        let requests = RequestLog()
        let session = makeStubSession { request in
            requests.record(request)
            gate.publishVocabulary(.none)
            return (
                HTTPURLResponse(url: request.url!, statusCode: 400, httpVersion: nil, headerFields: nil)!,
                Data(#"{"error":{"message":"unsupported reasoning_effort"}}"#.utf8)
            )
        }
        let provider = OpenAICompatibleCleanupProvider(
            model: "selected", serviceURL: URL(string: "http://127.0.0.1:1234/v1")!, session: session)
        do {
            _ = try await provider.clean(CleanupRequest(transcript: "words", receipt: admitted))
            XCTFail("The retry must recheck permission")
        } catch {
            XCTAssertEqual(error as? CleanupHoldback, .vocabularyChanged)
        }
        XCTAssertEqual(requests.count, 1)
    }

    func testAnAuthorizedResponsesRequestKeepsStoredOutputOff() async throws {
        let gate = CleanupSendGate()
        let admitted = try receipt(gate: gate, endpoint: "https://example.invalid/v1", apiStyle: .responses)
        let requests = RequestLog()
        let session = makeStubSession { request in
            requests.record(request)
            return (
                HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!,
                Data(#"{"output":[{"type":"message","content":[{"type":"output_text","text":"Cleaned."}]}]}"#.utf8)
            )
        }
        let provider = AdmittedCleanupProvider(
            provider: OpenAICompatibleCleanupProvider(
                model: "selected", serviceURL: URL(string: "https://example.invalid/v1")!,
                apiStyle: .responses, session: session))
        let result = try await provider.clean(CleanupRequest(transcript: "Words.", receipt: admitted))
        XCTAssertEqual(result.cleanedText, "Cleaned.")
        XCTAssertEqual(requests.count, 1)
        XCTAssertEqual(requests.all.first?.jsonBody["store"] as? Bool, false)
    }

    func testAnApplicationClientWithoutAReceiptFailsClosed() async throws {
        let requests = RequestLog()
        let provider = AdmittedCleanupProvider(
            provider: OpenAICompatibleCleanupProvider(
                model: "model", serviceURL: URL(string: "https://example.invalid/v1")!,
                session: makeStubSession { request in
                    requests.record(request)
                    return StubReply.completion(request, "Unwanted.")
                }))
        do {
            _ = try await provider.clean(CleanupRequest(transcript: "private text"))
            XCTFail("The production wrapper must refuse requests without admission")
        } catch {
            XCTAssertEqual(error as? CleanupHoldback, .noAdmission)
        }
        XCTAssertEqual(requests.count, 0)
    }

    func testARequestWaitingForTheLocalLaneIsCheckedAfterItGetsTheLane() async throws {
        let gate = CleanupSendGate()
        let admitted = try receipt(gate: gate)
        let lane = AsyncLane()
        let held = SettingsTestGate()
        let blocker = Task { try await lane.run { await held.pass() } }
        await held.waitForArrival()
        let requests = RequestLog()
        let session = makeStubSession { request in
            requests.record(request)
            return StubReply.completion(request, "Unwanted.")
        }
        let provider = OpenAICompatibleCleanupProvider(
            model: "selected", serviceURL: URL(string: "http://127.0.0.1:1234/v1")!,
            localModelLane: lane, session: session)
        let waiting = Task {
            try await provider.clean(CleanupRequest(transcript: "words", receipt: admitted))
        }
        await lane.waitUntilWaiting(atLeast: 1)
        gate.publishVocabulary(.none)
        await held.open()
        _ = try await blocker.value
        do {
            _ = try await waiting.value
            XCTFail("An old queued request must not send")
        } catch {
            XCTAssertEqual(error as? CleanupHoldback, .vocabularyChanged)
        }
        XCTAssertEqual(requests.count, 0)
    }

    func testLMStudioPreparationAndInferenceShareOneReceipt() async throws {
        let gate = CleanupSendGate()
        let admitted = try receipt(gate: gate, app: .lmStudio)
        let preparation = SettingsTestGate()
        let requests = RequestLog()
        let session = makeStubSession { request in
            requests.record(request)
            return StubReply.completion(request, "Unwanted.")
        }
        let provider = OpenAICompatibleCleanupProvider(
            model: "selected", serviceURL: URL(string: "http://127.0.0.1:1234/v1")!,
            localServerApp: .lmStudio,
            loadLocalContext: { _, _, _ in
                await preparation.pass()
                return await LocalServerClient(session: session).loadWithContext(
                    "http://127.0.0.1:1234/v1", modelID: "selected", contextTokens: 4096)
            },
            session: session)
        let work = Task {
            try await provider.clean(CleanupRequest(transcript: "words", receipt: admitted))
        }
        await preparation.waitForArrival()
        gate.publishVocabulary(.none)
        await preparation.open()
        do {
            _ = try await work.value
            XCTFail("Neither preparation nor inference may start after revocation")
        } catch {
            XCTAssertEqual(error as? CleanupHoldback, .vocabularyChanged)
        }
        XCTAssertEqual(requests.count, 0)
    }

    func testRevocationWhileAcquiringACredentialStopsTheInferenceTransport() async throws {
        let gate = CleanupSendGate()
        let admitted = try receipt(gate: gate)
        let credential = SettingsTestGate()
        let requests = RequestLog()
        let session = makeStubSession { request in
            requests.record(request)
            return StubReply.completion(request, "Unwanted.")
        }
        let provider = MicrosoftFoundryCleanupProvider(
            inferenceBase: URL(string: "https://example.invalid/openai/v1")!,
            deployment: "model", promptCachingEnabled: { true },
            credential: HeldCredential(gate: credential), session: session)
        let work = Task {
            try await provider.clean(CleanupRequest(transcript: "words", receipt: admitted))
        }
        await credential.waitForArrival()
        gate.publishVocabulary(.none)
        await credential.open()
        do {
            _ = try await work.value
            XCTFail("Credential completion does not grant permission to send")
        } catch {
            XCTAssertEqual(error as? CleanupHoldback, .vocabularyChanged)
        }
        XCTAssertEqual(requests.count, 0)
    }
}
