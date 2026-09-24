import AppKit
import Foundation
import XCTest
import os

@testable import Scribe

// Fakes for every service `DictationController` drives, and a harness that wires them together. Everything a test
// orders runs on the main actor: fakes hold their calls at gates the test opens, the clock moves only when the test
// moves it, and waits are bounded counts of `Task.yield()`, never sleeps.

/// Yields until `condition` holds, and fails the test instead of hanging when it never does. Each yield lets the
/// main actor run whatever is queued on it (tasks, and the capture relay's main-queue deliveries).
@MainActor
func waitUntil(
    _ description: String,
    maxYields: Int = 10_000,
    file: StaticString = #filePath,
    line: UInt = #line,
    _ condition: () -> Bool
) async {
    var yields = 0
    while !condition() {
        guard yields < maxYields else {
            XCTFail("Timed out waiting until \(description)", file: file, line: line)
            return
        }
        await Task.yield()
        yields += 1
    }
}

/// Lets the main actor run what is queued on it, a bounded number of times, for a test that checks nothing happens.
@MainActor
func drainMainActor(yields: Int = 200) async {
    for _ in 0..<yields {
        await Task.yield()
    }
}

/// Values a test's callbacks collect, on the main actor.
@MainActor
final class Collected<Value> {
    var values: [Value] = []
}

/// A point a fake waits at until the test opens it with a value or fails it. A waiter whose task is cancelled throws
/// `CancellationError`, as a real stalled operation that observes cancellation would.
@MainActor
final class DictationGate<Value: Sendable> {
    private var outcome: Result<Value, any Error>?
    private var waiters: [UInt64: CheckedContinuation<Value, any Error>] = [:]
    private var nextWaiter: UInt64 = 0
    private(set) var arrivals = 0

    var waitingCount: Int {
        waiters.count
    }

    func wait() async throws -> Value {
        arrivals += 1
        if let outcome {
            return try outcome.get()
        }
        nextWaiter += 1
        let id = nextWaiter
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Value, any Error>) in
                if Task.isCancelled {
                    continuation.resume(throwing: CancellationError())
                } else {
                    waiters[id] = continuation
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.waiters.removeValue(forKey: id)?.resume(throwing: CancellationError())
            }
        }
    }

    func open(_ value: Value) {
        settle(.success(value))
    }

    /// Waits until the gate opens even when the waiting task is cancelled, like a recognizer that had already
    /// produced its transcript when the cancellation arrived.
    func waitIgnoringCancellation() async throws -> Value {
        arrivals += 1
        if let outcome {
            return try outcome.get()
        }
        nextWaiter += 1
        let id = nextWaiter
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Value, any Error>) in
            waiters[id] = continuation
        }
    }

    func fail(_ error: any Error) {
        settle(.failure(error))
    }

    private func settle(_ result: Result<Value, any Error>) {
        outcome = result
        let pending = waiters
        waiters.removeAll()
        for waiter in pending.values {
            waiter.resume(with: result)
        }
    }
}

/// The microphone. An open succeeds at once unless `holdsOpens`, when it waits for `completeOpen`; a stop returns
/// `samples` for a recording whose open succeeded and nil for one that never opened, as the engine does.
@MainActor
final class FakeCapture: DictationCapturing {
    struct Start {
        let owner: RecordingID
        let policy: CaptureStopPolicy
        let events: @Sendable (CaptureEvent) -> Void
    }

    var holdsOpens = false
    var openError: (any Error)?
    var samples = [Float](repeating: 0.25, count: 8_000)
    private(set) var starts: [Start] = []
    private(set) var stops: [RecordingID] = []
    private(set) var idleWaits = 0
    private var gates: [RecordingID: DictationGate<CaptureOpenOutcome>] = [:]
    private var opened: Set<RecordingID> = []
    private var handedOver: Set<RecordingID> = []

    func startOpening(
        owner: RecordingID,
        policy: CaptureStopPolicy,
        events: @escaping @Sendable (CaptureEvent) -> Void
    ) -> Task<CaptureOpenOutcome, Error> {
        starts.append(Start(owner: owner, policy: policy, events: events))
        if let openError {
            return Task { throw openError }
        }
        guard holdsOpens else {
            opened.insert(owner)
            return Task { .live }
        }
        let gate = DictationGate<CaptureOpenOutcome>()
        gates[owner] = gate
        return Task { try await gate.wait() }
    }

    func stop(owner: RecordingID) -> CapturedAudio? {
        stops.append(owner)
        guard opened.contains(owner), !handedOver.contains(owner) else { return nil }
        handedOver.insert(owner)
        return CapturedAudio(owner: owner, samples: samples, summary: Self.summary(sampleCount: samples.count))
    }

    func waitUntilIdle() async {
        idleWaits += 1
    }

    var pendingOpens: Int {
        gates.values.reduce(0) { $0 + $1.waitingCount }
    }

    /// Finishes a held open. `.live` makes the recording's audio available to its stop.
    func completeOpen(_ owner: RecordingID, _ outcome: CaptureOpenOutcome = .live) {
        if outcome == .live {
            opened.insert(owner)
        }
        gates[owner]?.open(outcome)
    }

    func failOpen(_ owner: RecordingID, _ error: any Error) {
        gates[owner]?.fail(error)
    }

    /// Posts an event through the sink the recording was started with, as the engine's audio thread would.
    func post(_ kind: CaptureEvent.Kind, for owner: RecordingID) {
        starts.last(where: { $0.owner == owner })?.events(CaptureEvent(owner: owner, kind: kind))
    }

    func startCount(for owner: RecordingID) -> Int {
        starts.filter { $0.owner == owner }.count
    }

    func stopCount(for owner: RecordingID) -> Int {
        stops.filter { $0 == owner }.count
    }

    static func summary(sampleCount: Int) -> AudioCaptureSummary {
        AudioCaptureSummary(
            startedAt: Date(timeIntervalSince1970: 1_000_000),
            stoppedAt: Date(timeIntervalSince1970: 1_000_001),
            sampleCount: sampleCount,
            sampleRate: 16_000,
            ending: .stoppedByOwner,
            signal: nil,
            acceptedBufferCount: 1,
            droppedBufferCount: 0,
            resamplerFlush: .notNeeded)
    }
}

/// Speech recognition, answering each call with the next scripted step, or `defaultText`.
@MainActor
final class FakeTranscriber: DictationTranscribing {
    enum Step {
        case text(String)
        case failure(any Error)
        case gate(DictationGate<String>)
        /// Waits at the gate even after its task is cancelled, then returns the transcript anyway.
        case gateIgnoringCancellation(DictationGate<String>)
        case engine(TranscriptionEngine)
    }

    var steps: [Step] = []
    var defaultText = "hello from the recognizer"
    private(set) var calls = 0
    private(set) var active = 0
    private(set) var mostActiveAtOnce = 0

    func transcribe(samples: [Float], sampleRate: Double) async throws -> TranscriptionResult {
        calls += 1
        active += 1
        mostActiveAtOnce = max(mostActiveAtOnce, active)
        defer { active -= 1 }
        let step = steps.isEmpty ? .text(defaultText) : steps.removeFirst()
        switch step {
        case .text(let text):
            return Self.result(text)
        case .failure(let error):
            throw error
        case .gate(let gate):
            return Self.result(try await gate.wait())
        case .gateIgnoringCancellation(let gate):
            return Self.result(try await gate.waitIgnoringCancellation())
        case .engine(let engine):
            return try await engine.transcribe(samples: samples, sampleRate: sampleRate)
        }
    }

    static func result(_ text: String) -> TranscriptionResult {
        TranscriptionResult(
            text: text,
            backend: .foundryLocal,
            diagnostics: TranscriptionDiagnostics(
                duration: .zero, deadline: .seconds(30), usedColdBudget: false, standardOutputBytes: 0,
                standardErrorBytes: 0, outputHeldAfterExit: false))
    }
}

/// A cleanup provider that records every request and answers with `reply`, the transcript it was given by default.
final class RecordingCleanupProvider: CleanupProvider {
    let id = "test-cleanup"
    let displayName = "Test cleanup"
    let usesLocalCleanupPrompt: Bool
    private let log = OSAllocatedUnfairLock<[CleanupRequest]>(initialState: [])
    private let reply: @Sendable (CleanupRequest) async throws -> String

    init(
        usesLocalCleanupPrompt: Bool = false,
        reply: @escaping @Sendable (CleanupRequest) async throws -> String = { request in
            RecordingCleanupProvider.transcript(in: request)
        }
    ) {
        self.usesLocalCleanupPrompt = usesLocalCleanupPrompt
        self.reply = reply
    }

    var requests: [CleanupRequest] {
        log.withLock { $0 }
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        log.withLock { $0.append(request) }
        let text = try await reply(request)
        return CleanupResponse(cleanedText: text, latency: 0, providerID: id, modelID: "test")
    }

    /// The transcript between the request's tags.
    static func transcript(in request: CleanupRequest) -> String {
        CleanupPrompt.stripTranscriptTags(request.transcript)
    }
}

/// A cleanup provider on the main actor, so a dictation's cleanup waits at the test's gates and resumes in exactly the
/// order the test opens them. Answers with `reply`, the transcript it was given by default, and records every request.
@MainActor
final class GatedCleanupProvider: CleanupProvider {
    nonisolated let id = "gated-cleanup"
    nonisolated let displayName = "Gated cleanup"
    nonisolated let usesLocalCleanupPrompt = false
    private(set) var requests: [CleanupRequest] = []
    var reply: (CleanupRequest) async throws -> String = { request in
        RecordingCleanupProvider.transcript(in: request)
    }

    func clean(_ request: CleanupRequest) async throws -> CleanupResponse {
        requests.append(request)
        let text = try await reply(request)
        return CleanupResponse(cleanedText: text, latency: 0, providerID: id, modelID: "gated")
    }
}

@MainActor
final class FakeCleanup: DictationCleaning {
    var isEnabled = false
    var providerError: (any Error)?
    var cleanupProvider: any CleanupProvider
    private(set) var invalidations = 0

    init(provider: any CleanupProvider = GatedCleanupProvider()) {
        cleanupProvider = provider
    }

    /// The default provider, when the test did not replace it.
    var gated: GatedCleanupProvider? {
        cleanupProvider as? GatedCleanupProvider
    }

    func provider() async throws -> any CleanupProvider {
        if let providerError {
            throw providerError
        }
        return cleanupProvider
    }

    func invalidate() {
        invalidations += 1
    }
}

@MainActor
final class FakeTargeting: DictationTargeting {
    static let editorBundle = "com.example.editor"
    static let otherBundle = "com.example.other"

    static let editor = DictationTarget(
        injection: InjectionTarget(processIdentifier: 100, bundleIdentifier: editorBundle),
        bundleIdentifier: editorBundle,
        processName: "Editor")
    static let other = DictationTarget(
        injection: InjectionTarget(processIdentifier: 200, bundleIdentifier: otherBundle),
        bundleIdentifier: otherBundle,
        processName: "Other")
    static let terminal = DictationTarget(
        injection: InjectionTarget(processIdentifier: 300, bundleIdentifier: "com.apple.Terminal"),
        bundleIdentifier: "com.apple.Terminal",
        processName: "Terminal")
    /// What `TextInjector.captureTarget()` returns when nothing identifies the focused application.
    static let unknown = DictationTarget(injection: nil, bundleIdentifier: nil, processName: nil)

    var next = FakeTargeting.editor
    private(set) var captures = 0

    func captureTarget() -> DictationTarget {
        captures += 1
        return next
    }
}

@MainActor
final class FakeInjector: DictationInjecting {
    struct Delivery {
        let text: String
        let target: InjectionTarget?
    }

    var result = InjectionResult(delivery: .pasted, clipboard: .pasted, restore: .restored)
    /// Holds the next delivery until opened.
    var holdNext: DictationGate<Void>?
    private(set) var deliveries: [Delivery] = []
    private(set) var barrierCalls = 0
    private var inFlight = 0
    private var idleWaiters: [CheckedContinuation<Void, Never>] = []

    var texts: [String] {
        deliveries.map(\.text)
    }

    func inject(text: String, into target: InjectionTarget?, shiftReturnLineBreaks: Bool) async -> InjectionResult {
        deliveries.append(Delivery(text: text, target: target))
        inFlight += 1
        if let gate = holdNext {
            holdNext = nil
            _ = try? await gate.wait()
        }
        inFlight -= 1
        if inFlight == 0 {
            let waiting = idleWaiters
            idleWaiters.removeAll()
            waiting.forEach { $0.resume() }
        }
        return result
    }

    func waitUntilIdle() async {
        barrierCalls += 1
        guard inFlight > 0 else { return }
        await withCheckedContinuation { idleWaiters.append($0) }
    }
}

final class FakeHistory: DictationHistoryWriting {
    private struct State: Sendable {
        var records: [DictationHistoryRecord] = []
        var dictationIDs: [UInt64] = []
        var completed = false
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    func enqueue(_ record: DictationHistoryRecord, dictationID: UInt64) -> Bool {
        state.withLock { state in
            guard !state.completed else { return false }
            state.records.append(record)
            state.dictationIDs.append(dictationID)
            return true
        }
    }

    func complete(timeout: TimeInterval) -> HistoryDrainResult {
        state.withLock { $0.completed = true }
        return HistoryDrainResult(drained: true, stillWriting: 0, abandoned: 0)
    }

    var records: [DictationHistoryRecord] {
        state.withLock { $0.records }
    }

    var dictationIDs: [UInt64] {
        state.withLock { $0.dictationIDs }
    }

    var isCompleted: Bool {
        state.withLock { $0.completed }
    }
}

@MainActor
final class FakePresenter: DictationPresenting {
    private(set) var presentations: [DictationPresentation] = []

    func present(_ presentation: DictationPresentation) {
        presentations.append(presentation)
    }

    var last: DictationPresentation? {
        presentations.last
    }

    var overlays: [OverlayState] {
        presentations.map(\.overlay)
    }

    /// Each notice as it appeared: a notice presented again while it is still up (the next change of something else)
    /// counts once.
    func noticesShown() -> [OverlayNotice] {
        var shown: [OverlayNotice] = []
        var previous: OverlayState?
        for presentation in presentations {
            if case .notice(let notice) = presentation.overlay, presentation.overlay != previous {
                shown.append(notice)
            }
            previous = presentation.overlay
        }
        return shown
    }
}

@MainActor
final class FakeNotifier: DictationNotifying {
    private(set) var notices: [DictationNotice] = []

    func notify(_ notice: DictationNotice) {
        notices.append(notice)
    }
}

@MainActor
final class FakeTriggers: DictationTriggerSource {
    private(set) var cancelledToggles = 0

    func cancelToggle() {
        cancelledToggles += 1
    }
}

/// A clock that moves only when the test moves it. A sleeper wakes once `advance` reaches its deadline, or throws
/// `CancellationError` when its task is cancelled first. Main-actor state, like the controller's.
@MainActor
final class ManualDictationClock: DictationClock {
    private struct Sleeper {
        let id: UInt64
        let deadline: ContinuousClock.Instant
        let continuation: CheckedContinuation<Void, any Error>
    }

    private(set) var now = ContinuousClock.now
    private var sleepers: [Sleeper] = []
    private var nextID: UInt64 = 0

    var sleeperCount: Int {
        sleepers.count
    }

    func advance(by duration: Duration) {
        now = now.advanced(by: duration)
        let current = now
        let due = sleepers.filter { $0.deadline <= current }
        sleepers.removeAll { $0.deadline <= current }
        for sleeper in due {
            sleeper.continuation.resume()
        }
    }

    func sleep(until deadline: ContinuousClock.Instant) async throws {
        nextID += 1
        let id = nextID
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, any Error>) in
                if Task.isCancelled {
                    continuation.resume(throwing: CancellationError())
                } else if deadline <= now {
                    continuation.resume()
                } else {
                    sleepers.append(Sleeper(id: id, deadline: deadline, continuation: continuation))
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.cancelSleeper(id)
            }
        }
    }

    private func cancelSleeper(_ id: UInt64) {
        guard let index = sleepers.firstIndex(where: { $0.id == id }) else { return }
        sleepers.remove(at: index).continuation.resume(throwing: CancellationError())
    }
}

/// Every fake wired to one controller.
@MainActor
final class DictationHarness {
    /// Right Option: held while talking.
    static let holdKey = HotkeyBinding(keyCode: 61)
    /// Caps Lock: tapped on and off.
    static let toggleKey = HotkeyBinding(keyCode: 57)

    let capture: FakeCapture
    let transcriber: FakeTranscriber
    let cleanup: FakeCleanup
    let targeting: FakeTargeting
    let fakeInjector: FakeInjector
    let history: FakeHistory
    let gate: StartupGate
    let rules: DictationRules
    let presenter: FakePresenter
    let notifier: FakeNotifier
    let triggers: FakeTriggers
    let clock: ManualDictationClock
    let activity: ForegroundActivity
    let recovery: LastTranscriptStore
    let reports: PipelineReportStore
    let controller: DictationController

    init(
        configuration: DictationController.Configuration = DictationController.Configuration(),
        rulesLoaded: Bool = true,
        injector: (any DictationInjecting)? = nil
    ) {
        let capture = FakeCapture()
        let transcriber = FakeTranscriber()
        let cleanup = FakeCleanup()
        let targeting = FakeTargeting()
        let fakeInjector = FakeInjector()
        let history = FakeHistory()
        let gate = StartupGate()
        let rules = DictationRules(gate: gate)
        let presenter = FakePresenter()
        let notifier = FakeNotifier()
        let triggers = FakeTriggers()
        let clock = ManualDictationClock()
        let activity = ForegroundActivity()
        let recovery = LastTranscriptStore()
        let reports = PipelineReportStore()
        let controller = DictationController(
            services: DictationController.Services(
                capture: capture,
                transcriber: transcriber,
                cleanup: cleanup,
                targeting: targeting,
                injector: injector ?? fakeInjector,
                history: history,
                rules: rules,
                presenter: presenter,
                notifier: notifier,
                clock: clock,
                activity: activity,
                recovery: recovery,
                reports: reports),
            configuration: configuration)
        controller.triggers = triggers
        if rulesLoaded {
            gate.open(.ready)
        }
        self.capture = capture
        self.transcriber = transcriber
        self.cleanup = cleanup
        self.targeting = targeting
        self.fakeInjector = fakeInjector
        self.history = history
        self.gate = gate
        self.rules = rules
        self.presenter = presenter
        self.notifier = notifier
        self.triggers = triggers
        self.clock = clock
        self.activity = activity
        self.recovery = recovery
        self.reports = reports
        self.controller = controller
    }

    /// Applies rules as the app's refresher would.
    func load(
        dictionary: [DictionaryEntry] = [], snippets: [Snippet] = [], profiles: [AppProfile] = [],
        openingGate: Bool = true
    ) {
        rules.apply(
            PersistenceRuleSet(dictionaryEntries: dictionary, snippets: snippets, appProfiles: profiles),
            libraryEntries: [])
        if openingGate {
            gate.open(.ready)
        }
    }

    /// Presses `binding` and waits until the microphone has been asked for. Returns the recording, or nil when the
    /// press was turned away.
    @discardableResult
    func press(_ binding: HotkeyBinding = DictationHarness.holdKey) async -> RecordingID? {
        let before = capture.starts.count
        guard controller.hotkeyPressed(binding) else { return nil }
        await waitUntil("the microphone is asked for") { self.capture.starts.count > before }
        return capture.starts.last?.owner
    }

    func waitUntilLive(file: StaticString = #filePath, line: UInt = #line) async {
        await waitUntil("the recording is live", file: file, line: line) { self.controller.isRecordingLive }
    }

    func release(_ binding: HotkeyBinding = DictationHarness.holdKey) {
        controller.hotkeyReleased(binding, cause: .keyReleased)
    }

    /// Press, wait until live, release.
    @discardableResult
    func dictate(_ binding: HotkeyBinding = DictationHarness.holdKey) async -> RecordingID? {
        guard let id = await press(binding) else { return nil }
        await waitUntilLive()
        release(binding)
        return id
    }

    /// `press`, failing the test when the press was turned away.
    func pressAdmitted(
        _ binding: HotkeyBinding = DictationHarness.holdKey, file: StaticString = #filePath, line: UInt = #line
    ) async throws -> RecordingID {
        let id = await press(binding)
        return try XCTUnwrap(id, "the press was turned away", file: file, line: line)
    }

    /// `dictate`, failing the test when the press was turned away.
    @discardableResult
    func dictateAdmitted(
        _ binding: HotkeyBinding = DictationHarness.holdKey, file: StaticString = #filePath, line: UInt = #line
    ) async throws -> RecordingID {
        let id = await dictate(binding)
        return try XCTUnwrap(id, "the press was turned away", file: file, line: line)
    }

    func waitUntilProcessed(file: StaticString = #filePath, line: UInt = #line) async {
        await waitUntil("every dictation is processed", file: file, line: line) {
            self.controller.processingCount == 0
        }
    }

    var lastOverlay: OverlayState? {
        presenter.last?.overlay
    }
}

/// A failure a fake throws. Its name and payload carry nothing a log could leak.
struct DictationTestFailure: Error, Equatable {
    let code: Int
}
