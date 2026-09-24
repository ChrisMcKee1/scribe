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
    /// The event tap was disabled and, re-read afterwards, the key was no longer down.
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

/// A stopped recording admitted to processing, with everything captured when it started.
private struct AdmittedDictation: Sendable {
    let id: RecordingID
    let trigger: DictationTrigger
    let target: DictationTarget?
    let profiles: [AppProfile]?
    let captured: CapturedAudio
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
/// the press arrives and checked again when the microphone is asked for, so a pause that lands in between wins. Only a
/// toggle (Caps Lock, the tray) stops on silence; a held key never does. Both stop at the duration ceiling, which is a
/// deadline of this controller's own (Windows' `ArmDurationLimit` and `TryAcceptDurationLimit`), since the capture
/// engine's own ceiling and silence clock only move while buffers arrive.
///
/// **Processing.** A stopped recording is admitted to processing in the order recordings stopped, and a new recording
/// can start while earlier ones are processed. Each dictation runs raw speech recognition (one recognizer at a time),
/// waits for startup's first rule load, runs optional AI cleanup on the raw transcript with the target's writing style
/// (and a single-line instruction when the target flattens line breaks), checks the reply against the raw transcript
/// and normalizes its dashes (`CleanupResponseGuard`), applies snippets and the dictionary, formats line breaks for
/// the captured target, and delivers into that target only. Deliveries run in dictation order, so a second
/// dictation's text never goes in before the first's. Cleanup never sees a snippet template: templates are expanded
/// after it. Cancellation and admission are checked again before cleanup, recovery and delivery, because the
/// recognizer returns a transcript it has already produced even when the cancellation arrives just after it exited.
///
/// **Presentation.** Every change to what the tray and the pill show takes the next revision (`DictationPresentation`),
/// and a notice's timed end is tagged with the revision that showed it, so a stale end, or one that arrives after a
/// newer recording started, changes nothing. A live recording always owns the pill.
///
/// **Shutdown** (`shutDown`): nothing new is admitted, the live recording is discarded, every dictation in processing
/// is cancelled, a delivery in progress is awaited to completion (`TextInjector.waitUntilIdle`, which puts a borrowed
/// pasteboard back), every cancelled dictation is awaited (its recognizer's process group stopped and reaped by
/// `ProcessRunner`), and the history writer is drained within its bound, off the main actor.
///
/// Logs are shapes only: ids, counts, durations, enum names and failure shapes, never text, profile names or targets.
@MainActor
final class DictationController {
    struct Configuration: Sendable {
        /// Windows' `MaxDictationMinutes`; nil or zero means no ceiling.
        var maximumDuration: Duration? = CaptureStopPolicy.defaultMaximumDuration
        /// Whether a toggle's recording stops on silence (a held key never does).
        var autoStopOnSilence = true
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

    private let services: Services
    private let configuration: Configuration
    /// The push-to-talk key, told when a toggle's recording ended some other way than by the key.
    weak var triggers: (any DictationTriggerSource)?

    private(set) var isPaused: Bool
    private(set) var isClosing = false
    private var recording: LiveRecording?
    private var dictations: [RecordingID: Task<Void, Never>] = [:]
    private let transcriptionTurns = DictationTurns()
    private let deliveryTurns = DictationTurns()
    private var revision: UInt64 = 0
    private var levelDbfs = AudioLevelMeasurement.toDbfs(0)
    private var notice: (kind: OverlayNotice, revision: UInt64)?
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

    /// Dictations whose text is ready and waiting for an earlier dictation's delivery to finish.
    var dictationsWaitingToDeliver: Int {
        deliveryTurns.waitingCount
    }

    // MARK: - Inputs

    /// The push-to-talk key went down (Caps Lock: turned on). Returns whether a recording started, so the key's
    /// listener knows whether it is now held.
    @discardableResult
    func hotkeyPressed(_ binding: HotkeyBinding) -> Bool {
        beginRecording(.hotkey(binding))
    }

    /// The push-to-talk key came up (Caps Lock: turned off), or its listener settled it. Ends the recording only when
    /// that key started it.
    func hotkeyReleased(_ binding: HotkeyBinding, cause: HotkeyReleaseCause) {
        guard let current = recording, case .hotkey(let started) = current.trigger, started.keyCode == binding.keyCode
        else {
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

    /// A meter reading or a stop request from the capture engine, on the main actor. Events of a recording that no
    /// longer holds the microphone are ignored: its owner has moved on.
    func handleCaptureEvent(_ event: CaptureEvent) {
        guard !isClosing, let current = recording, current.id == event.owner else { return }
        switch event.kind {
        case .level(let level):
            guard current.phase == .live else { return }
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
                showNotice(.stillProcessing)
            }
            return false
        }

        let id = RecordingID.next()
        let policy = CaptureStopPolicy(
            gesture: trigger.gesture,
            autoStopOnSilence: configuration.autoStopOnSilence,
            maximumDuration: configuration.maximumDuration)
        clearNotice()
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
            microphoneFailedToOpen(id, error)
            return
        }

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
            ScribeLog.warning(
                .dictation, "The microphone reported a stop this recording never asked for",
                .integer("dictation", id.rawValue), .name("outcome", outcome))
            present()
        }
    }

    private func microphoneFailedToOpen(_ id: RecordingID, _ error: any Error) {
        ScribeLog.error(.dictation, "The microphone did not open", .integer("dictation", id.rawValue), .failure(error))
        // Stopped while it was opening: the stop has already let it go.
        guard let failed = recording, failed.id == id else { return }
        recording = nil
        failed.deadline?.cancel()
        failed.lease.end()
        if let captureError = error as? AudioCaptureEngineError, case .microphoneNotAuthorized = captureError {
            showNotice(.microphoneAccessNeeded)
            notify(.microphoneAccessNeeded)
        } else {
            showNotice(.microphoneUnavailable)
            notify(.microphoneUnavailable)
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

    /// Ends recording `id` once, whatever ended it, and admits what it captured to processing. A recording that
    /// captured nothing (stopped before or while its microphone opened) ends quietly.
    private func endRecording(_ id: RecordingID, reason: DictationStopReason) {
        guard let ended = recording, ended.id == id else { return }
        recording = nil
        ended.deadline?.cancel()
        let stoppedAudio = services.capture.stop(owner: id)
        // A toggle whose recording ended some other way than by its key is still on; its next tap has to start a
        // new recording rather than count as the second tap of this one (Windows' `CancelToggle`).
        if case .hotkey(let binding) = ended.trigger, binding.gesture == .toggle, reason != .hotkeyReleased {
            triggers?.cancelToggle()
        }
        ScribeLog.info(
            .dictation, "Recording stopped", .integer("dictation", id.rawValue), .name("reason", reason),
            .duration("held", ended.admittedAt.duration(to: services.clock.now)),
            .count("samples", stoppedAudio?.samples.count ?? 0))

        guard !isClosing, let captured = stoppedAudio, !captured.samples.isEmpty else {
            ended.lease.end()
            if !isClosing, reason == .deviceFault {
                showNotice(.microphoneUnavailable)
            } else {
                present()
            }
            return
        }

        let dictation = AdmittedDictation(
            id: id, trigger: ended.trigger, target: ended.target, profiles: ended.profiles, captured: captured,
            stopReason: reason, lease: ended.lease)
        transcriptionTurns.enroll(id)
        deliveryTurns.enroll(id)
        dictations[id] = Task { [weak self] in
            await self?.process(dictation)
        }
        present()
    }

    // MARK: - Processing, in Windows' order

    private var mayContinue: Bool {
        !isClosing && !Task.isCancelled
    }

    private func process(_ dictation: AdmittedDictation) async {
        defer { finishProcessing(dictation) }
        let id = dictation.id
        let clock = services.clock
        let summary = dictation.captured.summary
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
                samples: dictation.captured.samples, sampleRate: summary.sampleRate)
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

        // Optional AI cleanup of the raw transcript, before any snippet or dictionary rule has run, so a snippet
        // template never reaches a provider and the user's rules have the final say over the model's wording.
        var text = raw
        if services.cleanup.isEnabled {
            let stage = await cleanUp(raw, dictation: dictation, profile: profile, singleLine: singleLine)
            guard mayContinue else { return stopped(dictation, at: .duringCleanup) }
            report.cleanupOutcome = stage.outcome
            report.cleanupDuration = stage.requestDuration?.seconds
            if let cleaned = stage.text {
                text = cleaned
                report.cleanedText = cleaned
            }
            if stage.outcome == .fellBack {
                showNotice(.cleanupFellBack)
            }
        }

        // Snippets, then the dictionary and the libraries.
        let postStarted = clock.now
        let post = rules.postProcess(text)
        report.postProcessingDuration = postStarted.duration(to: clock.now).seconds
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
        announce(injection, of: dictation, transcript: insertion)
    }

    private func cleanUp(
        _ raw: String, dictation: AdmittedDictation, profile: AppProfile?, singleLine: Bool
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
            transcript: CleanupPrompt.wrapTranscript(raw),
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

        // Against the raw transcript, the text the model was given; the guard also normalizes the reply's dashes.
        switch CleanupResponseGuard.sanitize(candidate: response.cleanedText, original: raw) {
        case .accepted(let cleaned):
            let outcome: DictationCleanupOutcome = cleaned == raw ? .unchanged : .cleaned
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

    private func recognitionFailed(_ dictation: AdmittedDictation, _ error: any Error, report: PipelineReport) {
        ScribeLog.error(
            .dictation, "Speech recognition failed", .integer("dictation", dictation.id.rawValue), .failure(error))
        var failed = report
        failed.failureStage = .decode
        // Scribe's own words (`TranscriptionError`), shown only in Settings' Playground.
        failed.failureReason = error.localizedDescription
        services.reports.publish(failed)
        if let transcriptionError = error as? TranscriptionError,
            case .backendMissing(let issue) = transcriptionError
        {
            showNotice(.recognizerMissing)
            notify(.recognizerMissing(issue))
        } else {
            showNotice(.transcriptionFailed)
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

    /// The pill's notice and, when the text did not go in, a notice with the transcript to copy.
    private func announce(_ injection: InjectionResult, of dictation: AdmittedDictation, transcript: String) {
        switch injection.delivery {
        case .accessibility, .pasted, .typed, .nothingToInsert:
            if dictation.stopReason == .deviceFault {
                showNotice(.microphoneStoppedEarly)
            } else if dictation.stopReason == .durationLimit {
                showNotice(.durationLimitReached)
            }
            return
        case .cancelled:
            return
        case .targetChanged, .targetUnknown, .targetUnresponsive, .noFocusedElement, .failed:
            showNotice(.textKept)
            notify(.notInserted(transcript))
        case .typedPartially:
            showNotice(.partlyInserted)
            notify(.partlyInserted(transcript))
        case .accessibilityUnconfirmed:
            showNotice(.mayNotBeInserted)
            notify(.mayNotBeInserted(transcript))
        case .accessibilityDenied:
            showNotice(.accessibilityNeeded)
            notify(.accessibilityNeeded(transcript))
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
        present()
    }

    private static func isBlank(_ text: String) -> Bool {
        text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    // MARK: - Presentation

    /// What the pill shows now: a live recording's meter first, then a notice, then processing while any dictation
    /// is still being processed.
    private var overlayState: OverlayState {
        if let recording {
            if recording.phase == .live {
                return .listening(levelDbfs: levelDbfs)
            }
            return dictations.isEmpty ? .hidden : .processing
        }
        if let notice {
            return .notice(notice.kind)
        }
        return dictations.isEmpty ? .hidden : .processing
    }

    /// Hands the current state to the presenter under the next revision, and returns that revision.
    @discardableResult
    private func present() -> UInt64 {
        guard !isClosing else { return revision }
        revision &+= 1
        services.presenter.present(
            DictationPresentation(
                revision: revision, overlay: overlayState, isRecording: recording != nil, isPaused: isPaused))
        return revision
    }

    /// Shows `kind` for `configuration.noticeDuration`. A recording owns the pill from its admission, so a notice
    /// that arrives meanwhile is dropped rather than covering the meter.
    private func showNotice(_ kind: OverlayNotice) {
        guard !isClosing, recording == nil else { return }
        noticeTimer?.cancel()
        notice = (kind, 0)
        let shown = present()
        notice = (kind, shown)
        let clock = services.clock
        let until = clock.now.advanced(by: configuration.noticeDuration)
        noticeTimer = Task { [weak self] in
            do {
                try await clock.sleep(until: until)
            } catch {
                return
            }
            self?.noticeExpired(shownAt: shown)
        }
    }

    /// The timed end of the notice shown at revision `shownAt`. Stale when that notice is no longer the one held (a
    /// newer notice replaced it, or a recording started and cleared it): then it changes nothing.
    private func noticeExpired(shownAt: UInt64) {
        guard let held = notice, held.revision == shownAt else {
            ScribeLog.debug(.overlay, "A notice's timed end arrived after the notice was replaced; it changes nothing")
            return
        }
        notice = nil
        present()
    }

    private func clearNotice() {
        notice = nil
        noticeTimer?.cancel()
        noticeTimer = nil
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
        clearNotice()
        let discarded = recording
        if let discarded {
            recording = nil
            discarded.deadline?.cancel()
            _ = services.capture.stop(owner: discarded.id)
            discarded.lease.end()
            ScribeLog.info(
                .dictation, "Recording stopped", .integer("dictation", discarded.id.rawValue),
                .name("reason", DictationStopReason.shutdown), .flag("discarded", true))
        }
        let running = Array(dictations.values)
        ScribeLog.info(
            .dictation, "Shutting down: nothing new starts and dictations in progress are stopped",
            .flag("recording", discarded != nil), .count("processing", running.count))
        for dictation in running {
            dictation.cancel()
        }

        await services.injector.waitUntilIdle()
        for dictation in running {
            await dictation.value
        }
        await services.capture.waitUntilIdle()

        let history = services.history
        let timeout = configuration.historyDrainTimeout
        let drained = await Task.detached(priority: .userInitiated) { () -> HistoryDrainResult in
            stoppingMaintenance?()
            return history.complete(timeout: timeout)
        }.value
        ScribeLog.info(
            .dictation, "Shutdown finished", .flag("historyDrained", drained.drained),
            .count("historyStillWriting", drained.stillWriting), .count("historyAbandoned", drained.abandoned))
        return DictationShutdownReport(
            discardedRecording: discarded != nil, cancelledDictations: running.count, history: drained)
    }
}
