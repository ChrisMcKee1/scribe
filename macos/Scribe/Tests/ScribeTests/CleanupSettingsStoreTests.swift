import Security
import XCTest

@testable import Scribe

/// `CleanupSettingsStore` over a defaults suite and secret stores of each test's own: nothing here reads, replaces or
/// deletes the developer's AI cleanup settings or Keychain items, and the suites can run in parallel worker processes.
final class CleanupSettingsStoreTests: XCTestCase {
    func testDefaultsWhenNothingIsSaved() {
        let store = makeCleanupStore().store

        XCTAssertFalse(store.isEnabled)
        XCTAssertEqual(store.providerKind, .foundryLocal)
        XCTAssertEqual(store.foundryLocalModelAlias, "qwen2.5-1.5b")
        XCTAssertEqual(store.ollamaModel, "qwen2.5:3b")
        XCTAssertEqual(store.openAIBaseURL, "")
        XCTAssertEqual(store.openAIModel, "")
        XCTAssertEqual(store.azureEndpoint, "")
        XCTAssertEqual(store.azureDeployment, "")
        XCTAssertEqual(store.azureAuthMode, .azureCli)
        XCTAssertEqual(store.azureTenantId, "")
        XCTAssertEqual(store.azureClientId, "")
        XCTAssertEqual(store.secretRevision, "")
        XCTAssertNil(store.openAIApiKey())
        XCTAssertEqual(CleanupProviderKind.foundryLocal.displayName, "Foundry Local (recommended)")
    }

    func testEveryFieldRoundTripsThroughItsOwnSuiteAndNoOther() {
        let fixture = makeCleanupStore()
        let store = fixture.store
        let endpoint = "https://settings-probe-\(UUID().uuidString).example.com"

        store.isEnabled = true
        store.providerKind = .microsoftFoundry
        store.foundryLocalModelAlias = "qwen2.5-3b"
        store.ollamaModel = "llama3.2:1b"
        store.openAIBaseURL = "http://localhost:1234"
        store.openAIModel = "local-model"
        store.azureEndpoint = endpoint
        store.azureDeployment = "gpt-5-mini"
        store.azureAuthMode = .servicePrincipal
        store.azureTenantId = "11111111-1111-1111-1111-111111111111"
        store.azureClientId = "client-1"

        XCTAssertEqual(
            store.snapshot(),
            CleanupSettingsSnapshot(
                isEnabled: true, providerKind: .microsoftFoundry, foundryLocalModelAlias: "qwen2.5-3b",
                ollamaModel: "llama3.2:1b", openAIBaseURL: "http://localhost:1234", openAIModel: "local-model",
                azureEndpoint: endpoint, azureDeployment: "gpt-5-mini", azureAuthMode: .servicePrincipal,
                azureTenantId: "11111111-1111-1111-1111-111111111111", azureClientId: "client-1", secretRevision: ""))
        XCTAssertEqual(fixture.defaults.string(forKey: "ScribeCleanupAzureEndpoint"), endpoint)
        XCTAssertTrue(fixture.defaults.bool(forKey: "ScribeAiCleanupEnabled"))
        XCTAssertNotEqual(UserDefaults.standard.string(forKey: "ScribeCleanupAzureEndpoint"), endpoint)
    }

    /// Secrets go to the secret store and nowhere else: not one of them is written to the defaults.
    func testTheAPIKeyGoesToTheSecretStoreAndClearsOnNilOrEmpty() throws {
        let fixture = makeCleanupStore()

        try fixture.store.setOpenAIApiKey("sk-test-key")
        XCTAssertEqual(fixture.store.openAIApiKey(), "sk-test-key")
        XCTAssertEqual(fixture.apiKeys.secrets, [CleanupSettingsStore.openAIApiKeyAccount: "sk-test-key"])
        let stored = fixture.defaults.dictionaryRepresentation().values.compactMap { $0 as? String }
        XCTAssertFalse(stored.contains("sk-test-key"))

        try fixture.store.setOpenAIApiKey(nil)
        XCTAssertNil(fixture.store.openAIApiKey())
        try fixture.store.setOpenAIApiKey("sk-test-key")
        try fixture.store.setOpenAIApiKey("")
        XCTAssertNil(fixture.store.openAIApiKey())
        XCTAssertEqual(fixture.apiKeys.secrets, [:])
    }

    /// Keyed by client id, so switching app registrations never reads a stale secret, and trimmed, so an id pasted
    /// with a stray space still finds its own.
    func testAClientSecretIsKeyedByItsTrimmedClientId() throws {
        let fixture = makeCleanupStore()

        try fixture.store.setAzureClientSecret("secret-a", clientId: "client-a")
        try fixture.store.setAzureClientSecret("secret-b", clientId: " client-b ")

        XCTAssertEqual(fixture.store.azureClientSecret(clientId: "client-a"), "secret-a")
        XCTAssertEqual(fixture.store.azureClientSecret(clientId: "client-b"), "secret-b")
        XCTAssertEqual(fixture.clientSecrets.secrets, ["client-a": "secret-a", "client-b": "secret-b"])

        try fixture.store.setAzureClientSecret(nil, clientId: "client-a")
        XCTAssertNil(fixture.store.azureClientSecret(clientId: "client-a"))
    }

    /// No item is ever keyed by an empty account, which every unconfigured install would share.
    func testABlankClientIdStoresNoSecret() throws {
        let fixture = makeCleanupStore()

        try fixture.store.setAzureClientSecret("orphaned-secret", clientId: "  ")

        XCTAssertNil(fixture.store.azureClientSecret(clientId: ""))
        XCTAssertEqual(fixture.clientSecrets.writes, 0)
        XCTAssertEqual(fixture.store.secretRevision, "")
    }

    /// The revision is what tells the provider cache a secret changed, without the secret becoming part of a key.
    func testEverySecretChangeMovesTheRevisionAndNothingElseDoes() throws {
        let fixture = makeCleanupStore()
        let store = fixture.store
        var revisions: [String] = [store.secretRevision]

        store.azureEndpoint = "https://my-res.openai.azure.com"
        store.openAIModel = "local-model"
        XCTAssertEqual(store.secretRevision, revisions.last)

        try store.setOpenAIApiKey("sk-test")
        revisions.append(store.secretRevision)
        try store.setOpenAIApiKey(nil)
        revisions.append(store.secretRevision)
        try store.setAzureClientSecret("secret-1", clientId: "client-1")
        revisions.append(store.secretRevision)
        try store.setAzureClientSecret(nil, clientId: "client-1")
        revisions.append(store.secretRevision)

        XCTAssertEqual(Set(revisions).count, revisions.count, "\(revisions)")
    }

    func testAFailedSecretWriteKeepsTheRevisionAndSaysWhy() throws {
        let fixture = makeCleanupStore()
        try fixture.store.setOpenAIApiKey("sk-old")
        let before = fixture.store.secretRevision
        fixture.apiKeys.failNextWrite(with: errSecInteractionNotAllowed)

        XCTAssertThrowsError(try fixture.store.setOpenAIApiKey("sk-new")) {
            XCTAssertEqual($0 as? KeychainStore.KeychainError, .unhandled(errSecInteractionNotAllowed))
        }
        XCTAssertEqual(fixture.store.secretRevision, before)
        XCTAssertEqual(fixture.store.openAIApiKey(), "sk-old")
    }

    /// Settings shows a Keychain that cannot be read as no key; the resolver is told the difference.
    func testAnUnreadableKeyReadsAsNoKeyInSettingsButThrowsForTheResolver() {
        let fixture = makeCleanupStore(apiKeys: InMemorySecretStore([CleanupSettingsStore.openAIApiKeyAccount: "sk"]))

        fixture.apiKeys.failNextRead(with: errSecInteractionNotAllowed)
        XCTAssertNil(fixture.store.openAIApiKey())
        fixture.apiKeys.failNextRead(with: errSecInteractionNotAllowed)
        XCTAssertThrowsError(try fixture.store.readOpenAIApiKey())
    }

    func testIsConfiguredNeedsTheFieldsThatCannotBeGuessed() {
        let store = makeCleanupStore().store

        XCTAssertTrue(store.isConfigured(for: .foundryLocal))
        XCTAssertTrue(store.isConfigured(for: .ollama))
        XCTAssertFalse(store.isConfigured(for: .openAICompatible))
        XCTAssertFalse(store.isConfigured(for: .microsoftFoundry))

        store.openAIBaseURL = "http://localhost:1234"
        store.openAIModel = "  "
        XCTAssertFalse(store.isConfigured(for: .openAICompatible))
        store.openAIModel = "local-model"
        XCTAssertTrue(store.isConfigured(for: .openAICompatible))

        store.azureEndpoint = "https://my-res.openai.azure.com"
        XCTAssertFalse(store.isConfigured(for: .microsoftFoundry))
        store.azureDeployment = "gpt-5-mini"
        XCTAssertTrue(store.isConfigured(for: .microsoftFoundry))
    }

    /// The only test that builds the live store. It reads and writes neither the production Keychain services nor
    /// `UserDefaults.standard`; it only checks which of them the live store names.
    func testTheLiveStoreUsesTheProductionServicesAndStandardDefaults() {
        let live = CleanupSettingsStore.live

        XCTAssertEqual(live.domain, .standard)
        XCTAssertEqual((live.apiKeys as? KeychainSecretStore)?.service, "com.scribe.macos.openai-compatible-api-key")
        XCTAssertEqual((live.clientSecrets as? KeychainSecretStore)?.service, "com.scribe.macos.azure-client-secret")
    }

    /// The AI Cleanup tab's own adapter, over a store of this test's: it reads and writes that store only, and its
    /// Test Connection goes through the provider cache.
    @MainActor
    func testTheSettingsTabAdapterUsesOnlyTheStoreItIsGiven() async throws {
        let fixture = makeCleanupStore()
        let session = makeStubSession { request in StubReply.completion(request, "Ok.") }
        let cache = CleanupProviderCache(store: fixture.store, environment: [:], factory: .testing(session: session))
        let access = CleanupSettingsAccess.backed(by: fixture.store, providers: cache)

        var values = access.load()
        let old = values
        values.isEnabled = true
        values.providerKind = .openAICompatible
        values.openAIBaseURL = "http://127.0.0.1:1234"
        values.openAIModel = "local-model"
        access.save(values, old)
        try access.setOpenAIApiKey("sk-tab")

        XCTAssertEqual(access.load(), values)
        XCTAssertTrue(fixture.store.isEnabled)
        XCTAssertTrue(access.isConfigured(.openAICompatible))
        XCTAssertTrue(access.hasOpenAIApiKey())
        XCTAssertEqual(fixture.apiKeys.secrets, [CleanupSettingsStore.openAIApiKeyAccount: "sk-tab"])
        let check = await access.checkConnection()
        XCTAssertTrue(check.reachable, check.message)
    }
}
