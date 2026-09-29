import Foundation

/// What started a recording.
enum DictationTrigger: Equatable, Sendable {
    /// The push-to-talk key, as bound when it was pressed.
    case hotkey(HotkeyBinding)
    /// The tray's Start Test Dictation item, which works as a toggle.
    case menu

    var gesture: HotkeyGesture {
        switch self {
        case .hotkey(let binding): return binding.gesture
        case .menu: return .toggle
        }
    }

    /// Whether this trigger's recording stops on silence. The tray's test dictation always does, so one left running
    /// ends by itself. A push-to-talk key tapped on and off does only when the user opted in (`toggleKeyOptedIn`, off
    /// by default as on Windows' `AppSettings.AutoStopOnSilence`), and a held key never does.
    func stopsOnSilence(toggleKeyOptedIn: Bool) -> Bool {
        switch self {
        case .hotkey(let binding): return binding.gesture == .toggle && toggleKeyOptedIn
        case .menu: return true
        }
    }
}

/// Why a recording ended. Logged on every stop, as Windows' `DictationStopReason` is: the causes look alike from
/// outside ("it stopped after ten seconds") and call for different fixes.
enum DictationStopReason: String, Equatable, Sendable {
    /// The hold key was released, or the toggle key tapped again.
    case hotkeyReleased
    /// Stop Test Dictation in the tray.
    case menu
    /// Toggle silence auto-stop: the speaker went quiet, or never spoke within the lead-in.
    case silence
    /// The recording reached its duration ceiling.
    case durationLimit
    /// The input device failed or changed under the recording.
    case deviceFault
    /// Dictation was paused while the recording was live.
    case paused
    /// The push-to-talk key was rebound while it held a recording.
    case bindingChanged
    /// The event tap was disabled and, re-read afterwards, the key had been released meanwhile.
    case tapResynchronized
    /// Scribe is quitting; the recording is discarded.
    case shutdown
}

/// How AI cleanup went for one dictation.
enum DictationCleanupOutcome: String, Equatable, Sendable {
    /// Switched off, so it did not run.
    case off
    /// The model changed the text.
    case cleaned
    /// The model returned the text unchanged.
    case unchanged
    /// Cleanup could not start, failed, or its reply was rejected: the raw transcript was used.
    case fellBack
}

/// What `DictationController.shutDown` did.
struct DictationShutdownReport: Equatable, Sendable {
    /// A recording was live when shutdown began; it was discarded.
    let discardedRecording: Bool
    /// Dictations still being processed when shutdown began; each was cancelled and awaited.
    let cancelledDictations: Int
    let history: HistoryDrainResult
}

extension CleanupPrompt {
    /// Windows' `SingleLineWritingStyle`: added when the target flattens line breaks (a terminal), so the model is
    /// asked for one line instead of having its paragraphs run together afterwards.
    static let singleLineWritingStyle =
        "Return exactly one physical line with no carriage returns or line feeds. Keep exactly one space between "
        + "sentences, and use punctuation rather than line breaks to structure the text."

    /// The writing style for one dictation: the app profile's when it has one, otherwise the default, with the
    /// single-line contract added when the target needs it. Windows' `ResolveWritingStyleOverride`.
    static func writingStyle(profileStyle: String?, requireSingleLine: Bool) -> String {
        let trimmed = profileStyle?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let style = trimmed.isEmpty ? defaultWritingStyle : trimmed
        return requireSingleLine ? style + " " + singleLineWritingStyle : style
    }
}

/// A recording from its admission until its stop.
private struct LiveRecording {
    enum Phase {
        /// Admitted; its microphone has not been asked for yet.
        case admitted
        /// Its microphone is opening.
        case opening
        /// Its microphone is open and delivering.
        case live
    }

    let id: RecordingID
    let trigger: DictationTrigger
    let policy: CaptureStopPolicy
    let admittedAt: ContinuousClock.Instant
    /// Held from admission until the dictation's processing ends, so storage housekeeping stays out of the way.
    let lease: ForegroundActivity.Lease
    var phase = Phase.admitted
    /// Captured when the microphone starts opening, so a later change of focus changes neither where the text goes
    /// nor which app profile applies.
    var target: DictationTarget?
    /// The app profiles as they stood at activation, when the rules had loaded by then.
    var profiles: [AppProfile]?
    var deadline: Task<Void, Never>?
}

/// A stopped recording admitted to processing, with everything captured when it started. Its samples are sealed off
/// the main actor after the stop (`sealing`); it holds its place in both turn queues meanwhile.
private struct AdmittedDictation: Sendable {
    let id: RecordingID
    let trigger: DictationTrigger
    let target: DictationTarget?
    let profiles: [AppProfile]?
    let sealing: Task<CapturedAudio?, Never>
    let stopReason: DictationStopReason
    let lease: ForegroundActivity.Lease
}

/// Where a cancelled dictation stopped, for the log.
private enum StopPoint {
    case beforeTranscription
    case duringTranscription
    case afterTranscription
    case waitingForRules
    case duringCleanup
    case beforeDelivery
    case duringDelivery
}

/// Why an activation started nothing.
private enum ActivationRefusal {
    case closing
    case paused
    case alreadyRecording
    case stillProcessing
}

private struct CleanupStage {
    var outcome: DictationCleanupOutcome
    var text: String?
    /// How long the provider took, when a request was sent.
    var requestDuration: Duration?
}

extension Duration {
    fileprivate var seconds: Double {
        let (whole, fraction) = components
        return Double(whole) + Double(fraction) / 1e18
    }
}

/// The one owner of the dictation lifecycle: recordings from the key or the tray, their processing in Windows' order,
/// what the tray and the pill show, and shutdown.
///
/// **Recordings.** At most one recording is live, and it is the one `LiveRecording` held here: its id, the binding
/// or menu item that started it, its phase and what was captured when it started (the target application and
/// element, and the app profiles). Every start, stop, meter reading, fault and deadline names that id, so a late
/// event, a second stop or a stale deadline can never act on the recording that came after. Admission is decided when
/// the press arrives and checked again when the microphone is asked for, so a pause that lands in between wins. The
/// tray's test dictation stops on silence; a key tapped on and off (Caps Lock) does only when the user opted in
/// (`Configuration.toggleKeyStopsOnSilence`), and a held key never does. All stop at the duration ceiling, which is a
/// deadline of this controller's own (Windows' `ArmDurationLimit` and `TryAcceptDurationLimit`), since the capture
/// engine's own ceiling and silence clock only move while buffers arrive. A toggle whose recording ends any other way
/// than by its key (a failed open, silence, a fault) is settled, so the key's next change is not taken for its second
/// tap. Scribe never changes the lock state, so after such an ending Caps Lock's light is on with nothing recording;
/// since a recording starts only when the lock turns on, the next tap turns the light off and starts nothing, and the
/// tap after it starts the next recording.
///
/// **Stopping.** A stop can arrive in the event tap's callback, so it only retires the recording: the recording lets
/// go of the microphone, takes its place in both turn queues, and its samples are sealed on the capture engine's
/// control queue, off the main actor, while its processing waits for them.
///
/// **Processing.** A stopped recording is admitted to processing in the order recordings stopped, and a new recording
/// can start while earlier ones are processed. Each dictation runs raw speech recognition (one recognizer at a time)
/// and waits for startup's first rule load. With AI cleanup off it applies snippets, then the dictionary. With cleanup
/// on it decides every replacement on the raw transcript as cleanup off would, makes the vocabulary rules' in the text
/// it sends with the target's writing style (and a single-line instruction when the target flattens line breaks),
/// checks the reply against the text it sent and normalizes its dashes (`CleanupResponseGuard`), then makes the
/// snippets and the template-like replacements where the reply kept their words; a fallback is exactly the cleanup-off
/// result. It then formats line breaks for the captured target and delivers into that target only. Deliveries run in
/// dictation order, so a second dictation's text never goes in before the first's.
/// Cleanup never sees a snippet template or a template-like replacement, and no rule runs twice. Cancellation and
/// admission are checked again before cleanup, recovery and delivery, because the recognizer returns a transcript it
/// has already produced even when the cancellation arrives just after it exited.
///
/// **Presentation.** Every change to what the tray and the pill show takes the next revision (`DictationPresentation`).
/// Outcomes go through one schedule (`DictationNoticeSchedule`): a recording owns the pill, one notice shows at a time,
/// an outcome that cannot show yet waits rather than being lost or replacing a newer failure, and a notice's timed end
/// is tagged with the revision that showed it, so a stale end changes nothing.
///
/// **Shutdown** (`shutDown`): nothing new is admitted, the live recording is discarded, the pill and the tray go idle
/// under one last revision, every dictation in processing is cancelled, a delivery in progress is awaited to
/// completion (`TextInjector.waitUntilIdle`, which puts a borrowed pasteboard back), every cancelled dictation is
/// awaited (its recognizer's process group stopped and reaped by `ProcessRunner`), and the history writer is drained
/// within its bound, off the main actor.
///
/// Logs are shapes only: ids, counts, durations, enum names and failure shapes, never text, profile names or targets.
@MainActor
final class DictationController {
    struct Configuration: Sendable {
        /// Windows' `MaxDictationMinutes`; nil or zero means no ceiling.
        var maximumDuration: Duration? = CaptureStopPolicy.defaultMaximumDuration
        /// Whether a push-to-talk key tapped on and off (Caps Lock) stops its recording on silence, read at each
        /// press (`HotkeySettingsStore.autoStopOnSilence`). Off unless the user opts in: a pause to think would end
        /// the dictation, and a recording that ends itself leaves Caps Lock's light on until the next tap, which only
        /// turns it off, since the listen-only event tap cannot change it. The tray's test dictation always stops on
        /// silence and a held key never does (`DictationTrigger.stopsOnSilence`).
        var toggleKeyStopsOnSilence: @MainActor @Sendable () -> Bool = { false }
        /// Line-break handling when no app profile overrides it.
        var newlineMode: NewlineInjectionMode = .smartFlatten
        /// How long a notice stays on the pill.
        var noticeDuration: Duration = .milliseconds(1_800)
        /// A press while this many dictations are still processing is turned away, which bounds the audio held.
        var maximumDictationsInProcessing = 3
        /// How long shutdown waits for history already accepted to commit.
        var historyDrainTimeout: TimeInterval = 3
    }

    struct Services {
        var capture: any DictationCapturing
        var transcriber: any DictationTranscribing
        var cleanup: any DictationCleaning
        var targeting: any DictationTargeting
        var injector: any DictationInjecting
        var history: any DictationHistoryWriting
        var rules: any DictationRuleSource
        var presenter: any DictationPresenting
        var notifier: any DictationNotifying
        var clock: any DictationClock
        var activity: ForegroundActivity
        var recovery: LastTranscriptStore
        var reports: PipelineReportStore
    }

    /// Counts of steps the controller took that change nothing visible, so a test can wait for the step it checks
    /// instead of guessing how many turns of the main actor that takes. Counts only.
    struct Checkpoints: Equatable, Sendable {
        /// Scheduled microphone requests that ran their admission check, whether they opened anything or not.
        var admissionChecks = 0
        /// Opens that came back, live, stopped or failed, whether their recording was still current or not.
        var openAnswers = 0
        /// Releases that ended nothing: another key, or nothing recording.
        var ignoredReleases = 0
        /// Capture events of a recording that no longer holds the microphone, or that arrived at shutdown.
        var ignoredCaptureEvents = 0
        /// Duration deadlines that ended nothing.
        var ignoredDeadlines = 0
        /// Timed notice ends that found their notice already gone.
        var ignoredNoticeEnds = 0
        /// Outcomes the notice schedule rejected.
        var rejectedNotices = 0
    }

    /// How far shutdown has got.
    enum ShutdownProgress: Equatable, Sendable {
        case notStarted
        case awaitingDelivery
        case awaitingDictations
        case awaitingDevice
        case drainingHistory
        case finished
    }

    private let services: Services
    private let configuration: Configuration
    /// The push-to-talk key, told when a toggle's recording ended some other way than by the key.
    weak var triggers: (any DictationTriggerSource)?

    private(set) var isPaused: Bool
    private(set) var isClosing = false
    private(set) var checkpoints = Checkpoints()
    private(set) var shutdownProgress = ShutdownProgress.notStarted
    private var recording: LiveRecording?
    private var dictations: [RecordingID: Task<Void, Never>] = [:]
    private let transcriptionTurns = DictationTurns()
    private let deliveryTurns = DictationTurns()
    private var revision: UInt64 = 0
    private var levelDbfs = AudioLevelMeasurement.toDbfs(0)
    private var notices = DictationNoticeSchedule()
    private var nextNoticeID: UInt64 = 0
    private var noticeTimer: Task<Void, Never>?
    private var shutdown: Task<DictationShutdownReport, Never>?
    private lazy var captureEvents = CaptureEventRelay { [weak self] event in
        self?.handleCaptureEvent(event)
    }

    init(services: Services, configuration: Configuration = Configuration(), isPaused: Bool = false) {
        self.services = services
        self.configuration = configuration
        self.isPaused = isPaused
    }

    /// The recording admitted, opening or live, if any.
    var currentRecording: RecordingID? {
        recording?.id
    }

    /// Whether the current recording's microphone is open.
    var isRecordingLive: Bool {
        recording?.phase == .live
    }

    /// Dictations stopped and not yet finished processing.
    var processingCount: Int {
        dictations.count
    }

    /// Dictations waiting for an earlier dictation's recognizer to finish.
    var dictationsWaitingToTranscribe: Int {
        transcriptionTurns.waitingCount
    }

    /// Dictations whose text is ready and waiting for an earlier dictation's delivery to finish.
    var dictationsWaitingToDeliver: Int {
        deliveryTurns.waitingCount
    }

    /// The notice on the pill and the outcomes waiting for it.
    var noticeSchedule: DictationNoticeSchedule {
        notices
    }

    // MARK: - Inputs

    /// The push-to-talk key went down (Caps Lock: turned on). Returns whether a recording started, so the key's
    /// listener knows whether it is now held.
    @discardableResult
    func hotkeyPressed(_ binding: HotkeyBinding) -> Bool {
        beginRecording(.hotkey(binding))
    }

    /// The push-to-talk key came up (Caps Lock: turned off), or its listener settled it. Ends the recording only when
    /// that key started it. Runs in the event tap's callback, so it only retires the recording.
    func hotkeyReleased(_ binding: HotkeyBinding, cause: HotkeyReleaseCause) {
        guard let current = recording, case .hotkey(let started) = current.trigger, started.keyCode == binding.keyCode
        else {
            checkpoints.ignoredReleases += 1
            return
        }
        let reason: DictationStopReason
        switch cause {
        case .keyReleased: reason = .hotkeyReleased
        case .bindingChanged: reason = .bindingChanged
        case .tapResynchronized: reason = .tapResynchronized
        }
        endRecording(current.id, reason: reason)
    }

    /// The tray's Start or Stop Test Dictation.
    func toggleMenuDictation() {
        if let current = recording {
            endRecording(current.id, reason: .menu)
        } else {
            beginRecording(.menu)
        }
    }

    /// Mirrors Windows' `SetPaused`: pausing ends a live recording (what it captured is still processed) and turns
    /// away every press until resumed. The key's listener stays installed, and a paused press reaches other apps.
    func setPaused(_ paused: Bool) {
        guard paused != isPaused else { return }
        isPaused = paused
        if paused {
            ScribeLog.info(.dictation, "Dictation paused")
        } else {
            ScribeLog.info(.dictation, "Dictation resumed")
        }
        if paused, let current = recording {
            endRecording(current.id, reason: .paused)
        } else {
            present()
        }
    }

    /// Drops the cached cleanup provider and credential, for when cleanup is switched off or its settings change.
    func invalidateCleanup() {
        services.cleanup.invalidate()
    }

    /// Clear history started a new recovery generation: a notice about text kept before it, on the pill or waiting
    /// for it, offers text that is gone, and leaves.
    func recoveryWasCleared() {
        guard notices.recoveryCleared(current: services.recovery.generation) else { return }
        noticeTimer?.cancel()
        noticeTimer = nil
        showNextNoticeOrPresent()
    }

    /// A meter reading or a stop request from the capture engine, on the main actor. Events of a recording that no
    /// longer holds the microphone are ignored: its owner has moved on.
    func handleCaptureEvent(_ event: CaptureEvent) {
        guard !isClosing, let current = recording, current.id == event.owner else {
            checkpoints.ignoredCaptureEvents += 1
            return
        }
        switch event.kind {
        case .level(let level):
            guard current.phase == .live else {
                checkpoints.ignoredCaptureEvents += 1
                return
            }
            levelDbfs = level.rmsDbfs
            present()
        case .stopRequested(let ending):
            let reason: DictationStopReason
            switch ending {
            case .silence: reason = .silence
            case .durationLimit: reason = .durationLimit
            case .deviceChanged, .formatChanged, .conversionFailed: reason = .deviceFault
            }
            endRecording(current.id, reason: reason)
        }
    }

    /// A duration deadline fell due. Honored only for the recording it was armed for, while that recording is still
    /// live and has run for its whole ceiling: a tick queued for an earlier recording, or one that arrives just after
    /// the next recording started, ends nothing.
    func durationDeadlineReached(for id: RecordingID) {
        guard !isClosing, let current = recording, current.id == id, current.phase == .live,
            let limit = current.policy.maximumDuration,
            current.admittedAt.duration(to: services.clock.now) >= limit
        else {
            checkpoints.ignoredDeadlines += 1
            ScribeLog.debug(
                .dictation, "A duration deadline arrived with nothing to stop", .integer("dictation", id.rawValue))
            return
        }
        ScribeLog.warning(
            .dictation, "The recording reached its duration ceiling and is transcribed, as a forgotten toggle would",
            .integer("dictation", id.rawValue))
        endRecording(id, reason: .durationLimit)
    }

    // MARK: - Recording

    /// Admits a recording when dictation may start one now, and returns whether it did. Everything that can take
    /// time (the Accessibility query for the target, the device) runs on a later turn of the main actor, because a
    /// press can arrive in the event tap's callback, which must return at once.
    @discardableResult
    func beginRecording(_ trigger: DictationTrigger) -> Bool {
        if let refusal = activationRefusal() {
            switch refusal {
            case .closing, .paused, .alreadyRecording:
                ScribeLog.debug(.dictation, "An activation started nothing", .name("because", refusal))
            case .stillProcessing:
                ScribeLog.info(
                    .dictation, "An activation was turned away while earlier dictations are processed",
                    .count("processing", dictations.count))
                postOutcome(.stillProcessing, about: nil, stage: .admission)
            }
            return false
        }

        let id = RecordingID.next()
        let policy = CaptureStopPolicy(
            gesture: trigger.gesture,
            autoStopOnSilence: trigger.stopsOnSilence(toggleKeyOptedIn: configuration.toggleKeyStopsOnSilence()),
            maximumDuration: configuration.maximumDuration)
        // The recording owns the pill from its admission; the notice on it has been seen.
        notices.yieldToRecording()
        noticeTimer?.cancel()
        noticeTimer = nil
        levelDbfs = AudioLevelMeasurement.toDbfs(0)
        recording = LiveRecording(
            id: id, trigger: trigger, policy: policy, admittedAt: services.clock.now, lease: services.activity.begin())
        ScribeLog.info(
            .dictation, "Recording admitted", .integer("dictation", id.rawValue), .name("trigger", trigger),
            .name("gesture", trigger.gesture), .flag("stopsOnSilence", policy.stopsOnSilence),
            .flag("durationCeiling", policy.maximumDuration != nil))
        Task { [weak self] in
            await self?.openMicrophone(for: id)
        }
        present()
        return true
    }

    private func activationRefusal() -> ActivationRefusal? {
        if isClosing { return .closing }
        if isPaused { return .paused }
        if recording != nil { return .alreadyRecording }
        if dictations.count >= configuration.maximumDictationsInProcessing { return .stillProcessing }
        return nil
    }

    /// Asks for the microphone, then captures the target while it opens. The admission is checked again first: a
    /// pause, a stop or shutdown that arrived after the press has already ended this recording, and nothing opens.
    private func openMicrophone(for id: RecordingID) async {
        checkpoints.admissionChecks += 1
        guard !isClosing, let admitted = recording, admitted.id == id, admitted.phase == .admitted else { return }
        let opening = services.capture.startOpening(owner: id, policy: admitted.policy, events: captureEvents.sink)
        let target = services.targeting.captureTarget()
        let profiles = services.rules.isLoaded ? services.rules.appProfiles : nil
        if var current = recording, current.id == id {
            current.phase = .opening
            current.target = target
            current.profiles = profiles
            recording = current
        }

        let outcome: CaptureOpenOutcome
        do {
            outcome = try await opening.value
        } catch {
            checkpoints.openAnswers += 1
            microphoneFailedToOpen(id, error)
            return
        }
        checkpoints.openAnswers += 1

        // A stop that arrived while the device opened took the recording, and the engine closes or never opened the
        // device for it.
        guard var opened = recording, opened.id == id else { return }
        switch outcome {
        case .live:
            opened.phase = .live
            opened.deadline = armDurationDeadline(for: opened)
            recording = opened
            ScribeLog.info(.dictation, "Recording", .integer("dictation", id.rawValue))
            present()
        case .stoppedBeforeOpen, .stoppedWhileOpening:
            // Only this controller stops its recordings, and it lets go of a recording when it does, so the engine
            // saw a stop from somewhere else: nothing is open and nothing was recorded.
            recording = nil
            opened.lease.end()
            settleToggle(of: opened)
            ScribeLog.warning(
                .dictation, "The microphone reported a stop this recording never asked for",
                .integer("dictation", id.rawValue), .name("outcome", outcome))
            showNextNoticeOrPresent()
        }
    }

    private func microphoneFailedToOpen(_ id: RecordingID, _ error: any Error) {
        ScribeLog.error(.dictation, "The microphone did not open", .integer("dictation", id.rawValue), .failure(error))
        // Stopped while it was opening: the stop has already let it go.
        guard let failed = recording, failed.id == id else { return }
        recording = nil
        failed.deadline?.cancel()
        failed.lease.end()
        settleToggle(of: failed)
        // Outcomes that waited for this recording go first; this one queues behind them.
        let shown: Bool
        if let captureError = error as? AudioCaptureEngineError, case .microphoneNotAuthorized = captureError {
            shown = postOutcome(
                .microphoneAccessNeeded, about: id, stage: .capture, notification: .microphoneAccessNeeded)
        } else {
            shown = postOutcome(
                .microphoneUnavailable, about: id, stage: .capture, notification: .microphoneUnavailable)
        }
        if !shown {
            showNextNoticeOrPresent()
        }
    }

    /// The recording's own ceiling, timed from its admission.
    private func armDurationDeadline(for live: LiveRecording) -> Task<Void, Never>? {
        guard let limit = live.policy.maximumDuration else { return nil }
        let id = live.id
        let deadline = live.admittedAt.advanced(by: limit)
        let clock = services.clock
        return Task { [weak self] in
            do {
                try await clock.sleep(until: deadline)
            } catch {
                return
            }
            self?.durationDeadlineReached(for: id)
        }
    }

    /// Ends recording `id` once, whatever ended it. It only retires the recording, since a release can arrive in
    /// the event tap's callback: the recording gives up the microphone, and a recording that holds one takes its
    /// place in both turn queues now, in the order recordings stopped, while its samples are sealed off the main
    /// actor. A recording that never held the microphone (stopped before it was asked for) ends quietly.
    private func endRecording(_ id: RecordingID, reason: DictationStopReason) {
        guard let ended = recording, ended.id == id else { return }
        recording = nil
        ended.deadline?.cancel()
        let sealing = services.capture.retire(owner: id)
        if reason != .hotkeyReleased {
            settleToggle(of: ended)
        }
        ScribeLog.info(
            .dictation, "Recording stopped", .integer("dictation", id.rawValue), .name("reason", reason),
            .duration("held", ended.admittedAt.duration(to: services.clock.now)),
            .flag("heldMicrophone", sealing != nil))

        guard !isClosing, let sealing else {
            ended.lease.end()
            showNextNoticeOrPresent()
            return
        }

        let dictation = AdmittedDictation(
            id: id, trigger: ended.trigger, target: ended.target, profiles: ended.profiles, sealing: sealing,
            stopReason: reason, lease: ended.lease)
        transcriptionTurns.enroll(id)
        deliveryTurns.enroll(id)
        dictations[id] = Task { [weak self] in
            await self?.process(dictation)
        }
        showNextNoticeOrPresent()
    }

    /// A toggle whose recording ended some other way than by its key is still on; its next change must not count as
    /// the second tap of this one (Windows' `CancelToggle`), and Caps Lock starts the next recording only when its
    /// lock turns on. Called only for the recording that was current, and the key's listener ignores a binding it no
    /// longer has.
    private func settleToggle(of ended: LiveRecording) {
        guard case .hotkey(let binding) = ended.trigger, binding.gesture == .toggle else { return }
        triggers?.cancelToggle(binding)
    }

    // MARK: - Processing, in Windows' order

    private var mayContinue: Bool {
        !isClosing && !Task.isCancelled
    }

    private func process(_ dictation: AdmittedDictation) async {
        defer { finishProcessing(dictation) }
        let id = dictation.id
        let clock = services.clock

        // The samples, sealed off the main actor. Sealing always finishes, and quickly: a buffer in flight and the
        // resampler's tail.
        let sealed = await dictation.sealing.value
        guard mayContinue else { return stopped(dictation, at: .beforeTranscription) }
        guard let captured = sealed, !captured.samples.isEmpty else {
            return capturedNothing(dictation)
        }
        let summary = captured.summary
        ScribeLog.debug(
            .dictation, "Capture sealed", .integer("dictation", id.rawValue), .count("samples", captured.samples.count))
        var report = PipelineReport(
            dictationID: id.rawValue, capturedAt: summary.startedAt, trigger: dictation.trigger,
            stopReason: dictation.stopReason, captureDuration: summary.durationSeconds)

        // Raw speech recognition, one recognizer at a time, in dictation order.
        guard await transcriptionTurns.waitForTurn(id), mayContinue else {
            return stopped(dictation, at: .beforeTranscription)
        }
        let decodeStarted = clock.now
        let transcription: TranscriptionResult
        do {
            transcription = try await services.transcriber.transcribe(
                samples: captured.samples, sampleRate: summary.sampleRate)
        } catch {
            transcriptionTurns.finish(id)
            guard mayContinue else { return stopped(dictation, at: .duringTranscription) }
            return recognitionFailed(dictation, error, report: report)
        }
        transcriptionTurns.finish(id)
        let decode = decodeStarted.duration(to: clock.now)
        report.decodeDuration = decode.seconds
        report.realTimeFactor = summary.durationSeconds > 0 ? decode.seconds / summary.durationSeconds : nil
        // The recognizer hands back a transcript it had already produced even when the cancellation arrived just
        // after it exited, so the dictation checks for itself before it uses one.
        guard mayContinue else { return stopped(dictation, at: .afterTranscription) }
        let raw = transcription.text
        report.rawText = raw
        ScribeLog.info(
            .dictation, "Speech recognized", .integer("dictation", id.rawValue), .count("characters", raw.count),
            .duration("decode", decode), .name("backend", transcription.backend))
        guard !Self.isBlank(raw) else { return nothingToInsert(dictation, report: report) }

        // The user's rules and app profiles: startup's first load has to have finished (`StartupGate`).
        let rules = services.rules
        guard await awaitUnlessCancelled({ await rules.waitUntilLoaded() }) != nil, mayContinue else {
            return stopped(dictation, at: .waitingForRules)
        }
        let target = dictation.target
        let profile = AppProfileMatcher.match(
            profiles: dictation.profiles ?? rules.appProfiles,
            bundleIdentifier: target?.bundleIdentifier,
            processName: target?.processName)
        let newlineMode = AppProfileMatcher.resolveNewlineMode(
            profile: profile, globalDefault: configuration.newlineMode, bundleIdentifier: target?.bundleIdentifier)
        let singleLine = AppProfileMatcher.flattensNewlines(newlineMode, bundleIdentifier: target?.bundleIdentifier)

        // With AI cleanup off: snippets, then the dictionary and the libraries, each once, as Windows runs them. With
        // cleanup on, every replacement is decided once on the raw transcript, exactly as cleanup off decides it, and
        // split around the request: the vocabulary rules' (one-line spellings of at most 100 characters with no em or
        // en dash, already in the reply's normal form) are made in the text the provider is sent and its reply is
        // checked against, so the model starts from the user's spellings; the snippets and the template-like
        // replacements are held back and made on the accepted reply where it kept the words that set them off. So no
        // snippet template or template-like replacement ever reaches a provider, a dash the user wrote survives, no
        // rule runs twice, and a reply that is the text sent gives exactly the cleanup-off text.
        let post: TextPostProcessingResult
        var postDuration = Duration.zero
        if services.cleanup.isEnabled {
            let vocabularyStarted = clock.now
            let pass = rules.correctVocabulary(raw)
            postDuration += vocabularyStarted.duration(to: clock.now)
            if Self.isBlank(pass.text) {
                post = rules.postProcess(raw)
            } else {
                report.sentText = pass.text
                let stage = await cleanUp(pass.text, dictation: dictation, profile: profile, singleLine: singleLine)
                guard mayContinue else { return stopped(dictation, at: .duringCleanup) }
                report.cleanupOutcome = stage.outcome
                report.cleanupDuration = stage.requestDuration?.seconds
                if stage.outcome == .fellBack {
                    postOutcome(.cleanupFellBack, about: id, stage: .cleanup)
                }
                let finishStarted = clock.now
                if let cleaned = stage.text {
                    report.cleanedText = cleaned
                    post = rules.finishAfterCleanup(cleaned, after: pass)
                } else {
                    // Exactly what cleanup off gives: the vocabulary step is dropped and every rule runs once, in
                    // Windows' order, on the raw transcript.
                    post = rules.postProcess(raw)
                }
                postDuration += finishStarted.duration(to: clock.now)
            }
        } else {
            let postStarted = clock.now
            post = rules.postProcess(raw)
            postDuration += postStarted.duration(to: clock.now)
        }
        report.postProcessingDuration = postDuration.seconds
        report.postProcessing = post
        guard !Self.isBlank(post.text) else { return nothingToInsert(dictation, report: report) }

        // Line breaks last, for the target captured at activation: a terminal takes a newline as Enter.
        let insertion = AppProfileMatcher.applyNewlineMode(
            newlineMode, to: post.text, bundleIdentifier: target?.bundleIdentifier)
        report.finalText = insertion

        // Delivery in dictation order. The check after the wait is the last one: recovery and delivery follow with
        // no suspension in between.
        guard await deliveryTurns.waitForTurn(id), mayContinue else { return stopped(dictation, at: .beforeDelivery) }
        services.recovery.set(insertion)
        let recoveryGeneration = services.recovery.generation
        let deliveryStarted = clock.now
        let injection: InjectionResult
        if let destination = target?.injection {
            injection = await services.injector.inject(text: insertion, into: destination, shiftReturnLineBreaks: true)
        } else {
            // A target that could not be captured is never taken to mean "wherever focus is now".
            injection = InjectionResult(delivery: .targetUnknown)
        }
        report.injectionDuration = deliveryStarted.duration(to: clock.now).seconds
        report.injectionResult = injection
        ScribeLog.log(
            injection.isComplete || injection.delivery == .cancelled ? .info : .warning, .dictation,
            "Delivery finished",
            [
                .integer("dictation", id.rawValue), .name("delivery", injection.delivery),
                .name("clipboard", injection.clipboard), .name("restore", injection.restore),
            ])
        // Cancelled before anything was sent: shutdown stopped it, and there is nothing to record.
        guard injection.delivery != .cancelled else { return stopped(dictation, at: .duringDelivery) }

        // After delivery, so storage never sits between speaking and typing. The check before delivery is this
        // entry's admission check too: an entry describes text that went in, may have, or is kept for recovery, so a
        // delivery that began before shutdown is recorded, and shutdown drains the writer only after every dictation
        // has returned. A dictation still being processed when Clear history ran is recorded here, after the Clear
        // (`HistoryWriter.clearHistory`).
        _ = services.history.enqueue(
            DictationHistoryRecord(
                startedAt: summary.startedAt,
                durationSeconds: summary.durationSeconds,
                sampleCount: summary.sampleCount,
                decodeMilliseconds: decode.seconds * 1_000,
                cleanupMilliseconds: report.cleanupDuration.map { $0 * 1_000 },
                transcriptText: insertion,
                targetApp: target?.bundleIdentifier ?? target?.processName),
            dictationID: id.rawValue)
        services.reports.publish(report)
        announce(injection, of: dictation, transcript: insertion, recoveryGeneration: recoveryGeneration)
    }

    /// Sends `sent`, the raw transcript with the vocabulary rules applied, and checks the reply against it.
    private func cleanUp(
        _ sent: String, dictation: AdmittedDictation, profile: AppProfile?, singleLine: Bool
    ) async -> CleanupStage {
        let id = dictation.id
        let clock = services.clock
        let provider: any CleanupProvider
        do {
            provider = try await services.cleanup.provider()
        } catch {
            if mayContinue {
                ScribeLog.warning(
                    .cleanup, "AI cleanup is on but could not start, so the raw transcript is used",
                    .integer("dictation", id.rawValue), .failure(error))
            }
            return CleanupStage(outcome: .fellBack, text: nil, requestDuration: nil)
        }
        guard mayContinue else { return CleanupStage(outcome: .fellBack, text: nil, requestDuration: nil) }

        let style = CleanupPrompt.writingStyle(profileStyle: profile?.writingStylePrompt, requireSingleLine: singleLine)
        let request = CleanupRequest(
            transcript: CleanupPrompt.wrapTranscript(sent),
            writingStylePrompt: CleanupPrompt.systemPrompt(
                writingStyle: style, useLocalPrompt: provider.usesLocalCleanupPrompt),
            singleLineMode: singleLine)
        let started = clock.now
        let response: CleanupResponse
        do {
            response = try await provider.clean(request)
        } catch {
            let elapsed = started.duration(to: clock.now)
            if mayContinue {
                ScribeLog.warning(
                    .cleanup, "AI cleanup failed, so the raw transcript is used", .integer("dictation", id.rawValue),
                    .duration("cleanup", elapsed), .failure(error))
            }
            return CleanupStage(outcome: .fellBack, text: nil, requestDuration: elapsed)
        }
        let elapsed = started.duration(to: clock.now)

        // Against the text the model was sent; the guard also normalizes the reply's dashes.
        switch CleanupResponseGuard.sanitize(candidate: response.cleanedText, original: sent) {
        case .accepted(let cleaned):
            let outcome: DictationCleanupOutcome = cleaned == sent ? .unchanged : .cleaned
            ScribeLog.info(
                .cleanup, "AI cleanup finished", .integer("dictation", id.rawValue), .name("outcome", outcome),
                .duration("cleanup", elapsed))
            return CleanupStage(outcome: outcome, text: cleaned, requestDuration: elapsed)
        case .rejected(let reason):
            ScribeLog.warning(
                .cleanup, "The AI cleanup reply was rejected, so the raw transcript is used",
                .integer("dictation", id.rawValue), .name("reason", reason), .duration("cleanup", elapsed))
            return CleanupStage(outcome: .fellBack, text: nil, requestDuration: elapsed)
        }
    }

    private func recognitionFailed(_ dictation: AdmittedDictation, _ error: any Error, report failure: PipelineReport) {
        ScribeLog.error(
            .dictation, "Speech recognition failed", .integer("dictation", dictation.id.rawValue), .failure(error))
        var failed = failure
        failed.failureStage = .decode
        // Scribe's own words (`TranscriptionError`), shown only in Settings' Playground.
        failed.failureReason = error.localizedDescription
        services.reports.publish(failed)
        if let transcriptionError = error as? TranscriptionError,
            case .backendMissing(let issue) = transcriptionError
        {
            postOutcome(
                .recognizerMissing, about: dictation.id, stage: .recognition,
                notification: .recognizerMissing(issue))
        } else {
            postOutcome(.transcriptionFailed, about: dictation.id, stage: .recognition)
        }
    }

    /// The recording held the microphone and captured nothing, such as a quick tap. Nothing to transcribe; a device
    /// fault says the microphone was lost.
    private func capturedNothing(_ dictation: AdmittedDictation) {
        ScribeLog.info(.dictation, "The recording captured nothing", .integer("dictation", dictation.id.rawValue))
        if dictation.stopReason == .deviceFault {
            postOutcome(.microphoneUnavailable, about: dictation.id, stage: .capture)
        }
    }

    /// An empty transcript is nothing to insert, not an error: no notice, no history entry.
    private func nothingToInsert(_ dictation: AdmittedDictation, report: PipelineReport) {
        ScribeLog.info(.dictation, "Nothing to insert", .integer("dictation", dictation.id.rawValue))
        services.reports.publish(report)
    }

    private func stopped(_ dictation: AdmittedDictation, at point: StopPoint) {
        ScribeLog.info(
            .dictation, "Dictation processing stopped for shutdown", .integer("dictation", dictation.id.rawValue),
            .name("at", point))
    }

    /// The pill's notice and, when the text did not go in, a notification with the transcript to copy. Both are
    /// refused when Clear history has removed that transcript since it was kept.
    private func announce(
        _ injection: InjectionResult, of dictation: AdmittedDictation, transcript: String, recoveryGeneration: UInt64
    ) {
        let id = dictation.id
        switch injection.delivery {
        case .accessibility, .pasted, .typed, .nothingToInsert:
            if dictation.stopReason == .deviceFault {
                postOutcome(.microphoneStoppedEarly, about: id, stage: .capture)
            } else if dictation.stopReason == .durationLimit {
                postOutcome(.durationLimitReached, about: id, stage: .capture)
            }
        case .cancelled:
            return
        case .targetChanged, .targetUnknown, .targetUnresponsive, .noFocusedElement, .failed:
            postOutcome(
                .textKept, about: id, stage: .delivery, recoveryGeneration: recoveryGeneration,
                notification: .notInserted(transcript, recoveryGeneration: recoveryGeneration))
        case .typedPartially:
            postOutcome(
                .partlyInserted, about: id, stage: .delivery, recoveryGeneration: recoveryGeneration,
                notification: .partlyInserted(transcript, recoveryGeneration: recoveryGeneration))
        case .accessibilityUnconfirmed:
            postOutcome(
                .mayNotBeInserted, about: id, stage: .delivery, recoveryGeneration: recoveryGeneration,
                notification: .mayNotBeInserted(transcript, recoveryGeneration: recoveryGeneration))
        case .accessibilityDenied:
            postOutcome(
                .accessibilityNeeded, about: id, stage: .delivery, recoveryGeneration: recoveryGeneration,
                notification: .accessibilityNeeded(transcript, recoveryGeneration: recoveryGeneration))
        }
    }

    /// Nobody is left to act on a notice once shutdown has begun.
    private func notify(_ notice: DictationNotice) {
        guard !isClosing else { return }
        services.notifier.notify(notice)
    }

    private func finishProcessing(_ dictation: AdmittedDictation) {
        transcriptionTurns.finish(dictation.id)
        deliveryTurns.finish(dictation.id)
        dictations[dictation.id] = nil
        dictation.lease.end()
        showNextNoticeOrPresent()
    }

    private static func isBlank(_ text: String) -> Bool {
        text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    // MARK: - Outcomes and the pill

    /// One outcome, through the notice schedule. `notification`, when given, is posted whatever the pill does (it is
    /// the lasting record of something that needs the user), unless the outcome is rejected; a cleanup fallback or a
    /// failed recognition the pill cannot show at once is posted as a notification instead. Returns whether the
    /// outcome went on the pill.
    @discardableResult
    private func postOutcome(
        _ kind: OverlayNotice, about source: RecordingID?, stage: DictationNoticeStage,
        recoveryGeneration: UInt64? = nil, notification: DictationNotice? = nil
    ) -> Bool {
        nextNoticeID &+= 1
        let outcome = DictationOutcomeNotice(
            id: nextNoticeID, kind: kind, source: source, stage: stage, recoveryGeneration: recoveryGeneration)
        let arrival = notices.arrive(
            outcome, pillIsOwned: recording != nil, isClosing: isClosing,
            recoveryGeneration: services.recovery.generation)
        var shown = false
        switch arrival {
        case .show:
            show(outcome)
            shown = true
        case .waiting:
            ScribeLog.debug(
                .overlay, "A notice waits for the pill", .name("kind", kind), .name("stage", stage),
                .count("waiting", notices.waiting.count))
        case .notify:
            ScribeLog.info(
                .overlay, "The pill is taken, so a notice is posted as a notification", .name("kind", kind),
                .name("stage", stage))
            if let fallback = Self.fallbackNotification(for: kind) {
                notify(fallback)
            }
        case .rejected(let rejection):
            checkpoints.rejectedNotices += 1
            ScribeLog.debug(
                .overlay, "A notice was dropped", .name("kind", kind), .name("stage", stage),
                .name("because", rejection))
            return false
        }
        if let notification {
            notify(notification)
        }
        return shown
    }

    private static func fallbackNotification(for kind: OverlayNotice) -> DictationNotice? {
        switch kind {
        case .cleanupFellBack: return .cleanupFellBack
        case .transcriptionFailed: return .transcriptionFailed
        default: return nil
        }
    }

    /// Puts `outcome` on the pill for `configuration.noticeDuration`, under the revision `present()` allocates now.
    private func show(_ outcome: DictationOutcomeNotice) {
        noticeTimer?.cancel()
        // `present()` takes exactly the next revision, which becomes this notice's token.
        notices.didShow(outcome, token: revision &+ 1)
        let token = present()
        let clock = services.clock
        let until = clock.now.advanced(by: configuration.noticeDuration)
        noticeTimer = Task { [weak self] in
            do {
                try await clock.sleep(until: until)
            } catch {
                return
            }
            self?.noticeExpired(token: token)
        }
    }

    /// The timed end of the notice shown under `token`. Stale when that notice is no longer on the pill (a recording
    /// took the pill, a Clear removed it, or feedback was refreshed): then it changes nothing.
    private func noticeExpired(token: UInt64) {
        guard notices.expire(token: token) else {
            checkpoints.ignoredNoticeEnds += 1
            ScribeLog.debug(.overlay, "A notice's timed end arrived after the notice was gone; it changes nothing")
            return
        }
        noticeTimer = nil
        showNextNoticeOrPresent()
    }

    /// Shows the next waiting outcome when the pill is free, or presents what the pill shows otherwise.
    private func showNextNoticeOrPresent() {
        if let next = notices.takeNext(pillIsOwned: recording != nil) {
            show(next)
        } else {
            present()
        }
    }

    /// What the pill shows now: a live recording's meter first, then a notice, then processing while any dictation
    /// is still being processed.
    private var overlayState: OverlayState {
        if let recording {
            if recording.phase == .live {
                return .listening(levelDbfs: levelDbfs)
            }
            return dictations.isEmpty ? .hidden : .processing
        }
        if let shown = notices.shown {
            return .notice(shown.notice.kind)
        }
        return dictations.isEmpty ? .hidden : .processing
    }

    /// Hands the current state to the presenter under the next revision, and returns that revision. Once shutdown has
    /// begun it presents nothing: `presentClosed` has shown the last state.
    @discardableResult
    private func present() -> UInt64 {
        guard !isClosing else { return revision }
        revision &+= 1
        services.presenter.present(
            DictationPresentation(
                revision: revision, overlay: overlayState, isRecording: recording != nil, isPaused: isPaused))
        return revision
    }

    /// Shutdown's one presentation: hidden, not recording, under the next revision.
    private func presentClosed() {
        revision &+= 1
        services.presenter.present(
            DictationPresentation(revision: revision, overlay: .hidden, isRecording: false, isPaused: isPaused))
    }

    // MARK: - Shutdown

    /// Shuts dictation down in its one safe order and returns what it did; the app replies to
    /// `applicationShouldTerminate` only after this returns. Nothing new is admitted from the first step. The live
    /// recording is discarded. Every dictation in processing is cancelled; a delivery in progress is awaited to
    /// completion, however long that takes, because exiting during a paste would leave the user's previous pasteboard
    /// only in memory (`TextInjector.waitUntilIdle`). Every cancelled dictation is then awaited, which lets
    /// `ProcessRunner` stop and reap a recognizer's process group, and the device work is let finish. Last,
    /// `stoppingMaintenance` runs and the history writer is drained within `configuration.historyDrainTimeout`, both
    /// off the main actor since both block. Later calls await the first one's work.
    func shutDown(stoppingMaintenance: (@Sendable () -> Void)? = nil) async -> DictationShutdownReport {
        if let shutdown {
            return await shutdown.value
        }
        let work = Task { [self] in
            await runShutdown(stoppingMaintenance: stoppingMaintenance)
        }
        shutdown = work
        return await work.value
    }

    private func runShutdown(stoppingMaintenance: (@Sendable () -> Void)?) async -> DictationShutdownReport {
        isClosing = true
        notices.close()
        noticeTimer?.cancel()
        noticeTimer = nil
        let discarded = recording
        var discardedSeal: Task<CapturedAudio?, Never>?
        if let discarded {
            recording = nil
            discarded.deadline?.cancel()
            discardedSeal = services.capture.retire(owner: discarded.id)
            discarded.lease.end()
            ScribeLog.info(
                .dictation, "Recording stopped", .integer("dictation", discarded.id.rawValue),
                .name("reason", DictationStopReason.shutdown), .flag("discarded", true))
        }
        // One last presentation, under the next revision: the pill and the tray go idle at once, and `present` turns
        // away every later update, so nothing a stopping dictation does can show again.
        presentClosed()
        let running = Array(dictations.values)
        ScribeLog.info(
            .dictation, "Shutting down: nothing new starts and dictations in progress are stopped",
            .flag("recording", discarded != nil), .count("processing", running.count))
        for dictation in running {
            dictation.cancel()
        }

        shutdownProgress = .awaitingDelivery
        await services.injector.waitUntilIdle()
        shutdownProgress = .awaitingDictations
        for dictation in running {
            await dictation.value
        }
        shutdownProgress = .awaitingDevice
        // The discarded recording's audio is dropped; its seal is awaited so its device has closed.
        _ = await discardedSeal?.value
        await services.capture.waitUntilIdle()

        shutdownProgress = .drainingHistory
        let history = services.history
        let timeout = configuration.historyDrainTimeout
        let drained = await Task.detached(priority: .userInitiated) { () -> HistoryDrainResult in
            stoppingMaintenance?()
            return history.complete(timeout: timeout)
        }.value
        shutdownProgress = .finished
        ScribeLog.info(
            .dictation, "Shutdown finished", .flag("historyDrained", drained.drained),
            .count("historyStillWriting", drained.stillWriting), .count("historyAbandoned", drained.abandoned))
        return DictationShutdownReport(
            discardedRecording: discarded != nil, cancelledDictations: running.count, history: drained)
    }
}
