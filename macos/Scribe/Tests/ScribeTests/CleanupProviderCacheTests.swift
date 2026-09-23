import Security
import XCTest
import os

@testable import Scribe

final class CleanupProviderCacheTests: XCTestCase {
    private struct Rig {
        let fixture: CleanupStoreFixture
        let cache: CleanupProviderCache
        let requests: RequestLog
        let azureCli: FakeAzureCli
        let foundryStatus: FakeFoundryStatus
        let clock: TestClock

        var store: CleanupSettingsStore { fixture.store }
    }

    private static let entraHost = "login.microsoftonline.com"

    /// A cache over a store of this test's own, whose requests, `az` launches and `foundry status` lookups the test
    /// sees. Entra token requests get a token; everything else gets `reply`.
    private func makeRig(
        environment: [String: String] = [:],
        apiKeys: InMemorySecretStore = InMemorySecretStore(),
        clientSecrets: InMemorySecretStore = InMemorySecretStore(),
        reply: @escaping @Sendable (URLRequest) -> (HTTPURLResponse, Data) = { StubReply.completion($0, "Cleaned.") }
    ) throws -> Rig {
        let requests = RequestLog()
        let entraHost = Self.entraHost
        let session = makeStubSession { request in
            requests.record(request)
            if request.url?.host(percentEncoded: false) == entraHost {
                return StubReply.entraToken(request, "entra-token-\(requests.count(host: entraHost))")
            }
            return reply(request)
        }
        let clock = TestClock()
        let azureCli = FakeAzureCli(outcomes: [
            .azToken("cli-token", expiresOn: Int(clock.date.timeIntervalSince1970) + 3600)
        ])
        let foundryStatus = FakeFoundryStatus(endpoints: ["http://127.0.0.1:5001"])
        let azDirectory = try makeScript(named: "az", body: "exit 1").deletingLastPathComponent()
        let fixture = makeCleanupStore(apiKeys: apiKeys, clientSecrets: clientSecrets)
        let factory = CleanupProviderFactory.testing(
            session: session, foundryStatus: foundryStatus.source, azureCli: azureCli,
            azureCliSearchPath: [azDirectory.path(percentEncoded: false)], clock: clock)
        return Rig(
            fixture: fixture,
            cache: CleanupProviderCache(store: fixture.store, environment: environment, factory: factory),
            requests: requests, azureCli: azureCli, foundryStatus: foundryStatus, clock: clock)
    }

    private func configureMicrosoftFoundry(_ store: CleanupSettingsStore, deployment: String = "gpt-5-mini") {
        store.providerKind = .microsoftFoundry
        store.azureEndpoint = "https://my-res.services.ai.azure.com/api/projects/my-project"
        store.azureDeployment = deployment
        store.azureAuthMode = .azureCli
    }

    private func configureOpenAICompatible(_ store: CleanupSettingsStore) {
        store.providerKind = .openAICompatible
        store.openAIBaseURL = "http://127.0.0.1:1234/v1"
        store.openAIModel = "local-model"
    }

    private func clean(_ rig: Rig) async throws {
        _ = try await rig.cache.provider().clean(CleanupRequest(transcript: "raw text"))
    }

    private func same(_ first: any CleanupProvider, _ second: any CleanupProvider) -> Bool {
        (first as AnyObject) === (second as AnyObject)
    }

    // MARK: - Reuse

    func testTheSameConfigurationGetsTheSameProvider() throws {
        let rig = try makeRig()

        let first = try rig.cache.provider()
        let second = try rig.cache.provider()

        XCTAssertTrue(same(first, second))
        XCTAssertEqual(first.id, "foundry-local")
    }

    /// The prompt travels with each request, and the on switch and the other preferences are not part of a
    /// connection, so none of them rebuilds the provider.
    func testWhatIsNotPartOfTheConfigurationLeavesTheProviderAlone() throws {
        let rig = try makeRig()
        let first = try rig.cache.provider()

        rig.store.isEnabled = true
        rig.store.ollamaModel = "llama3.2:1b"
        rig.fixture.defaults.set("unrelated", forKey: "ScribeSomeOtherPreference")

        XCTAssertTrue(same(first, try rig.cache.provider()))
    }

    func testAChangedModelBuildsANewProvider() throws {
        let rig = try makeRig()
        let first = try rig.cache.provider()

        rig.store.foundryLocalModelAlias = "phi-3.5-mini"
        let second = try rig.cache.provider()
        rig.store.foundryLocalModelAlias = "qwen2.5-1.5b"
        let third = try rig.cache.provider()

        XCTAssertFalse(same(first, second))
        XCTAssertFalse(same(second, third))
        XCTAssertTrue(same(third, try rig.cache.provider()))
    }

    func testConcurrentCallersShareOneProvider() throws {
        let rig = try makeRig()
        let seen = OSAllocatedUnfairLock<Set<ObjectIdentifier>>(initialState: [])
        let cache = rig.cache

        DispatchQueue.concurrentPerform(iterations: 16) { _ in
            guard let provider = try? cache.provider() else { return }
            let identity = ObjectIdentifier(provider as AnyObject)
            seen.withLock { _ = $0.insert(identity) }
        }

        XCTAssertEqual(seen.withLock { $0.count }, 1)
    }

    // MARK: - What the reuse saves

    func testFoundryLocalsEndpointIsLookedUpOnceAcrossDictations() async throws {
        let rig = try makeRig()

        for _ in 0..<3 {
            try await clean(rig)
        }

        XCTAssertEqual(rig.foundryStatus.lookups, 1)
        XCTAssertEqual(rig.requests.count, 3)
    }

    /// One `az` launch serves every dictation for an identity, and a new deployment keeps the identity's credential.
    func testAzureCliRunsOncePerIdentityAcrossDictationsAndDeployments() async throws {
        let rig = try makeRig()
        configureMicrosoftFoundry(rig.store, deployment: "deployment-a")

        try await clean(rig)
        let first = try rig.cache.provider()
        try await clean(rig)
        rig.store.azureDeployment = "deployment-b"
        let second = try rig.cache.provider()
        try await clean(rig)

        XCTAssertFalse(same(first, second))
        XCTAssertEqual(rig.azureCli.launches, 1)
        XCTAssertEqual(rig.requests.all.map { $0.jsonBody["model"] as? String }, ["deployment-a", "deployment-a", "deployment-b"])
        XCTAssertEqual(rig.requests.all.map { $0.header("Authorization") }, Array(repeating: "Bearer cli-token", count: 3))
    }

    func testANewTenantGetsANewCredential() async throws {
        let rig = try makeRig()
        configureMicrosoftFoundry(rig.store)

        try await clean(rig)
        rig.store.azureTenantId = "contoso.onmicrosoft.com"
        try await clean(rig)

        XCTAssertEqual(rig.azureCli.launches, 2)
        XCTAssertEqual(rig.azureCli.commands.last.map { Array($0.arguments.suffix(2)) }, ["--tenant", "contoso.onmicrosoft.com"])
    }

    func testInvalidateDropsTheProviderAndItsToken() async throws {
        let rig = try makeRig()
        configureMicrosoftFoundry(rig.store)
        try await clean(rig)
        let before = try rig.cache.provider()

        rig.cache.invalidate()
        let after = try rig.cache.provider()
        try await clean(rig)

        XCTAssertFalse(same(before, after))
        XCTAssertEqual(rig.azureCli.launches, 2)
    }

    /// The Keychain is read when a provider is built, not on every dictation.
    func testTheAPIKeyIsReadOncePerSecretRevision() async throws {
        let rig = try makeRig(apiKeys: InMemorySecretStore([CleanupSettingsStore.openAIApiKeyAccount: "sk-old"]))
        configureOpenAICompatible(rig.store)

        for _ in 0..<3 {
            try await clean(rig)
        }
        XCTAssertEqual(rig.fixture.apiKeys.reads, 1)

        try rig.store.setOpenAIApiKey("sk-new")
        try await clean(rig)

        XCTAssertEqual(rig.fixture.apiKeys.reads, 2)
        XCTAssertEqual(
            rig.requests.all.map { $0.header("Authorization") },
            ["Bearer sk-old", "Bearer sk-old", "Bearer sk-old", "Bearer sk-new"])
    }

    func testANewClientSecretBuildsANewCredential() async throws {
        let rig = try makeRig()
        configureMicrosoftFoundry(rig.store)
        rig.store.azureAuthMode = .servicePrincipal
        rig.store.azureTenantId = "tenant-1"
        rig.store.azureClientId = "client-1"
        try rig.store.setAzureClientSecret("secret-old", clientId: "client-1")

        try await clean(rig)
        try await clean(rig)
        try rig.store.setAzureClientSecret("secret-new", clientId: "client-1")
        try await clean(rig)

        let tokenRequests = rig.requests.all.filter { $0.host == Self.entraHost }
        XCTAssertEqual(tokenRequests.map { FormDecoding.fields($0.body)["client_secret"] }, ["secret-old", "secret-new"])
        XCTAssertEqual(
            rig.requests.all.filter { $0.host != Self.entraHost }.map { $0.header("Authorization") },
            ["Bearer entra-token-1", "Bearer entra-token-1", "Bearer entra-token-2"])
        XCTAssertEqual(rig.fixture.clientSecrets.reads, 2)
    }

    func testASecretChangeLeavesAProviderWithoutSecretsAlone() throws {
        let rig = try makeRig()
        let first = try rig.cache.provider()

        try rig.store.setOpenAIApiKey("sk-unrelated")

        XCTAssertTrue(same(first, try rig.cache.provider()))
    }

    // MARK: - Configurations that cannot be used

    func testAnIncompleteConfigurationThrowsAndIsNotCached() throws {
        let rig = try makeRig()
        rig.store.providerKind = .openAICompatible

        XCTAssertThrowsError(try rig.cache.provider()) {
            XCTAssertEqual($0 as? CleanupProviderError, .notConfigured(.openAIEndpointMissing, source: .settings))
        }
        configureOpenAICompatible(rig.store)

        XCTAssertEqual(try rig.cache.provider().id, "openai-compatible")
    }

    func testAMissingClientSecretIsFoundWhenTheProviderIsBuilt() throws {
        let rig = try makeRig()
        configureMicrosoftFoundry(rig.store)
        rig.store.azureAuthMode = .servicePrincipal
        rig.store.azureTenantId = "tenant-1"
        rig.store.azureClientId = "client-1"

        for _ in 0..<2 {
            XCTAssertThrowsError(try rig.cache.provider()) {
                XCTAssertEqual($0 as? CleanupProviderError, .notConfigured(.azureClientSecretMissing, source: .settings))
            }
        }
        XCTAssertEqual(rig.fixture.clientSecrets.reads, 2)
    }

    /// A locked Keychain is not a key that was never saved: the message says which.
    func testAKeychainThatCannotBeReadIsNotMistakenForAMissingKey() throws {
        let apiKeys = InMemorySecretStore()
        apiKeys.failNextRead(with: errSecInteractionNotAllowed)
        let rig = try makeRig(apiKeys: apiKeys)
        configureOpenAICompatible(rig.store)

        XCTAssertThrowsError(try rig.cache.provider()) {
            XCTAssertEqual($0 as? CleanupProviderError, .secretUnavailable(.unhandled(errSecInteractionNotAllowed)))
        }
        XCTAssertEqual(try rig.cache.provider().id, "openai-compatible")
    }

    func testTheEnvironmentOverridesSettings() throws {
        let rig = try makeRig(environment: ["SCRIBE_CLEANUP_PROVIDER": "ollama", "SCRIBE_OLLAMA_MODEL": "llama3.2:1b"])
        configureMicrosoftFoundry(rig.store)

        let provider = try rig.cache.provider()

        XCTAssertEqual(provider.id, "managed-ollama")
        XCTAssertEqual((provider as? ManagedOllamaCleanupProvider)?.model, "llama3.2:1b")
    }

    // MARK: - Test Connection

    /// Test Connection runs one real cleanup of a one-word transcript through the provider dictation uses, so a model
    /// that cannot clean fails here rather than passing a model list.
    func testTestConnectionRunsOneRealCleanupThroughTheSameProvider() async throws {
        let rig = try makeRig(apiKeys: InMemorySecretStore([CleanupSettingsStore.openAIApiKeyAccount: "sk-test"]))
        configureOpenAICompatible(rig.store)

        let check = await rig.cache.checkConnection()
        try await clean(rig)

        XCTAssertTrue(check.reachable, check.message)
        XCTAssertTrue(check.message.hasPrefix("OpenAI-compatible endpoint cleaned a test phrase in "), check.message)
        let probe = try XCTUnwrap(rig.requests.all.first)
        XCTAssertEqual(probe.url?.absoluteString, "http://127.0.0.1:1234/v1/chat/completions")
        XCTAssertEqual(
            probe.messageContents,
            [
                CleanupPrompt.systemPrompt(writingStyle: CleanupPrompt.defaultWritingStyle, useLocalPrompt: false),
                "<transcript>\nok\n</transcript>",
            ])
        XCTAssertEqual(rig.requests.count, 2)
        XCTAssertEqual(rig.fixture.apiKeys.reads, 1, "the dictation reused the provider Test Connection built")
    }

    func testTestConnectionReportsADeploymentThatCannotClean() async throws {
        let rig = try makeRig { request in
            StubReply.json(
                request, status: 404,
                #"{"error":{"code":"DeploymentNotFound","message":"The API deployment for this resource does not exist."}}"#)
        }
        configureMicrosoftFoundry(rig.store)

        let check = await rig.cache.checkConnection()

        XCTAssertFalse(check.reachable)
        XCTAssertTrue(
            check.message.hasPrefix("Microsoft Foundry: Microsoft Foundry could not find the deployment (404)."),
            check.message)
        XCTAssertTrue(
            check.message.hasSuffix("The endpoint said: The API deployment for this resource does not exist."),
            check.message)
    }

    func testTestConnectionReportsAnIncompleteSetup() async throws {
        let rig = try makeRig()
        rig.store.providerKind = .openAICompatible

        let check = await rig.cache.checkConnection()

        XCTAssertFalse(check.reachable)
        XCTAssertEqual(check.message, CleanupConfigurationProblem.openAIEndpointMissing.message(for: .settings))
        XCTAssertEqual(rig.requests.count, 0)
    }

    func testTestConnectionGivesOnDeviceModelsTimeToLoad() {
        XCTAssertEqual(CleanupProviderCache.checkTimeout(for: .foundryLocal), 180)
        XCTAssertEqual(CleanupProviderCache.checkTimeout(for: .ollama), 180)
        XCTAssertEqual(CleanupProviderCache.checkTimeout(for: .openAICompatible), 90)
        XCTAssertEqual(CleanupProviderCache.checkTimeout(for: .microsoftFoundry), 90)
    }
}
