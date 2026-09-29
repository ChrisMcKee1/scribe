import XCTest

@testable import Scribe

/// The dictation pipeline: raw recognition; with AI cleanup on, the vocabulary rules on the raw transcript, that text
/// sent with the target's writing style, the reply's guard and dash normalization, then the snippets and the
/// template-like rules; with cleanup off, snippets and the dictionary in Windows' order; line breaks for the captured
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

    /// Cleanup is sent the raw transcript with the dictionary's one-line spellings applied and the snippet's trigger
    /// phrase as spoken, never its template; the snippet runs on the model's reply. So the model starts from the
    /// user's spellings, and a template never leaves the Mac.
    func testCleanupIsSentTheCorrectedTranscriptAndTheSnippetsRunOnItsReply() async throws {
        let harness = makeHarness(rulesLoaded: false)
        loadRules(into: harness)
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { _ in "Please insert my address, and deploy it with Kubeflow." }
        harness.transcriber.defaultText = "please insert my address and deploy it with cube flow"

        await harness.dictate()
        await harness.waitUntilProcessed()

        let request = try XCTUnwrap(provider.requests.first)
        XCTAssertEqual(provider.requests.count, 1)
        XCTAssertEqual(
            request.transcript, CleanupPrompt.wrapTranscript("please insert my address and deploy it with Kubeflow"))
        XCTAssertFalse(request.transcript.contains("Harbor"), "a snippet template reached the provider")
        XCTAssertFalse(request.writingStylePrompt.contains("Harbor"), "a snippet template reached the prompt")
        XCTAssertEqual(
            harness.fakeInjector.texts,
            ["Please \(Self.snippetTemplate), and deploy it with Kubeflow."])
        XCTAssertEqual(harness.reports.latest?.cleanupOutcome, .cleaned)
        XCTAssertEqual(harness.reports.latest?.sentText, "please insert my address and deploy it with Kubeflow")
        XCTAssertEqual(harness.reports.latest?.cleanedText, "Please insert my address, and deploy it with Kubeflow.")
    }

    /// The shipped AI model library writes "cloud opus four point eight" as "Claude Opus 4.8". A model that writes
    /// number words as digits would turn the spoken form into "Cloud opus 4.8" before any rule saw it; the library
    /// runs on the text the model is sent instead, so the fix survives however the model writes numbers.
    func testALibraryFixSurvivesAModelThatWritesNumbersAsDigits() async throws {
        let library = try XCTUnwrap(BuiltInDictionaryLibraries.all.first { $0.id == "ai-model-names" })
        let entry = try XCTUnwrap(library.entries.first { $0.pattern == "cloud opus four point eight" })
        XCTAssertEqual(entry.replacement, "Claude Opus 4.8")
        let harness = makeHarness(rulesLoaded: false)
        harness.load(libraries: library.entries)
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { request in Self.writingNumbersAsDigits(RecordingCleanupProvider.transcript(in: request)) }
        harness.transcriber.defaultText = "switch the agent to cloud opus four point eight today"

        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertEqual(
            provider.requests.map(\.transcript),
            [CleanupPrompt.wrapTranscript("switch the agent to Claude Opus 4.8 today")])
        XCTAssertEqual(harness.fakeInjector.texts, ["Switch the agent to Claude Opus 4.8 today."])
        // The stand-in model does what the test says it does: given the spoken form, it loses the library's match.
        XCTAssertEqual(
            Self.writingNumbersAsDigits("switch the agent to cloud opus four point eight today"),
            "Switch the agent to cloud opus 4.8 today.")
    }

    /// A dictionary entry whose replacement is a template in all but name (more than one line, or longer than
    /// Windows' 100-character glossary cap) runs after cleanup, as a snippet does, so its text never reaches a
    /// provider; the one-line entry beside it runs before. Each lands exactly once.
    func testATemplateLikeReplacementNeverReachesTheProviderAndLandsOnce() async throws {
        let signature = "Pat Doe\nSupport lead"
        let footer = Array(repeating: "Confidential and privileged", count: 5).joined(separator: ", ")
        XCTAssertGreaterThan(footer.count, TextPostProcessor.vocabularyReplacementLimit)
        let harness = makeHarness()
        harness.load(
            dictionary: [
                DictionaryEntry(pattern: "my sign off", replacement: signature),
                DictionaryEntry(pattern: "legal footer", replacement: footer),
                DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow"),
            ])
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { request in RecordingCleanupProvider.transcript(in: request) + "." }
        harness.transcriber.defaultText = "deploy it with cube flow then my sign off and the legal footer"

        await harness.dictate()
        await harness.waitUntilProcessed()

        let request = try XCTUnwrap(provider.requests.first)
        XCTAssertEqual(
            request.transcript,
            CleanupPrompt.wrapTranscript("deploy it with Kubeflow then my sign off and the legal footer"))
        for text in ["Pat Doe", "Support lead", "Confidential"] {
            XCTAssertFalse(request.transcript.contains(text), "a template-like replacement reached the provider")
            XCTAssertFalse(request.writingStylePrompt.contains(text), "a template-like replacement reached the prompt")
        }
        XCTAssertEqual(harness.fakeInjector.texts, ["deploy it with Kubeflow then \(signature) and the \(footer)."])
    }

    /// A casing fix, an expansion that holds its own spoken form, and a spelling whose output is another rule's spoken
    /// form each land once with cleanup on: they run on the text the provider is sent and never again on the reply,
    /// and one rule's output never feeds another rule after cleanup either.
    func testCasingAndExpansionRulesLandExactlyOnceAndNeverCascade() async throws {
        let harness = makeHarness()
        harness.load(
            dictionary: [
                DictionaryEntry(pattern: "azure", replacement: "Azure"),
                DictionaryEntry(pattern: "york", replacement: "New York"),
                DictionaryEntry(pattern: "gh", replacement: "GitHub"),
                DictionaryEntry(pattern: "github", replacement: "GitHub Enterprise"),
                DictionaryEntry(pattern: "sig", replacement: "signature"),
                DictionaryEntry(pattern: "signature", replacement: "Pat Doe\nSupport lead"),
            ])
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { request in RecordingCleanupProvider.transcript(in: request) + "." }
        harness.transcriber.defaultText = "move azure to york then open gh and add my sig"

        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertEqual(
            provider.requests.map(\.transcript),
            [CleanupPrompt.wrapTranscript("move Azure to New York then open GitHub and add my signature")])
        XCTAssertEqual(harness.fakeInjector.texts, ["move Azure to New York then open GitHub and add my signature."])

        // The same rules with cleanup off: one pass, the same words.
        harness.cleanup.isEnabled = false
        await harness.dictate()
        await harness.waitUntilProcessed()
        XCTAssertEqual(harness.fakeInjector.texts.last, "move Azure to New York then open GitHub and add my signature")
    }

    /// A reply that cannot be used falls back to exactly what cleanup off gives: the vocabulary step is dropped, and
    /// the snippets and the dictionary run once, in Windows' order, on the raw transcript. Running the rules again on
    /// the corrected text instead would expand "signature", which only a vocabulary rule wrote.
    func testAFallbackGivesExactlyTheCleanupOffResult() async throws {
        let harness = makeHarness()
        harness.load(
            dictionary: [
                DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow"),
                DictionaryEntry(pattern: "sig", replacement: "signature"),
            ],
            snippets: [
                Snippet(phrase: "insert my address", template: Self.snippetTemplate),
                Snippet(phrase: "signature", template: "Kind regards"),
            ])
        harness.transcriber.defaultText = "insert my address and deploy it with cube flow then my sig"
        await harness.dictate()
        await harness.waitUntilProcessed()
        let cleanupOff = try XCTUnwrap(harness.fakeInjector.texts.first)
        XCTAssertEqual(cleanupOff, "\(Self.snippetTemplate) and deploy it with Kubeflow then my signature")

        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { _ in "Absolutely, I can help with that. What would you like me to do next?" }
        await harness.dictate()
        await harness.waitUntilProcessed()
        provider.reply = { _ in throw DictationTestFailure(code: 7) }
        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertEqual(harness.fakeInjector.texts, [cleanupOff, cleanupOff, cleanupOff])
        XCTAssertEqual(harness.reports.latest?.cleanupOutcome, .fellBack)
        let sent = CleanupPrompt.wrapTranscript("insert my address and deploy it with Kubeflow then my signature")
        XCTAssertEqual(provider.requests.map(\.transcript), [sent, sent])
    }

    /// A model that returns what it was sent delivers exactly what cleanup off delivers: every replacement is decided
    /// on the transcript, and nothing is matched against the reply. The dictations are ones a written form in the
    /// reply was once taken for a trigger in: "my signature" read as the trigger "my sig" as a vocabulary rule would
    /// write it (cleanup off expands the snippet "signature" the user did say), a deletion inside a word, a trigger
    /// said once beside its own written form, and the trigger "my sig" itself.
    func testAModelThatChangesNothingDeliversExactlyTheCleanupOffText() async throws {
        let harness = makeHarness()
        harness.load(
            dictionary: [
                DictionaryEntry(pattern: "sig", replacement: "signature"),
                DictionaryEntry(pattern: "k eight s", replacement: "K8s"),
                DictionaryEntry(pattern: "x", replacement: "", wholeWord: false),
                DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow"),
            ],
            snippets: [
                Snippet(phrase: "my sig", template: "Best,\nPat Doe"),
                Snippet(phrase: "k eight s", template: "Private\nnotes"),
                Snippet(phrase: "signature", template: "Private\nfooter"),
            ])
        let transcripts = [
            "I will add my signature tomorrow", "signaturex", "k eight s then K8s", "deploy cube flow then my sig",
        ]
        for transcript in transcripts {
            harness.transcriber.defaultText = transcript
            await harness.dictate()
            await harness.waitUntilProcessed()
        }
        let cleanupOff = harness.fakeInjector.texts
        let expected = [
            "I will add my Private\nfooter tomorrow", "signature", "Private\nnotes then K8s",
            "deploy Kubeflow then Best,\nPat Doe",
        ]
        XCTAssertEqual(cleanupOff, expected)

        harness.cleanup.isEnabled = true
        for transcript in transcripts {
            harness.transcriber.defaultText = transcript
            await harness.dictate()
            await harness.waitUntilProcessed()
            XCTAssertEqual(harness.reports.latest?.cleanupOutcome, .unchanged, transcript)
        }

        XCTAssertEqual(Array(harness.fakeInjector.texts.dropFirst(transcripts.count)), cleanupOff)
        let provider = try XCTUnwrap(harness.cleanup.gated)
        XCTAssertEqual(provider.requests.count, transcripts.count)
    }

    /// The equality above is the rules', not the whole pipeline's: the response guard still works on every reply. A
    /// reply that returns what was sent inside the tags, a code fence, quotes or after a reasoning block is stripped
    /// back to it and then gets the cleanup-off text; and a dash in the reply is still rewritten, even one the
    /// transcript held (the recognizer is not expected to write one), so there the text differs from cleanup off by
    /// exactly that dash, while the snippet's own dash, which is the user's text, survives.
    func testTheGuardStillStripsWrappersAndRewritesDashesAroundHeldBackReplacements() async throws {
        let template = "Best,\nPat \u{2013} Doe"
        let harness = makeHarness()
        harness.load(
            dictionary: [DictionaryEntry(pattern: "cube flow", replacement: "Kubeflow")],
            snippets: [Snippet(phrase: "my sig", template: template)])
        harness.transcriber.defaultText = "deploy cube flow then my sig"
        await harness.dictate()
        await harness.waitUntilProcessed()
        let cleanupOff = try XCTUnwrap(harness.fakeInjector.texts.last)
        XCTAssertEqual(cleanupOff, "deploy Kubeflow then \(template)")

        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        let wrappers: [(String) -> String] = [
            { CleanupPrompt.wrapTranscript($0) }, { "```\n\($0)\n```" }, { "\"\($0)\"" },
            { "<think>Tidy it.</think>\($0)" },
        ]
        for wrap in wrappers {
            provider.reply = { request in wrap(RecordingCleanupProvider.transcript(in: request)) }
            await harness.dictate()
            await harness.waitUntilProcessed()
            XCTAssertEqual(harness.fakeInjector.texts.last, cleanupOff)
            XCTAssertEqual(harness.reports.latest?.cleanupOutcome, .unchanged)
        }

        harness.transcriber.defaultText = "deploy cube flow \u{2014} then my sig"
        provider.reply = { request in RecordingCleanupProvider.transcript(in: request) }
        await harness.dictate()
        await harness.waitUntilProcessed()

        let sent = provider.requests.last.map(RecordingCleanupProvider.transcript(in:))
        XCTAssertEqual(sent, "deploy Kubeflow \u{2014} then my sig")
        XCTAssertEqual(harness.fakeInjector.texts.last, "deploy Kubeflow, then \(template)")
        XCTAssertEqual(harness.reports.latest?.cleanupOutcome, .cleaned)
    }

    /// The reply is checked against the text the provider was sent, not the raw transcript: a one-line rule that wrote
    /// a long product name does not make the model's faithful reply look like a ramble.
    func testTheReplyIsCheckedAgainstTheTextThatWasSent() async throws {
        let name = "Contoso Enterprise Knowledge Platform for Regulated Industries, Second Edition, with Extended Help"
        XCTAssertTrue(TextPostProcessor.isVocabulary(DictionaryEntry(pattern: "c e k p", replacement: name)))
        let harness = makeHarness()
        harness.load(dictionary: [DictionaryEntry(pattern: "c e k p", replacement: name)])
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { request in RecordingCleanupProvider.transcript(in: request) + "." }
        harness.transcriber.defaultText = "c e k p"

        await harness.dictate()
        await harness.waitUntilProcessed()

        XCTAssertEqual(provider.requests.map(\.transcript), [CleanupPrompt.wrapTranscript(name)])
        XCTAssertEqual(harness.reports.latest?.cleanupOutcome, .cleaned)
        XCTAssertEqual(harness.fakeInjector.texts, ["\(name)."])
        if case .accepted = CleanupResponseGuard.sanitize(candidate: "\(name).", original: "c e k p") {
            XCTFail("against the raw transcript the same reply should have been rejected")
        }
    }

    /// Line breaks are formatted last, for the target captured at activation: a terminal gets one line, with the
    /// snippet's own line break flattened, and its cleanup request asks the model for a single line.
    func testATerminalTargetGetsOneLineAndCleanupIsAskedForOne() async throws {
        let harness = makeHarness(rulesLoaded: false)
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
        let harness = makeHarness(rulesLoaded: false)
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
    /// the model's reply only, before the snippets run, and a dictionary replacement with a dash runs after it too,
    /// so it is never sent for cleanup either.
    func testDashesAreNormalizedInTheReplyButKeptInTheUsersOwnSnippetAndRule() async throws {
        let harness = makeHarness(rulesLoaded: false)
        harness.load(
            dictionary: [DictionaryEntry(pattern: "team name", replacement: "Scribe \u{2013} Mac")],
            snippets: [Snippet(phrase: "sign off", template: "Pat \u{2013} Support")])
        harness.cleanup.isEnabled = true
        let provider = try XCTUnwrap(harness.cleanup.gated)
        provider.reply = { _ in "Thanks \u{2014} see you soon. sign off, team name" }
        harness.transcriber.defaultText = "thanks see you soon sign off team name"

        await harness.dictate()
        await harness.waitUntilProcessed()

        let delivered = try XCTUnwrap(harness.fakeInjector.texts.first)
        XCTAssertFalse(delivered.contains("\u{2014}"), "the model's em dash was delivered")
        XCTAssertTrue(delivered.contains("Pat \u{2013} Support"), "the snippet's own dash was normalized")
        XCTAssertTrue(delivered.contains("Scribe \u{2013} Mac"), "the rule's own dash was normalized")
        XCTAssertEqual(
            provider.requests.map(\.transcript),
            [CleanupPrompt.wrapTranscript("thanks see you soon sign off team name")])
    }

    /// The reply is checked against the raw transcript it was given; a reply that answers the dictation instead of
    /// cleaning it is rejected, the raw transcript goes in, and the pill says cleanup fell back.
    func testARejectedReplyFallsBackToTheRawTranscriptVisibly() async throws {
        let harness = makeHarness()
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
        let harness = makeHarness()
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
        let harness = makeHarness()
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
        let harness = makeHarness(rulesLoaded: false)
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
        let harness = makeHarness()
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
            let harness = makeHarness()
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
        let harness = makeHarness()
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
        let harness = makeHarness()
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
        let harness = makeHarness()
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
        let harness = makeHarness()
        let first = DictationGate<String>()
        let second = DictationGate<String>()
        harness.transcriber.steps = [.gate(first), .gate(second)]

        await harness.dictate()
        await harness.dictate()
        await waitUntil("A's recognizer runs") { first.waitingCount == 1 }
        await waitUntil("B waits for its turn") { harness.controller.dictationsWaitingToTranscribe == 1 }
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
        let harness = makeHarness(rulesLoaded: false)
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
        let harness = makeHarness(rulesLoaded: false)
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
        let harness = makeHarness(rulesLoaded: false)
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

    /// A stand-in for a cleanup model's number formatting: every number word becomes a digit, "4 point 8" becomes
    /// "4.8", and the sentence is capitalized and closed with a period.
    private static func writingNumbersAsDigits(_ text: String) -> String {
        let digits = [
            "one": "1", "two": "2", "three": "3", "four": "4", "five": "5", "six": "6", "seven": "7", "eight": "8",
            "nine": "9",
        ]
        let words = text.split(separator: " ").map { digits[String($0)] ?? String($0) }
        let spaced = words.joined(separator: " ")
        let decimals = spaced.replacingOccurrences(
            of: "([0-9]) point ([0-9])", with: "$1.$2", options: .regularExpression)
        return decimals.prefix(1).uppercased() + decimals.dropFirst() + "."
    }
}
