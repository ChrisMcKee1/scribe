import XCTest

@testable import Scribe

/// The HTTP stub every cleanup suite relies on keeps each test's answers to itself and never lets a request through.
final class StubURLProtocolTests: XCTestCase {
    func testEachSessionIsAnsweredByItsOwnHandler() async throws {
        let first = makeStubSession { request in StubReply.json(request, #"{"from":"first"}"#) }
        let second = makeStubSession { request in StubReply.json(request, #"{"from":"second"}"#) }

        async let firstAnswer = first.data(from: URL(string: "https://example.invalid/a")!)
        async let secondAnswer = second.data(from: URL(string: "https://example.invalid/b")!)
        let (firstData, _) = try await firstAnswer
        let (secondData, _) = try await secondAnswer

        XCTAssertEqual(String(decoding: firstData, as: UTF8.self), #"{"from":"first"}"#)
        XCTAssertEqual(String(decoding: secondData, as: UTF8.self), #"{"from":"second"}"#)
    }

    func testARequestWithoutAHandlerFailsInsteadOfReachingTheNetwork() async {
        let before = StubURLProtocol.unroutedRequests
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        configuration.httpAdditionalHeaders = [StubURLProtocol.routeHeader: "no-such-route"]
        let session = URLSession(configuration: configuration)
        defer { session.invalidateAndCancel() }

        do {
            _ = try await session.data(from: URL(string: "https://example.invalid/")!)
            XCTFail("Expected the unrouted request to fail")
        } catch {
            XCTAssertEqual((error as? URLError)?.code, .resourceUnavailable, "\(FailureShape(error))")
        }
        XCTAssertEqual(StubURLProtocol.unroutedRequests, before + 1)
    }

    /// A handler that throws fails its request the way a network error would.
    func testAHandlerCanFailARequest() async {
        let session = makeStubSession { _ in throw URLError(.cannotConnectToHost) }

        do {
            _ = try await session.data(from: URL(string: "https://example.invalid/")!)
            XCTFail("Expected the request to fail")
        } catch {
            XCTAssertEqual((error as? URLError)?.code, .cannotConnectToHost, "\(FailureShape(error))")
        }
    }
}
