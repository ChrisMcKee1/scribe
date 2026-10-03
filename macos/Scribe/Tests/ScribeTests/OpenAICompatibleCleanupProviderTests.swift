import XCTest

@testable import Scribe

private actor LMStudioLoadBox {
    var value: (endpoint: String, model: String, context: Int)?

    func record(endpoint: String, model: String, context: Int) {
        value = (endpoint, model, context)
    }
}

final class OpenAICompatibleEndpointTests: XCTestCase {
    /// OpenRouter documents its base with `/v1`, LM Studio without, and Windows expects the `/v1` form; either one must
    /// reach `/v1/chat/completions` exactly once.
    func testTheChatCompletionsPathIsAddedOnceWhateverTheBaseEndsWith() {
        let cases: [(base: String, expected: String)] = [
            ("http://127.0.0.1:1234", "http://127.0.0.1:1234/v1/chat/completions"),
            ("http://127.0.0.1:1234/", "http://127.0.0.1:1234/v1/chat/completions"),
            ("http://127.0.0.1:1234/v1", "http://127.0.0.1:1234/v1/chat/completions"),
            ("http://127.0.0.1:1234/v1/", "http://127.0.0.1:1234/v1/chat/completions"),
            ("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/chat/completions"),
            ("https://example.com/proxy/V1//", "https://example.com/proxy/v1/chat/completions"),
            ("HTTPS://example.com", "https://example.com/v1/chat/completions"),
        ]

        for (base, expected) in cases {
            XCTAssertEqual(
                OpenAICompatibleEndpoint.chatCompletionsURL(for: URL(string: base)!)?.absoluteString, expected, base)
        }
    }

    func testABaseThatIsNotAnHTTPURLWithAHostIsRefused() {
        for base in ["ftp://example.com", "localhost:1234", "file:///tmp/socket", "http://"] {
            guard let url = URL(string: base) else { continue }
            XCTAssertNil(OpenAICompatibleEndpoint.chatCompletionsURL(for: url), base)
        }
    }
}

final class OpenAICompatibleCleanupProviderTests: XCTestCase {
    func testPlainOllamaOwnSizeRetryStillEnforcesTheFieldOllamaReads() async throws {
        let log = RequestLog()
        let provider = OpenAICompatibleCleanupProvider(
            model: "model", serviceURL: URL(string: LocalAiServer.ollamaAddress)!,
            localServerApp: .ollama,
            readLocalServer: { _, _ in
                LocalServerState(
                    reach: .reached, models: [],
                    loaded: [LocalServerLoadedModel("model", 1, contextTokens: 4096)])
            },
            session: makeStubSession { request in
                log.record(request)
                if RecordedRequest(request).jsonBody["reasoning_effort"] != nil {
                    return StubReply.json(request, status: 400, #"{"error":{"message":"unsupported effort"}}"#)
                }
                return StubReply.completion(request, "Cleaned.")
            })
        _ = try await provider.clean(
            CleanupRequest(transcript: "sample", writingStylePrompt: "Edit.", maxOutputTokens: 32))
        XCTAssertEqual(log.count, 2)
        XCTAssertTrue(log.all.allSatisfy { $0.jsonBody["max_tokens"] as? Int == 32 })
        XCTAssertNil(log.all.last?.jsonBody["reasoning_effort"])
    }

    func testBothOllamaOwnSizePathsRefuseUnknownSmallAndOversizedRequests() async throws {
        for managed in [true, false] {
            for size in [0, 64, 2048, 32768] {
                let log = RequestLog()
                let session = makeStubSession { request in
                    log.record(request)
                    return StubReply.completion(request, "Cleaned.")
                }
                let read: @Sendable (String, String?) async -> LocalServerState = { _, _ in
                    LocalServerState(
                        reach: .reached, models: [],
                        loaded: [LocalServerLoadedModel("model", 0, contextTokens: size)])
                }
                let provider: any CleanupProvider =
                    managed
                    ? ManagedOllamaCleanupProvider(
                        model: "model", readLocalServer: { await read($0, nil) }, session: session)
                    : OpenAICompatibleCleanupProvider(
                        model: "model", apiKey: "saved-key",
                        serviceURL: URL(string: LocalAiServer.ollamaAddress)!,
                        localServerApp: .ollama, readLocalServer: read, session: session)
                for large in [false, true] {
                    let request = CleanupRequest(
                        transcript: large ? String(repeating: "語", count: 4200) : "private sample",
                        writingStylePrompt: "Edit.", maxOutputTokens: 32)
                    let fits = size >= 2048 && !large
                    let before = log.count
                    do {
                        _ = try await provider.clean(request)
                        XCTAssertTrue(fits)
                    } catch {
                        XCTAssertFalse(fits)
                        XCTAssertEqual(
                            error as? CleanupProviderError,
                            size == 0 ? .localContextUnknown : .localRequestTooLarge)
                    }
                    XCTAssertEqual(log.count - before, fits ? 1 : 0)
                }
                if let sent = log.all.first {
                    XCTAssertEqual(sent.url?.path, "/v1/chat/completions")
                    XCTAssertEqual(sent.jsonBody["max_completion_tokens"] as? Int, 32)
                    XCTAssertNil(sent.jsonBody["options"])
                    XCTAssertNil(sent.jsonBody["num_ctx"])
                }
            }
        }
    }

    func testOllamaOwnSizeConfirmsColdAndChangedCopiesWithoutTrustingLargerDefaults() async throws {
        for managed in [true, false] {
            for after in [0, 64, 2048] {
                let log = RequestLog()
                let reads = LockedValue<Int>()
                let read: @Sendable (String, String?) async -> LocalServerState = { _, _ in
                    let count = (reads.value ?? 0) + 1
                    reads.set(count)
                    return LocalServerState(
                        reach: .reached, models: [],
                        loaded: count == 1
                            ? []
                            : [
                                LocalServerLoadedModel("model", 0, contextTokens: count == 2 ? 2048 : after)
                            ])
                }
                let session = makeStubSession { request in
                    log.record(request)
                    return StubReply.completion(request, "Cleaned.")
                }
                let provider: any CleanupProvider =
                    managed
                    ? ManagedOllamaCleanupProvider(
                        model: "model", readLocalServer: { await read($0, nil) }, session: session)
                    : OpenAICompatibleCleanupProvider(
                        model: "model", serviceURL: URL(string: LocalAiServer.ollamaAddress)!,
                        localServerApp: .ollama, readLocalServer: read, session: session)
                do {
                    _ = try await provider.clean(
                        CleanupRequest(transcript: "private sample", writingStylePrompt: "Edit.", maxOutputTokens: 32))
                    XCTAssertEqual(after, 2048)
                } catch {
                    XCTAssertEqual(
                        error as? CleanupProviderError, after == 0 ? .localContextUnknown : .localRequestTooLarge)
                }
                XCTAssertEqual(log.count, 2)
                let first = try XCTUnwrap(log.all.first)
                XCTAssertEqual(first.jsonBody["max_completion_tokens"] as? Int, 1)
                XCTAssertFalse(first.bodyText.contains("private sample"))
                XCTAssertTrue(first.bodyText.contains("Return only OK."))
                XCTAssertEqual(reads.value, 3)
            }
        }
    }

    @MainActor
    func testBothOllamaOwnSizePathsReadyExactlyOnceAndRefuseUnconfirmedPrivateSends() async throws {
        for managed in [true, false] {
            for confirms in [true, false] {
                let log = RequestLog()
                let reads = LockedValue<Int>()
                let read: @Sendable (String, String?) async -> LocalServerState = { _, key in
                    if !managed { XCTAssertEqual(key, "saved-key") }
                    let count = (reads.value ?? 0) + 1
                    reads.set(count)
                    return LocalServerState(
                        reach: .reached, models: [],
                        loaded: confirms && count > 2
                            ? [LocalServerLoadedModel("model", 0, contextTokens: 2048)] : [])
                }
                let session = makeStubSession { request in
                    log.record(request)
                    return StubReply.completion(request, "OK")
                }
                let provider: any CleanupProvider =
                    managed
                    ? ManagedOllamaCleanupProvider(
                        model: "model", readLocalServer: { await read($0, nil) }, session: session)
                    : OpenAICompatibleCleanupProvider(
                        model: "model", apiKey: "saved-key",
                        serviceURL: URL(string: LocalAiServer.ollamaAddress)!,
                        localServerApp: .ollama, readLocalServer: read, session: session)
                do {
                    let result = try await provider.prepareLocalModel(isCurrent: { true }, onStarting: {})
                    XCTAssertTrue(confirms)
                    XCTAssertEqual(result, .started)
                } catch {
                    XCTAssertFalse(confirms)
                    XCTAssertEqual(error as? CleanupProviderError, .localContextUnknown)
                }
                XCTAssertEqual(log.count, 1)
                XCTAssertEqual(log.all.first?.jsonBody["max_completion_tokens"] as? Int, 1)
                XCTAssertEqual(reads.value, 3)
                let count = log.count
                do {
                    _ = try await provider.clean(
                        CleanupRequest(transcript: "private sample", writingStylePrompt: "Edit."))
                    XCTFail("An unspecified 4096-token output cannot fit the conservative default")
                } catch {
                    XCTAssertEqual(
                        error as? CleanupProviderError,
                        confirms ? .localRequestTooLarge : .localContextUnknown)
                }
                XCTAssertTrue(log.all.allSatisfy { !$0.bodyText.contains("private sample") })
                XCTAssertEqual(log.count, count + (confirms ? 0 : 1))
            }
        }
    }

    private let completionsURL = URL(string: "https://openrouter.ai/api/v1/chat/completions")!

    private func makeProvider(
        apiKey: String? = nil, _ handler: @escaping StubURLProtocol.Handler
    ) -> OpenAICompatibleCleanupProvider {
        OpenAICompatibleCleanupProvider(
            model: "test-model", apiKey: apiKey, completionsURL: completionsURL, session: makeStubSession(handler))
    }

    /// No `store` (Chat Completions keep nothing unless asked with `true`) and no `temperature` (a bring-your-own
    /// endpoint may serve a reasoning model, which rejects one).
    func testTheRequestIsAChatCompletionWithoutStoreOrTemperature() async throws {
        let log = RequestLog()
        let provider = makeProvider(apiKey: "sk-test") { request in
            log.record(request)
            return StubReply.completion(request, "  Cleaned sentence.  \n")
        }

        let response = try await provider.clean(CleanupRequest(transcript: "raw text", writingStylePrompt: "Be terse."))

        XCTAssertEqual(response.cleanedText, "Cleaned sentence.")
        XCTAssertEqual(response.providerID, "openai-compatible")
        XCTAssertEqual(response.modelID, "test-model")
        let sent = try XCTUnwrap(log.all.first)
        XCTAssertEqual(log.count, 1)
        XCTAssertEqual(sent.method, "POST")
        XCTAssertEqual(sent.url?.absoluteString, completionsURL.absoluteString)
        XCTAssertEqual(sent.header("Authorization"), "Bearer sk-test")
        XCTAssertEqual(sent.header("Content-Type"), "application/json")
        XCTAssertEqual(Set(sent.jsonBody.keys), ["model", "messages", "stream"])
        XCTAssertEqual(sent.jsonBody["model"] as? String, "test-model")
        XCTAssertEqual(sent.jsonBody["stream"] as? Bool, false)
        XCTAssertEqual(sent.messageContents, ["Be terse.", "raw text"])
        let roles = (sent.jsonBody["messages"] as? [[String: Any]])?.compactMap { $0["role"] as? String }
        XCTAssertEqual(roles, ["system", "user"])
    }

    func testALocalServerGetsOnDeviceGenerationSettings() async throws {
        let log = RequestLog()
        let provider = OpenAICompatibleCleanupProvider(
            model: "test-model",
            completionsURL: URL(string: "http://127.0.0.1:1234/v1/chat/completions")!,
            session: makeStubSession { request in
                log.record(request)
                return StubReply.completion(request, "Cleaned.")
            })

        _ = try await provider.clean(
            CleanupRequest(
                transcript: "raw text",
                writingStylePrompt: "Be terse.",
                maxOutputTokens: 16))

        let sent = try XCTUnwrap(log.all.first)
        XCTAssertEqual(sent.jsonBody["temperature"] as? Double, CleanupSampling.onDeviceTemperature)
        XCTAssertEqual(sent.jsonBody["reasoning_effort"] as? String, CleanupReasoningEffort.none)
        XCTAssertEqual(sent.jsonBody["max_completion_tokens"] as? Int, 16)
        XCTAssertEqual(sent.jsonBody["max_tokens"] as? Int, 16)
    }

    func testALoopbackLookingDomainIsRemoteAndCannotInheritLocalAppRequestFields() async throws {
        for endpoint in [
            "http://127.attacker.example/v1", "http://127.0.0.1.attacker.example/v1",
            "https://remote.example/v1",
        ] {
            XCTAssertFalse(LocalAiServer.isOnThisMac(endpoint), endpoint)
            let log = RequestLog()
            let provider = OpenAICompatibleCleanupProvider(
                model: "model", serviceURL: URL(string: endpoint)!, localServerApp: .lmStudio,
                localTuning: { LocalModelTuning(contextTokens: 32768, sendWholeVocabulary: true) },
                session: makeStubSession { request in
                    log.record(request)
                    return StubReply.completion(request, "Cleaned.")
                })
            XCTAssertFalse(provider.usesLocalCleanupPrompt)
            XCTAssertEqual(provider.localServerApp, .none)
            _ = try await provider.clean(CleanupRequest(transcript: "sample", maxOutputTokens: 16))
            let body = try XCTUnwrap(log.all.first?.jsonBody)
            for field in ["temperature", "reasoning_effort", "keep_alive", "ttl", "max_tokens", "options"] {
                XCTAssertNil(body[field], field)
            }
        }
        for endpoint in [
            "http://127.0.0.1/v1", "http://127.10.20.30/v1", "http://[::1]/v1",
            "http://localhost/v1", "http://app.localhost/v1",
        ] {
            XCTAssertTrue(LocalAiServer.isOnThisMac(endpoint), endpoint)
        }
    }

    func testALocalServerThatRejectsTheExtraFieldsFallsBackToPlainRequests() async throws {
        let log = RequestLog()
        let provider = OpenAICompatibleCleanupProvider(
            model: "test-model",
            completionsURL: URL(string: "http://127.0.0.1:1234/v1/chat/completions")!,
            session: makeStubSession { request in
                log.record(request)
                let body = RecordedRequest(request).jsonBody
                if body["reasoning_effort"] != nil || body["max_tokens"] != nil {
                    return StubReply.json(
                        request,
                        status: 400,
                        """
                        {"error":{"message":"reasoning_effort: Input should be 'low', 'medium' or 'high'",
                        "type":"BadRequestError"}}
                        """
                    )
                }
                return StubReply.completion(request, "Cleaned.")
            })

        let first = try await provider.clean(
            CleanupRequest(transcript: "raw text", writingStylePrompt: "Be terse.", maxOutputTokens: 16))
        let second = try await provider.clean(
            CleanupRequest(transcript: "raw text", writingStylePrompt: "Be terse.", maxOutputTokens: 16))

        XCTAssertEqual(first.cleanedText, "Cleaned.")
        XCTAssertEqual(second.cleanedText, "Cleaned.")

        let bodies = log.all.map(\.jsonBody)
        XCTAssertEqual(bodies.count, 3)
        XCTAssertNotNil(bodies[0]["reasoning_effort"])
        XCTAssertNotNil(bodies[0]["max_tokens"])
        XCTAssertNil(bodies[1]["reasoning_effort"])
        XCTAssertNil(bodies[1]["max_tokens"])
        XCTAssertEqual(bodies[1]["max_completion_tokens"] as? Int, 16)
        XCTAssertNil(bodies[2]["reasoning_effort"])
        XCTAssertNil(bodies[2]["max_tokens"])
    }

    func testAnOllamaContextSizeUsesTheNativeChatAPI() async throws {
        let log = RequestLog()
        let provider = OpenAICompatibleCleanupProvider(
            model: "gemma4:e4b",
            apiKey: "test-key",
            completionsURL: URL(string: "http://127.0.0.1:11434/v1/chat/completions")!,
            localServerApp: .ollama,
            localTuning: { LocalModelTuning(contextTokens: 32768, sendWholeVocabulary: false) },
            session: makeStubSession { request in
                XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer test-key")
                if request.url?.path == "/api/show" {
                    return StubReply.json(request, #"{"model_info":{"gemma.context_length":32768}}"#)
                }
                if request.url?.path == "/api/ps" {
                    return StubReply.json(request, #"{"models":[{"name":"gemma4:e4b","context_length":32768}]}"#)
                }
                log.record(request)
                return StubReply.json(
                    request,
                    """
                    {"model":"gemma4:e4b","message":{"role":"assistant","content":"Cleaned."},
                    "done":true,"done_reason":"stop"}
                    """
                )
            })

        _ = try await provider.clean(
            CleanupRequest(transcript: "raw text", writingStylePrompt: "Be terse.", maxOutputTokens: 16))

        let sent = try XCTUnwrap(log.all.first)
        XCTAssertEqual(sent.url?.path, "/api/chat")
        XCTAssertEqual(sent.jsonBody["keep_alive"] as? String, "\(LocalModelDefaults.keepAliveMinutes)m")
        XCTAssertEqual(sent.jsonBody["think"] as? Bool, false)
        let options = try XCTUnwrap(sent.jsonBody["options"] as? [String: Any])
        XCTAssertEqual(options["num_ctx"] as? Int, 32768)
        XCTAssertEqual(options["num_predict"] as? Int, 16)
    }

    func testLMStudioUsesAManualCopysActualContextAndRefusesUnknownOrOversizedRequests() async throws {
        for context in [0, 512, 4096] {
            let log = RequestLog()
            let provider = OpenAICompatibleCleanupProvider(
                model: "model", serviceURL: URL(string: LocalAiServer.lmStudioAddress)!,
                localServerApp: .lmStudio,
                localTuning: { LocalModelTuning(contextTokens: 32768, sendWholeVocabulary: false) },
                loadLocalContext: { _, _, _ in
                    XCTFail("A manual copy must never be resized")
                    return nil
                },
                readLocalServer: { _, _ in
                    LocalServerState(
                        reach: .reached, models: [],
                        loaded: [LocalServerLoadedModel("model", 0, contextTokens: context, instanceID: "manual")])
                },
                session: makeStubSession { request in
                    log.record(request)
                    return StubReply.completion(request, "Cleaned.")
                })
            do {
                let answer = try await provider.clean(
                    CleanupRequest(
                        transcript: String(repeating: "語", count: 600), writingStylePrompt: "Edit.",
                        maxOutputTokens: 64))
                XCTAssertEqual(context, 4096)
                XCTAssertEqual(answer.cleanedText, "Cleaned.")
            } catch {
                XCTAssertEqual(
                    error as? CleanupProviderError, context == 0 ? .localContextUnknown : .localRequestTooLarge)
            }
            XCTAssertEqual(log.count, context == 4096 ? 1 : 0)
        }
    }

    func testLMStudiosOwnSizeUsesTheActualCopyAndAnEnforcedOutputCeiling() async throws {
        for context in [512, 8192] {
            let log = RequestLog()
            let provider = OpenAICompatibleCleanupProvider(
                model: "model", serviceURL: URL(string: LocalAiServer.lmStudioAddress)!,
                localServerApp: .lmStudio,
                readLocalServer: { _, _ in
                    LocalServerState(
                        reach: .reached, models: [],
                        loaded: [LocalServerLoadedModel("model", 0, contextTokens: context)])
                },
                session: makeStubSession { request in
                    log.record(request)
                    return StubReply.completion(request, "Cleaned.")
                })
            do {
                let answer = try await provider.clean(
                    CleanupRequest(transcript: "sample", writingStylePrompt: "Edit."))
                XCTAssertEqual(context, 8192)
                XCTAssertEqual(answer.cleanedText, "Cleaned.")
            } catch {
                XCTAssertEqual(context, 512)
                XCTAssertEqual(error as? CleanupProviderError, .localRequestTooLarge)
            }
            XCTAssertEqual(log.count, context == 8192 ? 1 : 0)
            if context == 8192 {
                XCTAssertEqual(log.all.first?.jsonBody["max_completion_tokens"] as? Int, 4096)
                XCTAssertEqual(log.all.first?.jsonBody["max_tokens"] as? Int, 4096)
            }
        }
    }

    func testLMStudiosOwnSizeReadiesOnlyWithFixedWordsBeforeLearningTheLoadedSize() async throws {
        for reportsSize in [true, false] {
            let log = RequestLog()
            let readied = LockedValue<Bool>()
            let provider = OpenAICompatibleCleanupProvider(
                model: "model", apiKey: "synthetic-key", serviceURL: URL(string: LocalAiServer.lmStudioAddress)!,
                localServerApp: .lmStudio,
                readLocalServer: { _, key in
                    XCTAssertEqual(key, "synthetic-key")
                    return LocalServerState(
                        reach: .reached, models: [],
                        loaded: readied.value == true
                            ? [LocalServerLoadedModel("model", 0, contextTokens: reportsSize ? 4096 : 0)] : [])
                },
                session: makeStubSession { request in
                    log.record(request)
                    readied.set(true)
                    return StubReply.completion(request, "Cleaned.")
                })
            do {
                _ = try await provider.clean(
                    CleanupRequest(
                        transcript: "private sample", writingStylePrompt: "private instructions", maxOutputTokens: 64))
                XCTAssertTrue(reportsSize)
            } catch {
                XCTAssertFalse(reportsSize)
                XCTAssertEqual(error as? CleanupProviderError, .localContextUnknown)
            }
            let readying = try XCTUnwrap(log.all.first)
            XCTAssertEqual(
                readying.messageContents,
                [
                    LocalModelReadiness.request.writingStylePrompt, LocalModelReadiness.request.transcript,
                ])
            XCTAssertFalse(readying.messageContents.joined().contains("private"))
            XCTAssertEqual(readying.jsonBody["max_completion_tokens"] as? Int, 1)
            XCTAssertEqual(log.count, reportsSize ? 2 : 1)
            if reportsSize {
                XCTAssertEqual(log.all.last?.messageContents, ["private instructions", "private sample"])
            }
        }
    }

    func testAnUnavailableLMStudioResidencyReadingRefusesTheTextSend() async throws {
        let log = RequestLog()
        let provider = OpenAICompatibleCleanupProvider(
            model: "model", serviceURL: URL(string: LocalAiServer.lmStudioAddress)!,
            localServerApp: .lmStudio,
            localTuning: { LocalModelTuning(contextTokens: 32768, sendWholeVocabulary: false) },
            readLocalServer: { _, _ in .failed },
            session: makeStubSession { request in
                log.record(request)
                return StubReply.completion(request, "Never.")
            })
        do {
            _ = try await provider.clean(
                CleanupRequest(transcript: "sample", writingStylePrompt: "Edit.", maxOutputTokens: 64))
            XCTFail("A requested context does not prove the copy's size")
        } catch {
            XCTAssertEqual(error as? CleanupProviderError, .localContextUnknown)
        }
        XCTAssertEqual(log.count, 0)
    }

    func testABusyLMStudioCopyIsFittedAsItIsWithoutResizingUnderAnotherUse() async throws {
        let lifecycle = LocalModelLifecycle(
            idle: .zero,
            actions: .init(
                unloadModel: { _, _, _ in
                    XCTFail("A busy model cannot be unloaded")
                    return false
                },
                unloadInstance: { _, _, _ in
                    XCTFail("A busy copy cannot be unloaded")
                    return false
                }))
        let target = LocalModelTarget(
            endpoint: LocalAiServer.lmStudioAddress, model: "model", app: .lmStudio, apiKey: nil)
        let otherUse = try await lifecycle.beginUse(target)
        defer { otherUse.end() }
        let log = RequestLog()
        let provider = OpenAICompatibleCleanupProvider(
            model: "model", serviceURL: URL(string: target.endpoint)!,
            localServerApp: .lmStudio, lifecycle: lifecycle,
            localTuning: { LocalModelTuning(contextTokens: 32768, sendWholeVocabulary: false) },
            loadLocalContext: { _, _, _ in
                XCTFail("Another use must keep this copy from being resized")
                return nil
            },
            readLocalServer: { _, _ in
                LocalServerState(
                    reach: .reached, models: [],
                    loaded: [
                        LocalServerLoadedModel(
                            "model", 0, contextTokens: 512, instanceID: "busy-copy", remainingTTLSeconds: 600)
                    ])
            },
            session: makeStubSession { request in
                log.record(request)
                return StubReply.completion(request, "Cleaned.")
            })
        do {
            _ = try await provider.clean(
                CleanupRequest(
                    transcript: String(repeating: "語", count: 600), writingStylePrompt: "Edit.",
                    maxOutputTokens: 64))
            XCTFail("The requested size cannot replace a busy copy's actual size")
        } catch {
            XCTAssertEqual(error as? CleanupProviderError, .localRequestTooLarge)
        }
        let answer = try await provider.clean(
            CleanupRequest(transcript: "sample", writingStylePrompt: "Edit.", maxOutputTokens: 64))
        XCTAssertEqual(answer.cleanedText, "Cleaned.")
        XCTAssertEqual(log.count, 1)
    }

    func testALMStudioContextSizeLoadsTheModelBeforeChatCompletions() async throws {
        let log = RequestLog()
        let load = LMStudioLoadBox()
        let provider = OpenAICompatibleCleanupProvider(
            model: "google/gemma-4-e2b",
            completionsURL: URL(string: "http://127.0.0.1:1234/v1/chat/completions")!,
            localServerApp: .lmStudio,
            localTuning: { LocalModelTuning(contextTokens: 16384, sendWholeVocabulary: false) },
            loadLocalContext: { endpoint, model, contextTokens in
                await load.record(endpoint: endpoint, model: model, context: contextTokens)
                return "instance-1"
            },
            readLocalServer: { _, _ in
                let loaded = await load.value
                return LocalServerState(
                    reach: .reached, models: [],
                    loaded: loaded == nil
                        ? []
                        : [
                            LocalServerLoadedModel(
                                "google/gemma-4-e2b", 0, contextTokens: 16384, instanceID: "instance-1")
                        ])
            },
            session: makeStubSession { request in
                log.record(request)
                return StubReply.completion(request, "Cleaned.")
            })

        _ = try await provider.clean(CleanupRequest(transcript: "raw text", writingStylePrompt: "Be terse."))

        let recorded = await load.value
        XCTAssertEqual(recorded?.endpoint, "http://127.0.0.1:1234/v1")
        XCTAssertEqual(recorded?.model, "google/gemma-4-e2b")
        XCTAssertEqual(recorded?.context, 16384)
        XCTAssertEqual(log.all.first?.url?.path, "/v1/chat/completions")
    }

    func testResponsesRequestsGoToResponsesAndSetStoreFalse() async throws {
        let log = RequestLog()
        let provider = OpenAICompatibleCleanupProvider(
            model: "test-model",
            serviceURL: URL(string: "https://ai.example.invalid/v1")!,
            apiStyle: .responses,
            session: makeStubSession { request in
                log.record(request)
                return StubReply.json(
                    request,
                    """
                    {"output":[{"type":"message","content":[{"type":"output_text","text":"Cleaned."}]}]}
                    """
                )
            })

        _ = try await provider.clean(
            CleanupRequest(transcript: "raw text", writingStylePrompt: "Be terse.", maxOutputTokens: 16))

        let sent = try XCTUnwrap(log.all.first)
        XCTAssertEqual(sent.url?.path, "/v1/responses")
        XCTAssertEqual(sent.jsonBody["store"] as? Bool, false)
        XCTAssertEqual(sent.jsonBody["max_output_tokens"] as? Int, 16)
    }

    func testWithoutAKeyNoAuthorizationIsSent() async throws {
        let log = RequestLog()
        let provider = makeProvider(apiKey: "") { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned.")
        }

        _ = try await provider.clean(CleanupRequest(transcript: "raw text"))

        XCTAssertNil(try XCTUnwrap(log.all.first).header("Authorization"))
    }

    /// A small model occasionally echoes the `<transcript>` tags the guardrail prompt tells it to key on.
    func testEchoedTranscriptTagsAreStripped() async throws {
        let provider = makeProvider { request in
            StubReply.completion(request, "<transcript>\nCleaned sentence.\n</transcript>")
        }

        let response = try await provider.clean(CleanupRequest(transcript: "raw text"))

        XCTAssertEqual(response.cleanedText, "Cleaned sentence.")
    }

    func testARefusalCarriesItsStatusAndCodeButNotTheBody() async throws {
        let body =
            #"{"error":{"message":"Incorrect API key provided: sk-test.","#
            + #""type":"invalid_request_error","code":"invalid_api_key"}}"#
        let provider = makeProvider(apiKey: "sk-test") { request in StubReply.json(request, status: 401, body) }

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(
            error,
            .rejected(
                status: 401, provider: .openAICompatible,
                reply: CleanupServiceReply(code: "invalid_api_key", message: "Incorrect API key provided: sk-test.")))
        XCTAssertEqual(
            FailureShape(error).description, "CleanupProviderError.rejected values=401 http=401 service=invalid_api_key"
        )
        let description = try XCTUnwrap(error.errorDescription)
        XCTAssertTrue(description.contains("401"), description)
        XCTAssertFalse(description.contains("Incorrect API key"), description)
        XCTAssertFalse(String(describing: error).contains("Incorrect API key"))
        XCTAssertEqual(
            CleanupFailureText.forSettings(error, providerName: "OpenAI-compatible endpoint"),
            "OpenAI-compatible endpoint: \(description) The endpoint said: Incorrect API key provided: sk-test.")
    }

    func testOllamasPlainTextErrorIsKeptAsItsMessage() async throws {
        let provider = makeProvider { request in
            StubReply.json(request, status: 404, #"{"error":"model \"llama9\" not found, try pulling it first"}"#)
        }

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(error.failureHTTPStatus, 404)
        XCTAssertNil(error.failureServiceCode)
        XCTAssertEqual(error.settingsDetail, "model \"llama9\" not found, try pulling it first")
    }

    func testAServerErrorWithAnHTMLBodyKeepsNothingOfIt() async throws {
        let provider = makeProvider { request in StubReply.json(request, status: 502, "<html>Bad gateway</html>") }

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(error, .rejected(status: 502, provider: .openAICompatible, reply: .empty))
        XCTAssertNil(error.settingsDetail)
    }

    func testATimeoutIsReportedAsTimedOut() async throws {
        let provider = makeProvider { _ in throw URLError(.timedOut) }

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(error, .timedOut)
    }

    /// A URL error's user info holds the failing URL; only the code may travel on.
    func testARefusedConnectionKeepsOnlyTheURLErrorCode() async throws {
        let provider = makeProvider { _ in
            throw URLError(.cannotConnectToHost, userInfo: [NSURLErrorFailingURLStringErrorKey: PrivacyCanary.url])
        }

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(error, .transport(URLError(.cannotConnectToHost)))
        XCTAssertTrue(error.isConnectionRefusal)
        PrivacyCanary.assertAbsent(from: String(describing: error))
        PrivacyCanary.assertAbsent(from: FailureShape(error).description)
        XCTAssertTrue(
            FailureShape(error).description.contains("url=cannotConnectToHost"), FailureShape(error).description)
    }

    func testASuccessThatIsNotACompletionIsInvalid() async throws {
        let provider = makeProvider { request in StubReply.json(request, "not json") }

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(error, .invalidResponse(.undecodable))
    }

    func testAnEmptyOrMissingAnswerIsInvalid() async throws {
        let blank = makeProvider { request in StubReply.completion(request, "  \n ") }
        let missing = makeProvider { request in StubReply.json(request, #"{"choices":[{"message":{"content":null}}]}"#)
        }
        let none = makeProvider { request in StubReply.json(request, #"{"choices":[]}"#) }

        for provider in [blank, missing, none] {
            let error = try await cleanupFailure(of: provider)
            XCTAssertEqual(error, .invalidResponse(.emptyCompletion))
        }
    }

    /// A dictation still has no text when the model stopped at its output limit, so that is a failure there too. It
    /// is told apart from an empty answer only so Test Connection, which sends a limit, can see the model answered.
    func testAnAnswerCutOffBeforeAnyTextIsStillNoAnswer() async throws {
        let cutOff = makeProvider { request in StubReply.completion(request, nil, finishReason: "length") }
        let cutOffBlank = makeProvider { request in StubReply.completion(request, " ", finishReason: "length") }
        let stopped = makeProvider { request in StubReply.completion(request, "", finishReason: "stop") }
        let filtered = makeProvider { request in StubReply.completion(request, nil, finishReason: "content_filter") }

        for provider in [cutOff, cutOffBlank] {
            let error = try await cleanupFailure(of: provider)
            XCTAssertEqual(error, .invalidResponse(.outputLimitReachedBeforeText))
        }
        for provider in [stopped, filtered] {
            let error = try await cleanupFailure(of: provider)
            XCTAssertEqual(error, .invalidResponse(.emptyCompletion))
        }
    }

    /// An answer that stopped at the limit after some text is text.
    func testAnAnswerCutOffAfterSomeTextIsKept() async throws {
        let provider = makeProvider { request in StubReply.completion(request, "Cleaned so f", finishReason: "length") }

        let response = try await provider.clean(CleanupRequest(transcript: "raw text"))

        XCTAssertEqual(response.cleanedText, "Cleaned so f")
    }
}

final class ManagedOllamaCleanupProviderTests: XCTestCase {
    @MainActor
    func testBothNativePathsKeepASmallerRuntimeCapAndRefuseAnAnswerThatDidNotFit() async throws {
        for managed in [true, false] {
            let log = RequestLog()
            let heldContext = LockedValue<Int>()
            heldContext.set(2048)
            let session = makeStubSession { request in
                log.record(request)
                switch request.url?.path {
                case "/api/show":
                    return StubReply.json(request, #"{"model_info":{"gemma.context_length":32768}}"#)
                case "/api/ps":
                    return StubReply.json(
                        request, #"{"models":[{"name":"model","context_length":\#(heldContext.value ?? 0)}]}"#)
                default:
                    return StubReply.json(request, #"{"message":{"content":"Cleaned."},"done":true}"#)
                }
            }
            let provider: any CleanupProvider =
                managed
                ? ManagedOllamaCleanupProvider(
                    model: "model", contextTokens: 32768,
                    readLocalServer: { _ in
                        LocalServerState(
                            reach: .reached, models: [],
                            loaded: [LocalServerLoadedModel("model", 0, contextTokens: 2048)])
                    }, session: session)
                : OpenAICompatibleCleanupProvider(
                    model: "model", serviceURL: URL(string: LocalAiServer.ollamaAddress)!,
                    localServerApp: .ollama,
                    localTuning: { LocalModelTuning(contextTokens: 32768, sendWholeVocabulary: false) },
                    readLocalServer: { _, _ in
                        LocalServerState(
                            reach: .reached, models: [],
                            loaded: [LocalServerLoadedModel("model", 0, contextTokens: 2048)])
                    },
                    session: session)
            let request = CleanupRequest(transcript: "sample", writingStylePrompt: "Edit.", maxOutputTokens: 32)
            _ = try await provider.clean(request)
            _ = try await provider.clean(request)
            let chats = log.all.filter { $0.url?.path == "/api/chat" }
            XCTAssertEqual(chats.count, 2)
            XCTAssertEqual((chats[0].jsonBody["options"] as? [String: Any])?["num_ctx"] as? Int, 32768)
            XCTAssertEqual((chats[1].jsonBody["options"] as? [String: Any])?["num_ctx"] as? Int, 2048)
            let sends = log.count
            let readiness = try await provider.prepareLocalModel(isCurrent: { true }, onStarting: {})
            XCTAssertEqual(readiness, .resident)
            XCTAssertEqual(log.count, sends)
            heldContext.set(64)
            do {
                _ = try await provider.clean(request)
                XCTFail("An answer from an insufficient context must not be accepted")
            } catch {
                XCTAssertEqual(error as? CleanupProviderError, .localRequestTooLarge)
            }
            let count = log.all.filter { $0.url?.path == "/api/chat" }.count
            do {
                _ = try await provider.clean(request)
                XCTFail("A known runtime cap must prevent the next text send")
            } catch {
                XCTAssertEqual(error as? CleanupProviderError, .localRequestTooLarge)
            }
            XCTAssertEqual(log.all.filter { $0.url?.path == "/api/chat" }.count, count)
        }
    }

    func testMissingResidencyAfterAChatRefusesTheAnswer() async throws {
        let provider = ManagedOllamaCleanupProvider(
            model: "model", contextTokens: 8192,
            session: makeStubSession { request in
                switch request.url?.path {
                case "/api/show":
                    return StubReply.json(request, #"{"model_info":{"gemma.context_length":8192}}"#)
                case "/api/ps":
                    return StubReply.json(request, #"{"models":[]}"#)
                default:
                    return StubReply.json(request, #"{"message":{"content":"Unverified."},"done":true}"#)
                }
            })
        do {
            _ = try await provider.clean(
                CleanupRequest(transcript: "sample", writingStylePrompt: "Edit.", maxOutputTokens: 32))
            XCTFail("A model that disappeared cannot vouch for its answer's context")
        } catch {
            XCTAssertEqual(error as? CleanupProviderError, .localContextUnknown)
        }
    }

    func testBothNativePathsCapEachRequestToTheModelsReportedMaximum() async throws {
        for managed in [true, false] {
            let log = RequestLog()
            let maximum = LockedValue<Int>()
            maximum.set(4096)
            let session = makeStubSession { request in
                log.record(request)
                if request.url?.path == "/api/show" {
                    XCTAssertEqual(RecordedRequest(request).jsonBody["model"] as? String, "model")
                    return StubReply.json(
                        request, #"{"model_info":{"gemma.context_length":\#(maximum.value ?? 0)}}"#)
                }
                if request.url?.path == "/api/ps" {
                    return StubReply.json(
                        request, #"{"models":[{"name":"model","context_length":\#(maximum.value ?? 0)}]}"#)
                }
                return StubReply.json(request, #"{"message":{"content":"Cleaned."},"done":true}"#)
            }
            let provider: any CleanupProvider =
                managed
                ? ManagedOllamaCleanupProvider(model: "model", contextTokens: 32768, session: session)
                : OpenAICompatibleCleanupProvider(
                    model: "model", serviceURL: URL(string: LocalAiServer.ollamaAddress)!,
                    localServerApp: .ollama,
                    localTuning: { LocalModelTuning(contextTokens: 32768, sendWholeVocabulary: false) },
                    session: session)
            _ = try await provider.clean(
                CleanupRequest(transcript: "sample", writingStylePrompt: "Edit.", maxOutputTokens: 32))
            let first = try XCTUnwrap(log.all.first { $0.url?.path == "/api/chat" })
            XCTAssertEqual((first.jsonBody["options"] as? [String: Any])?["num_ctx"] as? Int, 4096)
            maximum.set(1024)
            _ = try await provider.clean(
                CleanupRequest(transcript: "sample", writingStylePrompt: "Edit.", maxOutputTokens: 32))
            let lastChat = log.all.last { $0.url?.path == "/api/chat" }
            XCTAssertEqual((lastChat?.jsonBody["options"] as? [String: Any])?["num_ctx"] as? Int, 1024)
            do {
                _ = try await provider.clean(
                    CleanupRequest(transcript: String(repeating: "語", count: 1100), maxOutputTokens: 32))
                XCTFail("The requested size cannot vouch for a smaller model")
            } catch {
                XCTAssertEqual(error as? CleanupProviderError, .localRequestTooLarge)
            }
            XCTAssertEqual(log.all.filter { $0.url?.path == "/api/chat" }.count, 2)
        }
    }

    func testMissingContextMetadataRefusesTextRatherThanGuessingALimit() async throws {
        let log = RequestLog()
        let provider = ManagedOllamaCleanupProvider(
            contextTokens: 8192,
            session: makeStubSession { request in
                log.record(request)
                return StubReply.json(request, #"{"model_info":{}}"#)
            })
        do {
            _ = try await provider.clean(CleanupRequest(transcript: "private sample", maxOutputTokens: 32))
            XCTFail("Missing metadata cannot authorize a text request")
        } catch {
            XCTAssertEqual(error as? CleanupProviderError, .localContextUnknown)
        }
        XCTAssertFalse(log.all.isEmpty)
        XCTAssertTrue(log.all.allSatisfy { $0.url?.path == "/api/show" && $0.messageContents.isEmpty })
    }

    @MainActor
    func testReadinessReusesTheCappedSizeAfterTheModelAnswered() async throws {
        let log = RequestLog()
        let provider = ManagedOllamaCleanupProvider(
            model: "model", contextTokens: 32768,
            readLocalServer: { _ in
                LocalServerState(
                    reach: .reached, models: [],
                    loaded: [LocalServerLoadedModel("model", 0, contextTokens: 4096)])
            },
            session: makeStubSession { request in
                log.record(request)
                if request.url?.path == "/api/show" {
                    return StubReply.json(request, #"{"model_info":{"gemma.context_length":4096}}"#)
                }
                if request.url?.path == "/api/ps" {
                    return StubReply.json(request, #"{"models":[{"name":"model","context_length":4096}]}"#)
                }
                return StubReply.json(request, #"{"message":{"content":"Cleaned."},"done":true}"#)
            })
        _ = try await provider.clean(CleanupRequest(transcript: "sample", maxOutputTokens: 32))
        let count = log.count
        let ready = try await provider.prepareLocalModel(isCurrent: { true }, onStarting: {})
        XCTAssertEqual(ready, .resident)
        XCTAssertEqual(log.count, count)
    }

    func testAnOversizeNativeRequestIsRefusedBeforeTransportOnBothOllamaPaths() async throws {
        let log = RequestLog()
        let session = makeStubSession { request in
            log.record(request)
            return StubReply.json(request, #"{"message":{"content":"never"},"done":true}"#)
        }
        let providers: [any CleanupProvider] = [
            ManagedOllamaCleanupProvider(contextTokens: 2048, session: session),
            OpenAICompatibleCleanupProvider(
                model: "gemma4:e4b", serviceURL: URL(string: LocalAiServer.ollamaAddress)!,
                localServerApp: .ollama,
                localTuning: { LocalModelTuning(contextTokens: 2048, sendWholeVocabulary: false) },
                session: session),
        ]
        for provider in providers {
            do {
                _ = try await provider.clean(
                    CleanupRequest(transcript: String(repeating: "語", count: 2048), maxOutputTokens: 1))
                XCTFail("An oversized request must not reach Ollama")
            } catch {
                XCTAssertEqual(error as? CleanupProviderError, .localRequestTooLarge)
            }
        }
        XCTAssertTrue(log.all.isEmpty)
    }

    func testAChosenContextUsesTheNativeOllamaRouteForDictation() async throws {
        let log = RequestLog()
        let provider = ManagedOllamaCleanupProvider(
            model: "gemma4:e4b", contextTokens: 8192,
            session: makeStubSession { request in
                if request.url?.path == "/api/show" {
                    return StubReply.json(request, #"{"model_info":{"gemma.context_length":8192}}"#)
                }
                if request.url?.path == "/api/ps" {
                    return StubReply.json(request, #"{"models":[{"name":"gemma4:e4b","context_length":8192}]}"#)
                }
                log.record(request)
                return StubReply.json(request, #"{"message":{"role":"assistant","content":"Cleaned."},"done":true}"#)
            })
        let answer = try await provider.clean(CleanupRequest(transcript: "raw", maxOutputTokens: 32))
        XCTAssertEqual(answer.providerID, "managed-ollama")
        let sent = try XCTUnwrap(log.all.first)
        XCTAssertEqual(sent.url?.absoluteString, "http://127.0.0.1:11434/api/chat")
        let options = try XCTUnwrap(sent.jsonBody["options"] as? [String: Any])
        XCTAssertEqual(options["num_ctx"] as? Int, 8192)
        XCTAssertEqual(options["num_predict"] as? Int, 32)
        XCTAssertEqual(sent.jsonBody["think"] as? Bool, false)
    }

    @MainActor
    func testReadinessChecksTheConfiguredAppAddressAndStartsAtTheChosenSize() async throws {
        for heldContext in [4096, 8192] {
            let log = RequestLog()
            let endpointRead = LockedValue<String>()
            let provider = ManagedOllamaCleanupProvider(
                model: "gemma4:e4b", baseURL: URL(string: "http://localhost:11434/v1")!, contextTokens: 8192,
                readLocalServer: { endpoint in
                    endpointRead.set(endpoint)
                    return LocalServerState(
                        reach: .reached, models: [],
                        loaded: [LocalServerLoadedModel("gemma4:e4b", 0, contextTokens: heldContext)])
                },
                session: makeStubSession { request in
                    if request.url?.path == "/api/show" {
                        return StubReply.json(request, #"{"model_info":{"gemma.context_length":8192}}"#)
                    }
                    if request.url?.path == "/api/ps" {
                        return StubReply.json(request, #"{"models":[{"name":"gemma4:e4b","context_length":8192}]}"#)
                    }
                    log.record(request)
                    return StubReply.json(request, #"{"message":{"role":"assistant","content":"OK"},"done":true}"#)
                })
            let result = try await provider.prepareLocalModel(isCurrent: { true }, onStarting: {})
            XCTAssertEqual(endpointRead.value, "http://localhost:11434/v1")
            XCTAssertEqual(result, heldContext == 8192 ? .resident : .started)
            if heldContext == 8192 {
                XCTAssertTrue(log.all.isEmpty)
            } else {
                let sent = try XCTUnwrap(log.all.first)
                XCTAssertEqual(sent.url?.path, "/api/chat")
                let options = try XCTUnwrap(sent.jsonBody["options"] as? [String: Any])
                XCTAssertEqual(options["num_ctx"] as? Int, 8192)
                XCTAssertEqual(options["num_predict"] as? Int, 1)
            }
        }
    }

    /// An on-device instruct model gets a low temperature, so it edits rather than paraphrases.
    func testOllamaGetsAnOnDeviceChatCompletionOnItsOwnPort() async throws {
        let log = RequestLog()
        let session = makeStubSession { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned.")
        }
        let provider = ManagedOllamaCleanupProvider(
            model: "qwen2.5:3b",
            readLocalServer: { _ in
                LocalServerState(
                    reach: .reached, models: [],
                    loaded: [LocalServerLoadedModel("qwen2.5:3b", 1, contextTokens: 4096)])
            }, session: session)

        let response = try await provider.clean(
            CleanupRequest(transcript: "raw text", writingStylePrompt: "Edit.", maxOutputTokens: 32))

        XCTAssertEqual(response.providerID, "managed-ollama")
        let sent = try XCTUnwrap(log.all.first)
        XCTAssertEqual(sent.url?.absoluteString, "http://127.0.0.1:11434/v1/chat/completions")
        XCTAssertNil(sent.header("Authorization"))
        XCTAssertEqual(
            Set(sent.jsonBody.keys),
            [
                "model", "messages", "temperature", "reasoning_effort", "keep_alive", "stream",
                "max_completion_tokens", "max_tokens",
            ])
        XCTAssertEqual(sent.jsonBody["max_completion_tokens"] as? Int, 32)
        XCTAssertEqual(sent.jsonBody["max_tokens"] as? Int, 32)
        XCTAssertEqual(sent.jsonBody["model"] as? String, "qwen2.5:3b")
        XCTAssertEqual(sent.jsonBody["temperature"] as? Double, CleanupSampling.onDeviceTemperature)
        XCTAssertEqual(sent.jsonBody["reasoning_effort"] as? String, CleanupReasoningEffort.none)
        XCTAssertEqual(sent.jsonBody["keep_alive"] as? String, "\(LocalModelDefaults.keepAliveMinutes)m")
    }
}
