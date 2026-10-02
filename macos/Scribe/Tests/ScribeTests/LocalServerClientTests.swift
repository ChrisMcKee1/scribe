import Foundation
import XCTest

@testable import Scribe

final class LocalServerClientTests: XCTestCase {
    func testInjectedSessionConfigurationKeepsTestRoutingButRemovesProxiesAndCaching() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpAdditionalHeaders = ["X-Scribe-Test-Route": "isolated"]
        configuration.protocolClasses = [StubURLProtocol.self]
        configuration.connectionProxyDictionary = ["HTTPEnable": 1, "HTTPProxy": "not-local"]
        configuration.httpShouldSetCookies = true
        configuration.urlCache = URLCache(memoryCapacity: 1024, diskCapacity: 0)
        let safe = LocalServerClient.makeConfiguration(configuration)
        XCTAssertEqual(safe.connectionProxyDictionary?.count, 0)
        XCTAssertFalse(safe.httpShouldSetCookies)
        XCTAssertNil(safe.urlCache)
        XCTAssertEqual(safe.httpAdditionalHeaders?["X-Scribe-Test-Route"] as? String, "isolated")
        XCTAssertTrue(safe.protocolClasses?.first == StubURLProtocol.self)
    }

    func testMutatingRequestsUseOnlyTheConfiguredAddressOnce() async {
        let log = RequestLog()
        let client = makeClient { request in
            log.record(request)
            switch request.url?.path {
            case "/api/v1/chat":
                return StubReply.json(request, status: 200, #"{"model_instance_id":"owned-copy"}"#)
            default:
                return StubReply.json(request, status: 200, "{}")
            }
        }
        let instance = await client.loadWithContext(
            "http://localhost:1234/v1", modelID: "m", contextTokens: 8192, apiKey: "local-key")
        XCTAssertEqual(instance, "owned-copy")
        let freed = await client.unloadInstance("http://localhost:1234/v1", instanceID: "owned-copy")
        XCTAssertTrue(freed)
        let unloaded = await client.unload("http://localhost:11434/v1", modelID: "m")
        XCTAssertTrue(unloaded)
        XCTAssertEqual(
            log.all.filter { $0.method == "POST" }.map(\.path),
            ["/api/v1/chat", "/api/v1/models/unload", "/api/generate"])
        XCTAssertTrue(log.all.allSatisfy { $0.host == "localhost" })
    }

    func testARefusedMutationDoesNotTryOtherLoopbackAddresses() async {
        let log = RequestLog()
        let client = makeClient { request in
            log.record(request)
            throw URLError(.cannotConnectToHost)
        }
        let instance = await client.loadWithContext("http://localhost:1234/v1", modelID: "m", contextTokens: 8192)
        XCTAssertNil(instance)
        XCTAssertEqual(log.count, 1)
        XCTAssertEqual(log.all.first?.host, "localhost")
    }

    private func makeClient(_ handler: @escaping StubURLProtocol.Handler) -> LocalServerClient {
        let route = StubURLProtocol.register(handler)
        let configuration = LocalServerClient.makeConfiguration()
        configuration.protocolClasses = [StubURLProtocol.self]
        configuration.httpAdditionalHeaders = [StubURLProtocol.routeHeader: route]
        let client = LocalServerClient(configuration: configuration)
        addTeardownBlock { StubURLProtocol.unregister(route) }
        return client
    }

    func testOnlyAnAppsExactDefaultAddressIsThatApp() {
        let cases: [(String?, LocalServerApp)] = [
            ("http://localhost:11434/v1", .ollama),
            ("http://localhost:11434/v1/", .ollama),
            ("http://127.0.0.1:11434/v1", .ollama),
            ("http://[::1]:11434/v1", .ollama),
            (" http://LOCALHOST:11434/v1 ", .ollama),
            ("http://localhost:1234/v1", .lmStudio),
            ("http://127.0.0.1:1234/v1", .lmStudio),
            ("http://localhost:11434", .none),
            ("http://localhost:11434/v1/chat", .none),
            ("http://localhost:11434/v1?x=1", .none),
            ("https://localhost:11434/v1", .none),
            ("http://localhost:8080/v1", .none),
            ("http://127.0.0.2:11434/v1", .none),
            ("http://ollama.localhost:11434/v1", .none),
            ("http://192.168.1.20:11434/v1", .none),
            ("localhost:11434/v1", .none),
            ("", .none),
            (nil, .none),
        ]

        for (endpoint, expected) in cases {
            XCTAssertEqual(LocalAiServer.appAt(endpoint), expected, endpoint ?? "nil")
        }
    }

    func testSameModelTreatsAnUnqualifiedNameAsLatest() {
        XCTAssertTrue(LocalServerClient.sameModel("gemma4:e2b", "gemma4:e2b"))
        XCTAssertTrue(LocalServerClient.sameModel("gemma3", "gemma3:latest"))
        XCTAssertTrue(LocalServerClient.sameModel("GEMMA3:LATEST", "gemma3"))
        XCTAssertTrue(LocalServerClient.sameModel("library/gemma3", "library/gemma3:latest"))
        XCTAssertFalse(LocalServerClient.sameModel("gemma3", "gemma3:4b"))
        XCTAssertFalse(LocalServerClient.sameModel("", "gemma3"))
        XCTAssertFalse(LocalServerClient.sameModel(nil, nil))
    }

    func testOllamaListsTheModelsItCanChatWithAndWhatEachLoadedOneTakes() async {
        let log = RequestLog()
        let client = makeClient { request in
            log.record(request)
            switch request.url?.path(percentEncoded: false) {
            case "/api/tags":
                return StubReply.json(
                    request,
                    """
                    {"models":[
                      {"name":"qwen3:4b-instruct","size":2500000000,"capabilities":["completion","tools"]},
                      {"name":"nomic-embed-text:latest","size":274000000,"capabilities":["embedding"]},
                      {"name":"gemma4:e2b","size":7200000000,"capabilities":["completion","vision"]},
                      {"name":"old-build:latest","size":1000}
                    ]}
                    """
                )
            case "/api/ps":
                return StubReply.json(
                    request,
                    """
                    {"models":[{"name":"gemma4:e2b","model":"gemma4:e2b","size":1706000000,"context_length":8192}]}
                    """
                )
            default:
                throw URLError(.fileDoesNotExist)
            }
        }

        let state = await client.read("http://localhost:11434/v1")

        XCTAssertEqual(state.reach, .reached)
        XCTAssertEqual(state.models.map(\.id), ["gemma4:e2b", "old-build:latest", "qwen3:4b-instruct"])
        XCTAssertEqual(state.loaded(for: "gemma4:e2b")?.memoryBytes, 1_706_000_000)
        XCTAssertEqual(state.loaded(for: "gemma4:e2b")?.contextTokens, 8_192)
        XCTAssertNil(state.loaded(for: "qwen3:4b-instruct"))
        let paths = log.all.compactMap(\.path)
        XCTAssertTrue(paths.allSatisfy { $0 == "/api/tags" || $0 == "/api/ps" })
        XCTAssertGreaterThanOrEqual(paths.filter { $0 == "/api/tags" }.count, 1)
        XCTAssertGreaterThanOrEqual(paths.filter { $0 == "/api/ps" }.count, 1)
    }

    func testALoadedModelReadThatFailsNeverHidesTheModelsOllamaListed() async {
        let failures: [StubURLProtocol.Handler] = [
            { request in
                if request.url?.path(percentEncoded: false) == "/api/tags" {
                    return StubReply.json(
                        request,
                        """
                        {"models":[{"name":"gemma4:12b","size":8000000000},{"name":"granite4:3b","size":2000000000}]}
                        """
                    )
                }
                throw URLError(.badServerResponse)
            },
            { request in
                if request.url?.path(percentEncoded: false) == "/api/tags" {
                    return StubReply.json(
                        request,
                        """
                        {"models":[{"name":"gemma4:12b","size":8000000000},{"name":"granite4:3b","size":2000000000}]}
                        """
                    )
                }
                return StubReply.json(request, "not json")
            },
            { request in
                if request.url?.path(percentEncoded: false) == "/api/tags" {
                    return StubReply.json(
                        request,
                        """
                        {"models":[{"name":"gemma4:12b","size":8000000000},{"name":"granite4:3b","size":2000000000}]}
                        """
                    )
                }
                return StubReply.json(request, status: 500, "{}")
            },
        ]

        for handler in failures {
            let client = makeClient(handler)
            let state = await client.read("http://localhost:11434/v1")
            XCTAssertEqual(state.reach, .reached)
            XCTAssertEqual(state.models.map(\.id), ["gemma4:12b", "granite4:3b"])
            XCTAssertTrue(state.loaded.isEmpty)
        }
    }

    func testAFailedReadSaysWhatFailedWithoutQuotingTheAnswer() async {
        let client = makeClient { request in
            StubReply.json(request, "private words, not json")
        }

        let state = await client.read("http://localhost:11434/v1")

        XCTAssertEqual(state.reach, .failed)
        XCTAssertFalse((state.failureDetail ?? "").isEmpty)
        XCTAssertFalse((state.failureDetail ?? "").localizedCaseInsensitiveContains("private"))
    }

    func testLMStudioListsOnlyItsChatModelsAndWhichAreLoaded() async {
        let client = makeClient { request in
            StubReply.json(
                request,
                """
                {"models":[
                  {"type":"llm","key":"google/gemma-3-4b","display_name":"Gemma 3 4B","size_bytes":3300000000,
                   "loaded_instances":[{"id":"google/gemma-3-4b","config":{"context_length":8192}}]},
                  {"type":"embedding","key":"text-embedding-nomic","display_name":"Nomic","size_bytes":84000000,
                   "loaded_instances":[]},
                  {"type":"llm","key":"ibm/granite-4-micro","display_name":"Granite 4 Micro",
                   "size_bytes":2099555678,"loaded_instances":[]}
                ]}
                """
            )
        }

        let state = await client.read("http://127.0.0.1:1234/v1")

        XCTAssertEqual(state.reach, .reached)
        XCTAssertEqual(state.models.map(\.id), ["google/gemma-3-4b", "ibm/granite-4-micro"])
        XCTAssertEqual(state.models.first?.displayName, "Gemma 3 4B")
        XCTAssertEqual(state.loaded(for: "google/gemma-3-4b")?.memoryBytes, 3_300_000_000)
        XCTAssertEqual(state.loaded(for: "google/gemma-3-4b")?.contextTokens, 8_192)
        XCTAssertNil(state.loaded(for: "ibm/granite-4-micro"))
    }

    func testNothingIsSentToAnAddressThatIsNotAnAppOnThisMac() async {
        let log = RequestLog()
        let client = makeClient { request in
            log.record(request)
            return StubReply.json(request, "{}")
        }

        let state = await client.read("https://openrouter.ai/api/v1")
        let unloaded = await client.unload("http://192.168.1.20:11434/v1", modelID: "gemma4:e2b")

        XCTAssertEqual(state.reach, .failed)
        XCTAssertFalse(unloaded)
        XCTAssertEqual(log.count, 0)
    }

    func testAnAppThatRefusesScribeWithoutAKeyReadsAsAskingForOne() async {
        let client = makeClient { request in
            StubReply.json(request, status: 401, #"{"error":"Unauthorized"}"#)
        }

        let state = await client.read("http://localhost:1234/v1")

        XCTAssertEqual(state.reach, .needsKey)
    }

    func testTheSavedKeyGoesWithEveryRequestThatNeedsIt() async {
        let log = RequestLog()
        let client = makeClient { request in
            log.record(request)
            return StubReply.json(
                request,
                """
                {"models":[{"type":"llm","key":"google/gemma-4-e2b","loaded_instances":[{"id":"google/gemma-4-e2b"}]}]}
                """
            )
        }

        _ = await client.read("http://localhost:1234/v1", apiKey: "lm-token")

        XCTAssertEqual(log.all.first?.header("Authorization"), "Bearer " + "lm-token")
    }

    func testAnAppListeningOnOnlyOneLoopbackAddressIsReached() async {
        let log = RequestLog()
        let client = makeClient { request in
            log.record(request)
            if request.url?.host(percentEncoded: false) == "127.0.0.1" {
                throw URLError(.cannotConnectToHost)
            }
            if ["::1", "[::1]"].contains(request.url?.host(percentEncoded: false) ?? "") {
                return StubReply.json(request, #"{"models":[{"name":"gemma4:e2b","size":7200000000}]}"#)
            }
            throw URLError(.cannotConnectToHost)
        }

        let state = await client.read("http://localhost:11434/v1")

        XCTAssertEqual(state.reach, .reached)
        XCTAssertEqual(state.models.map(\.id), ["gemma4:e2b"])
        let ipv6Hits = log.all.compactMap(\.host).filter { $0 == "::1" || $0 == "[::1]" }.count
        XCTAssertGreaterThanOrEqual(ipv6Hits, 1)
    }

    func testTheRequestsUseNoProxyAndFollowNoRedirect() async {
        let configuration = LocalServerClient.makeConfiguration()
        XCTAssertEqual(configuration.connectionProxyDictionary?.count ?? 0, 0)

        let log = RequestLog()
        let client = makeClient { request in
            log.record(request)
            let response = HTTPURLResponse(
                url: request.url ?? URL(fileURLWithPath: "/"),
                statusCode: 307,
                httpVersion: "HTTP/1.1",
                headerFields: ["Location": "https://example.com/api/tags"]
            )!
            return (response, Data())
        }

        let state = await client.read("http://localhost:11434/v1")
        let hosts = log.all.map(\.host)
        let onlyLoopback = hosts.allSatisfy { host in
            host == "localhost" || host == "127.0.0.1" || host == "::1"
        }

        XCTAssertEqual(state.reach, .failed)
        XCTAssertTrue(onlyLoopback)
    }
}
