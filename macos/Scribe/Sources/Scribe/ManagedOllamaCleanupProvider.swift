import Foundation
import OSLog

/// Fully supported alternative local cleanup provider, not deprecated by Foundry Local. Talks to
/// Ollama's fixed local port (11434, unlike Foundry Local's dynamic port) over its OpenAI-compatible
/// endpoint. Appropriate for users who already run Ollama for other tools or prefer its model
/// catalog. See PORTING-PLAN.md's "AI cleanup provider architecture" for the two-managed-provider
/// design (Foundry Local pre-selected by default, Ollama a first-class alternative).
final class ManagedOllamaCleanupProvider: CleanupProvider {
    let id = "managed-ollama"
    let displayName = "Ollama"

    /// `qwen2.5:3b` is the best-quality result benchmarked on Ollama (0.632 avg score) and is very
    /// close to Foundry Local's default `qwen2.5-1.5b`, making this a legitimate alternate choice
    /// rather than a downgrade. See CLEANUP-MODEL-BENCHMARK.md.
    private let model: String
    private let baseURL: URL
    private let ollamaCLIPath: String
    private let logger = Logger(subsystem: "com.scribe.macos", category: "Cleanup.Ollama")
    private let transport: OpenAICompatibleCleanupProvider

    init(model: String = "qwen2.5:3b", baseURL: URL = URL(string: "http://127.0.0.1:11434")!, ollamaCLIPath: String? = nil) {
        self.model = model
        self.baseURL = baseURL
        self.ollamaCLIPath = ollamaCLIPath ?? Self.resolveOllamaCLIPath()
        self.transport = OpenAICompatibleCleanupProvider(
            id: "managed-ollama",
            displayName: "Ollama",
            model: model,
            timeout: 30,
            baseURLProvider: { baseURL })
    }

    func healthSnapshot() async -> CleanupHealthSnapshot {
        await transport.healthSnapshot()
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        try await transport.clean(request)
    }

    /// Starts the Ollama daemon if it is not already running (`ollama serve` backgrounds itself
    /// once the model is loaded). Best-effort: failures are logged, never thrown, matching
    /// FoundryLocalCleanupProvider's warm-up contract.
    func warmUp() async {
        let health = await healthSnapshot()
        guard !health.reachable else {
            logger.debug("Ollama already reachable, skipping daemon start")
            return
        }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: ollamaCLIPath)
        process.arguments = ["serve"]
        // Detach: `ollama serve` runs until killed, so this must not block warm-up or hold the
        // pipe open for the app's lifetime.
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
            logger.info("Started Ollama daemon (pid \(process.processIdentifier))")
        } catch {
            logger.warning("Could not start Ollama daemon: \(error.localizedDescription, privacy: .public)")
        }
    }

    private static func resolveOllamaCLIPath() -> String {
        if let override = ProcessInfo.processInfo.environment["SCRIBE_OLLAMA_CLI"] {
            return override
        }
        for candidate in ["/opt/homebrew/bin/ollama", "/usr/local/bin/ollama"] {
            if FileManager.default.isExecutableFile(atPath: candidate) {
                return candidate
            }
        }
        return "/opt/homebrew/bin/ollama"
    }
}
