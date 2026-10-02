import XCTest

@testable import Scribe

final class CleanupAdmissionWireTests: XCTestCase {
    private func receipt(
        gate: CleanupSendGate, model: String = "selected", app: LocalServerApp = .none
    ) throws -> CleanupRequestReceipt {
        let fixture = makeCleanupStore()
        var settings = fixture.store.snapshot()
        settings.providerKind = .openAICompatible
        settings.openAIBaseURL = "http://127.0.0.1:1234/v1"
        settings.openAIModel = model
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
}
