import Foundation

/// Resolves which CleanupProvider to use. Env-var driven for now (SCRIBE_CLEANUP_PROVIDER =
/// "foundry-local" | "ollama" | "openai-compatible"), the same stopgap pattern TranscriptionEngine
/// uses for ASR backend selection, since the Settings UI (macos-overlay-ui todo) doesn't exist yet
/// in this port. Defaults to Foundry Local, matching the recommendation in PORTING-PLAN.md.
enum CleanupProviderResolver {
    /// Keychain service name for the Microsoft Foundry service-principal client secret; the account
    /// is the client id itself, so switching Entra app registrations never reads a stale secret.
    static let azureClientSecretKeychainService = "com.scribe.macos.azure-client-secret"

    static func resolveDefaultProvider() -> CleanupProvider {
        do {
            return try tryResolveDefaultProvider()
        } catch let CleanupProviderError.notConfigured(message) {
            fatalError(message)
        } catch {
            fatalError("Failed to resolve cleanup provider: \(error.localizedDescription)")
        }
    }

    /// Non-fatal variant for the live dictation pipeline: a misconfigured optional AI-cleanup
    /// provider must never crash mid-dictation (see AGENTS.md's offline-first guarantee, ported
    /// conceptually here), so this throws `CleanupProviderError.notConfigured` instead of calling
    /// `fatalError`. The CLI verbs (`--cleanup-text`, usage-insight summaries) keep using the
    /// fatal `resolveDefaultProvider()` above since a hard failure there is fine (and more visible)
    /// for a one-shot command-line invocation.
    static func tryResolveDefaultProvider() throws -> CleanupProvider {
        let environment = ProcessInfo.processInfo.environment
        switch environment["SCRIBE_CLEANUP_PROVIDER"] {
        case "microsoft-foundry":
            return try resolveMicrosoftFoundryProviderThrowing(environment: environment)
        case "ollama":
            let model = environment["SCRIBE_OLLAMA_MODEL"] ?? "qwen2.5:3b"
            return ManagedOllamaCleanupProvider(model: model)
        case "openai-compatible":
            guard
                let baseURLString = environment["SCRIBE_CLEANUP_BASE_URL"],
                let baseURL = URL(string: baseURLString),
                let model = environment["SCRIBE_CLEANUP_MODEL"]
            else {
                throw CleanupProviderError.notConfigured(
                    "SCRIBE_CLEANUP_PROVIDER=openai-compatible requires SCRIBE_CLEANUP_BASE_URL and SCRIBE_CLEANUP_MODEL")
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

    /// Configuration for `SCRIBE_CLEANUP_PROVIDER=microsoft-foundry`:
    ///
    /// - `SCRIBE_AZURE_FOUNDRY_ENDPOINT` (required): e.g. `https://my-resource.cognitiveservices.azure.com`
    /// - `SCRIBE_AZURE_FOUNDRY_DEPLOYMENT` (required): the deployed model name
    /// - `SCRIBE_AZURE_AUTH_MODE`: "cli" (default) or "service-principal"
    /// - `SCRIBE_AZURE_TENANT_ID`: required for service-principal; optional hint for `az` CLI mode
    /// - `SCRIBE_AZURE_CLIENT_ID`: required for service-principal; also the Keychain lookup key for
    ///   the secret, which is set out of band via `Scribe --set-azure-client-secret <client-id>`
    ///   (never via an environment variable, per AGENTS.md).
    private static func resolveMicrosoftFoundryProviderThrowing(environment: [String: String]) throws -> CleanupProvider {
        guard
            let endpointString = environment["SCRIBE_AZURE_FOUNDRY_ENDPOINT"],
            let endpoint = URL(string: endpointString),
            let deployment = environment["SCRIBE_AZURE_FOUNDRY_DEPLOYMENT"]
        else {
            throw CleanupProviderError.notConfigured(
                "SCRIBE_CLEANUP_PROVIDER=microsoft-foundry requires SCRIBE_AZURE_FOUNDRY_ENDPOINT and SCRIBE_AZURE_FOUNDRY_DEPLOYMENT")
        }

        let authMode = AzureAuthMode(rawValue: environment["SCRIBE_AZURE_AUTH_MODE"] == "service-principal" ? "servicePrincipal" : "azureCli")
            ?? .azureCli
        let tenantId = environment["SCRIBE_AZURE_TENANT_ID"]

        let credentialProvider: AzureCredentialProvider
        switch authMode {
        case .servicePrincipal:
            guard let clientId = environment["SCRIBE_AZURE_CLIENT_ID"] else {
                throw CleanupProviderError.notConfigured("SCRIBE_AZURE_AUTH_MODE=service-principal requires SCRIBE_AZURE_CLIENT_ID")
            }
            let secret = try? KeychainStore.get(service: azureClientSecretKeychainService, account: clientId)
            guard
                let principal = AzureServicePrincipal.tryCreate(
                    tenantId: tenantId, clientId: clientId, clientSecret: secret ?? nil)
            else {
                throw CleanupProviderError.notConfigured(
                    "SCRIBE_AZURE_AUTH_MODE=service-principal requires SCRIBE_AZURE_TENANT_ID, SCRIBE_AZURE_CLIENT_ID, and a secret saved via 'Scribe --set-azure-client-secret \(clientId)'")
            }
            credentialProvider = AzureServicePrincipalCredentialProvider(principal: principal)
        case .azureCli:
            credentialProvider = AzureCliCredentialProvider(tenantId: tenantId)
        }

        return MicrosoftFoundryCleanupProvider(
            endpoint: endpoint,
            deployment: deployment,
            credentialProvider: credentialProvider)
    }
}
