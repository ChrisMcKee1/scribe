import XCTest

@testable import Scribe

@MainActor
private final class AIDictionarySuggestionBacking {
    var recipient = CleanupConnection(target: .foundryLocal(modelAlias: "local-model"), source: .settings)
    var cleanupEnabled = true
    private(set) var sent: [(prompt: String, sample: String, recipient: CleanupConnection)] = []
    private(set) var addedCount = 0

    func record(prompt: String, sample: String, recipient: CleanupConnection) {
        sent.append((prompt, sample, recipient))
    }

    func recordAdded(_ count: Int) {
        addedCount += count
    }

    var service: AIDictionarySuggestionService {
        AIDictionarySuggestionService(
            currentRecipient: { self.recipient },
            isCleanupEnabled: { self.cleanupEnabled },
            complete: { prompt, sample, recipient in
                await self.record(prompt: prompt, sample: sample, recipient: recipient)
                return #"""
                    [
                    {"spoken":"a p i","written":"API"},
                    {"spoken":"post gres","written":"PostgreSQL"}
                    ]
                    """#
            })
    }

    func service(gate: SettingsTestGate) -> AIDictionarySuggestionService {
        AIDictionarySuggestionService(
            currentRecipient: { self.recipient },
            isCleanupEnabled: { self.cleanupEnabled },
            complete: { prompt, sample, recipient in
                await self.record(prompt: prompt, sample: sample, recipient: recipient)
                await gate.pass()
                return #"[{"spoken":"a p i","written":"API"}]"#
            })
    }
}

@MainActor
final class AIDictionarySuggestionsTests: XCTestCase {
    private func reportStore(raw: String = "raw recognized words", final: String = "expanded snippet body")
        -> PipelineReportStore
    {
        let reports = PipelineReportStore()
        var report = PipelineReport(
            dictationID: 42,
            capturedAt: Date(),
            trigger: .menu,
            stopReason: .menu,
            captureDuration: 1)
        report.rawText = raw
        report.finalText = final
        reports.publish(report)
        return reports
    }

    private func access(
        existing: [DictionaryEntry] = [],
        added: @escaping @Sendable (Int) async -> Void = { _ in }
    ) -> AIDictionarySuggestionAccess {
        AIDictionarySuggestionAccess(
            loadEntries: { existing },
            addIfAbsent: { entries in
                await added(entries.count)
                return entries.enumerated().map { index, entry in
                    DictionaryEntry(
                        id: Int64(index + 1),
                        pattern: entry.pattern,
                        replacement: entry.replacement,
                        wholeWord: entry.wholeWord,
                        enabled: entry.enabled)
                }
            })
    }

    private func makeModel(
        backing: AIDictionarySuggestionBacking,
        reports: PipelineReportStore? = nil,
        service: AIDictionarySuggestionService? = nil,
        access: AIDictionarySuggestionAccess? = nil,
        center: NotificationCenter = NotificationCenter()
    ) -> AIDictionarySuggestionModel {
        AIDictionarySuggestionModel(
            reports: reports ?? reportStore(),
            access: access ?? self.access(),
            service: service ?? backing.service,
            center: center)
    }

    func testParserBoundsDeduplicatesAndSkipsExistingOrUnsafeSuggestions() {
        let existing = [DictionaryEntry(pattern: "Post Gres", replacement: "PostgreSQL")]
        let output = """
            Here is the result:
            [{"spoken":"a p i","written":"API"},{"spoken":"A P I","written":"other"},
            {"spoken":"post gres","written":"PostgreSQL"},
            {"spoken":"multi\\nline","written":"bad"},
            {"spoken":"dash","written":"bad\\u2014value"},
            {"spoken":"same","written":"same"}]
            """

        let parsed = AIDictionarySuggestionModel.parseSuggestions(output, existing: existing, maxSuggestions: 1)

        XCTAssertEqual(parsed, [DictionaryEntry(pattern: "a p i", replacement: "API")])
        XCTAssertTrue(AIDictionarySuggestionModel.parseSuggestions("not json", existing: []).isEmpty)
        XCTAssertTrue(AIDictionarySuggestionModel.parseSuggestions("[", existing: []).isEmpty)
    }

    func testSampleUsesOnlyBoundedRawInMemoryReport() {
        let reports = reportStore(
            raw: "  \(PrivacyCanary.transcript)  ",
            final: "snippet-template-\(PrivacyCanary.secret)")
        let sample = AIDictionarySuggestionModel.boundedRawSample(reports.latest?.rawText)

        XCTAssertEqual(sample, PrivacyCanary.transcript)
        XCTAssertEqual(
            AIDictionarySuggestionModel.boundedRawSample(String(repeating: "x", count: 20), maxCharacters: 7), "xxxxxxx"
        )
        XCTAssertNil(AIDictionarySuggestionModel.boundedRawSample(nil))
        XCTAssertNotEqual(sample, reports.latest?.finalText)
    }

    func testConsentNamesRawSampleInstructionsVocabularyAndDestinationCategory() {
        let message = AIDictionarySuggestionModel.consentMessage(
            sampleCharacters: 123,
            destination: "Microsoft Foundry")

        XCTAssertTrue(message.contains("123 characters"))
        XCTAssertTrue(message.contains("raw text recognized"))
        XCTAssertTrue(message.contains("suggestion instructions"))
        XCTAssertTrue(message.contains("permitted word pack vocabulary"))
        XCTAssertTrue(message.contains("Snippet templates are never included"))
        XCTAssertTrue(message.contains("Microsoft Foundry"))
    }

    func testNoBoundCompletionOrCleanupOffCannotStartTheRequest() {
        let reports = reportStore()
        let unavailable = AIDictionarySuggestionModel(reports: reports, access: access())
        XCTAssertFalse(unavailable.canSuggest)
        unavailable.prepareConsent()
        XCTAssertTrue(unavailable.errorMessage?.contains("safe saved-provider handoff") == true)
        unavailable.requestSuggestions()
        XCTAssertTrue(unavailable.suggestions.isEmpty)

        let backing = AIDictionarySuggestionBacking()
        backing.cleanupEnabled = false
        let disabled = makeModel(backing: backing)
        XCTAssertFalse(disabled.canSuggest)
        disabled.prepareConsent()
        disabled.requestSuggestions()
        XCTAssertTrue(backing.sent.isEmpty)
    }

    func testRecipientChangeAfterConsentRefusesBeforeSending() {
        let backing = AIDictionarySuggestionBacking()
        let model = makeModel(backing: backing)
        model.prepareConsent()
        XCTAssertNotNil(model.consent)

        backing.recipient = CleanupConnection(target: .ollama(model: "different"), source: .settings)
        model.requestSuggestions()

        XCTAssertTrue(backing.sent.isEmpty)
        XCTAssertTrue(model.suggestions.isEmpty)
        XCTAssertNil(model.consent)
    }

    func testRecipientChangeWhileAwaitingDiscardsTheReply() async {
        let backing = AIDictionarySuggestionBacking()
        let gate = SettingsTestGate()
        let model = makeModel(backing: backing, service: backing.service(gate: gate))
        model.prepareConsent()
        model.requestSuggestions()
        let running = model.inFlight
        await gate.waitForArrival()

        backing.recipient = CleanupConnection(target: .ollama(model: "other"), source: .settings)
        await gate.open()
        await running?.value

        XCTAssertEqual(backing.sent.count, 1)
        XCTAssertTrue(model.suggestions.isEmpty)
        XCTAssertFalse(model.isReviewPresented)
    }

    func testTurningCleanupOffWhileAwaitingDiscardsTheReply() async {
        let backing = AIDictionarySuggestionBacking()
        let gate = SettingsTestGate()
        let model = makeModel(backing: backing, service: backing.service(gate: gate))
        model.prepareConsent()
        model.requestSuggestions()
        let running = model.inFlight
        await gate.waitForArrival()

        backing.cleanupEnabled = false
        await gate.open()
        await running?.value

        XCTAssertTrue(model.suggestions.isEmpty)
        XCTAssertFalse(model.isReviewPresented)
    }

    func testNewerRawReportCancelsAndDiscardsTheAwaitingSuggestion() async {
        let backing = AIDictionarySuggestionBacking()
        let gate = SettingsTestGate()
        let reports = reportStore()
        let model = makeModel(backing: backing, reports: reports, service: backing.service(gate: gate))
        model.prepareConsent()
        model.requestSuggestions()
        let running = model.inFlight
        await gate.waitForArrival()

        var newerReport = PipelineReport(
            dictationID: 43,
            capturedAt: Date(),
            trigger: .menu,
            stopReason: .menu,
            captureDuration: 1)
        newerReport.rawText = "newer raw sample"
        reports.publish(newerReport)
        await gate.open()
        await running?.value

        XCTAssertTrue(model.suggestions.isEmpty)
        XCTAssertFalse(model.isRequesting)
        XCTAssertTrue(model.errorMessage?.contains("recent dictation changed") == true)
    }

    func testCancellationDiscardsAnAwaitingResponse() async {
        let backing = AIDictionarySuggestionBacking()
        let gate = SettingsTestGate()
        let model = makeModel(backing: backing, service: backing.service(gate: gate))
        model.prepareConsent()
        model.requestSuggestions()
        let running = model.inFlight
        await gate.waitForArrival()

        model.cancel()
        await gate.open()
        await running?.value

        XCTAssertTrue(model.suggestions.isEmpty)
        XCTAssertFalse(model.isRequesting)
    }

    func testRawTranscriptIsTheOnlyUserTextSentAndReviewCommitsOnlySelection() async {
        let backing = AIDictionarySuggestionBacking()
        let reports = reportStore(
            raw: PrivacyCanary.transcript,
            final: "expanded signature template \(PrivacyCanary.secret)")
        let recorder = recordScribeLog()
        let model = makeModel(
            backing: backing,
            reports: reports,
            access: access(added: { await backing.recordAdded($0) }))
        model.prepareConsent()
        model.requestSuggestions()
        await model.inFlight?.value
        recorder.stop()

        XCTAssertEqual(backing.sent.count, 1)
        XCTAssertEqual(backing.sent[0].sample, PrivacyCanary.transcript)
        XCTAssertTrue(backing.sent[0].prompt.contains("raw speech-recognition transcript"))
        XCTAssertEqual(backing.sent[0].recipient, backing.recipient)
        XCTAssertEqual(model.suggestions.map(\.pattern), ["a p i", "post gres"])
        XCTAssertEqual(model.selectedSpokenForms.count, 2)
        PrivacyCanary.assertAbsent(from: recorder.everyText)

        let deselected = try! XCTUnwrap(model.suggestions.last)
        model.toggleSelection(for: deselected, selected: false)
        await model.commitSelectedSuggestions()

        XCTAssertEqual(backing.addedCount, 1)
        XCTAssertFalse(model.isReviewPresented)
        XCTAssertTrue(model.suggestions.isEmpty)
    }

    func testSettingsNotificationCancelsWhenCleanupIsDisabled() {
        let backing = AIDictionarySuggestionBacking()
        let center = NotificationCenter()
        let model = makeModel(backing: backing, center: center)
        model.prepareConsent()
        XCTAssertNotNil(model.consent)

        backing.cleanupEnabled = false
        center.post(name: UserDefaults.didChangeNotification, object: nil)

        XCTAssertNil(model.consent)
        XCTAssertTrue(model.suggestions.isEmpty)
    }
}
