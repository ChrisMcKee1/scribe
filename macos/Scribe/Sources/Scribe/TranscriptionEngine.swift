import Foundation

enum TranscriptionEngineError: LocalizedError {
    case missingFoundryCli
    case missingWhisperCli
    case missingWhisperModel
    case audioConversionFailed(String)
    case wavWriteFailed
    case processFailed(String)
    case emptyTranscript

    var errorDescription: String? {
        switch self {
        case .missingFoundryCli:
            return "Could not find the foundry CLI. Install it with `brew install microsoft/foundrylocal/foundrylocal`, or set SCRIBE_FOUNDRY_CLI."
        case .missingWhisperCli:
            return "Could not find whisper-cli. Install whisper-cpp or set SCRIBE_WHISPER_CLI."
        case .missingWhisperModel:
            return "Could not find the Whisper model. Download ggml-tiny.en.bin or set SCRIBE_WHISPER_MODEL."
        case .audioConversionFailed(let reason):
            return "Audio conversion failed: \(reason)"
        case .wavWriteFailed:
            return "Could not write the temporary WAV file for transcription."
        case .processFailed(let reason):
            return "The ASR process failed: \(reason)"
        case .emptyTranscript:
            return "The ASR backend returned an empty transcript."
        }
    }
}

/// Which real ASR backend a `TranscriptionEngine` instance is bound to. Foundry Local is the
/// production-track backend (real Parakeet TDT family, same as Windows, see PORTING-PLAN.md's ASR
/// strategy decision). Whisper.cpp is kept only as a documented fallback in case Foundry Local is
/// not installed or its latency proves unacceptable once profiled with longer dictations.
enum TranscriptionBackendKind: String {
    case foundryLocal
    case whisperCpp
}

struct TranscriptionBackendConfiguration {
    let kind: TranscriptionBackendKind
    /// For `.foundryLocal`, the `foundry` executable. For `.whisperCpp`, the `whisper-cli` executable.
    let cliURL: URL
    /// Only used for `.whisperCpp`; Foundry Local resolves and manages its own model cache.
    let modelURL: URL?
    /// Foundry Local model alias to pass to `foundry transcribe -m <alias>`.
    let foundryModelAlias: String
}

private struct FoundryTranscribeResult: Decodable {
    let model: String?
    let file: String?
    let text: String?
    let language: String?
    let durationSeconds: Double?
}

private struct FoundryTranscribeError: Decodable {
    struct ErrorBody: Decodable {
        let code: String?
        let message: String?
    }
    let error: ErrorBody
}

final class TranscriptionEngine {
    static let defaultFoundryModelAlias = "parakeet-tdt-0.6b-v2"

    private static let sourceRootURL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()

    private let fileManager: FileManager
    let backend: TranscriptionBackendConfiguration
    private let logSink: (String) -> Void

    init(
        fileManager: FileManager = .default,
        logSink: @escaping (String) -> Void = { _ in },
        backend: TranscriptionBackendConfiguration? = nil
    ) throws {
        self.fileManager = fileManager
        self.logSink = logSink
        self.backend = try backend ?? Self.resolveBackendConfiguration(fileManager: fileManager)
        logSink("ASR backend resolved: \(self.backend.kind.rawValue)")
    }

    func transcribe(samples: [Float], sampleRate: Double) throws -> String {
        let workingDirectory = try prepareWorkingDirectory()
        let wavURL = workingDirectory.appendingPathComponent("captured-\(UUID().uuidString).wav")
        try writeMonoFloat32Wav(samples: samples, sampleRate: sampleRate, to: wavURL)
        defer { try? fileManager.removeItem(at: wavURL) }
        return try transcribe(wavFileAt: wavURL)
    }

    func transcribeAudioFile(at inputURL: URL) throws -> String {
        guard inputURL.pathExtension.lowercased() == "wav" else {
            throw TranscriptionEngineError.audioConversionFailed(
                "The current backends accept WAV input only. Convert other formats to 16 kHz mono WAV first.")
        }

        return try transcribe(wavFileAt: inputURL)
    }

    func transcribe(wavFileAt wavURL: URL) throws -> String {
        switch backend.kind {
        case .foundryLocal:
            return try transcribeWithFoundryLocal(wavURL: wavURL)
        case .whisperCpp:
            return try transcribeWithWhisperCpp(wavURL: wavURL)
        }
    }

    // MARK: - Foundry Local backend (production path)

    /// Foundry Local's Parakeet TDT model, invoked via `foundry transcribe -m <alias> -f <wav> -o json`.
    /// No `--language` flag is passed: Parakeet TDT is a transducer with the vocabulary baked in and
    /// auto-handles whatever language is spoken, matching the Windows product's no-language-picker rule
    /// (see AGENTS.md). Steady-state latency measured on this machine (Apple M5): ~1.3s for a 4-word
    /// clip after the model is already loaded; first call after `foundry server start` pays a one-time
    /// model load cost (~60s including a fresh download).
    private func transcribeWithFoundryLocal(wavURL: URL) throws -> String {
        let process = Process()
        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        process.executableURL = backend.cliURL
        process.arguments = [
            "transcribe",
            "-m", backend.foundryModelAlias,
            "-f", wavURL.path(percentEncoded: false),
            "-o", "json"
        ]
        process.standardOutput = stdoutPipe
        process.standardError = stderrPipe

        logSink("Running ASR backend: foundry transcribe -m \(backend.foundryModelAlias)")

        do {
            try process.run()
        } catch {
            throw TranscriptionEngineError.processFailed(error.localizedDescription)
        }

        process.waitUntilExit()

        let stdoutData = stdoutPipe.fileHandleForReading.readDataToEndOfFile()
        let stderrData = stderrPipe.fileHandleForReading.readDataToEndOfFile()
        let stderrText = String(decoding: stderrData, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)

        // Foundry CLI reports both success and structured errors as JSON on stdout; check content
        // before trusting the exit code alone, since some failure modes still exit 0 with an
        // "error" payload.
        if let result = try? JSONDecoder().decode(FoundryTranscribeResult.self, from: stdoutData),
           let text = result.text?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty {
            return text
        }

        if let errorPayload = try? JSONDecoder().decode(FoundryTranscribeError.self, from: stdoutData) {
            let detail = errorPayload.error.message ?? errorPayload.error.code ?? "unknown foundry error"
            throw TranscriptionEngineError.processFailed(detail)
        }

        guard process.terminationStatus == 0 else {
            let stdoutText = String(decoding: stdoutData, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            let detail = stderrText.isEmpty ? stdoutText : stderrText
            throw TranscriptionEngineError.processFailed(detail.isEmpty ? "foundry transcribe exited with status \(process.terminationStatus)" : detail)
        }

        throw TranscriptionEngineError.emptyTranscript
    }

    // MARK: - whisper.cpp backend (documented fallback only)

    private func transcribeWithWhisperCpp(wavURL: URL) throws -> String {
        guard let modelURL = backend.modelURL else {
            throw TranscriptionEngineError.missingWhisperModel
        }

        let process = Process()
        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        process.executableURL = backend.cliURL
        process.arguments = [
            "-m", modelURL.path(percentEncoded: false),
            "-f", wavURL.path(percentEncoded: false),
            "-nt",
            "-np",
            "-l", "en"
        ]
        process.standardOutput = stdoutPipe
        process.standardError = stderrPipe

        logSink("Running ASR backend: whisper-cli with model \(modelURL.lastPathComponent)")

        do {
            try process.run()
        } catch {
            throw TranscriptionEngineError.processFailed(error.localizedDescription)
        }

        process.waitUntilExit()

        let stdoutData = stdoutPipe.fileHandleForReading.readDataToEndOfFile()
        let stderrData = stderrPipe.fileHandleForReading.readDataToEndOfFile()
        let stdoutText = String(decoding: stdoutData, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        let stderrText = String(decoding: stderrData, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)

        guard process.terminationStatus == 0 else {
            let detail = stderrText.isEmpty ? stdoutText : stderrText
            throw TranscriptionEngineError.processFailed(detail)
        }

        guard !stdoutText.isEmpty else {
            if !stderrText.isEmpty {
                throw TranscriptionEngineError.processFailed(stderrText)
            }

            throw TranscriptionEngineError.emptyTranscript
        }

        return stdoutText
    }

    private func prepareWorkingDirectory() throws -> URL {
        let applicationSupportURL = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let directoryURL = applicationSupportURL
            .appendingPathComponent("Scribe", isDirectory: true)
            .appendingPathComponent("asr-work", isDirectory: true)
        try fileManager.createDirectory(at: directoryURL, withIntermediateDirectories: true)
        return directoryURL
    }

    private func writeMonoFloat32Wav(samples: [Float], sampleRate: Double, to url: URL) throws {
        let channelCount: UInt16 = 1
        let bitsPerSample: UInt16 = 32
        let bytesPerSample = Int(bitsPerSample / 8)
        let audioFormat: UInt16 = 3
        let dataSize = samples.count * bytesPerSample
        let byteRate = UInt32(sampleRate) * UInt32(channelCount) * UInt32(bytesPerSample)
        let blockAlign = channelCount * UInt16(bytesPerSample)

        var data = Data()
        data.append("RIFF".data(using: .ascii)!)
        data.append(littleEndianBytes(UInt32(36 + dataSize)))
        data.append("WAVE".data(using: .ascii)!)
        data.append("fmt ".data(using: .ascii)!)
        data.append(littleEndianBytes(UInt32(16)))
        data.append(littleEndianBytes(audioFormat))
        data.append(littleEndianBytes(channelCount))
        data.append(littleEndianBytes(UInt32(sampleRate)))
        data.append(littleEndianBytes(byteRate))
        data.append(littleEndianBytes(blockAlign))
        data.append(littleEndianBytes(bitsPerSample))
        data.append("data".data(using: .ascii)!)
        data.append(littleEndianBytes(UInt32(dataSize)))

        let littleEndianSamples = samples.map { $0.bitPattern.littleEndian }
        littleEndianSamples.withUnsafeBufferPointer { buffer in
            guard let baseAddress = buffer.baseAddress else { return }
            data.append(contentsOf: UnsafeRawBufferPointer(start: baseAddress, count: buffer.count * bytesPerSample))
        }

        do {
            try data.write(to: url, options: .atomic)
        } catch {
            throw TranscriptionEngineError.wavWriteFailed
        }
    }

    /// Backend resolution order: explicit env override > Foundry Local (production default) >
    /// whisper.cpp (documented fallback). `SCRIBE_ASR_BACKEND=whisper` forces the fallback even when
    /// Foundry Local is installed, useful for A/B comparison or if Foundry Local's daemon is down.
    private static func resolveBackendConfiguration(fileManager: FileManager) throws -> TranscriptionBackendConfiguration {
        let environment = ProcessInfo.processInfo.environment

        if
            let cliPath = environment["SCRIBE_WHISPER_CLI"],
            let modelPath = environment["SCRIBE_WHISPER_MODEL"],
            fileManager.fileExists(atPath: cliPath),
            fileManager.fileExists(atPath: modelPath)
        {
            return TranscriptionBackendConfiguration(
                kind: .whisperCpp,
                cliURL: URL(fileURLWithPath: cliPath),
                modelURL: URL(fileURLWithPath: modelPath),
                foundryModelAlias: "")
        }

        let forceWhisper = environment["SCRIBE_ASR_BACKEND"]?.lowercased() == "whisper"

        if !forceWhisper {
            let foundryModelAlias = environment["SCRIBE_FOUNDRY_ASR_MODEL"] ?? defaultFoundryModelAlias
            if let foundryConfig = try? resolveFoundryConfiguration(
                fileManager: fileManager, environment: environment, modelAlias: foundryModelAlias) {
                return foundryConfig
            }
        }

        return try resolveWhisperConfiguration(fileManager: fileManager, environment: environment)
    }

    private static func resolveFoundryConfiguration(
        fileManager: FileManager,
        environment: [String: String],
        modelAlias: String
    ) throws -> TranscriptionBackendConfiguration {
        if let cliPath = environment["SCRIBE_FOUNDRY_CLI"], fileManager.isExecutableFile(atPath: cliPath) {
            return TranscriptionBackendConfiguration(
                kind: .foundryLocal, cliURL: URL(fileURLWithPath: cliPath), modelURL: nil, foundryModelAlias: modelAlias)
        }

        let candidateCliPaths = [
            "/opt/homebrew/bin/foundry",
            "/usr/local/bin/foundry"
        ]

        guard let cliPath = candidateCliPaths.first(where: { fileManager.isExecutableFile(atPath: $0) }) else {
            throw TranscriptionEngineError.missingFoundryCli
        }

        return TranscriptionBackendConfiguration(
            kind: .foundryLocal, cliURL: URL(fileURLWithPath: cliPath), modelURL: nil, foundryModelAlias: modelAlias)
    }

    private static func resolveWhisperConfiguration(
        fileManager: FileManager,
        environment: [String: String]
    ) throws -> TranscriptionBackendConfiguration {
        if
            let cliPath = environment["SCRIBE_WHISPER_CLI"],
            let modelPath = environment["SCRIBE_WHISPER_MODEL"],
            fileManager.fileExists(atPath: cliPath),
            fileManager.fileExists(atPath: modelPath)
        {
            return TranscriptionBackendConfiguration(
                kind: .whisperCpp,
                cliURL: URL(fileURLWithPath: cliPath),
                modelURL: URL(fileURLWithPath: modelPath),
                foundryModelAlias: "")
        }

        let candidateCliPaths = [
            "/opt/homebrew/opt/whisper-cpp/bin/whisper-cli",
            "/opt/homebrew/bin/whisper-cli",
            "/usr/local/bin/whisper-cli"
        ]
        let candidateModelPaths = [
            sourceRootURL.appendingPathComponent("Models/whisper/ggml-tiny.en.bin").path(percentEncoded: false),
            URL(fileURLWithPath: fileManager.currentDirectoryPath)
                .appendingPathComponent("macos/Scribe/Models/whisper/ggml-tiny.en.bin")
                .path(percentEncoded: false),
            URL(fileURLWithPath: fileManager.currentDirectoryPath)
                .appendingPathComponent("Models/whisper/ggml-tiny.en.bin")
                .path(percentEncoded: false)
        ]

        guard let cliPath = candidateCliPaths.first(where: { fileManager.isExecutableFile(atPath: $0) }) else {
            throw TranscriptionEngineError.missingWhisperCli
        }

        guard let modelPath = candidateModelPaths.first(where: { fileManager.fileExists(atPath: $0) }) else {
            throw TranscriptionEngineError.missingWhisperModel
        }

        return TranscriptionBackendConfiguration(
            kind: .whisperCpp,
            cliURL: URL(fileURLWithPath: cliPath),
            modelURL: URL(fileURLWithPath: modelPath),
            foundryModelAlias: "")
    }

    private func littleEndianBytes<T: FixedWidthInteger>(_ value: T) -> Data {
        var littleEndian = value.littleEndian
        return withUnsafeBytes(of: &littleEndian) { Data($0) }
    }
}
