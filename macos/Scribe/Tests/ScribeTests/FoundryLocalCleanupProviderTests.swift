import XCTest

@testable import Scribe

final class FoundryLocalStatusTests: XCTestCase {
    func testSetupGuidanceDoesNotRecommendAnArmOnlyInstallToIntelBuilds() {
        let intel = FoundryLocalSetupText.installationHint(appleSilicon: false)
        XCTAssertTrue(intel.contains("requires Apple Silicon"))
        XCTAssertTrue(intel.contains("whisper.cpp"))
        XCTAssertTrue(intel.contains("native arm64 build"))
        XCTAssertFalse(intel.contains("brew install"))
        let native = FoundryLocalSetupText.installationHint(appleSilicon: true)
        XCTAssertTrue(native.contains("brew install microsoft/foundrylocal/foundrylocal"))
        XCTAssertTrue(native.contains("SCRIBE_FOUNDRY_CLI"))
        XCTAssertEqual(CleanupEndpointProblem.foundryLocalNotInstalled.message, FoundryLocalSetupText.missing)
        XCTAssertEqual(
            TranscriptionError.backendMissing(.foundryCliNotFound).errorDescription, FoundryLocalSetupText.missing)
        #if arch(arm64)
            XCTAssertTrue(FoundryLocalSetupText.appleSiliconBuild)
        #else
            XCTAssertFalse(FoundryLocalSetupText.appleSiliconBuild)
        #endif
    }

    func testContextMetadataMustNameTheChatModelAndNeverEnlargesTheBudget() throws {
        for (reported, expected) in [(512, 512), (2048, 2048), (32768, 4096)] {
            let json = #"{"model":{"alias":"qwen","type":"Chat","contextLength":\#(reported)}}"#
            XCTAssertEqual(try FoundryLocalContextSource.capacity(from: Data(json.utf8), model: "qwen"), expected)
        }
        for json in [
            #"{"model":{"alias":"another","type":"Chat","contextLength":4096}}"#,
            #"{"model":{"alias":"qwen","type":"Speech","contextLength":4096}}"#,
            #"{"model":{"alias":"qwen","type":"Chat","contextLength":0}}"#,
            #"{"model":{"alias":"qwen","type":"Chat","contextLength":-1}}"#,
            #"{"model":{"alias":"qwen","type":"Chat","contextLength":null}}"#,
            #"{"model":{"alias":"qwen","type":"Chat","contextLength":"4096"}}"#,
            "{}",
        ] {
            XCTAssertThrowsError(try FoundryLocalContextSource.capacity(from: Data(json.utf8), model: "qwen")) {
                XCTAssertEqual($0 as? CleanupProviderError, .localContextUnknown)
            }
        }
        let exact = #"{"model":{"id":"qwen-gpu:4","type":"Chat","contextLength":2048}}"#
        XCTAssertEqual(try FoundryLocalContextSource.capacity(from: Data(exact.utf8), model: "qwen-gpu:4"), 2048)
    }

    func testContextLookupUsesOnlyTheBoundedMetadataCommand() async throws {
        let directory = try makeTemporaryDirectory(label: "foundry-context")
        let arguments = directory.appendingPathComponent("arguments")
        let script = try makeScript(
            named: "foundry",
            body: """
                printf '%s\\n' "$@" > '\(arguments.path(percentEncoded: false))'
                echo '{"model":{"alias":"qwen","type":"Chat","contextLength":32768}}'
                """)
        let source = FoundryLocalContextSource.live(environment: ["SCRIBE_FOUNDRY_CLI": script.path])
        let context = try await source.lookup("qwen")
        XCTAssertEqual(context, 4096)
        XCTAssertEqual(
            try String(contentsOf: arguments, encoding: .utf8).split(separator: "\n").map(String.init),
            ["model", "info", "qwen", "-o", "json"])
        do {
            _ = try await source.lookup("--help")
            XCTFail("A model cannot become a CLI option")
        } catch {
            XCTAssertEqual(error as? CleanupProviderError, .localContextUnknown)
        }
    }

    func testOnlyUncredentialedLoopbackHTTPAddressesAreLocalEndpoints() throws {
        for text in [
            "http://127.0.0.1:5273", "http://127.10.20.30:5273", "https://localhost:5273/v1",
            "http://[::1]:5273",
        ] {
            XCTAssertTrue(FoundryLocalStatus.isLocalEndpoint(try XCTUnwrap(URL(string: text))), text)
        }
        for text in [
            "https://remote.example/v1", "http://127.attacker.example", "http://192.168.1.2:5273",
            "file:///tmp/service", "http://user:password@localhost:5273", "http://localhost:5273?token=secret",
            "http://localhost:5273#remote", "http://localhost.attacker.example:5273",
        ] {
            let url = try XCTUnwrap(URL(string: text))
            XCTAssertFalse(FoundryLocalStatus.isLocalEndpoint(url), text)
            let json = try JSONSerialization.data(
                withJSONObject: ["service": ["ready": true, "webUrls": [text]]])
            XCTAssertThrowsError(try FoundryLocalStatus.baseURL(fromStatusOutput: json, exitStatus: 0)) {
                XCTAssertEqual(
                    $0 as? CleanupProviderError, .endpointUnavailable(.foundryLocalEndpointNotLocal))
            }
        }
    }

    func testAReadyServiceGivesItsFirstWebURL() throws {
        let json = #"{"service":{"ready":true,"webUrls":["http://127.0.0.1:5273","http://localhost:5273"]}}"#

        let url = try FoundryLocalStatus.baseURL(fromStatusOutput: Data(json.utf8), exitStatus: 0)

        XCTAssertEqual(url.absoluteString, "http://127.0.0.1:5273")
    }

    func testAServiceThatIsNotReadyOrHasNoEndpointIsNotReady() {
        let statuses = [
            #"{"service":{"ready":false,"webUrls":["http://127.0.0.1:5273"]}}"#,
            #"{"service":{"ready":true,"webUrls":[]}}"#,
            #"{"service":{"ready":true}}"#,
            #"{"service":{}}"#,
        ]
        for json in statuses {
            XCTAssertThrowsError(try FoundryLocalStatus.baseURL(fromStatusOutput: Data(json.utf8), exitStatus: 0)) {
                XCTAssertEqual($0 as? CleanupProviderError, .endpointUnavailable(.foundryLocalNotReady), json)
            }
        }
    }

    /// A failed command has no service to describe; a successful one that prints something else is a surprise.
    func testOutputThatIsNotAStatusDependsOnHowTheCommandEnded() {
        XCTAssertThrowsError(try FoundryLocalStatus.baseURL(fromStatusOutput: Data("oops".utf8), exitStatus: 0)) {
            XCTAssertEqual($0 as? CleanupProviderError, .endpointUnavailable(.foundryLocalStatusUnreadable))
        }
        XCTAssertThrowsError(try FoundryLocalStatus.baseURL(fromStatusOutput: Data(), exitStatus: 1)) {
            XCTAssertEqual($0 as? CleanupProviderError, .endpointUnavailable(.foundryLocalNotReady))
        }
    }

    /// End to end with a stand-in `foundry` script run through `ProcessRunner`.
    func testTheLiveLookupRunsFoundryStatusThroughTheProcessRunner() async throws {
        let directory = try makeTemporaryDirectory(label: "foundry")
        let received = directory.appendingPathComponent("arguments")
        let foundry = directory.appendingPathComponent("foundry")
        let script = """
            #!/bin/sh
            printf '%s\\n' "$@" > '\(received.path(percentEncoded: false))'
            echo '{"service":{"ready":true,"webUrls":["http://127.0.0.1:6123"]}}'
            """
        try Data(script.utf8).write(to: foundry)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o755], ofItemAtPath: foundry.path(percentEncoded: false))
        let status = FoundryLocalStatusSource.live(environment: [
            "SCRIBE_FOUNDRY_CLI": foundry.path(percentEncoded: false)
        ])

        let url = try await status.lookup()

        XCTAssertEqual(url.absoluteString, "http://127.0.0.1:6123")
        let arguments = try String(contentsOf: received, encoding: .utf8).split(separator: "\n").map(String.init)
        XCTAssertEqual(arguments, ["status", "-o", "json"])
    }

    func testTheLiveLookupWithoutFoundryIsNotInstalled() async {
        let status = FoundryLocalStatusSource.live(environment: ["SCRIBE_FOUNDRY_CLI": "/nonexistent/foundry"])

        do {
            _ = try await status.lookup()
            XCTFail("Expected Foundry Local to be missing")
        } catch {
            XCTAssertEqual(error as? CleanupProviderError, .endpointUnavailable(.foundryLocalNotInstalled))
        }
    }

    func testALiveLookupThatFailsIsNotReady() async throws {
        let foundry = try makeScript(named: "foundry", body: "echo 'Service is not running' >&2\nexit 1")
        let status = FoundryLocalStatusSource.live(environment: [
            "SCRIBE_FOUNDRY_CLI": foundry.path(percentEncoded: false)
        ])

        do {
            _ = try await status.lookup()
            XCTFail("Expected Foundry Local not to be ready")
        } catch {
            XCTAssertEqual(error as? CleanupProviderError, .endpointUnavailable(.foundryLocalNotReady))
        }
    }
}

final class FoundryLocalCleanupProviderTests: XCTestCase {
    func testCachedFoundryModelPlansAndAnswersABoundedSyntheticRequest() async throws {
        guard ProcessInfo.processInfo.environment["SCRIBE_REAL_FOUNDRY_CLEANUP"] == "1" else {
            throw XCTSkip("Opt in only with qwen2.5-1.5b already cached and loaded in Foundry Local.")
        }
        let provider = FoundryLocalCleanupProvider()
        let planned = try await provider.contextForPlanning()
        XCTAssertGreaterThan(try XCTUnwrap(planned), 0)
        XCTAssertLessThanOrEqual(try XCTUnwrap(planned), ContextBudget.assumedContextTokens)
        let request = CleanupRequest(
            transcript: "Please send the meeting notes tomorrow.",
            writingStylePrompt: "Return only the sentence with spelling and punctuation corrected.",
            maxOutputTokens: 64)
        let answer = try await provider.clean(request)
        XCTAssertFalse(answer.cleanedText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        XCTAssertEqual(answer.providerID, "foundry-local")
    }

    func testReportedContextPlansAndBoundsTheActualWireRequest() async throws {
        let log = RequestLog()
        let provider = FoundryLocalCleanupProvider(
            status: .init(lookup: { URL(string: "http://localhost:5273")! }),
            context: .init(lookup: { _ in 1024 }), residency: .alreadyResident,
            session: makeStubSession { request in
                log.record(request)
                return StubReply.completion(request, "Cleaned.")
            })
        let planned = try await provider.contextForPlanning()
        XCTAssertEqual(planned, 1024)
        XCTAssertTrue(provider.requiresOutputLimit)
        _ = try await provider.clean(
            CleanupRequest(transcript: "hello", writingStylePrompt: "Fix spelling.", maxOutputTokens: 64))
        XCTAssertEqual(log.all.count, 1)
        XCTAssertEqual(log.all.first?.jsonBody["max_completion_tokens"] as? Int, 64)
        let failure = try await cleanupFailure(
            of: provider, CleanupRequest(transcript: String(repeating: "large ", count: 1000), maxOutputTokens: 64))
        XCTAssertEqual(failure, .localRequestTooLarge)
        XCTAssertEqual(log.all.count, 1)
    }

    func testUnknownCapacityNeverSendsAndMissingOutputLimitBecomesBounded() async throws {
        let log = RequestLog()
        let handler: StubURLProtocol.Handler = { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned.")
        }
        let status = FoundryLocalStatusSource { URL(string: "http://localhost:5273")! }
        let unknown = FoundryLocalCleanupProvider(
            status: status, context: .init(lookup: { _ in 0 }), residency: .alreadyResident,
            session: makeStubSession(handler))
        let unknownFailure = try await cleanupFailure(of: unknown)
        XCTAssertEqual(unknownFailure, .localContextUnknown)
        XCTAssertTrue(log.all.isEmpty)
        let bounded = FoundryLocalCleanupProvider(
            status: status, context: .init(lookup: { _ in 4096 }), residency: .alreadyResident,
            session: makeStubSession(handler))
        _ = try await bounded.clean(CleanupRequest(transcript: "hello"))
        XCTAssertEqual(
            log.all.first?.jsonBody["max_completion_tokens"] as? Int,
            ContextBudget.cleanupOutputCeiling("hello"))
    }

    func testCapacityIsReadAgainAtSendRatherThanTrustedFromPlanning() async throws {
        let changed = StubSwitch()
        let log = RequestLog()
        let provider = FoundryLocalCleanupProvider(
            status: .init(lookup: { URL(string: "http://localhost:5273")! }),
            context: .init(lookup: { _ in changed.isOn ? 256 : 4096 }), residency: .alreadyResident,
            session: makeStubSession { request in
                log.record(request)
                return StubReply.completion(request, "never")
            })
        let planned = try await provider.contextForPlanning()
        XCTAssertEqual(planned, 4096)
        changed.turnOn()
        let request = CleanupRequest(transcript: String(repeating: "text ", count: 100), maxOutputTokens: 128)
        let failure = try await cleanupFailure(of: provider, request)
        XCTAssertEqual(failure, .localRequestTooLarge)
        XCTAssertTrue(log.all.isEmpty)
    }

    func testARefreshedEndpointMustPassTheCapacityGuardAgain() async throws {
        let changed = StubSwitch()
        let log = RequestLog()
        let status = FakeFoundryStatus(endpoints: ["http://localhost:5001", "http://localhost:5002"])
        let provider = FoundryLocalCleanupProvider(
            status: status.source, context: .init(lookup: { _ in changed.isOn ? 64 : 4096 }),
            residency: .alreadyResident,
            session: makeStubSession { request in
                log.record(request)
                if log.all.count > 1 {
                    changed.turnOn()
                    throw URLError(.cannotConnectToHost)
                }
                return StubReply.completion(request, "Cleaned.")
            })
        let request = CleanupRequest(transcript: "hello", writingStylePrompt: "Fix spelling.", maxOutputTokens: 64)
        _ = try await provider.clean(request)
        let failure = try await cleanupFailure(of: provider, request)
        XCTAssertEqual(failure, .localRequestTooLarge)
        XCTAssertEqual(status.lookups, 2)
        XCTAssertEqual(log.all.count, 2)
        XCTAssertTrue(log.all.allSatisfy { $0.url?.port == 5001 })
    }

    func testAnInjectedRemoteStatusCannotSendTheTranscript() async throws {
        let log = RequestLog()
        let provider = makeProvider(status: FakeFoundryStatus(endpoints: ["https://remote.example/v1"])) { request in
            log.record(request)
            return StubReply.completion(request, "never")
        }
        let failure = try await cleanupFailure(of: provider)
        XCTAssertEqual(failure, .endpointUnavailable(.foundryLocalEndpointNotLocal))
        XCTAssertTrue(log.all.isEmpty)
    }

    func testAServiceRefreshCannotReplaceLoopbackWithARemoteDestination() async throws {
        let log = RequestLog()
        let moved = StubSwitch()
        let provider = makeProvider(
            status: FakeFoundryStatus(endpoints: ["http://127.0.0.1:5273", "https://remote.example/v1"])
        ) { request in
            log.record(request)
            if moved.isOn { throw URLError(.cannotConnectToHost) }
            return StubReply.completion(request, "Cleaned.")
        }
        _ = try await provider.clean(CleanupRequest(transcript: "first dictation"))
        moved.turnOn()
        let failure = try await cleanupFailure(of: provider)
        XCTAssertEqual(failure, .endpointUnavailable(.foundryLocalEndpointNotLocal))
        XCTAssertEqual(log.all.count, 2)
        XCTAssertTrue(log.all.allSatisfy { $0.host == "127.0.0.1" })
    }

    func testFoundryTransportDropsProxyCookiesAndCacheButKeepsInjectedRoutingAndTimeouts() {
        let original = URLSessionConfiguration.ephemeral
        original.connectionProxyDictionary = ["HTTPEnable": 1, "HTTPProxy": "remote.example", "HTTPPort": 8080]
        original.httpShouldSetCookies = true
        original.urlCache = URLCache(memoryCapacity: 1024, diskCapacity: 1024)
        original.protocolClasses = [StubURLProtocol.self]
        original.timeoutIntervalForResource = 300
        let safe = FoundryLocalCleanupProvider.localConfiguration(original)
        XCTAssertEqual(safe.connectionProxyDictionary?.count, 0)
        XCTAssertFalse(safe.httpShouldSetCookies)
        XCTAssertEqual(safe.httpCookieAcceptPolicy, .never)
        XCTAssertNil(safe.httpCookieStorage)
        XCTAssertNil(safe.urlCache)
        XCTAssertEqual(safe.timeoutIntervalForResource, 300)
        XCTAssertTrue(safe.protocolClasses?.first == StubURLProtocol.self)
    }

    func testTheLocalTransportDelegateRefusesARedirectBeforeItsBodyCanBeForwarded() throws {
        let session = makeStubSession { request in StubReply.completion(request, "never") }
        defer { session.invalidateAndCancel() }
        let initial = try XCTUnwrap(URL(string: "http://127.0.0.1:5273/v1/chat/completions"))
        let destination = try XCTUnwrap(URL(string: "https://remote.example/v1/chat/completions"))
        let task = session.dataTask(with: initial)
        defer { task.cancel() }
        let response = try XCTUnwrap(
            HTTPURLResponse(
                url: initial, statusCode: 307, httpVersion: nil,
                headerFields: ["Location": destination.absoluteString]))
        var called = false
        LocalServerClient.RedirectRefusingURLSessionDelegate().urlSession(
            session, task: task, willPerformHTTPRedirection: response, newRequest: URLRequest(url: destination)
        ) { redirected in
            called = true
            XCTAssertNil(redirected)
        }
        XCTAssertTrue(called)
    }

    private func makeProvider(
        status: FakeFoundryStatus, clock: TestClock = TestClock(), _ handler: @escaping StubURLProtocol.Handler
    ) -> FoundryLocalCleanupProvider {
        FoundryLocalCleanupProvider(
            modelAlias: "qwen2.5-1.5b", status: status.source, context: .init(lookup: { _ in 4096 }),
            residency: .alreadyResident,
            session: makeStubSession(handler),
            now: clock.monotonicNow)
    }

    /// `foundry status` is a process launch: one lookup serves every dictation while the endpoint is fresh.
    func testTheEndpointIsLookedUpOnceForManyDictations() async throws {
        let status = FakeFoundryStatus(endpoints: ["http://127.0.0.1:5001"])
        let log = RequestLog()
        let provider = makeProvider(status: status) { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned.")
        }

        for _ in 0..<3 {
            _ = try await provider.clean(CleanupRequest(transcript: "raw text"))
        }

        XCTAssertEqual(status.lookups, 1)
        XCTAssertEqual(
            log.all.map { $0.url?.absoluteString },
            Array(repeating: "http://127.0.0.1:5001/v1/chat/completions", count: 3))
    }

    func testAnOldEndpointIsLookedUpAgain() async throws {
        let status = FakeFoundryStatus(endpoints: ["http://127.0.0.1:5001", "http://127.0.0.1:5002"])
        let clock = TestClock()
        let log = RequestLog()
        let provider = makeProvider(status: status, clock: clock) { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned.")
        }

        _ = try await provider.clean(CleanupRequest(transcript: "raw text"))
        clock.advance(by: 599)
        _ = try await provider.clean(CleanupRequest(transcript: "raw text"))
        clock.advance(by: 1)
        _ = try await provider.clean(CleanupRequest(transcript: "raw text"))

        XCTAssertEqual(status.lookups, 2)
        XCTAssertEqual(log.all.map { $0.url?.port }, [5001, 5001, 5002])
    }

    /// Foundry Local's service picks a new port when it restarts; the old one refuses the connection. The provider
    /// asks again and sends the request once more, so the dictation still gets cleaned.
    func testAServiceThatMovedIsFoundAgainAndTheRequestSentOnceMore() async throws {
        let status = FakeFoundryStatus(endpoints: ["http://127.0.0.1:5001", "http://127.0.0.1:5002"])
        let moved = StubSwitch()
        let log = RequestLog()
        let provider = makeProvider(status: status) { request in
            log.record(request)
            if moved.isOn, request.url?.port == 5001 {
                throw URLError(.cannotConnectToHost)
            }
            return StubReply.completion(request, "Cleaned at \(request.url?.port ?? 0).")
        }

        let before = try await provider.clean(CleanupRequest(transcript: "raw text"))
        moved.turnOn()
        let after = try await provider.clean(CleanupRequest(transcript: "raw text"))

        XCTAssertEqual(before.cleanedText, "Cleaned at 5001.")
        XCTAssertEqual(after.cleanedText, "Cleaned at 5002.")
        XCTAssertEqual(status.lookups, 2)
        XCTAssertEqual(log.all.map { $0.url?.port }, [5001, 5001, 5002])
    }

    /// Only an endpoint remembered from before may be stale; a refusal right after a lookup is the answer.
    func testARefusalAtAFreshEndpointIsNotRetried() async throws {
        let status = FakeFoundryStatus(endpoints: ["http://127.0.0.1:5001"])
        let provider = makeProvider(status: status) { _ in throw URLError(.cannotConnectToHost) }

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(error, .transport(URLError(.cannotConnectToHost)))
        XCTAssertEqual(status.lookups, 1)
    }

    /// A slow answer came from a server that is there, so it is not a sign the service moved.
    func testATimeoutIsNotMistakenForAMove() async throws {
        let status = FakeFoundryStatus(endpoints: ["http://127.0.0.1:5001"])
        let slow = StubSwitch()
        let provider = makeProvider(status: status) { request in
            if slow.isOn {
                throw URLError(.timedOut)
            }
            return StubReply.completion(request, "Cleaned.")
        }

        _ = try await provider.clean(CleanupRequest(transcript: "raw text"))
        slow.turnOn()
        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(error, .timedOut)
        XCTAssertEqual(status.lookups, 1)
    }

    func testTheRequestIsAnOnDeviceChatCompletion() async throws {
        let status = FakeFoundryStatus(endpoints: ["http://127.0.0.1:5001/v1/"])
        let log = RequestLog()
        let provider = makeProvider(status: status) { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned.")
        }

        let response = try await provider.clean(CleanupRequest(transcript: "raw text"))

        XCTAssertEqual(response.providerID, "foundry-local")
        XCTAssertEqual(response.modelID, "qwen2.5-1.5b")
        let sent = try XCTUnwrap(log.all.first)
        XCTAssertEqual(sent.url?.absoluteString, "http://127.0.0.1:5001/v1/chat/completions")
        XCTAssertNil(sent.header("Authorization"))
        XCTAssertEqual(
            Set(sent.jsonBody.keys), ["model", "messages", "temperature", "stream", "max_completion_tokens"])
        XCTAssertEqual(sent.jsonBody["model"] as? String, "qwen2.5-1.5b")
        XCTAssertEqual(sent.jsonBody["temperature"] as? Double, CleanupSampling.onDeviceTemperature)
    }

    func testAStatusFailureIsTheCleanupFailure() async throws {
        let provider = FoundryLocalCleanupProvider(
            status: FoundryLocalStatusSource { throw CleanupProviderError.endpointUnavailable(.foundryLocalNotReady) },
            session: makeStubSession { request in StubReply.completion(request, "never") })

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(error, .endpointUnavailable(.foundryLocalNotReady))
    }
}
