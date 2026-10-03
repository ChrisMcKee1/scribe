import Foundation

struct FoundryLocalResidencySource: Sendable {
    static let sharedLane = AsyncLane()
    let isLoaded: @Sendable (String) async throws -> Bool
    let loadCached: @Sendable (String) async throws -> Void

    static func live(environment: [String: String] = ProcessInfo.processInfo.environment) -> Self {
        Self(
            isLoaded: { model in
                let data = try await run(["model", "list", "--loaded", "-o", "json"], environment: environment)
                return try containsLoadedModel(data, model: model)
            },
            loadCached: { model in
                guard !model.isEmpty, !model.hasPrefix("-") else { throw LocalModelReadinessError.unavailable }
                let metadata = try await run(["model", "info", model, "-o", "json"], environment: environment)
                let id = try cachedVariant(metadata, model: model)
                let result = try await run(["model", "load", id, "-o", "json"], environment: environment)
                try confirmLoadReply(result)
            })
    }

    private static func run(_ arguments: [String], environment: [String: String]) async throws -> Data {
        try Task.checkCancellation()
        try CleanupSendHandoff.current?.perform {}
        guard let cli = FoundryLocalCLI.locate(environment: environment) else {
            throw LocalModelReadinessError.unavailable
        }
        let outcome: ProcessRunner.Outcome
        do {
            outcome = try await ProcessRunner.run(cli, arguments: arguments, timeout: FoundryLocalCLI.statusTimeout)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw LocalModelReadinessError.unavailable
        }
        if outcome.terminationReason == .cancelled { throw CancellationError() }
        guard outcome.terminationReason == .finished, outcome.exitStatus == 0,
            outcome.terminationSignal == nil, !outcome.standardOutput.isTruncated
        else {
            throw LocalModelReadinessError.unavailable
        }
        return outcome.standardOutput.data
    }

    private struct Model: Decodable {
        let alias: String?
        let id: String?
        let type: String?
        let cached: Bool?

        func matches(_ name: String) -> Bool { type?.lowercased() == "chat" && (alias == name || id == name) }
    }

    static func containsLoadedModel(_ data: Data, model: String) throws -> Bool {
        struct List: Decodable { let models: [Model] }
        guard let list = try? JSONDecoder().decode(List.self, from: data) else {
            throw LocalModelReadinessError.unavailable
        }
        return list.models.contains { $0.matches(model) }
    }

    static func cachedVariant(_ data: Data, model: String) throws -> String {
        struct Info: Decodable { let model: Model }
        guard let info = try? JSONDecoder().decode(Info.self, from: data),
            info.model.matches(model), info.model.cached == true,
            let id = info.model.id, !id.isEmpty, !id.hasPrefix("-")
        else { throw LocalModelReadinessError.unavailable }
        return id
    }

    static func confirmLoadReply(_ data: Data) throws {
        struct Reply: Decodable { let success: Bool }
        guard let reply = try? JSONDecoder().decode(Reply.self, from: data), reply.success else {
            throw LocalModelReadinessError.unavailable
        }
    }
}
