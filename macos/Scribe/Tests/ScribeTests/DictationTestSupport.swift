import AVFoundation
import AppKit
import CoreGraphics
import Foundation
import XCTest
import os

@testable import Scribe

// Fakes for every service `DictationController` drives, and a harness that wires them together. Everything a test
// orders runs on the main actor: fakes hold their calls at gates the test opens, the clock moves only when the test
// moves it, and a test waits for an explicit signal (a gate's waiter, a controller checkpoint, a count) before it
// checks what did or did not happen, so no order depends on time. Time appears only as failure bounds: `waitUntil`
// gives up after a number of yields, `pollUntil` (for work on other threads, the capture engine's control queue)
// after a real deadline, and `bounded`, `within` and `HeldFlush` are watchdogs around waits a regression could strand.
// A harness settles every gate its test left open and shuts its controller down in the test's teardown.

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

/// Waits until `condition` holds while work on other threads (the capture engine's control queue, a child process)
/// makes progress, checking between short sleeps, and fails the test instead of hanging when `seconds` pass first.
/// The sleeps only pace the checks; the order is still decided by `condition`.
@MainActor
func pollUntil(
    _ description: String,
    within seconds: Double = 10,
    file: StaticString = #filePath,
    line: UInt = #line,
    _ condition: () -> Bool
) async {
    let deadline = ContinuousClock.now.advanced(by: .milliseconds(Int(seconds * 1_000)))
    while !condition() {
        guard ContinuousClock.now < deadline else {
            XCTFail("Timed out polling until \(description)", file: file, line: line)
            return
        }
        try? await Task.sleep(for: .milliseconds(2))
    }
}

/// Awaits `operation` under a watchdog (`finishes(within:)`): fails the test and returns nil, instead of hanging,
/// when it has not finished within `seconds`.
@MainActor
func bounded<Value: Sendable>(
    _ description: String,
    within seconds: Double = 30,
    file: StaticString = #filePath,
    line: UInt = #line,
    _ operation: @escaping @MainActor @Sendable () async -> Value
) async -> Value? {
    let result = Collected<Value>()
    let finished = await finishes(within: seconds) { @MainActor in
        result.values.append(await operation())
    }
    XCTAssertTrue(finished, "Timed out waiting for \(description)", file: file, line: line)
    return result.values.first
}

/// Values a test's callbacks collect, on the main actor.
@MainActor
final class Collected<Value> {
    var values: [Value] = []
}

/// What `within` throws when its watchdog fires.
struct WatchdogTimeout: Error, CustomStringConvertible {
    let description: String
}

/// `bounded` for an operation that throws: returns its value, rethrows its error, and throws `WatchdogTimeout` (after
/// failing the test) when it has not finished within `seconds`.
@MainActor
func within<Value: Sendable>(
    _ description: String,
    seconds: Double = 30,
    file: StaticString = #filePath,
    line: UInt = #line,
    _ operation: @escaping @MainActor @Sendable () async throws -> Value
) async throws -> Value {
    let outcome = await bounded(description, within: seconds, file: file, line: line) {
        () -> Result<Value, any Error> in
        do {
            return .success(try await operation())
        } catch {
            return .failure(error)
        }
    }
    guard let outcome else {
        throw WatchdogTimeout(description: "timed out waiting for \(description)")
    }
    return try outcome.get()
}

/// A resampler flush the capture engine runs on its control queue, held there until the test lets it go. Only the
/// first flush is held. A watchdog on another queue lets it go by itself after `watchdog` seconds when the test has not,
/// and records that it did, so a regression that waits for the flush on the caller's thread neither hangs the suite nor
/// passes: the test finds the flush let go by the watchdog instead of still held. No assertion measures time.
final class HeldFlush: Sendable {
    enum Release: Equatable, Sendable {
        case test
        case watchdog
    }

    private struct State: Sendable {
        var flushes = 0
        var isHeld = false
        var releasedBy: Release?
    }

    private let state = OSAllocatedUnfairLock(initialState: State())
    private let gate = DispatchSemaphore(value: 0)
    /// Signalled when the first flush begins to wait.
    let entered = AudioTestSignalLatch()

    init(watchdog seconds: Double = 20) {
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + seconds) { [self] in
            _ = self.release(by: .watchdog)
        }
    }

    /// The flush to give `AudioCaptureEngine(resamplerTailFlush:)`.
    var tailFlush: CaptureProcessor.TailFlush {
        { [self] converter, target, samples in
            let holds = state.withLock { state -> Bool in
                state.flushes += 1
                guard state.flushes == 1, state.releasedBy == nil else { return false }
                state.isHeld = true
                return true
            }
            if holds {
                entered.signal()
                // The watchdog lets it go when the test does not; this bound is only for a watchdog that never ran.
                _ = gate.wait(timeout: .now() + 120)
            }
            return CaptureProcessor.flushResamplerTail(converter, target, &samples)
        }
    }

    /// Whether the first flush is waiting now.
    var isHeld: Bool {
        state.withLock { $0.isHeld }
    }

    /// Who let the first flush go, if anyone has.
    var releasedBy: Release? {
        state.withLock { $0.releasedBy }
    }

    /// Lets the first flush go, whether it is waiting yet or not; false when it was let go already.
    @discardableResult
    func release(by releaser: Release = .test) -> Bool {
        let first = state.withLock { state -> Bool in
            guard state.releasedBy == nil else { return false }
            state.releasedBy = releaser
            state.isHeld = false
            return true
        }
        if first {
            gate.signal()
        }
        return first
    }
}

/// Every `DictationGate` made since the last release, so a harness's teardown can settle whatever its test left open.
@MainActor
enum HeldGates {
    private static var releases: [@MainActor () -> Void] = []

    static func register(_ release: @escaping @MainActor () -> Void) {
        releases.append(release)
    }

    /// Settles every gate made since the last call that the test left open (`DictationGate.settleForTeardown`).
    static func releaseAll() {
        let pending = releases
        releases = []
        for release in pending {
            release()
        }
    }
}

/// A point a fake waits at until the test opens it with a value or fails it. A waiter whose task is cancelled throws
/// `CancellationError`, as a real stalled operation that observes cancellation would.
@MainActor
final class DictationGate<Value: Sendable> {
    private var outcome: Result<Value, any Error>?
    private var waiters: [UInt64: CheckedContinuation<Value, any Error>] = [:]
    private var nextWaiter: UInt64 = 0
    private(set) var arrivals = 0

    init() {
        HeldGates.register { [weak self] in
            self?.settleForTeardown()
        }
    }

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

    /// Teardown: a gate the test left unsettled fails for good with `CancellationError`, so every waiter goes on, the
    /// ones parked now and any that arrive later. A gate the test settled keeps its outcome.
    func settleForTeardown() {
        guard outcome == nil else { return }
        settle(.failure(CancellationError()))
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

/// The microphone. An open succeeds at once unless `holdsOpens`, when it waits for `completeOpen`; a retire hands
/// back `samples` for a recording whose open succeeded and nil for one that never opened, as the engine does, sealed
/// at once unless `holdsSeals`, when the seal waits for `releaseSeal`.
@MainActor
final class FakeCapture: DictationCapturing {
    struct Start {
        let owner: RecordingID
        let policy: CaptureStopPolicy
        let events: @Sendable (CaptureEvent) -> Void
    }

    var holdsOpens = false
    var holdsSeals = false
    var openError: (any Error)?
    var samples = [Float](repeating: 0.25, count: 8_000)
    private(set) var starts: [Start] = []
    private(set) var stops: [RecordingID] = []
    private(set) var idleWaits = 0
    private var gates: [RecordingID: DictationGate<CaptureOpenOutcome>] = [:]
    private var sealGates: [RecordingID: DictationGate<Void>] = [:]
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

    func retire(owner: RecordingID) -> Task<CapturedAudio?, Never>? {
        stops.append(owner)
        guard opened.contains(owner), !handedOver.contains(owner) else { return nil }
        handedOver.insert(owner)
        let audio = CapturedAudio(owner: owner, samples: samples, summary: Self.summary(sampleCount: samples.count))
        guard holdsSeals else {
            return Task { () -> CapturedAudio? in audio }
        }
        let gate = DictationGate<Void>()
        sealGates[owner] = gate
        return Task { () -> CapturedAudio? in
            // Teardown fails the gate; the seal still hands its audio over, as sealing always finishes.
            _ = try? await gate.wait()
            return audio
        }
    }

    func waitUntilIdle() async {
        idleWaits += 1
    }

    var pendingOpens: Int {
        gates.values.reduce(0) { $0 + $1.waitingCount }
    }

    /// Seals still held.
    var pendingSeals: Int {
        sealGates.values.reduce(0) { $0 + $1.waitingCount }
    }

    /// Finishes a held open. `.live` makes the recording's audio available to its retire.
    func completeOpen(_ owner: RecordingID, _ outcome: CaptureOpenOutcome = .live) {
        if outcome == .live {
            opened.insert(owner)
        }
        gates[owner]?.open(outcome)
    }

    func failOpen(_ owner: RecordingID, _ error: any Error) {
        gates[owner]?.fail(error)
    }

    func releaseSeal(_ owner: RecordingID) {
        sealGates[owner]?.open(())
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
    /// The sample counts of every call, in order.
    private(set) var sampleCounts: [Int] = []

    func transcribe(samples: [Float], sampleRate: Double) async throws -> TranscriptionResult {
        calls += 1
        sampleCounts.append(samples.count)
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

    var kinds: [DictationNotice.Kind] {
        notices.map(\.kind)
    }
}

@MainActor
final class FakeTriggers: DictationTriggerSource {
    private(set) var settledToggles: [HotkeyBinding] = []

    var cancelledToggles: Int {
        settledToggles.count
    }

    func cancelToggle(_ binding: HotkeyBinding) {
        settledToggles.append(binding)
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
    private var isClosed = false

    var sleeperCount: Int {
        sleepers.count
    }

    /// When each sleeper wakes.
    var sleeperDeadlines: [ContinuousClock.Instant] {
        sleepers.map(\.deadline)
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
                if Task.isCancelled || isClosed {
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

    /// Teardown: the clock closes for good. Every sleeper throws `CancellationError`, and so does every later sleep.
    func closeForTeardown() {
        isClosed = true
        let pending = sleepers
        sleepers.removeAll()
        for sleeper in pending {
            sleeper.continuation.resume(throwing: CancellationError())
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

    /// `capture` replaces the fake microphone, for a test that drives the real engine through its adapter.
    init(
        configuration: DictationController.Configuration = DictationController.Configuration(),
        rulesLoaded: Bool = true,
        injector: (any DictationInjecting)? = nil,
        capture liveCapture: (any DictationCapturing)? = nil
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
                capture: liveCapture ?? capture,
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

    /// The notice on the pill now, from the controller's schedule.
    var shownNotice: DictationOutcomeNotice? {
        controller.noticeSchedule.shown?.notice
    }

    /// Teardown: settles every gate the test left open, for good, so a waiter that arrives later goes on too, closes
    /// the clock, and shuts the controller down under a watchdog, so no task of this test is still suspended, or runs,
    /// once the next test starts.
    func tearDown() async {
        HeldGates.releaseAll()
        if !gate.isOpen {
            gate.open(.withoutStoredRules)
        }
        clock.closeForTeardown()
        _ = await bounded("the harness's controller to shut down in teardown") {
            await self.controller.shutDown()
        }
    }
}

extension XCTestCase {
    /// A harness whose controller the test's teardown shuts down, after releasing whatever the test left held.
    @MainActor
    func makeHarness(
        configuration: DictationController.Configuration = DictationController.Configuration(),
        rulesLoaded: Bool = true,
        injector: (any DictationInjecting)? = nil,
        capture: (any DictationCapturing)? = nil
    ) -> DictationHarness {
        let harness = DictationHarness(
            configuration: configuration, rulesLoaded: rulesLoaded, injector: injector, capture: capture)
        addTeardownBlock { @MainActor in
            await harness.tearDown()
        }
        return harness
    }
}

/// Keyboard events as the event tap reports them, for a real `HotkeyManager`.
enum HotkeyTestEvents {
    /// Right Option's own device bit, as `CGEventFlags` carries it.
    static let rightOptionDown = CGEventFlags(rawValue: CGEventFlags.maskAlternate.rawValue | 0x0040)

    static func event(
        _ type: CGEventType, keyCode: CGKeyCode, flags: CGEventFlags = [], repeat isAutorepeat: Bool = false,
        synthetic: Bool = false
    ) -> HotkeyObservedEvent {
        HotkeyObservedEvent(
            typeRawValue: type.rawValue, keyCode: keyCode, flagsRawValue: flags.rawValue,
            isAutorepeat: isAutorepeat, isSynthetic: synthetic)
    }

    /// Caps Lock's flags event after a tap, with its lock on or off.
    static func capsLock(on: Bool) -> HotkeyObservedEvent {
        event(.flagsChanged, keyCode: 57, flags: on ? [.maskAlphaShift] : [])
    }

    static func rightOption(down: Bool) -> HotkeyObservedEvent {
        event(.flagsChanged, keyCode: 61, flags: down ? rightOptionDown : [])
    }
}

extension DictationHarness {
    /// A real `HotkeyManager`, without an event tap, wired to the controller as the app wires it: its presses and
    /// releases reach the controller, and the controller settles its toggle. `flags` is what it reads as the
    /// keyboard's state now.
    func wireHotkeyManager(keyCode: CGKeyCode, flags: @escaping () -> CGEventFlags = { [] }) -> HotkeyManager {
        let manager = HotkeyManager(keyCode: keyCode, readModifierFlags: flags, readKeyDown: { _ in false })
        manager.onPressed = { [controller] binding in
            controller.hotkeyPressed(binding)
        }
        manager.onReleased = { [controller] binding, cause in
            controller.hotkeyReleased(binding, cause: cause)
        }
        controller.triggers = manager
        return manager
    }
}

/// A failure a fake throws. Its name and payload carry nothing a log could leak.
struct DictationTestFailure: Error, Equatable {
    let code: Int
}
