import Foundation

enum CleanupCredentialInput: Sendable, CustomStringConvertible, CustomReflectable {
    case saved
    case replace(String)
    case remove

    var description: String { "CleanupCredentialInput" }
    var customMirror: Mirror { Mirror(self, children: [:]) }

    func resolve(_ saved: () throws -> String?) rethrows -> String? {
        switch self {
        case .saved: return try saved()
        case .replace(let value): return value.isEmpty ? nil : value
        case .remove: return nil
        }
    }
}

/// A candidate never writes to a settings or credential store. Environment overrides are resolved by the same
/// resolver as dictation, and its receipt uses a private authority which cannot authorize a serving request.
struct CleanupCandidate: Sendable, CustomStringConvertible, CustomReflectable {
    let settings: CleanupSettingsSnapshot
    var apiKey: CleanupCredentialInput = .saved
    var clientSecret: CleanupCredentialInput = .saved
    var writingStyle = CleanupPrompt.defaultWritingStyle

    var description: String { "CleanupCandidate" }
    var customMirror: Mirror { Mirror(self, children: [:]) }

    func recipient(environment: [String: String]) throws -> CleanupRecipient {
        CleanupRecipient(
            connection: try CleanupProviderResolver.connection(settings: settings, environment: environment),
            settings: settings)
    }

    func makeProvider(
        recipient: CleanupRecipient,
        store: CleanupSettingsStore,
        environment: [String: String],
        factory: CleanupProviderFactory
    ) throws -> any CleanupProvider {
        let source = recipient.connection.source
        switch recipient.connection.target {
        case .foundryLocal(let modelAlias):
            return FoundryLocalCleanupProvider(
                modelAlias: modelAlias, status: factory.foundryLocalStatus, session: factory.session,
                now: factory.monotonicNow)
        case .ollama(let model):
            return ManagedOllamaCleanupProvider(model: model, session: factory.session)
        case .openAICompatible(let url, let model, let keySource, let apiStyle):
            let key: String?
            switch keySource {
            case .environment:
                key = environment["SCRIBE_CLEANUP_API_KEY"]
            case .secretStore:
                key = try apiKey.resolve { try store.readOpenAIApiKey() }
            }
            let app: LocalServerApp
            if source == .settings {
                app = settings.selectedLocalApp != .none
                    ? settings.selectedLocalApp
                    : (key == nil ? LocalAiServer.appAt(url.absoluteString) : .none)
            } else {
                app = .none
            }
            let tuning = source == .settings ? LocalModelTuning.forSettings(settings) : .none
            let session = factory.session
            return OpenAICompatibleCleanupProvider(
                model: model, apiKey: key, serviceURL: url, apiStyle: apiStyle,
                localServerApp: app, localTuning: { tuning },
                loadLocalContext: { endpoint, model, context in
                    await LocalServerClient(session: session).loadWithContext(
                        endpoint, modelID: model, contextTokens: context, apiKey: key)
                },
                session: session)
        case .microsoftFoundry(let base, let deployment, let identity):
            let credential: any AzureCredentialProvider
            switch identity {
            case .azureCli(let tenant):
                credential = AzureCliCredentialProvider(
                    tenantId: tenant, searchPath: factory.azureCliSearchPath, lane: factory.azureCliLane,
                    launch: factory.azureCliLaunch, now: factory.now)
            case .servicePrincipal(let tenant, let client, _):
                let input: CleanupCredentialInput = source == .environment ? .saved : clientSecret
                let secret = try input.resolve { try store.readAzureClientSecret(clientId: client) }
                guard let secret, !secret.isEmpty else {
                    throw CleanupProviderError.notConfigured(.azureClientSecretMissing, source: source)
                }
                credential = AzureServicePrincipalCredentialProvider(
                    principal: AzureServicePrincipal(tenantId: tenant, clientId: client, clientSecret: secret),
                    session: factory.session, now: factory.now)
            }
            let caching = settings.azurePromptCaching
            return MicrosoftFoundryCleanupProvider(
                inferenceBase: base, deployment: deployment, promptCachingEnabled: { caching },
                credential: credential, session: factory.session)
        }
    }
}
