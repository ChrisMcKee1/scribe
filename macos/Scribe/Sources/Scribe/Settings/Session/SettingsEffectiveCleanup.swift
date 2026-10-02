import Foundation

enum SettingsCleanupPurpose: Equatable, Sendable {
    case serving
    case candidate
}

enum SettingsCleanupSource: String, Sendable {
    case savedSettings
    case environment
    case draftCandidate
}

struct SettingsEffectiveCleanup: Equatable, Sendable {
    let snapshot: CleanupSettingsSnapshot
    let source: SettingsCleanupSource
    let environmentOverrides: [String]
    let purpose: SettingsCleanupPurpose

    var explanation: String? {
        if source == .environment && purpose == .candidate {
            return "This test uses AI cleanup set outside Settings, not the unsaved details shown here."
        }
        if source == .environment {
            return "AI cleanup is set outside Settings."
        }
        return nil
    }

    /// UI projection only. The candidate/serving factory uses the resolver's effective connection as its authority.
    static func resolve(
        snapshot saved: CleanupSettingsSnapshot,
        environment: [String: String],
        purpose: SettingsCleanupPurpose
    ) -> SettingsEffectiveCleanup {
        guard let provider = environment["SCRIBE_CLEANUP_PROVIDER"] else {
            return SettingsEffectiveCleanup(
                snapshot: saved,
                source: purpose == .candidate ? .draftCandidate : .savedSettings,
                environmentOverrides: [],
                purpose: purpose)
        }
        var snapshot = saved
        var keys = ["SCRIBE_CLEANUP_PROVIDER"]
        let relevant: [String]
        switch provider {
        case "ollama":
            snapshot.providerKind = .ollama
            snapshot.ollamaModel = environment["SCRIBE_OLLAMA_MODEL"] ?? CleanupSettingsStore.defaultOllamaModel
            relevant = ["SCRIBE_OLLAMA_MODEL"]
        case "openai-compatible":
            snapshot.providerKind = .openAICompatible
            snapshot.openAIBaseURL = environment["SCRIBE_CLEANUP_BASE_URL"] ?? ""
            snapshot.openAIModel = environment["SCRIBE_CLEANUP_MODEL"] ?? ""
            snapshot.openAIApiStyle = .chatCompletions
            relevant = ["SCRIBE_CLEANUP_BASE_URL", "SCRIBE_CLEANUP_MODEL", "SCRIBE_CLEANUP_API_KEY"]
        case "microsoft-foundry":
            snapshot.providerKind = .microsoftFoundry
            snapshot.azureEndpoint = environment["SCRIBE_AZURE_FOUNDRY_ENDPOINT"] ?? ""
            snapshot.azureDeployment = environment["SCRIBE_AZURE_FOUNDRY_DEPLOYMENT"] ?? ""
            snapshot.azureAuthMode =
                environment["SCRIBE_AZURE_AUTH_MODE"] == "service-principal" ? .servicePrincipal : .azureCli
            snapshot.azureTenantId = environment["SCRIBE_AZURE_TENANT_ID"] ?? ""
            snapshot.azureClientId = environment["SCRIBE_AZURE_CLIENT_ID"] ?? ""
            relevant = [
                "SCRIBE_AZURE_FOUNDRY_ENDPOINT", "SCRIBE_AZURE_FOUNDRY_DEPLOYMENT", "SCRIBE_AZURE_AUTH_MODE",
                "SCRIBE_AZURE_TENANT_ID", "SCRIBE_AZURE_CLIENT_ID",
            ]
        default:
            snapshot.providerKind = .foundryLocal
            snapshot.foundryLocalModelAlias =
                environment["SCRIBE_FOUNDRY_CLEANUP_MODEL"] ?? CleanupSettingsStore.defaultFoundryLocalModelAlias
            relevant = ["SCRIBE_FOUNDRY_CLEANUP_MODEL"]
        }
        keys += relevant.filter { environment[$0] != nil }
        return SettingsEffectiveCleanup(
            snapshot: snapshot,
            source: .environment,
            environmentOverrides: keys,
            purpose: purpose)
    }
}
