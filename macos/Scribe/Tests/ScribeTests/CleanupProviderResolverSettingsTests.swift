import XCTest
@testable import Scribe

/// Exercises `CleanupProviderResolver.tryResolveDefaultProvider()`'s Settings-backed fallback path
/// (`CleanupSettingsStore`), used whenever no `SCRIBE_CLEANUP_PROVIDER` environment variable is
/// set. The environment-variable path itself is exercised indirectly by
/// `MicrosoftFoundryCleanupProviderTests`/`OpenAICompatibleCleanupProviderTests`, which construct
/// those providers directly; this suite is specifically about the newer GUI configuration route.
///
/// Assumes `SCRIBE_CLEANUP_PROVIDER` is not set in the test environment (true for a normal
/// `swift test` invocation); if it were set, every case here would instead exercise the
/// environment-variable path, which is intentional and would still pass, but wouldn't be testing
/// what this suite claims to.
final class CleanupProviderResolverSettingsTests: XCTestCase {
    private var originalProviderKind = CleanupProviderKind.foundryLocal
    private var originalFoundryLocalModelAlias = ""
    private var originalOllamaModel = ""
    private var originalOpenAIBaseURL = ""
    private var originalOpenAIModel = ""
    private var originalAzureEndpoint = ""
    private var originalAzureDeployment = ""
    private var originalAzureAuthMode = AzureAuthMode.azureCli
    private var originalAzureClientId = ""

    override func setUp() {
        super.setUp()
        XCTAssertNil(
            ProcessInfo.processInfo.environment["SCRIBE_CLEANUP_PROVIDER"],
            "This suite assumes no SCRIBE_CLEANUP_PROVIDER override in the test environment.")
        originalProviderKind = CleanupSettingsStore.providerKind
        originalFoundryLocalModelAlias = CleanupSettingsStore.foundryLocalModelAlias
        originalOllamaModel = CleanupSettingsStore.ollamaModel
        originalOpenAIBaseURL = CleanupSettingsStore.openAIBaseURL
        originalOpenAIModel = CleanupSettingsStore.openAIModel
        originalAzureEndpoint = CleanupSettingsStore.azureEndpoint
        originalAzureDeployment = CleanupSettingsStore.azureDeployment
        originalAzureAuthMode = CleanupSettingsStore.azureAuthMode
        originalAzureClientId = CleanupSettingsStore.azureClientId
    }

    override func tearDown() {
        CleanupSettingsStore.providerKind = originalProviderKind
        CleanupSettingsStore.foundryLocalModelAlias = originalFoundryLocalModelAlias
        CleanupSettingsStore.ollamaModel = originalOllamaModel
        CleanupSettingsStore.openAIBaseURL = originalOpenAIBaseURL
        CleanupSettingsStore.openAIModel = originalOpenAIModel
        CleanupSettingsStore.azureEndpoint = originalAzureEndpoint
        CleanupSettingsStore.azureDeployment = originalAzureDeployment
        CleanupSettingsStore.azureAuthMode = originalAzureAuthMode
        CleanupSettingsStore.azureClientId = originalAzureClientId
        super.tearDown()
    }

    func testDefaultSettingsResolveToFoundryLocal() throws {
        CleanupSettingsStore.providerKind = .foundryLocal
        CleanupSettingsStore.foundryLocalModelAlias = "qwen2.5-1.5b"

        let provider = try CleanupProviderResolver.tryResolveDefaultProvider()

        XCTAssertEqual(provider.id, "foundry-local")
        XCTAssertEqual(provider.displayName, "Foundry Local")
    }

    func testOllamaSettingResolvesToManagedOllamaProvider() throws {
        CleanupSettingsStore.providerKind = .ollama
        CleanupSettingsStore.ollamaModel = "qwen2.5:3b"

        let provider = try CleanupProviderResolver.tryResolveDefaultProvider()

        XCTAssertEqual(provider.id, "managed-ollama")
    }

    func testOpenAICompatibleWithoutBaseURLOrModelThrowsNotConfigured() {
        CleanupSettingsStore.providerKind = .openAICompatible
        CleanupSettingsStore.openAIBaseURL = ""
        CleanupSettingsStore.openAIModel = ""

        XCTAssertThrowsError(try CleanupProviderResolver.tryResolveDefaultProvider()) { error in
            guard case CleanupProviderError.notConfigured = error else {
                return XCTFail("Expected .notConfigured, got \(error)")
            }
        }
    }

    func testOpenAICompatibleWithBaseURLAndModelResolves() throws {
        CleanupSettingsStore.providerKind = .openAICompatible
        CleanupSettingsStore.openAIBaseURL = "http://localhost:1234"
        CleanupSettingsStore.openAIModel = "local-model"

        let provider = try CleanupProviderResolver.tryResolveDefaultProvider()

        XCTAssertEqual(provider.id, "openai-compatible")
        XCTAssertEqual(provider.displayName, "OpenAI-compatible endpoint")
    }

    func testMicrosoftFoundryWithoutEndpointOrDeploymentThrowsNotConfigured() {
        CleanupSettingsStore.providerKind = .microsoftFoundry
        CleanupSettingsStore.azureEndpoint = ""
        CleanupSettingsStore.azureDeployment = ""

        XCTAssertThrowsError(try CleanupProviderResolver.tryResolveDefaultProvider()) { error in
            guard case CleanupProviderError.notConfigured = error else {
                return XCTFail("Expected .notConfigured, got \(error)")
            }
        }
    }

    func testMicrosoftFoundryWithEndpointAndDeploymentUsingCliAuthResolves() throws {
        CleanupSettingsStore.providerKind = .microsoftFoundry
        CleanupSettingsStore.azureEndpoint = "https://example.cognitiveservices.azure.com"
        CleanupSettingsStore.azureDeployment = "gpt-4o-mini"
        CleanupSettingsStore.azureAuthMode = .azureCli

        let provider = try CleanupProviderResolver.tryResolveDefaultProvider()

        XCTAssertEqual(provider.id, "microsoft-foundry")
    }

    func testMicrosoftFoundryServicePrincipalWithoutClientIdThrowsNotConfigured() {
        CleanupSettingsStore.providerKind = .microsoftFoundry
        CleanupSettingsStore.azureEndpoint = "https://example.cognitiveservices.azure.com"
        CleanupSettingsStore.azureDeployment = "gpt-4o-mini"
        CleanupSettingsStore.azureAuthMode = .servicePrincipal
        CleanupSettingsStore.azureClientId = ""

        XCTAssertThrowsError(try CleanupProviderResolver.tryResolveDefaultProvider()) { error in
            guard case CleanupProviderError.notConfigured = error else {
                return XCTFail("Expected .notConfigured, got \(error)")
            }
        }
    }

    func testMicrosoftFoundryServicePrincipalWithoutSavedSecretThrowsNotConfigured() {
        let clientId = "test-client-\(UUID().uuidString)"
        CleanupSettingsStore.providerKind = .microsoftFoundry
        CleanupSettingsStore.azureEndpoint = "https://example.cognitiveservices.azure.com"
        CleanupSettingsStore.azureDeployment = "gpt-4o-mini"
        CleanupSettingsStore.azureAuthMode = .servicePrincipal
        CleanupSettingsStore.azureClientId = clientId
        // Deliberately no saved secret and no tenant id for this fresh client id.

        XCTAssertThrowsError(try CleanupProviderResolver.tryResolveDefaultProvider()) { error in
            guard case CleanupProviderError.notConfigured = error else {
                return XCTFail("Expected .notConfigured, got \(error)")
            }
        }
    }

    func testMicrosoftFoundryServicePrincipalWithSavedSecretResolves() throws {
        let clientId = "test-client-\(UUID().uuidString)"
        CleanupSettingsStore.providerKind = .microsoftFoundry
        CleanupSettingsStore.azureEndpoint = "https://example.cognitiveservices.azure.com"
        CleanupSettingsStore.azureDeployment = "gpt-4o-mini"
        CleanupSettingsStore.azureAuthMode = .servicePrincipal
        CleanupSettingsStore.azureTenantId = "11111111-1111-1111-1111-111111111111"
        CleanupSettingsStore.azureClientId = clientId
        try CleanupSettingsStore.setAzureClientSecret("test-secret", clientId: clientId)
        defer { try? CleanupSettingsStore.setAzureClientSecret(nil, clientId: clientId) }

        let provider = try CleanupProviderResolver.tryResolveDefaultProvider()

        XCTAssertEqual(provider.id, "microsoft-foundry")
    }
}
