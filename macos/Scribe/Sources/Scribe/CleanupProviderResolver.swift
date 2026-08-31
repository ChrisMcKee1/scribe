import Foundation

/// Resolves which CleanupProvider to use. `SCRIBE_CLEANUP_PROVIDER` and its related environment
/// variables take priority when set, preserving the original CLI/scripted-usage contract
/// (`--cleanup-text`, the offline eval harness) unchanged. Once no such environment variable is
/// present, resolution falls through to `CleanupSettingsStore`, which is what the Settings
/// window's "AI Cleanup" tab writes to, so a user can configure everything from the GUI without
/// ever touching an environment variable.
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
        if let envProviderName = environment["SCRIBE_CLEANUP_PROVIDER"] {
            return try resolveFromEnvironment(envProviderName, environment: environment)
        }
        return try resolveFromSettings()
    }

    /// Configuration via environment variables (legacy/scripted path):
    ///
    /// - `SCRIBE_CLEANUP_PROVIDER`: "foundry-local" (default) | "ollama" | "openai-compatible" | "microsoft-foundry"
    /// - `SCRIBE_FOUNDRY_CLEANUP_MODEL`, `SCRIBE_OLLAMA_MODEL`: model overrides for the two managed local providers
    /// - `SCRIBE_CLEANUP_BASE_URL`, `SCRIBE_CLEANUP_MODEL`, `SCRIBE_CLEANUP_API_KEY`: openai-compatible config
    /// - `SCRIBE_AZURE_FOUNDRY_ENDPOINT`, `SCRIBE_AZURE_FOUNDRY_DEPLOYMENT`, `SCRIBE_AZURE_AUTH_MODE`,
    ///   `SCRIBE_AZURE_TENANT_ID`, `SCRIBE_AZURE_CLIENT_ID`: microsoft-foundry config (secret via Keychain, see below)
    private static func resolveFromEnvironment(_ providerName: String, environment: [String: String]) throws -> CleanupProvider {
        switch providerName {
        case "microsoft-foundry":
            return try resolveMicrosoftFoundryProviderThrowing(
                endpointString: environment["SCRIBE_AZURE_FOUNDRY_ENDPOINT"],
                deployment: environment["SCRIBE_AZURE_FOUNDRY_DEPLOYMENT"],
                useServicePrincipal: environment["SCRIBE_AZURE_AUTH_MODE"] == "service-principal",
                tenantId: environment["SCRIBE_AZURE_TENANT_ID"],
                clientId: environment["SCRIBE_AZURE_CLIENT_ID"],
                notConfiguredHint: "'Scribe --set-azure-client-secret <client-id>'")
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

    /// Builds a provider from the Settings window's AI Cleanup tab (`CleanupSettingsStore`), used
    /// whenever no `SCRIBE_CLEANUP_PROVIDER` environment variable is set.
    private static func resolveFromSettings() throws -> CleanupProvider {
        switch CleanupSettingsStore.providerKind {
        case .foundryLocal:
            return FoundryLocalCleanupProvider(modelAlias: CleanupSettingsStore.foundryLocalModelAlias)
        case .ollama:
            return ManagedOllamaCleanupProvider(model: CleanupSettingsStore.ollamaModel)
        case .openAICompatible:
            guard
                !CleanupSettingsStore.openAIBaseURL.isEmpty,
                let baseURL = URL(string: CleanupSettingsStore.openAIBaseURL),
                !CleanupSettingsStore.openAIModel.isEmpty
            else {
                throw CleanupProviderError.notConfigured(
                    "Set the endpoint URL and model for the OpenAI-compatible provider in Settings > AI Cleanup.")
            }
            return OpenAICompatibleCleanupProvider(
                id: "openai-compatible",
                displayName: "OpenAI-compatible endpoint",
                model: CleanupSettingsStore.openAIModel,
                apiKey: CleanupSettingsStore.openAIApiKey(),
                baseURLProvider: { baseURL })
        case .microsoftFoundry:
            let clientId = CleanupSettingsStore.azureClientId
            return try resolveMicrosoftFoundryProviderThrowing(
                endpointString: CleanupSettingsStore.azureEndpoint,
                deployment: CleanupSettingsStore.azureDeployment,
                useServicePrincipal: CleanupSettingsStore.azureAuthMode == .servicePrincipal,
                tenantId: CleanupSettingsStore.azureTenantId.isEmpty ? nil : CleanupSettingsStore.azureTenantId,
                clientId: clientId.isEmpty ? nil : clientId,
                notConfiguredHint: "Settings > AI Cleanup")
        }
    }

    /// Shared by both the environment-variable and Settings-store resolution paths. The service
    /// principal's secret always comes from Keychain (never an env var, a plist, or UserDefaults),
    /// looked up by `clientId` regardless of which path supplied the id.
    private static func resolveMicrosoftFoundryProviderThrowing(
        endpointString: String?,
        deployment: String?,
        useServicePrincipal: Bool,
        tenantId: String?,
        clientId: String?,
        notConfiguredHint: String
    ) throws -> CleanupProvider {
        guard
            let endpointString, !endpointString.isEmpty, let endpoint = URL(string: endpointString),
            let deployment, !deployment.isEmpty
        else {
            throw CleanupProviderError.notConfigured(
                "Microsoft Foundry cleanup requires an endpoint and a deployment name (configure in \(notConfiguredHint)).")
        }

        let credentialProvider: AzureCredentialProvider
        if useServicePrincipal {
            guard let clientId, !clientId.isEmpty else {
                throw CleanupProviderError.notConfigured(
                    "Service-principal auth requires a client id (configure in \(notConfiguredHint)).")
            }
            let secret = try? KeychainStore.get(service: azureClientSecretKeychainService, account: clientId)
            guard
                let principal = AzureServicePrincipal.tryCreate(
                    tenantId: tenantId, clientId: clientId, clientSecret: secret ?? nil)
            else {
                throw CleanupProviderError.notConfigured(
                    "Service-principal auth requires a tenant id, client id, and a saved client secret (configure in \(notConfiguredHint)).")
            }
            credentialProvider = AzureServicePrincipalCredentialProvider(principal: principal)
        } else {
            credentialProvider = AzureCliCredentialProvider(tenantId: tenantId)
        }

        return MicrosoftFoundryCleanupProvider(
            endpoint: endpoint,
            deployment: deployment,
            credentialProvider: credentialProvider)
    }
}
