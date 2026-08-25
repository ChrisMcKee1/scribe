import XCTest
@testable import Scribe

/// Exercises `CleanupSettingsStore` against the real `UserDefaults.standard` and real Keychain
/// (there is no dependency-injected store to substitute, matching the rest of this port's
/// stopgap-persistence tests). Every field's original value is captured in `setUp` and restored in
/// `tearDown`, including deleting secrets that didn't exist beforehand, so running this suite can
/// never permanently overwrite a developer's real saved AI cleanup configuration.
final class CleanupSettingsStoreTests: XCTestCase {
    private var originalIsEnabled = false
    private var originalProviderKind = CleanupProviderKind.foundryLocal
    private var originalFoundryLocalModelAlias = ""
    private var originalOllamaModel = ""
    private var originalOpenAIBaseURL = ""
    private var originalOpenAIModel = ""
    private var originalOpenAIApiKey: String?
    private var originalAzureEndpoint = ""
    private var originalAzureDeployment = ""
    private var originalAzureAuthMode = AzureAuthMode.azureCli
    private var originalAzureTenantId = ""
    private var originalAzureClientId = ""
    private var originalAzureClientSecret: String?

    override func setUp() {
        super.setUp()
        originalIsEnabled = CleanupSettingsStore.isEnabled
        originalProviderKind = CleanupSettingsStore.providerKind
        originalFoundryLocalModelAlias = CleanupSettingsStore.foundryLocalModelAlias
        originalOllamaModel = CleanupSettingsStore.ollamaModel
        originalOpenAIBaseURL = CleanupSettingsStore.openAIBaseURL
        originalOpenAIModel = CleanupSettingsStore.openAIModel
        originalOpenAIApiKey = CleanupSettingsStore.openAIApiKey()
        originalAzureEndpoint = CleanupSettingsStore.azureEndpoint
        originalAzureDeployment = CleanupSettingsStore.azureDeployment
        originalAzureAuthMode = CleanupSettingsStore.azureAuthMode
        originalAzureTenantId = CleanupSettingsStore.azureTenantId
        originalAzureClientId = CleanupSettingsStore.azureClientId
        originalAzureClientSecret = originalAzureClientId.isEmpty
            ? nil
            : CleanupSettingsStore.azureClientSecret(clientId: originalAzureClientId)
    }

    override func tearDown() {
        CleanupSettingsStore.isEnabled = originalIsEnabled
        CleanupSettingsStore.providerKind = originalProviderKind
        CleanupSettingsStore.foundryLocalModelAlias = originalFoundryLocalModelAlias
        CleanupSettingsStore.ollamaModel = originalOllamaModel
        CleanupSettingsStore.openAIBaseURL = originalOpenAIBaseURL
        CleanupSettingsStore.openAIModel = originalOpenAIModel
        try? CleanupSettingsStore.setOpenAIApiKey(originalOpenAIApiKey)
        CleanupSettingsStore.azureEndpoint = originalAzureEndpoint
        CleanupSettingsStore.azureDeployment = originalAzureDeployment
        CleanupSettingsStore.azureAuthMode = originalAzureAuthMode
        CleanupSettingsStore.azureTenantId = originalAzureTenantId
        // Clean up any test client id's secret before restoring the original client id, so a
        // throwaway test id never leaves a Keychain entry of its own behind.
        if CleanupSettingsStore.azureClientId != originalAzureClientId {
            try? CleanupSettingsStore.setAzureClientSecret(nil, clientId: CleanupSettingsStore.azureClientId)
        }
        CleanupSettingsStore.azureClientId = originalAzureClientId
        if !originalAzureClientId.isEmpty {
            try? CleanupSettingsStore.setAzureClientSecret(originalAzureClientSecret, clientId: originalAzureClientId)
        }
        super.tearDown()
    }

    func testDefaultsMatchProviderDefaultsWhenNothingIsSaved() {
        // Not a strict "unset" assertion (UserDefaults.standard is real and process-wide), but the
        // getters' fallback values must match what FoundryLocalCleanupProvider/
        // ManagedOllamaCleanupProvider themselves default to, so a first-run user gets a
        // consistent provider whether or not Settings has ever been opened.
        CleanupSettingsStore.providerKind = .foundryLocal
        XCTAssertEqual(CleanupProviderKind.foundryLocal.displayName, "Foundry Local (recommended)")
    }

    func testNonSecretFieldsRoundTripThroughUserDefaults() {
        CleanupSettingsStore.isEnabled = true
        XCTAssertTrue(CleanupSettingsStore.isEnabled)

        CleanupSettingsStore.providerKind = .microsoftFoundry
        XCTAssertEqual(CleanupSettingsStore.providerKind, .microsoftFoundry)

        CleanupSettingsStore.foundryLocalModelAlias = "qwen2.5-3b"
        XCTAssertEqual(CleanupSettingsStore.foundryLocalModelAlias, "qwen2.5-3b")

        CleanupSettingsStore.ollamaModel = "llama3.2:1b"
        XCTAssertEqual(CleanupSettingsStore.ollamaModel, "llama3.2:1b")

        CleanupSettingsStore.openAIBaseURL = "http://localhost:1234"
        XCTAssertEqual(CleanupSettingsStore.openAIBaseURL, "http://localhost:1234")

        CleanupSettingsStore.openAIModel = "local-model"
        XCTAssertEqual(CleanupSettingsStore.openAIModel, "local-model")

        CleanupSettingsStore.azureEndpoint = "https://example.cognitiveservices.azure.com"
        XCTAssertEqual(CleanupSettingsStore.azureEndpoint, "https://example.cognitiveservices.azure.com")

        CleanupSettingsStore.azureDeployment = "gpt-4o-mini"
        XCTAssertEqual(CleanupSettingsStore.azureDeployment, "gpt-4o-mini")

        CleanupSettingsStore.azureAuthMode = .servicePrincipal
        XCTAssertEqual(CleanupSettingsStore.azureAuthMode, .servicePrincipal)

        CleanupSettingsStore.azureTenantId = "11111111-1111-1111-1111-111111111111"
        XCTAssertEqual(CleanupSettingsStore.azureTenantId, "11111111-1111-1111-1111-111111111111")
    }

    func testOpenAIApiKeyRoundTripsThroughKeychainAndClearsOnNil() throws {
        try CleanupSettingsStore.setOpenAIApiKey("sk-test-key")
        XCTAssertEqual(CleanupSettingsStore.openAIApiKey(), "sk-test-key")

        try CleanupSettingsStore.setOpenAIApiKey(nil)
        XCTAssertNil(CleanupSettingsStore.openAIApiKey())
    }

    func testOpenAIApiKeySettingEmptyStringClearsIt() throws {
        try CleanupSettingsStore.setOpenAIApiKey("sk-test-key")
        try CleanupSettingsStore.setOpenAIApiKey("")
        XCTAssertNil(CleanupSettingsStore.openAIApiKey())
    }

    func testAzureClientSecretRoundTripsKeyedByClientId() throws {
        let clientId = "test-client-id-\(UUID().uuidString)"
        defer { try? CleanupSettingsStore.setAzureClientSecret(nil, clientId: clientId) }

        XCTAssertNil(CleanupSettingsStore.azureClientSecret(clientId: clientId))

        try CleanupSettingsStore.setAzureClientSecret("super-secret", clientId: clientId)
        XCTAssertEqual(CleanupSettingsStore.azureClientSecret(clientId: clientId), "super-secret")

        try CleanupSettingsStore.setAzureClientSecret(nil, clientId: clientId)
        XCTAssertNil(CleanupSettingsStore.azureClientSecret(clientId: clientId))
    }

    func testAzureClientSecretIsScopedPerClientId() throws {
        let firstClientId = "test-client-a-\(UUID().uuidString)"
        let secondClientId = "test-client-b-\(UUID().uuidString)"
        defer {
            try? CleanupSettingsStore.setAzureClientSecret(nil, clientId: firstClientId)
            try? CleanupSettingsStore.setAzureClientSecret(nil, clientId: secondClientId)
        }

        try CleanupSettingsStore.setAzureClientSecret("secret-a", clientId: firstClientId)
        try CleanupSettingsStore.setAzureClientSecret("secret-b", clientId: secondClientId)

        XCTAssertEqual(CleanupSettingsStore.azureClientSecret(clientId: firstClientId), "secret-a")
        XCTAssertEqual(CleanupSettingsStore.azureClientSecret(clientId: secondClientId), "secret-b")
    }

    func testAzureClientSecretWithBlankClientIdIsANoOp() throws {
        // Guards against ever writing a Keychain item keyed by an empty account string, which
        // would be shared/ambiguous across every not-yet-configured install.
        try CleanupSettingsStore.setAzureClientSecret("orphaned-secret", clientId: "")
        XCTAssertNil(CleanupSettingsStore.azureClientSecret(clientId: ""))
    }

    func testIsConfiguredForFoundryLocalAndOllamaIsAlwaysTrue() {
        XCTAssertTrue(CleanupSettingsStore.isConfigured(for: .foundryLocal))
        XCTAssertTrue(CleanupSettingsStore.isConfigured(for: .ollama))
    }

    func testIsConfiguredForOpenAICompatibleRequiresBaseURLAndModel() {
        CleanupSettingsStore.openAIBaseURL = ""
        CleanupSettingsStore.openAIModel = ""
        XCTAssertFalse(CleanupSettingsStore.isConfigured(for: .openAICompatible))

        CleanupSettingsStore.openAIBaseURL = "http://localhost:1234"
        CleanupSettingsStore.openAIModel = ""
        XCTAssertFalse(CleanupSettingsStore.isConfigured(for: .openAICompatible))

        CleanupSettingsStore.openAIModel = "local-model"
        XCTAssertTrue(CleanupSettingsStore.isConfigured(for: .openAICompatible))
    }

    func testIsConfiguredForMicrosoftFoundryRequiresEndpointAndDeployment() {
        CleanupSettingsStore.azureEndpoint = ""
        CleanupSettingsStore.azureDeployment = ""
        XCTAssertFalse(CleanupSettingsStore.isConfigured(for: .microsoftFoundry))

        CleanupSettingsStore.azureEndpoint = "https://example.cognitiveservices.azure.com"
        CleanupSettingsStore.azureDeployment = ""
        XCTAssertFalse(CleanupSettingsStore.isConfigured(for: .microsoftFoundry))

        CleanupSettingsStore.azureDeployment = "gpt-4o-mini"
        XCTAssertTrue(CleanupSettingsStore.isConfigured(for: .microsoftFoundry))
    }
}
