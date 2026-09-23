import Foundation
import Security

/// One validated cleanup configuration: every setting a provider is built from except the secret itself, which the
/// secret store's revision stands in for. Two equal connections build interchangeable providers, so
/// `CleanupProviderCache` keys on it. The prompt is not part of it; it travels with each `CleanupRequest`.
///
/// Printing or dumping one, or its target, shows only the provider kind, since the endpoint, deployment, model and ids
/// are the user's.
struct CleanupConnection: Hashable, Sendable, CustomStringConvertible, CustomReflectable {
    enum Target: Hashable, Sendable, CustomStringConvertible, CustomReflectable {
        case foundryLocal(modelAlias: String)
        case ollama(model: String)
        case openAICompatible(completionsURL: URL, model: String, apiKey: CleanupSecretSource)
        case microsoftFoundry(inferenceBase: URL, deployment: String, identity: AzureIdentity)

        var kind: CleanupProviderKind {
            switch self {
            case .foundryLocal: return .foundryLocal
            case .ollama: return .ollama
            case .openAICompatible: return .openAICompatible
            case .microsoftFoundry: return .microsoftFoundry
            }
        }

        var description: String { "CleanupConnection.Target(\(kind.rawValue))" }
        var customMirror: Mirror { Mirror(self, children: ["kind": kind]) }
    }

    let target: Target
    let source: CleanupConfigurationSource

    var kind: CleanupProviderKind { target.kind }

    var description: String { "CleanupConnection(\(kind.rawValue))" }
    var customMirror: Mirror { Mirror(self, children: ["kind": kind]) }
}

/// Where the OpenAI-compatible API key comes from.
enum CleanupSecretSource: Hashable, Sendable {
    /// `SCRIBE_CLEANUP_API_KEY`, fixed for the life of the process.
    case environment
    /// The settings store's secret store, as of `CleanupSettingsStore.secretRevision`.
    case secretStore(revision: String)
}

/// What providers are built with. `live` sends requests through `cleanupSession`, runs the real `az` and `foundry`
/// tools and reads the system clocks; tests pass stubs and fakes.
struct CleanupProviderFactory: Sendable {
    /// One ephemeral session for every cleanup and token request, so no response, cookie or credential from a cleanup
    /// endpoint or from Entra is ever written to disk.
    static let cleanupSession: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.urlCache = nil
        configuration.httpCookieAcceptPolicy = .never
        configuration.httpShouldSetCookies = false
        return URLSession(configuration: configuration)
    }()

    var session: URLSession
    var foundryLocalStatus: FoundryLocalStatusSource
    var azureCliSearchPath: [String]
    var azureCliLane: AsyncLane
    var azureCliLaunch: AzureCliCredentialProvider.Launch
    /// Wall-clock time, for token expiry.
    var now: @Sendable () -> Date
    /// Elapsed time, for how long Foundry Local's endpoint is trusted.
    var monotonicNow: @Sendable () -> ContinuousClock.Instant

    static var live: CleanupProviderFactory {
        CleanupProviderFactory(
            session: cleanupSession,
            foundryLocalStatus: .live(),
            azureCliSearchPath: ProcessRunner.defaultSearchPath(),
            azureCliLane: AzureCliCredentialProvider.processLane,
            azureCliLaunch: AzureCliCredentialProvider.launchThroughProcessRunner,
            now: { Date() },
            monotonicNow: { ContinuousClock.now })
    }
}

/// Turns the stored settings, or the `SCRIBE_CLEANUP_PROVIDER` environment variables when they are set, into a
/// validated `CleanupConnection`, and a connection into a provider.
///
/// The environment takes priority when `SCRIBE_CLEANUP_PROVIDER` is set, preserving the CLI and scripted contract
/// (`--cleanup-text`, the offline eval harness); otherwise the Settings window's AI Cleanup tab decides, through
/// `CleanupSettingsStore`, so a user can configure everything without an environment variable.
///
/// The app resolves through `CleanupProviderCache`, which keeps one provider per connection. The uncached entry points
/// here, `tryResolveDefaultProvider` and `resolveDefaultProvider`, build a fresh provider every time and are for
/// one-shot use.
enum CleanupProviderResolver {
    /// The configuration to clean with now. Throws `CleanupProviderError.notConfigured` when it is incomplete or
    /// invalid. It reads only preferences: the secret store is read when a provider is built, not here.
    ///
    /// The environment variables of the scripted path:
    /// - `SCRIBE_CLEANUP_PROVIDER`: "foundry-local" (default) | "ollama" | "openai-compatible" | "microsoft-foundry"
    /// - `SCRIBE_FOUNDRY_CLEANUP_MODEL`, `SCRIBE_OLLAMA_MODEL`: model overrides for the two managed local providers
    /// - `SCRIBE_CLEANUP_BASE_URL`, `SCRIBE_CLEANUP_MODEL`, `SCRIBE_CLEANUP_API_KEY`: openai-compatible config
    /// - `SCRIBE_AZURE_FOUNDRY_ENDPOINT`, `SCRIBE_AZURE_FOUNDRY_DEPLOYMENT`, `SCRIBE_AZURE_AUTH_MODE`,
    ///   `SCRIBE_AZURE_TENANT_ID`, `SCRIBE_AZURE_CLIENT_ID`: microsoft-foundry config. The client secret always comes
    ///   from the Keychain, saved with `Scribe --set-azure-client-secret <client-id>`, never from the environment.
    static func connection(store: CleanupSettingsStore, environment: [String: String]) throws -> CleanupConnection {
        if let providerName = environment["SCRIBE_CLEANUP_PROVIDER"] {
            return try environmentConnection(providerName, environment: environment, store: store)
        }
        return try settingsConnection(store.snapshot())
    }

    /// Builds the provider for `connection`, reading its secret, when it has one, from `store`. Microsoft Foundry
    /// credentials come from `credentials`, so a provider rebuilt for the same identity keeps its token.
    static func makeProvider(
        for connection: CleanupConnection,
        store: CleanupSettingsStore,
        environment: [String: String],
        credentials: AzureCredentialCache,
        factory: CleanupProviderFactory
    ) throws -> any CleanupProvider {
        switch connection.target {
        case .foundryLocal(let modelAlias):
            return FoundryLocalCleanupProvider(
                modelAlias: modelAlias, status: factory.foundryLocalStatus, session: factory.session,
                now: factory.monotonicNow)
        case .ollama(let model):
            return ManagedOllamaCleanupProvider(model: model, session: factory.session)
        case .openAICompatible(let completionsURL, let model, let keySource):
            let apiKey: String?
            switch keySource {
            case .environment:
                apiKey = environment["SCRIBE_CLEANUP_API_KEY"]
            case .secretStore:
                apiKey = try readSecret { try store.readOpenAIApiKey() }
            }
            return OpenAICompatibleCleanupProvider(
                model: model, apiKey: apiKey, completionsURL: completionsURL, session: factory.session)
        case .microsoftFoundry(let inferenceBase, let deployment, let identity):
            let credential = try credentials.credential(for: identity) {
                try makeCredential(for: identity, store: store, source: connection.source, factory: factory)
            }
            return MicrosoftFoundryCleanupProvider(
                inferenceBase: inferenceBase, deployment: deployment, credential: credential, session: factory.session)
        }
    }

    /// A fresh provider for the live settings and environment, uncached: for one-shot use and tests. The app goes
    /// through `CleanupProviderCache.shared` instead.
    static func tryResolveDefaultProvider(
        store: CleanupSettingsStore = .live,
        environment: [String: String] = ProcessInfo.processInfo.environment,
        factory: CleanupProviderFactory = .live
    ) throws -> any CleanupProvider {
        let connection = try connection(store: store, environment: environment)
        return try makeProvider(
            for: connection, store: store, environment: environment, credentials: AzureCredentialCache(),
            factory: factory)
    }

    /// For the command line verbs only (`--cleanup-text`), where a hard stop is the clearest answer to an incomplete
    /// setup. Everything in the running app, dictation, Test Connection and the usage summary included, goes through
    /// `CleanupProviderCache.provider()`, which throws instead, because an optional feature must never end the app.
    static func resolveDefaultProvider() -> any CleanupProvider {
        do {
            return try tryResolveDefaultProvider()
        } catch {
            fatalError(error.localizedDescription)
        }
    }

    // MARK: - Connections

    private static func environmentConnection(
        _ providerName: String, environment: [String: String], store: CleanupSettingsStore
    ) throws -> CleanupConnection {
        let source = CleanupConfigurationSource.environment
        switch providerName {
        case "microsoft-foundry":
            return try microsoftFoundryConnection(
                endpoint: environment["SCRIBE_AZURE_FOUNDRY_ENDPOINT"],
                deployment: environment["SCRIBE_AZURE_FOUNDRY_DEPLOYMENT"],
                authMode: environment["SCRIBE_AZURE_AUTH_MODE"] == "service-principal" ? .servicePrincipal : .azureCli,
                tenantId: environment["SCRIBE_AZURE_TENANT_ID"],
                clientId: environment["SCRIBE_AZURE_CLIENT_ID"],
                secretRevision: store.secretRevision,
                source: source)
        case "ollama":
            return try ollamaConnection(
                model: environment["SCRIBE_OLLAMA_MODEL"] ?? CleanupSettingsStore.defaultOllamaModel, source: source)
        case "openai-compatible":
            return try openAICompatibleConnection(
                baseURL: environment["SCRIBE_CLEANUP_BASE_URL"], model: environment["SCRIBE_CLEANUP_MODEL"],
                apiKey: .environment, source: source)
        default:
            return try foundryLocalConnection(
                modelAlias: environment["SCRIBE_FOUNDRY_CLEANUP_MODEL"]
                    ?? CleanupSettingsStore.defaultFoundryLocalModelAlias,
                source: source)
        }
    }

    private static func settingsConnection(_ settings: CleanupSettingsSnapshot) throws -> CleanupConnection {
        let source = CleanupConfigurationSource.settings
        switch settings.providerKind {
        case .foundryLocal:
            return try foundryLocalConnection(modelAlias: settings.foundryLocalModelAlias, source: source)
        case .ollama:
            return try ollamaConnection(model: settings.ollamaModel, source: source)
        case .openAICompatible:
            return try openAICompatibleConnection(
                baseURL: settings.openAIBaseURL, model: settings.openAIModel,
                apiKey: .secretStore(revision: settings.secretRevision), source: source)
        case .microsoftFoundry:
            return try microsoftFoundryConnection(
                endpoint: settings.azureEndpoint,
                deployment: settings.azureDeployment,
                authMode: settings.azureAuthMode,
                tenantId: settings.azureTenantId,
                clientId: settings.azureClientId,
                secretRevision: settings.secretRevision,
                source: source)
        }
    }

    private static func foundryLocalConnection(
        modelAlias: String, source: CleanupConfigurationSource
    ) throws -> CleanupConnection {
        guard let alias = trimmed(modelAlias) else {
            throw CleanupProviderError.notConfigured(.foundryLocalModelMissing, source: source)
        }
        return CleanupConnection(target: .foundryLocal(modelAlias: alias), source: source)
    }

    private static func ollamaConnection(
        model: String, source: CleanupConfigurationSource
    ) throws -> CleanupConnection {
        guard let model = trimmed(model) else {
            throw CleanupProviderError.notConfigured(.ollamaModelMissing, source: source)
        }
        return CleanupConnection(target: .ollama(model: model), source: source)
    }

    private static func openAICompatibleConnection(
        baseURL: String?, model: String?, apiKey: CleanupSecretSource, source: CleanupConfigurationSource
    ) throws -> CleanupConnection {
        guard let baseText = trimmed(baseURL) else {
            throw CleanupProviderError.notConfigured(.openAIEndpointMissing, source: source)
        }
        guard let base = URL(string: baseText),
            let completionsURL = OpenAICompatibleEndpoint.chatCompletionsURL(for: base)
        else {
            throw CleanupProviderError.notConfigured(.openAIEndpointInvalid, source: source)
        }
        guard let model = trimmed(model) else {
            throw CleanupProviderError.notConfigured(.openAIModelMissing, source: source)
        }
        return CleanupConnection(
            target: .openAICompatible(completionsURL: completionsURL, model: model, apiKey: apiKey), source: source)
    }

    private static func microsoftFoundryConnection(
        endpoint: String?,
        deployment: String?,
        authMode: AzureAuthMode,
        tenantId: String?,
        clientId: String?,
        secretRevision: String,
        source: CleanupConfigurationSource
    ) throws -> CleanupConnection {
        guard let endpointText = trimmed(endpoint) else {
            throw CleanupProviderError.notConfigured(.azureEndpointMissing, source: source)
        }
        guard let endpointURL = URL(string: endpointText),
            let inferenceBase = MicrosoftFoundryCleanupProvider.inferenceBase(for: endpointURL)
        else {
            throw CleanupProviderError.notConfigured(.azureEndpointInvalid, source: source)
        }
        guard let deployment = trimmed(deployment) else {
            throw CleanupProviderError.notConfigured(.azureDeploymentMissing, source: source)
        }
        let tenant = trimmed(tenantId)
        if let tenant, !AzureTenant.isValid(tenant) {
            throw CleanupProviderError.notConfigured(.azureTenantInvalid, source: source)
        }

        let identity: AzureIdentity
        switch authMode {
        case .azureCli:
            identity = .azureCli(tenantId: tenant)
        case .servicePrincipal:
            guard let clientId = trimmed(clientId) else {
                throw CleanupProviderError.notConfigured(.azureClientIdMissing, source: source)
            }
            guard let tenant else {
                throw CleanupProviderError.notConfigured(.azureTenantMissing, source: source)
            }
            identity = .servicePrincipal(tenantId: tenant, clientId: clientId, secretRevision: secretRevision)
        }
        return CleanupConnection(
            target: .microsoftFoundry(inferenceBase: inferenceBase, deployment: deployment, identity: identity),
            source: source)
    }

    // MARK: - Credentials

    private static func makeCredential(
        for identity: AzureIdentity,
        store: CleanupSettingsStore,
        source: CleanupConfigurationSource,
        factory: CleanupProviderFactory
    ) throws -> any AzureCredentialProvider {
        switch identity {
        case .azureCli(let tenantId):
            return AzureCliCredentialProvider(
                tenantId: tenantId, searchPath: factory.azureCliSearchPath, lane: factory.azureCliLane,
                launch: factory.azureCliLaunch, now: factory.now)
        case .servicePrincipal(let tenantId, let clientId, _):
            let secret = try readSecret { try store.readAzureClientSecret(clientId: clientId) }
            guard let secret, !secret.isEmpty else {
                throw CleanupProviderError.notConfigured(.azureClientSecretMissing, source: source)
            }
            return AzureServicePrincipalCredentialProvider(
                principal: AzureServicePrincipal(tenantId: tenantId, clientId: clientId, clientSecret: secret),
                session: factory.session, now: factory.now)
        }
    }

    /// A secret store read, with a failure to read kept apart from a secret that was never saved.
    private static func readSecret(_ read: () throws -> String?) throws -> String? {
        do {
            return try read()
        } catch let error as KeychainStore.KeychainError {
            throw CleanupProviderError.secretUnavailable(error)
        } catch {
            throw CleanupProviderError.secretUnavailable(.unhandled(errSecInternalComponent))
        }
    }

    private static func trimmed(_ value: String?) -> String? {
        guard let value = value?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty else { return nil }
        return value
    }
}
