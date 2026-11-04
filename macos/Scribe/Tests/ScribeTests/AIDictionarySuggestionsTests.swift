import XCTest
import os

@testable import Scribe

@MainActor
private final class AIDictionarySuggestionBacking {
    static let foundryLocal = CleanupConnection(target: .foundryLocal(modelAlias: "local-model"), source: .settings)
    static let ollama = CleanupConnection(target: .ollama(model: "llama"), source: .settings)

    var recipient = AIDictionarySuggestionBacking.foundryLocal {
        didSet { revision &+= 1 }
    }
    var cleanupEnabled = true {
        didSet { revision &+= 1 }
    }
    var answer = #"""
        [
        {"spoken":"a p i","written":"API"},
        {"spoken":"post gres","written":"PostgreSQL"}
        ]
        """#
    private var revision: UInt64 = 0
    private(set) var sent: [(prompt: String, sample: String, recipient: CleanupConnection)] = []

    func record(prompt: String, sample: String, recipient: CleanupConnection) {
        sent.append((prompt, sample, recipient))
    }

    var service: AIDictionarySuggestionService {
        service(gate: nil)
    }

    func service(gate: SettingsTestGate?) -> AIDictionarySuggestionService {
        AIDictionarySuggestionService(admit: {
            guard self.cleanupEnabled else { return nil }
            let revision = self.revision
            let recipient = self.recipient
            return AIDictionarySuggestionAdmission(
                recipient: recipient,
                isCurrent: { self.cleanupEnabled && self.revision == revision },
                complete: { prompt, sample in
                    await self.record(prompt: prompt, sample: sample, recipient: recipient)
                    if let gate {
                        await gate.pass()
                    }
                    return await self.answer
                })
        })
    }
}

/// What the history scan was asked to do, in order: `true` when the AI service ran first.
@MainActor
private final class HistoryScanRecorder {
    private(set) var runs: [Bool] = []

    var scan: AIDictionarySuggestionModel.HistoryScan {
        { afterAI in self.runs.append(afterAI) }
    }
}

private struct HistoryUnreadable: Error {}

@MainActor
final class AIDictionarySuggestionsTests: XCTestCase {
    private static let history = ["we put the a p i behind the gateway", "post gres is slow today"]

    private func access(
        history: [String] = AIDictionarySuggestionsTests.history,
        existing: [DictionaryEntry] = [],
        committed: @escaping @Sendable ([DictionaryEntry]) async -> Void = { _ in }
    ) -> AIDictionarySuggestionAccess {
        AIDictionarySuggestionAccess(
            loadEntries: { existing },
            loadHistory: { limit in
                XCTAssertEqual(limit, AIDictionarySuggester.historyLimit)
                return history
            },
            addOrRepair: { entries in
                await committed(entries)
                return DictionarySuggestionCommit(
                    added: entries.enumerated().map { index, entry in
                        DictionaryEntry(
                            id: Int64(index + 1),
                            pattern: entry.pattern,
                            replacement: entry.replacement,
                            wholeWord: entry.wholeWord,
                            enabled: entry.enabled)
                    })
            })
    }

    private func makeModel(
        backing: AIDictionarySuggestionBacking,
        service: AIDictionarySuggestionService? = nil,
        access: AIDictionarySuggestionAccess? = nil,
        center: NotificationCenter = NotificationCenter()
    ) -> AIDictionarySuggestionModel {
        AIDictionarySuggestionModel(
            access: access ?? self.access(),
            service: service ?? backing.service,
            center: center)
    }

    /// Starts Learn from history and waits for it to settle: at the question, at the review, or after the scan.
    private func learn(_ model: AIDictionarySuggestionModel, _ scans: HistoryScanRecorder) async {
        model.learnFromHistory(scanHistory: scans.scan)
        await model.inFlight?.value
        await model.scanTask?.value
    }

    private func confirm(_ model: AIDictionarySuggestionModel) async throws {
        let consent = try XCTUnwrap(model.consent)
        model.requestSuggestions(consent)
        await model.inFlight?.value
        await model.scanTask?.value
    }

    // MARK: - Where Learn from history goes

    func testWithoutAServingAIServiceLearnFromHistoryRunsTheHistoryScan() async {
        let scans = HistoryScanRecorder()
        let unbound = AIDictionarySuggestionModel(access: access())
        XCTAssertFalse(unbound.usesAIService)
        await learn(unbound, scans)
        XCTAssertEqual(scans.runs, [false])
        XCTAssertNil(unbound.consent)

        let backing = AIDictionarySuggestionBacking()
        backing.cleanupEnabled = false
        let disabled = makeModel(backing: backing)
        XCTAssertFalse(disabled.usesAIService)
        await learn(disabled, scans)
        XCTAssertEqual(scans.runs, [false, false])
        XCTAssertTrue(backing.sent.isEmpty)
    }

    func testFoundryLocalIsNotAskedAndGetsTheNewestDistinctDictations() async {
        let backing = AIDictionarySuggestionBacking()
        let scans = HistoryScanRecorder()
        let model = makeModel(
            backing: backing, access: access(history: ["newest words", "older words", "Newest words"]))
        XCTAssertTrue(model.usesAIService)

        await learn(model, scans)

        XCTAssertNil(model.consent)
        XCTAssertEqual(backing.sent.count, 1)
        XCTAssertEqual(backing.sent[0].prompt, AIDictionarySuggestionModel.systemPrompt)
        XCTAssertEqual(backing.sent[0].sample, "newest words\nolder words")
        XCTAssertEqual(backing.sent[0].recipient, backing.recipient)
        XCTAssertEqual(model.suggestions.map(\.pattern), ["a p i", "post gres"])
        XCTAssertTrue(model.isReviewPresented)
        XCTAssertTrue(scans.runs.isEmpty)
    }

    func testAnyOtherAIServiceIsAskedFirstAndNothingIsSentUntilTheAnswerIsYes() async throws {
        let backing = AIDictionarySuggestionBacking()
        backing.recipient = AIDictionarySuggestionBacking.ollama
        let scans = HistoryScanRecorder()
        let model = makeModel(backing: backing)

        await learn(model, scans)

        let consent = try XCTUnwrap(model.consent)
        XCTAssertEqual(consent.title, CleanupDisclosure.suggestionConsentTitle)
        XCTAssertEqual(consent.message, CleanupDisclosure.suggestionConsent(for: backing.recipient.target))
        XCTAssertTrue(consent.message.contains("Ollama, which runs on this Mac"))
        XCTAssertTrue(model.isBusy)
        XCTAssertTrue(backing.sent.isEmpty)

        try await confirm(model)

        XCTAssertEqual(backing.sent.count, 1)
        XCTAssertEqual(backing.sent[0].sample, Self.history.joined(separator: "\n"))
        XCTAssertTrue(model.isReviewPresented)
    }

    func testDeclinedQuestionSendsNothing() async {
        let backing = AIDictionarySuggestionBacking()
        backing.recipient = AIDictionarySuggestionBacking.ollama
        let scans = HistoryScanRecorder()
        let model = makeModel(backing: backing)
        await learn(model, scans)

        model.cancelConsent()

        XCTAssertNil(model.consent)
        XCTAssertFalse(model.isBusy)
        XCTAssertTrue(backing.sent.isEmpty)
        XCTAssertTrue(scans.runs.isEmpty)
    }

    func testNoRecentDictationsAsksNothingAndSendsNothing() async {
        let backing = AIDictionarySuggestionBacking()
        backing.recipient = AIDictionarySuggestionBacking.ollama
        let scans = HistoryScanRecorder()
        let model = makeModel(backing: backing, access: access(history: ["  ", ""]))

        await learn(model, scans)

        XCTAssertNil(model.consent)
        XCTAssertTrue(backing.sent.isEmpty)
        XCTAssertEqual(model.statusMessage, AIDictionarySuggestionModel.nothingToLearnFrom)
        XCTAssertFalse(model.isBusy)
    }

    func testUnreadableHistorySendsNothing() async {
        let backing = AIDictionarySuggestionBacking()
        let scans = HistoryScanRecorder()
        var failing = access()
        failing.loadHistory = { _ in throw HistoryUnreadable() }
        let model = makeModel(backing: backing, access: failing)

        await learn(model, scans)

        XCTAssertTrue(backing.sent.isEmpty)
        XCTAssertEqual(model.errorMessage, AIDictionarySuggestionModel.historyUnreadable)
        XCTAssertFalse(model.isBusy)
    }

    // MARK: - The question is bound to the configuration it named

    func testRecipientChangeAfterTheQuestionRefusesBeforeSending() async throws {
        let backing = AIDictionarySuggestionBacking()
        backing.recipient = AIDictionarySuggestionBacking.ollama
        let model = makeModel(backing: backing)
        await learn(model, HistoryScanRecorder())
        let consent = try XCTUnwrap(model.consent)

        backing.recipient = CleanupConnection(target: .ollama(model: "different"), source: .settings)
        model.requestSuggestions(consent)

        XCTAssertTrue(backing.sent.isEmpty)
        XCTAssertTrue(model.suggestions.isEmpty)
        XCTAssertNil(model.consent)
        XCTAssertEqual(model.errorMessage, AIDictionarySuggestionModel.changedBeforeSending)
    }

    func testRecipientChangedBackStillWithdrawsTheQuestion() async throws {
        let backing = AIDictionarySuggestionBacking()
        backing.recipient = AIDictionarySuggestionBacking.ollama
        let model = makeModel(backing: backing)
        await learn(model, HistoryScanRecorder())
        let consent = try XCTUnwrap(model.consent)

        let original = backing.recipient
        backing.recipient = CleanupConnection(target: .ollama(model: "other"), source: .settings)
        backing.recipient = original
        model.requestSuggestions(consent)

        XCTAssertTrue(backing.sent.isEmpty)
        XCTAssertNil(model.consent)
        XCTAssertNotNil(model.errorMessage)
    }

    func testSettingsNotificationWithdrawsTheQuestionWhenCleanupIsTurnedOff() async {
        let backing = AIDictionarySuggestionBacking()
        backing.recipient = AIDictionarySuggestionBacking.ollama
        let center = NotificationCenter()
        let model = makeModel(backing: backing, center: center)
        await learn(model, HistoryScanRecorder())
        XCTAssertNotNil(model.consent)

        backing.cleanupEnabled = false
        center.post(name: UserDefaults.didChangeNotification, object: nil)

        XCTAssertNil(model.consent)
        XCTAssertTrue(model.suggestions.isEmpty)
        XCTAssertEqual(model.errorMessage, AIDictionarySuggestionModel.changedBeforeSending)
    }

    func testRecipientChangeWhileAwaitingDiscardsTheReply() async {
        let backing = AIDictionarySuggestionBacking()
        let gate = SettingsTestGate()
        let model = makeModel(backing: backing, service: backing.service(gate: gate))
        model.learnFromHistory(scanHistory: HistoryScanRecorder().scan)
        let running = model.inFlight
        await gate.waitForArrival()

        backing.recipient = CleanupConnection(target: .ollama(model: "other"), source: .settings)
        await gate.open()
        await running?.value

        XCTAssertEqual(backing.sent.count, 1)
        XCTAssertTrue(model.suggestions.isEmpty)
        XCTAssertFalse(model.isReviewPresented)
        XCTAssertEqual(model.errorMessage, AIDictionarySuggestionModel.changedAfterSending)
    }

    func testTurningCleanupOffWhileAwaitingDiscardsTheReply() async {
        let backing = AIDictionarySuggestionBacking()
        let gate = SettingsTestGate()
        let model = makeModel(backing: backing, service: backing.service(gate: gate))
        model.learnFromHistory(scanHistory: HistoryScanRecorder().scan)
        let running = model.inFlight
        await gate.waitForArrival()

        backing.cleanupEnabled = false
        await gate.open()
        await running?.value

        XCTAssertTrue(model.suggestions.isEmpty)
        XCTAssertFalse(model.isReviewPresented)
        XCTAssertFalse(model.isBusy)
    }

    func testCancellationDiscardsAnAwaitingResponse() async {
        let backing = AIDictionarySuggestionBacking()
        let gate = SettingsTestGate()
        let scans = HistoryScanRecorder()
        let model = makeModel(backing: backing, service: backing.service(gate: gate))
        model.learnFromHistory(scanHistory: scans.scan)
        let running = model.inFlight
        await gate.waitForArrival()

        model.cancel()
        await gate.open()
        await running?.value

        XCTAssertTrue(model.suggestions.isEmpty)
        XCTAssertFalse(model.isBusy)
        XCTAssertTrue(scans.runs.isEmpty)
    }

    // MARK: - What comes back

    func testAnAnswerWithNothingUsableFallsBackToTheHistoryScan() async {
        for answer in ["", "no json here", "[]", #"[{"spoken":"same","written":"same"}]"#] {
            let backing = AIDictionarySuggestionBacking()
            backing.answer = answer
            let scans = HistoryScanRecorder()
            let model = makeModel(backing: backing)

            await learn(model, scans)

            XCTAssertEqual(backing.sent.count, 1, answer)
            XCTAssertEqual(scans.runs, [true], answer)
            XCTAssertFalse(model.isReviewPresented, answer)
        }
    }

    // History keeps a dictation as it was typed even after a rule was added to fix it (0.5.18).
    func testASuggestionThatWritesBackAMistakeAnEnabledWordFixesIsLeftOut() async {
        let backing = AIDictionarySuggestionBacking()
        backing.answer = #"[{"spoken":"m c b","written":"MCB"},{"spoken":"a k s","written":"AKS"}]"#
        let model = makeModel(
            backing: backing, access: access(existing: [DictionaryEntry(pattern: "MCB", replacement: "MCP")]))

        await learn(model, HistoryScanRecorder())

        XCTAssertEqual(model.suggestions, [DictionaryEntry(pattern: "a k s", replacement: "AKS")])
    }

    func testDashesNeverReachAWrittenForm() async {
        let backing = AIDictionarySuggestionBacking()
        backing.answer =
            #"[{"spoken":"cosmos db","written":"Cosmos\u2014DB"},{"spoken":"a k s","written":"AKS"},"#
            + "{\"spoken\":\"en dash\",\"written\":\"before \u{2013} after\"}]"
        let model = makeModel(backing: backing)

        await learn(model, HistoryScanRecorder())

        XCTAssertFalse(model.suggestions.isEmpty)
        for suggestion in model.suggestions {
            XCTAssertFalse(DashNormalizer.containsDash(suggestion.pattern))
            XCTAssertFalse(DashNormalizer.containsDash(suggestion.replacement))
        }
        XCTAssertFalse(model.suggestions.contains { $0.pattern == "cosmos db" })
    }

    func testARepairShowsWhatScribeWritesNowAndIsCommittedInPlace() async throws {
        let directory = try StorageTestDirectory()
        defer { directory.remove() }
        let store = PersistenceStore(databaseURL: directory.databaseURL)
        try store.initialize()
        let apim = try store.insertDictionaryEntry(
            DictionaryEntry(pattern: "a p i m", replacement: "Apim", wholeWord: false, enabled: false))
        try store.recordDictation(
            startedAt: Date(), durationSeconds: 1, sampleCount: 16_000, transcriptText: "the Apim gateway is down")
        let backing = AIDictionarySuggestionBacking()
        backing.answer = #"[{"spoken":"a p i m","written":"APIM"},{"spoken":"cosmos db","written":"Cosmos DB"}]"#
        let changes = SettingsTestCounter()
        let model = AIDictionarySuggestionModel(
            access: .live(store), service: backing.service, center: NotificationCenter(),
            onChanged: { changes.increment() })

        await learn(model, HistoryScanRecorder())

        XCTAssertEqual(backing.sent.map { $0.sample }, ["the Apim gateway is down"])
        let repair = try XCTUnwrap(model.suggestions.first)
        XCTAssertEqual(repair, DictionaryEntry(pattern: "a p i m", replacement: "APIM"))
        XCTAssertEqual(model.currentReplacement(for: repair), "Apim")
        let added = try XCTUnwrap(model.suggestions.last)
        XCTAssertNil(model.currentReplacement(for: added))

        await model.commitSelectedSuggestions()

        let stored = try store.fetchAllDictionaryEntries()
        XCTAssertEqual(
            stored.first { $0.id == apim },
            DictionaryEntry(id: apim, pattern: "a p i m", replacement: "APIM", wholeWord: false, enabled: true))
        XCTAssertEqual(stored.map(\.pattern), ["a p i m", "cosmos db"])
        XCTAssertEqual(
            model.statusMessage, "Added 1 word to your dictionary and fixed how Scribe writes 1 word.")
        XCTAssertEqual(model.dictionaryRevision, 1)
        XCTAssertEqual(changes.count, 1)
        XCTAssertFalse(model.isReviewPresented)
    }

    func testReviewCommitsOnlyTheSelectionAndNoDictationReachesTheLog() async throws {
        let backing = AIDictionarySuggestionBacking()
        let committed = CommittedRows()
        let recorder = recordScribeLog()
        let model = makeModel(
            backing: backing,
            access: access(history: [PrivacyCanary.transcript], committed: { committed.record($0) }))

        await learn(model, HistoryScanRecorder())
        recorder.stop()

        XCTAssertEqual(backing.sent.map { $0.sample }, [PrivacyCanary.transcript])
        XCTAssertEqual(model.selectedSpokenForms.count, 2)
        PrivacyCanary.assertAbsent(from: recorder.publicText)

        let deselected = try XCTUnwrap(model.suggestions.last)
        XCTAssertTrue(model.isSelected(deselected))
        model.toggleSelection(for: deselected, selected: false)
        await model.commitSelectedSuggestions()

        XCTAssertEqual(committed.patterns, ["a p i"])
        XCTAssertEqual(model.statusMessage, "Added 1 word to your dictionary.")
        XCTAssertFalse(model.isReviewPresented)
        XCTAssertTrue(model.suggestions.isEmpty)
    }

    func testTheCommitSummaryNamesWhatWasAddedAndFixed() {
        let word = DictionaryEntry(pattern: "a", replacement: "A")
        XCTAssertEqual(
            AIDictionarySuggestionModel.commitSummary(DictionarySuggestionCommit()),
            "Those words are already in your dictionary.")
        XCTAssertEqual(
            AIDictionarySuggestionModel.commitSummary(DictionarySuggestionCommit(added: [word, word])),
            "Added 2 words to your dictionary.")
        XCTAssertEqual(
            AIDictionarySuggestionModel.commitSummary(DictionarySuggestionCommit(repaired: [word])),
            "Fixed how Scribe writes 1 word in your dictionary.")
        XCTAssertEqual(
            AIDictionarySuggestionModel.commitSummary(
                DictionarySuggestionCommit(added: [word], repaired: [word, word])),
            "Added 1 word to your dictionary and fixed how Scribe writes 2 words.")
    }

    func testTheInstructionsAreWindowsWordForWord() {
        let prompt = AIDictionarySuggestionModel.systemPrompt
        XCTAssertTrue(prompt.hasPrefix("You help build a personal dictation dictionary."))
        XCTAssertTrue(prompt.contains("Read the surrounding sentence, topic, and nearby dictations carefully"))
        XCTAssertTrue(prompt.contains("earlier rewrite got wrong in context"))
        XCTAssertTrue(prompt.contains("existing vocabulary rules"))
        XCTAssertTrue(prompt.contains("so the dictionary can self-heal"))
        XCTAssertTrue(prompt.contains("Return at most 25 items, fewer is fine."))
        XCTAssertFalse(DashNormalizer.containsDash(prompt))
    }

    // MARK: - Through the live one-off handoff

    func testTheLiveServiceFitsSuggestionsAndRefusesAChangedPlanningRevision() async throws {
        for changesDuringRead in [false, true] {
            let fixture = makeCleanupStore()
            let store = fixture.store
            store.isEnabled = true
            store.providerKind = .openAICompatible
            store.openAIBaseURL = LocalAiServer.lmStudioAddress
            store.openAIModel = "model"
            store.selectedLocalApp = .lmStudio
            let prompt = AIDictionarySuggestionModel.systemPrompt
            let sample = "a p i"
            let context =
                ContextBudget.chatTemplateTokens + 64 + TokenEstimate.vocabulary(prompt)
                + TokenEstimate.transcript(sample) + 700
            let requests = RequestLog()
            let cache = CleanupProviderCache(
                store: store, environment: [:],
                factory: .testing(
                    session: makeStubSession { request in
                        requests.record(request)
                        return StubReply.completion(request, #"[{"spoken":"a p i","written":"API"}]"#)
                    },
                    readLocalServer: { _, _ in
                        if changesDuringRead {
                            store.isEnabled = false
                            store.isEnabled = true
                        }
                        return LocalServerState(
                            reach: .reached, models: [],
                            loaded: [LocalServerLoadedModel("model", 0, contextTokens: context)])
                    }))
            let service = AIDictionarySuggestionService.backed(by: cache, operations: AuxiliaryOperations())
            let admission = try XCTUnwrap(service.admit())
            if changesDuringRead {
                do {
                    _ = try await admission.complete(prompt, sample)
                    XCTFail("A changed-back revision cannot authorize fitted suggestions")
                } catch {
                    XCTAssertEqual(error as? CleanupSendHandoff.Refusal, .settingsChanged)
                }
                XCTAssertEqual(requests.count, 0)
            } else {
                let answer = try await admission.complete(prompt, sample)
                XCTAssertEqual(answer, #"[{"spoken":"a p i","written":"API"}]"#)
                let sent = try XCTUnwrap(requests.all.first)
                XCTAssertEqual(requests.count, 1)
                XCTAssertEqual(sent.messageContents, [prompt, sample])
                XCTAssertEqual(sent.jsonBody["max_completion_tokens"] as? Int, 700)
            }
        }
    }

    func testTheLiveAdapterBindsTheQuestionToTheSettingsRevision() async throws {
        let fixture = makeCleanupStore()
        fixture.store.isEnabled = true
        fixture.store.providerKind = .openAICompatible
        fixture.store.openAIBaseURL = "https://example.invalid/v1"
        fixture.store.openAIModel = "test-model"
        let requests = RequestLog()
        let session = makeStubSession { request in
            requests.record(request)
            return StubReply.completion(request, #"[{"spoken":"a p i","written":"API"}]"#)
        }
        let cache = CleanupProviderCache(
            store: fixture.store, environment: [:], factory: .testing(session: session))
        let model = AIDictionarySuggestionModel(
            access: access(), service: .backed(by: cache, operations: AuxiliaryOperations()),
            center: NotificationCenter())
        await learn(model, HistoryScanRecorder())
        let consent = try XCTUnwrap(model.consent)
        XCTAssertTrue(consent.message.contains("the AI service you set up"))

        fixture.store.openAIModel = "changed"
        fixture.store.openAIModel = "test-model"
        model.requestSuggestions(consent)
        await model.inFlight?.value

        XCTAssertEqual(requests.count, 0)
        XCTAssertTrue(model.suggestions.isEmpty)
    }

    func testTheLiveAdapterSendsTheInstructionsAndTheHistorySampleAndNothingElse() async throws {
        let fixture = makeCleanupStore()
        fixture.store.isEnabled = true
        fixture.store.providerKind = .openAICompatible
        fixture.store.openAIBaseURL = "https://example.invalid/v1"
        fixture.store.openAIModel = "test-model"
        let requests = RequestLog()
        let session = makeStubSession { request in
            requests.record(request)
            return StubReply.completion(request, #"[{"spoken":"a p i","written":"API"}]"#)
        }
        let cache = CleanupProviderCache(
            store: fixture.store, environment: [:], factory: .testing(session: session))
        let existing = [DictionaryEntry(pattern: "zqx spoken", replacement: "Zqxwritten")]
        let model = AIDictionarySuggestionModel(
            access: access(history: ["newest dictation", "older dictation"], existing: existing),
            service: .backed(by: cache, operations: AuxiliaryOperations()),
            center: NotificationCenter())

        await learn(model, HistoryScanRecorder())
        try await confirm(model)

        XCTAssertEqual(requests.count, 1)
        let sent = try XCTUnwrap(requests.all.first)
        XCTAssertEqual(
            sent.messageContents, [AIDictionarySuggestionModel.systemPrompt, "newest dictation\nolder dictation"])
        XCTAssertFalse(sent.bodyText.lowercased().contains("zqx"))
        XCTAssertEqual(model.suggestions.map(\.pattern), ["a p i"])
        XCTAssertTrue(model.isReviewPresented)
    }
}

/// The patterns a fake commit was handed, across actors.
private final class CommittedRows: Sendable {
    private let rows = OSAllocatedUnfairLock<[String]>(initialState: [])

    func record(_ entries: [DictionaryEntry]) {
        let patterns = entries.map(\.pattern)
        rows.withLock { $0 += patterns }
    }

    var patterns: [String] {
        rows.withLock { $0 }
    }
}
