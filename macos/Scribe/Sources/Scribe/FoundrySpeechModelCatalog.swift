import Foundation

struct FoundrySpeechModelChoice: Sendable, Hashable, Identifiable {
    let alias: String
    let title: String
    var id: String { alias }
}

enum FoundrySpeechModelCatalog {
    static let choices = [
        FoundrySpeechModelChoice(alias: "parakeet-tdt-0.6b-v2", title: "Parakeet TDT 0.6B v2"),
        FoundrySpeechModelChoice(alias: "whisper-tiny", title: "Whisper tiny"),
        FoundrySpeechModelChoice(alias: "whisper-base", title: "Whisper base"),
        FoundrySpeechModelChoice(alias: "whisper-small", title: "Whisper small"),
        FoundrySpeechModelChoice(alias: "whisper-medium", title: "Whisper medium"),
        FoundrySpeechModelChoice(alias: "whisper-large-v3-turbo", title: "Whisper large v3 turbo"),
        FoundrySpeechModelChoice(alias: "nemotron-3.5-asr-streaming-0.6b", title: "Nemotron 3.5 ASR streaming"),
        FoundrySpeechModelChoice(
            alias: "nemotron-speech-streaming-en-0.6b", title: "Nemotron speech streaming English"),
        FoundrySpeechModelChoice(
            alias: "nemotron-speech-streaming-es-0.6b", title: "Nemotron speech streaming Spanish"),
    ]

    static func cachedAliases(cliURL: URL) async throws -> Set<String> {
        let outcome = try await ProcessRunner.run(
            cliURL, arguments: ["cache", "list", "-o", "json"], timeout: .seconds(20))
        guard outcome.terminationReason == .finished else {
            if outcome.terminationReason == .cancelled { throw CancellationError() }
            throw FoundrySpeechModelError.cacheUnavailable
        }
        guard outcome.exitStatus == 0,
            let response = try? JSONDecoder().decode(CacheResponse.self, from: outcome.standardOutput.data)
        else {
            throw FoundrySpeechModelError.cacheUnavailable
        }
        return Set(response.models.filter(\.cached).map(\.alias))
    }

    static func download(alias: String, cliURL: URL) async throws {
        guard choices.contains(where: { $0.alias == alias }) else {
            throw FoundrySpeechModelError.unsupportedModel
        }
        let outcome = try await ProcessRunner.run(
            cliURL, arguments: ["model", "download", alias], timeout: .seconds(3_600), outputLimit: 16_384)
        guard outcome.terminationReason == .finished else {
            if outcome.terminationReason == .cancelled { throw CancellationError() }
            throw FoundrySpeechModelError.downloadFailed
        }
        guard outcome.exitStatus == 0 else { throw FoundrySpeechModelError.downloadFailed }
    }

    private struct CacheResponse: Decodable {
        let models: [CachedModel]
    }

    private struct CachedModel: Decodable {
        let alias: String
        let cached: Bool
    }
}

enum FoundrySpeechModelError: Error, Equatable {
    case cacheUnavailable
    case downloadFailed
    case unsupportedModel
}
