import Darwin
import Foundation
import os

/// Why a transcription produced no text. `TranscriptionEngine` throws nothing else. None of the cases carries
/// text from the recognizer: its output can hold the transcript and its errors can hold file paths, so only
/// codes survive (PRIVACY.md), and the descriptions are Scribe's own words.
enum TranscriptionError: LocalizedError, Equatable {
    /// No recognizer is installed where Scribe looks.
    case backendMissing(TranscriptionBackendIssue)
    /// The command-line verb was given a file that is not a WAV.
    case unsupportedAudioFile
    /// The recording could not be written for the recognizer.
    case audioWriteFailed(errno: Int32)
    /// The recognizer could not be started; `errno` is the POSIX error, or 0 when there was none.
    case launchFailed(errno: Int32)
    /// The recognizer ran past its deadline and was stopped.
    case timedOut
    /// The caller cancelled the transcription and the recognizer was stopped.
    case cancelled
    /// The recognizer exited with this non-zero status and no transcript.
    case exitCode(Int32)
    /// Something outside Scribe ended the recognizer with this signal.
    case terminatedBySignal(Int32)
    /// Foundry Local answered with its structured error reply.
    case backendReportedError
    /// The recognizer succeeded but returned no words.
    case emptyOutput
    /// The recognizer's reply could not be read.
    case malformedOutput

    var errorDescription: String? {
        switch self {
        case .backendMissing(.foundryCliNotFound):
            return "Scribe could not find Foundry Local. Install it with "
                + "`brew install microsoft/foundrylocal/foundrylocal`, or set SCRIBE_FOUNDRY_CLI."
        case .backendMissing(.whisperCliNotFound):
            return "Could not find whisper-cli. Install whisper-cpp or set SCRIBE_WHISPER_CLI."
        case .backendMissing(.whisperModelNotFound):
            return "Could not find the Whisper model. Download ggml-tiny.en.bin or set SCRIBE_WHISPER_MODEL."
        case .unsupportedAudioFile:
            return "The speech recognizer reads WAV files only. Convert the file to 16 kHz mono WAV first."
        case .audioWriteFailed(let code):
            return "Scribe could not write the recording for the speech recognizer (error \(code))."
        case .launchFailed(let code):
            return "The speech recognizer could not be started (error \(code))."
        case .timedOut:
            return "The speech recognizer did not finish in time, so Scribe stopped it."
        case .cancelled:
            return "The transcription was cancelled."
        case .exitCode(let status):
            return "The speech recognizer failed with exit status \(status)."
        case .terminatedBySignal(let signal):
            return "The speech recognizer was ended by signal \(signal)."
        case .backendReportedError:
            return "Foundry Local reported an error. Run `foundry transcribe` in Terminal to see why."
        case .emptyOutput:
            return "The speech recognizer returned no text."
        case .malformedOutput:
            return "The speech recognizer's reply could not be read."
        }
    }
}

/// Which part of a recognizer is missing.
enum TranscriptionBackendIssue: Sendable, Equatable {
    case foundryCliNotFound
    case whisperCliNotFound
    case whisperModelNotFound
}

/// Foundry Local is the production recognizer (the Parakeet TDT family, as on Windows; see PORTING-PLAN.md).
/// whisper.cpp is a documented fallback for when Foundry Local is not installed.
enum TranscriptionBackendKind: String, Sendable {
    case foundryLocal
    case whisperCpp
}

struct TranscriptionBackendConfiguration: Sendable, Hashable {
    let kind: TranscriptionBackendKind
    /// `foundry` for Foundry Local, `whisper-cli` for whisper.cpp.
    let cliURL: URL
    /// whisper.cpp's model; Foundry Local manages its own model cache.
    let modelURL: URL?
    /// The Foundry Local model alias passed to `foundry transcribe -m`.
    let foundryModelAlias: String
}

/// Finds the recognizer. Order: `SCRIBE_WHISPER_CLI` with `SCRIBE_WHISPER_MODEL` when both exist, then Foundry
/// Local (`SCRIBE_FOUNDRY_CLI`, or `foundry` on the search path) unless `SCRIBE_ASR_BACKEND=whisper`, then
/// whisper.cpp from its usual places.
struct TranscriptionBackendResolver: Sendable {
    /// English only: NVIDIA publishes `parakeet-tdt-0.6b-v2` as an English model, and Foundry Local's build of
    /// it keeps the base model's capabilities. There is no language parameter to pass.
    static let defaultFoundryModelAlias = "parakeet-tdt-0.6b-v2"

    var environment: [String: String]
    /// Where `foundry` and `whisper-cli` are looked up, in order.
    var searchPath: [String]
    /// Absolute paths tried for whisper-cli before the search path.
    var whisperCliCandidates: [String]
    var whisperModelCandidates: [String]

    static func live(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> TranscriptionBackendResolver {
        let sourceRoot = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        let currentDirectory = URL(fileURLWithPath: FileManager.default.currentDirectoryPath, isDirectory: true)
        return TranscriptionBackendResolver(
            environment: environment,
            searchPath: ProcessRunner.defaultSearchPath(environment: environment),
            whisperCliCandidates: ["/opt/homebrew/opt/whisper-cpp/bin/whisper-cli"],
            whisperModelCandidates: [
                sourceRoot.appendingPathComponent("Models/whisper/ggml-tiny.en.bin").path(percentEncoded: false),
                currentDirectory.appendingPathComponent("macos/Scribe/Models/whisper/ggml-tiny.en.bin")
                    .path(percentEncoded: false),
                currentDirectory.appendingPathComponent("Models/whisper/ggml-tiny.en.bin").path(percentEncoded: false),
            ])
    }

    /// Throws `TranscriptionError.backendMissing`.
    func resolve() throws -> TranscriptionBackendConfiguration {
        let fileManager = FileManager.default
        if let cli = environment["SCRIBE_WHISPER_CLI"], let model = environment["SCRIBE_WHISPER_MODEL"],
            fileManager.isExecutableFile(atPath: cli), fileManager.fileExists(atPath: model)
        {
            return Self.whisper(cli: URL(fileURLWithPath: cli), model: URL(fileURLWithPath: model))
        }

        let forceWhisper = environment["SCRIBE_ASR_BACKEND"]?.lowercased() == "whisper"
        if !forceWhisper, let foundry = locateFoundry() {
            return TranscriptionBackendConfiguration(
                kind: .foundryLocal,
                cliURL: foundry,
                modelURL: nil,
                foundryModelAlias: environment["SCRIBE_FOUNDRY_ASR_MODEL"] ?? Self.defaultFoundryModelAlias)
        }

        let cli =
            whisperCliCandidates.first(where: fileManager.isExecutableFile(atPath:)).map { URL(fileURLWithPath: $0) }
            ?? ProcessRunner.locateExecutable(named: "whisper-cli", searchPath: searchPath)
        let model = whisperModelCandidates.first(where: fileManager.fileExists(atPath:))
        if let cli, let model {
            return Self.whisper(cli: cli, model: URL(fileURLWithPath: model))
        }

        // Foundry Local is the recognizer to install unless whisper.cpp was asked for by name.
        guard forceWhisper else { throw TranscriptionError.backendMissing(.foundryCliNotFound) }
        throw TranscriptionError.backendMissing(cli == nil ? .whisperCliNotFound : .whisperModelNotFound)
    }

    private func locateFoundry() -> URL? {
        if let override = environment["SCRIBE_FOUNDRY_CLI"], FileManager.default.isExecutableFile(atPath: override) {
            return URL(fileURLWithPath: override)
        }
        return ProcessRunner.locateExecutable(named: "foundry", searchPath: searchPath)
    }

    private static func whisper(cli: URL, model: URL) -> TranscriptionBackendConfiguration {
        TranscriptionBackendConfiguration(kind: .whisperCpp, cliURL: cli, modelURL: model, foundryModelAlias: "")
    }
}

/// How long a recognizer may run: a base plus a share of the audio's length. The base is larger while the
/// recognizer is cold (the first run after launch, or after a failure), when Foundry Local may still have to
/// load, or even download, the model; the file's own measurements put that near a minute.
struct TranscriptionDeadlines: Sendable, Equatable {
    var coldBase: Duration = .seconds(300)
    var warmBase: Duration = .seconds(30)
    /// Seconds allowed per second of audio. Measured decodes run 6 to 28 times faster than real time.
    var perAudioSecond: Double = 1

    func deadline(audioSeconds: Double, warm: Bool) -> Duration {
        let audio = audioSeconds.isFinite ? max(0, audioSeconds) : 0
        return (warm ? warmBase : coldBase) + .seconds(audio * max(0, perAudioSecond))
    }
}

/// How one run of the recognizer went. Shapes only, for logs and the pipeline report.
struct TranscriptionDiagnostics: Sendable, Equatable {
    let duration: Duration
    let deadline: Duration
    /// The cold base applied, because this recognizer had not succeeded yet or failed last time.
    let usedColdBudget: Bool
    let standardOutputBytes: Int
    let standardErrorBytes: Int
    /// A process the recognizer started kept its output open after it exited, so the run waited out
    /// `ProcessRunner.postExitDrainLimit` (500 ms) before returning. Seen once, it is the run that started a
    /// background service; seen on every run, it is a delay on every dictation.
    let outputHeldAfterExit: Bool
}

struct TranscriptionResult: Sendable, Equatable {
    /// The transcript, trimmed. Never log it.
    let text: String
    let backend: TranscriptionBackendKind
    let diagnostics: TranscriptionDiagnostics
}

/// Turns a recording into text by running the recognizer as a child process through `ProcessRunner`.
///
/// Nothing here blocks the caller: `transcribe` suspends while the child runs. The child has a deadline scaled
/// to the audio (see `TranscriptionDeadlines`), cancelling the calling task stops the child's process group,
/// and both output streams are drained while it runs, so no child can wedge a dictation. The recording goes to
/// a private scratch file that is deleted when the run ends, however it ends (`ScratchAudioDirectory`).
///
/// Cancellation stops a recognizer that is still running and then throws `TranscriptionError.cancelled`; a run
/// stopped that way never yields a transcript, however complete its output looks. A cancellation that arrives
/// after the runner observed the recognizer's exit, for example while a process the recognizer left behind still
/// holds its output open (up to `ProcessRunner.postExitDrainLimit`), does not discard its transcript:
/// `transcribe` returns it, and the caller, which knows why it cancelled, decides whether to use it. The runner
/// observes the exit when it handles the kernel's exit notification, so one narrow window remains: a cancellation
/// it handles first, even one delivered ahead of that notification in the same batch of kernel events, still
/// counts as stopping the recognizer and discards the output.
///
/// The recognizer is looked up again for every transcription, reusing a successful lookup for a few seconds,
/// so installing Foundry Local while Scribe runs takes effect on the next dictation. Logs carry shapes only:
/// the backend kind, durations, byte and character counts, exit codes.
final class TranscriptionEngine: Sendable {
    static let defaultFoundryModelAlias = TranscriptionBackendResolver.defaultFoundryModelAlias

    /// How long a successful lookup is reused. A failed lookup is never reused.
    static let defaultResolutionLifetime: Duration = .seconds(10)

    private let resolver: TranscriptionBackendResolver
    private let scratch: ScratchAudioDirectory
    private let deadlines: TranscriptionDeadlines
    private let resolutionLifetime: Duration
    private let killGracePeriod: Duration
    private let now: @Sendable () -> ContinuousClock.Instant
    private let state = OSAllocatedUnfairLock(initialState: State())

    private struct State: Sendable {
        var resolved: TranscriptionBackendConfiguration?
        var resolvedAt: ContinuousClock.Instant?
        /// Recognizers whose last run succeeded, so the warm base applies to them.
        var warm: Set<TranscriptionBackendConfiguration> = []
    }

    init(
        resolver: TranscriptionBackendResolver = .live(),
        scratch: ScratchAudioDirectory = .live,
        deadlines: TranscriptionDeadlines = TranscriptionDeadlines(),
        resolutionLifetime: Duration = TranscriptionEngine.defaultResolutionLifetime,
        killGracePeriod: Duration = ProcessRunner.defaultKillGracePeriod,
        now: @escaping @Sendable () -> ContinuousClock.Instant = { ContinuousClock.now }
    ) {
        self.resolver = resolver
        self.scratch = scratch
        self.deadlines = deadlines
        self.resolutionLifetime = resolutionLifetime
        self.killGracePeriod = killGracePeriod
        self.now = now
    }

    /// The recognizer the next transcription would use. Throws `TranscriptionError.backendMissing`.
    func resolveBackend() throws -> TranscriptionBackendConfiguration {
        let instant = now()
        let lifetime = resolutionLifetime
        let cached = state.withLock { state -> TranscriptionBackendConfiguration? in
            guard let resolved = state.resolved, let resolvedAt = state.resolvedAt,
                resolvedAt.duration(to: instant) < lifetime
            else {
                return nil
            }
            return resolved
        }
        if let cached {
            return cached
        }

        do {
            let backend = try resolver.resolve()
            state.withLock { state in
                state.resolved = backend
                state.resolvedAt = instant
            }
            ScribeLog.debug(.transcription, "Speech recognizer found", .name("backend", backend.kind))
            return backend
        } catch {
            state.withLock { state in
                state.resolved = nil
                state.resolvedAt = nil
            }
            if case TranscriptionError.backendMissing(let issue) = error {
                ScribeLog.warning(.transcription, "No speech recognizer found", .name("issue", issue))
            }
            throw error
        }
    }

    /// Transcribes 16 kHz (or `sampleRate`) mono samples. Throws `TranscriptionError`. A cancellation that arrives
    /// after the runner observed the recognizer's exit does not discard its transcript; one the runner handles
    /// before the kernel's exit notification, even in the same batch of events, still does (see the type's notes).
    func transcribe(samples: [Float], sampleRate: Double) async throws -> TranscriptionResult {
        let backend = try resolveBackend()
        guard !Task.isCancelled else { throw TranscriptionError.cancelled }

        let file: ScratchAudioFile
        do {
            file = try scratch.writeRecording(samples: samples, sampleRate: sampleRate)
        } catch let error as ScratchAudioError {
            ScribeLog.error(
                .transcription, "Could not write the recording for the recognizer", .name("step", error.operation),
                .integer("errno", error.errno))
            throw TranscriptionError.audioWriteFailed(errno: error.errno)
        }
        defer { scratch.remove(file) }

        let audioSeconds = sampleRate > 0 ? Double(samples.count) / sampleRate : 0
        return try await run(backend, audioURL: file.url, audioSeconds: audioSeconds)
    }

    /// Transcribes a WAV file the caller owns, for the `--transcribe-wav` verb. Throws `TranscriptionError`.
    func transcribe(wavFileAt url: URL) async throws -> TranscriptionResult {
        guard url.pathExtension.lowercased() == "wav" else { throw TranscriptionError.unsupportedAudioFile }
        let backend = try resolveBackend()
        // Estimated at the sparsest common WAV density, 8 kHz 16-bit mono, so the deadline can only err long.
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        return try await run(backend, audioURL: url, audioSeconds: Double(max(0, size - 44)) / 16_000)
    }

    // MARK: - Running the recognizer

    private func run(
        _ backend: TranscriptionBackendConfiguration,
        audioURL: URL,
        audioSeconds: Double
    ) async throws -> TranscriptionResult {
        let warm = state.withLock { $0.warm.contains(backend) }
        let deadline = deadlines.deadline(audioSeconds: audioSeconds, warm: warm)
        ScribeLog.info(
            .transcription, "Transcribing", .name("backend", backend.kind),
            .decimal("audioSeconds", audioSeconds, precision: 2), .duration("deadline", deadline), .flag("warm", warm))

        let outcome: ProcessRunner.Outcome
        do {
            outcome = try await ProcessRunner.run(
                backend.cliURL,
                arguments: Self.arguments(for: backend, audioURL: audioURL),
                timeout: deadline,
                killGracePeriod: killGracePeriod)
        } catch is CancellationError {
            ScribeLog.info(.transcription, "Transcription cancelled before the recognizer started")
            throw TranscriptionError.cancelled
        } catch {
            forget(backend)
            ScribeLog.error(.transcription, "The recognizer could not be started", .failure(error))
            if case ProcessRunnerError.launchFailed(let code) = error {
                throw TranscriptionError.launchFailed(errno: code)
            }
            throw TranscriptionError.launchFailed(errno: 0)
        }

        let diagnostics = TranscriptionDiagnostics(
            duration: outcome.duration,
            deadline: deadline,
            usedColdBudget: !warm,
            standardOutputBytes: outcome.standardOutput.totalByteCount,
            standardErrorBytes: outcome.standardError.totalByteCount,
            outputHeldAfterExit: !outcome.standardOutput.reachedEndOfFile || !outcome.standardError.reachedEndOfFile)
        if diagnostics.outputHeldAfterExit {
            ScribeLog.warning(
                .transcription, "A process the recognizer started kept its output open until the drain limit",
                .name("backend", backend.kind), .duration("drainLimit", ProcessRunner.postExitDrainLimit))
        }

        do {
            let text = try Self.transcript(from: outcome, kind: backend.kind)
            markWarm(backend)
            ScribeLog.info(
                .transcription, "Transcribed", .name("backend", backend.kind), .duration("decode", outcome.duration),
                .count("characters", text.count), .count("stdoutBytes", diagnostics.standardOutputBytes),
                .count("stderrBytes", diagnostics.standardErrorBytes))
            return TranscriptionResult(text: text, backend: backend.kind, diagnostics: diagnostics)
        } catch TranscriptionError.cancelled {
            ScribeLog.info(
                .transcription, "Transcription cancelled; the recognizer was stopped", .name("backend", backend.kind),
                .duration("duration", outcome.duration))
            throw TranscriptionError.cancelled
        } catch {
            markCold(backend)
            var fields: [ScribeLog.Field] = [
                .name("reason", error as? TranscriptionError), .name("backend", backend.kind),
                .duration("duration", outcome.duration), .count("stdoutBytes", diagnostics.standardOutputBytes),
                .count("stderrBytes", diagnostics.standardErrorBytes),
            ]
            if let status = outcome.exitStatus {
                fields.append(.integer("exitStatus", status))
            }
            if let signal = outcome.terminationSignal {
                fields.append(.integer("signal", signal))
            }
            ScribeLog.log(.warning, .transcription, "Transcription failed", fields)
            throw error
        }
    }

    static func arguments(for backend: TranscriptionBackendConfiguration, audioURL: URL) -> [String] {
        let audioPath = audioURL.path(percentEncoded: false)
        switch backend.kind {
        case .foundryLocal:
            // No language flag: the model has no language parameter to set.
            return ["transcribe", "-m", backend.foundryModelAlias, "-f", audioPath, "-o", "json"]
        case .whisperCpp:
            let modelPath = backend.modelURL?.path(percentEncoded: false) ?? ""
            return ["-m", modelPath, "-f", audioPath, "-nt", "-np", "-l", "en"]
        }
    }

    /// Reads the transcript out of a finished run, or throws the `TranscriptionError` that explains why there
    /// is none. A run Scribe stopped (deadline or cancellation) never yields a partial transcript.
    static func transcript(from outcome: ProcessRunner.Outcome, kind: TranscriptionBackendKind) throws -> String {
        switch outcome.terminationReason {
        case .timedOut:
            throw TranscriptionError.timedOut
        case .cancelled:
            throw TranscriptionError.cancelled
        case .finished:
            break
        }

        switch kind {
        case .foundryLocal:
            // Foundry reports success and its structured errors as JSON on standard output, and some failures
            // still exit 0, so the reply is read before the exit status is trusted.
            if let reply = FoundryReply.find(in: outcome.standardOutput.data) {
                let text = reply.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                if !text.isEmpty {
                    return text
                }
                if reply.reportsError {
                    throw TranscriptionError.backendReportedError
                }
                if reply.text != nil {
                    throw TranscriptionError.emptyOutput
                }
            }
            try throwForAbnormalExit(outcome)
            let output = outcome.standardOutput.text.trimmingCharacters(in: .whitespacesAndNewlines)
            throw output.isEmpty ? TranscriptionError.emptyOutput : TranscriptionError.malformedOutput
        case .whisperCpp:
            try throwForAbnormalExit(outcome)
            let text = outcome.standardOutput.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { throw TranscriptionError.emptyOutput }
            return text
        }
    }

    private static func throwForAbnormalExit(_ outcome: ProcessRunner.Outcome) throws {
        if let signal = outcome.terminationSignal {
            throw TranscriptionError.terminatedBySignal(signal)
        }
        if let status = outcome.exitStatus, status != 0 {
            throw TranscriptionError.exitCode(status)
        }
    }

    private func markWarm(_ backend: TranscriptionBackendConfiguration) {
        _ = state.withLock { $0.warm.insert(backend) }
    }

    private func markCold(_ backend: TranscriptionBackendConfiguration) {
        _ = state.withLock { $0.warm.remove(backend) }
    }

    /// A recognizer that could not be started is looked up again next time, and starts cold.
    private func forget(_ backend: TranscriptionBackendConfiguration) {
        state.withLock { state in
            if state.resolved == backend {
                state.resolved = nil
                state.resolvedAt = nil
            }
            state.warm.remove(backend)
        }
    }
}

/// The part of `foundry transcribe -o json` Scribe reads. Only the transcript is decoded; an error reply is
/// recognized by its key, and its message, which can hold a path, is never read.
private struct FoundryReply: Decodable {
    let text: String?
    let reportsError: Bool

    private enum CodingKeys: String, CodingKey {
        case text
        case error
    }

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        text = try? container.decodeIfPresent(String.self, forKey: .text)
        reportsError = container.contains(.error) && !((try? container.decodeNil(forKey: .error)) ?? true)
    }

    /// The reply in `data`: the whole output as one JSON object, or else its last line that is one, in case
    /// progress text precedes it.
    static func find(in data: Data) -> FoundryReply? {
        let decoder = JSONDecoder()
        if let reply = try? decoder.decode(FoundryReply.self, from: data) {
            return reply
        }
        let lines = String(decoding: data, as: UTF8.self).split(whereSeparator: \.isNewline)
        for line in lines.reversed() {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard trimmed.hasPrefix("{"), let reply = try? decoder.decode(FoundryReply.self, from: Data(trimmed.utf8))
            else {
                continue
            }
            return reply
        }
        return nil
    }
}
