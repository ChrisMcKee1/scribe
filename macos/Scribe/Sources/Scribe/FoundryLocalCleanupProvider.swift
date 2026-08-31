import Foundation
import OSLog

/// Recommended default local cleanup provider. Wraps `OpenAICompatibleCleanupProvider`, resolving
/// Foundry Local's dynamic port by shelling out to `foundry status -o json` (the same CLI already
/// used for ASR in TranscriptionEngine.swift), since the daemon's OpenAI-compatible port is not
/// fixed like Ollama's 11434. See PORTING-PLAN.md's "AI cleanup provider architecture" for why this
/// is the recommended default (shared runtime with ASR, competitive benchmarked quality, SDK owns
/// hardware selection).
final class FoundryLocalCleanupProvider: CleanupProvider {
    let id = "foundry-local"
    let displayName = "Foundry Local"

    /// Small on-device instruct models follow the terser local guardrail more reliably than the
    /// frontier prose; see `CleanupPrompt.defaultLocalPrompt`.
    let usesLocalCleanupPrompt = true

    /// `qwen2.5-1.5b` is the benchmarked recommendation: matches Ollama's `qwen2.5:1.5b` on quality
    /// with a flatter, spike-free latency curve. See CLEANUP-MODEL-BENCHMARK.md.
    private let modelAlias: String
    private let foundryCLIPath: String
    private let logger = Logger(subsystem: "com.scribe.macos", category: "Cleanup.FoundryLocal")
    private let transport: OpenAICompatibleCleanupProvider

    /// Cache the resolved base URL briefly: `foundry status` is a subprocess spawn on every call,
    /// which is wasteful for back-to-back dictations. Re-resolved after this interval or if a
    /// request fails, in case the daemon restarted with a new port.
    private let statusCacheDuration: TimeInterval = 30
    private var cachedBaseURL: URL?
    private var cachedAt: Date?

    init(modelAlias: String = "qwen2.5-1.5b", foundryCLIPath: String? = nil) {
        self.modelAlias = modelAlias
        self.foundryCLIPath = foundryCLIPath ?? Self.resolveFoundryCLIPath()

        var resolveBaseURL: (() async -> URL?)!
        self.transport = OpenAICompatibleCleanupProvider(
            id: "foundry-local",
            displayName: "Foundry Local",
            model: modelAlias,
            timeout: 30,
            baseURLProvider: { await resolveBaseURL() })

        resolveBaseURL = { [weak self] in
            await self?.resolveBaseURLWithCache()
        }
    }

    func healthSnapshot() async -> CleanupHealthSnapshot {
        await transport.healthSnapshot()
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        try await transport.clean(request)
    }

    /// Best-effort warm-up: load the model ahead of the first real dictation so cleanup latency
    /// doesn't pay Foundry Local's cold-start cost (which can be tens of seconds; see the ASR
    /// latency profile documented in PORTING-PLAN.md for the same daemon). Failures are logged,
    /// never thrown, since warm-up is an optimization, not a requirement.
    func warmUp() async {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: foundryCLIPath)
        process.arguments = ["model", "load", modelAlias]
        do {
            try process.run()
            process.waitUntilExit()
            if process.terminationStatus == 0 {
                logger.info("Warmed up Foundry Local model \(self.modelAlias, privacy: .public)")
            } else {
                logger.warning("Foundry Local warm-up exited with status \(process.terminationStatus)")
            }
        } catch {
            logger.warning("Foundry Local warm-up failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    private func resolveBaseURLWithCache() async -> URL? {
        if let cachedBaseURL, let cachedAt, Date().timeIntervalSince(cachedAt) < statusCacheDuration {
            return cachedBaseURL
        }

        guard let resolved = await Self.resolveBaseURL(foundryCLIPath: foundryCLIPath) else {
            return nil
        }

        cachedBaseURL = resolved
        cachedAt = Date()
        return resolved
    }

    private static func resolveBaseURL(foundryCLIPath: String) async -> URL? {
        await withCheckedContinuation { continuation in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: foundryCLIPath)
            process.arguments = ["status", "-o", "json"]

            let outputPipe = Pipe()
            process.standardOutput = outputPipe
            process.standardError = Pipe()

            do {
                try process.run()
            } catch {
                continuation.resume(returning: nil)
                return
            }

            process.waitUntilExit()
            let data = outputPipe.fileHandleForReading.readDataToEndOfFile()

            guard
                let status = try? JSONDecoder().decode(FoundryStatus.self, from: data),
                status.service.ready,
                let urlString = status.service.webUrls.first,
                let url = URL(string: urlString)
            else {
                continuation.resume(returning: nil)
                return
            }

            continuation.resume(returning: url)
        }
    }

    private static func resolveFoundryCLIPath() -> String {
        if let override = ProcessInfo.processInfo.environment["SCRIBE_FOUNDRY_CLI"] {
            return override
        }
        for candidate in ["/opt/homebrew/bin/foundry", "/usr/local/bin/foundry"] {
            if FileManager.default.isExecutableFile(atPath: candidate) {
                return candidate
            }
        }
        return "/opt/homebrew/bin/foundry"
    }
}

/// Decodes the subset of `foundry status -o json` this provider needs. See TranscriptionEngine.swift
/// for the sibling `foundry transcribe -o json` contract; this is a different JSON shape.
private struct FoundryStatus: Decodable {
    struct Service: Decodable {
        let ready: Bool
        let webUrls: [String]
    }
    let service: Service
}
