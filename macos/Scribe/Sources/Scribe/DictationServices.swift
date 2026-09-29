import AppKit
import Foundation

// The services the dictation lifecycle drives, one protocol each, so `DictationController` can be driven entirely by
// fakes in tests. Every protocol is main-actor isolated: the controller lives there, and each live adapter below hands
// its blocking or slow work to something that runs off the main actor (the capture engine's control queue, the
// recognizer's child process, the provider's network request), so the main actor only ever awaits.

/// The microphone, one recording at a time (`AudioCaptureEngine`).
@MainActor
protocol DictationCapturing {
    /// Starts opening the microphone for `owner` and returns at once with the task that ends when the open has. The
    /// open runs off the main actor from the moment this returns, so the caller can capture the injection target
    /// while the device opens. A stop for `owner` makes a pending open end as `.stoppedBeforeOpen` or
    /// `.stoppedWhileOpening`.
    func startOpening(
        owner: RecordingID,
        policy: CaptureStopPolicy,
        events: @escaping @Sendable (CaptureEvent) -> Void
    ) -> Task<CaptureOpenOutcome, Error>

    /// Ends `owner`'s recording without waiting for anything and returns the task that yields its samples once they
    /// are sealed, off the main actor, or nil when `owner` holds nothing. A release can arrive in the event tap's
    /// callback, which must return at once, so neither a buffer being converted nor the resampler's tail is waited
    /// for here.
    func retire(owner: RecordingID) -> Task<CapturedAudio?, Never>?

    /// Returns once the device work queued so far (opens and closes) has run.
    func waitUntilIdle() async
}

/// Speech recognition (`TranscriptionEngine`). Cancelling the calling task stops the recognizer.
@MainActor
protocol DictationTranscribing {
    func transcribe(samples: [Float], sampleRate: Double) async throws -> TranscriptionResult
}

/// Optional AI cleanup (`CleanupProviderCache`).
@MainActor
protocol DictationCleaning {
    /// The user's switch, read when a dictation reaches cleanup, so turning cleanup off stops every dictation that
    /// has not reached it yet from being sent.
    var isEnabled: Bool { get }

    /// The provider for the configuration stored now. Building one can read the Keychain, so it never runs on the
    /// main actor.
    func provider() async throws -> any CleanupProvider

    /// Drops the cached provider and credential (`CleanupProviderCache.invalidate()`).
    func invalidate()
}

/// Where a dictation is meant to go, captured when its recording starts.
struct DictationTarget: Sendable {
    /// What delivery confirms focus against; nil when nothing identified the focused application, in which case
    /// the dictation is never delivered (it is kept for recovery instead).
    let injection: InjectionTarget?
    /// The target's bundle identifier, for its app profile and its line-break handling.
    let bundleIdentifier: String?
    /// The target's name, the profile matcher's fallback when a profile lists process names.
    let processName: String?
}

/// Captures the focused application and element (`TextInjector.captureTarget()`).
@MainActor
protocol DictationTargeting {
    func captureTarget() -> DictationTarget
}

/// Delivery into the focused application (`TextInjector`).
@MainActor
protocol DictationInjecting: AnyObject {
    func inject(text: String, into target: InjectionTarget?, shiftReturnLineBreaks: Bool) async -> InjectionResult

    /// The shutdown barrier: returns once every delivery requested before it has finished, however long that takes.
    func waitUntilIdle() async
}

extension TextInjector: DictationInjecting {}

/// The ordered background history writer (`HistoryWriter`).
protocol DictationHistoryWriting: Sendable {
    /// Queues one entry and returns at once; false when the writer refused it (closed, or full).
    func enqueue(_ record: DictationHistoryRecord, dictationID: UInt64) -> Bool

    /// Closes the writer and waits up to `timeout` for what it accepted. Blocks the calling thread, so the lifecycle
    /// calls it off the main actor.
    func complete(timeout: TimeInterval) -> HistoryDrainResult
}

extension HistoryWriter: DictationHistoryWriting {}

/// The user's dictionary rules, snippets and app profiles. `Sendable` so a dictation can wait for startup's first
/// load in a task of its own (`awaitUnlessCancelled`); every conformer is main-actor isolated.
@MainActor
protocol DictationRuleSource: AnyObject, Sendable {
    /// Whether startup's first rule load has finished (`StartupGate`).
    var isLoaded: Bool { get }

    /// Returns once startup's first rule load has finished, at once after that.
    func waitUntilLoaded() async -> StartupGate.State

    var appProfiles: [AppProfile] { get }

    /// With AI cleanup off, and when cleanup fell back: snippets, then the dictionary and the enabled libraries.
    func postProcess(_ text: String) -> TextPostProcessingResult

    /// With AI cleanup on, before the request: every replacement decided on the raw transcript, and the vocabulary
    /// rules' made. The pass's text is what the provider is sent (`TextPostProcessor.correctVocabulary`).
    func correctVocabulary(_ text: String) -> VocabularyPass

    /// With AI cleanup on, after an accepted reply: the snippets and the template-like replacements `pass` held back,
    /// made where the reply kept their words, and no rule matched again (`TextPostProcessor.finishAfterCleanup`).
    func finishAfterCleanup(_ reply: String, after pass: VocabularyPass) -> TextPostProcessingResult
}

/// What the tray and the pill show.
@MainActor
protocol DictationPresenting: AnyObject {
    func present(_ presentation: DictationPresentation)
}

/// Non-modal notices about a dictation, delivered as local notifications in the app.
@MainActor
protocol DictationNotifying: AnyObject {
    func notify(_ notice: DictationNotice)
}

/// The push-to-talk key (`HotkeyManager`).
@MainActor
protocol DictationTriggerSource: AnyObject {
    /// A toggle's recording ended by some other way than the key: the key's next change is not taken as the toggle's
    /// second tap (Caps Lock starts a recording only when its lock turns on). Windows' `CancelToggle`. Ignored unless
    /// `binding` is the toggle bound now, so a recording started by a key since rebound settles nothing.
    func cancelToggle(_ binding: HotkeyBinding)
}

/// Time for the duration ceiling and the pill's notices. Main-actor isolated like the controller that reads it, so a
/// test's clock moves and wakes sleepers in exactly the order the test says.
@MainActor
protocol DictationClock: Sendable {
    var now: ContinuousClock.Instant { get }

    /// Returns at `deadline`, or throws `CancellationError` when the calling task is cancelled first.
    func sleep(until deadline: ContinuousClock.Instant) async throws
}

// MARK: - Live adapters

struct LiveDictationCapture: DictationCapturing {
    let engine: AudioCaptureEngine

    func startOpening(
        owner: RecordingID,
        policy: CaptureStopPolicy,
        events: @escaping @Sendable (CaptureEvent) -> Void
    ) -> Task<CaptureOpenOutcome, Error> {
        let engine = engine
        // Detached, so the engine admits the recording and queues the open on its control queue straight away,
        // while the main actor goes on to capture the target.
        return Task.detached(priority: .userInitiated) {
            try await engine.start(owner: owner, policy: policy, events: events)
        }
    }

    func retire(owner: RecordingID) -> Task<CapturedAudio?, Never>? {
        // The engine queues the seal on its control queue before this returns, so it runs ahead of the next
        // recording's open; the task only waits for it.
        guard let seal = engine.retire(owner: owner) else { return nil }
        return Task.detached(priority: .userInitiated) {
            await seal.audio
        }
    }

    func waitUntilIdle() async {
        await engine.waitUntilIdle()
    }
}

struct LiveDictationTranscriber: DictationTranscribing {
    let engine: TranscriptionEngine

    func transcribe(samples: [Float], sampleRate: Double) async throws -> TranscriptionResult {
        try await engine.transcribe(samples: samples, sampleRate: sampleRate)
    }
}

struct LiveDictationCleanup: DictationCleaning {
    let cache: CleanupProviderCache

    var isEnabled: Bool {
        cache.store.isEnabled
    }

    func provider() async throws -> any CleanupProvider {
        let cache = cache
        // Detached, because a build reads the Keychain, and a Keychain read waits for the user whenever macOS asks
        // them to allow it; that wait must not hold the main actor.
        return try await Task.detached(priority: .userInitiated) {
            try cache.provider()
        }.value
    }

    func invalidate() {
        cache.invalidate()
    }
}

/// When a change of the cleanup settings should drop the cached provider and credential at once rather than on the
/// next dictation: when cleanup was switched off, so no token or secret stays in memory for a feature the user
/// turned off, and when any provider setting changed. Switching it on needs nothing: the next dictation builds.
enum CleanupInvalidation {
    static func shouldInvalidate(from old: CleanupSettingsSnapshot, to new: CleanupSettingsSnapshot) -> Bool {
        if old.isEnabled, !new.isEnabled {
            return true
        }
        var sameSwitch = new
        sameSwitch.isEnabled = old.isEnabled
        return sameSwitch != old
    }
}

struct LiveDictationTargeting: DictationTargeting {
    let injector: TextInjector

    func captureTarget() -> DictationTarget {
        let target = injector.captureTarget()
        let application = target?.processIdentifier.flatMap { NSRunningApplication(processIdentifier: $0) }
        return DictationTarget(
            injection: target,
            bundleIdentifier: target?.bundleIdentifier ?? application?.bundleIdentifier,
            processName: application?.localizedName)
    }
}

/// One load of the rules every dictation applies, compiled. Compiling every library's rules takes tens of
/// milliseconds, so the app compiles off the main actor (`compile`) and installs the result there in one step
/// (`DictationRules.install`); a dictation meets either the rules before or the rules after, never a mix.
struct DictationRuleSnapshot: Sendable {
    let rules: TextPostProcessor.CompiledRules
    let appProfiles: [AppProfile]
    /// For the log: how many rules of each kind went in, and how long compiling took.
    let dictionaryEntryCount: Int
    let libraryEntryCount: Int
    let snippetCount: Int
    let compileDuration: Duration

    /// Compiles `ruleSet` on the calling thread.
    init(_ ruleSet: PersistenceRuleSet, libraryEntries: [DictionaryEntry]) {
        let clock = ContinuousClock()
        let started = clock.now
        rules = TextPostProcessor.CompiledRules(
            dictionaryEntries: ruleSet.dictionaryEntries, snippets: ruleSet.snippets, libraryEntries: libraryEntries)
        appProfiles = ruleSet.appProfiles
        dictionaryEntryCount = ruleSet.dictionaryEntries.count
        libraryEntryCount = libraryEntries.count
        snippetCount = ruleSet.snippets.count
        compileDuration = started.duration(to: clock.now)
    }

    /// Compiles `ruleSet` off the main actor.
    static func compile(
        _ ruleSet: PersistenceRuleSet, libraryEntries: [DictionaryEntry]
    ) async -> DictationRuleSnapshot {
        await Task.detached(priority: .userInitiated) {
            DictationRuleSnapshot(ruleSet, libraryEntries: libraryEntries)
        }.value
    }
}

/// The rules every dictation applies, loaded at launch and after every change in Settings or Quick Add
/// (`RuleSetRefresher`), and whether startup's first load has finished (`StartupGate`).
@MainActor
final class DictationRules: DictationRuleSource {
    let gate: StartupGate
    private let processor = TextPostProcessor()
    private(set) var appProfiles: [AppProfile] = []

    init(gate: StartupGate) {
        self.gate = gate
    }

    var isLoaded: Bool {
        gate.isOpen
    }

    func waitUntilLoaded() async -> StartupGate.State {
        await gate.wait()
    }

    /// Installs rules compiled off the main actor, in one step.
    func install(_ snapshot: DictationRuleSnapshot) {
        processor.install(snapshot.rules)
        appProfiles = snapshot.appProfiles
    }

    /// Compiles `rules` here and installs them. The app compiles off the main actor instead (`install`).
    func apply(_ rules: PersistenceRuleSet, libraryEntries: [DictionaryEntry]) {
        install(DictationRuleSnapshot(rules, libraryEntries: libraryEntries))
    }

    func postProcess(_ text: String) -> TextPostProcessingResult {
        processor.processDetailed(text)
    }

    func correctVocabulary(_ text: String) -> VocabularyPass {
        processor.correctVocabulary(text)
    }

    func finishAfterCleanup(_ reply: String, after pass: VocabularyPass) -> TextPostProcessingResult {
        processor.finishAfterCleanup(reply, after: pass)
    }
}

struct SystemDictationClock: DictationClock {
    var now: ContinuousClock.Instant {
        ContinuousClock.now
    }

    func sleep(until deadline: ContinuousClock.Instant) async throws {
        try await Task.sleep(until: deadline, clock: .continuous)
    }
}
