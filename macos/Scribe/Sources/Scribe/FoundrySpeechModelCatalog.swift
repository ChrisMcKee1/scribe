import Foundation

struct FoundrySpeechModelChoice: Sendable, Hashable, Identifiable {
    let alias: String
    let title: String
    /// `nil` means this Foundry version did not report whether the model is cached.
    let isCached: Bool?

    var id: String { alias }
}

enum FoundrySpeechModelCatalog {
    static let defaultModel = FoundrySpeechModelChoice(
        alias: TranscriptionBackendResolver.defaultFoundryModelAlias,
        title: "Parakeet TDT 0.6B v2",
        isCached: nil)

    static func choices(
        from models: [FoundrySpeechModelChoice],
        preserving selectedAlias: String
    ) -> [FoundrySpeechModelChoice] {
        var choices = models
        if !choices.contains(where: { $0.alias == defaultModel.alias }) {
            choices.append(defaultModel)
        }
        if !choices.contains(where: { $0.alias == selectedAlias }) {
            choices.append(
                FoundrySpeechModelChoice(
                    alias: selectedAlias,
                    title: "Saved selection, not listed: \(selectedAlias)",
                    isCached: nil))
        }
        return choices
    }

    static func list(cliURL: URL) async throws -> [FoundrySpeechModelChoice] {
        let outcome = try await ProcessRunner.run(
            cliURL, arguments: ["model", "list", "--type", "speech", "-o", "json"], timeout: .seconds(20))
        guard outcome.terminationReason == .finished else {
            if outcome.terminationReason == .cancelled { throw CancellationError() }
            throw FoundrySpeechModelError.catalogUnavailable
        }
        guard outcome.exitStatus == 0,
            let response = try? JSONDecoder().decode(ModelListResponse.self, from: outcome.standardOutput.data)
        else {
            throw FoundrySpeechModelError.catalogUnavailable
        }
        return response.models
            .filter { $0.type.map { $0.caseInsensitiveCompare("speech") == .orderedSame } ?? true }
            .compactMap { model in
                guard !model.alias.isEmpty else { return nil }
                return FoundrySpeechModelChoice(
                    alias: model.alias,
                    title: model.displayName.flatMap { $0.isEmpty ? nil : $0 } ?? model.alias,
                    isCached: model.cached)
            }
    }

    static func download(alias: String, cliURL: URL) async throws {
        guard !alias.isEmpty, !alias.utf8.contains(0) else {
            throw FoundrySpeechModelError.invalidAlias
        }
        let outcome = try await ProcessRunner.run(
            cliURL, arguments: ["model", "download", alias], timeout: .seconds(3_600), outputLimit: 16_384)
        guard outcome.terminationReason == .finished else {
            if outcome.terminationReason == .cancelled { throw CancellationError() }
            throw FoundrySpeechModelError.downloadFailed
        }
        guard outcome.exitStatus == 0 else { throw FoundrySpeechModelError.downloadFailed }
    }

    private struct ModelListResponse: Decodable {
        let models: [ListedModel]
    }

    private struct ListedModel: Decodable {
        let alias: String
        let displayName: String?
        let type: String?
        let cached: Bool?
    }
}

enum FoundrySpeechModelError: Error, Equatable {
    case catalogUnavailable
    case downloadFailed
    case invalidAlias
}
