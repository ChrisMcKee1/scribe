import XCTest

@testable import Scribe

/// Test Connection's requests, one step at a time, against a provider whose every answer the test scripts: how many
/// requests are made, which carry the output ceiling, what counts as connected, and that a cancellation between two
/// requests stops the second.
final class CleanupProbeTests: XCTestCase {
    private let lengthStop = CleanupProviderError.invalidResponse(.outputLimitReachedBeforeText)
    private let refusal = CleanupProviderError.rejected(status: 400, provider: .openAICompatible, reply: .empty)

    private func probe(_ provider: ScriptedCleanupProvider) async throws -> CleanupProviderCache.ProbeAnswer {
        try await CleanupProviderCache.probe(provider, kind: .openAICompatible)
    }

    private func ceilings(_ provider: ScriptedCleanupProvider) -> [Int?] {
        provider.requests.map(\.maxOutputTokens)
    }

    func testTextUnderTheCeilingIsConnectedAtOnce() async throws {
        let provider = ScriptedCleanupProvider([{ _ in "Ok." }])

        let answer = try await probe(provider)

        XCTAssertEqual(answer, .text)
        XCTAssertEqual(ceilings(provider), [16])
    }

    /// A `length` stop under the ceiling proves nothing by itself. The request without one decides: text is
    /// connected, and a second `length` stop, which is what a server that stops every request sends, is a failure.
    func testALengthStopUnderTheCeilingIsDecidedByOneRequestWithout() async throws {
        let lengthStop = self.lengthStop
        let answers = ScriptedCleanupProvider([{ _ in throw lengthStop }, { _ in "Ok." }])
        let stopsEveryRequest = ScriptedCleanupProvider([{ _ in throw lengthStop }, { _ in throw lengthStop }])

        let answer = try await probe(answers)
        XCTAssertEqual(answer, .textAfterOutputLimit)
        XCTAssertEqual(ceilings(answers), [16, nil])

        do {
            _ = try await probe(stopsEveryRequest)
            XCTFail("A server that stops every request at length must fail the check")
        } catch {
            XCTAssertEqual(error as? CleanupProviderError, lengthStop)
        }
        XCTAssertEqual(ceilings(stopsEveryRequest), [16, nil])
    }

    /// A 400 or 422 to the ceiling is retried once without it; that retry is the last request, so its own `length`
    /// stop, or its own refusal, is the verdict, and there is never a third request.
    func testARefusedCeilingIsRetriedOnceAndTheRetryDecides() async throws {
        let lengthStop = self.lengthStop
        let refusal = self.refusal
        let unprocessable = CleanupProviderError.rejected(status: 422, provider: .openAICompatible, reply: .empty)
        let passes = ScriptedCleanupProvider([{ _ in throw unprocessable }, { _ in "Ok." }])
        let stopsAtLength = ScriptedCleanupProvider([{ _ in throw refusal }, { _ in throw lengthStop }])
        let refusesAgain = ScriptedCleanupProvider([{ _ in throw refusal }, { _ in throw refusal }, { _ in "Ok." }])

        let answer = try await probe(passes)
        XCTAssertEqual(answer, .textWithoutOutputLimit)
        XCTAssertEqual(ceilings(passes), [16, nil])

        for (provider, expected) in [(stopsAtLength, lengthStop), (refusesAgain, refusal)] {
            do {
                _ = try await probe(provider)
                XCTFail("The retry's failure must be the verdict")
            } catch {
                XCTAssertEqual(error as? CleanupProviderError, expected)
            }
            XCTAssertEqual(ceilings(provider), [16, nil], "never a third request")
        }
    }

    /// Only a 400 or 422 is a refusal of the ceiling: anything else is the answer, with no second request.
    func testOtherFailuresAreNotRetried() async throws {
        for status in [401, 403, 404, 429, 500] {
            let failure = CleanupProviderError.rejected(status: status, provider: .openAICompatible, reply: .empty)
            let provider = ScriptedCleanupProvider([{ _ in throw failure }, { _ in "Ok." }])

            do {
                _ = try await probe(provider)
                XCTFail("\(status) must not be retried")
            } catch {
                XCTAssertEqual(error as? CleanupProviderError, failure)
            }
            XCTAssertEqual(ceilings(provider), [16], "\(status)")
        }
    }

    /// A check cancelled while its first request was refused, or stopped at the ceiling, sends no second request. Each
    /// probe runs in a task of its own, which its first request cancels, as a Cancel press between two requests would.
    func testACancellationBetweenTwoRequestsStopsTheSecond() async throws {
        for first in [refusal, lengthStop] {
            let provider = ScriptedCleanupProvider([
                { _ in
                    _ = withUnsafeCurrentTask { $0?.cancel() }
                    throw first
                },
                { _ in "Ok." },
            ])
            let probing = Task { () -> (any Error)? in
                do {
                    _ = try await CleanupProviderCache.probe(provider, kind: .openAICompatible)
                    return nil
                } catch {
                    return error
                }
            }

            let error = await probing.value

            XCTAssertTrue(error is CancellationError, "\(error.map { "\(FailureShape($0))" } ?? "no error")")
            XCTAssertEqual(provider.requests.count, 1, "\(FailureShape(first))")
        }
    }

    /// A probe that starts cancelled sends nothing at all.
    func testAProbeThatStartsCancelledSendsNothing() async throws {
        let provider = ScriptedCleanupProvider([{ _ in "Ok." }])
        let probing = Task { () -> Bool in
            _ = withUnsafeCurrentTask { $0?.cancel() }
            do {
                _ = try await CleanupProviderCache.probe(provider, kind: .openAICompatible)
                return false
            } catch {
                return error is CancellationError
            }
        }

        let cancelled = await probing.value

        XCTAssertTrue(cancelled)
        XCTAssertEqual(provider.requests.count, 0)
    }
}
