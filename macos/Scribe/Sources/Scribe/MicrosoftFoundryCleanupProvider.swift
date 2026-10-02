import Foundation
import os

/// Cloud AI cleanup via Microsoft Foundry (Azure), reached directly over REST rather than through an SDK (Azure's .NET
/// Agent Framework and Azure.Identity have no macOS-relevant Swift equivalent). Authenticates with the user's own
/// Azure CLI session or a pinned Entra service principal (see AzureCredential.swift).
///
/// Requests go to the account's unified inference endpoint, `{account}/openai/v1/chat/completions`, with `model` set to
/// the deployment name, whichever endpoint shape was saved. Windows 0.4.3 routes the same way after a Foundry project's
/// own route returned HTTP 500 for a model the account endpoint served. The body has no `temperature`, which reasoning
/// deployments reject, and no `store`, which Chat Completions act on only when it is `true`.
///
/// Deliberately has no ARM subscription or deployment discovery: the endpoint and deployment name are supplied
/// directly, mirroring how Windows' service-principal mode hides ARM discovery (a data-plane-only permission
/// footprint), applied to both auth modes here.
final class MicrosoftFoundryCleanupProvider: CleanupProvider {
    private enum AzureReasoningEffort: String, Equatable {
        case none
        case low
    }

    private enum AzureReasoningMode: Equatable, Sendable {
        case none
        case low
        case omitted

        var field: String? {
            switch self {
            case .none: return AzureReasoningEffort.none.rawValue
            case .low: return AzureReasoningEffort.low.rawValue
            case .omitted: return nil
            }
        }

        var next: AzureReasoningMode? {
            switch self {
            case .none: return .low
            case .low: return .omitted
            case .omitted: return nil
            }
        }
    }

    private static let reasoningField = "reasoning_effort"
    private static let promptCacheField = "prompt_cache_options"
    private static let promptCacheExplicitMode = "explicit"
    /// The audience of the unified `/openai/v1/` endpoint, the one Windows requests
    /// (`AzureOpenAIResponsesClientFactory.AzureAIScope`). The dated deployments route took the Cognitive Services
    /// audience instead.
    static let inferenceScope = "https://ai.azure.com/.default"

    let id = "microsoft-foundry"
    let displayName = "Microsoft Foundry"
    let deployment: String
    /// `{account}/openai/v1/chat/completions`.
    let completionsURL: URL
    private let promptCachingEnabled: @Sendable () -> Bool
    private let credential: any AzureCredentialProvider
    private let timeout: TimeInterval
    private let transport: ChatCompletionsTransport
    private let reasoningMode = OSAllocatedUnfairLock(initialState: AzureReasoningMode.none)

    /// - Parameter inferenceBase: The account's `/openai/v1/` base, from `inferenceBase(for:)`.
    init(
        inferenceBase: URL,
        deployment: String,
        promptCachingEnabled: @escaping @Sendable () -> Bool = { CleanupSettingsStore.live.azurePromptCaching },
        credential: any AzureCredentialProvider,
        timeout: TimeInterval = 30,
        session: URLSession = CleanupProviderFactory.cleanupSession
    ) {
        self.deployment = deployment
        self.completionsURL = inferenceBase.appendingPathComponent("chat").appendingPathComponent("completions")
        self.promptCachingEnabled = promptCachingEnabled
        self.credential = credential
        self.timeout = timeout
        self.transport = ChatCompletionsTransport(session: session)
    }

    /// The account's inference base, `{scheme}://{host}[:{port}]/openai/v1/`, from any endpoint a user pastes: the
    /// resource endpoint, a Foundry project URL (`.../api/projects/<name>`), a URL that already ends in `/openai/v1`,
    /// or a dated deployments URL. The path, query, fragment and any user info are dropped, as Windows'
    /// `AzureOpenAIResponsesClientFactory.GetV1Endpoint` keeps only the authority. `nil` unless the endpoint is an
    /// https URL with a host: Azure serves these endpoints over TLS only, and the Entra token each request carries
    /// must never cross the network in plain text.
    static func inferenceBase(for endpoint: URL) -> URL? {
        guard let components = URLComponents(url: endpoint, resolvingAgainstBaseURL: false),
            components.scheme?.lowercased() == "https",
            let host = components.host?.lowercased(), !host.isEmpty
        else {
            return nil
        }
        var base = URLComponents()
        base.scheme = "https"
        base.host = host
        base.port = components.port
        base.path = "/openai/v1/"
        return base.url
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        try request.receipt?.check()
        let promptCaching = request.receipt?.recipient.settings.azurePromptCaching ?? promptCachingEnabled()
        let token: AzureAccessToken
        do {
            token = try await credential.accessToken(scope: Self.inferenceScope)
        } catch let error as AzureCredentialError {
            throw CleanupProviderError.credentialUnavailable(error)
        }
        var mode = reasoningMode.withLock { $0 }
        while true {
            do {
                let completion = try await transport.complete(
                    request,
                    at: completionsURL,
                    model: deployment,
                    bearerToken: token.token,
                    temperature: nil,
                    reasoningEffort: mode.field,
                    promptCacheMode: promptCaching ? nil : Self.promptCacheExplicitMode,
                    defaultTimeout: timeout,
                    provider: .microsoftFoundry)
                return CleanupResponse(
                    cleanedText: completion.text,
                    latency: completion.latency,
                    providerID: id,
                    modelID: deployment)
            } catch let error as CleanupProviderError {
                if !promptCaching, Self.namesField(Self.promptCacheField, in: error) {
                    throw error
                }
                guard let next = Self.retryMode(after: mode, for: error) else {
                    throw error
                }
                let currentMode = mode
                mode = reasoningMode.withLock { state in
                    if state == currentMode {
                        state = next
                    }
                    return state
                }
            }
        }
    }

    private static func retryMode(
        after mode: AzureReasoningMode,
        for error: CleanupProviderError
    ) -> AzureReasoningMode? {
        guard namesField(reasoningField, in: error) else { return nil }
        return mode.next
    }

    private static func namesField(_ field: String, in error: CleanupProviderError) -> Bool {
        guard case .rejected(let status, _, let reply) = error, status == 400 else { return false }
        return reply.code?.localizedCaseInsensitiveContains(field) == true
            || reply.message?.localizedCaseInsensitiveContains(field) == true
    }
}
