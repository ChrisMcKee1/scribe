import Foundation

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
    /// The audience of the unified `/openai/v1/` endpoint, the one Windows requests
    /// (`AzureOpenAIResponsesClientFactory.AzureAIScope`). The dated deployments route took the Cognitive Services
    /// audience instead.
    static let inferenceScope = "https://ai.azure.com/.default"

    let id = "microsoft-foundry"
    let displayName = "Microsoft Foundry"
    let deployment: String
    /// `{account}/openai/v1/chat/completions`.
    let completionsURL: URL
    private let credential: any AzureCredentialProvider
    private let timeout: TimeInterval
    private let transport: ChatCompletionsTransport

    /// - Parameter inferenceBase: The account's `/openai/v1/` base, from `inferenceBase(for:)`.
    init(
        inferenceBase: URL,
        deployment: String,
        credential: any AzureCredentialProvider,
        timeout: TimeInterval = 30,
        session: URLSession = CleanupProviderFactory.cleanupSession
    ) {
        self.deployment = deployment
        self.completionsURL = inferenceBase.appendingPathComponent("chat").appendingPathComponent("completions")
        self.credential = credential
        self.timeout = timeout
        self.transport = ChatCompletionsTransport(session: session)
    }

    /// The account's inference base, `{scheme}://{host}[:{port}]/openai/v1/`, from any endpoint a user pastes: the
    /// resource endpoint, a Foundry project URL (`.../api/projects/<name>`), a URL that already ends in `/openai/v1`,
    /// or a dated deployments URL. The path, query, fragment and any user info are dropped, as Windows'
    /// `AzureOpenAIResponsesClientFactory.GetV1Endpoint` keeps only the authority. `nil` unless the endpoint is an
    /// http or https URL with a host.
    static func inferenceBase(for endpoint: URL) -> URL? {
        guard let components = URLComponents(url: endpoint, resolvingAgainstBaseURL: false),
            let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https",
            let host = components.host?.lowercased(), !host.isEmpty
        else {
            return nil
        }
        var base = URLComponents()
        base.scheme = scheme
        base.host = host
        base.port = components.port
        base.path = "/openai/v1/"
        return base.url
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        let token: AzureAccessToken
        do {
            token = try await credential.accessToken(scope: Self.inferenceScope)
        } catch let error as AzureCredentialError {
            throw CleanupProviderError.credentialUnavailable(error)
        }
        let completion = try await transport.complete(
            request, at: completionsURL, model: deployment, bearerToken: token.token, temperature: nil,
            defaultTimeout: timeout, provider: .microsoftFoundry)
        return CleanupResponse(
            cleanedText: completion.text, latency: completion.latency, providerID: id, modelID: deployment)
    }
}
