import Darwin
import XCTest
import os

@testable import Scribe

/// Runs small real children (`/bin/sh`, `/bin/cat`, `/bin/sleep`) because what matters here is how
/// the runner behaves against the kernel: pipes that fill up, signals, process groups, descriptors.
/// Nothing waits by sleeping; a child that has to reach a point first opens a `FileGate`. Every child
/// ends by itself within about a minute whatever the runner does (a `sleep 30`, a CPU limit of 30
/// seconds, or a loop of 600 short sleeps), and a wait on a run that a regression could keep from
/// returning goes through `awaitOutcome(within:_:)`, so a regression fails a test instead of hanging
/// the suite or leaving processes behind.
final class ProcessRunnerTests: XCTestCase {
    private let shell = URL(fileURLWithPath: "/bin/sh")

    private func runShell(
        _ script: String,
        _ arguments: String...,
        timeout: Duration = .seconds(60),
        outputLimit: Int = ProcessRunner.defaultOutputLimit,
        killGracePeriod: Duration = ProcessRunner.defaultKillGracePeriod
    ) async throws -> ProcessRunner.Outcome {
        try await ProcessRunner.run(
            shell,
            arguments: ["-c", script, "sh"] + arguments,
            timeout: timeout,
            outputLimit: outputLimit,
            killGracePeriod: killGracePeriod)
    }

    /// Starts `script` in a task and returns once the script has created `ready`.
    private func startShellAndWaitUntilReady(
        _ script: String,
        killGracePeriod: Duration = ProcessRunner.defaultKillGracePeriod
    ) async throws -> Task<ProcessRunner.Outcome, any Error> {
        let ready = try makeTemporaryDirectory(label: "process").appendingPathComponent("ready")
        let shell = shell
        let task = Task {
            try await ProcessRunner.run(
                shell,
                arguments: ["-c", script, "sh", ready.path(percentEncoded: false)],
                timeout: .seconds(60),
                killGracePeriod: killGracePeriod)
        }
        let reachedReady = await FileGate.waitForFile(at: ready, timeout: .seconds(30))
        XCTAssertTrue(reachedReady, "The child never reached its ready point")
        return task
    }

    /// Waits for `operation` for at most `limit`. A regression that keeps a run from returning then fails
    /// the test that met it instead of hanging the suite; the operation is left running, and every child
    /// these tests start ends by itself. The sleep here orders nothing: it only bounds a failure.
    private func awaitOutcome(
        within limit: Duration,
        _ operation: @escaping @Sendable () async throws -> ProcessRunner.Outcome
    ) async throws -> ProcessRunner.Outcome {
        try await withCheckedThrowingContinuation { continuation in
            let first = FirstResult(continuation)
            let watchdog = Task {
                try? await Task.sleep(for: limit)
                first.resume(with: .failure(RunDidNotReturn(limit: limit)))
            }
            Task {
                do {
                    first.resume(with: .success(try await operation()))
                } catch {
                    first.resume(with: .failure(error))
                }
                watchdog.cancel()
            }
        }
    }

    // MARK: - Exit status and output

    func testCapturesBothStreamsAndTheExitStatus() async throws {
        let outcome = try await runShell("printf out; printf err >&2; exit 3")

        XCTAssertEqual(outcome.terminationReason, .finished)
        XCTAssertEqual(outcome.exitStatus, 3)
        XCTAssertNil(outcome.terminationSignal)
        XCTAssertFalse(outcome.succeeded)
        XCTAssertEqual(outcome.standardOutput.text, "out")
        XCTAssertEqual(outcome.standardError.text, "err")
        XCTAssertTrue(outcome.standardOutput.reachedEndOfFile)
        XCTAssertTrue(outcome.standardError.reachedEndOfFile)
        XCTAssertFalse(outcome.standardOutput.isTruncated)
        XCTAssertGreaterThan(outcome.duration, .zero)
    }

    func testSucceededMeansAZeroExitTheChildChoseItself() async throws {
        let success = try await ProcessRunner.run(URL(fileURLWithPath: "/usr/bin/true"), timeout: .seconds(60))
        let failure = try await ProcessRunner.run(URL(fileURLWithPath: "/usr/bin/false"), timeout: .seconds(60))

        XCTAssertTrue(success.succeeded)
        XCTAssertEqual(success.exitStatus, 0)
        XCTAssertFalse(failure.succeeded)
        XCTAssertEqual(failure.exitStatus, 1)
    }

    func testReportsTheSignalThatEndedAChildItDidNotStop() async throws {
        let outcome = try await runShell("kill -KILL $$")

        XCTAssertEqual(outcome.terminationReason, .finished)
        XCTAssertEqual(outcome.terminationSignal, SIGKILL)
        XCTAssertNil(outcome.exitStatus)
        XCTAssertFalse(outcome.succeeded)
    }

    /// A reader that waits for the exit before draining, or drains one stream to its end before the
    /// other, deadlocks here: the child fills the standard error pipe before it writes anything else.
    func testLargeOutputOnBothStreamsNeitherDeadlocksNorGrowsPastTheLimit() async throws {
        let size = 4 * 1024 * 1024
        let limit = 1024 * 1024

        let outcome = try await runShell(
            "head -c \(size) /dev/zero >&2; head -c \(size) /dev/zero", outputLimit: limit)

        XCTAssertEqual(outcome.terminationReason, .finished)
        XCTAssertEqual(outcome.exitStatus, 0)
        XCTAssertEqual(outcome.standardError.totalByteCount, size)
        XCTAssertEqual(outcome.standardOutput.totalByteCount, size)
        XCTAssertEqual(outcome.standardError.data.count, limit)
        XCTAssertEqual(outcome.standardOutput.data.count, limit)
        XCTAssertTrue(outcome.standardError.isTruncated)
        XCTAssertTrue(outcome.standardOutput.isTruncated)
    }

    /// `cat` echoes as it reads, so writing all of the input before reading any output deadlocks once
    /// both pipes are full.
    func testStandardInputIsWrittenWhileTheOutputIsRead() async throws {
        let input = Data((0..<(3 * 1024 * 1024)).map { (index: Int) -> UInt8 in
            UInt8(truncatingIfNeeded: index &* 31 &+ index >> 11)
        })

        let outcome = try await ProcessRunner.run(
            URL(fileURLWithPath: "/bin/cat"),
            standardInput: input,
            timeout: .seconds(60),
            outputLimit: 4 * 1024 * 1024)

        XCTAssertTrue(outcome.succeeded)
        XCTAssertEqual(outcome.standardOutput.totalByteCount, input.count)
        XCTAssertEqual(outcome.standardOutput.data, input)
    }

    func testWithoutStandardInputTheChildReadsDevNull() async throws {
        let outcome = try await runShell(#"test "$(stat -f %d:%i /dev/fd/0)" = "$(stat -f %d:%i /dev/null)""#)

        XCTAssertTrue(outcome.succeeded, "Standard input was not /dev/null: \(outcome.standardError.text)")
    }

    func testAGivenEnvironmentIsTheChildsWholeEnvironment() async throws {
        let outcome = try await ProcessRunner.run(
            URL(fileURLWithPath: "/usr/bin/env"),
            environment: ["SCRIBE_PROBE": "probe value"],
            timeout: .seconds(60))

        XCTAssertTrue(outcome.succeeded)
        XCTAssertEqual(outcome.standardOutput.text, "SCRIBE_PROBE=probe value\n")
    }

    func testWithoutAnEnvironmentTheChildInheritsScribes() async throws {
        // macOS strips DYLD_ variables when it starts a protected system binary such as env, and the
        // sanitizer runs set one.
        let expected = ProcessInfo.processInfo.environment.filter {
            !$0.key.hasPrefix("DYLD_") && !$0.value.contains("\n")
        }

        let outcome = try await ProcessRunner.run(URL(fileURLWithPath: "/usr/bin/env"), timeout: .seconds(60))

        XCTAssertTrue(outcome.succeeded)
        var inherited: [String: String] = [:]
        for line in outcome.standardOutput.text.split(separator: "\n") {
            guard let separator = line.firstIndex(of: "=") else { continue }
            inherited[String(line[..<separator])] = String(line[line.index(after: separator)...])
        }
        XCTAssertFalse(expected.isEmpty)
        for (name, value) in expected {
            XCTAssertEqual(inherited[name], value, "The child did not inherit \(name)")
        }
    }

    // MARK: - Deadline and cancellation

    func testTheDeadlineStopsAChildThatWouldRunLonger() async throws {
        let outcome = try await ProcessRunner.run(
            URL(fileURLWithPath: "/bin/sleep"), arguments: ["30"], timeout: .milliseconds(300))

        XCTAssertEqual(outcome.terminationReason, .timedOut)
        XCTAssertEqual(outcome.terminationSignal, SIGTERM)
        XCTAssertNil(outcome.exitStatus)
        XCTAssertFalse(outcome.succeeded)
        XCTAssertGreaterThanOrEqual(outcome.duration, .milliseconds(300))
        XCTAssertLessThan(outcome.duration, .seconds(20))
    }

    func testCancellationStopsARunningChild() async throws {
        let task = try await startShellAndWaitUntilReady(#"touch "$1"; exec sleep 30"#)

        task.cancel()
        let outcome = try await task.value

        XCTAssertEqual(outcome.terminationReason, .cancelled)
        XCTAssertEqual(outcome.terminationSignal, SIGTERM)
        XCTAssertLessThan(outcome.duration, .seconds(20))
    }

    /// Ignoring SIGTERM is inherited through `exec`, so the one process left ignores it too.
    func testAChildThatIgnoresTerminateIsKilledAfterTheGracePeriod() async throws {
        let task = try await startShellAndWaitUntilReady(
            #"trap "" TERM; touch "$1"; exec sleep 30"#, killGracePeriod: .milliseconds(200))

        task.cancel()
        let outcome = try await task.value

        XCTAssertEqual(outcome.terminationReason, .cancelled)
        XCTAssertEqual(outcome.terminationSignal, SIGKILL)
        XCTAssertGreaterThanOrEqual(outcome.duration, .milliseconds(200))
        XCTAssertLessThan(outcome.duration, .seconds(20))
    }

    /// The background `sleep` holds both output pipes. Signalling only the shell would leave it
    /// running, and the streams would never reach end of file.
    func testStoppingAChildEndsItsDescendantsToo() async throws {
        let task = try await startShellAndWaitUntilReady(#"sleep 30 & touch "$1"; wait"#)

        task.cancel()
        let outcome = try await task.value

        XCTAssertEqual(outcome.terminationReason, .cancelled)
        XCTAssertTrue(outcome.standardOutput.reachedEndOfFile)
        XCTAssertTrue(outcome.standardError.reachedEndOfFile)
    }

    /// A daemon the child starts can keep the output pipes open for as long as it runs; the outcome
    /// must still arrive shortly after the child itself exits.
    func testADescendantHoldingTheOutputDoesNotHoldTheOutcomeBack() async throws {
        let outcome = try await runShell("sleep 3 & printf done")

        XCTAssertEqual(outcome.terminationReason, .finished)
        XCTAssertEqual(outcome.exitStatus, 0)
        XCTAssertEqual(outcome.standardOutput.text, "done")
        XCTAssertFalse(outcome.standardOutput.reachedEndOfFile)
        XCTAssertLessThan(outcome.duration, .milliseconds(2_500))
    }

    /// Each run's descendant stays behind a gate holding standard input (with a megabyte still unwritten),
    /// standard output and standard error. Once an outcome is back, the run must hold nothing: no thread
    /// is still working for it and every pipe and kqueue it opened is closed, however many of those
    /// descendants are still waiting. They poll 600 times at most and only while the test's directory
    /// exists, and the test opens the gate and waits for each of them to end before it returns, so none
    /// outlives it.
    func testADescendantHoldingEveryPipeLeavesNothingOfTheRunBehind() async throws {
        let directory = try makeTemporaryDirectory(label: "process")
        let directoryPath = directory.path(percentEncoded: false)
        let gatePath = directory.appendingPathComponent("gate").path(percentEncoded: false)
        let script = #"""
            (i=0; while [ $i -lt 600 ] && [ -d "$2" ] && [ ! -e "$1" ]; do sleep 0.05; i=$((i+1)); done) <&0 &
            echo $! > "$3"; printf done
            """#
        var descendants: [pid_t] = []
        defer {
            _ = FileManager.default.createFile(atPath: gatePath, contents: nil)
            for descendant in descendants {
                XCTAssertTrue(
                    ProcessResources.waitForExit(of: descendant, timeout: .seconds(10)),
                    "Descendant \(descendant) outlived the test")
            }
        }
        let input = Data(repeating: 0x2A, count: 1 << 20)
        let shell = shell
        func runHeldBehindTheGate() async throws -> ProcessRunner.Outcome {
            let pidPath = directory.appendingPathComponent(UUID().uuidString).path(percentEncoded: false)
            let outcome = try await ProcessRunner.run(
                shell, arguments: ["-c", script, "sh", gatePath, directoryPath, pidPath], standardInput: input,
                timeout: .seconds(60))
            let recorded = try String(contentsOfFile: pidPath, encoding: .utf8)
            descendants.append(try XCTUnwrap(pid_t(recorded.trimmingCharacters(in: .whitespacesAndNewlines))))
            return outcome
        }

        // The first run also settles anything the runtime creates lazily, so the baseline is fair.
        _ = try await runHeldBehindTheGate()
        let baseline = ProcessResources.pipesAndQueues()
        XCTAssertGreaterThanOrEqual(baseline.pipes, 0)
        XCTAssertEqual(ProcessResources.threads(named: "ScribeProcessRunner"), 0)

        for _ in 0..<4 {
            let outcome = try await runHeldBehindTheGate()

            XCTAssertEqual(outcome.terminationReason, .finished)
            XCTAssertEqual(outcome.standardOutput.text, "done")
            XCTAssertFalse(outcome.standardOutput.reachedEndOfFile)
            XCTAssertFalse(outcome.standardError.reachedEndOfFile)
            XCTAssertEqual(ProcessResources.pipesAndQueues().pipes, baseline.pipes)
            XCTAssertEqual(ProcessResources.pipesAndQueues().queues, baseline.queues)
            XCTAssertEqual(ProcessResources.threads(named: "ScribeProcessRunner"), 0)
        }
    }

    /// The shell exits on SIGTERM at once while its worker spends a moment cleaning up, then says so on
    /// standard output. The worker must get its grace period instead of a SIGKILL the moment its parent
    /// is gone, and once it has finished the run need not wait out the rest of the period.
    func testADescendantCleaningUpAfterItsParentExitsGetsTheGracePeriod() async throws {
        let task = try await startShellAndWaitUntilReady(
            #"""
            (trap 'sleep 0.3; printf cleaned; exit 0' TERM; touch "$1"
            i=0; while [ $i -lt 600 ]; do sleep 0.05; i=$((i+1)); done) & wait
            """#,
            killGracePeriod: .seconds(10))

        task.cancel()
        let outcome = try await task.value

        XCTAssertEqual(outcome.terminationReason, .cancelled)
        XCTAssertEqual(outcome.terminationSignal, SIGTERM)
        XCTAssertEqual(outcome.standardOutput.text, "cleaned")
        XCTAssertTrue(outcome.standardOutput.reachedEndOfFile)
        XCTAssertLessThan(outcome.duration, .seconds(8))
    }

    /// With the shell already gone, its worker ignores SIGTERM; the SIGKILL at the end of the grace
    /// period must still reach it, which the pipes reaching end of file prove.
    func testADescendantIgnoringTerminateAfterItsParentExitsIsKilledAtTheDeadline() async throws {
        let task = try await startShellAndWaitUntilReady(
            #"(trap "" TERM; touch "$1"; i=0; while [ $i -lt 600 ]; do sleep 0.05; i=$((i+1)); done) & wait"#,
            killGracePeriod: .milliseconds(300))

        task.cancel()
        let outcome = try await task.value

        XCTAssertEqual(outcome.terminationReason, .cancelled)
        XCTAssertEqual(outcome.terminationSignal, SIGTERM)
        XCTAssertTrue(outcome.standardOutput.reachedEndOfFile)
        XCTAssertTrue(outcome.standardError.reachedEndOfFile)
        XCTAssertGreaterThanOrEqual(outcome.duration, .milliseconds(300))
        XCTAssertLessThan(outcome.duration, .seconds(20))
    }

    func testCancellationBeforeTheLaunchStartsNothingAndThrows() async {
        let task = Task { () async throws -> ProcessRunner.Outcome in
            withUnsafeCurrentTask { $0?.cancel() }
            return try await ProcessRunner.run(
                URL(fileURLWithPath: "/bin/sleep"), arguments: ["30"], timeout: .seconds(60))
        }

        do {
            _ = try await task.value
            XCTFail("Expected CancellationError")
        } catch {
            XCTAssertTrue(error is CancellationError, "Got \(error)")
        }
    }

    /// libproc reports a failed lookup of a process group as an empty list with errno set. Here every
    /// lookup fails that way while the shell is gone and its worker, which ignores SIGTERM, holds no pipe:
    /// the run must not take the failure for an empty group, and must end the worker at the deadline.
    func testAFailedGroupLookupStillEndsTheWorkerAtTheDeadline() async throws {
        let failingLookup = ProcessRunnerSystemCalls(
            listProcessGroup: { _, _, _ in
                errno = EPERM
                return 0
            },
            readPipe: ProcessRunnerSystemCalls.live.readPipe)
        let ready = try makeTemporaryDirectory(label: "process").appendingPathComponent("ready")
        let readyPath = ready.path(percentEncoded: false)
        // The worker ignores SIGTERM, lets go of the output pipes, writes its pid as the ready file and polls
        // 600 times at most.
        let script = #"""
            sh -c 'trap "" TERM; exec >/dev/null 2>&1; echo $$ > "$1.pid"; mv "$1.pid" "$1"
            i=0; while [ $i -lt 600 ]; do sleep 0.05; i=$((i+1)); done' worker "$1" &
            wait
            """#
        let shell = shell
        let task = ProcessRunner.systemCalls.withValue(failingLookup) {
            Task {
                try await ProcessRunner.run(
                    shell, arguments: ["-c", script, "sh", readyPath], timeout: .seconds(60),
                    killGracePeriod: .milliseconds(300))
            }
        }
        let reachedReady = await FileGate.waitForFile(at: ready, timeout: .seconds(30))
        XCTAssertTrue(reachedReady, "The worker never reached its ready point")
        let recorded = try String(contentsOfFile: readyPath, encoding: .utf8)
        let worker = try XCTUnwrap(pid_t(recorded.trimmingCharacters(in: .whitespacesAndNewlines)))

        let clock = ContinuousClock()
        let cancelledAt = clock.now
        task.cancel()
        let outcome = try await awaitOutcome(within: .seconds(90)) { try await task.value }
        let waitedAfterCancelling = cancelledAt.duration(to: clock.now)
        let workerEnded = ProcessResources.waitForExit(of: worker, timeout: .seconds(10))
        if !workerEnded {
            kill(worker, SIGKILL)
        }

        XCTAssertEqual(outcome.terminationReason, .cancelled)
        XCTAssertEqual(outcome.terminationSignal, SIGTERM)
        XCTAssertGreaterThanOrEqual(waitedAfterCancelling, .milliseconds(300))
        XCTAssertTrue(workerEnded, "The worker outlived the grace period")
    }

    // MARK: - Endless output

    /// `yes` writes for as long as it runs, and reading it must still leave room for the deadline.
    /// `ulimit -t` ends `yes` by itself after 30 seconds of CPU time, so a regression fails the duration
    /// check instead of leaving it running.
    func testEndlessOutputDoesNotDelayTheDeadline() async throws {
        let shell = shell
        let outcome = try await awaitOutcome(within: .seconds(90)) {
            try await ProcessRunner.run(
                shell, arguments: ["-c", "ulimit -t 30; exec yes"], timeout: .milliseconds(300),
                outputLimit: 1 << 16)
        }

        XCTAssertEqual(outcome.terminationReason, .timedOut)
        XCTAssertEqual(outcome.terminationSignal, SIGTERM)
        XCTAssertEqual(outcome.standardOutput.data.count, 1 << 16)
        XCTAssertTrue(outcome.standardOutput.isTruncated)
        XCTAssertLessThan(outcome.duration, .seconds(10))
    }

    func testEndlessOutputDoesNotDelayCancellation() async throws {
        let task = try await startShellAndWaitUntilReady(#"touch "$1"; ulimit -t 30; exec yes"#)

        task.cancel()
        let outcome = try await awaitOutcome(within: .seconds(90)) { try await task.value }

        XCTAssertEqual(outcome.terminationReason, .cancelled)
        XCTAssertEqual(outcome.terminationSignal, SIGTERM)
        XCTAssertLessThan(outcome.duration, .seconds(10))
    }

    /// The shell exits at once and leaves `yes` writing into standard output. The outcome must still
    /// arrive at the drain limit, whose last read is bounded however fast `yes` refills the pipe.
    func testEndlessOutputFromADescendantEndsAtTheDrainLimit() async throws {
        let shell = shell
        let outcome = try await awaitOutcome(within: .seconds(90)) {
            try await ProcessRunner.run(
                shell, arguments: ["-c", "(ulimit -t 30; exec yes) & printf done"], timeout: .seconds(60),
                outputLimit: 1 << 16)
        }

        XCTAssertEqual(outcome.terminationReason, .finished)
        XCTAssertEqual(outcome.exitStatus, 0)
        XCTAssertFalse(outcome.standardOutput.reachedEndOfFile)
        XCTAssertLessThan(outcome.duration, .seconds(10))
    }

    /// A stand-in for `read` that never runs dry is a writer that is always faster than its reader, which
    /// no real child can promise. The run must still stop the child at its deadline and, since neither
    /// stream ever ends, finish with the bounded last read at the drain limit.
    func testAStreamThatNeverRunsDryCannotHoldTheRunPastItsLimits() async throws {
        let neverDry = ProcessRunnerSystemCalls(
            listProcessGroup: ProcessRunnerSystemCalls.live.listProcessGroup,
            readPipe: { _, buffer, count in
                buffer.initializeMemory(as: UInt8.self, repeating: 0x79, count: count)
                return count
            })
        let shell = shell
        let outcome = try await awaitOutcome(within: .seconds(90)) {
            try await ProcessRunner.systemCalls.withValue(neverDry) {
                try await ProcessRunner.run(
                    shell, arguments: ["-c", "exec >&- 2>&-; exec sleep 30"], timeout: .milliseconds(300),
                    outputLimit: 1 << 16, killGracePeriod: .milliseconds(100))
            }
        }

        XCTAssertEqual(outcome.terminationReason, .timedOut)
        XCTAssertEqual(outcome.terminationSignal, SIGTERM)
        XCTAssertFalse(outcome.standardOutput.reachedEndOfFile)
        XCTAssertFalse(outcome.standardError.reachedEndOfFile)
        XCTAssertGreaterThan(outcome.standardOutput.totalByteCount, 1 << 16)
        XCTAssertLessThan(outcome.duration, .seconds(10))
    }

    // MARK: - Launch

    func testAMissingOrUnrunnableExecutableFailsToLaunch() async throws {
        let directory = try makeTemporaryDirectory(label: "process")
        let notExecutable = directory.appendingPathComponent("plain-file")
        try Data("#!/bin/sh\n".utf8).write(to: notExecutable)

        await assertLaunchFails(URL(fileURLWithPath: "/nonexistent/scribe-tool"), with: .launchFailed(errno: ENOENT))
        await assertLaunchFails(notExecutable, with: .launchFailed(errno: EACCES))
        await assertLaunchFails(URL(string: "https://example.invalid/tool")!, with: .invalidRequest)
        await assertLaunchFails(shell, arguments: ["-c", "a\u{0}b"], with: .invalidRequest)
        await assertLaunchFails(shell, environment: ["A=B": "c"], with: .invalidRequest)
    }

    private func assertLaunchFails(
        _ executable: URL,
        arguments: [String] = [],
        environment: [String: String]? = nil,
        with expected: ProcessRunnerError,
        file: StaticString = #filePath,
        line: UInt = #line
    ) async {
        do {
            _ = try await ProcessRunner.run(
                executable, arguments: arguments, environment: environment, timeout: .seconds(60))
            XCTFail("Expected \(expected)", file: file, line: line)
        } catch let error as ProcessRunnerError {
            XCTAssertEqual(error, expected, file: file, line: line)
        } catch {
            XCTFail("Expected \(expected), got \(error)", file: file, line: line)
        }
    }

    /// A descriptor Scribe opened without close-on-exec must not reach the child. If one could, a
    /// child started concurrently would hold another child's pipe open and delay its end of file.
    func testAChildInheritsOnlyItsStandardDescriptors() async throws {
        var descriptors: [Int32] = [-1, -1]
        XCTAssertEqual(pipe(&descriptors), 0)
        let inheritable = fcntl(descriptors[0], F_DUPFD, 200)
        XCTAssertGreaterThanOrEqual(inheritable, 200)
        defer {
            close(descriptors[0])
            close(descriptors[1])
            close(inheritable)
        }

        let outcome = try await ProcessRunner.run(
            URL(fileURLWithPath: "/bin/ls"), arguments: ["/dev/fd"], timeout: .seconds(60))

        XCTAssertTrue(outcome.succeeded)
        let listed = Set(outcome.standardOutput.text.split(whereSeparator: \.isNewline).compactMap { Int32($0) })
        XCTAssertTrue(listed.isSuperset(of: [0, 1, 2]), "Listed \(listed.sorted())")
        XCTAssertFalse(listed.contains(inheritable), "Listed \(listed.sorted())")
        XCTAssertFalse(listed.contains { $0 >= 200 }, "Listed \(listed.sorted())")
    }

    // MARK: - The caller's actor

    /// The release below is a main-actor job, queued before the run starts, that can only execute
    /// while the main actor is free. If `run` held the main actor until the child exited, the child
    /// would wait for the release until its deadline and the outcome would say `.timedOut`.
    @MainActor
    func testTheMainActorStaysFreeWhileAChildRuns() async throws {
        let release = try makeTemporaryDirectory(label: "process").appendingPathComponent("release")
        let releasePath = release.path(percentEncoded: false)
        let shell = shell

        let outcome = try await Task { @MainActor () async throws -> ProcessRunner.Outcome in
            Task { @MainActor in
                _ = FileManager.default.createFile(atPath: releasePath, contents: nil)
            }
            return try await ProcessRunner.run(
                shell,
                arguments: [
                    "-c", #"i=0; while [ $i -lt 2000 ] && [ ! -e "$1" ]; do sleep 0.01; i=$((i+1)); done"#, "sh",
                    releasePath,
                ],
                timeout: .seconds(20))
        }.value

        XCTAssertEqual(outcome.terminationReason, .finished)
        XCTAssertEqual(outcome.exitStatus, 0)
    }

    // MARK: - Finding executables

    func testLocateExecutableSearchesTheDirectoriesInOrder() throws {
        let first = try makeTemporaryDirectory(label: "locate")
        let second = try makeTemporaryDirectory(label: "locate")
        let name = "scribe-probe-\(UUID().uuidString)"
        try makeExecutable(first.appendingPathComponent(name))
        try makeExecutable(second.appendingPathComponent(name))

        let found = ProcessRunner.locateExecutable(
            named: name, searchPath: [second.path(percentEncoded: false), first.path(percentEncoded: false)])

        XCTAssertEqual(
            found?.path(percentEncoded: false), second.appendingPathComponent(name).path(percentEncoded: false))
    }

    func testLocateExecutableSkipsWhatCannotRunAndRefusesPaths() throws {
        let directory = try makeTemporaryDirectory(label: "locate")
        let plainName = "scribe-plain-\(UUID().uuidString)"
        try Data("#!/bin/sh\n".utf8).write(to: directory.appendingPathComponent(plainName))
        let folderName = "scribe-folder-\(UUID().uuidString)"
        try FileManager.default.createDirectory(
            at: directory.appendingPathComponent(folderName), withIntermediateDirectories: false)
        let searchPath = [directory.path(percentEncoded: false)]

        XCTAssertNil(ProcessRunner.locateExecutable(named: plainName, searchPath: searchPath))
        XCTAssertNil(ProcessRunner.locateExecutable(named: folderName, searchPath: searchPath))
        XCTAssertNil(ProcessRunner.locateExecutable(named: "sh", searchPath: ["bin", "./bin"]))
        XCTAssertNil(ProcessRunner.locateExecutable(named: "bin/sh", searchPath: ["/"]))
        XCTAssertNil(ProcessRunner.locateExecutable(named: "", searchPath: searchPath))
        XCTAssertEqual(
            ProcessRunner.locateExecutable(named: "sh", searchPath: ["/bin"])?.path(percentEncoded: false), "/bin/sh")
    }

    func testTheDefaultSearchPathPutsHomebrewFirstAndDropsRelativeEntries() {
        let searchPath = ProcessRunner.defaultSearchPath(
            environment: ["PATH": "/usr/bin:.:/opt/homebrew/bin::relative/bin:/bin"])

        XCTAssertEqual(searchPath, ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"])
    }

    private func makeExecutable(_ url: URL) throws {
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path(percentEncoded: false))
    }
}

/// Resumes a continuation with whichever result arrives first, and drops the rest.
private final class FirstResult: Sendable {
    private let continuation: OSAllocatedUnfairLock<CheckedContinuation<ProcessRunner.Outcome, any Error>?>

    init(_ continuation: CheckedContinuation<ProcessRunner.Outcome, any Error>) {
        self.continuation = OSAllocatedUnfairLock(initialState: continuation)
    }

    func resume(with result: Result<ProcessRunner.Outcome, any Error>) {
        let pending = continuation.withLock { current -> CheckedContinuation<ProcessRunner.Outcome, any Error>? in
            defer { current = nil }
            return current
        }
        pending?.resume(with: result)
    }
}

private struct RunDidNotReturn: Error, CustomStringConvertible {
    let limit: Duration

    var description: String { "The run did not return within \(limit)" }
}
