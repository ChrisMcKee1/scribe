import XCTest

@testable import Scribe

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
    private let completionsURL = URL(string: "http://127.0.0.1:9999/v1/chat/completions")!

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
        let body = #"{"error":{"message":"Incorrect API key provided: sk-test.","type":"invalid_request_error","code":"invalid_api_key"}}"#
        let provider = makeProvider(apiKey: "sk-test") { request in StubReply.json(request, status: 401, body) }

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(
            error,
            .rejected(
                status: 401, provider: .openAICompatible,
                reply: CleanupServiceReply(code: "invalid_api_key", message: "Incorrect API key provided: sk-test.")))
        XCTAssertEqual(FailureShape(error).description, "CleanupProviderError.rejected values=401 http=401 service=invalid_api_key")
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
        XCTAssertTrue(FailureShape(error).description.contains("url=cannotConnectToHost"), FailureShape(error).description)
    }

    func testASuccessThatIsNotACompletionIsInvalid() async throws {
        let provider = makeProvider { request in StubReply.json(request, "not json") }

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(error, .invalidResponse(.undecodable))
    }

    func testAnEmptyOrMissingAnswerIsInvalid() async throws {
        let blank = makeProvider { request in StubReply.completion(request, "  \n ") }
        let missing = makeProvider { request in StubReply.json(request, #"{"choices":[{"message":{"content":null}}]}"#) }
        let none = makeProvider { request in StubReply.json(request, #"{"choices":[]}"#) }

        for provider in [blank, missing, none] {
            let error = try await cleanupFailure(of: provider)
            XCTAssertEqual(error, .invalidResponse(.emptyCompletion))
        }
    }
}

final class ManagedOllamaCleanupProviderTests: XCTestCase {
    /// An on-device instruct model gets a low temperature, so it edits rather than paraphrases.
    func testOllamaGetsAnOnDeviceChatCompletionOnItsOwnPort() async throws {
        let log = RequestLog()
        let session = makeStubSession { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned.")
        }
        let provider = ManagedOllamaCleanupProvider(model: "qwen2.5:3b", session: session)

        let response = try await provider.clean(CleanupRequest(transcript: "raw text"))

        XCTAssertEqual(response.providerID, "managed-ollama")
        let sent = try XCTUnwrap(log.all.first)
        XCTAssertEqual(sent.url?.absoluteString, "http://127.0.0.1:11434/v1/chat/completions")
        XCTAssertNil(sent.header("Authorization"))
        XCTAssertEqual(Set(sent.jsonBody.keys), ["model", "messages", "temperature", "stream"])
        XCTAssertEqual(sent.jsonBody["model"] as? String, "qwen2.5:3b")
        XCTAssertEqual(sent.jsonBody["temperature"] as? Double, CleanupSampling.onDeviceTemperature)
    }
}
