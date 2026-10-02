import Foundation

/// Fully supported alternative local cleanup provider, not deprecated by Foundry Local. Talks to Ollama's fixed local
/// port (11434, unlike Foundry Local's dynamic port) over its OpenAI-compatible endpoint. Appropriate for users who
/// already run Ollama for other tools or prefer its model catalog. See PORTING-PLAN.md's "AI cleanup provider
/// architecture" for the two-managed-provider design (Foundry Local pre-selected by default, Ollama a first-class
/// alternative).
///
/// The Ollama daemon is the user's and other tools share it, so Scribe only talks to it: it does not start, stop or
/// update it.
final class ManagedOllamaCleanupProvider: CleanupProvider {
    static let defaultBaseURL = URL(string: "http://127.0.0.1:11434")!

    let id = "managed-ollama"
    let displayName = "Ollama"
    let usesLocalCleanupPrompt = true
    /// `qwen2.5:3b` is the best-quality result benchmarked on Ollama (0.632 avg score) and is very close to Foundry
    /// Local's default `qwen2.5-1.5b`, making this a legitimate alternate choice rather than a downgrade. See
    /// CLEANUP-MODEL-BENCHMARK.md.
    let model: String
    let completionsURL: URL
    private let keepAliveMinutes: Int
    private let localModelLane: AsyncLane
    private let timeout: TimeInterval
    private let transport: ChatCompletionsTransport
    private let readLocalServer: @Sendable (String) async -> LocalServerState

    init(
        model: String = CleanupSettingsStore.defaultOllamaModel,
        baseURL: URL = ManagedOllamaCleanupProvider.defaultBaseURL,
        keepAliveMinutes: Int = LocalModelDefaults.keepAliveMinutes,
        localModelLane: AsyncLane = LocalModelDefaults.sharedLane,
        timeout: TimeInterval = 30,
        readLocalServer: @escaping @Sendable (String) async -> LocalServerState = { endpoint in
            await LocalServerClient().read(endpoint)
        },
        session: URLSession = CleanupProviderFactory.cleanupSession
    ) {
        self.model = model
        self.completionsURL =
            OpenAICompatibleEndpoint.chatCompletionsURL(for: baseURL)
            ?? baseURL.appendingPathComponent("v1/chat/completions")
        self.keepAliveMinutes = keepAliveMinutes
        self.localModelLane = localModelLane
        self.timeout = timeout
        self.transport = ChatCompletionsTransport(session: session)
        self.readLocalServer = readLocalServer
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        let completion = try await localModelLane.run {
            try await transport.complete(
                request,
                at: completionsURL,
                model: model,
                bearerToken: nil,
                temperature: CleanupSampling.onDeviceTemperature,
                reasoningEffort: CleanupReasoningEffort.none,
                includeLegacyMaxTokens: true,
                keepAlive: keepAliveMinutes > 0 ? "\(keepAliveMinutes)m" : nil,
                defaultTimeout: timeout,
                provider: .ollama)
        }
        return CleanupResponse(
            cleanedText: completion.text, latency: completion.latency, providerID: id, modelID: model)
    }

    func prepareLocalModel(
        isCurrent: @escaping @MainActor @Sendable () async -> Bool,
        onStarting: @escaping @MainActor @Sendable () async -> Void
    ) async throws -> LocalModelPreparationResult {
        try await localModelLane.run {
            try await LocalModelReadiness.prepare(
                isResident: {
                    let state = await self.readLocalServer(Self.defaultBaseURL.absoluteString)
                    guard state.reach == .reached else { throw LocalModelReadinessError.unavailable }
                    return state.loaded(for: self.model) != nil
                },
                isCurrent: isCurrent,
                onStarting: onStarting,
                start: {
                    _ = try await self.transport.complete(
                        LocalModelReadiness.request,
                        at: self.completionsURL,
                        model: self.model,
                        bearerToken: nil,
                        temperature: CleanupSampling.onDeviceTemperature,
                        reasoningEffort: CleanupReasoningEffort.none,
                        includeLegacyMaxTokens: true,
                        keepAlive: self.keepAliveMinutes > 0 ? "\(self.keepAliveMinutes)m" : nil,
                        defaultTimeout: self.timeout,
                        provider: .ollama)
                })
        }
    }
}
