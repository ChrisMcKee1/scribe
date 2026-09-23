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
/// - reads standard output and standard error from the moment the child starts, each on its own
///   thread, keeping at most `outputLimit` bytes of each and counting the rest, so a chatty child
///   never stalls on a full pipe;
/// - writes `standardInput` on its own thread too, or connects `/dev/null`, so a child never waits on
///   a terminal, and never waits on a parent that has not read its output yet;
/// - enforces `timeout` and honours task cancellation: the child's process group gets `SIGTERM`, then
///   `SIGKILL` after `killGracePeriod`, so a wrapper script (Homebrew's `az` is a shell script that
///   starts Python) cannot leave a grandchild running or holding the pipes;
/// - starts the child in a new process group with only descriptors 0, 1 and 2, so a child started
///   concurrently elsewhere can never inherit this child's pipes and hold back its end of file;
/// - does every blocking step (spawning, reading, writing, waiting for the exit) on threads of its
///   own, so an awaiting caller, the main actor included, is only ever suspended.
///
/// It never logs. Captured output is content (an `az` access token, a transcript): callers must not
/// log it, only its shape, such as byte counts, the exit status and the termination reason.
enum ProcessRunner {
    /// Bytes kept from each output stream unless a caller asks for another limit.
    static let defaultOutputLimit = 1 << 20

    /// How long a stopped child has between `SIGTERM` and `SIGKILL` unless a caller asks otherwise.
    static let defaultKillGracePeriod: Duration = .seconds(2)

    /// How long to keep waiting for end of file after the child has exited. Everything the child
    /// wrote is in the pipes by then; this only bounds the wait on a descendant that kept a stream
    /// open, such as a daemon the child started, which must not hold the outcome back.
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
        /// `false` when something still held the stream open as the outcome was taken, which
        /// happens only when a descendant of the child outlived it.
        let reachedEndOfFile: Bool

        var isTruncated: Bool { totalByteCount > data.count }

        /// The kept bytes as UTF-8, with any invalid sequence replaced.
        var text: String { String(decoding: data, as: UTF8.self) }
    }

    /// How a child ended and what it wrote.
    struct Outcome: Sendable, Equatable {
        let terminationReason: TerminationReason
        /// The status the child passed to `exit`, when it exited rather than being killed by a signal.
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
    ///   - killGracePeriod: How long a stopped child has between `SIGTERM` and `SIGKILL`.
    /// - Returns: An outcome for every child that started, however it ended. A stopped child is
    ///   reported, not thrown: the outcome says `.timedOut` or `.cancelled`.
    /// - Throws: `CancellationError` when the task was cancelled before a child started, and
    ///   `ProcessRunnerError` when the request is invalid or the child cannot be started.
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
            child.stop(because: .cancelled)
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

/// One child from spawn to outcome.
///
/// Every mutable fact lives in `state` behind one lock. The continuation is resumed exactly once, by
/// whichever path first marks the run delivered: a cancellation before the spawn, a spawn failure, or
/// the finish once the child is reaped and its streams are drained. Signals are only ever sent while
/// the child has not been seen to exit; after that its pid may belong to another process.
private final class ChildProcess: Sendable {
    private enum Launch: Sendable, Equatable {
        case pending
        case spawning
        case running(pid_t)
    }

    private struct State: Sendable {
        var continuation: CheckedContinuation<ProcessRunner.Outcome, any Error>?
        var delivered = false
        var launch = Launch.pending
        var stopReason: ProcessRunner.TerminationReason?
        var sentKill = false
        var exited = false
        var reaped = false
        var exitStatus: Int32?
        var terminationSignal: Int32?
        var openStreams = 2
        var startedAt: SuspendingClock.Instant?
    }

    private let state = OSAllocatedUnfairLock(initialState: State())
    private let standardOutput: OutputCollector
    private let standardError: OutputCollector
    private let timeout: Duration
    private let killGracePeriod: Duration
    private let clock = SuspendingClock()

    init(outputLimit: Int, timeout: Duration, killGracePeriod: Duration) {
        self.standardOutput = OutputCollector(limit: outputLimit)
        self.standardError = OutputCollector(limit: outputLimit)
        self.timeout = timeout
        self.killGracePeriod = killGracePeriod
    }

    func start(
        _ command: SpawnCommand,
        standardInput: Data?,
        continuation: CheckedContinuation<ProcessRunner.Outcome, any Error>
    ) {
        let proceed = state.withLock { current -> Bool in
            current.continuation = continuation
            guard current.stopReason == nil else { return false }
            current.launch = .spawning
            return true
        }
        guard proceed else {
            deliver(.failure(CancellationError()))
            return
        }

        // Spawning forks, which is too slow for a caller that may be the main actor.
        DispatchQueue.global(qos: .userInitiated).async {
            self.spawn(command, standardInput: standardInput)
        }
    }

    func stop(because reason: ProcessRunner.TerminationReason) {
        let signalled = state.withLock { current -> Bool in
            guard !current.delivered, !current.exited, current.stopReason == nil else { return false }
            current.stopReason = reason
            // While the spawn is still under way there is no pid yet; `spawn` sends the signal as soon
            // as there is one.
            guard case .running(let pid) = current.launch else { return false }
            killpg(pid, SIGTERM)
            return true
        }
        if signalled {
            scheduleKill()
        }
    }

    // MARK: - Spawning

    private func spawn(_ command: SpawnCommand, standardInput: Data?) {
        let streams: ChildStreams
        do {
            streams = try ChildStreams(feedsStandardInput: standardInput != nil)
        } catch {
            deliver(.failure(error))
            return
        }

        let pid: pid_t
        switch streams.spawn(command) {
        case .failure(let error):
            streams.closeParentEnds()
            deliver(.failure(error))
            return
        case .success(let spawnedPid):
            pid = spawnedPid
        }

        let startedAt = clock.now
        let stopPending = state.withLock { current -> Bool in
            current.launch = .running(pid)
            current.startedAt = startedAt
            guard current.stopReason != nil else { return false }
            killpg(pid, SIGTERM)
            return true
        }

        startThread(named: "ScribeProcessRunner.wait") { self.waitForExit(of: pid) }
        let outputDescriptor = streams.standardOutputRead
        let errorDescriptor = streams.standardErrorRead
        startThread(named: "ScribeProcessRunner.stdout") {
            self.readStream(outputDescriptor, into: self.standardOutput)
        }
        startThread(named: "ScribeProcessRunner.stderr") {
            self.readStream(errorDescriptor, into: self.standardError)
        }
        if let standardInput, let inputDescriptor = streams.standardInputWrite {
            startThread(named: "ScribeProcessRunner.stdin") {
                Self.writeStandardInput(standardInput, to: inputDescriptor)
            }
        }

        DispatchQueue.global(qos: .userInitiated).asyncAfter(deadline: .now() + timeout.dispatchInterval) {
            [weak self] in
            self?.stop(because: .timedOut)
        }
        if stopPending {
            scheduleKill()
        }
    }

    private func startThread(named name: String, _ body: @escaping @Sendable () -> Void) {
        let thread = Thread(block: body)
        thread.name = name
        thread.qualityOfService = .userInitiated
        thread.start()
    }

    private func scheduleKill() {
        DispatchQueue.global(qos: .userInitiated).asyncAfter(deadline: .now() + killGracePeriod.dispatchInterval) {
            [weak self] in
            self?.killIfStillRunning()
        }
    }

    private func killIfStillRunning() {
        state.withLock { current in
            guard !current.exited, !current.sentKill, case .running(let pid) = current.launch else { return }
            killpg(pid, SIGKILL)
            current.sentKill = true
        }
    }

    // MARK: - Threads

    private func waitForExit(of pid: pid_t) {
        var info = siginfo_t()
        var observed: Int32
        repeat {
            observed = waitid(P_PID, id_t(pid), &info, WEXITED | WNOWAIT)
        } while observed == -1 && errno == EINTR

        let heldAsZombie = observed == 0
        if heldAsZombie {
            // The exited child stays an unreaped zombie until `waitpid` below, and until then its pid,
            // and with it the process group id, cannot be reused. That makes this the last moment a
            // group signal is certain to reach only this child's descendants: a stopped run ends them
            // all, while a run that finished by itself leaves whatever it started alone.
            state.withLock { current in
                current.exited = true
                if current.stopReason != nil, !current.sentKill {
                    killpg(pid, SIGKILL)
                    current.sentKill = true
                }
            }
        }

        var status: Int32 = 0
        var reaped: pid_t
        repeat {
            reaped = waitpid(pid, &status, 0)
        } while reaped == -1 && errno == EINTR

        let decoded: (exitStatus: Int32?, signal: Int32?) =
            reaped == pid ? Self.decodeWaitStatus(status) : (exitStatus: nil, signal: nil)
        state.withLock { current in
            current.exited = true
            current.reaped = true
            current.exitStatus = decoded.exitStatus
            current.terminationSignal = decoded.signal
        }

        DispatchQueue.global(qos: .utility).asyncAfter(
            deadline: .now() + ProcessRunner.postExitDrainLimit.dispatchInterval
        ) { [weak self] in
            self?.finishIfDone(drainLimitReached: true)
        }
        finishIfDone(drainLimitReached: false)
    }

    private func readStream(_ descriptor: Int32, into collector: OutputCollector) {
        let capacity = 64 * 1024
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: capacity, alignment: 1)
        defer {
            buffer.deallocate()
            close(descriptor)
        }

        while true {
            let count = read(descriptor, buffer, capacity)
            if count > 0 {
                collector.append(UnsafeRawPointer(buffer), count: count)
            } else if count == 0 || errno != EINTR {
                break
            }
        }

        collector.markEndOfFile()
        state.withLock { $0.openStreams -= 1 }
        finishIfDone(drainLimitReached: false)
    }

    private static func writeStandardInput(_ data: Data, to descriptor: Int32) {
        defer { close(descriptor) }
        data.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) in
            guard let base = bytes.baseAddress else { return }
            var offset = 0
            while offset < bytes.count {
                let written = write(descriptor, base + offset, bytes.count - offset)
                if written > 0 {
                    offset += written
                } else if written < 0 && errno == EINTR {
                    continue
                } else {
                    // EPIPE: the child closed its input or exited, so the rest is not wanted.
                    return
                }
            }
        }
    }

    // MARK: - Finishing

    private func finishIfDone(drainLimitReached: Bool) {
        let delivery = state.withLock { current -> Delivery? in
            guard !current.delivered, current.reaped, current.openStreams == 0 || drainLimitReached,
                let continuation = current.continuation, let startedAt = current.startedAt
            else {
                return nil
            }
            current.delivered = true
            current.continuation = nil
            let outcome = ProcessRunner.Outcome(
                terminationReason: current.stopReason ?? .finished,
                exitStatus: current.exitStatus,
                terminationSignal: current.terminationSignal,
                standardOutput: standardOutput.seal(),
                standardError: standardError.seal(),
                duration: startedAt.duration(to: clock.now))
            return Delivery(continuation: continuation, outcome: outcome)
        }
        if let delivery {
            delivery.continuation.resume(returning: delivery.outcome)
        }
    }

    private struct Delivery: Sendable {
        let continuation: CheckedContinuation<ProcessRunner.Outcome, any Error>
        let outcome: ProcessRunner.Outcome
    }

    private func deliver(_ result: Result<ProcessRunner.Outcome, any Error>) {
        let continuation = state.withLock { current -> CheckedContinuation<ProcessRunner.Outcome, any Error>? in
            guard !current.delivered else { return nil }
            current.delivered = true
            defer { current.continuation = nil }
            return current.continuation
        }
        continuation?.resume(with: result)
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
        // A new process group (so a stop can reach every descendant), every signal unblocked and at
        // its default action (an ignored SIGPIPE or SIGTERM in Scribe must not carry over), and only
        // the descriptors set up above (POSIX_SPAWN_CLOEXEC_DEFAULT is Apple's).
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

/// One stream's bytes: the first `limit` kept, every byte counted, and nothing kept once the outcome
/// has been taken, so a descendant that holds the stream open after the outcome costs no memory.
private final class OutputCollector: Sendable {
    private struct Buffer: Sendable {
        var data = Data()
        var totalByteCount = 0
        var reachedEndOfFile = false
        var sealed = false
    }

    private let limit: Int
    private let buffer = OSAllocatedUnfairLock(initialState: Buffer())

    init(limit: Int) {
        self.limit = limit
    }

    /// Called only from the stream's one reader thread, so `data` cannot grow between the two locked
    /// steps; the second only has to recheck `sealed`.
    func append(_ bytes: UnsafeRawPointer, count: Int) {
        let limit = limit
        let room = buffer.withLock { current in current.sealed ? 0 : max(0, limit - current.data.count) }
        let kept = room > 0 ? Data(bytes: bytes, count: min(room, count)) : nil
        buffer.withLock { current in
            current.totalByteCount += count
            if let kept, !current.sealed {
                current.data.append(kept)
            }
        }
    }

    func markEndOfFile() {
        buffer.withLock { $0.reachedEndOfFile = true }
    }

    func seal() -> ProcessRunner.CapturedOutput {
        buffer.withLock { current in
            let captured = ProcessRunner.CapturedOutput(
                data: current.data,
                totalByteCount: current.totalByteCount,
                reachedEndOfFile: current.reachedEndOfFile)
            current.sealed = true
            current.data = Data()
            return captured
        }
    }
}

extension Duration {
    /// Clamped to zero below and to a year above, which is "never" for any deadline in this app and
    /// keeps the nanosecond count far from overflowing.
    fileprivate var dispatchInterval: DispatchTimeInterval {
        let (seconds, attoseconds) = components
        let year: Int64 = 365 * 24 * 60 * 60
        if seconds >= year {
            return .seconds(Int(year))
        }
        if seconds < 0 || (seconds == 0 && attoseconds <= 0) {
            return .nanoseconds(0)
        }
        return .nanoseconds(Int(seconds) * 1_000_000_000 + Int(attoseconds / 1_000_000_000))
    }
}
