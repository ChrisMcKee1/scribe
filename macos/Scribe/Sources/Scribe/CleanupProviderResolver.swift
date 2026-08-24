import Foundation

/// Resolves which CleanupProvider to use. Env-var driven for now (SCRIBE_CLEANUP_PROVIDER =
/// "foundry-local" | "ollama" | "openai-compatible"), the same stopgap pattern TranscriptionEngine
/// uses for ASR backend selection, since the Settings UI (macos-overlay-ui todo) doesn't exist yet
/// in this port. Defaults to Foundry Local, matching the recommendation in PORTING-PLAN.md.
enum CleanupProviderResolver {
    static func resolveDefaultProvider() -> CleanupProvider {
        let environment = ProcessInfo.processInfo.environment
        switch environment["SCRIBE_CLEANUP_PROVIDER"] {
        case "ollama":
            let model = environment["SCRIBE_OLLAMA_MODEL"] ?? "qwen2.5:3b"
            return ManagedOllamaCleanupProvider(model: model)
        case "openai-compatible":
            guard
                let baseURLString = environment["SCRIBE_CLEANUP_BASE_URL"],
                let baseURL = URL(string: baseURLString),
                let model = environment["SCRIBE_CLEANUP_MODEL"]
            else {
                fatalError("SCRIBE_CLEANUP_PROVIDER=openai-compatible requires SCRIBE_CLEANUP_BASE_URL and SCRIBE_CLEANUP_MODEL")
            }
            return OpenAICompatibleCleanupProvider(
                id: "openai-compatible",
                displayName: "OpenAI-compatible endpoint",
                model: model,
                apiKey: environment["SCRIBE_CLEANUP_API_KEY"],
                baseURLProvider: { baseURL })
        default:
            let modelAlias = environment["SCRIBE_FOUNDRY_CLEANUP_MODEL"] ?? "qwen2.5-1.5b"
            return FoundryLocalCleanupProvider(modelAlias: modelAlias)
        }
    }
}
