import XCTest
@testable import Scribe

final class AzureCliAccessTokenParserTests: XCTestCase {
    func testParsesExpiresOnStringFormat() {
        let json = """
        {"accessToken":"tok-abc","expiresOn":"2099-01-01 12:00:00.000000","subscription":"sub","tenant":"ten","tokenType":"Bearer"}
        """.data(using: .utf8)!

        let token = AzureCliAccessTokenParser.parse(json)
        XCTAssertEqual(token?.token, "tok-abc")
        XCTAssertNotNil(token?.expiresAt)
        XCTAssertGreaterThan(token!.expiresAt, Date())
    }

    func testParsesExpiresOnEpochFormat() {
        let futureEpoch = Int(Date().addingTimeInterval(3600).timeIntervalSince1970)
        let json = """
        {"accessToken":"tok-epoch","expires_on":\(futureEpoch)}
        """.data(using: .utf8)!

        let token = AzureCliAccessTokenParser.parse(json)
        XCTAssertEqual(token?.token, "tok-epoch")
        XCTAssertGreaterThan(token!.expiresAt, Date())
    }

    func testFallsBackToConservativeExpiryWhenDateUnparseable() {
        let json = """
        {"accessToken":"tok-noexpiry"}
        """.data(using: .utf8)!

        let token = AzureCliAccessTokenParser.parse(json)
        XCTAssertEqual(token?.token, "tok-noexpiry")
        XCTAssertNotNil(token?.expiresAt)
    }

    func testReturnsNilWhenAccessTokenMissing() {
        let json = """
        {"subscription":"sub"}
        """.data(using: .utf8)!

        XCTAssertNil(AzureCliAccessTokenParser.parse(json))
    }

    func testReturnsNilForInvalidJSON() {
        let json = "not json".data(using: .utf8)!
        XCTAssertNil(AzureCliAccessTokenParser.parse(json))
    }
}

final class AzureServicePrincipalTests: XCTestCase {
    func testTryCreateSucceedsWhenAllFieldsPresent() {
        let principal = AzureServicePrincipal.tryCreate(
            tenantId: "tenant-1", clientId: "client-1", clientSecret: "secret-1")
        XCTAssertNotNil(principal)
        XCTAssertEqual(principal?.tenantId, "tenant-1")
        XCTAssertEqual(principal?.clientId, "client-1")
        XCTAssertEqual(principal?.clientSecret, "secret-1")
    }

    func testTryCreateFailsWhenTenantMissing() {
        XCTAssertNil(AzureServicePrincipal.tryCreate(tenantId: nil, clientId: "client-1", clientSecret: "secret-1"))
    }

    func testTryCreateFailsWhenClientIdBlank() {
        XCTAssertNil(AzureServicePrincipal.tryCreate(tenantId: "tenant-1", clientId: "  ", clientSecret: "secret-1"))
    }

    func testTryCreateFailsWhenSecretMissing() {
        XCTAssertNil(AzureServicePrincipal.tryCreate(tenantId: "tenant-1", clientId: "client-1", clientSecret: nil))
    }

    func testTryCreateFailsWhenSecretEmpty() {
        XCTAssertNil(AzureServicePrincipal.tryCreate(tenantId: "tenant-1", clientId: "client-1", clientSecret: ""))
    }
}

final class AzureServicePrincipalCredentialProviderTests: XCTestCase {
    private func stubbedSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: configuration)
    }

    override func tearDown() {
        StubURLProtocol.responseProvider = nil
        super.tearDown()
    }

    func testAccessTokenPostsClientCredentialsAndParsesResponse() async throws {
        StubURLProtocol.responseProvider = { request in
            XCTAssertEqual(request.url?.host, "login.microsoftonline.com")
            XCTAssertEqual(request.url?.path, "/tenant-1/oauth2/v2.0/token")
            XCTAssertEqual(request.httpMethod, "POST")

            let bodyText = request.httpBodyText
            XCTAssertTrue(bodyText?.contains("grant_type=client_credentials") == true)
            XCTAssertTrue(bodyText?.contains("client_id=client-1") == true)
            XCTAssertTrue(bodyText?.contains("client_secret=secret-1") == true)

            let body = """
            {"access_token":"entra-token","expires_in":3600,"token_type":"Bearer"}
            """.data(using: .utf8)!
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, body)
        }

        let principal = AzureServicePrincipal(tenantId: "tenant-1", clientId: "client-1", clientSecret: "secret-1")
        let provider = AzureServicePrincipalCredentialProvider(principal: principal, session: stubbedSession())

        let token = try await provider.accessToken(scope: "https://cognitiveservices.azure.com/.default")
        XCTAssertEqual(token.token, "entra-token")
        XCTAssertGreaterThan(token.expiresAt, Date())
    }

    func testAccessTokenThrowsOnNonSuccessStatus() async {
        StubURLProtocol.responseProvider = { request in
            let body = """
            {"error":"invalid_client","error_description":"bad secret"}
            """.data(using: .utf8)!
            let response = HTTPURLResponse(url: request.url!, statusCode: 401, httpVersion: nil, headerFields: nil)!
            return (response, body)
        }

        let principal = AzureServicePrincipal(tenantId: "tenant-1", clientId: "client-1", clientSecret: "wrong")
        let provider = AzureServicePrincipalCredentialProvider(principal: principal, session: stubbedSession())

        do {
            _ = try await provider.accessToken(scope: "https://cognitiveservices.azure.com/.default")
            XCTFail("Expected a token request failure")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("invalid_client"))
        }
    }

    func testAccessTokenIsCachedUntilExpiry() async throws {
        var requestCount = 0
        StubURLProtocol.responseProvider = { request in
            requestCount += 1
            let body = """
            {"access_token":"cached-token","expires_in":3600}
            """.data(using: .utf8)!
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, body)
        }

        let principal = AzureServicePrincipal(tenantId: "tenant-1", clientId: "client-1", clientSecret: "secret-1")
        let provider = AzureServicePrincipalCredentialProvider(principal: principal, session: stubbedSession())

        _ = try await provider.accessToken(scope: "https://cognitiveservices.azure.com/.default")
        _ = try await provider.accessToken(scope: "https://cognitiveservices.azure.com/.default")

        XCTAssertEqual(requestCount, 1, "second call within expiry should reuse the cached token")
    }
}

final class MicrosoftFoundryCleanupProviderTests: XCTestCase {
    private func stubbedSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: configuration)
    }

    override func tearDown() {
        StubURLProtocol.responseProvider = nil
        super.tearDown()
    }

    private struct StubCredentialProvider: AzureCredentialProvider {
        let token: String
        func accessToken(scope: String) async throws -> AzureAccessToken {
            AzureAccessToken(token: token, expiresAt: Date().addingTimeInterval(3600))
        }
    }

    func testCleanCallsChatCompletionsEndpointWithBearerToken() async throws {
        StubURLProtocol.responseProvider = { request in
            XCTAssertEqual(request.url?.path, "/openai/deployments/gpt-4o-mini/chat/completions")
            XCTAssertTrue(request.url?.query?.contains("api-version=") == true)
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer entra-token")

            let body = """
            {"choices":[{"message":{"content":"Cleaned text."}}]}
            """.data(using: .utf8)!
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, body)
        }

        let provider = MicrosoftFoundryCleanupProvider(
            endpoint: URL(string: "https://my-resource.cognitiveservices.azure.com")!,
            deployment: "gpt-4o-mini",
            credentialProvider: StubCredentialProvider(token: "entra-token"),
            session: stubbedSession())

        let response = try await provider.clean(CleanupRequest(transcript: "raw text"))
        XCTAssertEqual(response.cleanedText, "Cleaned text.")
        XCTAssertEqual(response.modelID, "gpt-4o-mini")
    }

    func testCleanSurfacesRoleAndPropagationHintsOn401() async {
        StubURLProtocol.responseProvider = { request in
            let response = HTTPURLResponse(url: request.url!, statusCode: 401, httpVersion: nil, headerFields: nil)!
            return (response, "unauthorized".data(using: .utf8)!)
        }

        let provider = MicrosoftFoundryCleanupProvider(
            endpoint: URL(string: "https://my-resource.cognitiveservices.azure.com")!,
            deployment: "gpt-4o-mini",
            credentialProvider: StubCredentialProvider(token: "entra-token"),
            session: stubbedSession())

        do {
            _ = try await provider.clean(CleanupRequest(transcript: "raw text"))
            XCTFail("Expected a request failure")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("propagat"))
            XCTAssertTrue(error.localizedDescription.contains("custom subdomain"))
        }
    }
}

final class KeychainStoreTests: XCTestCase {
    // A dedicated service name (distinct from the real Microsoft Foundry secret's service) so
    // this test can never collide with, or leave residue inside, a user's genuine credential.
    private let service = "com.scribe.macos.tests.keychain-store"
    private let account = "unit-test-account"

    override func tearDown() {
        try? KeychainStore.delete(service: service, account: account)
        super.tearDown()
    }

    func testSetThenGetRoundTrips() throws {
        try KeychainStore.set("s3cr3t-value", service: service, account: account)
        let fetched = try KeychainStore.get(service: service, account: account)
        XCTAssertEqual(fetched, "s3cr3t-value")
    }

    func testGetReturnsNilWhenNothingStored() throws {
        let fetched = try KeychainStore.get(service: service, account: "no-such-account")
        XCTAssertNil(fetched)
    }

    func testSetOverwritesPreviousValue() throws {
        try KeychainStore.set("first", service: service, account: account)
        try KeychainStore.set("second", service: service, account: account)
        let fetched = try KeychainStore.get(service: service, account: account)
        XCTAssertEqual(fetched, "second")
    }

    func testDeleteRemovesStoredValue() throws {
        try KeychainStore.set("to-delete", service: service, account: account)
        try KeychainStore.delete(service: service, account: account)
        let fetched = try KeychainStore.get(service: service, account: account)
        XCTAssertNil(fetched)
    }

    func testDeleteIsIdempotentWhenNothingStored() throws {
        try KeychainStore.delete(service: service, account: "already-absent")
    }
}

private extension URLRequest {
    var httpBodyText: String? {
        if let body = httpBody {
            return String(data: body, encoding: .utf8)
        }
        guard let stream = httpBodyStream else { return nil }
        stream.open()
        defer { stream.close() }
        var data = Data()
        let bufferSize = 4096
        var buffer = [UInt8](repeating: 0, count: bufferSize)
        while stream.hasBytesAvailable {
            let read = stream.read(&buffer, maxLength: bufferSize)
            if read > 0 {
                data.append(buffer, count: read)
            } else {
                break
            }
        }
        return String(data: data, encoding: .utf8)
    }
}
