import XCTest

@testable import Scribe

/// Failing providers, driven through stubs and fakes with privacy canaries wherever a key, an endpoint, an identity or
/// the user's words can hide, prove that no failure carries them into a standard error line, the unified log's public
/// argument, an error description, a printed or dumped error, or a failure shape. Only the Settings text may repeat
/// what an endpoint said about a refused request, and these tests pin that too, so the one exception stays visible.
final class CleanupPrivacyTests: XCTestCase {
    private let canaryHost = "https://canary-7f3a.openai.azure.com"

    /// Every way an error can be turned into text by accident, and the log's own path.
    private func assertNothingLeaks(from error: any Error, file: StaticString = #filePath, line: UInt = #line) {
        var dumped = ""
        dump(error, to: &dumped)
        for text in [
            error.localizedDescription, String(describing: error), String(reflecting: error),
            FailureShape(error).description, dumped,
        ] {
            PrivacyCanary.assertAbsent(from: text, file: file, line: line)
        }

        let recorder = recordScribeLog()
        ScribeLog.warning(.cleanup, "AI cleanup failed, using the text without it", .failure(error))
        recorder.stop()
        XCTAssertEqual(recorder.renderings.count, 1, file: file, line: line)
        PrivacyCanary.assertAbsent(from: recorder.publicText, file: file, line: line)
    }

    func testAnOpenAICompatibleRefusalLeaksNothingButTellsSettings() async throws {
        let body = """
            {"error":{"message":"Key \(PrivacyCanary.secret) is not valid for \(canaryHost)","code":"canary_code"}}
            """
        let recorder = recordScribeLog()
        let provider = OpenAICompatibleCleanupProvider(
            model: "canary-model", apiKey: PrivacyCanary.secret,
            completionsURL: OpenAICompatibleEndpoint.chatCompletionsURL(for: URL(string: PrivacyCanary.url)!)!,
            session: makeStubSession { request in StubReply.json(request, status: 401, body) })

        let error = try await cleanupFailure(of: provider, CleanupRequest(transcript: PrivacyCanary.transcript))
        recorder.stop()

        assertNothingLeaks(from: error)
        PrivacyCanary.assertAbsent(from: recorder.publicText)
        XCTAssertEqual(FailureShape(error).description, "CleanupProviderError.rejected values=401 http=401 service=other")
        let settingsText = CleanupFailureText.forSettings(error, providerName: "OpenAI-compatible endpoint")
        XCTAssertTrue(settingsText.contains("is not valid for"), "the endpoint's own words reach Settings: \(settingsText)")
    }

    /// A successful cleanup logs its shape: the provider, the time and a character count, never the answer.
    func testASuccessfulCleanupLogsOnlyItsShape() async throws {
        let recorder = recordScribeLog()
        let provider = OpenAICompatibleCleanupProvider(
            model: "canary-model", apiKey: PrivacyCanary.secret,
            completionsURL: URL(string: "http://127.0.0.1:9/v1/chat/completions")!,
            session: makeStubSession { request in StubReply.completion(request, PrivacyCanary.transcript) })

        let response = try await provider.clean(CleanupRequest(transcript: PrivacyCanary.transcript))
        recorder.stop()

        XCTAssertEqual(response.cleanedText, PrivacyCanary.transcript)
        let finished = recorder.lines.filter { $0.contains("Cleanup request finished") }
        XCTAssertEqual(finished.count, 1, "\(recorder.lines)")
        PrivacyCanary.assertAbsent(from: recorder.publicText)
    }

    func testAMicrosoftFoundryRefusalLeaksNeitherTheEndpointNorTheDeployment() async throws {
        let body = #"{"error":{"code":"DeploymentNotFound","message":"No deployment gpt-canary on canary-7f3a"}}"#
        let provider = MicrosoftFoundryCleanupProvider(
            inferenceBase: MicrosoftFoundryCleanupProvider.inferenceBase(for: URL(string: canaryHost)!)!,
            deployment: "gpt-canary", credential: RecordingCredential(token: PrivacyCanary.secret),
            session: makeStubSession { request in StubReply.json(request, status: 404, body) })

        let error = try await cleanupFailure(of: provider, CleanupRequest(transcript: PrivacyCanary.transcript))

        assertNothingLeaks(from: error)
    }

    /// Entra's description repeats the app and tenant ids and carries trace ids, so it does not even reach Settings.
    func testAnEntraRefusalLeaksNoIdsAndNoDescriptionAnywhere() async throws {
        let body = """
            {"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret for app 'canary-app' \
            in tenant 'canary-tenant'. Trace ID: canary-trace","error_codes":[7000215]}
            """
        let credential = AzureServicePrincipalCredentialProvider(
            principal: AzureServicePrincipal(
                tenantId: "canary-tenant.onmicrosoft.com", clientId: "canary-client", clientSecret: PrivacyCanary.secret),
            session: makeStubSession { request in StubReply.json(request, status: 401, body) })
        let provider = MicrosoftFoundryCleanupProvider(
            inferenceBase: MicrosoftFoundryCleanupProvider.inferenceBase(for: URL(string: canaryHost)!)!,
            deployment: "gpt-canary", credential: credential,
            session: makeStubSession { request in StubReply.completion(request, "never") })

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(
            error,
            .credentialUnavailable(
                .tokenRejected(status: 401, aadsts: 7_000_215, reply: CleanupServiceReply(code: "invalid_client", message: nil))))
        assertNothingLeaks(from: error)
        PrivacyCanary.assertAbsent(from: CleanupFailureText.forSettings(error, providerName: "Microsoft Foundry"))
    }

    /// `az` prints the signed-in account; none of it survives, not even in Settings.
    func testAnAzureCliFailureLeaksNothingItPrinted() async throws {
        let fake = FakeAzureCli(outcomes: [
            .exited(
                1,
                standardError: "ERROR: Please run 'az login' to setup account. Signed in as dana-canary@contoso.com "
                    + "in tenant canary-tenant")
        ])
        let directory = try makeScript(named: "az", body: "exit 1").deletingLastPathComponent()
        let credential = AzureCliCredentialProvider(
            tenantId: "canary-tenant.onmicrosoft.com", searchPath: [directory.path(percentEncoded: false)],
            lane: AsyncLane(), launch: fake.launch)
        let provider = MicrosoftFoundryCleanupProvider(
            inferenceBase: MicrosoftFoundryCleanupProvider.inferenceBase(for: URL(string: canaryHost)!)!,
            deployment: "gpt-canary", credential: credential,
            session: makeStubSession { request in StubReply.completion(request, "never") })

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(error, .credentialUnavailable(.cliFailed(exitStatus: 1, reason: .notSignedIn, aadsts: nil)))
        assertNothingLeaks(from: error)
        PrivacyCanary.assertAbsent(from: CleanupFailureText.forSettings(error, providerName: "Microsoft Foundry"))
    }

    func testAFoundryLocalStatusFailureLeaksNothingItPrinted() async throws {
        let foundry = try makeScript(
            named: "foundry", body: "echo '{\"canary\":\"/Users/dana-canary/models\"}'\necho 'canary' >&2\nexit 1")
        let provider = FoundryLocalCleanupProvider(
            status: .live(environment: ["SCRIBE_FOUNDRY_CLI": foundry.path(percentEncoded: false)]),
            session: makeStubSession { request in StubReply.completion(request, "never") })

        let error = try await cleanupFailure(of: provider, CleanupRequest(transcript: PrivacyCanary.transcript))

        XCTAssertEqual(error, .endpointUnavailable(.foundryLocalNotReady))
        assertNothingLeaks(from: error)
    }

    /// Test Connection may show the endpoint's words in Settings, but its log line is the shape alone.
    func testTestConnectionLogsOnlyTheShape() async throws {
        let fixture = makeCleanupStore(
            apiKeys: InMemorySecretStore([CleanupSettingsStore.openAIApiKeyAccount: PrivacyCanary.secret]))
        fixture.store.providerKind = .openAICompatible
        fixture.store.openAIBaseURL = canaryHost
        fixture.store.openAIModel = "canary-model"
        let session = makeStubSession { request in
            StubReply.json(request, status: 403, #"{"error":{"message":"canary-model is not enabled for this key"}}"#)
        }
        let cache = CleanupProviderCache(store: fixture.store, environment: [:], factory: .testing(session: session))
        let recorder = recordScribeLog()

        let check = await cache.checkConnection()
        recorder.stop()

        XCTAssertFalse(check.reachable)
        XCTAssertTrue(check.message.contains("is not enabled for this key"), check.message)
        XCTAssertTrue(recorder.lines.contains { $0.contains("Test Connection failed") }, "\(recorder.lines)")
        PrivacyCanary.assertAbsent(from: recorder.publicText)
    }

    /// The values that describe a configuration or an identity print without it.
    func testConnectionsCredentialsAndRepliesPrintNothingIdentifying() throws {
        let fixture = makeCleanupStore(clientSecrets: InMemorySecretStore(["canary-client": PrivacyCanary.secret]))
        fixture.store.providerKind = .microsoftFoundry
        fixture.store.azureEndpoint = canaryHost + "/api/projects/canary-project"
        fixture.store.azureDeployment = "gpt-canary"
        fixture.store.azureAuthMode = .servicePrincipal
        fixture.store.azureTenantId = "canary-tenant.onmicrosoft.com"
        fixture.store.azureClientId = "canary-client"

        let connection = try CleanupProviderResolver.connection(store: fixture.store, environment: [:])
        let principal = AzureServicePrincipal(
            tenantId: "canary-tenant", clientId: "canary-client", clientSecret: PrivacyCanary.secret)
        let token = AzureAccessToken(token: PrivacyCanary.secret, expiresAt: .distantFuture)
        let reply = CleanupServiceReply(code: "canary_code", message: PrivacyCanary.transcript)

        for value in [connection as Any, principal, token, reply] {
            var dumped = ""
            dump(value, to: &dumped)
            for text in [String(describing: value), String(reflecting: value), dumped] {
                PrivacyCanary.assertAbsent(from: text)
            }
        }
    }
}
