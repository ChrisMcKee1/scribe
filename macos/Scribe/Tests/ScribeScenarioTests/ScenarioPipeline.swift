import CoreGraphics
import Foundation
import XCTest
import os

@testable import Scribe

/// The course one clip is expected to take through a pipeline scenario.
struct PipelineLine: Sendable {
    let clip: ScenarioClip
    /// The clip's transcript with the vocabulary rules applied: what the stand-in model is sent.
    let sent: String
    /// What the stand-in model answers when it is sent `sent`.
    let reply: String
    /// The reply as the pipeline carries it on, after the response guard, which also normalizes dashes.
    let cleaned: String
    /// The text that must reach the target.
    let delivered: String
}

/// The lines of one pipeline scenario, and the lookups the fakes use to tell which clip a step was about.
struct PipelineScript: Sendable {
    let lines: [PipelineLine]

    func line(raw: String) -> PipelineLine? {
        lines.first { $0.clip.text == raw }
    }

    func line(sent: String) -> PipelineLine? {
        lines.first { $0.sent == sent }
    }

    func line(cleaned: String) -> PipelineLine? {
        lines.first(where: { $0.cleaned == cleaned }) ?? line(raw: cleaned)
    }

    func line(delivered: String) -> PipelineLine? {
        lines.first { $0.delivered == delivered }
    }
}

/// The dictionary and snippets of the Windows scenario suite (`tools/Scribe.AsrCheck`, `TestVocabulary`), written for
/// these same fixtures.
enum ScenarioVocabulary {
    static let signatureTrigger = "insert my signature"
    /// The template passes through the dictionary after it expands, so "scribe" in it comes out canonical: snippets
    /// first, then the dictionary, is part of the contract.
    static let signatureTemplate = "Kind regards,\nsent from scribe"
    static let signatureCanonical = "Kind regards,\nsent from Scribe"
    static let addressTrigger = "insert my address"
    static let addressTemplate = "1 Example Street\nSpringfield"

    static let dictionary: [DictionaryEntry] = [
        DictionaryEntry(pattern: "azure devops", replacement: "Azure DevOps"),
        DictionaryEntry(pattern: "azure dev ops", replacement: "Azure DevOps"),
        DictionaryEntry(pattern: "azure devos", replacement: "Azure DevOps"),
        DictionaryEntry(pattern: "github copilot", replacement: "GitHub Copilot"),
        DictionaryEntry(pattern: "github co-pilot", replacement: "GitHub Copilot"),
        DictionaryEntry(pattern: "git hub copilot", replacement: "GitHub Copilot"),
        DictionaryEntry(pattern: "gethub copilot", replacement: "GitHub Copilot"),
        DictionaryEntry(pattern: "kubernetes", replacement: "Kubernetes"),
        DictionaryEntry(pattern: "cuba ernets", replacement: "Kubernetes"),
        DictionaryEntry(pattern: "scribe", replacement: "Scribe"),
    ]

    static let snippets: [Snippet] = [
        Snippet(phrase: signatureTrigger, template: signatureTemplate),
        Snippet(phrase: addressTrigger, template: addressTemplate),
    ]

    /// `text` with this vocabulary's one-line rules applied, as the pipeline corrects a transcript before it sends it
    /// for cleanup (`TextPostProcessor.correctVocabulary`).
    static func corrected(_ text: String) -> String {
        let processor = TextPostProcessor()
        processor.reload(dictionaryEntries: dictionary, snippets: snippets)
        return processor.correctVocabulary(text).text
    }
}

enum PipelineScenarioError: Error {
    case unexpectedRecognition
    case pressRefused
}

/// What the pipeline's fakes saw, in the order they saw it.
final class PipelineJournal: Sendable {
    enum Step: String, Sendable {
        case recognized
        case vocabularyCorrected
        case cleanupRequested
        case cleanupAnswered
        case postProcessed
        case delivered
        case historyQueued
    }

    struct Entry: Equatable, Sendable {
        let step: Step
        /// The clip the step was about, or `?` when its text matched no line of the script.
        let clip: String
    }

    private let entries = OSAllocatedUnfairLock<[Entry]>(initialState: [])

    func record(_ step: Step, _ clip: String) {
        entries.withLock { $0.append(Entry(step: step, clip: clip)) }
    }

    var all: [Entry] {
        entries.withLock { $0 }
    }

    /// Where `step` happened for `clip`, or nil when it never did.
    func position(of step: Step, for clip: String) -> Int? {
        all.firstIndex(of: Entry(step: step, clip: clip))
    }

    /// The clips `step` happened for, in order.
    func clips(at step: Step) -> [String] {
        all.filter { $0.step == step }.map(\.clip)
    }
}

/// Speech recognition that knows which fixture it is hearing: the harness queues the clip each dictation plays, and the
/// recognizer measures how closely the audio the pipeline hands it follows that clip, then answers with the manifest's
/// text, as the real recognizer would for a perfect decode.
@MainActor
final class FixtureRecognizer: DictationTranscribing {
    struct Heard: Sendable {
        let clip: String
        let sampleCount: Int
        let sampleRate: Double
        let correlation: Double
        let lag: Int
    }

    private let journal: PipelineJournal
    private var queued: [ScenarioClip] = []
    private(set) var heard: [Heard] = []

    init(journal: PipelineJournal) {
        self.journal = journal
    }

    func expect(_ clip: ScenarioClip) {
        queued.append(clip)
    }

    func transcribe(samples: [Float], sampleRate: Double) async throws -> TranscriptionResult {
        guard !queued.isEmpty else { throw PipelineScenarioError.unexpectedRecognition }
        let clip = queued.removeFirst()
        let match = ScenarioAudio.similarity(of: samples, to: clip.samples, maxLag: 64)
        heard.append(
            Heard(
                clip: clip.name, sampleCount: samples.count, sampleRate: sampleRate, correlation: match.correlation,
                lag: match.lag))
        journal.record(.recognized, clip.name)
        return TranscriptionResult(
            text: clip.text,
            backend: .foundryLocal,
            diagnostics: TranscriptionDiagnostics(
                duration: .zero, deadline: .seconds(30), usedColdBudget: false, standardOutputBytes: 0,
                standardErrorBytes: 0, outputHeldAfterExit: false))
    }
}

/// A stand-in for the cleanup model: it answers each transcript it is sent with the reply its line scripts, and a clip
/// can be held until the test lets the model answer it.
final class ScenarioCleanupModel: CleanupProvider {
    let id = "scenario-model"
    let displayName = "Scenario model"
    let script: PipelineScript
    let journal: PipelineJournal
    /// Advanced as each request arrives, before any hold.
    let arrivals = ScenarioCounter()
    private let recorded = OSAllocatedUnfairLock<[CleanupRequest]>(initialState: [])
    private let holds = OSAllocatedUnfairLock<[String: ScenarioLatch]>(initialState: [:])

    init(script: PipelineScript, journal: PipelineJournal) {
        self.script = script
        self.journal = journal
    }

    /// Every request, in the order it arrived.
    var requests: [CleanupRequest] {
        recorded.withLock { $0 }
    }

    /// Holds the answer to `clip`'s request until the returned latch opens.
    func hold(_ clip: ScenarioClip) -> ScenarioLatch {
        let latch = ScenarioLatch()
        holds.withLock { $0[clip.name] = latch }
        return latch
    }

    /// Lets every held answer go, for good.
    func releaseAll() {
        for latch in holds.withLock({ Array($0.values) }) {
            latch.open()
        }
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        let sent = CleanupPrompt.stripTranscriptTags(request.transcript)
        let line = script.line(sent: sent)
        let clip = line?.clip.name ?? "?"
        recorded.withLock { $0.append(request) }
        journal.record(.cleanupRequested, clip)
        arrivals.increment()
        if let latch = holds.withLock({ $0[clip] }) {
            try await latch.wait()
        }
        journal.record(.cleanupAnswered, clip)
        return CleanupResponse(cleanedText: line?.reply ?? sent, latency: 0, providerID: id, modelID: "scenario")
    }
}

@MainActor
final class ScenarioCleanupSource: DictationCleaning {
    let model: ScenarioCleanupModel
    var isEnabled = true
    private(set) var invalidations = 0

    init(model: ScenarioCleanupModel) {
        self.model = model
    }

    func provider() async throws -> any CleanupProvider {
        model
    }

    func invalidate() {
        invalidations += 1
    }
}

/// The production rules (`DictationRules`: `StartupGate` and `TextPostProcessor`), with every rule step journaled.
@MainActor
final class ScenarioRules: DictationRuleSource {
    let rules: DictationRules
    let script: PipelineScript
    let journal: PipelineJournal
    /// Advanced after each post-processing step: with cleanup off, or on the reply, never for the vocabulary step.
    let processed = ScenarioCounter()

    init(rules: DictationRules, script: PipelineScript, journal: PipelineJournal) {
        self.rules = rules
        self.script = script
        self.journal = journal
    }

    var isLoaded: Bool {
        rules.isLoaded
    }

    func waitUntilLoaded() async -> StartupGate.State {
        await rules.waitUntilLoaded()
    }

    var appProfiles: [AppProfile] {
        rules.appProfiles
    }

    func postProcess(_ text: String) -> TextPostProcessingResult {
        let result = rules.postProcess(text)
        journal.record(.postProcessed, script.line(cleaned: text)?.clip.name ?? "?")
        processed.increment()
        return result
    }

    func correctVocabulary(_ text: String) -> VocabularyPass {
        let pass = rules.correctVocabulary(text)
        journal.record(.vocabularyCorrected, script.line(raw: text)?.clip.name ?? "?")
        return pass
    }

    func finishAfterCleanup(_ reply: String, after pass: VocabularyPass) -> TextPostProcessingResult {
        let result = rules.finishAfterCleanup(reply, after: pass)
        journal.record(.postProcessed, script.line(cleaned: reply)?.clip.name ?? "?")
        processed.increment()
        return result
    }
}

@MainActor
final class ScenarioTargeting: DictationTargeting {
    var next: DictationTarget

    init(_ target: DictationTarget) {
        next = target
    }

    func captureTarget() -> DictationTarget {
        next
    }
}

/// Delivery that always succeeds by paste, and journals what it was given.
@MainActor
final class ScenarioInjector: DictationInjecting {
    struct Delivery {
        let text: String
        let bundleIdentifier: String?
    }

    private let script: PipelineScript
    private let journal: PipelineJournal
    private(set) var deliveries: [Delivery] = []

    init(script: PipelineScript, journal: PipelineJournal) {
        self.script = script
        self.journal = journal
    }

    func inject(text: String, into target: InjectionTarget?, shiftReturnLineBreaks: Bool) async -> InjectionResult {
        deliveries.append(Delivery(text: text, bundleIdentifier: target?.bundleIdentifier))
        journal.record(.delivered, script.line(delivered: text)?.clip.name ?? "?")
        return InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored)
    }

    func waitUntilIdle() async {}
}

/// The production history writer and store, with every entry journaled as it is handed over.
final class ScenarioHistory: DictationHistoryWriting {
    let writer: HistoryWriter
    let script: PipelineScript
    let journal: PipelineJournal
    /// Advanced after each entry is handed to the writer.
    let queued = ScenarioCounter()

    init(writer: HistoryWriter, script: PipelineScript, journal: PipelineJournal) {
        self.writer = writer
        self.script = script
        self.journal = journal
    }

    func enqueue(_ record: DictationHistoryRecord, dictationID: UInt64) -> Bool {
        journal.record(.historyQueued, script.line(delivered: record.transcriptText ?? "")?.clip.name ?? "?")
        let accepted = writer.enqueue(record, dictationID: dictationID)
        queued.increment()
        return accepted
    }

    func complete(timeout: TimeInterval) -> HistoryDrainResult {
        writer.complete(timeout: timeout)
    }
}

@MainActor
final class ScenarioPresenter: DictationPresenting {
    private(set) var presentations: [DictationPresentation] = []
    private var listening: ScenarioLatch?

    /// A latch the next presentation of a live recording opens.
    func nextListening() -> ScenarioLatch {
        let latch = ScenarioLatch()
        listening = latch
        return latch
    }

    func present(_ presentation: DictationPresentation) {
        presentations.append(presentation)
        if case .listening = presentation.overlay {
            listening?.open()
            listening = nil
        }
    }
}

@MainActor
final class ScenarioNotifier: DictationNotifying {
    private(set) var notices: [DictationNotice] = []

    func notify(_ notice: DictationNotice) {
        notices.append(notice)
    }
}

@MainActor
final class ScenarioTriggers: DictationTriggerSource {
    private(set) var settled: [HotkeyBinding] = []

    func cancelToggle(_ binding: HotkeyBinding) {
        settled.append(binding)
    }
}

/// A clock that never moves, so no duration ceiling or notice ends on its own during a scenario. A sleeper wakes only
/// by cancellation, or when the clock closes at teardown.
@MainActor
final class ScenarioClock: DictationClock {
    let now = ContinuousClock.now
    private var sleepers: [UInt64: CheckedContinuation<Void, any Error>] = [:]
    private var nextID: UInt64 = 0
    private var isClosed = false

    func sleep(until deadline: ContinuousClock.Instant) async throws {
        nextID += 1
        let id = nextID
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
                if isClosed || Task.isCancelled {
                    continuation.resume(throwing: CancellationError())
                } else {
                    sleepers[id] = continuation
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.sleepers.removeValue(forKey: id)?.resume(throwing: CancellationError())
            }
        }
    }

    /// Teardown: every sleeper, now and later, throws `CancellationError`.
    func close() {
        isClosed = true
        let pending = sleepers
        sleepers.removeAll()
        for sleeper in pending.values {
            sleeper.resume(throwing: CancellationError())
        }
    }
}

/// One `DictationController` wired to the production capture engine (fed by scenario devices), the production rules,
/// history writer and store, and fakes only for the recognizer, the model, the target and delivery. Everything the test
/// orders runs on the main actor, and every wait is an explicit signal under the watchdog.
@MainActor
final class PipelineHarness {
    /// Right Option, a key held while talking, so no silence ends a recording.
    static let holdKey = HotkeyBinding(keyCode: 61)

    static let editor = DictationTarget(
        injection: InjectionTarget(processIdentifier: 4_242, bundleIdentifier: "com.example.canary-editor"),
        bundleIdentifier: "com.example.canary-editor",
        processName: "CanaryEditor")

    static let terminal = DictationTarget(
        injection: InjectionTarget(processIdentifier: 4_343, bundleIdentifier: "com.apple.Terminal"),
        bundleIdentifier: "com.apple.Terminal",
        processName: "Terminal")

    let script: PipelineScript
    let journal: PipelineJournal
    let devices: ScenarioDeviceQueue
    let engine: AudioCaptureEngine
    let recognizer: FixtureRecognizer
    let model: ScenarioCleanupModel
    let cleanup: ScenarioCleanupSource
    let targeting: ScenarioTargeting
    let injector: ScenarioInjector
    let store: PersistenceStore
    let history: ScenarioHistory
    let gate: StartupGate
    let rules: ScenarioRules
    let presenter: ScenarioPresenter
    let notifier: ScenarioNotifier
    let triggers: ScenarioTriggers
    let clock: ScenarioClock
    let recovery: LastTranscriptStore
    let reports: PipelineReportStore
    let controller: DictationController
    private var streams: [ScenarioStream] = []

    init(script: PipelineScript, databaseURL: URL, target: DictationTarget) throws {
        let journal = PipelineJournal()
        let devices = ScenarioDeviceQueue()
        let engine = AudioCaptureEngine(makeDevice: devices.makeDevice)
        let model = ScenarioCleanupModel(script: script, journal: journal)
        let store = PersistenceStore(databaseURL: databaseURL)
        try store.initialize()
        let gate = StartupGate()
        let dictationRules = DictationRules(gate: gate)
        dictationRules.apply(
            PersistenceRuleSet(
                dictionaryEntries: ScenarioVocabulary.dictionary, snippets: ScenarioVocabulary.snippets,
                appProfiles: []),
            libraryEntries: [])
        gate.open(.ready)
        let recognizer = FixtureRecognizer(journal: journal)
        let cleanup = ScenarioCleanupSource(model: model)
        let targeting = ScenarioTargeting(target)
        let injector = ScenarioInjector(script: script, journal: journal)
        let history = ScenarioHistory(writer: HistoryWriter(recorder: store), script: script, journal: journal)
        let rules = ScenarioRules(rules: dictationRules, script: script, journal: journal)
        let presenter = ScenarioPresenter()
        let notifier = ScenarioNotifier()
        let triggers = ScenarioTriggers()
        let clock = ScenarioClock()
        let recovery = LastTranscriptStore()
        let reports = PipelineReportStore()
        let controller = DictationController(
            services: DictationController.Services(
                capture: LiveDictationCapture(engine: engine),
                transcriber: recognizer,
                cleanup: cleanup,
                targeting: targeting,
                injector: injector,
                history: history,
                rules: rules,
                presenter: presenter,
                notifier: notifier,
                clock: clock,
                activity: ForegroundActivity(),
                recovery: recovery,
                reports: reports))
        controller.triggers = triggers

        self.script = script
        self.journal = journal
        self.devices = devices
        self.engine = engine
        self.recognizer = recognizer
        self.model = model
        self.cleanup = cleanup
        self.targeting = targeting
        self.injector = injector
        self.store = store
        self.history = history
        self.gate = gate
        self.rules = rules
        self.presenter = presenter
        self.notifier = notifier
        self.triggers = triggers
        self.clock = clock
        self.recovery = recovery
        self.reports = reports
        self.controller = controller
    }

    /// Plays `line`'s clip on a device at `sampleRate` laid out as `layout`: presses the key, waits until the pill
    /// shows the recording and the device has delivered every buffer, releases the key, and, when `waitingForHistory`,
    /// waits until the dictation has handed its history entry to the writer, which is the last step of its processing.
    func dictate(
        _ line: PipelineLine,
        at sampleRate: Double,
        layout: ScenarioChannelLayout,
        encoding: ScenarioDeviceAudio.Encoding,
        seed: UInt64,
        waitingForHistory: Bool = true
    ) async throws {
        let audio = try ScenarioDeviceAudio.device(
            playing: line.clip.samples, at: sampleRate, layout: layout, encoding: encoding, seed: seed)
        let device = ScenarioCaptureDevice(audio: audio)
        devices.enqueue(device)
        recognizer.expect(line.clip)
        let listening = presenter.nextListening()
        let queued = history.queued
        let queuedBefore = queued.value
        guard controller.hotkeyPressed(Self.holdKey) else { throw PipelineScenarioError.pressRefused }

        let started = device.started
        try await underWatchdog("the microphone to open for \(line.clip.name)") { try await started.wait() }
        let stream = device.stream(
            frameCounts: ScenarioAudio.frameCounts(total: audio.frameCount, seed: seed, within: 64...4_800))
        streams.append(stream)
        try await underWatchdog("the device to deliver \(line.clip.name)") { try await stream.finished.wait() }
        try await underWatchdog("the pill to show \(line.clip.name) recording") { try await listening.wait() }
        controller.hotkeyReleased(Self.holdKey, cause: .keyReleased)

        guard waitingForHistory else { return }
        try await underWatchdog("\(line.clip.name) to reach history") {
            try await queued.wait(atLeast: queuedBefore + 1)
        }
    }

    /// Shuts the controller down, which drains the history writer, and returns what that did.
    func shutDown() async throws -> DictationShutdownReport {
        let controller = controller
        return try await underWatchdog("the controller to shut down") { @MainActor in
            await controller.shutDown()
        }
    }

    /// Teardown: lets go of everything a test may have left held, then shuts the controller down and waits for the
    /// engine's device work, so nothing of the test runs on once it has ended.
    func tearDown() async {
        for stream in streams {
            stream.resume()
        }
        model.releaseAll()
        clock.close()
        _ = try? await shutDown()
        let engine = engine
        _ = try? await underWatchdog("the capture engine to go idle") { await engine.waitUntilIdle() }
    }
}

extension XCTestCase {
    /// A harness whose teardown runs when the test ends.
    @MainActor
    func makePipelineHarness(script: PipelineScript, target: DictationTarget) throws -> PipelineHarness {
        let directory = try makeScenarioDirectory("pipeline")
        let harness = try PipelineHarness(
            script: script, databaseURL: directory.appendingPathComponent("scribe.db"), target: target)
        addTeardownBlock { @MainActor in
            await harness.tearDown()
        }
        return harness
    }
}
