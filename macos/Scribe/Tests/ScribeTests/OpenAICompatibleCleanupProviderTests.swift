import XCTest
@testable import Scribe

/// Stubs URLSession responses so OpenAICompatibleCleanupProvider can be tested without a real
/// network call to Foundry Local/Ollama/etc.
final class StubURLProtocol: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var responseProvider: ((URLRequest) -> (HTTPURLResponse, Data))?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let responseProvider = Self.responseProvider else {
            client?.urlProtocol(self, didFailWithError: URLError(.unknown))
            return
        }
        let (response, data) = responseProvider(request)
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

final class OpenAICompatibleCleanupProviderTests: XCTestCase {
    private func stubbedSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: configuration)
    }

    override func tearDown() {
        StubURLProtocol.responseProvider = nil
        super.tearDown()
    }

    func testCleanReturnsTrimmedCompletionContent() async throws {
        StubURLProtocol.responseProvider = { request in
            XCTAssertEqual(request.url?.path, "/v1/chat/completions")
            let body = """
            {"choices":[{"message":{"content":"  Cleaned sentence.  \\n"}}]}
            """.data(using: .utf8)!
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, body)
        }

        let provider = OpenAICompatibleCleanupProvider(
            id: "test",
            displayName: "Test",
            model: "test-model",
            session: stubbedSession(),
            baseURLProvider: { URL(string: "http://127.0.0.1:9999") })

        let response = try await provider.clean(CleanupRequest(transcript: "raw text"))
        XCTAssertEqual(response.cleanedText, "Cleaned sentence.")
        XCTAssertEqual(response.providerID, "test")
        XCTAssertEqual(response.modelID, "test-model")
    }

    func testCleanThrowsOnNonSuccessStatus() async {
        StubURLProtocol.responseProvider = { request in
            let response = HTTPURLResponse(url: request.url!, statusCode: 500, httpVersion: nil, headerFields: nil)!
            return (response, "server error".data(using: .utf8)!)
        }

        let provider = OpenAICompatibleCleanupProvider(
            id: "test",
            displayName: "Test",
            model: "test-model",
            session: stubbedSession(),
            baseURLProvider: { URL(string: "http://127.0.0.1:9999") })

        do {
            _ = try await provider.clean(CleanupRequest(transcript: "raw text"))
            XCTFail("Expected clean(_:) to throw on HTTP 500")
        } catch let error as CleanupProviderError {
            guard case .requestFailed = error else {
                return XCTFail("Expected .requestFailed, got \(error)")
            }
        } catch {
            XCTFail("Expected CleanupProviderError, got \(error)")
        }
    }

    func testCleanThrowsNotConfiguredWhenBaseURLUnavailable() async {
        let provider = OpenAICompatibleCleanupProvider(
            id: "test",
            displayName: "Test",
            model: "test-model",
            session: stubbedSession(),
            baseURLProvider: { nil })

        do {
            _ = try await provider.clean(CleanupRequest(transcript: "raw text"))
            XCTFail("Expected clean(_:) to throw when base URL is unavailable")
        } catch let error as CleanupProviderError {
            guard case .notConfigured = error else {
                return XCTFail("Expected .notConfigured, got \(error)")
            }
        } catch {
            XCTFail("Expected CleanupProviderError, got \(error)")
        }
    }
}
