import Darwin
import Foundation
import os

/// Runs one helper executable (the `foundry`, `az` and `whisper-cli` tools) as a child process and
/// collects its outcome without blocking the caller.
///
/// `Process.waitUntilExit()` is the wrong tool for this app: it blocks the calling thread (on the main
/// actor that freezes the menu bar and every later dictation), it has no deadline, and reading the
/// pipes only after the exit deadlocks as soon as a child writes more than a pipe buffer holds.
/// `run` instead:
///
/// - reads standard output and standard error from the moment the child starts, keeping at most
///   `outputLimit` bytes of each and counting the rest, so a chatty child never stalls on a full pipe;
/// - writes `standardInput` at the same time, or connects `/dev/null`, so a child never waits on a
///   terminal, and never waits on a parent that has not read its output yet;
/// - enforces `timeout` and honours task cancellation by signalling the child's process group:
///   `SIGTERM`, then `SIGKILL` after `killGracePeriod`, so a wrapper script (Homebrew's `az` is a
///   shell script that starts Python) cannot leave its worker running;
/// - starts the child in a new process group with only descriptors 0, 1 and 2, so a child started
///   concurrently elsewhere can never inherit this child's pipes and hold back its end of file;
/// - does every blocking step on one supervisor thread per run, driven by a kqueue, so an awaiting
///   caller, the main actor included, is only ever suspended.
///
/// When `run` returns, the child has been reaped and the run has let go of everything it held: its
/// supervisor thread has ended and every pipe and queue it opened is closed.
///
/// Signalling is by process group, not by walking the process tree. A descendant that leaves the
/// group (`setsid`, `setpgid`, a daemon that double-forks) is not signalled, and if it keeps one of
/// the pipes open, Scribe closes its own end at `postExitDrainLimit`, after which that descendant's
/// writes fail with `EPIPE` or `SIGPIPE`. A tool that starts a daemon on purpose must redirect the
/// daemon's streams.
///
/// It never logs. Captured output is content (an `az` access token, a transcript): callers must not
/// log it, only its shape, such as byte counts, the exit status and the termination reason.
enum ProcessRunner {
    /// Bytes kept from each output stream unless a caller asks for another limit.
    static let defaultOutputLimit = 1 << 20

    /// How long a stopped child's process group has between `SIGTERM` and `SIGKILL` unless a caller
    /// asks otherwise.
    static let defaultKillGracePeriod: Duration = .seconds(2)

    /// How long a stream may stay open after the child has been reaped. Everything the child wrote is
    /// in the pipes by then; this only bounds the wait on a descendant that kept a stream open. At the
    /// limit Scribe takes what is buffered and closes its ends, standard input included.
    static let postExitDrainLimit: Duration = .milliseconds(500)

    /// Why the child ended.
    enum TerminationReason: Sendable, Equatable {
        /// Nothing Scribe did ended it: it exited, or a signal from outside killed it.
        case finished
        /// Scribe stopped it because `timeout` passed.
        case timedOut
        /// Scribe stopped it because the awaiting task was cancelled.
        case cancelled
    }

    /// What one output stream carried.
    struct CapturedOutput: Sendable, Equatable {
        /// The first `outputLimit` bytes the child wrote.
        let data: Data
        /// Every byte the child wrote, kept or not.
        let totalByteCount: Int
        /// `false` when a descendant still held the stream open at `postExitDrainLimit`, so Scribe
        /// closed its end before the stream ended.
        let reachedEndOfFile: Bool

        var isTruncated: Bool { totalByteCount > data.count }

        /// The kept bytes as UTF-8, with any invalid sequence replaced.
        var text: String { String(decoding: data, as: UTF8.self) }
    }

    /// How a child ended and what it wrote.
    struct Outcome: Sendable, Equatable {
        let terminationReason: TerminationReason
        /// The status the child passed to `exit`, when it exited rather than being killed by a signal.
        /// Both this and `terminationSignal` are `nil` only when something else in Scribe reaped the
        /// child first, so its status was never seen.
        let exitStatus: Int32?
        /// The signal that ended the child, when one did.
        let terminationSignal: Int32?
        let standardOutput: CapturedOutput
        let standardError: CapturedOutput
        /// From the spawn to the outcome, not counting time the Mac spent asleep.
        let duration: Duration

        /// The child ended by itself with status 0.
        var succeeded: Bool { terminationReason == .finished && exitStatus == 0 }
    }

    /// Runs `executableURL` to completion and returns how it ended.
    ///
    /// - Parameters:
    ///   - executableURL: An absolute file URL. `locateExecutable(named:searchPath:)` finds one by name.
    ///   - arguments: The arguments after the executable's own path.
    ///   - environment: The child's whole environment; `nil` passes Scribe's own.
    ///   - standardInput: Bytes the child reads on standard input, which is then closed; `nil`
    ///     connects `/dev/null`.
    ///   - timeout: How long the child may run before it is stopped and the outcome says `.timedOut`.
    ///   - outputLimit: Bytes kept from each of standard output and standard error.
    ///   - killGracePeriod: How long a stopped child's process group has between `SIGTERM` and
    ///     `SIGKILL`. A stopped child that exits during it stays unreaped, which keeps its pid and group
    ///     id from being reused, until both output streams have ended with no live process left in the
    ///     group, or until the period ends. Descendants still cleaning up keep their grace, and the
    ///     `SIGKILL` can only reach this run's group.
    /// - Returns: An outcome for every child that started, however it ended. A stopped child is
    ///   reported, not thrown: the outcome says `.timedOut` or `.cancelled`.
    /// - Throws: `CancellationError` when the task was cancelled before a child started, and
    ///   `ProcessRunnerError` when the request is invalid or the child cannot be started.
    ///
    /// Cancelling the awaiting task asks the supervisor to stop the child, and the stop completes only
    /// when this call returns, up to `killGracePeriod` later. That makes cancelling from
    /// `applicationWillTerminate` too late: the app exits before the child has been reaped. The
    /// lifecycle owner has to return `.terminateLater` from `applicationShouldTerminate`, cancel the
    /// work, wait for these calls to return, and then call `NSApp.reply(toApplicationShouldTerminate:)`.
    static func run(
        _ executableURL: URL,
        arguments: [String] = [],
        environment: [String: String]? = nil,
        standardInput: Data? = nil,
        timeout: Duration,
        outputLimit: Int = defaultOutputLimit,
        killGracePeriod: Duration = defaultKillGracePeriod
    ) async throws -> Outcome {
        let command = try SpawnCommand(
            executableURL: executableURL,
            arguments: arguments,
            environment: environment ?? ProcessInfo.processInfo.environment)
        try Task.checkCancellation()

        let child = ChildProcess(outputLimit: max(0, outputLimit), timeout: timeout, killGracePeriod: killGracePeriod)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                child.start(command, standardInput: standardInput, continuation: continuation)
            }
        } onCancel: {
            child.requestStop(.cancelled)
        }
    }

    /// The first executable regular file called `name` in the directories of `searchPath`, in order.
    ///
    /// Resolving a tool to an absolute path here is what lets a caller run the tool it means. An app
    /// opened from Finder inherits a minimal `PATH` that has no Homebrew directory in it, and handing
    /// the lookup to `/usr/bin/env` instead means the tool's name has to travel as the first argument,
    /// which is easy to lose. `name` must be a bare file name; relative directories are skipped.
    static func locateExecutable(named name: String, searchPath: [String]) -> URL? {
        guard !name.isEmpty, name != ".", name != "..", !name.contains("/") else { return nil }

        let fileManager = FileManager.default
        for directory in searchPath where directory.hasPrefix("/") {
            let candidate = URL(fileURLWithPath: directory, isDirectory: true)
                .appendingPathComponent(name, isDirectory: false)
            let path = candidate.path(percentEncoded: false)
            var isDirectory: ObjCBool = false
            if fileManager.fileExists(atPath: path, isDirectory: &isDirectory), !isDirectory.boolValue,
                fileManager.isExecutableFile(atPath: path)
            {
                return candidate
            }
        }
        return nil
    }

    /// Homebrew's two prefixes, where an app opened from Finder never looks, then each absolute
    /// directory of `PATH`, without repeats.
    static func defaultSearchPath(environment: [String: String] = ProcessInfo.processInfo.environment) -> [String] {
        var directories = ["/opt/homebrew/bin", "/usr/local/bin"]
        for entry in (environment["PATH"] ?? "").split(separator: ":") {
            let directory = String(entry)
            if directory.hasPrefix("/"), !directories.contains(directory) {
                directories.append(directory)
            }
        }
        return directories
    }
}

enum ProcessRunnerError: Error, Equatable {
    /// The request cannot be run as given: an executable that is not an absolute file URL, a NUL
    /// character in an argument or the environment, or an environment name that is empty or holds `=`.
    case invalidRequest
    /// The child could not be started. `errno` is the POSIX error, such as `ENOENT` for a missing
    /// executable or `EACCES` for a file that is not executable.
    case launchFailed(errno: Int32)
}

/// The validated strings `posix_spawn` receives. Checked before anything is created, so a bad
/// request never leaves a descriptor or a thread behind.
private struct SpawnCommand: Sendable {
    let path: String
    let argumentVector: [String]
    let environmentStrings: [String]

    init(executableURL: URL, arguments: [String], environment: [String: String]) throws {
        let path = executableURL.path(percentEncoded: false)
        guard executableURL.isFileURL, path.hasPrefix("/") else {
            throw ProcessRunnerError.invalidRequest
        }

        let argumentVector = [path] + arguments
        guard !argumentVector.contains(where: { $0.utf8.contains(0) }) else {
            throw ProcessRunnerError.invalidRequest
        }

        var environmentStrings: [String] = []
        environmentStrings.reserveCapacity(environment.count)
        for (name, value) in environment {
            guard !name.isEmpty, !name.contains("="), !name.utf8.contains(0), !value.utf8.contains(0) else {
                throw ProcessRunnerError.invalidRequest
            }
            environmentStrings.append("\(name)=\(value)")
        }

        self.path = path
        self.argumentVector = argumentVector
        self.environmentStrings = environmentStrings
    }
}

/// One run as seen from outside its supervisor thread: the continuation, a stop request and the
/// queue that wakes the supervisor. The continuation is resumed exactly once, by whichever of
/// `start` (for a run cancelled before it began) and `deliver` first marks the run delivered.
private final class ChildProcess: Sendable {
    private struct Shared: Sendable {
        var continuation: CheckedContinuation<ProcessRunner.Outcome, any Error>?
        var delivered = false
        var requestedStop: ProcessRunner.TerminationReason?
        var eventQueue: Int32 = -1
    }

    let outputLimit: Int
    let timeout: Duration
    let killGracePeriod: Duration
    private let shared = OSAllocatedUnfairLock(initialState: Shared())

    init(outputLimit: Int, timeout: Duration, killGracePeriod: Duration) {
        self.outputLimit = outputLimit
        self.timeout = timeout
        self.killGracePeriod = killGracePeriod
    }

    func start(
        _ command: SpawnCommand,
        standardInput: Data?,
        continuation: CheckedContinuation<ProcessRunner.Outcome, any Error>
    ) {
        let cancelledAlready = shared.withLock { current -> Bool in
            current.continuation = continuation
            return current.requestedStop != nil
        }
        guard !cancelledAlready else {
            deliver(.failure(CancellationError()))
            return
        }

        do {
            try SupervisorThread.start(name: "ScribeProcessRunner") { thread in
                let result = Supervision(owner: self, command: command, standardInput: standardInput).run()
                thread.whenEnded {
                    self.deliver(result)
                }
            }
        } catch {
            deliver(.failure(error))
        }
    }

    /// Records the request and wakes the supervisor if it is already waiting on its queue; one that
    /// is still starting reads the request before it waits.
    func requestStop(_ reason: ProcessRunner.TerminationReason) {
        shared.withLock { current in
            guard !current.delivered, current.requestedStop == nil else { return }
            current.requestedStop = reason
            guard current.eventQueue >= 0 else { return }
            var trigger = kevent(
                ident: Supervision.stopEvent,
                filter: Int16(truncatingIfNeeded: EVFILT_USER),
                flags: 0,
                fflags: UInt32(truncatingIfNeeded: NOTE_TRIGGER),
                data: 0,
                udata: nil)
            _ = kevent(current.eventQueue, &trigger, 1, nil, 0, nil)
        }
    }

    var requestedStop: ProcessRunner.TerminationReason? {
        shared.withLock { $0.requestedStop }
    }

    /// Called by the supervisor with its queue once `requestStop` may trigger it, and with -1 before it
    /// closes the queue. Both happen under the lock `requestStop` holds while triggering, so a trigger
    /// never reaches a queue that has been closed or a descriptor that has been reused.
    func publishEventQueue(_ descriptor: Int32) {
        shared.withLock { $0.eventQueue = descriptor }
    }

    private func deliver(_ result: Result<ProcessRunner.Outcome, any Error>) {
        let continuation = shared.withLock { current -> CheckedContinuation<ProcessRunner.Outcome, any Error>? in
            guard !current.delivered else { return nil }
            current.delivered = true
            defer { current.continuation = nil }
            return current.continuation
        }
        continuation?.resume(with: result)
    }
}

/// Everything one run owns, driven from its supervisor thread by one kqueue: the child's exit, both
/// output pipes, the input pipe, the deadline, the kill grace period, the drain limit and stop
/// requests. Only the supervisor thread touches it, so none of it needs a lock.
private final class Supervision {
    static let stopEvent: UInt = 1

    private enum TimerEvent: UInt {
        case deadline = 1
        case killGrace = 2
        case drainLimit = 3
        case exitPoll = 4
    }

    private static let readBufferSize = 64 * 1024
    private static let writeChunkSize = 64 * 1024

    private let owner: ChildProcess
    private let command: SpawnCommand
    private let clock = SuspendingClock()
    private let readBuffer = UnsafeMutableRawPointer.allocate(byteCount: Supervision.readBufferSize, alignment: 1)

    private var queue: Int32 = -1
    private var pid: pid_t = 0
    private var startedAt: SuspendingClock.Instant?
    private let standardOutput: CapturedStream
    private let standardError: CapturedStream
    private var inputDescriptor: Int32 = -1
    private var pendingInput: Data?
    private var inputOffset = 0

    private var stopReason: ProcessRunner.TerminationReason?
    private var leaderExited = false
    private var sentKill = false
    private var reaped = false
    private var exitStatus: Int32?
    private var terminationSignal: Int32?

    init(owner: ChildProcess, command: SpawnCommand, standardInput: Data?) {
        self.owner = owner
        self.command = command
        self.pendingInput = standardInput
        self.standardOutput = CapturedStream(limit: owner.outputLimit)
        self.standardError = CapturedStream(limit: owner.outputLimit)
    }

    deinit {
        readBuffer.deallocate()
    }

    /// Spawns the child, drives it to its outcome, and closes everything the run opened before
    /// returning.
    func run() -> Result<ProcessRunner.Outcome, any Error> {
        defer { closeEverything() }
        do {
            try prepare()
        } catch {
            return .failure(error)
        }
        while !isFinished {
            waitForEvents()
        }
        return .success(outcome())
    }

    private var isFinished: Bool {
        reaped && !standardOutput.isOpen && !standardError.isOpen && inputDescriptor < 0
    }

    private var outputStreamsClosed: Bool {
        !standardOutput.isOpen && !standardError.isOpen
    }

    // MARK: - Starting

    private func prepare() throws {
        queue = kqueue()
        guard queue >= 0 else { throw ProcessRunnerError.launchFailed(errno: errno) }
        guard register(ident: Self.stopEvent, filter: EVFILT_USER, flags: EV_ADD | EV_CLEAR) else {
            throw ProcessRunnerError.launchFailed(errno: errno)
        }
        owner.publishEventQueue(queue)

        let streams = try ChildStreams(feedsStandardInput: pendingInput != nil)
        switch streams.spawn(command) {
        case .failure(let error):
            streams.closeParentEnds()
            throw error
        case .success(let spawned):
            pid = spawned
        }

        // The child exists from here on, so nothing below throws: every path ends with it reaped.
        startedAt = clock.now
        standardOutput.open(streams.standardOutputRead)
        standardError.open(streams.standardErrorRead)
        inputDescriptor = streams.standardInputWrite ?? -1
        for descriptor in [standardOutput.descriptor, standardError.descriptor, inputDescriptor] where descriptor >= 0 {
            Self.makeNonBlocking(descriptor)
        }

        for stream in [standardOutput, standardError]
        where !register(ident: UInt(stream.descriptor), filter: EVFILT_READ, flags: EV_ADD) {
            stream.close(reachedEndOfFile: false)
        }
        if inputDescriptor >= 0 {
            if pendingInput?.isEmpty ?? true {
                closeInput()
            } else if !register(ident: UInt(inputDescriptor), filter: EVFILT_WRITE, flags: EV_ADD) {
                closeInput()
            }
        }

        let watchingExit = register(
            ident: UInt(pid), filter: EVFILT_PROC, flags: EV_ADD | EV_ONESHOT,
            fflags: UInt32(truncatingIfNeeded: NOTE_EXIT))
        // A deadline that cannot be armed would let the child run unbounded; stopping it is the safe side.
        if !arm(.deadline, after: owner.timeout) {
            beginStop(.timedOut)
        }
        if let reason = owner.requestedStop {
            beginStop(reason)
        }
        // The exit watch is registered before this check, so an exit after the check still fires the
        // watch, and an exit before the watch existed is seen here.
        if childHasExited() {
            leaderDidExit()
        } else if !watchingExit {
            arm(.exitPoll, after: .milliseconds(50), repeating: true)
        }
    }

    // MARK: - Waiting

    private func waitForEvents() {
        let capacity: Int32 = 8
        var events: [kevent] = Array(repeating: kevent(), count: Int(capacity))
        let count = kevent(queue, nil, 0, &events, capacity, nil)
        guard count >= 0 else {
            if errno != EINTR {
                endAfterQueueFailure()
            }
            return
        }
        for event in events.prefix(Int(count)) {
            handle(event)
        }
    }

    private func handle(_ event: kevent) {
        switch Int32(event.filter) {
        case EVFILT_READ:
            // Compared with the streams' current descriptors, so an event queued for a stream that has
            // just been closed is ignored even if the number now belongs to something else.
            let descriptor = Int32(truncatingIfNeeded: event.ident)
            if descriptor == standardOutput.descriptor {
                drain(standardOutput)
            } else if descriptor == standardError.descriptor {
                drain(standardError)
            }
        case EVFILT_WRITE:
            if Int32(truncatingIfNeeded: event.ident) == inputDescriptor {
                writeInput()
            }
        case EVFILT_PROC:
            leaderDidExit()
        case EVFILT_TIMER:
            switch TimerEvent(rawValue: event.ident) {
            case .deadline:
                beginStop(.timedOut)
            case .killGrace:
                killRemainingGroup()
            case .drainLimit:
                letGoOfStreams()
            case .exitPoll:
                if childHasExited() {
                    disarm(.exitPoll)
                    leaderDidExit()
                }
            case nil:
                break
            }
        case EVFILT_USER:
            if let reason = owner.requestedStop {
                beginStop(reason)
            }
        default:
            break
        }
    }

    // MARK: - Streams

    private func drain(_ stream: CapturedStream) {
        while stream.isOpen {
            let count = Darwin.read(stream.descriptor, readBuffer, Self.readBufferSize)
            if count > 0 {
                stream.append(UnsafeRawPointer(readBuffer), count: count)
            } else if count == 0 {
                stream.close(reachedEndOfFile: true)
            } else if errno == EINTR {
                continue
            } else if errno == EAGAIN {
                return
            } else {
                stream.close(reachedEndOfFile: false)
            }
        }
        if outputStreamsClosed {
            reapIfGroupIsEmpty()
        }
    }

    private func writeInput() {
        guard inputDescriptor >= 0, let input = pendingInput else {
            closeInput()
            return
        }
        let descriptor = inputDescriptor
        var offset = inputOffset
        let finished = input.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) -> Bool in
            guard let base = bytes.baseAddress else { return true }
            while offset < bytes.count {
                let written = Darwin.write(descriptor, base + offset, min(bytes.count - offset, Self.writeChunkSize))
                if written > 0 {
                    offset += written
                } else if written < 0 && errno == EINTR {
                    continue
                } else if written < 0 && errno == EAGAIN {
                    return false
                } else {
                    // EPIPE: the child closed its input or exited, so the rest is not wanted.
                    return true
                }
            }
            return true
        }
        inputOffset = offset
        if finished {
            closeInput()
        }
    }

    private func closeInput() {
        if inputDescriptor >= 0 {
            Darwin.close(inputDescriptor)
            inputDescriptor = -1
        }
        pendingInput = nil
    }

    /// At the drain limit: take whatever is buffered, then let go of every end, standard input
    /// included, so a descendant that keeps the pipes open holds nothing of Scribe's.
    private func letGoOfStreams() {
        drain(standardOutput)
        drain(standardError)
        standardOutput.close(reachedEndOfFile: false)
        standardError.close(reachedEndOfFile: false)
        closeInput()
    }

    // MARK: - Stopping and reaping

    private func beginStop(_ reason: ProcessRunner.TerminationReason) {
        guard stopReason == nil, !leaderExited, pid > 0 else { return }
        stopReason = reason
        killpg(pid, SIGTERM)
        if !arm(.killGrace, after: owner.killGracePeriod) {
            killRemainingGroup()
        }
    }

    private func leaderDidExit() {
        guard !leaderExited else { return }
        leaderExited = true
        if stopReason == nil || sentKill {
            reap()
            return
        }
        // A stopped child that has exited stays an unreaped zombie until its group is empty or the kill
        // grace period ends. While it exists its pid, and with it the process group id, cannot be
        // reused, so descendants still cleaning up after SIGTERM keep their grace, and the SIGKILL at
        // the end of it can reach only this run's group.
        reapIfGroupIsEmpty()
    }

    private func killRemainingGroup() {
        guard !reaped, !sentKill, pid > 0 else { return }
        killpg(pid, SIGKILL)
        sentKill = true
        if leaderExited {
            reap()
        }
    }

    /// Once the leader and every holder of the output pipes are gone, a stopped run need not wait out
    /// the grace period: reaping early is safe when no live process remains in the group.
    private func reapIfGroupIsEmpty() {
        guard leaderExited, !reaped, outputStreamsClosed, !Self.groupHasLiveMembers(leader: pid) else { return }
        reap()
    }

    private func reap() {
        guard !reaped else { return }
        var status: Int32 = 0
        var result: pid_t
        repeat {
            result = waitpid(pid, &status, 0)
        } while result == -1 && errno == EINTR
        let decoded: (exitStatus: Int32?, signal: Int32?) =
            result == pid ? Self.decodeWaitStatus(status) : (exitStatus: nil, signal: nil)
        exitStatus = decoded.exitStatus
        terminationSignal = decoded.signal
        reaped = true
        disarm(.killGrace)
        if !isFinished, !arm(.drainLimit, after: ProcessRunner.postExitDrainLimit) {
            letGoOfStreams()
        }
    }

    private func childHasExited() -> Bool {
        var info = siginfo_t()
        let result = waitid(P_PID, id_t(pid), &info, WEXITED | WNOHANG | WNOWAIT)
        if result == -1 {
            // ECHILD: something else in Scribe reaped the child, so it is certainly gone.
            return errno == ECHILD
        }
        return info.si_pid == pid
    }

    /// `kevent` itself failed, which leaves nothing to wait on. The one safe way out is to end the run
    /// now: kill the group, reap the child and let go of every stream.
    private func endAfterQueueFailure() {
        if !reaped {
            if stopReason == nil {
                stopReason = .cancelled
            }
            if !sentKill {
                killpg(pid, SIGKILL)
                sentKill = true
            }
            reap()
        }
        letGoOfStreams()
    }

    private func closeEverything() {
        standardOutput.close(reachedEndOfFile: false)
        standardError.close(reachedEndOfFile: false)
        closeInput()
        if queue >= 0 {
            owner.publishEventQueue(-1)
            Darwin.close(queue)
            queue = -1
        }
    }

    private func outcome() -> ProcessRunner.Outcome {
        let now = clock.now
        return ProcessRunner.Outcome(
            terminationReason: stopReason ?? .finished,
            exitStatus: exitStatus,
            terminationSignal: terminationSignal,
            standardOutput: standardOutput.captured,
            standardError: standardError.captured,
            duration: (startedAt ?? now).duration(to: now))
    }

    // MARK: - Kernel helpers

    @discardableResult
    private func register(ident: UInt, filter: Int32, flags: Int32, fflags: UInt32 = 0, data: Int = 0) -> Bool {
        var change = kevent(
            ident: ident,
            filter: Int16(truncatingIfNeeded: filter),
            flags: UInt16(truncatingIfNeeded: flags),
            fflags: fflags,
            data: data,
            udata: nil)
        return kevent(queue, &change, 1, nil, 0, nil) == 0
    }

    @discardableResult
    private func arm(_ timer: TimerEvent, after duration: Duration, repeating: Bool = false) -> Bool {
        register(
            ident: timer.rawValue,
            filter: EVFILT_TIMER,
            flags: repeating ? EV_ADD : EV_ADD | EV_ONESHOT,
            fflags: UInt32(truncatingIfNeeded: NOTE_NSECONDS),
            data: duration.timerNanoseconds)
    }

    private func disarm(_ timer: TimerEvent) {
        register(ident: timer.rawValue, filter: EVFILT_TIMER, flags: EV_DELETE)
    }

    private static func makeNonBlocking(_ descriptor: Int32) {
        let flags = fcntl(descriptor, F_GETFL)
        if flags >= 0 {
            _ = fcntl(descriptor, F_SETFL, flags | O_NONBLOCK)
        }
    }

    /// Whether any process other than `leader` in the process group `leader` leads is still alive.
    /// Zombies do not count: an exited descendant waiting for launchd to reap it can no longer be
    /// signalled or do anything. When the kernel cannot say, the answer is yes, which only means
    /// waiting for the end of the grace period.
    private static func groupHasLiveMembers(leader: pid_t) -> Bool {
        var members = [pid_t](repeating: 0, count: 256)
        let reported = members.withUnsafeMutableBytes { buffer in
            proc_listpgrppids(leader, buffer.baseAddress, Int32(buffer.count))
        }
        guard reported >= 0, Int(reported) < members.count else { return true }
        for member in members.prefix(Int(reported)) where member > 0 && member != leader {
            var info = proc_bsdinfo()
            let size = Int32(MemoryLayout<proc_bsdinfo>.size)
            errno = 0
            if proc_pidinfo(member, PROC_PIDTBSDINFO, 0, &info, size) == size {
                if info.pbi_status != UInt32(SZOMB) {
                    return true
                }
            } else if errno != ESRCH {
                // ESRCH: the member has exited, or is a zombie, which this query does not find. Any other
                // failure leaves the member's state unknown.
                return true
            }
        }
        return false
    }

    /// `WIFEXITED`, `WEXITSTATUS`, `WIFSIGNALED` and `WTERMSIG` are C macros Swift cannot import.
    private static func decodeWaitStatus(_ status: Int32) -> (exitStatus: Int32?, signal: Int32?) {
        let low = status & 0x7f
        if low == 0 {
            return ((status >> 8) & 0xff, nil)
        }
        if low != 0x7f {
            return (nil, low)
        }
        return (nil, nil)
    }
}

/// One output stream on the supervisor thread: its descriptor while open, the first `limit` bytes,
/// and the count of every byte.
private final class CapturedStream {
    private(set) var descriptor: Int32 = -1
    private let limit: Int
    private var data = Data()
    private var totalByteCount = 0
    private var reachedEndOfFile = false

    init(limit: Int) {
        self.limit = limit
    }

    var isOpen: Bool { descriptor >= 0 }

    func open(_ descriptor: Int32) {
        self.descriptor = descriptor
    }

    func append(_ bytes: UnsafeRawPointer, count: Int) {
        totalByteCount += count
        let room = limit - data.count
        if room > 0 {
            data.append(bytes.assumingMemoryBound(to: UInt8.self), count: min(room, count))
        }
    }

    func close(reachedEndOfFile: Bool) {
        guard descriptor >= 0 else { return }
        Darwin.close(descriptor)
        descriptor = -1
        self.reachedEndOfFile = reachedEndOfFile
    }

    var captured: ProcessRunner.CapturedOutput {
        ProcessRunner.CapturedOutput(data: data, totalByteCount: totalByteCount, reachedEndOfFile: reachedEndOfFile)
    }
}

/// The pipes a child writes to and reads from. The parent's ends are close-on-exec, so no other child
/// can inherit them, and the child's ends are closed in the parent right after the spawn, so each
/// stream reaches end of file as soon as the child and its descendants are done with it.
private struct ChildStreams {
    let standardOutputRead: Int32
    let standardErrorRead: Int32
    let standardInputWrite: Int32?
    private let standardOutputWrite: Int32
    private let standardErrorWrite: Int32
    private let standardInputRead: Int32?

    init(feedsStandardInput: Bool) throws {
        var created: [Int32] = []
        do {
            let outputPipe = try Self.makePipe()
            created += [outputPipe.read, outputPipe.write]
            let errorPipe = try Self.makePipe()
            created += [errorPipe.read, errorPipe.write]
            var input: (read: Int32, write: Int32)?
            if feedsStandardInput {
                let inputPipe = try Self.makePipe()
                created += [inputPipe.read, inputPipe.write]
                // A child that exits without reading all of its input must make the write fail with
                // EPIPE, not raise SIGPIPE and end Scribe.
                _ = fcntl(inputPipe.write, F_SETNOSIGPIPE, 1)
                input = inputPipe
            }
            standardOutputRead = outputPipe.read
            standardOutputWrite = outputPipe.write
            standardErrorRead = errorPipe.read
            standardErrorWrite = errorPipe.write
            standardInputRead = input?.read
            standardInputWrite = input?.write
        } catch {
            for descriptor in created {
                close(descriptor)
            }
            throw error
        }
    }

    func spawn(_ command: SpawnCommand) -> Result<pid_t, ProcessRunnerError> {
        var actions: posix_spawn_file_actions_t?
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        if let standardInputRead {
            posix_spawn_file_actions_adddup2(&actions, standardInputRead, STDIN_FILENO)
        } else {
            posix_spawn_file_actions_addopen(&actions, STDIN_FILENO, "/dev/null", O_RDONLY, 0)
        }
        posix_spawn_file_actions_adddup2(&actions, standardOutputWrite, STDOUT_FILENO)
        posix_spawn_file_actions_adddup2(&actions, standardErrorWrite, STDERR_FILENO)

        var attributes: posix_spawnattr_t?
        posix_spawnattr_init(&attributes)
        defer { posix_spawnattr_destroy(&attributes) }
        // A new process group (so a stop can reach the child's descendants), every signal unblocked
        // and at its default action (an ignored SIGPIPE or SIGTERM in Scribe must not carry over), and
        // only the descriptors set up above (POSIX_SPAWN_CLOEXEC_DEFAULT is Apple's).
        posix_spawnattr_setpgroup(&attributes, 0)
        var noSignals = sigset_t()
        sigemptyset(&noSignals)
        posix_spawnattr_setsigmask(&attributes, &noSignals)
        var defaultSignals = sigset_t()
        sigfillset(&defaultSignals)
        sigdelset(&defaultSignals, SIGKILL)
        sigdelset(&defaultSignals, SIGSTOP)
        posix_spawnattr_setsigdefault(&attributes, &defaultSignals)
        let flags = POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_CLOEXEC_DEFAULT
        posix_spawnattr_setflags(&attributes, Int16(flags))

        let arguments = CStringArray(command.argumentVector)
        let environment = CStringArray(command.environmentStrings)
        guard arguments.isComplete, environment.isComplete else {
            closeChildEnds()
            return .failure(.launchFailed(errno: ENOMEM))
        }

        var pid: pid_t = 0
        let result = posix_spawn(&pid, command.path, &actions, &attributes, arguments.pointers, environment.pointers)
        closeChildEnds()
        return result == 0 ? .success(pid) : .failure(.launchFailed(errno: result))
    }

    func closeParentEnds() {
        close(standardOutputRead)
        close(standardErrorRead)
        if let standardInputWrite {
            close(standardInputWrite)
        }
    }

    private func closeChildEnds() {
        close(standardOutputWrite)
        close(standardErrorWrite)
        if let standardInputRead {
            close(standardInputRead)
        }
    }

    private static func makePipe() throws -> (read: Int32, write: Int32) {
        var descriptors: [Int32] = [-1, -1]
        guard pipe(&descriptors) == 0 else {
            throw ProcessRunnerError.launchFailed(errno: errno)
        }
        for descriptor in descriptors {
            _ = fcntl(descriptor, F_SETFD, FD_CLOEXEC)
        }
        return (descriptors[0], descriptors[1])
    }
}

/// A NUL-terminated array of C strings for `posix_spawn`, freed with the array.
private final class CStringArray {
    let pointers: [UnsafeMutablePointer<CChar>?]

    init(_ strings: [String]) {
        pointers = strings.map { strdup($0) } + [nil]
    }

    /// `false` when `strdup` ran out of memory for one of the strings.
    var isComplete: Bool { !pointers.dropLast().contains(nil) }

    deinit {
        for case let pointer? in pointers {
            free(pointer)
        }
    }
}

/// A joinable POSIX thread. The run's outcome is delivered only after the thread that owned its child,
/// descriptors and buffers has ended, so a caller holding an outcome holds nothing else of the run.
private enum SupervisorThread {
    /// The thread, as a number so it can travel in a `@Sendable` closure.
    struct Handle: Sendable {
        fileprivate let bits: UInt

        /// Runs `body` on a global queue once the thread has returned.
        func whenEnded(_ body: @escaping @Sendable () -> Void) {
            let bits = bits
            DispatchQueue.global(qos: .userInitiated).async {
                if let thread = pthread_t(bitPattern: bits) {
                    pthread_join(thread, nil)
                }
                body()
            }
        }
    }

    private final class Start: Sendable {
        let name: String
        let body: @Sendable (Handle) -> Void

        init(name: String, body: @escaping @Sendable (Handle) -> Void) {
            self.name = name
            self.body = body
        }
    }

    /// Starts `body` on a new thread and passes it the thread's own handle, which the body must hand to
    /// `Handle.whenEnded` exactly once, since the thread is joinable and is released only by that join.
    static func start(name: String, _ body: @escaping @Sendable (Handle) -> Void) throws {
        var attributes = pthread_attr_t()
        pthread_attr_init(&attributes)
        defer { pthread_attr_destroy(&attributes) }
        pthread_attr_set_qos_class_np(&attributes, QOS_CLASS_USER_INITIATED, 0)

        let context = Unmanaged.passRetained(Start(name: name, body: body)).toOpaque()
        var thread: pthread_t?
        let result = pthread_create(
            &thread, &attributes,
            { argument in
                let pointer: UnsafeMutableRawPointer? = argument
                guard let pointer else { return nil }
                let entry = Unmanaged<Start>.fromOpaque(pointer).takeRetainedValue()
                pthread_setname_np(entry.name)
                entry.body(Handle(bits: UInt(bitPattern: pthread_self())))
                return nil
            }, context)
        guard result == 0 else {
            Unmanaged<Start>.fromOpaque(context).release()
            throw ProcessRunnerError.launchFailed(errno: result)
        }
    }
}

extension Duration {
    /// Nanoseconds for a kqueue timer: clamped to zero below and to a year above, which is "never" for
    /// any deadline in this app and keeps the count far from overflowing.
    fileprivate var timerNanoseconds: Int {
        let (seconds, attoseconds) = components
        let year: Int64 = 365 * 24 * 60 * 60
        if seconds >= year {
            return Int(year) * 1_000_000_000
        }
        if seconds < 0 || (seconds == 0 && attoseconds <= 0) {
            return 0
        }
        return Int(seconds) * 1_000_000_000 + Int(attoseconds / 1_000_000_000)
    }
}
