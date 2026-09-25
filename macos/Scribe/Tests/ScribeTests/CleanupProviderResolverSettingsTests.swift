import XCTest

@testable import Scribe

/// `CleanupProviderResolver` turns what Settings or the `SCRIBE_*` environment variables hold into a validated
/// connection and a provider. Every case runs against a store of its own and an environment it passes in, so nothing
/// here depends on the developer's preferences or on how the test process was launched.
final class CleanupProviderResolverSettingsTests: XCTestCase {
    private func connection(_ store: CleanupSettingsStore, environment: [String: String] = [:]) throws
        -> CleanupConnection
    {
        try CleanupProviderResolver.connection(store: store, environment: environment)
    }

    private func assertNotConfigured(
        _ store: CleanupSettingsStore,
        environment: [String: String] = [:],
        _ problem: CleanupConfigurationProblem,
        source: CleanupConfigurationSource = .settings,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        XCTAssertThrowsError(try connection(store, environment: environment), file: file, line: line) {
            XCTAssertEqual(
                $0 as? CleanupProviderError, .notConfigured(problem, source: source), file: file, line: line)
        }
    }

    private func makeProvider(
        _ store: CleanupSettingsStore, environment: [String: String] = [:]
    ) throws -> any CleanupProvider {
        try CleanupProviderResolver.tryResolveDefaultProvider(
            store: store, environment: environment,
            factory: .testing(session: makeStubSession { request in StubReply.completion(request, "Cleaned.") }))
    }

    // MARK: - Settings

    func testDefaultSettingsResolveToFoundryLocal() throws {
        let fixture = makeCleanupStore()

        XCTAssertEqual(try connection(fixture.store).target, .foundryLocal(modelAlias: "qwen2.5-1.5b"))
        let provider = try makeProvider(fixture.store)
        XCTAssertEqual(provider.id, "foundry-local")
        XCTAssertEqual(provider.displayName, "Foundry Local")
    }

    func testABlankModelIsNotConfigured() {
        let fixture = makeCleanupStore()
        fixture.store.foundryLocalModelAlias = "  "
        assertNotConfigured(fixture.store, .foundryLocalModelMissing)

        fixture.store.providerKind = .ollama
        fixture.store.ollamaModel = ""
        assertNotConfigured(fixture.store, .ollamaModelMissing)
    }

    func testOllamaResolvesToTheManagedProvider() throws {
        let fixture = makeCleanupStore()
        fixture.store.providerKind = .ollama
        fixture.store.ollamaModel = " qwen2.5:3b "

        XCTAssertEqual(try connection(fixture.store).target, .ollama(model: "qwen2.5:3b"))
        XCTAssertEqual(try makeProvider(fixture.store).id, "managed-ollama")
    }

    func testOpenAICompatibleNeedsAnHTTPBaseURLAndAModel() {
        let fixture = makeCleanupStore()
        fixture.store.providerKind = .openAICompatible
        assertNotConfigured(fixture.store, .openAIEndpointMissing)

        fixture.store.openAIBaseURL = "localhost:1234"
        fixture.store.openAIModel = "local-model"
        assertNotConfigured(fixture.store, .openAIEndpointInvalid)

        fixture.store.openAIBaseURL = "http://localhost:1234"
        fixture.store.openAIModel = " "
        assertNotConfigured(fixture.store, .openAIModelMissing)
    }

    func testOpenAICompatibleResolvesWithItsCompletionsURLAndTheSecretRevision() throws {
        let fixture = makeCleanupStore()
        fixture.store.providerKind = .openAICompatible
        fixture.store.openAIBaseURL = "https://openrouter.ai/api/v1"
        fixture.store.openAIModel = "some/model"
        try fixture.store.setOpenAIApiKey("sk-test")

        XCTAssertEqual(
            try connection(fixture.store).target,
            .openAICompatible(
                completionsURL: URL(string: "https://openrouter.ai/api/v1/chat/completions")!, model: "some/model",
                apiKey: .secretStore(revision: fixture.store.secretRevision)))
        let provider = try makeProvider(fixture.store)
        XCTAssertEqual(provider.id, "openai-compatible")
        XCTAssertEqual(provider.displayName, "OpenAI-compatible endpoint")
    }

    func testMicrosoftFoundryNeedsAnEndpointAndADeployment() {
        let fixture = makeCleanupStore()
        fixture.store.providerKind = .microsoftFoundry
        assertNotConfigured(fixture.store, .azureEndpointMissing)

        fixture.store.azureEndpoint = "my-res.openai.azure.com"
        fixture.store.azureDeployment = "gpt-5-mini"
        assertNotConfigured(fixture.store, .azureEndpointInvalid)

        fixture.store.azureEndpoint = "http://my-res.openai.azure.com"
        assertNotConfigured(fixture.store, .azureEndpointInvalid)

        fixture.store.azureEndpoint = "https://my-res.openai.azure.com"
        fixture.store.azureDeployment = ""
        assertNotConfigured(fixture.store, .azureDeploymentMissing)
    }

    /// A project URL and the account URL name one account, so they are one connection and share one provider.
    func testEverySavedShapeOfOneAccountIsOneConnection() throws {
        let fixture = makeCleanupStore()
        fixture.store.providerKind = .microsoftFoundry
        fixture.store.azureDeployment = "gpt-5-mini"

        fixture.store.azureEndpoint = "https://my-res.services.ai.azure.com/api/projects/my-project"
        let fromProject = try connection(fixture.store)
        fixture.store.azureEndpoint = "https://my-res.services.ai.azure.com/"
        let fromAccount = try connection(fixture.store)

        XCTAssertEqual(fromProject, fromAccount)
        XCTAssertEqual(
            fromProject.target,
            .microsoftFoundry(
                inferenceBase: URL(string: "https://my-res.services.ai.azure.com/openai/v1/")!,
                deployment: "gpt-5-mini", identity: .azureCli(tenantId: nil)))
    }

    func testAzureCliAuthTakesAnOptionalTenant() throws {
        let fixture = makeCleanupStore()
        fixture.store.providerKind = .microsoftFoundry
        fixture.store.azureEndpoint = "https://my-res.openai.azure.com"
        fixture.store.azureDeployment = "gpt-5-mini"
        fixture.store.azureTenantId = " contoso.onmicrosoft.com "

        guard case .microsoftFoundry(_, _, let identity) = try connection(fixture.store).target else {
            return XCTFail("Expected a Microsoft Foundry connection")
        }
        XCTAssertEqual(identity, .azureCli(tenantId: "contoso.onmicrosoft.com"))
        XCTAssertEqual(try makeProvider(fixture.store).id, "microsoft-foundry")
    }

    /// The tenant goes into a URL path or an `az` argument, so one that could change either is refused.
    func testAnInvalidTenantIsNotConfigured() {
        let fixture = makeCleanupStore()
        fixture.store.providerKind = .microsoftFoundry
        fixture.store.azureEndpoint = "https://my-res.openai.azure.com"
        fixture.store.azureDeployment = "gpt-5-mini"

        for tenant in ["tenant/../other", "--allow-no-subscriptions"] {
            fixture.store.azureTenantId = tenant
            assertNotConfigured(fixture.store, .azureTenantInvalid)
        }
    }

    func testServicePrincipalNeedsAClientIdAndATenant() {
        let fixture = makeCleanupStore()
        fixture.store.providerKind = .microsoftFoundry
        fixture.store.azureEndpoint = "https://my-res.openai.azure.com"
        fixture.store.azureDeployment = "gpt-5-mini"
        fixture.store.azureAuthMode = .servicePrincipal
        fixture.store.azureTenantId = "tenant-1"
        assertNotConfigured(fixture.store, .azureClientIdMissing)

        fixture.store.azureClientId = "client-1"
        fixture.store.azureTenantId = ""
        assertNotConfigured(fixture.store, .azureTenantMissing)
    }

    /// The secret is read when the provider is built, so a connection needs none; building one without it fails.
    func testServicePrincipalWithoutASavedSecretFailsWhenBuilt() throws {
        let fixture = makeCleanupStore()
        fixture.store.providerKind = .microsoftFoundry
        fixture.store.azureEndpoint = "https://my-res.openai.azure.com"
        fixture.store.azureDeployment = "gpt-5-mini"
        fixture.store.azureAuthMode = .servicePrincipal
        fixture.store.azureTenantId = "tenant-1"
        fixture.store.azureClientId = "client-1"

        XCTAssertNoThrow(try connection(fixture.store))
        XCTAssertThrowsError(try makeProvider(fixture.store)) {
            XCTAssertEqual($0 as? CleanupProviderError, .notConfigured(.azureClientSecretMissing, source: .settings))
        }

        try fixture.store.setAzureClientSecret("secret-1", clientId: "client-1")
        guard case .microsoftFoundry(_, _, let identity) = try connection(fixture.store).target else {
            return XCTFail("Expected a Microsoft Foundry connection")
        }
        XCTAssertEqual(
            identity,
            .servicePrincipal(tenantId: "tenant-1", clientId: "client-1", secretRevision: fixture.store.secretRevision))
        XCTAssertEqual(try makeProvider(fixture.store).id, "microsoft-foundry")
    }

    // MARK: - Environment

    func testTheEnvironmentTakesPriorityOverSettings() throws {
        let fixture = makeCleanupStore()
        fixture.store.providerKind = .microsoftFoundry

        let resolved = try connection(
            fixture.store, environment: ["SCRIBE_CLEANUP_PROVIDER": "ollama", "SCRIBE_OLLAMA_MODEL": "llama3.2:1b"])

        XCTAssertEqual(resolved.target, .ollama(model: "llama3.2:1b"))
        XCTAssertEqual(resolved.source, .environment)
    }

    func testAnUnknownProviderNameMeansFoundryLocal() throws {
        let fixture = makeCleanupStore()

        let resolved = try connection(fixture.store, environment: ["SCRIBE_CLEANUP_PROVIDER": "foundry-local"])

        XCTAssertEqual(resolved.target, .foundryLocal(modelAlias: "qwen2.5-1.5b"))
    }

    func testAnIncompleteEnvironmentSaysWhereTheSettingsComeFrom() throws {
        let fixture = makeCleanupStore()
        let environment = ["SCRIBE_CLEANUP_PROVIDER": "openai-compatible"]

        assertNotConfigured(fixture.store, environment: environment, .openAIEndpointMissing, source: .environment)
        let message = CleanupConfigurationProblem.openAIEndpointMissing.message(for: .environment)
        XCTAssertTrue(message.contains("SCRIBE_CLEANUP_PROVIDER"), message)
    }

    /// `SCRIBE_CLEANUP_API_KEY` is sent, but never becomes part of the connection or its description.
    func testTheEnvironmentKeyIsSentButIsNoPartOfTheConnection() async throws {
        let fixture = makeCleanupStore()
        let environment = [
            "SCRIBE_CLEANUP_PROVIDER": "openai-compatible",
            "SCRIBE_CLEANUP_BASE_URL": "http://127.0.0.1:1234",
            "SCRIBE_CLEANUP_MODEL": "local-model",
            "SCRIBE_CLEANUP_API_KEY": PrivacyCanary.secret,
        ]
        let log = RequestLog()
        let session = makeStubSession { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned.")
        }

        let resolved = try connection(fixture.store, environment: environment)
        let provider = try CleanupProviderResolver.tryResolveDefaultProvider(
            store: fixture.store, environment: environment, factory: .testing(session: session))
        _ = try await provider.clean(CleanupRequest(transcript: "raw text"))

        XCTAssertEqual(
            resolved.target,
            .openAICompatible(
                completionsURL: URL(string: "http://127.0.0.1:1234/v1/chat/completions")!, model: "local-model",
                apiKey: .environment))
        PrivacyCanary.assertAbsent(from: String(describing: resolved))
        XCTAssertEqual(log.all.first?.header("Authorization"), "Bearer \(PrivacyCanary.secret)")
    }

    /// The scripted path takes the client id from the environment and the secret from the secret store, never from
    /// an environment variable.
    func testTheEnvironmentServicePrincipalReadsItsSecretFromTheSecretStore() throws {
        let fixture = makeCleanupStore(clientSecrets: InMemorySecretStore(["client-1": "secret-1"]))
        let environment = [
            "SCRIBE_CLEANUP_PROVIDER": "microsoft-foundry",
            "SCRIBE_AZURE_FOUNDRY_ENDPOINT": "https://my-res.openai.azure.com",
            "SCRIBE_AZURE_FOUNDRY_DEPLOYMENT": "gpt-5-mini",
            "SCRIBE_AZURE_AUTH_MODE": "service-principal",
            "SCRIBE_AZURE_TENANT_ID": "tenant-1",
            "SCRIBE_AZURE_CLIENT_ID": "client-1",
        ]

        XCTAssertEqual(try makeProvider(fixture.store, environment: environment).id, "microsoft-foundry")
        XCTAssertEqual(fixture.clientSecrets.reads, 1)

        var withoutSecret = environment
        withoutSecret["SCRIBE_AZURE_CLIENT_ID"] = "client-2"
        XCTAssertThrowsError(try makeProvider(fixture.store, environment: withoutSecret)) {
            XCTAssertEqual($0 as? CleanupProviderError, .notConfigured(.azureClientSecretMissing, source: .environment))
        }
    }
}
