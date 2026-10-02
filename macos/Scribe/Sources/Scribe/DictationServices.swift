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
    var isStartingLocalModel: Bool { get }

    /// The provider for the configuration stored now. Building one can read the Keychain, so it never runs on the
    /// main actor.
    func provider() async throws -> any CleanupProvider

    /// Drops the cached provider and credential (`CleanupProviderCache.invalidate()`).
    func invalidate()

    /// Starts any background local-model readying work for a recording that just began.
    func recordingStarted(writingStylePrompt: String)

    /// Waits for local-model readying when the next cleanup request needs it. False means the request should fall back
    /// rather than wait any longer.
    func waitForLocalModelIfNeeded() async -> Bool

    /// One local-model request answered.
    func noteAnswerReceived(from provider: any CleanupProvider)

    /// Turns a pause into a best-effort local-model release once no dictation is using it.
    func releaseForPause()

    /// Reacts to a cleanup settings change: invalidate the provider cache and release a previous local-app model when
    /// cleanup stopped using it.
    func settingsChanged(from previous: CleanupSettingsSnapshot, to current: CleanupSettingsSnapshot)
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

@MainActor
final class LiveDictationCleanup: DictationCleaning {
    private struct LocalReleaseTarget: Equatable, Sendable {
        let endpoint: String
        let model: String
        let apiKey: String?
    }

    let cache: CleanupProviderCache
    private let localServers: any LocalServerControlling
    private let localModelLane: AsyncLane
    private let clock: @Sendable () -> ContinuousClock.Instant
    private var prewarmTask: Task<Bool, Never>?
    private var prewarmGeneration: UInt64 = 0
    private var prewarmOutcome: Bool?
    private var isStartingLocalModelStorage = false
    private var lastLocalAnswerAt: ContinuousClock.Instant?

    init(
        cache: CleanupProviderCache,
        localServers: any LocalServerControlling = LocalServerClient(),
        localModelLane: AsyncLane = LocalModelDefaults.sharedLane,
        clock: @escaping @Sendable () -> ContinuousClock.Instant = { ContinuousClock.now }
    ) {
        self.cache = cache
        self.localServers = localServers
        self.localModelLane = localModelLane
        self.clock = clock
    }

    var isEnabled: Bool {
        cache.store.isEnabled
    }

    var isStartingLocalModel: Bool {
        isStartingLocalModelStorage
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

    func recordingStarted(writingStylePrompt: String) {
        if prewarmOutcome != nil {
            prewarmTask = nil
            prewarmOutcome = nil
        }
        guard prewarmTask == nil else { return }
        let snapshot = cache.store.snapshot()
        guard snapshot.isEnabled else { return }
        let target = localReleaseTarget(from: snapshot)
        let localServer = target != nil

        let remoteLocal = snapshot.providerKind == .foundryLocal
            || (snapshot.providerKind == .openAICompatible && LocalAiServer.isOnThisMac(snapshot.openAIBaseURL))
            || snapshot.providerKind == .ollama
        guard localServer || remoteLocal else { return }
        let lastAnswerAt = lastLocalAnswerAt
        prewarmGeneration &+= 1
        let generation = prewarmGeneration

        prewarmTask = Task { [cache, localServers, localModelLane, clock] in
            if let last = lastAnswerAt,
                last.duration(to: clock()) < LocalModelDefaults.prewarmAfterIdle,
                let target
            {
                let state =
                    (try? await localModelLane.run {
                        await localServers.read(target.endpoint, apiKey: target.apiKey)
                    }) ?? .failed
                if state.loaded(for: target.model) != nil {
                    return true
                }
            } else if let last = lastAnswerAt,
                last.duration(to: clock()) < LocalModelDefaults.prewarmAfterIdle
            {
                return true
            }

            guard let provider = try? cache.provider() else {
                return false
            }
            let request = CleanupRequest(
                transcript: CleanupPrompt.wrapTranscript(""),
                writingStylePrompt: CleanupPrompt.systemPrompt(
                    writingStyle: writingStylePrompt,
                    useLocalPrompt: provider.usesLocalCleanupPrompt),
                timeout: ChatCompletionsTransport.seconds(LocalModelDefaults.startWait),
                maxOutputTokens: 1)

            do {
                _ = try await provider.clean(request)
                return true
            } catch let error as CleanupProviderError {
                switch error {
                case .invalidResponse(.emptyCompletion), .invalidResponse(.outputLimitReachedBeforeText):
                    return true
                default:
                    return false
                }
            } catch {
                return false
            }
        }
        isStartingLocalModelStorage = true
        Task { [weak self] in
            guard let self, let task = self.prewarmTask else { return }
            let success = await task.value
            guard self.prewarmGeneration == generation else { return }
            self.prewarmOutcome = success
            self.isStartingLocalModelStorage = false
        }
    }

    func waitForLocalModelIfNeeded() async -> Bool {
        if let outcome = prewarmOutcome {
            prewarmTask = nil
            prewarmOutcome = nil
            if outcome {
                lastLocalAnswerAt = clock()
            }
            return outcome
        }
        guard let task = prewarmTask else { return true }
        do {
            let success = try await OperationDeadline.run(within: LocalModelDefaults.startWait) {
                await task.value
            }
            prewarmTask = nil
            prewarmOutcome = nil
            if success {
                lastLocalAnswerAt = clock()
            }
            return success
        } catch {
            task.cancel()
            prewarmTask = nil
            prewarmOutcome = nil
            isStartingLocalModelStorage = false
            return false
        }
    }

    func noteAnswerReceived(from provider: any CleanupProvider) {
        guard provider.usesLocalCleanupPrompt else { return }
        lastLocalAnswerAt = clock()
    }

    func releaseForPause() {
        guard let target = localReleaseTarget(from: cache.store.snapshot()) else { return }
        scheduleRelease(of: target)
    }

    func settingsChanged(from previous: CleanupSettingsSnapshot, to current: CleanupSettingsSnapshot) {
        prewarmTask?.cancel()
        prewarmTask = nil
        prewarmOutcome = nil
        prewarmGeneration &+= 1
        isStartingLocalModelStorage = false

        guard let previousTarget = localReleaseTarget(from: previous) else {
            return
        }
        let currentTarget = localReleaseTarget(from: current)
        if !current.isEnabled || currentTarget != previousTarget {
            scheduleRelease(of: previousTarget)
        }
    }

    private func scheduleRelease(of target: LocalReleaseTarget) {
        let lane = localModelLane
        let localServers = localServers
        Task {
            do {
                try await lane.run {
                    _ = await localServers.unload(target.endpoint, modelID: target.model, apiKey: target.apiKey)
                }
            } catch {
                return
            }
        }
    }

    private func localReleaseTarget(from snapshot: CleanupSettingsSnapshot) -> LocalReleaseTarget? {
        guard snapshot.isEnabled else { return nil }
        switch snapshot.providerKind {
        case .foundryLocal, .microsoftFoundry:
            return nil
        case .ollama:
            guard let model = Self.trimmed(snapshot.ollamaModel) else { return nil }
            return LocalReleaseTarget(endpoint: LocalAiServer.ollamaAddress, model: model, apiKey: nil)
        case .openAICompatible:
            guard LocalAiServer.appAt(snapshot.openAIBaseURL) != .none,
                let endpoint = Self.trimmed(snapshot.openAIBaseURL),
                let model = Self.trimmed(snapshot.openAIModel)
            else {
                return nil
            }
            return LocalReleaseTarget(endpoint: endpoint, model: model, apiKey: cache.store.openAIApiKey())
        }
    }

    private static func trimmed(_ value: String) -> String? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
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
