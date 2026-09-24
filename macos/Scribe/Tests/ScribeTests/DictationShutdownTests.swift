import Darwin
import XCTest

@testable import Scribe

/// Shutdown in its one safe order: nothing new starts, the pipeline is cancelled, a paste in progress is awaited to
/// completion and puts the user's pasteboard back, a recognizer that ignores SIGTERM is stopped and reaped by
/// `ProcessRunner`, and history is drained, all before `shutDown` returns and the app replies to
/// `applicationShouldTerminate`.
@MainActor
final class DictationShutdownTests: XCTestCase {
    private let usersClipboard = "The user's own clipboard text."

    /// What `shutDown` found when it returned.
    @MainActor
    private final class ShutdownProbe {
        private(set) var isDone = false
        private(set) var pasteboardText: String?
        private(set) var recognizerAlive: Bool?

        func record(pasteboardText: String?, recognizerAlive: Bool) {
            self.pasteboardText = pasteboardText
            self.recognizerAlive = recognizerAlive
            isDone = true
        }
    }

    private func makeScript(_ body: String, in directory: URL) throws -> URL {
        let url = directory.appendingPathComponent("foundry")
        try Data("#!/bin/sh\n\(body)\n".utf8).write(to: url)
        XCTAssertEqual(chmod(url.path(percentEncoded: false), 0o755), 0)
        return url
    }

    /// Dictation A is being pasted and holds in the settle after Command-V; dictation B's recognizer is a real child
    /// that ignores SIGTERM. Quit then waits for both: A's paste finishes and restores the pasteboard, B's recognizer
    /// is killed at the end of its grace period and reaped, and only then does shutdown return. A's text, delivered,
    /// is in history; B's, never produced, is nowhere.
    func testShutdownWaitsForAHeldPasteAndABlockedRecognizerBeforeItReturns() async throws {
        let directory = try makeTemporaryDirectory(label: "shutdown")
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
        let engine = TranscriptionEngine(
            resolver: TranscriptionBackendResolver(
                environment: ["SCRIBE_FOUNDRY_CLI": script.path(percentEncoded: false)],
                searchPath: [],
                whisperCliCandidates: [],
                whisperModelCandidates: []),
            scratch: ScratchAudioDirectory(url: directory.appendingPathComponent("scratch", isDirectory: true)),
            killGracePeriod: .milliseconds(300))
        let injection = InjectionHarness()
        defer { injection.releasePasteboard() }
        injection.copyAsAnotherApplication(usersClipboard)
        injection.pacer.holdNext = .afterPaste
        let harness = DictationHarness(injector: injection.injector)
        harness.transcriber.steps = [.text("first dictation"), .engine(engine)]

        await harness.dictate()
        await waitUntil("A's paste holds after Command-V") { injection.pacer.isHolding }
        XCTAssertEqual(injection.pasteboard.string(forType: .string), "first dictation")

        await harness.dictate()
        let recognizerStarted = await FileGate.waitForFile(at: ready, timeout: .seconds(30))
        XCTAssertTrue(recognizerStarted)
        let pid = try XCTUnwrap(
            pid_t(try String(contentsOf: pidFile, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))

        let probe = ShutdownProbe()
        let shutdown = Task { @MainActor in
            let report = await harness.controller.shutDown()
            probe.record(
                pasteboardText: injection.pasteboard.string(forType: .string),
                recognizerAlive: ProcessResources.isAlive(pid))
            return report
        }

        // Nothing moves shutdown past a paste in progress: it waits for the delivery to finish, however long.
        await drainMainActor(yields: 500)
        XCTAssertFalse(probe.isDone, "shutdown returned while a paste was still in progress")
        XCTAssertTrue(injection.pacer.isHolding)
        XCTAssertFalse(harness.controller.hotkeyPressed(DictationHarness.holdKey), "a press was admitted at quit")

        injection.pacer.releaseHeldPause()
        let returned = await finishes(within: 30) { _ = await shutdown.value }
        XCTAssertTrue(returned, "shutdown did not return once the paste finished")
        guard returned else { return }
        let report = await shutdown.value

        XCTAssertTrue(probe.isDone)
        XCTAssertEqual(probe.pasteboardText, usersClipboard, "shutdown returned before the pasteboard was put back")
        XCTAssertEqual(probe.recognizerAlive, false, "shutdown returned while the recognizer still ran")
        XCTAssertTrue(ProcessResources.waitForExit(of: pid, timeout: .seconds(10)))
        XCTAssertEqual(report.cancelledDictations, 2)
        XCTAssertFalse(report.discardedRecording)
        XCTAssertTrue(report.history.drained)
        XCTAssertTrue(harness.history.isCompleted)
        XCTAssertEqual(harness.history.records.map(\.transcriptText), ["first dictation"])
        XCTAssertEqual(harness.recovery.recent(), ["first dictation"])
        XCTAssertEqual(injection.system.posted.map(\.keystroke), [.paste])
        XCTAssertFalse(harness.activity.isActive)
    }

    /// The recognizer can hand back a transcript it had already produced after the cancellation arrived. The
    /// dictation checks its own cancellation before using it: no cleanup request, no recovery entry, no delivery, no
    /// history.
    func testATranscriptThatArrivesAfterTheCancellationIsNeverUsed() async throws {
        let harness = DictationHarness()
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        let late = DictationGate<String>()
        harness.transcriber.steps = [.gateIgnoringCancellation(late)]

        await harness.dictate()
        await waitUntil("the recognizer runs") { late.waitingCount == 1 }
        let shutdown = Task { @MainActor in await harness.controller.shutDown() }
        await waitUntil("shutdown began") { harness.controller.isClosing }
        late.open("words the recognizer finished anyway")
        _ = await shutdown.value

        XCTAssertTrue(provider.requests.isEmpty)
        XCTAssertTrue(harness.recovery.recent().isEmpty)
        XCTAssertTrue(harness.fakeInjector.deliveries.isEmpty)
        XCTAssertTrue(harness.history.records.isEmpty)
        XCTAssertNil(harness.reports.latest)
        XCTAssertTrue(harness.notifier.notices.isEmpty)
        XCTAssertFalse(harness.activity.isActive)
    }

    /// A dictation waiting for startup's first rule load does not hold up the quit: that wait is cancellable here,
    /// although `StartupGate` itself is not, and the gate may never open.
    func testShutdownDoesNotWaitForARuleLoadThatNeverFinishes() async {
        let harness = DictationHarness(rulesLoaded: false)

        await harness.dictate()
        await waitUntil("the dictation waits for the rules") { harness.gate.waitingCount == 1 }
        let report = await harness.controller.shutDown()

        XCTAssertEqual(report.cancelledDictations, 1)
        XCTAssertTrue(harness.fakeInjector.deliveries.isEmpty)
        XCTAssertTrue(harness.history.records.isEmpty)
        XCTAssertEqual(harness.controller.processingCount, 0)
        XCTAssertFalse(harness.activity.isActive)
    }

    /// The live recording is discarded (its audio is never processed), its lease ends, and nothing new starts.
    func testShutdownDiscardsTheLiveRecordingAndAdmitsNothingNew() async throws {
        let harness = DictationHarness()
        let id = try await harness.pressAdmitted()
        await harness.waitUntilLive()

        let report = await harness.controller.shutDown()

        XCTAssertTrue(report.discardedRecording)
        XCTAssertEqual(report.cancelledDictations, 0)
        XCTAssertEqual(harness.capture.stopCount(for: id), 1)
        XCTAssertGreaterThanOrEqual(harness.capture.idleWaits, 1)
        XCTAssertEqual(harness.transcriber.calls, 0)
        XCTAssertFalse(harness.activity.isActive)
        XCTAssertFalse(harness.controller.hotkeyPressed(DictationHarness.holdKey))
        harness.controller.toggleMenuDictation()
        XCTAssertNil(harness.controller.currentRecording)
        XCTAssertTrue(harness.history.isCompleted)
    }

    /// A second call, such as a second Quit, waits for the same shutdown instead of starting another.
    func testShutdownRunsOnce() async {
        let harness = DictationHarness()
        await harness.dictate()

        async let first = harness.controller.shutDown()
        async let second = harness.controller.shutDown()
        let reports = await [first, second]

        XCTAssertEqual(reports[0], reports[1])
        XCTAssertEqual(harness.fakeInjector.barrierCalls, 1)
    }
}
