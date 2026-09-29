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
    /// `qwen2.5:3b` is the best-quality result benchmarked on Ollama (0.632 avg score) and is very close to Foundry
    /// Local's default `qwen2.5-1.5b`, making this a legitimate alternate choice rather than a downgrade. See
    /// CLEANUP-MODEL-BENCHMARK.md.
    let model: String
    let completionsURL: URL
    private let timeout: TimeInterval
    private let transport: ChatCompletionsTransport

    init(
        model: String = CleanupSettingsStore.defaultOllamaModel,
        baseURL: URL = ManagedOllamaCleanupProvider.defaultBaseURL,
        timeout: TimeInterval = 30,
        session: URLSession = CleanupProviderFactory.cleanupSession
    ) {
        self.model = model
        self.completionsURL =
            OpenAICompatibleEndpoint.chatCompletionsURL(for: baseURL)
            ?? baseURL.appendingPathComponent("v1/chat/completions")
        self.timeout = timeout
        self.transport = ChatCompletionsTransport(session: session)
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        let completion = try await transport.complete(
            request, at: completionsURL, model: model, bearerToken: nil,
            temperature: CleanupSampling.onDeviceTemperature, defaultTimeout: timeout, provider: .ollama)
        return CleanupResponse(
            cleanedText: completion.text, latency: completion.latency, providerID: id, modelID: model)
    }
}
