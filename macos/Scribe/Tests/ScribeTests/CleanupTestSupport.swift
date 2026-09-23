import Darwin
import Foundation
import Security
import XCTest
import os

@testable import Scribe

// Fakes and stubs for the AI cleanup suites. None of them reaches the network, a real `az` or `foundry`, the
// developer's defaults or a production Keychain item, so these suites can run in parallel worker processes.

// MARK: - HTTP

/// Answers the requests of the sessions `makeStubSession` builds, each with the handler of the test that built it.
///
/// A session's requests carry its route in a header its configuration adds, and the handler is looked up in a
/// lock-protected registry, so tests running at the same time never answer each other's requests. A request whose
/// route has no handler fails, and is counted, instead of reaching the network. `URLProtocol` is not `Sendable`, and
/// this subclass adds no state of its own: everything shared lives in the registry.
final class StubURLProtocol: URLProtocol {
    typealias Handler = @Sendable (URLRequest) throws -> (HTTPURLResponse, Data)

    static let routeHeader = "X-Scribe-Test-Route"

    private static let routes = OSAllocatedUnfairLock<[String: Handler]>(initialState: [:])
    private static let unrouted = OSAllocatedUnfairLock<Int>(initialState: 0)

    static func register(_ handler: @escaping Handler) -> String {
        let route = UUID().uuidString
        routes.withLock { $0[route] = handler }
        return route
    }

    static func unregister(_ route: String) {
        _ = routes.withLock { $0.removeValue(forKey: route) }
    }

    /// Requests that arrived without a registered route, since the test process started.
    static var unroutedRequests: Int {
        unrouted.withLock { $0 }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let handler = request.value(forHTTPHeaderField: Self.routeHeader).flatMap { route in
            Self.routes.withLock { $0[route] }
        }
        guard let handler else {
            Self.unrouted.withLock { $0 += 1 }
            client?.urlProtocol(self, didFailWithError: URLError(.resourceUnavailable))
            return
        }
        do {
            let (response, data) = try handler(request)
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch {
            client?.urlProtocol(self, didFailWithError: error)
        }
    }

    override func stopLoading() {}
}

extension XCTestCase {
    /// A session whose every request goes to `handler`, for this test alone. Nothing it sends reaches the network.
    func makeStubSession(_ handler: @escaping StubURLProtocol.Handler) -> URLSession {
        let route = StubURLProtocol.register(handler)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        configuration.httpAdditionalHeaders = [StubURLProtocol.routeHeader: route]
        let session = URLSession(configuration: configuration)
        addTeardownBlock {
            session.invalidateAndCancel()
            StubURLProtocol.unregister(route)
        }
        return session
    }
}

/// What a stub saw of one request, copied inside the handler so the test asserts on it afterwards, on its own thread.
struct RecordedRequest: Sendable {
    let url: URL?
    let method: String?
    let headers: [String: String]
    let body: Data

    init(_ request: URLRequest) {
        url = request.url
        method = request.httpMethod
        headers = request.allHTTPHeaderFields ?? [:]
        body = request.bodyData()
    }

    func header(_ name: String) -> String? {
        headers.first { $0.key.caseInsensitiveCompare(name) == .orderedSame }?.value
    }

    var bodyText: String {
        String(decoding: body, as: UTF8.self)
    }

    /// The body as a JSON object, or an empty one.
    var jsonBody: [String: Any] {
        ((try? JSONSerialization.jsonObject(with: body)) as? [String: Any]) ?? [:]
    }

    /// The system and user message contents, in order.
    var messageContents: [String] {
        ((jsonBody["messages"] as? [[String: Any]]) ?? []).compactMap { $0["content"] as? String }
    }

    var host: String? {
        url?.host(percentEncoded: false)
    }

    var path: String? {
        url?.path(percentEncoded: false)
    }
}

/// Every request a stub saw, in order.
final class RequestLog: Sendable {
    private let requests = OSAllocatedUnfairLock<[RecordedRequest]>(initialState: [])

    func record(_ request: URLRequest) {
        let recorded = RecordedRequest(request)
        requests.withLock { $0.append(recorded) }
    }

    var all: [RecordedRequest] {
        requests.withLock { $0 }
    }

    var count: Int {
        requests.withLock { $0.count }
    }

    func count(host: String) -> Int {
        all.filter { $0.host == host }.count
    }
}

extension URLRequest {
    /// The body as sent. URLSession hands a protocol the body as a stream rather than as `httpBody`.
    func bodyData() -> Data {
        if let httpBody {
            return httpBody
        }
        guard let stream = httpBodyStream else { return Data() }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let read = stream.read(&buffer, maxLength: buffer.count)
            guard read > 0 else { break }
            data.append(buffer, count: read)
        }
        return data
    }
}

/// Answers for stub handlers.
enum StubReply {
    static func json(_ request: URLRequest, status: Int = 200, _ body: String) -> (HTTPURLResponse, Data) {
        let response = HTTPURLResponse(
            url: request.url ?? URL(fileURLWithPath: "/"), statusCode: status, httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": "application/json"])!
        return (response, Data(body.utf8))
    }

    /// A chat completion whose answer is `content`.
    static func completion(_ request: URLRequest, _ content: String) -> (HTTPURLResponse, Data) {
        let object: [String: Any] = ["choices": [["message": ["role": "assistant", "content": content]]]]
        let body = (try? JSONSerialization.data(withJSONObject: object)) ?? Data()
        return json(request, String(decoding: body, as: UTF8.self))
    }

    /// An Entra token response.
    static func entraToken(_ request: URLRequest, _ token: String, expiresIn: Int = 3600) -> (HTTPURLResponse, Data) {
        json(request, #"{"token_type":"Bearer","expires_in":\#(expiresIn),"access_token":"\#(token)"}"#)
    }
}

/// Decodes `application/x-www-form-urlencoded` the way a server does: `+` is a space, then percent escapes.
enum FormDecoding {
    static func fields(_ body: Data) -> [String: String] {
        var fields: [String: String] = [:]
        for pair in String(decoding: body, as: UTF8.self).split(separator: "&", omittingEmptySubsequences: false) {
            let parts = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            let name = decode(String(parts[0]))
            fields[name] = parts.count > 1 ? decode(String(parts[1])) : ""
        }
        return fields
    }

    private static func decode(_ text: String) -> String {
        text.replacingOccurrences(of: "+", with: " ").removingPercentEncoding ?? "<invalid percent encoding>"
    }
}

// MARK: - Secrets and settings

/// A `SecretStore` in memory, which counts its reads and writes and can be told to fail the next one.
final class InMemorySecretStore: SecretStore {
    private struct State: Sendable {
        var secrets: [String: String]
        var reads = 0
        var writes = 0
        var failNextRead: OSStatus?
        var failNextWrite: OSStatus?
    }

    private let state: OSAllocatedUnfairLock<State>

    init(_ secrets: [String: String] = [:]) {
        state = OSAllocatedUnfairLock(initialState: State(secrets: secrets))
    }

    func secret(for account: String) throws -> String? {
        let result = state.withLock { current -> Result<String?, KeychainStore.KeychainError> in
            current.reads += 1
            if let status = current.failNextRead {
                current.failNextRead = nil
                return .failure(.unhandled(status))
            }
            return .success(current.secrets[account])
        }
        return try result.get()
    }

    func save(_ secret: String, for account: String) throws {
        try write { $0[account] = secret }
    }

    func removeSecret(for account: String) throws {
        try write { $0[account] = nil }
    }

    var secrets: [String: String] {
        state.withLock { $0.secrets }
    }

    var reads: Int {
        state.withLock { $0.reads }
    }

    var writes: Int {
        state.withLock { $0.writes }
    }

    func failNextRead(with status: OSStatus) {
        state.withLock { $0.failNextRead = status }
    }

    func failNextWrite(with status: OSStatus) {
        state.withLock { $0.failNextWrite = status }
    }

    private func write(_ change: @escaping @Sendable (inout [String: String]) -> Void) throws {
        let failure = state.withLock { current -> OSStatus? in
            if let status = current.failNextWrite {
                current.failNextWrite = nil
                return status
            }
            current.writes += 1
            change(&current.secrets)
            return nil
        }
        if let failure {
            throw KeychainStore.KeychainError.unhandled(failure)
        }
    }
}

/// A cleanup settings store over a defaults suite and secret stores of this test's own.
struct CleanupStoreFixture {
    let store: CleanupSettingsStore
    let defaults: UserDefaults
    let apiKeys: InMemorySecretStore
    let clientSecrets: InMemorySecretStore
}

extension XCTestCase {
    func makeCleanupStore(
        apiKeys: InMemorySecretStore = InMemorySecretStore(),
        clientSecrets: InMemorySecretStore = InMemorySecretStore()
    ) -> CleanupStoreFixture {
        let isolated = makeIsolatedDefaults(label: "cleanup")
        return CleanupStoreFixture(
            store: CleanupSettingsStore(
                domain: .suite(isolated.suiteName), apiKeys: apiKeys, clientSecrets: clientSecrets),
            defaults: isolated.defaults,
            apiKeys: apiKeys,
            clientSecrets: clientSecrets)
    }

    /// An executable shell script called `name`, alone in a directory of this test's own.
    func makeScript(named name: String, body: String) throws -> URL {
        let file = try makeTemporaryDirectory(label: "tools").appendingPathComponent(name)
        try Data(("#!/bin/sh\n" + body + "\n").utf8).write(to: file)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: file.path(percentEncoded: false))
        return file
    }
}

// MARK: - Clocks and processes

/// Wall-clock and elapsed time that a test moves by hand.
final class TestClock: Sendable {
    private struct State: Sendable {
        var date: Date
        var instant: ContinuousClock.Instant
    }

    private let state: OSAllocatedUnfairLock<State>

    init(date: Date = Date(timeIntervalSince1970: 1_800_000_000)) {
        state = OSAllocatedUnfairLock(initialState: State(date: date, instant: ContinuousClock.now))
    }

    var date: Date {
        state.withLock { $0.date }
    }

    var instant: ContinuousClock.Instant {
        state.withLock { $0.instant }
    }

    func advance(by seconds: TimeInterval) {
        state.withLock { current in
            current.date = current.date.addingTimeInterval(seconds)
            current.instant = current.instant.advanced(by: .seconds(seconds))
        }
    }

    var now: @Sendable () -> Date {
        { self.date }
    }

    var monotonicNow: @Sendable () -> ContinuousClock.Instant {
        { self.instant }
    }
}

extension ProcessRunner.CapturedOutput {
    static func text(_ text: String) -> ProcessRunner.CapturedOutput {
        ProcessRunner.CapturedOutput(data: Data(text.utf8), totalByteCount: text.utf8.count, reachedEndOfFile: true)
    }
}

extension ProcessRunner.Outcome {
    static func exited(_ status: Int32, standardOutput: String = "", standardError: String = "") -> ProcessRunner.Outcome {
        ProcessRunner.Outcome(
            terminationReason: .finished, exitStatus: status, terminationSignal: nil,
            standardOutput: .text(standardOutput), standardError: .text(standardError), duration: .milliseconds(3))
    }

    static func stopped(_ reason: ProcessRunner.TerminationReason) -> ProcessRunner.Outcome {
        ProcessRunner.Outcome(
            terminationReason: reason, exitStatus: nil, terminationSignal: SIGTERM,
            standardOutput: .text(""), standardError: .text(""), duration: .milliseconds(3))
    }

    /// What `az account get-access-token --output json` prints.
    static func azToken(_ token: String, expiresOn epoch: Int) -> ProcessRunner.Outcome {
        exited(0, standardOutput: #"{"accessToken":"\#(token)","expires_on":\#(epoch),"tokenType":"Bearer"}"#)
    }
}

/// Stands in for `az`: records every command, answers with the outcomes a test queued (the last one again once the
/// queue runs out), and holds each launch at `gate`, when there is one, until the test opens it.
final class FakeAzureCli: Sendable {
    private struct State: Sendable {
        var commands: [AzureCliCommand] = []
        var outcomes: [ProcessRunner.Outcome]
        var running = 0
        var mostRunningAtOnce = 0
    }

    private let state: OSAllocatedUnfairLock<State>
    private let gate: SettingsTestGate?

    init(outcomes: [ProcessRunner.Outcome], gate: SettingsTestGate? = nil) {
        precondition(!outcomes.isEmpty)
        state = OSAllocatedUnfairLock(initialState: State(outcomes: outcomes))
        self.gate = gate
    }

    var launch: AzureCliCredentialProvider.Launch {
        { command in try await self.run(command) }
    }

    var commands: [AzureCliCommand] {
        state.withLock { $0.commands }
    }

    var launches: Int {
        state.withLock { $0.commands.count }
    }

    var mostRunningAtOnce: Int {
        state.withLock { $0.mostRunningAtOnce }
    }

    private func run(_ command: AzureCliCommand) async throws -> ProcessRunner.Outcome {
        state.withLock { current in
            current.commands.append(command)
            current.running += 1
            current.mostRunningAtOnce = max(current.mostRunningAtOnce, current.running)
        }
        if let gate {
            await gate.pass()
        }
        return state.withLock { current -> ProcessRunner.Outcome in
            current.running -= 1
            return current.outcomes.count > 1 ? current.outcomes.removeFirst() : current.outcomes[0]
        }
    }
}

/// Stands in for `foundry status`: counts lookups and answers with the endpoints a test gave, in turn (the last one
/// again once the list runs out).
final class FakeFoundryStatus: Sendable {
    private struct State: Sendable {
        var endpoints: [URL]
        var lookups = 0
    }

    private let state: OSAllocatedUnfairLock<State>

    init(endpoints: [String]) {
        precondition(!endpoints.isEmpty)
        state = OSAllocatedUnfairLock(initialState: State(endpoints: endpoints.map { URL(string: $0)! }))
    }

    var source: FoundryLocalStatusSource {
        FoundryLocalStatusSource { self.next() }
    }

    var lookups: Int {
        state.withLock { $0.lookups }
    }

    private func next() -> URL {
        state.withLock { current -> URL in
            current.lookups += 1
            return current.endpoints.count > 1 ? current.endpoints.removeFirst() : current.endpoints[0]
        }
    }
}

/// An Azure credential that answers with a fixed token or failure and records the scopes it was asked for.
final class RecordingCredential: AzureCredentialProvider {
    private let scopes = OSAllocatedUnfairLock<[String]>(initialState: [])
    private let result: Result<AzureAccessToken, AzureCredentialError>

    init(token: String = "entra-token") {
        result = .success(AzureAccessToken(token: token, expiresAt: .distantFuture))
    }

    init(failure: AzureCredentialError) {
        result = .failure(failure)
    }

    func accessToken(scope: String) async throws -> AzureAccessToken {
        scopes.withLock { $0.append(scope) }
        return try result.get()
    }

    var requestedScopes: [String] {
        scopes.withLock { $0 }
    }
}

/// A flag handlers read and tests flip.
final class StubSwitch: Sendable {
    private let value = OSAllocatedUnfairLock<Bool>(initialState: false)

    var isOn: Bool {
        value.withLock { $0 }
    }

    func turnOn() {
        value.withLock { $0 = true }
    }
}

struct UnexpectedCleanupSuccess: Error {}

/// The `CleanupProviderError` a cleanup fails with. A success, or an error of another type, fails the test.
func cleanupFailure(
    of provider: some CleanupProvider, _ request: CleanupRequest = CleanupRequest(transcript: "raw text")
) async throws -> CleanupProviderError {
    do {
        _ = try await provider.clean(request)
    } catch let error as CleanupProviderError {
        return error
    }
    throw UnexpectedCleanupSuccess()
}

/// Whether `operation` finishes within `seconds`. A deadline for a test to fail on rather than hang when the code under
/// test is broken, never a way to order its steps: every wait it bounds already has an exact condition to wait for.
/// An operation that never finishes is left suspended, and the test goes on to fail its assertions.
func finishes(within seconds: Double, _ operation: @escaping @Sendable () async -> Void) async -> Bool {
    let outcome = FirstOutcome()
    let work = Task {
        await operation()
        outcome.settle(true)
    }
    let deadline = Task {
        try? await Task.sleep(for: .seconds(seconds))
        outcome.settle(false)
    }
    let finished = await outcome.value
    if finished {
        deadline.cancel()
    } else {
        work.cancel()
    }
    return finished
}

extension XCTestCase {
    /// Waits for `operation`, and fails the test instead of hanging when it has not finished within `seconds`.
    func waitBounded(
        _ description: String,
        within seconds: Double = 30,
        file: StaticString = #filePath,
        line: UInt = #line,
        _ operation: @escaping @Sendable () async -> Void
    ) async {
        let finished = await finishes(within: seconds, operation)
        XCTAssertTrue(finished, "Timed out waiting for \(description)", file: file, line: line)
    }
}

/// Keeps the first of the answers it is given and hands it to its one reader.
final class FirstOutcome: Sendable {
    private struct State: Sendable {
        var answer: Bool?
        var reader: CheckedContinuation<Bool, Never>?
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    func settle(_ answer: Bool) {
        let reader = state.withLock { current -> CheckedContinuation<Bool, Never>? in
            guard current.answer == nil else { return nil }
            current.answer = answer
            defer { current.reader = nil }
            return current.reader
        }
        reader?.resume(returning: answer)
    }

    var value: Bool {
        get async {
            await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
                let answer = state.withLock { current -> Bool? in
                    if let answer = current.answer {
                        return answer
                    }
                    current.reader = continuation
                    return nil
                }
                if let answer {
                    continuation.resume(returning: answer)
                }
            }
        }
    }
}

extension CleanupProviderFactory {
    /// A factory that reaches nothing outside the test: requests go to `session`, `az` is `azureCli` (a launch that
    /// fails as not found when there is none), `foundry status` is `foundryStatus` and time is `clock`.
    static func testing(
        session: URLSession,
        foundryStatus: FoundryLocalStatusSource = FoundryLocalStatusSource {
            throw CleanupProviderError.endpointUnavailable(.foundryLocalNotInstalled)
        },
        azureCli: FakeAzureCli? = nil,
        azureCliSearchPath: [String] = [],
        lane: AsyncLane = AsyncLane(),
        clock: TestClock = TestClock()
    ) -> CleanupProviderFactory {
        let missing: AzureCliCredentialProvider.Launch = { _ in throw ProcessRunnerError.launchFailed(errno: ENOENT) }
        let launch = azureCli?.launch ?? missing
        return CleanupProviderFactory(
            session: session,
            foundryLocalStatus: foundryStatus,
            azureCliSearchPath: azureCliSearchPath,
            azureCliLane: lane,
            azureCliLaunch: launch,
            now: clock.now,
            monotonicNow: clock.monotonicNow)
    }
}
