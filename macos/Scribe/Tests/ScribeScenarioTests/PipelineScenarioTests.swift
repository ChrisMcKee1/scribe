import XCTest

@testable import Scribe

/// The dictation pipeline end to end on the committed fixtures: each clip is played through the production capture
/// engine on a device of its own, recognized by a stand-in that answers with the manifest's text, cleaned by a
/// stand-in model, run through the production rules (the Windows suite's dictionary and snippets), delivered to a
/// captured target and written through the production history writer into a real database.
///
/// What is checked is the order with AI cleanup on: raw recognition, the vocabulary rules on the raw transcript, that
/// corrected text sent for cleanup, the response guard with dash normalization, the snippets and any template-like
/// rule on the reply, line breaks for the target last, delivery, and history after delivery. So the model is sent the
/// dictionary's spellings and never a snippet template, a terminal gets one line however the model wrote it,
/// deliveries keep dictation order whichever cleanup finished first, every presentation takes a newer revision than
/// the one before, and no dictated text reaches a log.
@MainActor
final class PipelineScenarioTests: XCTestCase {
    /// A device a dictation plays on.
    private struct DeviceSetup {
        let rate: Double
        let layout: ScenarioChannelLayout
        let encoding: ScenarioDeviceAudio.Encoding
    }

    /// The device each dictation plays on, in turn, so the pipeline also meets every conversion path.
    private static let devices = [
        DeviceSetup(rate: 16_000, layout: .mono, encoding: .float32),
        DeviceSetup(rate: 44_100, layout: .mono, encoding: .float32),
        DeviceSetup(rate: 48_000, layout: .stereoOne(voice: 0, otherHasFloor: true), encoding: .float32),
        DeviceSetup(rate: 48_000, layout: .stereoOne(voice: 1, otherHasFloor: false), encoding: .int16Interleaved),
    ]

    /// The texts no log may contain: the snippet templates, the dictionary's canonical forms and the targets.
    private static let neverLogged = [
        ScenarioVocabulary.signatureTemplate, ScenarioVocabulary.addressTemplate, "Azure DevOps", "GitHub Copilot",
        "Kubernetes", "com.example.canary-editor", "com.apple.Terminal",
    ]

    func testEveryFixtureReachesAnEditorInWindowsOrderWithItsHistoryAfterIt() async throws {
        let library = try ScenarioLibrary.shared()
        let script = PipelineScript(lines: try editorLines(library))
        let harness = try makePipelineHarness(script: script, target: PipelineHarness.editor)
        let log = recordScenarioLog()
        let report = ScenarioReport("pipeline-editor")
        let clock = ContinuousClock()
        let began = clock.now

        for (index, line) in script.lines.enumerated() {
            let device = Self.devices[index % Self.devices.count]
            try await harness.dictate(
                line, at: device.rate, layout: device.layout, encoding: device.encoding, seed: UInt64(100 + index))
        }
        let dictating = began.duration(to: clock.now)
        let shutdown = try await harness.shutDown()

        XCTAssertTrue(shutdown.history.drained, "the history writer did not drain at shutdown")
        XCTAssertEqual(shutdown.cancelledDictations, 0)
        try assertRecognized(script, by: harness, exactEvery: Self.devices.count)
        assertCleanupRequests(script, harness, singleLine: false)
        XCTAssertEqual(harness.injector.deliveries.map(\.text), script.lines.map(\.delivered))
        XCTAssertTrue(harness.injector.deliveries.allSatisfy { $0.bundleIdentifier == "com.example.canary-editor" })
        assertWindowsOrder(script, harness.journal)
        try assertHistory(script, harness, targetApp: "com.example.canary-editor")
        assertPresentationsOnlyMoveForward(harness)
        XCTAssertTrue(harness.notifier.notices.isEmpty, "a notice was posted: \(harness.notifier.notices.map(\.kind))")
        XCTAssertEqual(
            harness.recovery.recent(),
            script.lines.suffix(LastTranscriptStore.capacity).reversed().map(\.delivered),
            "Recent Dictations does not hold the newest deliveries, newest first")
        assertNothingDictatedWasLogged(script, log)

        report.note("dictations", count: script.lines.count)
        report.note("audioSeconds", value: script.lines.reduce(0) { $0 + $1.clip.seconds }, digits: 2)
        report.note("presentations", count: harness.presenter.presentations.count)
        report.note("logEvents", count: log.renderings.count)
        report.note("lowestCorrelation", value: harness.recognizer.heard.map(\.correlation).min() ?? 0, digits: 5)
        report.note("dictating", duration: dictating)
        report.write()
    }

    func testATerminalGetsOneLineHoweverTheModelAndTheSnippetsWroteIt() async throws {
        let library = try ScenarioLibrary.shared()
        let editor = try editorLines(library)
        let chosen = ["list", "snippet-signature", "long-passage", "dict-kubernetes"]
        let lines = editor.filter { chosen.contains($0.clip.name) }.map { line in
            PipelineLine(
                clip: line.clip, sent: line.sent, reply: line.reply, cleaned: line.cleaned,
                delivered: Self.flattened(line.delivered))
        }
        let script = PipelineScript(lines: lines)
        let harness = try makePipelineHarness(script: script, target: PipelineHarness.terminal)
        let log = recordScenarioLog()
        let report = ScenarioReport("pipeline-terminal")

        for (index, line) in script.lines.enumerated() {
            let device = Self.devices[(index + 2) % Self.devices.count]
            try await harness.dictate(
                line, at: device.rate, layout: device.layout, encoding: device.encoding, seed: UInt64(200 + index))
        }
        let shutdown = try await harness.shutDown()

        XCTAssertTrue(shutdown.history.drained)
        XCTAssertTrue(lines.contains { $0.reply.contains("\n") }, "no reply has a line break to flatten")
        XCTAssertTrue(
            lines.contains { $0.clip.name == "snippet-signature" },
            "the snippet's own line break is not exercised")
        assertCleanupRequests(script, harness, singleLine: true)
        let delivered = harness.injector.deliveries.map(\.text)
        XCTAssertEqual(delivered, script.lines.map(\.delivered))
        XCTAssertFalse(delivered.contains { $0.contains("\n") || $0.contains("\r") }, "a line break reached a terminal")
        XCTAssertTrue(harness.injector.deliveries.allSatisfy { $0.bundleIdentifier == "com.apple.Terminal" })
        assertWindowsOrder(script, harness.journal)
        try assertHistory(script, harness, targetApp: "com.apple.Terminal")
        assertPresentationsOnlyMoveForward(harness)
        assertNothingDictatedWasLogged(script, log)

        report.note("dictations", count: script.lines.count)
        report.note("presentations", count: harness.presenter.presentations.count)
        report.write()
    }

    /// The first dictation's cleanup is held until the second has been cleaned, post-processed and is waiting to be
    /// delivered. The first still goes in first, and history follows delivery order.
    func testDeliveriesKeepDictationOrderWhenTheSecondCleanupFinishesFirst() async throws {
        let library = try ScenarioLibrary.shared()
        let editor = try editorLines(library)
        let first = try XCTUnwrap(editor.first { $0.clip.name == "dict-kubernetes" })
        let second = try XCTUnwrap(editor.first { $0.clip.name == "question" })
        let script = PipelineScript(lines: [first, second])
        let harness = try makePipelineHarness(script: script, target: PipelineHarness.editor)
        let log = recordScenarioLog()
        let held = harness.model.hold(first.clip)
        let arrivals = harness.model.arrivals
        let processed = harness.rules.processed
        let queued = harness.history.queued

        try await harness.dictate(
            first, at: 48_000, layout: .stereoBoth, encoding: .float32, seed: 301, waitingForHistory: false)
        try await underWatchdog("the first cleanup request") { try await arrivals.wait(atLeast: 1) }
        try await harness.dictate(
            second, at: 44_100, layout: .mono, encoding: .float32, seed: 302, waitingForHistory: false)
        try await underWatchdog("the second dictation's post-processing") { try await processed.wait(atLeast: 1) }

        // The second dictation's text is ready, and it waits for the first to be delivered.
        XCTAssertTrue(harness.injector.deliveries.isEmpty, "a dictation was delivered before the first")
        XCTAssertEqual(harness.controller.dictationsWaitingToDeliver, 1)
        XCTAssertEqual(harness.journal.clips(at: .postProcessed), [second.clip.name])

        held.open()
        try await underWatchdog("both dictations to reach history") { try await queued.wait(atLeast: 2) }
        let shutdown = try await harness.shutDown()

        XCTAssertTrue(shutdown.history.drained)
        XCTAssertEqual(harness.journal.clips(at: .postProcessed), [second.clip.name, first.clip.name])
        XCTAssertEqual(harness.journal.clips(at: .delivered), [first.clip.name, second.clip.name])
        XCTAssertEqual(harness.journal.clips(at: .historyQueued), [first.clip.name, second.clip.name])
        XCTAssertEqual(harness.injector.deliveries.map(\.text), [first.delivered, second.delivered])
        try assertHistory(script, harness, targetApp: "com.example.canary-editor")
        assertPresentationsOnlyMoveForward(harness)
        assertNothingDictatedWasLogged(script, log)
    }

    // MARK: - The script

    /// What the stand-in model answers for every fixture, and what must reach an editor. The model is sent each
    /// transcript with the vocabulary rules applied and, unless a line says otherwise, answers with what it was sent,
    /// so the dictionary's spellings come back in its reply. It adds line breaks, formats numbers, capitalizes the
    /// snippet triggers and writes one em dash, which the response guard turns into a comma.
    private func editorLines(_ library: ScenarioLibrary) throws -> [PipelineLine] {
        func line(_ name: String, reply: String? = nil, cleaned: String? = nil, delivered: String? = nil) throws
            -> PipelineLine
        {
            let clip = try library.clip(name)
            let sent = ScenarioVocabulary.corrected(clip.text)
            let answered = reply ?? sent
            let carried = cleaned ?? answered
            return PipelineLine(
                clip: clip, sent: sent, reply: answered, cleaned: carried, delivered: delivered ?? carried)
        }
        let passage = try library.clip("long-passage").text
        let longer = try library.clip("longer").text
        return [
            try line("dict-azure-devops"),
            try line("dict-github-copilot"),
            try line("dict-kubernetes"),
            try line("dict-scribe"),
            try line(
                "snippet-signature", reply: "Insert my signature", delivered: ScenarioVocabulary.signatureCanonical),
            try line("snippet-address", reply: "Insert my address", delivered: ScenarioVocabulary.addressTemplate),
            try line("question"),
            try line("list", reply: "We need milk, eggs, bread,\ncoffee, and a bag of apples."),
            try line(
                "numbers-time",
                reply: "The meeting moved to 3:30 on Tuesday, and 42 people signed up for the workshop."),
            try line(
                "long-passage", reply: passage.replacingOccurrences(of: " Today we will", with: "\n\nToday we will")),
            try line("greeting"),
            try line("pangram"),
            try line("sentence"),
            try line(
                "longer", reply: longer.replacingOccurrences(of: "laptop, checked", with: "laptop \u{2014} checked"),
                cleaned: longer),
        ]
    }

    /// What the three dictionary clips are sent, written out, so the vocabulary step is pinned by more than itself.
    private static let sentWithTheDictionary = [
        "dict-azure-devops": "We track every bug in Azure DevOps and review the board on Monday morning.",
        "dict-github-copilot": "Ask GitHub Copilot to explain why the build is failing.",
        "dict-kubernetes": "The Kubernetes cluster restarted overnight after the upgrade.",
    ]

    /// Every line break turned into a space, the way a terminal target's text is flattened.
    private static func flattened(_ text: String) -> String {
        text.replacingOccurrences(of: "\r\n", with: " ").replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\r", with: " ")
    }

    // MARK: - Checks

    /// Every clip reached the recognizer once, in dictation order, as 16 kHz audio of the fixture's length that follows
    /// the fixture closely; sample for sample on the 16 kHz mono device every `exactEvery`th dictation played on.
    private func assertRecognized(_ script: PipelineScript, by harness: PipelineHarness, exactEvery: Int) throws {
        let heard = harness.recognizer.heard
        XCTAssertEqual(heard.map(\.clip), script.lines.map(\.clip.name))
        for (index, hearing) in heard.enumerated() {
            let clip = script.lines[index].clip
            XCTAssertEqual(hearing.sampleRate, 16_000, clip.name)
            XCTAssertLessThanOrEqual(
                abs(hearing.sampleCount - clip.pcm.count), ScenarioLimits.resamplerSlack, clip.name)
            if index % exactEvery == 0 {
                XCTAssertEqual(hearing.sampleCount, clip.pcm.count, clip.name)
                XCTAssertGreaterThan(hearing.correlation, 0.999_99, clip.name)
                XCTAssertEqual(hearing.lag, 0, clip.name)
            } else {
                XCTAssertGreaterThanOrEqual(hearing.correlation, ScenarioLimits.minimumCorrelation, clip.name)
            }
        }
    }

    /// The model was sent each transcript once, in tags, with the vocabulary rules applied, so the dictionary's
    /// spellings reached it, and never a snippet's template; its prompt asks for a single line exactly when the target
    /// flattens line breaks.
    private func assertCleanupRequests(_ script: PipelineScript, _ harness: PipelineHarness, singleLine: Bool) {
        let requests = harness.model.requests
        XCTAssertEqual(requests.map(\.transcript), script.lines.map { CleanupPrompt.wrapTranscript($0.sent) })
        for line in script.lines {
            if let expected = Self.sentWithTheDictionary[line.clip.name] {
                XCTAssertEqual(line.sent, expected, "\(line.clip.name) was not sent with the dictionary's spelling")
            }
        }
        for request in requests {
            for text in [ScenarioVocabulary.signatureTemplate, ScenarioVocabulary.addressTemplate, "Kind regards"] {
                XCTAssertFalse(request.transcript.contains(text), "a snippet template reached the model")
                XCTAssertFalse(request.writingStylePrompt.contains(text), "a snippet template reached the prompt")
            }
            XCTAssertEqual(request.singleLineMode, singleLine)
            XCTAssertEqual(request.writingStylePrompt.hasSuffix(CleanupPrompt.singleLineWritingStyle), singleLine)
            XCTAssertNil(request.maxOutputTokens, "a dictation sent an output ceiling")
        }
    }

    /// For every clip: recognized, its vocabulary corrected, sent to the model, answered, post-processed, delivered
    /// and handed to history, in that order; and recognition, delivery and history each in dictation order.
    private func assertWindowsOrder(_ script: PipelineScript, _ journal: PipelineJournal) {
        let steps: [PipelineJournal.Step] = [
            .recognized, .vocabularyCorrected, .cleanupRequested, .cleanupAnswered, .postProcessed, .delivered,
            .historyQueued,
        ]
        for line in script.lines {
            let positions = steps.map { journal.position(of: $0, for: line.clip.name) }
            XCTAssertFalse(positions.contains(nil), "\(line.clip.name) missed a step: \(positions)")
            let found = positions.compactMap { $0 }
            XCTAssertEqual(found, found.sorted(), "\(line.clip.name) took its steps out of order: \(positions)")
        }
        XCTAssertFalse(journal.all.contains { $0.clip == "?" }, "a step matched no clip: \(journal.all)")
        let names = script.lines.map(\.clip.name)
        XCTAssertEqual(journal.clips(at: .recognized), names)
        XCTAssertEqual(journal.clips(at: .delivered), names)
        XCTAssertEqual(journal.clips(at: .historyQueued), names)
    }

    /// The database holds one row per dictation, in delivery order, with exactly the text that went in and the target
    /// it went to.
    private func assertHistory(_ script: PipelineScript, _ harness: PipelineHarness, targetApp: String) throws {
        let rows = try harness.store.fetchDictationHistory(limit: 100)
        XCTAssertEqual(rows.map { $0.transcriptText ?? "" }, harness.injector.deliveries.map(\.text))
        XCTAssertEqual(rows.count, script.lines.count)
        XCTAssertTrue(rows.allSatisfy { $0.targetApp == targetApp }, "\(rows.map(\.targetApp))")
        XCTAssertTrue(rows.allSatisfy { $0.durationSeconds > 0 && $0.sampleCount > 0 })
    }

    /// Every presentation takes a newer revision than the one before, and the pill is hidden once every dictation is
    /// done.
    private func assertPresentationsOnlyMoveForward(_ harness: PipelineHarness) {
        let revisions = harness.presenter.presentations.map(\.revision)
        XCTAssertEqual(revisions, revisions.sorted(), "a presentation went backwards")
        XCTAssertEqual(Set(revisions).count, revisions.count, "two presentations shared a revision")
        XCTAssertEqual(harness.presenter.presentations.last?.overlay, .hidden)
        XCTAssertEqual(harness.presenter.presentations.last?.isRecording, false)
    }

    /// No run of four dictated words, no text sent for cleanup, no reply, no template and no target reached a log line
    /// or either unified log argument.
    private func assertNothingDictatedWasLogged(_ script: PipelineScript, _ log: ScenarioLogRecorder) {
        let texts =
            script.lines.flatMap { [$0.clip.text, $0.sent, $0.reply, $0.cleaned, $0.delivered] } + Self.neverLogged
        XCTAssertGreaterThan(log.renderings.count, 0, "nothing was logged, so nothing was checked")
        XCTAssertEqual(ScenarioPrivacy.leaks(of: texts, in: log), [], "dictated content reached the log")
    }
}
