import Darwin
import Foundation
import XCTest
import os

@testable import Scribe

/// Runs `TranscriptionEngine` against small shell scripts standing in for `foundry` and `whisper-cli`, so the
/// deadline, cancellation, failure types, backend lookup and scratch cleanup are exercised against real child
/// processes. Nothing waits by sleeping; a script that has to reach a point first creates a `FileGate` file.
/// Every script, and every process a script starts, ends by itself within about half a minute whatever the engine
/// does (an `exec sleep 30`, a `sleep 30` in the background, or a loop of 600 short sleeps), so a regression fails
/// a test instead of hanging the suite, and a test whose script leaves a process behind waits for it to end.
final class TranscriptionEngineTests: XCTestCase {
    private let tone = AudioTestSignal.tone(seconds: 0.5, frequency: 220, amplitude: 0.25, sampleRate: 16_000)

    /// Writes an executable `/bin/sh` script. The engine calls it as `foundry transcribe -m <alias> -f <wav> -o
    /// json`, so the recording's path is `$5`.
    private func writeScript(_ body: String, at url: URL) throws {
        try Data("#!/bin/sh\n\(body)\n".utf8).write(to: url)
        XCTAssertEqual(chmod(url.path(percentEncoded: false), 0o755), 0)
    }

    private func makeScript(_ body: String, in directory: URL, named name: String = "foundry") throws -> URL {
        let url = directory.appendingPathComponent(name)
        try writeScript(body, at: url)
        return url
    }

    private func makeEngine(
        foundry: URL,
        scratch: ScratchAudioDirectory,
        deadlines: TranscriptionDeadlines = TranscriptionDeadlines(),
        resolutionLifetime: Duration = TranscriptionEngine.defaultResolutionLifetime,
        now: @escaping @Sendable () -> ContinuousClock.Instant = { ContinuousClock.now }
    ) -> TranscriptionEngine {
        TranscriptionEngine(
            resolver: TranscriptionBackendResolver(
                environment: ["SCRIBE_FOUNDRY_CLI": foundry.path(percentEncoded: false)],
                searchPath: [],
                whisperCliCandidates: [],
                whisperModelCandidates: []),
            scratch: scratch,
            deadlines: deadlines,
            resolutionLifetime: resolutionLifetime,
            killGracePeriod: .milliseconds(500),
            now: now)
    }

    /// The recordings left in `scratch`, merged from three reads, each through both `FileManager` and `readdir`.
    /// While these tests were proven with mutations, a check like this one passed a few times although the
    /// recording was still in place, so a leak now has to be missed by every read to pass unseen, reads that
    /// disagree are printed, and a directory that cannot be read at all fails the test.
    private func scratchFiles(
        _ scratch: ScratchAudioDirectory, file: StaticString = #filePath, line: UInt = #line
    ) -> [String] {
        var path = scratch.url.path(percentEncoded: false)
        while path.count > 1, path.hasSuffix("/") {
            path.removeLast()
        }
        var found = Set<String>()
        var counts: [Int] = []
        var failedReads = 0
        for _ in 0..<3 {
            var info = stat()
            guard lstat(path, &info) == 0 else {
                if errno == ENOENT {
                    counts.append(0)
                } else {
                    failedReads += 1
                }
                continue
            }
            if let names = try? FileManager.default.contentsOfDirectory(atPath: path) {
                counts.append(names.count)
                found.formUnion(names)
            } else {
                failedReads += 1
            }
            if let handle = opendir(path) {
                var names: [String] = []
                while let entry = readdir(handle) {
                    var raw = entry.pointee.d_name
                    let length = Int(entry.pointee.d_namlen)
                    let name = withUnsafeBytes(of: &raw) { String(decoding: $0.prefix(length), as: UTF8.self) }
                    if name != "." && name != ".." {
                        names.append(name)
                    }
                }
                closedir(handle)
                counts.append(names.count)
                found.formUnion(names)
            } else {
                failedReads += 1
            }
        }
        if Set(counts).count > 1 {
            print("Scratch directory reads disagreed: counts \(counts), line \(line)")
        }
        guard !counts.isEmpty else {
            XCTFail("could not read the scratch directory (\(failedReads) failed reads)", file: file, line: line)
            return ["unreadable"]
        }
        return found.sorted()
    }

    private func transcriptionError(
        _ operation: () async throws -> TranscriptionResult,
        file: StaticString = #filePath,
        line: UInt = #line
    ) async -> TranscriptionError? {
        do {
            _ = try await operation()
            XCTFail("the transcription should have failed", file: file, line: line)
            return nil
        } catch let error as TranscriptionError {
            return error
        } catch {
            XCTFail("unexpected \(error)", file: file, line: line)
            return nil
        }
    }

    // MARK: - Success

    func testTheTranscriptComesBackTrimmedAndTheRecordingIsWrittenPrivatelyThenDeleted() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let evidence = directory.appendingPathComponent("evidence", isDirectory: true)
        try FileManager.default.createDirectory(at: evidence, withIntermediateDirectories: true)
        let evidencePath = evidence.path(percentEncoded: false)
        let script = try makeScript(
            """
            cp "$5" '\(evidencePath)/copy.wav'
            stat -f '%Lp' "$5" > '\(evidencePath)/file-mode'
            stat -f '%Lp' "$(dirname "$5")" > '\(evidencePath)/directory-mode'
            printf '%s' "$3" > '\(evidencePath)/alias'
            printf '{"model":"parakeet","file":"x","text":"  The report is due on Friday.  ","language":null}\\n'
            """, in: directory)
        let scratch = ScratchAudioDirectory(url: directory.appendingPathComponent("scratch", isDirectory: true))
        let engine = makeEngine(foundry: script, scratch: scratch)

        let result = try await engine.transcribe(samples: tone, sampleRate: 16_000)

        XCTAssertEqual(result.text, "The report is due on Friday.")
        XCTAssertEqual(result.backend, .foundryLocal)
        XCTAssertTrue(result.diagnostics.usedColdBudget)
        XCTAssertFalse(result.diagnostics.outputHeldAfterExit)
        XCTAssertEqual(try String(contentsOf: evidence.appendingPathComponent("file-mode"), encoding: .utf8)
            .trimmingCharacters(in: .whitespacesAndNewlines), "600")
        XCTAssertEqual(try String(contentsOf: evidence.appendingPathComponent("directory-mode"), encoding: .utf8)
            .trimmingCharacters(in: .whitespacesAndNewlines), "700")
        XCTAssertEqual(
            try String(contentsOf: evidence.appendingPathComponent("alias"), encoding: .utf8),
            TranscriptionEngine.defaultFoundryModelAlias)

        let wav = try Data(contentsOf: evidence.appendingPathComponent("copy.wav"))
        XCTAssertEqual(
            Array(wav.prefix(44)), ScratchAudioDirectory.wavHeader(sampleCount: tone.count, sampleRate: 16_000))
        let payload: [Float] = wav.dropFirst(44).withUnsafeBytes { raw in
            (0..<(raw.count / 4)).map { raw.loadUnaligned(fromByteOffset: $0 * 4, as: Float.self) }
        }
        XCTAssertEqual(payload, tone)

        XCTAssertTrue(scratchFiles(scratch).isEmpty)
        let freshURL = URL(fileURLWithPath: scratch.url.path(percentEncoded: false), isDirectory: true)
        let excluded = try freshURL.resourceValues(forKeys: [.isExcludedFromBackupKey]).isExcludedFromBackup
        XCTAssertEqual(excluded, true)
    }

    func testProgressLinesBeforeTheReplyAreSkipped() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let script = try makeScript(
            "echo 'Downloading model 42%'; echo '{\"text\":\"hello\"}'", in: directory)
        let engine = makeEngine(
            foundry: script, scratch: ScratchAudioDirectory(url: directory.appendingPathComponent("s")))

        let result = try await engine.transcribe(samples: tone, sampleRate: 16_000)

        XCTAssertEqual(result.text, "hello")
    }

    func testTheColdBudgetAppliesUntilASuccessAndAgainAfterAFailure() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let script = try makeScript("printf '{\"text\":\"one\"}'", in: directory)
        let engine = makeEngine(
            foundry: script, scratch: ScratchAudioDirectory(url: directory.appendingPathComponent("s")))

        let first = try await engine.transcribe(samples: tone, sampleRate: 16_000)
        let second = try await engine.transcribe(samples: tone, sampleRate: 16_000)
        try writeScript("exit 3", at: script)
        let failure = await transcriptionError { try await engine.transcribe(samples: self.tone, sampleRate: 16_000) }
        try writeScript("printf '{\"text\":\"two\"}'", at: script)
        let third = try await engine.transcribe(samples: tone, sampleRate: 16_000)

        XCTAssertTrue(first.diagnostics.usedColdBudget)
        XCTAssertFalse(second.diagnostics.usedColdBudget)
        XCTAssertEqual(failure, .exitCode(3))
        XCTAssertTrue(third.diagnostics.usedColdBudget)
        XCTAssertEqual(first.diagnostics.deadline, Duration.seconds(300) + .seconds(0.5))
        XCTAssertEqual(second.diagnostics.deadline, Duration.seconds(30) + .seconds(0.5))
    }

    /// If a recognizer starts a background process that keeps its output open, every run would pay the drain
    /// limit; the run says so instead of paying it silently. The background process polls 600 times at most and
    /// only while the test's directory exists, and the test opens its gate and waits for it to end.
    func testADescendantHoldingTheOutputIsReportedAndTheTranscriptStillArrives() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let directoryPath = directory.path(percentEncoded: false)
        let gatePath = directory.appendingPathComponent("gate").path(percentEncoded: false)
        let descendantPath = directory.appendingPathComponent("descendant").path(percentEncoded: false)
        defer { FileManager.default.createFile(atPath: gatePath, contents: nil) }
        let script = try makeScript(
            """
            (
              i=0
              while [ $i -lt 600 ] && [ -d '\(directoryPath)' ] && [ ! -e '\(gatePath)' ]; do
                sleep 0.05; i=$((i+1))
              done
            ) &
            echo $! > '\(descendantPath)'
            printf '{"text":"held"}'
            """, in: directory)
        let recorder = recordScribeLog()
        let engine = makeEngine(
            foundry: script, scratch: ScratchAudioDirectory(url: directory.appendingPathComponent("s")))

        let result = try await engine.transcribe(samples: tone, sampleRate: 16_000)
        FileManager.default.createFile(atPath: gatePath, contents: nil)
        let recorded = try String(contentsOfFile: descendantPath, encoding: .utf8)
        let descendant = try XCTUnwrap(pid_t(recorded.trimmingCharacters(in: .whitespacesAndNewlines)))
        XCTAssertTrue(
            ProcessResources.waitForExit(of: descendant, timeout: .seconds(10)), "the descendant outlived the test")

        XCTAssertEqual(result.text, "held")
        XCTAssertTrue(result.diagnostics.outputHeldAfterExit)
        XCTAssertGreaterThanOrEqual(result.diagnostics.duration, ProcessRunner.postExitDrainLimit)
        XCTAssertTrue(recorder.lines.contains { $0.contains("kept its output open") }, "\(recorder.lines)")
    }

    // MARK: - Deadline and cancellation

    func testARecognizerPastItsDeadlineIsStoppedAndItsRecordingDeleted() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let script = try makeScript("exec sleep 30", in: directory)
        let scratch = ScratchAudioDirectory(url: directory.appendingPathComponent("scratch", isDirectory: true))
        let engine = makeEngine(
            foundry: script, scratch: scratch,
            deadlines: TranscriptionDeadlines(
                coldBase: .milliseconds(300), warmBase: .milliseconds(300), perAudioSecond: 0))
        let clock = ContinuousClock()
        let started = clock.now

        let error = await transcriptionError { try await engine.transcribe(samples: self.tone, sampleRate: 16_000) }

        XCTAssertEqual(error, .timedOut)
        XCTAssertLessThan(started.duration(to: clock.now), .seconds(20))
        XCTAssertTrue(scratchFiles(scratch).isEmpty)
    }

    /// Cancelling stops the recognizer's whole process group: the recognizer, which has been reaped when the error
    /// arrives, and a helper it started in the background, which ends too.
    func testCancellingTheTaskStopsTheRecognizerAndDeletesTheRecording() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let ready = directory.appendingPathComponent("ready")
        let pidFile = directory.appendingPathComponent("pid")
        let helperFile = directory.appendingPathComponent("helper")
        let script = try makeScript(
            """
            sleep 30 &
            echo $! > '\(helperFile.path(percentEncoded: false))'
            echo $$ > '\(pidFile.path(percentEncoded: false))'
            : > '\(ready.path(percentEncoded: false))'
            wait
            """, in: directory)
        let scratch = ScratchAudioDirectory(url: directory.appendingPathComponent("scratch", isDirectory: true))
        let engine = makeEngine(foundry: script, scratch: scratch)
        let samples = tone

        let task = Task {
            try await engine.transcribe(samples: samples, sampleRate: 16_000)
        }
        let reachedReady = await FileGate.waitForFile(at: ready, timeout: .seconds(30))
        XCTAssertTrue(reachedReady)
        XCTAssertEqual(scratchFiles(scratch).count, 1)
        task.cancel()

        let error = await transcriptionError { try await task.value }

        XCTAssertEqual(error, .cancelled)
        XCTAssertTrue(scratchFiles(scratch).isEmpty)
        let pid = try XCTUnwrap(
            pid_t(try String(contentsOf: pidFile, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))
        let signalled = kill(pid, 0)
        let code = errno
        XCTAssertEqual(signalled, -1)
        XCTAssertEqual(code, ESRCH)
        let helper = try XCTUnwrap(
            pid_t(try String(contentsOf: helperFile, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)))
        XCTAssertTrue(
            ProcessResources.waitForExit(of: helper, timeout: .seconds(10)), "the helper outlived the cancellation")
    }

    func testATaskCancelledBeforeTheRecognizerStartsWritesNothing() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let script = try makeScript("printf '{\"text\":\"never\"}'", in: directory)
        let scratch = ScratchAudioDirectory(url: directory.appendingPathComponent("scratch", isDirectory: true))
        let engine = makeEngine(foundry: script, scratch: scratch)
        let samples = tone
        let gate = AudioTestSignalLatch()

        let task = Task {
            _ = await gate.wait()
            return try await engine.transcribe(samples: samples, sampleRate: 16_000)
        }
        task.cancel()
        gate.signal()

        let error = await transcriptionError { try await task.value }
        XCTAssertEqual(error, .cancelled)
        XCTAssertTrue(scratchFiles(scratch).isEmpty)
    }

    /// A `ProcessRunner.systemCalls` stand-in whose first read after the recognizer written to `leaderPath` has been
    /// reaped (so the runner has observed its exit) waits on `release` until the test lets it go, after announcing
    /// itself on `held`. The run cannot complete in between, so whatever the test does there happens after the
    /// observed exit and before completion, however slowly the test is scheduled.
    private func readsHeldAfterTheObservedExit(
        of leaderPath: String, held: AudioTestSignalLatch, release: DispatchSemaphore
    ) -> ProcessRunnerSystemCalls {
        let holding = OSAllocatedUnfairLock(initialState: false)
        return ProcessRunnerSystemCalls(
            listProcessGroup: ProcessRunnerSystemCalls.live.listProcessGroup,
            readPipe: { descriptor, buffer, count in
                if !holding.withLock({ $0 }),
                    let recorded = try? String(contentsOfFile: leaderPath, encoding: .utf8),
                    let leader = pid_t(recorded.trimmingCharacters(in: .whitespacesAndNewlines)),
                    kill(leader, 0) == -1, errno == ESRCH,
                    holding.withLock({ claimed -> Bool in
                        defer { claimed = true }
                        return !claimed
                    })
                {
                    held.signal()
                    _ = release.wait(timeout: .now() + .seconds(30))
                }
                return ProcessRunnerSystemCalls.live.readPipe(descriptor, buffer, count)
            })
    }

    /// The recognizer printed its transcript and exited on its own, and a process it left behind still holds its
    /// output open. The runner is held after it has observed that exit and before the run completes, and the
    /// cancellation arrives then: it must not discard the finished transcript, because the lifecycle decides what a
    /// cancelled dictation does with it.
    func testACancellationAfterTheRunnerObservedTheExitKeepsTheTranscript() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let directoryPath = directory.path(percentEncoded: false)
        let gatePath = directory.appendingPathComponent("gate").path(percentEncoded: false)
        let descendantPath = directory.appendingPathComponent("descendant").path(percentEncoded: false)
        let leader = directory.appendingPathComponent("leader")
        let leaderPath = leader.path(percentEncoded: false)
        defer { FileManager.default.createFile(atPath: gatePath, contents: nil) }
        let script = try makeScript(
            """
            (
              i=0
              while [ $i -lt 600 ] && [ -d '\(directoryPath)' ] && [ ! -e '\(gatePath)' ]; do
                sleep 0.05; i=$((i+1))
              done
            ) &
            echo $! > '\(descendantPath)'
            printf '{"text":"kept after the cancel"}'
            echo $$ > '\(leaderPath).tmp' && mv '\(leaderPath).tmp' '\(leaderPath)'
            """, in: directory)
        let scratch = ScratchAudioDirectory(url: directory.appendingPathComponent("scratch", isDirectory: true))
        let engine = makeEngine(foundry: script, scratch: scratch)
        let samples = tone
        let held = AudioTestSignalLatch()
        let release = DispatchSemaphore(value: 0)
        let systemCalls = readsHeldAfterTheObservedExit(of: leaderPath, held: held, release: release)

        let task = Task {
            try await ProcessRunner.systemCalls.withValue(systemCalls) {
                try await engine.transcribe(samples: samples, sampleRate: 16_000)
            }
        }
        let heldAfterExit = await held.wait()
        XCTAssertTrue(heldAfterExit, "the runner never read after reaping the recognizer")
        task.cancel()
        release.signal()

        let result = try await task.value

        XCTAssertEqual(result.text, "kept after the cancel")
        XCTAssertTrue(result.diagnostics.outputHeldAfterExit)
        XCTAssertTrue(scratchFiles(scratch).isEmpty)
        FileManager.default.createFile(atPath: gatePath, contents: nil)
        let recorded = try String(contentsOfFile: descendantPath, encoding: .utf8)
        let descendant = try XCTUnwrap(pid_t(recorded.trimmingCharacters(in: .whitespacesAndNewlines)))
        XCTAssertTrue(
            ProcessResources.waitForExit(of: descendant, timeout: .seconds(10)), "the descendant outlived the test")
    }

    /// Fails unless the recording whose path the scripted recognizer wrote to `record` existed while it ran and is
    /// gone now. The check is `lstat` on that exact path, so it does not depend on reading the directory, and an
    /// error other than "no such file" fails as itself.
    private func assertRecordingGone(
        recordedIn record: URL, file: StaticString = #filePath, line: UInt = #line
    ) throws {
        let path = try String(contentsOf: record, encoding: .utf8)
        XCTAssertTrue(path.hasSuffix(".wav"), "the recognizer was handed \(path)", file: file, line: line)
        XCTAssertTrue(
            FileManager.default.fileExists(atPath: record.path(percentEncoded: false) + ".existed"),
            "the recognizer did not find its recording", file: file, line: line)
        var info = stat()
        if lstat(path, &info) == 0 {
            XCTFail("the recording is still there", file: file, line: line)
            return
        }
        let code = errno
        XCTAssertEqual(code, ENOENT, "looking for the recording failed with errno \(code)", file: file, line: line)
    }

    /// The exact recording the recognizer is handed exists while it runs and is gone once `transcribe` returns,
    /// whether the recognizer succeeds, fails, runs past its deadline or is cancelled.
    func testTheRecordingTheRecognizerReadIsGoneAfterEveryOutcome() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let scratch = ScratchAudioDirectory(url: directory.appendingPathComponent("scratch", isDirectory: true))
        let evidence = directory.appendingPathComponent("evidence", isDirectory: true)
        try FileManager.default.createDirectory(at: evidence, withIntermediateDirectories: true)
        func recognizer(_ name: String, then tail: String) throws -> (script: URL, record: URL) {
            let record = evidence.appendingPathComponent(name)
            let path = record.path(percentEncoded: false)
            let script = try makeScript(
                """
                printf '%s' "$5" > '\(path).tmp' && mv '\(path).tmp' '\(path)'
                if [ -f "$5" ]; then : > '\(path).existed'; fi
                \(tail)
                """, in: directory, named: "foundry-\(name)")
            return (script, record)
        }
        let quickDeadline = TranscriptionDeadlines(
            coldBase: .milliseconds(300), warmBase: .milliseconds(300), perAudioSecond: 0)

        let success = try recognizer("success", then: "printf '{\"text\":\"done\"}'")
        let result = try await makeEngine(foundry: success.script, scratch: scratch)
            .transcribe(samples: tone, sampleRate: 16_000)
        XCTAssertEqual(result.text, "done")
        try assertRecordingGone(recordedIn: success.record)

        let failure = try recognizer("failure", then: "exit 3")
        let failed = await transcriptionError {
            try await self.makeEngine(foundry: failure.script, scratch: scratch)
                .transcribe(samples: self.tone, sampleRate: 16_000)
        }
        XCTAssertEqual(failed, .exitCode(3))
        try assertRecordingGone(recordedIn: failure.record)

        let slow = try recognizer("deadline", then: "exec sleep 30")
        let late = await transcriptionError {
            try await self.makeEngine(foundry: slow.script, scratch: scratch, deadlines: quickDeadline)
                .transcribe(samples: self.tone, sampleRate: 16_000)
        }
        XCTAssertEqual(late, .timedOut)
        try assertRecordingGone(recordedIn: slow.record)

        let ready = directory.appendingPathComponent("ready")
        let held = try recognizer(
            "cancel", then: ": > '\(ready.path(percentEncoded: false))'\nexec sleep 30")
        let engine = makeEngine(foundry: held.script, scratch: scratch)
        let samples = tone
        let task = Task {
            try await engine.transcribe(samples: samples, sampleRate: 16_000)
        }
        let reachedReady = await FileGate.waitForFile(at: ready, timeout: .seconds(30))
        XCTAssertTrue(reachedReady)
        task.cancel()
        let cancelled = await transcriptionError { try await task.value }
        XCTAssertEqual(cancelled, .cancelled)
        try assertRecordingGone(recordedIn: held.record)
    }

    // MARK: - Failures

    func testEachWayARecognizerFailsHasItsOwnTypedError() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let scratch = ScratchAudioDirectory(url: directory.appendingPathComponent("scratch", isDirectory: true))
        let cases: [(String, TranscriptionError)] = [
            ("exit 3", .exitCode(3)),
            ("kill -KILL $$", .terminatedBySignal(SIGKILL)),
            ("exit 0", .emptyOutput),
            ("printf '{\"text\":\"   \"}'", .emptyOutput),
            ("echo 'not json at all'", .malformedOutput),
            ("printf '{\"error\":{\"code\":\"x\",\"message\":\"no\"}}'; exit 1", .backendReportedError),
            ("printf '{\"error\":{\"code\":\"x\"}}'", .backendReportedError),
        ]
        for (index, (body, expected)) in cases.enumerated() {
            let script = try makeScript(body, in: directory, named: "foundry-\(index)")
            let engine = makeEngine(foundry: script, scratch: scratch)

            let error = await transcriptionError { try await engine.transcribe(samples: self.tone, sampleRate: 16_000) }

            XCTAssertEqual(error, expected, body)
            XCTAssertTrue(scratchFiles(scratch).isEmpty, body)
        }
    }

    func testAMissingRecognizerIsReportedAndFoundAgainOnceInstalled() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let script = directory.appendingPathComponent("foundry")
        let engine = makeEngine(
            foundry: script, scratch: ScratchAudioDirectory(url: directory.appendingPathComponent("s")))

        let missing = await transcriptionError { try await engine.transcribe(samples: self.tone, sampleRate: 16_000) }
        XCTAssertEqual(missing, .backendMissing(.foundryCliNotFound))

        try writeScript("printf '{\"text\":\"found\"}'", at: script)
        let result = try await engine.transcribe(samples: tone, sampleRate: 16_000)
        XCTAssertEqual(result.text, "found")
    }

    /// A lookup is reused briefly, a recognizer that disappears is looked up again after it fails to start, and
    /// the engine never stays without a recognizer once one is back.
    func testALookupIsReusedBrieflyAndForgottenWhenTheRecognizerCannotStart() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let scratch = ScratchAudioDirectory(url: directory.appendingPathComponent("scratch", isDirectory: true))
        let script = try makeScript("printf '{\"text\":\"ok\"}'", in: directory)
        let offset = OSAllocatedUnfairLock(initialState: Duration.zero)
        let base = ContinuousClock.now
        let engine = makeEngine(
            foundry: script, scratch: scratch, resolutionLifetime: .seconds(10),
            now: { base.advanced(by: offset.withLock { $0 }) })

        _ = try await engine.transcribe(samples: tone, sampleRate: 16_000)
        try FileManager.default.removeItem(at: script)
        XCTAssertNoThrow(try engine.resolveBackend(), "a lookup inside its lifetime is reused")

        let launch = await transcriptionError { try await engine.transcribe(samples: self.tone, sampleRate: 16_000) }
        XCTAssertEqual(launch, .launchFailed(errno: ENOENT))
        XCTAssertTrue(scratchFiles(scratch).isEmpty)

        let missing = await transcriptionError { try await engine.transcribe(samples: self.tone, sampleRate: 16_000) }
        XCTAssertEqual(missing, .backendMissing(.foundryCliNotFound))

        try writeScript("printf '{\"text\":\"back\"}'", at: script)
        let back = try await engine.transcribe(samples: tone, sampleRate: 16_000)
        XCTAssertEqual(back.text, "back")

        try FileManager.default.removeItem(at: script)
        offset.withLock { $0 = .seconds(11) }
        XCTAssertThrowsError(try engine.resolveBackend(), "an expired lookup is made again")
    }

    func testTheWhisperFallbackIsUsedWhenAskedForByName() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let whisper = try makeScript("echo '  whisper words  '", in: directory, named: "whisper-cli")
        let model = directory.appendingPathComponent("ggml-tiny.en.bin")
        try Data([0]).write(to: model)
        let engine = TranscriptionEngine(
            resolver: TranscriptionBackendResolver(
                environment: [
                    "SCRIBE_WHISPER_CLI": whisper.path(percentEncoded: false),
                    "SCRIBE_WHISPER_MODEL": model.path(percentEncoded: false),
                ],
                searchPath: [], whisperCliCandidates: [], whisperModelCandidates: []),
            scratch: ScratchAudioDirectory(url: directory.appendingPathComponent("s")))

        let result = try await engine.transcribe(samples: tone, sampleRate: 16_000)

        XCTAssertEqual(result.text, "whisper words")
        XCTAssertEqual(result.backend, .whisperCpp)
    }

    func testTheCommandLineVerbRefusesAFileThatIsNotAWav() async throws {
        let directory = try makeTemporaryDirectory(label: "asr")
        let engine = makeEngine(
            foundry: directory.appendingPathComponent("foundry"),
            scratch: ScratchAudioDirectory(url: directory.appendingPathComponent("s")))

        let error = await transcriptionError {
            try await engine.transcribe(wavFileAt: directory.appendingPathComponent("recording.m4a"))
        }
        XCTAssertEqual(error, .unsupportedAudioFile)
    }

    // MARK: - Reading a finished run

    private func outcome(
        _ reason: ProcessRunner.TerminationReason = .finished,
        status: Int32? = 0,
        signal: Int32? = nil,
        stdout: String = ""
    ) -> ProcessRunner.Outcome {
        let data = Data(stdout.utf8)
        return ProcessRunner.Outcome(
            terminationReason: reason,
            exitStatus: status,
            terminationSignal: signal,
            standardOutput: ProcessRunner.CapturedOutput(
                data: data, totalByteCount: data.count, reachedEndOfFile: true),
            standardError: ProcessRunner.CapturedOutput(data: Data(), totalByteCount: 0, reachedEndOfFile: true),
            duration: .milliseconds(10))
    }

    func testAStoppedRunNeverYieldsAPartialTranscript() {
        XCTAssertThrowsError(
            try TranscriptionEngine.transcript(
                from: outcome(.timedOut, stdout: "{\"text\":\"partial\"}"), kind: .foundryLocal)
        ) { XCTAssertEqual($0 as? TranscriptionError, .timedOut) }
        XCTAssertThrowsError(
            try TranscriptionEngine.transcript(from: outcome(.cancelled, stdout: "partial"), kind: .whisperCpp)
        ) { XCTAssertEqual($0 as? TranscriptionError, .cancelled) }
    }

    func testFoundrysReplyIsReadBeforeItsExitStatus() throws {
        XCTAssertEqual(
            try TranscriptionEngine.transcript(
                from: outcome(status: 1, stdout: "{\"text\":\" words \"}"), kind: .foundryLocal),
            "words")
        XCTAssertThrowsError(
            try TranscriptionEngine.transcript(from: outcome(status: 0, stdout: "{\"error\":{}}"), kind: .foundryLocal)
        ) { XCTAssertEqual($0 as? TranscriptionError, .backendReportedError) }
        XCTAssertThrowsError(
            try TranscriptionEngine.transcript(from: outcome(status: 2, stdout: ""), kind: .foundryLocal)
        ) { XCTAssertEqual($0 as? TranscriptionError, .exitCode(2)) }
        XCTAssertThrowsError(
            try TranscriptionEngine.transcript(from: outcome(status: nil, signal: SIGTERM), kind: .whisperCpp)
        ) { XCTAssertEqual($0 as? TranscriptionError, .terminatedBySignal(SIGTERM)) }
    }

    func testTheDeadlineGrowsWithTheAudioAndIsLongerWhileCold() {
        let deadlines = TranscriptionDeadlines()

        XCTAssertEqual(deadlines.deadline(audioSeconds: 60, warm: true), .seconds(90))
        XCTAssertEqual(deadlines.deadline(audioSeconds: 60, warm: false), .seconds(360))
        XCTAssertEqual(deadlines.deadline(audioSeconds: -5, warm: true), .seconds(30))
        XCTAssertEqual(deadlines.deadline(audioSeconds: .nan, warm: true), .seconds(30))
    }

    // MARK: - Privacy

    /// The transcript, the recognizer's own words and the recording's path must never reach a log line, and the
    /// errors the app shows must not carry them either.
    func testLogsCarryNeitherTheTranscriptNorTheRecognizersOutputNorThePath() async throws {
        let recorder = recordScribeLog()
        let directory = try makeTemporaryDirectory(label: "canary")
        let scratch = ScratchAudioDirectory(url: directory.appendingPathComponent("canary-scratch", isDirectory: true))
        let speaking = try makeScript(
            """
            printf '{"text":"%s"}' '\(PrivacyCanary.transcript)'
            echo '\(PrivacyCanary.secret) \(PrivacyCanary.path)' >&2
            """, in: directory, named: "foundry-canary")
        let failing = try makeScript(
            """
            printf '{"error":{"code":"%s","message":"%s"}}' '\(PrivacyCanary.secret)' '\(PrivacyCanary.path)'
            exit 1
            """, in: directory, named: "foundry-canary-failing")

        let result = try await makeEngine(foundry: speaking, scratch: scratch)
            .transcribe(samples: tone, sampleRate: 16_000)
        XCTAssertEqual(result.text, PrivacyCanary.transcript)
        let failure = await transcriptionError {
            try await self.makeEngine(foundry: failing, scratch: scratch)
                .transcribe(samples: self.tone, sampleRate: 16_000)
        }
        let missing = await transcriptionError {
            try await self.makeEngine(foundry: directory.appendingPathComponent("absent-canary"), scratch: scratch)
                .transcribe(samples: self.tone, sampleRate: 16_000)
        }

        XCTAssertEqual(failure, .backendReportedError)
        XCTAssertEqual(missing, .backendMissing(.foundryCliNotFound))
        PrivacyCanary.assertAbsent(from: recorder.publicText)
        XCTAssertFalse(recorder.renderings.contains { $0.privateText?.lowercased().contains("canary") == true })
        PrivacyCanary.assertAbsent(from: failure?.localizedDescription ?? "")
        PrivacyCanary.assertAbsent(from: missing?.localizedDescription ?? "")
        XCTAssertFalse(recorder.lines.isEmpty, "the engine logs its shapes")
    }
}
