import Foundation
import OSLog

/// Cloud AI cleanup via Microsoft Foundry (Azure), reached directly over REST rather than through
/// an SDK (Azure's .NET Agent Framework / Azure.Identity have no macOS-relevant Swift equivalent).
/// Authenticates with either the user's own Azure CLI session or a pinned Entra service principal
/// (see AzureCredential.swift), and calls the Azure OpenAI-compatible chat completions endpoint,
/// reusing the same JSON wire format as OpenAICompatibleCleanupProvider.
///
/// Deliberately has no ARM/subscription/deployment discovery: the endpoint and deployment name are
/// supplied directly, mirroring how Windows' own service-principal mode already hides ARM discovery
/// (a data-plane-only permission footprint), just applied uniformly to both auth modes here since
/// macOS has no Settings GUI yet to drive a discovery flow from.
final class MicrosoftFoundryCleanupProvider: CleanupProvider {
    /// The Cognitive Services resource scope covers both Foundry and classic Azure OpenAI
    /// resources; see AGENTS.md's RBAC section (`Foundry User` / `Cognitive Services OpenAI User`
    /// both grant access under this same resource type).
    static let defaultScope = "https://cognitiveservices.azure.com/.default"
    static let defaultAPIVersion = "2024-08-01-preview"

    let id = "microsoft-foundry"
    let displayName: String

    private let endpoint: URL
    private let deployment: String
    private let apiVersion: String
    private let scope: String
    private let model: String
    private let credentialProvider: AzureCredentialProvider
    private let timeout: TimeInterval
    private let session: URLSession
    private let logger: Logger

    init(
        endpoint: URL,
        deployment: String,
        apiVersion: String = MicrosoftFoundryCleanupProvider.defaultAPIVersion,
        scope: String = MicrosoftFoundryCleanupProvider.defaultScope,
        credentialProvider: AzureCredentialProvider,
        displayName: String = "Microsoft Foundry",
        timeout: TimeInterval = 30,
        session: URLSession = .shared
    ) {
        self.endpoint = endpoint
        self.deployment = deployment
        self.apiVersion = apiVersion
        self.scope = scope
        self.model = deployment
        self.credentialProvider = credentialProvider
        self.displayName = displayName
        self.timeout = timeout
        self.session = session
        self.logger = Logger(subsystem: "com.scribe.macos", category: "Cleanup.microsoft-foundry")
    }

    func healthSnapshot() async -> CleanupHealthSnapshot {
        do {
            _ = try await credentialProvider.accessToken(scope: scope)
            return CleanupHealthSnapshot(
                providerID: id, reachable: true, detail: "Authenticated for \(endpoint.absoluteString)")
        } catch {
            return CleanupHealthSnapshot(providerID: id, reachable: false, detail: error.localizedDescription)
        }
    }

    func clean(_ cleanupRequest: CleanupRequest) async throws -> CleanupResponse {
        let token: AzureAccessToken
        do {
            token = try await credentialProvider.accessToken(scope: scope)
        } catch {
            throw CleanupProviderError.notConfigured(error.localizedDescription)
        }

        guard var components = URLComponents(
            url: endpoint.appendingPathComponent("openai/deployments/\(deployment)/chat/completions"),
            resolvingAgainstBaseURL: false)
        else {
            throw CleanupProviderError.notConfigured("Invalid Microsoft Foundry endpoint")
        }
        components.queryItems = [URLQueryItem(name: "api-version", value: apiVersion)]
        guard let url = components.url else {
            throw CleanupProviderError.notConfigured("Invalid Microsoft Foundry endpoint")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = timeout
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(token.token)", forHTTPHeaderField: "Authorization")

        let body = ChatCompletionRequest(
            model: model,
            messages: [
                .init(role: "system", content: cleanupRequest.writingStylePrompt),
                .init(role: "user", content: cleanupRequest.transcript)
            ],
            temperature: 0.2,
            stream: false)
        request.httpBody = try JSONEncoder().encode(body)

        let started = Date()
        let (data, response): (Data, URLResponse)
        do {
            (data, response) = try await session.data(for: request)
        } catch let urlError as URLError where urlError.code == .timedOut {
            throw CleanupProviderError.timedOut
        } catch {
            throw CleanupProviderError.requestFailed(error.localizedDescription)
        }

        guard let httpResponse = response as? HTTPURLResponse else {
            throw CleanupProviderError.invalidResponse("No HTTP response")
        }
        guard (200..<300).contains(httpResponse.statusCode) else {
            throw CleanupProviderError.requestFailed(describeFailure(status: httpResponse.statusCode, data: data))
        }

        let decoded: ChatCompletionResponse
        do {
            decoded = try JSONDecoder().decode(ChatCompletionResponse.self, from: data)
        } catch {
            throw CleanupProviderError.invalidResponse("Could not decode chat completion: \(error.localizedDescription)")
        }

        guard let text = decoded.choices.first?.message.content, !text.isEmpty else {
            throw CleanupProviderError.invalidResponse("Empty completion from \(displayName)")
        }

        let latency = Date().timeIntervalSince(started)
        logger.debug("Cleanup via \(self.deployment, privacy: .public) completed in \(latency, format: .fixed(precision: 3))s")

        return CleanupResponse(
            cleanedText: CleanupPrompt.stripTranscriptTags(text),
            latency: latency,
            providerID: id,
            modelID: deployment)
    }

    /// Surfaces the two failure modes AGENTS.md documents as easy to misdiagnose: a fresh role
    /// assignment that hasn't propagated yet (which looks identical to "wrong role" for the first
    /// several minutes), and a Cognitive Services resource missing the custom-subdomain setting
    /// Entra auth requires (a regional endpoint rejects the token regardless of role).
    private func describeFailure(status: Int, data: Data) -> String {
        let bodyText = String(data: data, encoding: .utf8) ?? "<undecodable body>"
        guard status == 401 || status == 403 else {
            return "HTTP \(status): \(bodyText)"
        }
        return """
        HTTP \(status): \(bodyText)
        This is commonly caused by one of: the role assignment has not finished propagating yet \
        (this can take longer than the widely quoted five minutes), the resource is missing the \
        custom subdomain Entra auth requires (a regional endpoint always rejects the token), or the \
        signed-in identity does not hold the "Foundry User" (Foundry) or "Cognitive Services OpenAI \
        User" (classic Azure OpenAI) role on the resource.
        """
    }
}
