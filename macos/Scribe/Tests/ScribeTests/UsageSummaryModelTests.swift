import XCTest

@testable import Scribe

/// What the Usage Insights summary model reads and sends, held in the test. `cleanupEnabled` is read through the
/// same closure the model uses for the tray's AI Cleanup switch.
@MainActor
final class UsageSummaryBackingFake {
    let center = NotificationCenter()
    var cleanupEnabled: Bool
    private(set) var sentPayloads: [String] = []

    init(cleanupEnabled: Bool) {
        self.cleanupEnabled = cleanupEnabled
    }

    func record(_ payload: String) {
        sentPayloads.append(payload)
    }

    /// Turns cleanup on or off the way the tray does: a stored change, then the defaults notification.
    func setCleanupEnabledFromTray(_ enabled: Bool) {
        cleanupEnabled = enabled
        center.post(name: UserDefaults.didChangeNotification, object: nil)
    }
}

private struct UnfinishedProviderSetup: LocalizedError {
    var errorDescription: String? {
        "Cleanup provider not configured: Set the endpoint URL and model in Settings > AI Cleanup."
    }
}

final class UsageSummaryModelTests: XCTestCase {
    func testTheProductionSummaryFitsItsAnswerToEachLocalAppsReportedContext() async throws {
        let payload = "Dictations: 3"
        let context =
            ContextBudget.chatTemplateTokens + 64 + TokenEstimate.vocabulary(UsageInsight.systemPrompt)
            + TokenEstimate.transcript(payload) + 700
        for app in [LocalServerApp.ollama, .lmStudio] {
            let fixture = makeCleanupStore()
            fixture.store.isEnabled = true
            fixture.store.providerKind = .openAICompatible
            fixture.store.openAIBaseURL =
                app == .ollama ? LocalAiServer.ollamaAddress : LocalAiServer.lmStudioAddress
            fixture.store.openAIModel = "model"
            fixture.store.selectedLocalApp = app
            let requests = RequestLog()
            let cache = CleanupProviderCache(
                store: fixture.store, environment: [:],
                factory: .testing(
                    session: makeStubSession { request in
                        requests.record(request)
                        return StubReply.completion(request, "A summary.")
                    },
                    readLocalServer: { _, _ in
                        LocalServerState(
                            reach: .reached, models: [],
                            loaded: [LocalServerLoadedModel("model", 0, contextTokens: context)])
                    }))
            let summary = try await UsageSummaryModel.summarizeWithConfiguredProvider(payload, cache: cache)
            XCTAssertEqual(summary, "A summary.")
            let sent = try XCTUnwrap(requests.all.first)
            XCTAssertEqual(requests.count, 1)
            XCTAssertEqual(sent.messageContents, [UsageInsight.systemPrompt, payload])
            XCTAssertEqual(sent.jsonBody["max_completion_tokens"] as? Int, 700)
            XCTAssertEqual(sent.jsonBody["max_tokens"] as? Int, 700)
        }
    }

    func testTheProductionSummarySendsNothingWhenItsMinimumAnswerCannotFit() async throws {
        let fixture = makeCleanupStore()
        fixture.store.isEnabled = true
        fixture.store.providerKind = .openAICompatible
        fixture.store.openAIBaseURL = LocalAiServer.ollamaAddress
        fixture.store.openAIModel = "model"
        fixture.store.selectedLocalApp = .ollama
        let requests = RequestLog()
        let cache = CleanupProviderCache(
            store: fixture.store, environment: [:],
            factory: .testing(
                session: makeStubSession { request in
                    requests.record(request)
                    return StubReply.completion(request, "Unused.")
                },
                readLocalServer: { _, _ in
                    LocalServerState(
                        reach: .reached, models: [],
                        loaded: [LocalServerLoadedModel("model", 0, contextTokens: 512)])
                }))
        do {
            _ = try await UsageSummaryModel.summarizeWithConfiguredProvider("Dictations: 3", cache: cache)
            XCTFail("The minimum useful answer must fit before sending a payload")
        } catch {
            XCTAssertEqual(error as? CleanupProviderError, .localRequestTooLarge)
        }
        XCTAssertEqual(requests.count, 0)
    }

    func testTheProductionSummaryBoundsOnlyRecognizedLocalAppOutput() async throws {
        for localApp in [true, false] {
            let fixture = makeCleanupStore()
            fixture.store.isEnabled = true
            fixture.store.providerKind = .openAICompatible
            fixture.store.openAIBaseURL =
                localApp ? LocalAiServer.ollamaAddress : "https://remote.example/v1"
            fixture.store.openAIModel = "model"
            fixture.store.selectedLocalApp = localApp ? .ollama : .none
            let requests = RequestLog()
            let factory = CleanupProviderFactory.testing(
                session: makeStubSession { request in
                    requests.record(request)
                    return StubReply.completion(request, "A summary.")
                },
                readLocalServer: { _, _ in
                    LocalServerState(
                        reach: .reached, models: [],
                        loaded: [LocalServerLoadedModel("model", 0, contextTokens: 4096)])
                })
            let cache = CleanupProviderCache(store: fixture.store, environment: [:], factory: factory)
            let text = try await UsageSummaryModel.summarizeWithConfiguredProvider(
                "Dictations: 3", cache: cache)
            XCTAssertEqual(text, "A summary.")
            XCTAssertEqual(requests.count, 1)
            let sent = try XCTUnwrap(requests.all.first)
            XCTAssertEqual(sent.messageContents, [UsageInsight.systemPrompt, "Dictations: 3"])
            XCTAssertEqual(sent.jsonBody["max_completion_tokens"] as? Int, localApp ? 1024 : nil)
            XCTAssertEqual(sent.jsonBody["max_tokens"] as? Int, localApp ? 1024 : nil)
        }
    }

    @MainActor
    private func makeModel(
        _ backing: UsageSummaryBackingFake,
        summarize: @escaping @Sendable (String) async throws -> String
    ) -> UsageSummaryModel {
        UsageSummaryModel(
            readCleanupEnabled: { backing.cleanupEnabled },
            summarize: summarize,
            center: backing.center)
    }

    @MainActor
    func testNothingIsSentWhileCleanupIsOff() async {
        let backing = UsageSummaryBackingFake(cleanupEnabled: false)
        let model = makeModel(backing) { payload in
            await backing.record(payload)
            return "A summary."
        }

        XCTAssertFalse(model.canGenerate)
        model.generate(payload: "Dictations: 3")
        await model.inFlight?.value

        XCTAssertNil(model.inFlight)
        XCTAssertEqual(backing.sentPayloads, [])
        XCTAssertNil(model.summary)
    }

    @MainActor
    func testTurningCleanupOnFromTheTrayEnablesTheButton() {
        let backing = UsageSummaryBackingFake(cleanupEnabled: false)
        let model = makeModel(backing) { _ in "A summary." }

        backing.setCleanupEnabledFromTray(true)

        XCTAssertTrue(model.isCleanupEnabled)
        XCTAssertTrue(model.canGenerate)
    }

    @MainActor
    func testASuccessfulReplyIsShown() async {
        let backing = UsageSummaryBackingFake(cleanupEnabled: true)
        let model = makeModel(backing) { payload in
            await backing.record(payload)
            return "  You dictate mostly about Azure.  "
        }

        model.generate(payload: "Dictations: 3")
        await model.inFlight?.value

        XCTAssertEqual(backing.sentPayloads, ["Dictations: 3"])
        XCTAssertEqual(model.summary, "You dictate mostly about Azure.")
        XCTAssertNil(model.errorMessage)
        XCTAssertFalse(model.isGenerating)
    }

    /// The fatal resolver used to crash the app here; an unfinished provider setup is now a message on the tab,
    /// and the last good summary stays.
    @MainActor
    func testAProviderErrorIsShownAndKeepsTheLastSummary() async {
        let backing = UsageSummaryBackingFake(cleanupEnabled: true)
        let failing = SettingsTestCounter()
        let model = makeModel(backing) { _ in
            if await failing.count > 0 {
                throw UnfinishedProviderSetup()
            }
            await failing.increment()
            return "First summary."
        }
        model.generate(payload: "Dictations: 3")
        await model.inFlight?.value
        XCTAssertEqual(model.summary, "First summary.")

        model.generate(payload: "Dictations: 4")
        await model.inFlight?.value

        XCTAssertEqual(model.summary, "First summary.")
        XCTAssertEqual(model.errorMessage, UnfinishedProviderSetup().errorDescription)
        XCTAssertFalse(model.isGenerating)
        XCTAssertTrue(model.canGenerate)
    }

    @MainActor
    func testAnEmptyReplyIsReportedWithoutClearingTheSummary() async {
        let backing = UsageSummaryBackingFake(cleanupEnabled: true)
        let replies = SettingsTestCounter()
        let model = makeModel(backing) { _ in
            let earlier = await replies.count
            await replies.increment()
            return earlier == 0 ? "First summary." : "   "
        }
        model.generate(payload: "Dictations: 3")
        await model.inFlight?.value

        model.generate(payload: "Dictations: 4")
        await model.inFlight?.value

        XCTAssertEqual(model.summary, "First summary.")
        XCTAssertEqual(model.errorMessage, "The AI provider returned no usable summary.")
    }

    /// A reply for the period the tab showed before the change must not appear under the new period.
    @MainActor
    func testAReplyThatArrivesAfterThePeriodChangedIsDropped() async {
        let backing = UsageSummaryBackingFake(cleanupEnabled: true)
        let gate = SettingsTestGate()
        let model = makeModel(backing) { _ in
            await gate.pass()
            return "A summary of the old period."
        }

        model.generate(payload: "Dictations: 3")
        let running = model.inFlight
        await gate.waitForArrival()
        XCTAssertTrue(model.isGenerating)

        model.reset()
        XCTAssertFalse(model.isGenerating)
        await gate.open()
        await running?.value

        XCTAssertNil(model.summary)
        XCTAssertNil(model.errorMessage)
        XCTAssertFalse(model.isGenerating)
    }

    @MainActor
    func testTurningCleanupOffMidRequestDropsTheReply() async {
        let backing = UsageSummaryBackingFake(cleanupEnabled: true)
        let gate = SettingsTestGate()
        let model = makeModel(backing) { _ in
            await gate.pass()
            return "A summary."
        }

        model.generate(payload: "Dictations: 3")
        let running = model.inFlight
        await gate.waitForArrival()

        backing.setCleanupEnabledFromTray(false)
        await gate.open()
        await running?.value

        XCTAssertNil(model.summary)
        XCTAssertFalse(model.isGenerating)
        XCTAssertFalse(model.canGenerate)
    }

    /// The summary is model output like a cleaned dictation, so the house style's dash rule holds for it too.
    @MainActor
    func testADashInTheReplyIsRewritten() async {
        let backing = UsageSummaryBackingFake(cleanupEnabled: true)
        let model = makeModel(backing) { _ in "You dictate about Azure \u{2014} mostly in the morning." }

        model.generate(payload: "Dictations: 3")
        await model.inFlight?.value

        XCTAssertEqual(model.summary, "You dictate about Azure, mostly in the morning.")
    }

    @MainActor
    func testASecondRequestWhileOneIsRunningIsNotStarted() async {
        let backing = UsageSummaryBackingFake(cleanupEnabled: true)
        let gate = SettingsTestGate()
        let model = makeModel(backing) { payload in
            await backing.record(payload)
            await gate.pass()
            return "A summary."
        }

        model.generate(payload: "first")
        let running = model.inFlight
        await gate.waitForArrival()
        model.generate(payload: "second")
        await gate.open()
        await running?.value

        XCTAssertEqual(backing.sentPayloads, ["first"])
        XCTAssertEqual(model.summary, "A summary.")
    }
}
