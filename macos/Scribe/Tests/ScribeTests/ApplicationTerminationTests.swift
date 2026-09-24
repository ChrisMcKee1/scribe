import AppKit
import Darwin
import XCTest
import os

@testable import Scribe

/// Quit in its one order (`ApplicationTermination`), with provider work that runs outside dictation
/// (`AuxiliaryOperations`): Settings' Test Connection and the Usage Insights summary can start `az` or `foundry`, and
/// Quit cancels that work and waits for it before it replies.
@MainActor
final class ApplicationTerminationTests: XCTestCase {
    private func makeScript(_ body: String, in directory: URL) throws -> URL {
        let url = directory.appendingPathComponent("probe")
        try Data("#!/bin/sh\n\(body)\n".utf8).write(to: url)
        XCTAssertEqual(chmod(url.path(percentEncoded: false), 0o755), 0)
        return url
    }

    /// No dictation is running; Settings' Test Connection is, and its child ignores SIGTERM. Quit cancels the check,
    /// `ProcessRunner` kills the child at the end of its grace period and reaps it, and only then does the one reply
    /// go out. A second Quit meanwhile changes nothing, and a check asked for after Quit starts no child.
    func testQuitWaitsForATestConnectionWhoseChildIgnoresSIGTERM() async throws {
        let directory = try makeTemporaryDirectory(label: "quit-probe")
        let ready = directory.appendingPathComponent("ready")
        let pidFile = directory.appendingPathComponent("pid")
        // Ignores SIGTERM, so ProcessRunner has to escalate; `exec sleep 30` ends it by itself if everything fails.
        let script = try makeScript(
            """
            trap '' TERM
            echo $$ > '\(pidFile.path(percentEncoded: false))'
            : > '\(ready.path(percentEncoded: false))'
            exec sleep 30
            """, in: directory)
        let operations = AuxiliaryOperations()
        let backing = CleanupSettingsBackingFake()
        backing.stored.isEnabled = true
        var access = backing.access
        let starts = SendableCounter()
        access.checkConnection = {
            starts.increment()
            do {
                let outcome = try await ProcessRunner.run(
                    script, timeout: .seconds(60), killGracePeriod: .milliseconds(300))
                return CleanupConnectionCheck(reachable: false, message: "stopped: \(outcome.terminationReason)")
            } catch {
                return CleanupConnectionCheck(reachable: false, message: "did not start")
            }
        }
        let model = CleanupSettingsModel(
            access: access, drafts: backing.drafts, center: backing.center, operations: operations)

        let check = Task { @MainActor in await model.testConnection() }
        let started = await FileGate.waitForFile(at: ready, timeout: .seconds(30))
        XCTAssertTrue(started)
        let pid = try XCTUnwrap(
            pid_t(try String(contentsOf: pidFile, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))
        XCTAssertEqual(operations.runningCount, 1)

        let harness = makeHarness()
        let replies = Collected<Bool>()
        let stops = Collected<Int>()
        let termination = ApplicationTermination(
            work: ApplicationTermination.Work(
                stopListening: { stops.values.append(1) },
                operations: operations,
                dictation: harness.controller,
                stopMaintenance: nil,
                removeScratchAudio: {}),
            reply: { replies.values.append(ProcessResources.isAlive(pid)) })

        XCTAssertEqual(termination.request(), .terminateLater)
        XCTAssertEqual(termination.request(), .terminateLater)
        let replied = await finishes(within: 30) { await termination.waitUntilReplied() }
        XCTAssertTrue(replied, "Quit never replied")

        XCTAssertEqual(replies.values, [false], "not exactly one reply, or one while the child still ran")
        XCTAssertEqual(stops.values.count, 1)
        XCTAssertTrue(ProcessResources.waitForExit(of: pid, timeout: .seconds(10)))
        XCTAssertTrue(operations.isClosed)
        XCTAssertEqual(operations.runningCount, 0)
        _ = await bounded("the Test Connection that was cancelled") { await check.value }
        XCTAssertFalse(model.isTesting)

        let refused: Void? = await bounded("a Test Connection asked for after Quit", within: 10) {
            await model.testConnection()
        }
        XCTAssertTrue(refused != nil, "the refused check did not return")
        XCTAssertEqual(starts.value, 1, "a check after Quit started a child")
        XCTAssertEqual(model.errorMessage?.contains("quitting"), true)
    }

    /// The Usage Insights summary goes through the same barrier: Quit cancels it and waits for it to return, and a
    /// summary asked for afterwards is refused without running.
    func testQuitCancelsAUsageSummaryInFlightAndWaitsForIt() async {
        let operations = AuxiliaryOperations()
        let entered = AudioTestSignalLatch()
        let calls = SendableCounter()
        let model = UsageSummaryModel(
            readCleanupEnabled: { true },
            summarize: { _ in
                calls.increment()
                entered.signal()
                try await Task.sleep(for: .seconds(30))
                return "never"
            },
            center: NotificationCenter(),
            operations: operations)

        model.generate(payload: "totals only")
        let summarizing = await entered.wait()
        XCTAssertTrue(summarizing)
        XCTAssertEqual(operations.runningCount, 1)

        operations.beginClosing()
        let finished = await finishes(within: 10) { await operations.waitUntilFinished() }
        XCTAssertTrue(finished, "the cancelled summary was not waited for")
        await waitUntil("the model hears the summary ended") { !model.isGenerating }
        XCTAssertEqual(operations.runningCount, 0)

        model.generate(payload: "totals only")
        await waitUntil("the refused summary is reported") { model.errorMessage == "Scribe is quitting." }
        XCTAssertEqual(calls.value, 1, "a summary after Quit ran")
    }

    /// A Quit requested again while shutdown runs, or after it replied, schedules nothing more: every request answers
    /// `.terminateLater`, the work runs once, and exactly one reply follows, after dictation has shut down.
    func testARepeatedQuitRequestSchedulesNothingMoreAndOneReplyFollows() async {
        let harness = makeHarness()
        let replies = Collected<DictationController.ShutdownProgress>()
        let stops = Collected<Int>()
        let termination = ApplicationTermination(
            work: ApplicationTermination.Work(
                stopListening: { stops.values.append(1) },
                operations: AuxiliaryOperations(),
                dictation: harness.controller,
                stopMaintenance: nil,
                removeScratchAudio: {}),
            reply: { replies.values.append(harness.controller.shutdownProgress) })

        XCTAssertEqual(termination.request(), .terminateLater)
        XCTAssertTrue(termination.isTerminating)
        XCTAssertEqual(termination.request(), .terminateLater)
        _ = await bounded("the reply") { await termination.waitUntilReplied() }
        XCTAssertEqual(termination.request(), .terminateLater)
        _ = await bounded("a second wait for the reply") { await termination.waitUntilReplied() }

        XCTAssertEqual(replies.values, [.finished])
        XCTAssertEqual(stops.values.count, 1)
        XCTAssertTrue(harness.controller.isClosing)
    }
}

/// A count any thread can bump.
final class SendableCounter: Sendable {
    private let count = OSAllocatedUnfairLock(initialState: 0)

    func increment() {
        count.withLock { $0 += 1 }
    }

    var value: Int {
        count.withLock { $0 }
    }
}
