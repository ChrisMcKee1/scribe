import XCTest

@testable import Scribe

/// The dictation pipeline in Windows' order: raw recognition, optional cleanup of the raw transcript with the target's
/// writing style, the reply's guard and dash normalization, snippets and the dictionary, line breaks for the captured
/// target, delivery into that target only, in dictation order.
@MainActor
final class DictationPipelineTests: XCTestCase {
    private static let snippetTemplate = "12 Harbor Road\nSpringfield"

    private func loadRules(into harness: DictationHarness, profiles: [AppProfile] = []) {
        harness.load(
            dictionary: [DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow")],
            snippets: [Snippet(phrase: "insert my address", template: Self.snippetTemplate)],
            profiles: profiles)
    }

    // MARK: - Order

    /// Cleanup is sent the raw transcript, trigger phrase and all, never the snippet's template; snippets and the
    /// dictionary run on the model's reply, so the user's rules have the final say.
    func testCleanupSeesTheRawTranscriptAndTheRulesRunOnItsReply() async throws {
        let harness = DictationHarness(rulesLoaded: false)
        loadRules(into: harness)
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { _ in "Please insert my address, and deploy it with cube flow." }
        harness.transcriber.defaultText = "please insert my address and deploy it with cube flow"

        await harness.dictate()
        await harness.waitUntilProcessed()

        let request = try XCTUnwrap(provider.requests.first)
        XCTAssertEqual(provider.requests.count, 1)
        XCTAssertTrue(request.transcript.contains("please insert my address and deploy it with cube flow"))
        XCTAssertFalse(request.transcript.contains("Harbor"), "a snippet template reached the provider")
        XCTAssertFalse(request.transcript.contains("Kubeflow"), "the dictionary ran before cleanup")
        XCTAssertEqual(
            harness.fakeInjector.texts,
            ["Please \(Self.snippetTemplate), and deploy it with Kubeflow."])
        XCTAssertEqual(harness.reports.latest?.cleanupOutcome, .cleaned)
        XCTAssertEqual(harness.reports.latest?.cleanedText, "Please insert my address, and deploy it with cube flow.")
    }

    /// Line breaks are formatted last, for the target captured at activation: a terminal gets one line, with the
    /// snippet's own line break flattened, and its cleanup request asks the model for a single line.
    func testATerminalTargetGetsOneLineAndCleanupIsAskedForOne() async throws {
        let harness = DictationHarness(rulesLoaded: false)
        loadRules(into: harness)
        harness.targeting.next = FakeTargeting.terminal
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { _ in "First line.\n\nThen insert my address" }
        harness.transcriber.defaultText = "first line then insert my address"

        await harness.dictate()
        await harness.waitUntilProcessed()

        let request = try XCTUnwrap(provider.requests.first)
        XCTAssertTrue(request.writingStylePrompt.hasSuffix(CleanupPrompt.singleLineWritingStyle))
        XCTAssertTrue(request.singleLineMode)
        let delivered = try XCTUnwrap(harness.fakeInjector.texts.first)
        XCTAssertFalse(delivered.contains("\n"), "a newline reached a terminal")
        XCTAssertTrue(delivered.contains("12 Harbor Road Springfield"))
    }

    /// The same dictation into an editor keeps its line breaks, and its request carries no single-line instruction.
    func testAnEditorTargetKeepsLineBreaks() async throws {
        let harness = DictationHarness(rulesLoaded: false)
        loadRules(into: harness)
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        harness.transcriber.defaultText = "insert my address"

        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertFalse(try XCTUnwrap(provider.requests.first).writingStylePrompt.contains("one physical line"))
        XCTAssertEqual(harness.fakeInjector.texts, [Self.snippetTemplate])
    }

    /// The model's dashes are normalized away, and the user's own text keeps its dash: dash normalization applies to
    /// the model's reply only, before the snippets run.
    func testDashesAreNormalizedInTheReplyButKeptInTheUsersOwnSnippet() async throws {
        let harness = DictationHarness(rulesLoaded: false)
        harness.load(snippets: [Snippet(phrase: "sign off", template: "Pat \u{2013} Support")])
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { _ in "Thanks \u{2014} see you soon. sign off" }
        harness.transcriber.defaultText = "thanks see you soon sign off"

        await harness.dictate()
        await harness.waitUntilProcessed()

        let delivered = try XCTUnwrap(harness.fakeInjector.texts.first)
        XCTAssertFalse(delivered.contains("\u{2014}"), "the model's em dash was delivered")
        XCTAssertTrue(delivered.contains("Pat \u{2013} Support"), "the snippet's own dash was normalized")
    }

    /// The reply is checked against the raw transcript it was given; a reply that answers the dictation instead of
    /// cleaning it is rejected, the raw transcript goes in, and the pill says cleanup fell back.
    func testARejectedReplyFallsBackToTheRawTranscriptVisibly() async throws {
        let harness = DictationHarness()
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { _ in "Absolutely, I can help with that. What would you like me to do next?" }
        harness.transcriber.defaultText = "can you check the build"

        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.fakeInjector.texts, ["can you check the build"])
        XCTAssertEqual(harness.reports.latest?.cleanupOutcome, .fellBack)
        XCTAssertTrue(harness.presenter.noticesShown().contains(.cleanupFellBack))
    }

    /// A provider that fails, or cannot be built, falls back to the raw transcript with the pill's own "raw text
    /// used" notice, never the insertion failure's.
    func testACleanupFailureFallsBackToTheRawTranscriptWithItsOwnNotice() async throws {
        let harness = DictationHarness()
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { _ in throw DictationTestFailure(code: 7) }
        harness.transcriber.defaultText = "ship it on friday"

        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.fakeInjector.texts, ["ship it on friday"])
        XCTAssertEqual(harness.presenter.noticesShown(), [.cleanupFellBack])
        XCTAssertTrue(harness.notifier.notices.isEmpty)

        harness.cleanup.providerError = CleanupProviderError.notConfigured(.openAIModelMissing, source: .settings)
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.fakeInjector.texts, ["ship it on friday", "ship it on friday"])
        XCTAssertEqual(harness.presenter.noticesShown(), [.cleanupFellBack, .cleanupFellBack])
        XCTAssertEqual(provider.requests.count, 1, "a provider that could not be built was sent a request")
    }

    /// With cleanup switched off nothing is sent, and the switch is read when the dictation reaches cleanup.
    func testCleanupSwitchedOffSendsNothing() async throws {
        let harness = DictationHarness()
        let provider = try XCTUnwrap(harness.cleanup.gated)

        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertTrue(provider.requests.isEmpty)
        XCTAssertEqual(harness.reports.latest?.cleanupOutcome, .off)
        XCTAssertNil(harness.history.records.first?.cleanupMilliseconds)
    }

    // MARK: - Target and profile

    /// The target and the app profile are the ones that had focus when the recording started: moving focus during
    /// processing changes neither where the text goes nor which writing style cleanup is given.
    func testTheTargetAndProfileAreCapturedAtActivation() async throws {
        let editorProfile = AppProfile(
            name: "Editor", bundleIdentifiers: [FakeTargeting.editorBundle], processNames: [],
            writingStylePrompt: "Write for the editor.", newlineHandling: nil)
        let otherProfile = AppProfile(
            name: "Other", bundleIdentifiers: [FakeTargeting.otherBundle], processNames: [],
            writingStylePrompt: "Write for the other app.", newlineHandling: nil)
        let harness = DictationHarness(rulesLoaded: false)
        harness.load(profiles: [editorProfile, otherProfile])
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        let gate = DictationGate<String>()
        harness.transcriber.steps = [.gate(gate)]

        await harness.dictate()
        // Focus moves while the recognizer runs.
        harness.targeting.next = FakeTargeting.other
        await waitUntil("the recognizer is asked") { gate.waitingCount == 1 }
        gate.open("words for the editor")
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.targeting.captures, 1)
        let request = try XCTUnwrap(provider.requests.first)
        XCTAssertTrue(request.writingStylePrompt.contains("Write for the editor."))
        XCTAssertFalse(request.writingStylePrompt.contains("Write for the other app."))
        let delivery = try XCTUnwrap(harness.fakeInjector.deliveries.first)
        XCTAssertEqual(delivery.target?.bundleIdentifier, FakeTargeting.editorBundle)
        XCTAssertEqual(delivery.target?.processIdentifier, 100)
        XCTAssertEqual(harness.history.records.first?.targetApp, FakeTargeting.editorBundle)
    }

    /// A target that could not be captured is never delivered to "wherever focus is": the transcript is kept for
    /// recovery and a notice offers it.
    func testATargetThatCouldNotBeCapturedIsNeverDeliveredElsewhere() async throws {
        let harness = DictationHarness()
        harness.targeting.next = FakeTargeting.unknown

        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertTrue(harness.fakeInjector.deliveries.isEmpty)
        XCTAssertEqual(harness.recovery.recent(), ["hello from the recognizer"])
        XCTAssertEqual(harness.presenter.noticesShown(), [.textKept])
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.notInserted])
        XCTAssertEqual(harness.notifier.notices.first?.recoveryText, "hello from the recognizer")
        XCTAssertEqual(harness.history.records.count, 1)
    }

    /// Each way a delivery can fall short has a notice of its own, on the pill and with the transcript to copy.
    func testEachDeliveryShortfallHasItsOwnNotice() async {
        let cases: [(InjectionDelivery, OverlayNotice, DictationNotice.Kind)] = [
            (.targetChanged, .textKept, .notInserted),
            (.targetUnresponsive, .textKept, .notInserted),
            (.noFocusedElement, .textKept, .notInserted),
            (.typedPartially, .partlyInserted, .partlyInserted),
            (.accessibilityUnconfirmed, .mayNotBeInserted, .mayNotBeInserted),
            (.accessibilityDenied, .accessibilityNeeded, .accessibilityNeeded),
        ]
        for (delivery, notice, kind) in cases {
            let harness = DictationHarness()
            harness.fakeInjector.result = InjectionResult(delivery: delivery)
            await harness.dictate()
            await harness.waitUntilProcessed()
            XCTAssertEqual(harness.presenter.noticesShown(), [notice], "\(delivery)")
            XCTAssertEqual(harness.notifier.notices.map(\.kind), [kind], "\(delivery)")
            XCTAssertEqual(harness.notifier.notices.first?.recoveryText, "hello from the recognizer", "\(delivery)")
        }
    }

    // MARK: - Nothing to insert

    /// An empty transcript is nothing to insert: no delivery, no history entry, no notice, no notification.
    func testAnEmptyTranscriptIsNothingToInsertAndQuiet() async {
        let harness = DictationHarness()
        harness.transcriber.defaultText = "   "

        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertTrue(harness.fakeInjector.deliveries.isEmpty)
        XCTAssertTrue(harness.history.records.isEmpty)
        XCTAssertTrue(harness.presenter.noticesShown().isEmpty)
        XCTAssertTrue(harness.notifier.notices.isEmpty)
        XCTAssertTrue(harness.recovery.recent().isEmpty)
        XCTAssertEqual(harness.lastOverlay, .hidden)
    }

    /// A missing recognizer says so without an alert, and the next dictation looks for it again.
    func testAMissingRecognizerIsANoticeAndTheNextDictationTriesAgain() async {
        let harness = DictationHarness()
        harness.transcriber.steps = [.failure(TranscriptionError.backendMissing(.foundryCliNotFound))]

        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.presenter.noticesShown(), [.recognizerMissing])
        XCTAssertEqual(harness.notifier.notices.map(\.kind), [.recognizerMissing])
        XCTAssertEqual(harness.reports.latest?.failureStage, .decode)
        XCTAssertTrue(harness.history.records.isEmpty)

        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.transcriber.calls, 2)
        XCTAssertEqual(harness.fakeInjector.texts, ["hello from the recognizer"])
    }

    // MARK: - Order across dictations

    /// Dictation A's cleanup finishes after dictation B's; A still goes in first, and B's text waits for it.
    func testCleanupFinishingInReverseOrderStillDeliversInDictationOrder() async throws {
        let harness = DictationHarness()
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        let replyA = DictationGate<String>()
        let replyB = DictationGate<String>()
        provider.reply = { request in
            let gate = RecordingCleanupProvider.transcript(in: request).contains("alpha") ? replyA : replyB
            return try await gate.wait()
        }
        harness.transcriber.steps = [.text("alpha words"), .text("beta words")]

        await harness.dictate()
        await waitUntil("A's cleanup is asked") { replyA.waitingCount == 1 }
        await harness.dictate()
        await waitUntil("B's cleanup is asked") { replyB.waitingCount == 1 }

        replyB.open("Beta words.")
        await waitUntil("B is ready to deliver, or has overtaken A") {
            harness.controller.dictationsWaitingToDeliver == 1 || !harness.fakeInjector.deliveries.isEmpty
        }
        XCTAssertTrue(harness.fakeInjector.deliveries.isEmpty, "B was inserted before A")

        replyA.open("Alpha words.")
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.fakeInjector.texts, ["Alpha words.", "Beta words."])
        XCTAssertEqual(harness.history.records.map(\.transcriptText), ["Alpha words.", "Beta words."])
        XCTAssertEqual(harness.recovery.recent(), ["Beta words.", "Alpha words."])
        XCTAssertEqual(harness.transcriber.mostActiveAtOnce, 1)
    }

    /// One recognizer at a time: B's recognition waits for A's, in dictation order.
    func testOneRecognizerRunsAtATimeInDictationOrder() async {
        let harness = DictationHarness()
        let first = DictationGate<String>()
        let second = DictationGate<String>()
        harness.transcriber.steps = [.gate(first), .gate(second)]

        await harness.dictate()
        await harness.dictate()
        await waitUntil("A's recognizer runs") { first.waitingCount == 1 }
        await drainMainActor()
        XCTAssertEqual(harness.transcriber.calls, 1, "B's recognizer started while A's ran")

        first.open("first")
        await waitUntil("B's recognizer runs") { second.waitingCount == 1 }
        second.open("second")
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.fakeInjector.texts, ["first", "second"])
        XCTAssertEqual(harness.transcriber.mostActiveAtOnce, 1)
    }

    // MARK: - Startup

    /// A dictation that finishes before startup's first rule load waits for it, and is processed with the user's
    /// rules, never the empty set the app starts with.
    func testADictationWaitsForTheFirstRuleLoad() async {
        let harness = DictationHarness(rulesLoaded: false)
        harness.transcriber.defaultText = "deploy it with cube flow"

        await harness.dictate()
        await waitUntil("the dictation waits for the rules") { harness.gate.waitingCount == 1 }
        XCTAssertTrue(harness.fakeInjector.deliveries.isEmpty)

        harness.load(dictionary: [DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow")])
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.fakeInjector.texts, ["deploy it with Kubeflow"])
    }

    /// A first rule load that failed opens the gate degraded: dictation still works, without stored rules.
    func testADegradedStartupStillDictates() async {
        let harness = DictationHarness(rulesLoaded: false)
        await harness.dictate()
        harness.gate.open(.withoutStoredRules)
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.fakeInjector.texts, ["hello from the recognizer"])
    }

    // MARK: - Privacy

    /// The whole pipeline, with canaries in the transcript, the model's reply, a snippet, a dictionary rule, the app
    /// profile's name and style, and the target's identity, logs none of them, in public or private text, on the
    /// success path and on the failure paths.
    func testThePipelineLogsNoTextNoRuleNoProfileAndNoTarget() async throws {
        let recorder = recordScribeLog()
        let canaryTarget = DictationTarget(
            injection: InjectionTarget(processIdentifier: 4_242, bundleIdentifier: "com.canary.editor"),
            bundleIdentifier: "com.canary.editor",
            processName: "Canary Editor")
        let profile = AppProfile(
            name: "Canary profile", bundleIdentifiers: ["com.canary.editor"], processNames: ["Canary Editor"],
            writingStylePrompt: "Canary style for Dana.", newlineHandling: .keepNewlines)
        let harness = DictationHarness(rulesLoaded: false)
        harness.load(
            dictionary: [DictionaryEntry(pattern: "quarterly", replacement: "canary-quarterly")],
            snippets: [Snippet(phrase: "friday", template: "Canary snippet body for Dana")],
            profiles: [profile])
        harness.targeting.next = canaryTarget
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { request in "Canary reply: " + RecordingCleanupProvider.transcript(in: request) }
        harness.transcriber.defaultText = PrivacyCanary.transcript

        // Success, with a delivery that falls short so the notice path runs too.
        harness.fakeInjector.result = InjectionResult(delivery: .targetChanged)
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.fakeInjector.deliveries.count, 1)
        XCTAssertEqual(harness.notifier.notices.first?.recoveryText?.contains("Canary snippet body"), true)

        // A provider failure and a rejected reply.
        provider.reply = { _ in throw CleanupProviderError.invalidResponse(.undecodable) }
        await harness.dictate()
        await harness.waitUntilProcessed()
        provider.reply = { _ in "I'm sorry, as an AI I cannot help with Canary quarterly numbers." }
        await harness.dictate()
        await harness.waitUntilProcessed()

        // A recognizer failure.
        harness.transcriber.steps = [.failure(TranscriptionError.exitCode(9))]
        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertFalse(recorder.renderings.isEmpty)
        PrivacyCanary.assertAbsent(from: recorder.everyText)
    }
}
