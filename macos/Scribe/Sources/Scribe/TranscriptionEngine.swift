import Foundation

enum TranscriptionEngineError: LocalizedError {
    case missingWhisperCli
    case missingWhisperModel
    case audioConversionFailed(String)
    case wavWriteFailed
    case processFailed(String)
    case emptyTranscript

    var errorDescription: String? {
        switch self {
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

struct TranscriptionBackendConfiguration {
    let cliURL: URL
    let modelURL: URL
}

final class TranscriptionEngine {
    private static let sourceRootURL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()

    private let fileManager: FileManager
    private let backend: TranscriptionBackendConfiguration
    private let logSink: (String) -> Void

    init(
        fileManager: FileManager = .default,
        logSink: @escaping (String) -> Void = { _ in },
        backend: TranscriptionBackendConfiguration? = nil
    ) throws {
        self.fileManager = fileManager
        self.logSink = logSink
        self.backend = try backend ?? Self.resolveBackendConfiguration(fileManager: fileManager)
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
                "The current stopgap backend accepts WAV input only. Convert other formats to 16 kHz mono WAV first.")
        }

        return try transcribe(wavFileAt: inputURL)
    }

    func transcribe(wavFileAt wavURL: URL) throws -> String {
        let process = Process()
        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        process.executableURL = backend.cliURL
        process.arguments = [
            "-m", backend.modelURL.path(percentEncoded: false),
            "-f", wavURL.path(percentEncoded: false),
            "-nt",
            "-np",
            "-l", "en"
        ]
        process.standardOutput = stdoutPipe
        process.standardError = stderrPipe

        logSink("Running ASR backend: whisper-cli with model \(backend.modelURL.lastPathComponent)")

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

    private static func resolveBackendConfiguration(fileManager: FileManager) throws -> TranscriptionBackendConfiguration {
        let environment = ProcessInfo.processInfo.environment

        if
            let cliPath = environment["SCRIBE_WHISPER_CLI"],
            let modelPath = environment["SCRIBE_WHISPER_MODEL"],
            fileManager.fileExists(atPath: cliPath),
            fileManager.fileExists(atPath: modelPath)
        {
            return TranscriptionBackendConfiguration(
                cliURL: URL(fileURLWithPath: cliPath),
                modelURL: URL(fileURLWithPath: modelPath))
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
            cliURL: URL(fileURLWithPath: cliPath),
            modelURL: URL(fileURLWithPath: modelPath))
    }

    private func littleEndianBytes<T: FixedWidthInteger>(_ value: T) -> Data {
        var littleEndian = value.littleEndian
        return withUnsafeBytes(of: &littleEndian) { Data($0) }
    }
}
