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
    /// sees. Entra token requests get a token; everything else gets `reply`, which a trailing closure sets.
    /// `checkDeadline` replaces Test Connection's deadline and `checkTimer` the timer that waits it out, and
    /// `foundryStatus` and `azureCliLaunch` replace the fakes for one test. Every parameter that takes a closure comes
    /// after `reply`, so a trailing closure can only ever be `reply`.
    private func makeRig(
        environment: [String: String] = [:],
        apiKeys: InMemorySecretStore = InMemorySecretStore(),
        clientSecrets: InMemorySecretStore = InMemorySecretStore(),
        checkDeadline: Duration? = nil,
        reply: @escaping @Sendable (URLRequest) throws -> (HTTPURLResponse, Data) = {
            StubReply.completion($0, "Cleaned.")
        },
        checkTimer: (@Sendable (Duration) async throws -> Void)? = nil,
        foundryStatus: FoundryLocalStatusSource? = nil,
        azureCliLaunch: AzureCliCredentialProvider.Launch? = nil
    ) throws -> Rig {
        let requests = RequestLog()
        let entraHost = Self.entraHost
        let session = makeStubSession { request in
            requests.record(request)
            if request.url?.host(percentEncoded: false) == entraHost {
                return StubReply.entraToken(request, "entra-token-\(requests.count(host: entraHost))")
            }
            return try reply(request)
        }
        let clock = TestClock()
        let azureCli = FakeAzureCli(outcomes: [
            .azToken("cli-token", expiresOn: Int(clock.date.timeIntervalSince1970) + 3600)
        ])
        let fakeFoundryStatus = FakeFoundryStatus(endpoints: ["http://127.0.0.1:5001"])
        let azDirectory = try makeScript(named: "az", body: "exit 1").deletingLastPathComponent()
        let fixture = makeCleanupStore(apiKeys: apiKeys, clientSecrets: clientSecrets)
        let factory = CleanupProviderFactory.testing(
            session: session, foundryStatus: foundryStatus ?? fakeFoundryStatus.source, azureCli: azureCli,
            azureCliLaunch: azureCliLaunch, azureCliSearchPath: [azDirectory.path(percentEncoded: false)], clock: clock)
        let realTimer: @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }
        let timer: @Sendable (Duration) async throws -> Void = checkTimer ?? realTimer
        let cache = CleanupProviderCache(
            store: fixture.store, environment: environment, factory: factory,
            checkDeadline: { kind in checkDeadline ?? CleanupProviderCache.checkDeadline(for: kind) },
            checkTimer: timer)
        return Rig(
            fixture: fixture, cache: cache, requests: requests, azureCli: azureCli, foundryStatus: fakeFoundryStatus,
            clock: clock)
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

    /// Test Connection, with a bound for the test itself: a check that never ends fails the test instead of hanging it.
    private func boundedCheck(
        _ cache: CleanupProviderCache, file: StaticString = #filePath, line: UInt = #line
    ) async throws -> CleanupConnectionCheck {
        let result = LockedValue<CleanupConnectionCheck>()
        await waitBounded("Test Connection to end", file: file, line: line) {
            result.set(await cache.checkConnection())
        }
        return try XCTUnwrap(result.value, file: file, line: line)
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
        XCTAssertEqual(
            rig.requests.all.map { $0.jsonBody["model"] as? String }, ["deployment-a", "deployment-a", "deployment-b"])
        XCTAssertEqual(
            rig.requests.all.map { $0.header("Authorization") }, Array(repeating: "Bearer cli-token", count: 3))
    }

    func testANewTenantGetsANewCredential() async throws {
        let rig = try makeRig()
        configureMicrosoftFoundry(rig.store)

        try await clean(rig)
        rig.store.azureTenantId = "contoso.onmicrosoft.com"
        try await clean(rig)

        XCTAssertEqual(rig.azureCli.launches, 2)
        XCTAssertEqual(
            rig.azureCli.commands.last.map { Array($0.arguments.suffix(2)) }, ["--tenant", "contoso.onmicrosoft.com"])
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
        XCTAssertEqual(
            tokenRequests.map { FormDecoding.fields($0.body)["client_secret"] }, ["secret-old", "secret-new"])
        XCTAssertEqual(
            rig.requests.all.filter { $0.host != Self.entraHost }.map { $0.header("Authorization") },
            ["Bearer entra-token-1", "Bearer entra-token-1", "Bearer entra-token-2"])
        XCTAssertEqual(rig.fixture.clientSecrets.reads, 2)
    }

    /// A secret an earlier build saved under the client id exactly as configured, surrounding whitespace included,
    /// still signs in, whether the id comes from Settings or from the environment, and it moves to the trimmed id.
    func testASecretSavedUnderAnUntrimmedClientIdStillSignsIn() async throws {
        let settings = try makeRig(clientSecrets: InMemorySecretStore([" client-1 ": "legacy-secret"]))
        configureMicrosoftFoundry(settings.store)
        settings.store.azureAuthMode = .servicePrincipal
        settings.store.azureTenantId = "tenant-1"
        settings.store.azureClientId = " client-1 "
        let environment = try makeRig(
            environment: [
                "SCRIBE_CLEANUP_PROVIDER": "microsoft-foundry",
                "SCRIBE_AZURE_FOUNDRY_ENDPOINT": "https://my-res.services.ai.azure.com",
                "SCRIBE_AZURE_FOUNDRY_DEPLOYMENT": "gpt-5-mini",
                "SCRIBE_AZURE_AUTH_MODE": "service-principal",
                "SCRIBE_AZURE_TENANT_ID": "tenant-1",
                "SCRIBE_AZURE_CLIENT_ID": "client-1\n",
            ],
            clientSecrets: InMemorySecretStore(["client-1\n": "legacy-secret"]))

        for rig in [settings, environment] {
            try await clean(rig)

            let tokenRequests = rig.requests.all.filter { $0.host == Self.entraHost }
            XCTAssertEqual(tokenRequests.map { FormDecoding.fields($0.body)["client_secret"] }, ["legacy-secret"])
            XCTAssertEqual(tokenRequests.map { FormDecoding.fields($0.body)["client_id"] }, ["client-1"])
            XCTAssertEqual(rig.fixture.clientSecrets.secrets, ["client-1": "legacy-secret"])
        }
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
                XCTAssertEqual(
                    $0 as? CleanupProviderError, .notConfigured(.azureClientSecretMissing, source: .settings))
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
        XCTAssertTrue(
            check.message.hasPrefix("OpenAI-compatible endpoint is connected: the model answered the test in "),
            check.message)
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
                #"{"error":{"code":"DeploymentNotFound","#
                    + #""message":"The API deployment for this resource does not exist."}}"#
            )
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
        XCTAssertEqual(CleanupProviderCache.checkDeadline(for: .foundryLocal), .seconds(180))
        XCTAssertEqual(CleanupProviderCache.checkDeadline(for: .ollama), .seconds(180))
        XCTAssertEqual(CleanupProviderCache.checkDeadline(for: .openAICompatible), .seconds(90))
        XCTAssertEqual(CleanupProviderCache.checkDeadline(for: .microsoftFoundry), .seconds(90))
    }

    /// No request through the cleanup session may run for the session default of seven days, however slowly an
    /// answer trickles in.
    func testNoCleanupRequestCanRunForDays() {
        XCTAssertEqual(CleanupProviderFactory.cleanupSession.configuration.timeoutIntervalForResource, 300)
        XCTAssertGreaterThan(
            CleanupProviderFactory.requestCeiling,
            ChatCompletionsTransport.seconds(CleanupProviderCache.checkDeadline(for: .foundryLocal)))
    }

    // MARK: - Test Connection's deadline

    /// The deadline covers getting the token as well as the completion: an `az` that never answers ends the check at
    /// the deadline, and the launch sees the cancellation that stops a real `az`.
    func testTheCheckEndsAtItsDeadlineWhileAzHasNotAnswered() async throws {
        let held = HeldWork()
        let rig = try makeRig(
            checkDeadline: .milliseconds(200),
            azureCliLaunch: { _ in
                try await held.hold()
                throw CancellationError()
            })
        configureMicrosoftFoundry(rig.store)

        let started = ContinuousClock.now
        let check = try await boundedCheck(rig.cache)

        XCTAssertFalse(check.reachable)
        XCTAssertTrue(check.message.hasPrefix("Microsoft Foundry did not finish the test within"), check.message)
        XCTAssertTrue(held.sawCancellation, "the az launch was cancelled")
        XCTAssertGreaterThanOrEqual(started.duration(to: .now), .milliseconds(200))
        XCTAssertEqual(rig.requests.count, 0)
    }

    /// Finding Foundry Local's endpoint is inside the deadline too.
    func testTheCheckEndsAtItsDeadlineWhileFoundryLocalHasNotSaidWhereItIs() async throws {
        let held = HeldWork()
        let rig = try makeRig(
            checkDeadline: .milliseconds(200),
            foundryStatus: FoundryLocalStatusSource {
                try await held.hold()
                throw CancellationError()
            })

        let check = try await boundedCheck(rig.cache)

        XCTAssertFalse(check.reachable)
        XCTAssertTrue(check.message.hasPrefix("Foundry Local did not finish the test within"), check.message)
        XCTAssertTrue(check.message.hasSuffix("try again once it has loaded."), check.message)
        XCTAssertTrue(held.sawCancellation, "the foundry status lookup was cancelled")
    }

    /// A completion that never arrives, which an idle timeout alone would wait out for as long as data keeps
    /// trickling in, ends at the deadline, and `URLSession` stops the load.
    func testTheCheckEndsAtItsDeadlineWhileTheCompletionHasNotArrived() async throws {
        let stopped = FirstOutcome()
        let rig = try makeRig(checkDeadline: .milliseconds(200)) { _ in
            throw StubURLProtocol.Hold(stopped: { stopped.settle(true) })
        }
        configureOpenAICompatible(rig.store)

        let check = try await boundedCheck(rig.cache)

        XCTAssertFalse(check.reachable)
        XCTAssertTrue(
            check.message.hasPrefix("OpenAI-compatible endpoint did not finish the test within"), check.message)
        await waitBounded("URLSession to stop the held request") { _ = await stopped.value }
    }

    /// Cancelling the check is not a deadline, and says so.
    func testACancelledCheckSaysItWasCancelled() async throws {
        let held = HeldWork()
        let rig = try makeRig(azureCliLaunch: { _ in
            try await held.hold()
            throw CancellationError()
        })
        configureMicrosoftFoundry(rig.store)
        let cache = rig.cache

        let checking = Task { await cache.checkConnection() }
        await waitBounded("az to be launched") { await held.waitUntilStarted() }
        checking.cancel()
        let result = LockedValue<CleanupConnectionCheck>()
        await waitBounded("the cancelled check to end") { result.set(await checking.value) }
        let check = try XCTUnwrap(result.value)

        XCTAssertFalse(check.reachable)
        XCTAssertEqual(check.message, "Microsoft Foundry: The check was cancelled.")
        XCTAssertTrue(held.sawCancellation)
    }

    // MARK: - Test Connection's output ceiling

    /// Windows' readiness probe ceilings: 4096 for a Microsoft Foundry deployment, which may reason before it answers,
    /// and 16 for the rest. A dictation carries no ceiling.
    func testTheCheckCapsTheAnswerAsWindowsDoes() async throws {
        let cases: [(kind: CleanupProviderKind, ceiling: Int)] = [
            (.foundryLocal, 16), (.ollama, 16), (.openAICompatible, 16), (.microsoftFoundry, 4096),
        ]
        for (kind, ceiling) in cases {
            let rig = try makeRig()
            switch kind {
            case .foundryLocal:
                break
            case .ollama:
                rig.store.providerKind = .ollama
            case .openAICompatible:
                configureOpenAICompatible(rig.store)
            case .microsoftFoundry:
                configureMicrosoftFoundry(rig.store)
            }

            let check = await rig.cache.checkConnection()
            try await clean(rig)

            XCTAssertTrue(check.reachable, "\(kind): \(check.message)")
            let bodies = rig.requests.all.filter { $0.host != Self.entraHost }.map(\.jsonBody)
            XCTAssertEqual(bodies.count, 2, "\(kind)")
            XCTAssertEqual(bodies.first?["max_completion_tokens"] as? Int, ceiling, "\(kind)")
            XCTAssertNil(bodies.last?["max_completion_tokens"], "\(kind): a dictation has no ceiling")
            XCTAssertNil(bodies.first?["max_tokens"], "\(kind)")
        }
    }

    // MARK: - Invalidation across both tiers

    /// A credential made while `invalidate()` ran belongs to the time before it. The build that made it hands its
    /// provider to its caller once, but neither that provider nor the credential is kept, so the next provider reads
    /// the secret again and makes a credential of its own.
    func testACredentialMadeWhileInvalidatingIsNotKept() async throws {
        let clientSecrets = InMemorySecretStore()
        let rig = try makeRig(clientSecrets: clientSecrets)
        configureMicrosoftFoundry(rig.store)
        rig.store.azureAuthMode = .servicePrincipal
        rig.store.azureTenantId = "tenant-1"
        rig.store.azureClientId = "client-1"
        try rig.store.setAzureClientSecret("secret-1", clientId: "client-1")
        let cache = rig.cache
        let pause = clientSecrets.pauseNextRead()

        let building = Task { try await onBackgroundThread { try cache.provider() } }
        await waitBounded("the build to read the client secret") { await pause.waitUntilReached() }
        cache.invalidate()
        pause.release()
        let handedOut = try await building.value
        let next = try cache.provider()

        XCTAssertFalse(same(handedOut, next))
        XCTAssertEqual(clientSecrets.reads, 2, "the next provider made a credential of its own")
        _ = try await handedOut.clean(CleanupRequest(transcript: "raw text"))
        _ = try await next.clean(CleanupRequest(transcript: "raw text"))
        XCTAssertEqual(rig.requests.count(host: Self.entraHost), 2, "each credential asked Entra for its own token")
        XCTAssertTrue(same(next, try cache.provider()), "a build after the invalidation is kept")
    }

    // MARK: - Test Connection passes wherever dictation works

    /// A reasoning model can spend the probe's whole ceiling thinking and stop at `length` with nothing visible. One
    /// request without the ceiling then comes back with text, so the check passes, says only that the model answered,
    /// and the dictation that follows cleans with it. The confirmation carries neither field.
    func testAModelThatThoughtThroughTheWholeCeilingIsConnectedOnceItAnswersWithoutOne() async throws {
        let rig = try makeRig { request in
            RecordedRequest(request).jsonBody["max_completion_tokens"] == nil
                ? StubReply.completion(request, "Cleaned.")
                : StubReply.completion(request, nil, finishReason: "length")
        }
        configureOpenAICompatible(rig.store)

        let check = try await boundedCheck(rig.cache)
        try await clean(rig)

        XCTAssertTrue(check.reachable, check.message)
        XCTAssertTrue(
            check.message.hasPrefix("OpenAI-compatible endpoint is connected: the model answered the test in "),
            check.message)
        XCTAssertTrue(check.message.hasSuffix("so Scribe asked once more without one, and it answered."), check.message)
        XCTAssertFalse(check.message.contains("clean"), "a check claims no cleanup: \(check.message)")
        let bodies = rig.requests.all.map(\.jsonBody)
        XCTAssertEqual(bodies.count, 3, "the capped probe, its confirmation, then the dictation")
        XCTAssertEqual(bodies[0]["max_completion_tokens"] as? Int, 16)
        XCTAssertNil(bodies[1]["max_completion_tokens"])
        XCTAssertNil(bodies[1]["max_tokens"])
    }

    /// A server that stops every request at `length` with nothing visible (a deployment cap, a proxy, a model that
    /// never writes text) would fail every dictation, so it fails the check: the confirmation without a ceiling stops
    /// the same way. A confirmation refused outright fails it too. Either way there is no third request.
    func testAServerThatStopsEveryRequestAtLengthFailsTheCheck() async throws {
        let replies: [(name: String, reply: @Sendable (URLRequest) -> (HTTPURLResponse, Data))] = [
            ("always length", { request in StubReply.completion(request, nil, finishReason: "length") }),
            (
                "length, then refused",
                { request in
                    RecordedRequest(request).jsonBody["max_completion_tokens"] == nil
                        ? StubReply.json(request, status: 400, #"{"error":{"message":"refused"}}"#)
                        : StubReply.completion(request, nil, finishReason: "length")
                }
            ),
        ]
        for (name, reply) in replies {
            let rig = try makeRig(reply: reply)
            configureOpenAICompatible(rig.store)

            let check = try await boundedCheck(rig.cache)

            XCTAssertFalse(check.reachable, "\(name): \(check.message)")
            XCTAssertEqual(rig.requests.count, 2, name)
            XCTAssertNil(rig.requests.all.last?.jsonBody["max_completion_tokens"], name)
        }
        let alwaysLength = try makeRig { request in StubReply.completion(request, nil, finishReason: "length") }
        configureOpenAICompatible(alwaysLength.store)
        let check = try await boundedCheck(alwaysLength.cache)
        XCTAssertEqual(
            check.message, "OpenAI-compatible endpoint: The model reached its output limit before writing any text.")
    }

    /// An empty answer that did not stop at the ceiling, or an answer that is no completion at all, still fails, and
    /// is not retried.
    func testAnEmptyOrUnreadableAnswerStillFailsTheCheck() async throws {
        let bodies = [
            #"{"choices":[{"message":{"content":""},"finish_reason":"stop"}]}"#,
            #"{"choices":[{"message":{"content":null},"finish_reason":"content_filter"}]}"#,
            #"{"choices":[{"message":{"content":null}}]}"#,
            #"{"choices":[]}"#,
            #"{"unexpected":true}"#,
        ]
        for body in bodies {
            let rig = try makeRig { request in StubReply.json(request, body) }
            configureOpenAICompatible(rig.store)

            let check = try await boundedCheck(rig.cache)

            XCTAssertFalse(check.reachable, body)
            XCTAssertEqual(rig.requests.count, 1, body)
        }
    }

    /// After a 400 to the ceiling field, the request without a ceiling is the last one: its `length` stop with nothing
    /// visible is the server's own limit, which a dictation would meet too, so the check fails, says why, and sends no
    /// third request.
    func testALengthStopWithoutTheProbesCeilingStillFails() async throws {
        let rig = try makeRig { request in
            RecordedRequest(request).jsonBody["max_completion_tokens"] == nil
                ? StubReply.completion(request, nil, finishReason: "length")
                : StubReply.json(request, status: 400, #"{"error":{"message":"extra fields not permitted"}}"#)
        }
        configureOpenAICompatible(rig.store)

        let check = try await boundedCheck(rig.cache)

        XCTAssertFalse(check.reachable)
        XCTAssertEqual(
            check.message, "OpenAI-compatible endpoint: The model reached its output limit before writing any text.")
        XCTAssertEqual(rig.requests.count, 2)
    }

    /// A strict server that declares only `max_tokens` and forbids every other field (vLLM 0.6.0) refuses the ceiling,
    /// not the model: the check asks once more with no ceiling at all, not the older field, and passes, as a dictation,
    /// which sends none, would. The retry is logged by its shape alone.
    func testAServerThatRefusesTheCeilingFieldPassesOnTheRetryWithout() async throws {
        for status in [400, 422] {
            let rig = try makeRig { request in
                let allowed: Set<String> = ["model", "messages", "temperature", "stream", "max_tokens"]
                guard Set(RecordedRequest(request).jsonBody.keys).isSubset(of: allowed) else {
                    return StubReply.json(
                        request, status: status,
                        #"{"object":"error","message":"Extra inputs are not permitted canary-field","#
                            + #""type":"BadRequestError","code":\#(status)}"#
                    )
                }
                return StubReply.completion(request, "Cleaned.")
            }
            configureOpenAICompatible(rig.store)
            let recorder = recordScribeLog()

            let check = try await boundedCheck(rig.cache)
            recorder.stop()

            XCTAssertTrue(check.reachable, "\(status): \(check.message)")
            let bodies = rig.requests.all.map(\.jsonBody)
            XCTAssertEqual(bodies.count, 2, "\(status)")
            XCTAssertEqual(bodies.first?["max_completion_tokens"] as? Int, 16, "\(status)")
            XCTAssertNil(bodies.last?["max_completion_tokens"], "\(status)")
            XCTAssertNil(bodies.last?["max_tokens"], "\(status): the retry sends no ceiling at all")
            XCTAssertTrue(
                recorder.lines.contains { $0.contains("Test Connection retrying without an output limit") },
                "\(recorder.lines)")
            PrivacyCanary.assertAbsent(from: recorder.everyText)
        }
    }

    /// Only a 400 or 422 to a request that carried the ceiling is retried, and only once: the retry's own 400 is the
    /// answer, and a 401 or a 404 is never retried.
    func testOnlyARefusalOfTheCeilingIsRetriedAndOnlyOnce() async throws {
        for (status, sent) in [(400, 2), (422, 2), (401, 1), (404, 1)] {
            let rig = try makeRig { request in
                StubReply.json(request, status: status, #"{"error":{"message":"refused"}}"#)
            }
            configureOpenAICompatible(rig.store)

            let check = try await boundedCheck(rig.cache)

            XCTAssertFalse(check.reachable, "\(status)")
            XCTAssertEqual(rig.requests.count, sent, "\(status)")
            XCTAssertEqual(rig.requests.all.first?.jsonBody["max_completion_tokens"] as? Int, 16, "\(status)")
        }
    }

    // MARK: - The deadline covers the whole check

    /// The deadline starts with the button, before the provider is built: when it passes during the Keychain read,
    /// the check waits for the read, which cannot be interrupted, and then ends at the deadline without sending a
    /// request. The read is let go only once the deadline's cancellation has reached it, so only the checks after the
    /// build stand between it and a request: `URLSession` cannot be relied on to refuse a request made from a
    /// cancelled task, and with those checks removed it sometimes sent this one.
    func testTheDeadlineCoversBuildingTheProvider() async throws {
        let apiKeys = InMemorySecretStore([CleanupSettingsStore.openAIApiKeyAccount: "sk-test"])
        let timer = ManualTimer()
        let rig = try makeRig(
            apiKeys: apiKeys,
            reply: { _ in throw StubURLProtocol.Hold(stopped: {}) },
            checkTimer: timer.sleep)
        configureOpenAICompatible(rig.store)
        let pause = apiKeys.pauseNextRead()
        let cache = rig.cache
        let result = LockedValue<CleanupConnectionCheck>()

        let checking = Task { result.set(await cache.checkConnection()) }
        await waitBounded("the build to read the API key") { await pause.waitUntilReached() }
        await waitBounded("the deadline to be running during the read") { await timer.waitUntilStarted() }
        timer.fire()
        pause.releaseOnceCancelled()
        await waitBounded("the check to end") { await checking.value }

        let check = try XCTUnwrap(result.value)
        XCTAssertFalse(check.reachable)
        XCTAssertTrue(
            check.message.hasPrefix("OpenAI-compatible endpoint did not finish the test within"), check.message)
        XCTAssertEqual(rig.requests.count, 0, "nothing is sent after a build that outlasted the deadline")
        XCTAssertEqual(apiKeys.reads, 1)
    }

    /// A check cancelled before it began, by a Cancel pressed at once or Settings closing, builds nothing: no Keychain
    /// read, which could put up a prompt, and no request.
    func testACheckCancelledBeforeItBeganReadsNoSecretAndSendsNothing() async throws {
        let apiKeys = InMemorySecretStore([CleanupSettingsStore.openAIApiKeyAccount: "sk-test"])
        let rig = try makeRig(apiKeys: apiKeys)
        configureOpenAICompatible(rig.store)
        let cache = rig.cache
        let result = LockedValue<CleanupConnectionCheck>()

        let checking = Task {
            _ = withUnsafeCurrentTask { $0?.cancel() }
            result.set(await cache.checkConnection())
        }
        await waitBounded("the cancelled check to end") { await checking.value }

        XCTAssertEqual(result.value?.message, "OpenAI-compatible endpoint: The check was cancelled.")
        XCTAssertEqual(apiKeys.reads, 0)
        XCTAssertEqual(rig.requests.count, 0)
    }

    /// Messages about a provider that was not built use the name the provider gives itself.
    func testEveryKindIsNamedAsItsProviderNamesItself() throws {
        for kind in CleanupProviderKind.allCases {
            let rig = try makeRig()
            switch kind {
            case .foundryLocal:
                break
            case .ollama:
                rig.store.providerKind = .ollama
            case .openAICompatible:
                configureOpenAICompatible(rig.store)
            case .microsoftFoundry:
                configureMicrosoftFoundry(rig.store)
            }

            XCTAssertEqual(try rig.cache.provider().displayName, kind.providerName, "\(kind)")
        }
    }
}
