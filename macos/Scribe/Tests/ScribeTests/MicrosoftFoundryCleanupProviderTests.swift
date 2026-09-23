import XCTest

@testable import Scribe

final class MicrosoftFoundryEndpointTests: XCTestCase {
    /// Windows 0.4.3 sends every saved shape to the account's `/openai/v1/` inference endpoint
    /// (`AzureOpenAIResponsesClientFactory.GetV1Endpoint`), after a project's own route answered HTTP 500.
    func testEverySavedEndpointShapeReachesTheAccountsV1Base() {
        let cases: [(endpoint: String, base: String)] = [
            ("https://my-res.services.ai.azure.com/api/projects/my-project", "https://my-res.services.ai.azure.com/openai/v1/"),
            ("https://my-res.openai.azure.com/", "https://my-res.openai.azure.com/openai/v1/"),
            ("https://my-res.cognitiveservices.azure.com", "https://my-res.cognitiveservices.azure.com/openai/v1/"),
            ("https://my-res.openai.azure.com/openai/v1/", "https://my-res.openai.azure.com/openai/v1/"),
            (
                "https://my-res.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-08-01-preview",
                "https://my-res.openai.azure.com/openai/v1/"
            ),
            ("https://My-Res.OpenAI.Azure.com/openai/v1", "https://my-res.openai.azure.com/openai/v1/"),
            ("https://user:secret@my-res.openai.azure.com/x#part", "https://my-res.openai.azure.com/openai/v1/"),
            ("https://localhost:8443/whatever", "https://localhost:8443/openai/v1/"),
        ]

        for (endpoint, base) in cases {
            XCTAssertEqual(
                MicrosoftFoundryCleanupProvider.inferenceBase(for: URL(string: endpoint)!)?.absoluteString, base,
                endpoint)
        }
    }

    /// An Entra token never travels over plain HTTP, so an http endpoint is refused before any request is built.
    func testAnEndpointThatIsNotAnHTTPSURLWithAHostHasNoBase() {
        for endpoint in [
            "ftp://my-res.openai.azure.com", "my-res.openai.azure.com", "file:///openai/v1",
            "http://my-res.openai.azure.com", "HTTP://my-res.openai.azure.com/openai/v1/", "http://localhost:8080",
        ] {
            XCTAssertNil(MicrosoftFoundryCleanupProvider.inferenceBase(for: URL(string: endpoint)!), endpoint)
        }
    }
}

final class MicrosoftFoundryCleanupProviderTests: XCTestCase {
    private func makeProvider(
        endpoint: String = "https://my-res.services.ai.azure.com/api/projects/my-project",
        deployment: String = "gpt-5-mini",
        credential: any AzureCredentialProvider = RecordingCredential(),
        _ handler: @escaping StubURLProtocol.Handler
    ) -> MicrosoftFoundryCleanupProvider {
        MicrosoftFoundryCleanupProvider(
            inferenceBase: MicrosoftFoundryCleanupProvider.inferenceBase(for: URL(string: endpoint)!)!,
            deployment: deployment, credential: credential, session: makeStubSession(handler))
    }

    /// The request shape reasoning deployments accept: the account's v1 route with no dated api-version, `model` set
    /// to the deployment, no `temperature` and no `store`, and a bearer token for the Azure AI audience.
    func testCleanPostsToTheAccountsV1ChatCompletionsWithTheDeploymentAsTheModel() async throws {
        let log = RequestLog()
        let credential = RecordingCredential(token: "entra-token")
        let provider = makeProvider(credential: credential) { request in
            log.record(request)
            return StubReply.completion(request, "Cleaned text.")
        }

        let response = try await provider.clean(CleanupRequest(transcript: "raw text", writingStylePrompt: "Style."))

        XCTAssertEqual(response.cleanedText, "Cleaned text.")
        XCTAssertEqual(response.providerID, "microsoft-foundry")
        XCTAssertEqual(response.modelID, "gpt-5-mini")
        let sent = try XCTUnwrap(log.all.first)
        XCTAssertEqual(sent.method, "POST")
        XCTAssertEqual(sent.url?.absoluteString, "https://my-res.services.ai.azure.com/openai/v1/chat/completions")
        XCTAssertNil(sent.url?.query)
        XCTAssertEqual(sent.header("Authorization"), "Bearer entra-token")
        XCTAssertEqual(Set(sent.jsonBody.keys), ["model", "messages", "stream"])
        XCTAssertEqual(sent.jsonBody["model"] as? String, "gpt-5-mini")
        XCTAssertEqual(sent.messageContents, ["Style.", "raw text"])
        XCTAssertEqual(credential.requestedScopes, ["https://ai.azure.com/.default"])
    }

    func testAForbiddenAnswerLeadsWithPropagationAndTheRightRoles() async throws {
        let provider = makeProvider { request in
            StubReply.json(request, status: 403, #"{"error":{"code":"PermissionDenied","message":"Principal does not have access."}}"#)
        }

        let error = try await cleanupFailure(of: provider)

        let description = try XCTUnwrap(error.errorDescription)
        XCTAssertTrue(description.contains("(403)"), description)
        XCTAssertTrue(description.contains("ten minutes"), description)
        XCTAssertTrue(description.contains("'Foundry User'"), description)
        XCTAssertTrue(description.contains("'Cognitive Services OpenAI User'"), description)
        XCTAssertEqual(error.failureServiceCode, "PermissionDenied")
        XCTAssertEqual(error.settingsDetail, "Principal does not have access.")
    }

    func testAnUnauthorizedAnswerMentionsTheCustomSubdomain() async throws {
        let provider = makeProvider { request in StubReply.json(request, status: 401, "unauthorized") }

        let error = try await cleanupFailure(of: provider)

        let description = try XCTUnwrap(error.errorDescription)
        XCTAssertTrue(description.contains("(401)"), description)
        XCTAssertTrue(description.contains("custom subdomain"), description)
    }

    /// The deployment name is the user's; the description stays as safe to log as the shape.
    func testAMissingDeploymentIsNamedWithoutItsName() async throws {
        let provider = makeProvider(deployment: "gpt-private-name") { request in
            StubReply.json(
                request, status: 404,
                #"{"error":{"code":"DeploymentNotFound","message":"The API deployment for this resource does not exist."}}"#)
        }

        let error = try await cleanupFailure(of: provider)

        let description = try XCTUnwrap(error.errorDescription)
        XCTAssertTrue(description.contains("could not find the deployment (404)"), description)
        XCTAssertFalse(description.contains("gpt-private-name"), description)
        XCTAssertEqual(
            FailureShape(error).description, "CleanupProviderError.rejected values=404 http=404 service=DeploymentNotFound")
    }

    func testACredentialFailureIsReportedWithoutSendingAnything() async throws {
        let log = RequestLog()
        let provider = makeProvider(credential: RecordingCredential(failure: .cliNotFound)) { request in
            log.record(request)
            return StubReply.completion(request, "never")
        }

        let error = try await cleanupFailure(of: provider)

        XCTAssertEqual(error, .credentialUnavailable(.cliNotFound))
        XCTAssertEqual(log.count, 0)
    }
}

final class AzureCliAccessTokenParserTests: XCTestCase {
    private let utc = TimeZone(identifier: "UTC")!

    /// `expires_on` is seconds since 1970 and cannot be misread; `expiresOn` is local time without a zone.
    func testTheEpochExpiryWinsOverTheLocalTime() throws {
        let json = #"{"accessToken":"tok","expires_on":1900000000,"expiresOn":"2099-01-01 12:00:00.000000"}"#

        let token = try XCTUnwrap(AzureCliAccessTokenParser.parse(Data(json.utf8), timeZone: utc))

        XCTAssertEqual(token.token, "tok")
        XCTAssertEqual(token.expiresAt, Date(timeIntervalSince1970: 1_900_000_000))
    }

    func testTheLocalTimeIsReadInTheGivenTimeZone() throws {
        let expected = try XCTUnwrap(ISO8601DateFormatter().date(from: "2030-01-02T03:04:05Z"))
        for text in ["2030-01-02 03:04:05.000000", "2030-01-02 03:04:05"] {
            let json = #"{"accessToken":"tok","expiresOn":"\#(text)"}"#
            let token = try XCTUnwrap(AzureCliAccessTokenParser.parse(Data(json.utf8), timeZone: utc), text)
            XCTAssertEqual(token.expiresAt, expected, text)
        }
    }

    func testAnEpochWrittenAsTextIsRead() throws {
        let json = #"{"accessToken":"tok","expires_on":"1900000000"}"#

        let token = try XCTUnwrap(AzureCliAccessTokenParser.parse(Data(json.utf8)))

        XCTAssertEqual(token.expiresAt, Date(timeIntervalSince1970: 1_900_000_000))
    }

    func testAnUnreadableExpiryGetsFiveMinutesFromNow() throws {
        let now = Date(timeIntervalSince1970: 1_000)
        let json = #"{"accessToken":"tok","expiresOn":"soon"}"#

        let token = try XCTUnwrap(AzureCliAccessTokenParser.parse(Data(json.utf8), now: now, timeZone: utc))

        XCTAssertEqual(token.expiresAt, Date(timeIntervalSince1970: 1_300))
    }

    func testOutputWithoutATokenIsNotAToken() {
        for json in [#"{"subscription":"sub"}"#, #"{"accessToken":""}"#, "not json"] {
            XCTAssertNil(AzureCliAccessTokenParser.parse(Data(json.utf8)), json)
        }
    }
}

final class AzureServicePrincipalTests: XCTestCase {
    func testTryCreateSucceedsWhenAllFieldsArePresent() {
        let principal = AzureServicePrincipal.tryCreate(
            tenantId: " tenant-1 ", clientId: "client-1", clientSecret: "secret-1")
        XCTAssertEqual(principal?.tenantId, "tenant-1")
        XCTAssertEqual(principal?.clientId, "client-1")
        XCTAssertEqual(principal?.clientSecret, "secret-1")
    }

    func testTryCreateFailsWhenAnyFieldIsBlank() {
        XCTAssertNil(AzureServicePrincipal.tryCreate(tenantId: nil, clientId: "client-1", clientSecret: "secret-1"))
        XCTAssertNil(AzureServicePrincipal.tryCreate(tenantId: "tenant-1", clientId: "  ", clientSecret: "secret-1"))
        XCTAssertNil(AzureServicePrincipal.tryCreate(tenantId: "tenant-1", clientId: "client-1", clientSecret: nil))
        XCTAssertNil(AzureServicePrincipal.tryCreate(tenantId: "tenant-1", clientId: "client-1", clientSecret: ""))
    }

    func testOnlyATenantThatCannotChangeTheURLOrReachAzAsAnOptionIsValid() {
        for tenant in ["11111111-1111-1111-1111-111111111111", "contoso.onmicrosoft.com", "Contoso-Dev.example.org"] {
            XCTAssertTrue(AzureTenant.isValid(tenant), tenant)
        }
        for tenant in ["", "a/b", "../x", "a?b", "a b", "--tenant", ".hidden", "t\u{e9}nant", String(repeating: "a", count: 254)] {
            XCTAssertFalse(AzureTenant.isValid(tenant), tenant)
        }
    }
}

final class AzureServicePrincipalCredentialProviderTests: XCTestCase {
    private let scope = MicrosoftFoundryCleanupProvider.inferenceScope

    private func makeProvider(
        tenantId: String = "tenant-1", clientId: String = "client-1", secret: String = "secret-1",
        clock: TestClock = TestClock(), _ handler: @escaping StubURLProtocol.Handler
    ) -> AzureServicePrincipalCredentialProvider {
        AzureServicePrincipalCredentialProvider(
            principal: AzureServicePrincipal(tenantId: tenantId, clientId: clientId, clientSecret: secret),
            session: makeStubSession(handler), now: clock.now)
    }

    func testTheTokenRequestIsAFormPostToTheTenantsTokenEndpoint() async throws {
        let log = RequestLog()
        let provider = makeProvider { request in
            log.record(request)
            return StubReply.entraToken(request, "entra-token")
        }

        let token = try await provider.accessToken(scope: scope)

        XCTAssertEqual(token.token, "entra-token")
        let sent = try XCTUnwrap(log.all.first)
        XCTAssertEqual(sent.method, "POST")
        XCTAssertEqual(sent.url?.absoluteString, "https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token")
        XCTAssertEqual(sent.header("Content-Type"), "application/x-www-form-urlencoded")
        XCTAssertEqual(
            FormDecoding.fields(sent.body),
            ["grant_type": "client_credentials", "client_id": "client-1", "client_secret": "secret-1", "scope": scope])
    }

    /// `URLComponents` leaves `+` alone and a form decoder reads it as a space, which corrupted a secret holding one.
    /// Every character a secret can hold has to decode back to itself.
    func testEverySecretCharacterSurvivesTheFormBody() async throws {
        let secrets = [
            "a+b", "a+b+c==", "100%", "%2B", "x&y=z", "with space", "tab\there", "~._-*'()!$,;:@/?#[]",
            "\u{fc}n\u{ef}c\u{f8}d\u{e9}", "\u{1F642}", "\"quoted\"\\",
        ]
        let log = RequestLog()
        let session = makeStubSession { request in
            log.record(request)
            return StubReply.entraToken(request, "entra-token")
        }

        for secret in secrets {
            let provider = AzureServicePrincipalCredentialProvider(
                principal: AzureServicePrincipal(tenantId: "tenant-1", clientId: "client+1", clientSecret: secret),
                session: session)
            _ = try await provider.accessToken(scope: scope)
        }

        XCTAssertEqual(log.count, secrets.count)
        for (sent, secret) in zip(log.all, secrets) {
            let fields = FormDecoding.fields(sent.body)
            XCTAssertEqual(fields["client_secret"], secret, secret)
            XCTAssertEqual(fields["client_id"], "client+1", secret)
            XCTAssertEqual(fields["scope"], scope, secret)
            XCTAssertFalse(sent.bodyText.contains("+"), sent.bodyText)
            XCTAssertFalse(sent.bodyText.contains(" "), sent.bodyText)
        }
    }

    func testTheEncodingLeavesOnlyUnreservedCharactersAlone() {
        XCTAssertEqual(FormURLEncoding.encode("aZ09-._~"), "aZ09-._~")
        XCTAssertEqual(FormURLEncoding.encode("a+b c&d=e%f"), "a%2Bb%20c%26d%3De%25f")
        XCTAssertEqual(FormURLEncoding.encode("\u{e9}"), "%C3%A9")
    }

    func testAnInvalidTenantIsRefusedBeforeAnythingIsSent() async throws {
        let log = RequestLog()
        let provider = makeProvider(tenantId: "tenant/../other") { request in
            log.record(request)
            return StubReply.entraToken(request, "never")
        }

        do {
            _ = try await provider.accessToken(scope: scope)
            XCTFail("Expected an invalid tenant")
        } catch let error as AzureCredentialError {
            XCTAssertEqual(error, .tenantInvalid)
        }
        XCTAssertEqual(log.count, 0)
    }

    /// Microsoft warns that credentials which are not reused draw HTTP 429 throttling from Entra.
    func testTheTokenIsReusedUntilAMinuteBeforeItExpires() async throws {
        let log = RequestLog()
        let clock = TestClock()
        let provider = makeProvider(clock: clock) { request in
            log.record(request)
            return StubReply.entraToken(request, "token-\(log.count)", expiresIn: 3600)
        }

        let first = try await provider.accessToken(scope: scope)
        clock.advance(by: 3539)
        let reused = try await provider.accessToken(scope: scope)
        clock.advance(by: 1)
        let renewed = try await provider.accessToken(scope: scope)

        XCTAssertEqual(first.token, "token-1")
        XCTAssertEqual(reused.token, "token-1")
        XCTAssertEqual(renewed.token, "token-2")
        XCTAssertEqual(log.count, 2)
    }

    func testConcurrentRequestsShareOneTokenRequest() async throws {
        let log = RequestLog()
        let provider = makeProvider { request in
            log.record(request)
            return StubReply.entraToken(request, "shared-token")
        }

        let scope = self.scope
        async let first = provider.accessToken(scope: scope)
        async let second = provider.accessToken(scope: scope)
        let tokens = try await [first, second]

        XCTAssertEqual(tokens.map(\.token), ["shared-token", "shared-token"])
        XCTAssertEqual(log.count, 1)
    }

    /// Entra's description repeats the app and tenant ids and carries trace ids; only the code and the number stay.
    func testARefusalKeepsTheCodeAndTheNumberButNotTheDescription() async throws {
        let body = #"{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided for app 'app-id-9'. Trace ID: trace-7","error_codes":[7000215]}"#
        let provider = makeProvider { request in StubReply.json(request, status: 401, body) }

        do {
            _ = try await provider.accessToken(scope: scope)
            XCTFail("Expected a refusal")
        } catch let error as AzureCredentialError {
            XCTAssertEqual(
                error,
                .tokenRejected(status: 401, aadsts: 7_000_215, reply: CleanupServiceReply(code: "invalid_client", message: nil)))
            let description = try XCTUnwrap(error.errorDescription)
            XCTAssertTrue(description.contains("AADSTS7000215"), description)
            XCTAssertTrue(description.contains("Value, not its Secret ID"), description)
            XCTAssertFalse(description.contains("app-id-9"), description)
            XCTAssertFalse(description.contains("trace-7"), description)
            let shape = FailureShape(error).description
            XCTAssertTrue(shape.contains("http=401 service=AADSTS7000215"), shape)
        }
    }

    func testAnUnreachableEntraKeepsOnlyTheURLErrorCode() async throws {
        let provider = makeProvider { _ in throw URLError(.notConnectedToInternet) }

        do {
            _ = try await provider.accessToken(scope: scope)
            XCTFail("Expected Entra to be unreachable")
        } catch let error as AzureCredentialError {
            XCTAssertEqual(error, .tokenEndpointUnreachable(URLError(.notConnectedToInternet)))
        }
    }

    func testAnAnswerWithoutATokenIsUnreadable() async throws {
        let provider = makeProvider { request in StubReply.json(request, "{}") }

        do {
            _ = try await provider.accessToken(scope: scope)
            XCTFail("Expected an unreadable answer")
        } catch let error as AzureCredentialError {
            XCTAssertEqual(error, .tokenResponseUnparseable)
        }
    }
}
