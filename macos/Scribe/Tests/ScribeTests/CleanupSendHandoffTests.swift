import Foundation
import XCTest
import os

@testable import Scribe

final class CleanupSendHandoffTests: XCTestCase {
    func testAChangeAndChangeBackStillWithdrawTheHandoff() throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        store.openAIModel = "first"
        let handoff = CleanupSendHandoff(store: store)
        store.openAIModel = "second"
        store.openAIModel = "first"
        XCTAssertThrowsError(try handoff.perform { XCTFail("a withdrawn admission must not send") }) {
            XCTAssertEqual($0 as? CleanupSendHandoff.Refusal, .settingsChanged)
        }
    }

    func testCopiesOfTheStoreShareTheSameWriteBoundary() throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        let copy = store
        let handoff = CleanupSendHandoff(store: store)
        copy.isEnabled = false
        XCTAssertThrowsError(try handoff.perform {})
    }

    func testOtherDefaultsDomainsDoNotWithdrawTheHandoff() throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        let other = makeCleanupStore().store
        let handoff = CleanupSendHandoff(store: store)
        other.isEnabled = true
        XCTAssertEqual(try handoff.perform { 42 }, 42)
    }

    func testSecretChangesBlockAnAdmissionCapturedDuringTheWrite() throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        CleanupSettingsHandoff.shared.beginSecretChange(store.domain)
        let handoff = CleanupSendHandoff(store: store)
        XCTAssertThrowsError(try handoff.perform {})
        CleanupSettingsHandoff.shared.endSecretChange(store.domain)
        XCTAssertThrowsError(try handoff.perform {})
        XCTAssertNoThrow(try CleanupSendHandoff(store: store).perform {})
    }

    func testSettingsCannotChangeBetweenTheCheckAndTheSendStart() async throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        store.openAIModel = "first"
        let handoff = CleanupSendHandoff(store: store)
        let writerStarted = DispatchSemaphore(value: 0)
        let events = OSAllocatedUnfairLock(initialState: [String]())
        let writerDone = expectation(description: "write finished")
        try handoff.perform {
            DispatchQueue.global().async {
                writerStarted.signal()
                store.openAIModel = "second"
                events.withLock { $0.append("write") }
                writerDone.fulfill()
            }
            XCTAssertEqual(writerStarted.wait(timeout: .now() + 5), .success)
            XCTAssertEqual(store.openAIModel, "first")
            events.withLock { $0.append("start") }
        }
        await fulfillment(of: [writerDone], timeout: 5)
        XCTAssertEqual(events.withLock { $0 }, ["start", "write"])
    }

    func testWithdrawnRequestDoesNotReachTheTransport() async throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        let calls = OSAllocatedUnfairLock(initialState: 0)
        let session = makeStubSession { request in
            calls.withLock { $0 += 1 }
            return (HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!, Data())
        }
        let handoff = CleanupSendHandoff(store: store)
        store.isEnabled = false
        do {
            _ = try await CleanupSendHandoff.$current.withValue(handoff) {
                try await CleanupSendHandoff.data(
                    for: URLRequest(url: URL(string: "https://example.test")!), session: session)
            }
            XCTFail("a withdrawn request must fail")
        } catch {
            XCTAssertEqual(error as? CleanupSendHandoff.Refusal, .settingsChanged)
        }
        XCTAssertEqual(calls.withLock { $0 }, 0)
    }

    func testBoundRequestReturnsTheRealResponse() async throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        let session = makeStubSession { request in
            (
                HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!,
                Data("answer".utf8)
            )
        }
        let (data, _) = try await CleanupSendHandoff.$current.withValue(CleanupSendHandoff(store: store)) {
            try await CleanupSendHandoff.data(
                for: URLRequest(url: URL(string: "https://example.test")!), session: session)
        }
        XCTAssertEqual(data, Data("answer".utf8))
    }

    func testLocalPlainRetryCannotSendAfterTheSettingsChange() async throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        let calls = OSAllocatedUnfairLock(initialState: 0)
        let session = makeStubSession { request in
            calls.withLock { $0 += 1 }
            store.isEnabled = false
            return (HTTPURLResponse(url: request.url!, statusCode: 400, httpVersion: nil, headerFields: nil)!, Data())
        }
        let provider = OpenAICompatibleCleanupProvider(
            model: "test", serviceURL: URL(string: "http://localhost:1234/v1")!, session: session)
        do {
            _ = try await CleanupSendHandoff.$current.withValue(CleanupSendHandoff(store: store)) {
                try await provider.clean(CleanupRequest(transcript: "test", writingStylePrompt: "test"))
            }
            XCTFail("a changed configuration must refuse the retry")
        } catch {
            XCTAssertEqual(error as? CleanupSendHandoff.Refusal, .settingsChanged)
        }
        XCTAssertEqual(calls.withLock { $0 }, 1)
    }

    func testCancellingABoundRequestStopsItsUnderlyingTask() async throws {
        let store = makeCleanupStore().store
        store.isEnabled = true
        let started = expectation(description: "request started")
        let stopped = expectation(description: "request stopped")
        let session = makeStubSession { _ in
            started.fulfill()
            throw StubURLProtocol.Hold { stopped.fulfill() }
        }
        let task = Task {
            try await CleanupSendHandoff.$current.withValue(CleanupSendHandoff(store: store)) {
                try await CleanupSendHandoff.data(
                    for: URLRequest(url: URL(string: "https://example.test")!), session: session)
            }
        }
        await fulfillment(of: [started], timeout: 5)
        task.cancel()
        do {
            _ = try await task.value
            XCTFail("a cancelled request must throw")
        } catch {
            XCTAssertTrue(error is CancellationError || (error as? URLError)?.code == .cancelled)
        }
        await fulfillment(of: [stopped], timeout: 5)
    }
}
