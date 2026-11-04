import Foundation
import XCTest
import os

@testable import Scribe

/// Plays a script for each request of the sessions `makeScriptedSession` builds: an answer's status, its lines one at a
/// time, then an end, a failure or a hold. A hold leaves the request open, as an Ollama still downloading does, until
/// `URLSession` stops it, which calls the hold's `stopped`. Like `StubURLProtocol`, every request carries its test's
/// route in a header, so tests running at the same time never answer each other's requests, and a request with no
/// route fails instead of reaching the network.
final class ScriptedPullProtocol: URLProtocol {
    enum Step: Sendable {
        case respond(Int)
        case send(String)
        case finish
        case fail(URLError)
        case hold(stopped: @Sendable () -> Void)
    }

    typealias Script = @Sendable (URLRequest) -> [Step]

    static let routeHeader = "X-Scribe-Pull-Route"

    private static let routes = OSAllocatedUnfairLock<[String: Script]>(initialState: [:])
    private static let held = OSAllocatedUnfairLock<[ObjectIdentifier: @Sendable () -> Void]>(initialState: [:])

    static func register(_ script: @escaping Script) -> String {
        let route = UUID().uuidString
        routes.withLock { $0[route] = script }
        return route
    }

    static func unregister(_ route: String) {
        _ = routes.withLock { $0.removeValue(forKey: route) }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let script = request.value(forHTTPHeaderField: Self.routeHeader).flatMap { route in
            Self.routes.withLock { $0[route] }
        }
        guard let script else {
            client?.urlProtocol(self, didFailWithError: URLError(.resourceUnavailable))
            return
        }
        for step in script(request) {
            switch step {
            case .respond(let status):
                let response = HTTPURLResponse(
                    url: request.url ?? URL(fileURLWithPath: "/"), statusCode: status, httpVersion: "HTTP/1.1",
                    headerFields: ["Content-Type": "application/x-ndjson"])!
                client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            case .send(let text):
                client?.urlProtocol(self, didLoad: Data(text.utf8))
            case .finish:
                client?.urlProtocolDidFinishLoading(self)
                return
            case .fail(let error):
                client?.urlProtocol(self, didFailWithError: error)
                return
            case .hold(let stopped):
                let key = ObjectIdentifier(self)
                Self.held.withLock { $0[key] = stopped }
                return
            }
        }
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {
        let key = ObjectIdentifier(self)
        let stopped = Self.held.withLock { $0.removeValue(forKey: key) }
        stopped?()
    }
}

/// The progress a download reported, in order.
final class ProgressLog: Sendable {
    private let values = OSAllocatedUnfairLock<[OllamaDownloadProgress]>(initialState: [])

    func record(_ value: OllamaDownloadProgress) {
        values.withLock { $0.append(value) }
    }

    var all: [OllamaDownloadProgress] {
        values.withLock { $0 }
    }

    var reportedSuccess: Bool {
        all.contains { $0.stage == .completed }
    }
}

/// A successful answer with `lines`, each ended by a line break.
func pullLines(_ lines: String...) -> [ScriptedPullProtocol.Step] {
    [.respond(200)] + lines.map { .send($0 + "\n") } + [.finish]
}

/// Runs a download and returns what it threw, or `nil` when it finished.
func downloadFailure(
    of downloader: OllamaModelDownloader,
    _ model: String,
    from endpoint: String = LocalAiServer.ollamaAddress,
    progress: ProgressLog = ProgressLog()
) async -> (any Error)? {
    do {
        try await downloader.download(model, from: endpoint) { progress.record($0) }
        return nil
    } catch {
        return error
    }
}

final class OllamaModelDownloaderTests: XCTestCase {
    private let missingModel =
        "Ollama couldn't find that model and tag. Copy the exact name and tag from the catalog's run command; some "
        + "publishers don't offer latest."
    private let couldNotFinish =
        "Ollama couldn't finish the download. Check that it's open and you have enough disk space, then try again."

    private func makeDownloader(_ script: @escaping ScriptedPullProtocol.Script) -> OllamaModelDownloader {
        let route = ScriptedPullProtocol.register(script)
        let configuration = OllamaModelDownloader.makeConfiguration()
        configuration.protocolClasses = [ScriptedPullProtocol.self]
        configuration.httpAdditionalHeaders = [ScriptedPullProtocol.routeHeader: route]
        addTeardownBlock { ScriptedPullProtocol.unregister(route) }
        return OllamaModelDownloader(configuration: configuration)
    }

    func testDownloadsThroughOllamasPullToOllamaOnlyAndReportsItsFileProgress() async throws {
        let cases = [
            (" gemma4:12b ", "gemma4:12b"),
            ("deepseek-r1", "deepseek-r1"),
            ("VicRodger27/Writex:4b", "VicRodger27/Writex:4b"),
            ("ollama run VicRodger27/Writex:4b", "VicRodger27/Writex:4b"),
        ]
        for (input, model) in cases {
            let log = RequestLog()
            let downloader = makeDownloader { request in
                log.record(request)
                return pullLines(
                    #"{"status":"pulling manifest"}"#,
                    #"{"status":"pulling sha256:abc","digest":"sha256:abc","total":100,"completed":40}"#,
                    #"{"status":"verifying sha256 digest"}"#,
                    #"{"status":"writing manifest"}"#,
                    #"{"status":"success"}"#)
            }
            let progress = ProgressLog()

            try await downloader.download(input) { progress.record($0) }

            XCTAssertEqual(log.count, 1, input)
            let sent = try XCTUnwrap(log.all.first)
            XCTAssertEqual(sent.method, "POST")
            XCTAssertEqual(sent.host, "localhost")
            XCTAssertEqual(sent.url?.port, 11434)
            XCTAssertEqual(sent.path, "/api/pull")
            XCTAssertNil(sent.header("Authorization"))
            XCTAssertEqual(sent.jsonBody["model"] as? String, model)
            XCTAssertEqual(sent.jsonBody["stream"] as? Bool, true)
            XCTAssertEqual(Set(sent.jsonBody.keys), ["model", "stream"], "Only the model name is sent")
            XCTAssertEqual(
                progress.all.map(\.stage), [.preparing, .downloading, .verifying, .installing, .completed], input)
            let file = progress.all[1]
            XCTAssertEqual(file.completedBytes, 40)
            XCTAssertEqual(file.totalBytes, 100)
            XCTAssertEqual(file.percent, 40)
            XCTAssertNil(progress.all[0].percent)
        }
    }

    func testTheSavedOllamaAddressIsUsedAndOnlyOnce() async throws {
        let log = RequestLog()
        let downloader = makeDownloader { request in
            log.record(request)
            return pullLines(#"{"status":"success"}"#)
        }

        try await downloader.download("gemma4", from: "http://127.0.0.1:11434/v1/")

        XCTAssertEqual(log.all.map(\.host), ["127.0.0.1"])
        XCTAssertEqual(log.all.first?.url?.port, 11434)
    }

    func testInvalidOrCloudNamesSendNothing() async {
        for model in [
            "", "https://ollama.com/library/gemma4", "gemma4:cloud", "gemma4:31b-cloud", "gemma4:12b; stop",
            "ollama run gemma4:cloud", "ollama run VicRodger27/Writex:4b --verbose",
        ] {
            let log = RequestLog()
            let downloader = makeDownloader { request in
                log.record(request)
                return pullLines(#"{"status":"success"}"#)
            }

            let error = await downloadFailure(of: downloader, model)

            XCTAssertEqual(error as? OllamaDownloadError, .invalidName, model)
            XCTAssertEqual(log.count, 0, model)
        }
    }

    func testAnAddressThatIsNotOllamasOwnSendsNothing() async {
        for endpoint in [
            "http://localhost:1234/v1", "https://example.com/v1", "http://192.168.1.20:11434/v1",
            "http://localhost:11434", "",
        ] {
            let log = RequestLog()
            let downloader = makeDownloader { request in
                log.record(request)
                return pullLines(#"{"status":"success"}"#)
            }

            let error = await downloadFailure(of: downloader, "gemma4", from: endpoint)

            XCTAssertEqual(error as? OllamaDownloadError, .notOllama, endpoint)
            XCTAssertEqual(log.count, 0, endpoint)
        }
    }

    func testAStreamEndingWithoutSuccessIsNotACompletedDownload() async {
        let downloader = makeDownloader { _ in
            pullLines(
                #"{"status":"pulling sha256:abc","digest":"sha256:abc","total":100,"completed":100}"#,
                #"{"status":"verifying sha256 digest"}"#)
        }
        let progress = ProgressLog()

        let error = await downloadFailure(of: downloader, "gemma4:12b", progress: progress)

        XCTAssertEqual(error as? OllamaDownloadError, .notConfirmed)
        XCTAssertFalse(progress.reportedSuccess)
        XCTAssertEqual(OllamaModelDownload.failure(error!), couldNotFinish)
    }

    func testAStreamErrorOrAnAnswerThatIsNotProgressIsNotACompletedDownload() async {
        for answer in [#"{"error":"private failure"}"#, "not json", "[1, 2]", #""success""#] {
            let downloader = makeDownloader { _ in pullLines(answer, #"{"status":"success"}"#) }
            let progress = ProgressLog()

            let error = await downloadFailure(of: downloader, "gemma4:12b", progress: progress)

            XCTAssertNotNil(error, answer)
            XCTAssertFalse(progress.reportedSuccess, answer)
            XCTAssertEqual(OllamaModelDownload.failure(error!), couldNotFinish, answer)
        }
    }

    func testHTTPErrorsReportAnExplicitSafeFailureAndNeverSuccess() async {
        let cases: [(Int, String)] = [
            (400, couldNotFinish),
            (500, couldNotFinish),
            (404, missingModel),
            (401, "Ollama couldn't access that model. Check it in the catalog and sign in to Ollama if needed."),
            (403, "Ollama couldn't access that model. Check it in the catalog and sign in to Ollama if needed."),
        ]
        for (status, notice) in cases {
            let downloader = makeDownloader { _ in
                [.respond(status), .send(#"{"error":"synthetic private registry message"}"#), .finish]
            }
            let progress = ProgressLog()

            let error = await downloadFailure(of: downloader, "gemma4:12b", progress: progress)

            XCTAssertEqual(error as? OllamaDownloadError, .httpStatus(status))
            XCTAssertFalse(progress.reportedSuccess)
            XCTAssertEqual(OllamaModelDownload.failure(error!), notice, "\(status)")
        }
    }

    func testAMissingManifestInASuccessfulStreamNamesTheModelTagProblem() async {
        let downloader = makeDownloader { _ in
            pullLines(#"{"status":"pulling manifest"}"#, #"{"error":"pull model manifest: file does not exist"}"#)
        }
        let progress = ProgressLog()

        let error = await downloadFailure(of: downloader, "VicRodger27/Writex", progress: progress)

        XCTAssertEqual(error as? OllamaDownloadError, .serverError(missingManifest: true))
        XCTAssertFalse(progress.reportedSuccess)
        XCTAssertEqual(OllamaModelDownload.failure(error!), missingModel)
    }

    func testArbitraryStreamErrorTextIsNeverUsedToGuessAMissingTag() async {
        for message in [
            "private server text: pull model manifest: file does not exist",
            "pull model manifest: file does not exist, private server text",
            "unknown model or private access",
        ] {
            let body = try! JSONSerialization.data(withJSONObject: ["error": message])
            let downloader = makeDownloader { _ in pullLines(String(decoding: body, as: UTF8.self)) }

            let error = await downloadFailure(of: downloader, "gemma4", progress: ProgressLog())

            XCTAssertEqual(error as? OllamaDownloadError, .serverError(missingManifest: false), message)
            XCTAssertEqual(OllamaModelDownload.failure(error!), couldNotFinish, message)
        }
    }

    func testCancellationReachesTheRequestAndNeverReportsSuccess() async {
        let stopped = expectation(description: "the request was stopped")
        let arrived = expectation(description: "the request arrived")
        let downloader = makeDownloader { _ in
            arrived.fulfill()
            return [.hold(stopped: { stopped.fulfill() })]
        }
        let progress = ProgressLog()
        let download = Task { await downloadFailure(of: downloader, "gemma4:12b", progress: progress) }

        await fulfillment(of: [arrived], timeout: 5)
        download.cancel()

        let error = await download.value
        XCTAssertTrue(error is CancellationError, "\(String(describing: error))")
        await fulfillment(of: [stopped], timeout: 5)
        XCTAssertFalse(progress.reportedSuccess)
    }

    func testCancellationBeforeAdmissionSendsNothing() async {
        let log = RequestLog()
        let downloader = makeDownloader { request in
            log.record(request)
            return pullLines(#"{"status":"success"}"#)
        }

        let download = Task { () -> (any Error)? in
            withUnsafeCurrentTask { $0?.cancel() }
            return await downloadFailure(of: downloader, "gemma4:12b")
        }
        let error = await download.value

        XCTAssertTrue(error is CancellationError, "\(String(describing: error))")
        XCTAssertEqual(log.count, 0)
    }

    func testCancellationAlsoStopsReadingADownloadAfterItsHeadersAndFirstProgress() async {
        let stopped = expectation(description: "the read was stopped")
        let firstProgress = expectation(description: "the first progress arrived")
        let downloader = makeDownloader { _ in
            [
                .respond(200),
                .send(#"{"status":"pulling sha256:abc","digest":"sha256:abc","total":100,"completed":1}"# + "\n"),
                .hold(stopped: { stopped.fulfill() }),
            ]
        }
        let progress = ProgressLog()
        let download = Task { () -> (any Error)? in
            do {
                try await downloader.download("gemma4:12b") { value in
                    progress.record(value)
                    if progress.all.count == 1 {
                        firstProgress.fulfill()
                    }
                }
                return nil
            } catch {
                return error
            }
        }

        await fulfillment(of: [firstProgress], timeout: 5)
        download.cancel()

        let error = await download.value
        XCTAssertTrue(error is CancellationError, "\(String(describing: error))")
        await fulfillment(of: [stopped], timeout: 5)
        XCTAssertEqual(progress.all.map(\.stage), [.downloading])
    }

    func testOneDownloaderRunsOneDownloadAtATime() async {
        let arrived = expectation(description: "the first request arrived")
        let log = RequestLog()
        let downloader = makeDownloader { request in
            log.record(request)
            arrived.fulfill()
            return [.hold(stopped: {})]
        }
        let first = Task { await downloadFailure(of: downloader, "gemma4:12b") }
        await fulfillment(of: [arrived], timeout: 5)

        let second = await downloadFailure(of: downloader, "deepseek-r1")

        XCTAssertEqual(second as? OllamaDownloadError, .alreadyDownloading)
        XCTAssertEqual(log.count, 1)
        first.cancel()
        _ = await first.value
    }

    func testARedirectIsNotFollowedAndNothingLeavesThisMac() async {
        let log = RequestLog()
        let downloader = makeDownloader { request in
            log.record(request)
            return [.respond(307), .finish]
        }

        let error = await downloadFailure(of: downloader, "gemma4")

        XCTAssertEqual(error as? OllamaDownloadError, .httpStatus(307))
        XCTAssertEqual(log.all.map(\.host), ["localhost"])
        let configuration = OllamaModelDownloader.makeConfiguration()
        XCTAssertEqual(configuration.connectionProxyDictionary?.count ?? 0, 0)
        XCTAssertFalse(configuration.httpShouldSetCookies)
        XCTAssertNil(configuration.urlCache)
        XCTAssertEqual(configuration.timeoutIntervalForResource, OllamaModelDownloader.overallTimeout)
        XCTAssertEqual(configuration.timeoutIntervalForRequest, OllamaModelDownloader.overallTimeout)
    }

    func testAnOllamaThatIsNotRunningOrTimesOutSaysSo() async {
        let cases: [(URLError.Code, OllamaDownloadError, String)] = [
            (
                .cannotConnectToHost, .unreachable,
                "Scribe can't reach Ollama. Open the Ollama app, then choose Download again."
            ),
            (.timedOut, .timedOut, "The download timed out. Choose Download to resume it."),
            (.badServerResponse, .interrupted, couldNotFinish),
        ]
        for (code, expected, notice) in cases {
            let downloader = makeDownloader { _ in [.fail(URLError(code))] }

            let error = await downloadFailure(of: downloader, "gemma4")

            XCTAssertEqual(error as? OllamaDownloadError, expected)
            XCTAssertEqual(OllamaModelDownload.failure(error!), notice)
        }
    }

    func testTheLogNamesTheOutcomeButNeverTheModel() async throws {
        let recorder = recordScribeLog()
        let downloader = makeDownloader { _ in
            pullLines(#"{"status":"pulling manifest"}"#, #"{"error":"canary private registry text"}"#)
        }

        _ = await downloadFailure(of: downloader, "VicRodger27/Canary-Writex:4b")
        let completed = makeDownloader { _ in pullLines(#"{"status":"success"}"#) }
        try await completed.download("canary-model")

        let text = recorder.publicText + "\n" + recorder.renderings.compactMap(\.privateText).joined()
        PrivacyCanary.assertAbsent(from: text)
        XCTAssertFalse(text.contains("Writex"))
        XCTAssertTrue(text.contains("Ollama model download finished"))
        XCTAssertTrue(text.contains("serverError"))
        XCTAssertTrue(text.contains("completed"))
    }

    func testProgressLinesAreReadAsOllamaWritesThem() throws {
        func parse(_ line: String) throws -> OllamaDownloadProgress? {
            try OllamaModelDownloader.parse(Array(line.utf8))
        }

        XCTAssertNil(try parse("   \r"))
        XCTAssertEqual(try parse(#"{"status":"success"}"#)?.stage, .completed)
        XCTAssertEqual(try parse(#"{"status":"removing any unused layers"}"#)?.stage, .installing)
        XCTAssertEqual(try parse(#"{"status":"Success"}"#)?.stage, .preparing, "Ollama's words are matched exactly")
        XCTAssertEqual(try parse(#"{}"#)?.stage, .preparing)
        XCTAssertEqual(try parse(#"{"error":null,"status":"success"}"#)?.stage, .completed)
        let odd = try XCTUnwrap(parse(#"{"status":"pulling x","total":true,"completed":-5}"#))
        XCTAssertEqual(odd.stage, .preparing)
        XCTAssertEqual(odd.completedBytes, 0)
        XCTAssertNil(odd.percent)
        XCTAssertThrowsError(try parse(#"{"error":""}"#))
        XCTAssertThrowsError(try parse(#"{"error":{"code":1}}"#))
    }

    func testAnEndlessLineIsRefusedRatherThanKept() async {
        let long = String(repeating: "a", count: OllamaModelDownloader.maximumLineBytes + 1)
        let downloader = makeDownloader { _ in [.respond(200), .send(long), .finish] }

        let error = await downloadFailure(of: downloader, "gemma4")

        XCTAssertEqual(error as? OllamaDownloadError, .unreadableAnswer)
    }
}
