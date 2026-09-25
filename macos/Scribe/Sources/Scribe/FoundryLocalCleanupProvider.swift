import Foundation
import os

/// Recommended default local cleanup provider. Foundry Local serves an OpenAI-compatible endpoint on a port its
/// service picks when it starts, unlike Ollama's fixed 11434, so the provider asks `foundry status -o json` for it
/// (the CLI TranscriptionEngine also uses) and remembers the answer. See PORTING-PLAN.md's "AI cleanup provider
/// architecture" for why this is the recommended default (shared runtime with ASR, competitive benchmarked quality,
/// the SDK owns hardware selection).
///
/// The provider lives as long as its configuration does in `CleanupProviderCache`, so the endpoint it found serves
/// every dictation until it is `endpointLifetime` old or stops answering. A service that restarted on another port
/// refuses the connection; the provider then forgets the old endpoint, asks for the new one and sends the request once
/// more.
final class FoundryLocalCleanupProvider: CleanupProvider {
    static let endpointLifetime: Duration = .seconds(600)

    let id = "foundry-local"
    let displayName = "Foundry Local"
    /// Small on-device instruct models follow the terser local guardrail more reliably than the frontier prose; see
    /// `CleanupPrompt.defaultLocalPrompt`.
    let usesLocalCleanupPrompt = true
    /// `qwen2.5-1.5b` is the benchmarked recommendation: it matches Ollama's `qwen2.5:1.5b` on quality with a flatter,
    /// spike-free latency curve. See CLEANUP-MODEL-BENCHMARK.md.
    let modelAlias: String
    private let status: FoundryLocalStatusSource
    private let timeout: TimeInterval
    private let transport: ChatCompletionsTransport
    private let now: @Sendable () -> ContinuousClock.Instant
    private let endpoint = OSAllocatedUnfairLock<ResolvedEndpoint?>(initialState: nil)

    private struct ResolvedEndpoint: Sendable {
        let completionsURL: URL
        let resolvedAt: ContinuousClock.Instant
    }

    init(
        modelAlias: String = CleanupSettingsStore.defaultFoundryLocalModelAlias,
        status: FoundryLocalStatusSource = .live(),
        timeout: TimeInterval = 30,
        session: URLSession = CleanupProviderFactory.cleanupSession,
        now: @escaping @Sendable () -> ContinuousClock.Instant = { ContinuousClock.now }
    ) {
        self.modelAlias = modelAlias
        self.status = status
        self.timeout = timeout
        self.transport = ChatCompletionsTransport(session: session)
        self.now = now
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        let (url, wasCached) = try await completionsURL()
        do {
            return try await send(request, to: url)
        } catch let error as CleanupProviderError where wasCached && error.isConnectionRefusal {
            forget(url)
            ScribeLog.info(.cleanup, "Foundry Local no longer answers at the endpoint it reported; asking again")
            let (fresh, _) = try await completionsURL()
            return try await send(request, to: fresh)
        }
    }

    private func send(_ request: CleanupRequest, to url: URL) async throws -> CleanupResponse {
        let completion = try await transport.complete(
            request, at: url, model: modelAlias, bearerToken: nil, temperature: CleanupSampling.onDeviceTemperature,
            defaultTimeout: timeout, provider: .foundryLocal)
        return CleanupResponse(
            cleanedText: completion.text, latency: completion.latency, providerID: id, modelID: modelAlias)
    }

    /// The endpoint to use now, and whether it came from an earlier lookup.
    private func completionsURL() async throws -> (url: URL, wasCached: Bool) {
        let checkedAt = now()
        if let cached = endpoint.withLock({ $0 }),
            cached.resolvedAt.duration(to: checkedAt) < Self.endpointLifetime
        {
            return (cached.completionsURL, true)
        }

        let base = try await status.lookup()
        guard let url = OpenAICompatibleEndpoint.chatCompletionsURL(for: base) else {
            throw CleanupProviderError.endpointUnavailable(.foundryLocalStatusUnreadable)
        }
        let resolved = ResolvedEndpoint(completionsURL: url, resolvedAt: now())
        endpoint.withLock { $0 = resolved }
        return (url, false)
    }

    private func forget(_ url: URL) {
        endpoint.withLock { current in
            if current?.completionsURL == url {
                current = nil
            }
        }
    }
}

/// How the provider finds Foundry Local's endpoint. `live` runs `foundry status -o json` through `ProcessRunner`, so
/// the lookup never blocks a thread, has a deadline and stops when its task is cancelled; tests pass their own.
struct FoundryLocalStatusSource: Sendable {
    let lookup: @Sendable () async throws -> URL

    init(lookup: @escaping @Sendable () async throws -> URL) {
        self.lookup = lookup
    }

    static func live(environment: [String: String] = ProcessInfo.processInfo.environment) -> FoundryLocalStatusSource {
        FoundryLocalStatusSource {
            guard let cli = FoundryLocalCLI.locate(environment: environment) else {
                throw CleanupProviderError.endpointUnavailable(.foundryLocalNotInstalled)
            }
            let outcome: ProcessRunner.Outcome
            do {
                outcome = try await ProcessRunner.run(
                    cli, arguments: ["status", "-o", "json"], timeout: FoundryLocalCLI.statusTimeout)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                throw CleanupProviderError.endpointUnavailable(.foundryLocalLaunchFailed)
            }
            switch outcome.terminationReason {
            case .cancelled:
                throw CancellationError()
            case .timedOut:
                throw CleanupProviderError.endpointUnavailable(.foundryLocalStatusTimedOut)
            case .finished:
                break
            }
            let base = try FoundryLocalStatus.baseURL(
                fromStatusOutput: outcome.standardOutput.data, exitStatus: outcome.exitStatus)
            ScribeLog.debug(.cleanup, "Read Foundry Local's endpoint", .duration("elapsed", outcome.duration))
            return base
        }
    }
}

enum FoundryLocalCLI {
    static let statusTimeout: Duration = .seconds(20)

    /// `SCRIBE_FOUNDRY_CLI` when it names an executable, otherwise `foundry` in Homebrew's prefixes or on `PATH`.
    static func locate(environment: [String: String]) -> URL? {
        if let override = environment["SCRIBE_FOUNDRY_CLI"], !override.isEmpty {
            return FileManager.default.isExecutableFile(atPath: override) ? URL(fileURLWithPath: override) : nil
        }
        return ProcessRunner.locateExecutable(
            named: "foundry", searchPath: ProcessRunner.defaultSearchPath(environment: environment))
    }
}

/// The part of `foundry status -o json` the provider reads. See TranscriptionEngine.swift for the sibling
/// `foundry transcribe -o json` contract, a different JSON shape.
enum FoundryLocalStatus {
    private struct Status: Decodable {
        struct Service: Decodable {
            let ready: Bool?
            let webUrls: [String]?
        }
        let service: Service
    }

    /// The service's base URL. A status that reports no ready service with an endpoint is `foundryLocalNotReady`;
    /// output that is not a status at all is `foundryLocalNotReady` when the command failed (there was no service to
    /// describe) and `foundryLocalStatusUnreadable` when it succeeded.
    static func baseURL(fromStatusOutput data: Data, exitStatus: Int32?) throws -> URL {
        guard let status = try? JSONDecoder().decode(Status.self, from: data) else {
            throw CleanupProviderError.endpointUnavailable(
                exitStatus == 0 ? .foundryLocalStatusUnreadable : .foundryLocalNotReady)
        }
        guard status.service.ready == true, let text = status.service.webUrls?.first, let url = URL(string: text),
            let host = url.host(percentEncoded: false), !host.isEmpty
        else {
            throw CleanupProviderError.endpointUnavailable(.foundryLocalNotReady)
        }
        return url
    }
}
