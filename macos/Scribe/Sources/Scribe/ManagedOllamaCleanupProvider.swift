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
    private let contextTokens: Int
    private let localModelLane: AsyncLane
    private let lifecycle: LocalModelLifecycle
    private let lifecycleEndpoint: String
    private let timeout: TimeInterval
    private let transport: ChatCompletionsTransport
    private let readLocalServer: @Sendable (String) async -> LocalServerState

    init(
        model: String = CleanupSettingsStore.defaultOllamaModel,
        baseURL: URL = ManagedOllamaCleanupProvider.defaultBaseURL,
        keepAliveMinutes: Int = LocalModelDefaults.keepAliveMinutes,
        contextTokens: Int = 0,
        localModelLane: AsyncLane = LocalModelDefaults.sharedLane,
        lifecycle: LocalModelLifecycle? = nil,
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
        self.contextTokens = ContextBudget.sanitize(contextTokens)
        self.localModelLane = localModelLane
        self.lifecycle = lifecycle ?? LocalModelLifecycle(idle: .zero, actions: .connected(to: session))
        self.lifecycleEndpoint =
            OpenAICompatibleEndpoint.serviceURL(for: self.completionsURL)?.absoluteString ?? baseURL.absoluteString
        self.timeout = timeout
        self.transport = ChatCompletionsTransport(session: session)
        self.readLocalServer = readLocalServer
    }

    private func beginLease() async throws -> LocalModelLifecycle.Lease {
        do {
            return try await lifecycle.beginUse(
                LocalModelTarget(
                    endpoint: lifecycleEndpoint, model: model, app: .ollama, apiKey: nil))
        } catch is LocalModelLifecycleError {
            throw CleanupProviderError.timedOut
        }
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        let lease = try await beginLease()
        defer { lease.end() }
        let completion = try await localModelLane.run {
            try await complete(request)
        }
        return CleanupResponse(
            cleanedText: completion.text, latency: completion.latency, providerID: id, modelID: model)
    }

    private func complete(_ request: CleanupRequest) async throws -> ChatCompletionsTransport.Completion {
        if contextTokens > 0 {
            guard let url = URL(string: lifecycleEndpoint) else {
                throw CleanupProviderError.transport(URLError(.badURL))
            }
            return try await transport.completeOllama(
                request, at: url, model: model,
                keepAlive: keepAliveMinutes > 0 ? "\(keepAliveMinutes)m" : nil,
                contextTokens: contextTokens, defaultTimeout: timeout)
        }
        return try await transport.complete(
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

    func prepareLocalModel(
        isCurrent: @escaping @MainActor @Sendable () async -> Bool,
        onStarting: @escaping @MainActor @Sendable () async -> Void
    ) async throws -> LocalModelPreparationResult {
        let lease = try await beginLease()
        defer { lease.end() }
        return try await localModelLane.run {
            try await LocalModelReadiness.prepare(
                isResident: {
                    let state = await self.readLocalServer(self.lifecycleEndpoint)
                    guard state.reach == .reached else { throw LocalModelReadinessError.unavailable }
                    guard let loaded = state.loaded(for: self.model) else { return false }
                    return self.contextTokens == 0 || loaded.contextTokens == self.contextTokens
                },
                isCurrent: isCurrent,
                onStarting: onStarting,
                start: {
                    _ = try await self.complete(LocalModelReadiness.request)
                })
        }
    }
}
