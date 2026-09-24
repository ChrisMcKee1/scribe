import XCTest

@testable import Scribe

final class FoundryLocalStatusTests: XCTestCase {
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
    private func makeProvider(
        status: FakeFoundryStatus, clock: TestClock = TestClock(), _ handler: @escaping StubURLProtocol.Handler
    ) -> FoundryLocalCleanupProvider {
        FoundryLocalCleanupProvider(
            modelAlias: "qwen2.5-1.5b", status: status.source, session: makeStubSession(handler),
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
        XCTAssertEqual(Set(sent.jsonBody.keys), ["model", "messages", "temperature", "stream"])
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
