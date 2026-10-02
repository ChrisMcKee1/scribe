import XCTest

@testable import Scribe

final class CleanupPrivacySwitchTests: XCTestCase {
    private enum PrivacyChoice: CaseIterable {
        case cleanup
        case promptCache
        case foundryVocabulary
        case ollamaVocabulary
        case lmStudioVocabulary

        func turnOff(in store: CleanupSettingsStore) {
            switch self {
            case .cleanup: store.isEnabled = false
            case .promptCache: store.azurePromptCaching = false
            case .foundryVocabulary: store.foundryLocalSendWholeVocabulary = false
            case .ollamaVocabulary: store.ollamaSendWholeVocabulary = false
            case .lmStudioVocabulary: store.lmStudioSendWholeVocabulary = false
            }
        }
    }

    func testEveryPrivacyChoiceIsRecheckedAfterTheRequestBodyWasBuilt() async throws {
        for choice in PrivacyChoice.allCases {
            let fixture = makeCleanupStore()
            let store = fixture.store
            store.isEnabled = true
            store.providerKind = .openAICompatible
            store.openAIBaseURL = "https://example.invalid/v1"
            store.openAIModel = "model"
            store.azurePromptCaching = true
            store.foundryLocalSendWholeVocabulary = true
            store.ollamaSendWholeVocabulary = true
            store.lmStudioSendWholeVocabulary = true
            let barrier = SettingsTestGate()
            let requests = RequestLog()
            let cache = CleanupProviderCache(
                store: store, environment: [:],
                factory: .testing(
                    session: makeStubSession { request in
                        requests.record(request)
                        return StubReply.completion(request, "Unwanted.")
                    }))
            let consent = try cache.admitAuxiliary()
            let work = Task {
                try await CleanupSendContext.$beforeTransportStart.withValue({ await barrier.pass() }) {
                    try await cache.complete(
                        CleanupRequest(transcript: "private text", writingStylePrompt: "private vocabulary"),
                        consent: consent)
                }
            }

            func testTheNextMicrosoftFoundryRequestUsesTheNewPromptCacheChoice() async throws {
                let fixture = makeCleanupStore()
                let store = fixture.store
                store.isEnabled = true
                store.providerKind = .microsoftFoundry
                store.azureEndpoint = "https://resource.invalid"
                store.azureDeployment = "model"
                store.azureAuthMode = .servicePrincipal
                store.azureTenantId = "synthetic.onmicrosoft.com"
                store.azureClientId = "11111111-1111-1111-1111-111111111111"
                try store.setAzureClientSecret("synthetic-client-secret", clientId: store.azureClientId)
                let requests = RequestLog()
                let cache = CleanupProviderCache(
                    store: store, environment: [:],
                    factory: .testing(
                        session: makeStubSession { request in
                            requests.record(request)
                            if request.url?.host == "login.microsoftonline.com" {
                                return StubReply.entraToken(request, "synthetic-token")
                            }
                            return StubReply.completion(request, "Answer.")
                        }))
                let request = CleanupRequest(transcript: "Words.")
                let first = try cache.admitAuxiliary()
                _ = try await cache.complete(request, consent: first)
                store.azurePromptCaching = false
                let next = try cache.admitAuxiliary()
                _ = try await cache.complete(request, consent: next)
                let inference = requests.all.filter { $0.url?.host == "resource.invalid" }
                XCTAssertEqual(inference.count, 2)
                XCTAssertNil(inference.first?.jsonBody["prompt_cache_options"])
                let cacheOptions = inference.last?.jsonBody["prompt_cache_options"] as? [String: Any]
                XCTAssertEqual(cacheOptions?["mode"] as? String, "explicit")
            }
            await barrier.waitForArrival()
            choice.turnOff(in: store)
            await barrier.open()
            do {
                _ = try await work.value
                XCTFail("A changed privacy choice must invalidate the already-composed request: \(choice)")
            } catch {
                XCTAssertEqual(error as? CleanupHoldback, .recipientChanged, "\(choice)")
            }
            XCTAssertEqual(requests.count, 0, "\(choice)")
        }
    }

    @MainActor
    func testTurningWholeVocabularyOffChangesTheNextLocalDictationsActualPrompt() async {
        for app in [LocalServerApp.ollama, .lmStudio] {
            let fixture = makeCleanupStore()
            let store = fixture.store
            store.isEnabled = true
            store.providerKind = .openAICompatible
            store.selectedLocalApp = app
            store.openAIBaseURL = app == .ollama ? "http://127.0.0.1:11434/v1" : "http://127.0.0.1:1234/v1"
            store.openAIModel = "model"
            store.ollamaSendWholeVocabulary = true
            store.lmStudioSendWholeVocabulary = true
            let requests = RequestLog()
            let cache = CleanupProviderCache(
                store: store, environment: [:],
                factory: .testing(
                    session: makeStubSession { request in
                        requests.record(request)
                        return StubReply.completion(request, "hello world")
                    }))
            let harness = makeHarness(cleanupSource: LiveDictationCleanup(cache: cache))
            harness.transcriber.defaultText = "hello world"
            harness.load(dictionary: [DictionaryEntry(pattern: "zxqvocabulary", replacement: "ZxqVocabulary")])
            await harness.dictate()
            await harness.waitUntilProcessed()
            XCTAssertTrue(requests.all.first?.messageContents.joined().contains("ZxqVocabulary") == true, "\(app)")

            store.ollamaSendWholeVocabulary = false
            store.lmStudioSendWholeVocabulary = false
            await harness.dictate()
            await harness.waitUntilProcessed()
            XCTAssertEqual(requests.count, 2, "\(app)")
            XCTAssertFalse(requests.all.last?.messageContents.joined().contains("ZxqVocabulary") == true, "\(app)")
            XCTAssertEqual(harness.fakeInjector.texts, ["hello world ", "hello world "])
        }
    }

    @MainActor
    func testCleanupOffKeepsDictationAndLocalCorrectionsWorkingWithoutAnyCleanupRequest() async {
        let fixture = makeCleanupStore()
        let store = fixture.store
        store.isEnabled = false
        store.providerKind = .openAICompatible
        store.openAIBaseURL = "https://example.invalid/v1"
        store.openAIModel = "model"
        let requests = RequestLog()
        let cache = CleanupProviderCache(
            store: store, environment: [:],
            factory: .testing(
                session: makeStubSession { request in
                    requests.record(request)
                    return StubReply.completion(request, "Must not run.")
                }))
        let harness = makeHarness(cleanupSource: LiveDictationCleanup(cache: cache))
        harness.transcriber.defaultText = "local correction"
        harness.load(dictionary: [DictionaryEntry(pattern: "local correction", replacement: "Works offline")])
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.fakeInjector.texts, ["Works offline "])
        XCTAssertEqual(harness.reports.latest?.cleanupOutcome, .off)
        XCTAssertEqual(requests.count, 0)
        XCTAssertEqual(fixture.apiKeys.reads, 0)
    }
}
