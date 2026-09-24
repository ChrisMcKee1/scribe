import AVFoundation
import AudioToolbox
import CoreAudio
import Foundation
import os

/// Names one recording for the capture engine. The lifecycle issues it when a recording starts and passes it
/// to every start and stop, so each can only ever act on its own recording. Identifiers only grow: a stop that
/// names a recording also refuses any later start for that recording or an earlier one.
struct RecordingID: Hashable, Comparable, Sendable, CustomStringConvertible {
    let rawValue: UInt64

    init(rawValue: UInt64) {
        self.rawValue = rawValue
    }

    /// A new identifier, higher than every one `next()` returned before in this process. Never zero.
    static func next() -> RecordingID {
        RecordingID(rawValue: issued.withLock { last -> UInt64 in
            last += 1
            return last
        })
    }

    static func < (lhs: RecordingID, rhs: RecordingID) -> Bool {
        lhs.rawValue < rhs.rawValue
    }

    var description: String { "#\(rawValue)" }

    private static let issued = OSAllocatedUnfairLock(initialState: UInt64(0))
}

/// How `AudioCaptureEngine.start` ended, for a start that did not throw.
enum CaptureOpenOutcome: Sendable, Equatable {
    /// The microphone is open and the recording owns it.
    case live
    /// The recording's stop arrived before its microphone opened, so none was opened.
    case stoppedBeforeOpen
    /// The recording's stop arrived while its microphone was opening. The stop received whatever was
    /// captured, and the microphone was closed right after it opened.
    case stoppedWhileOpening
}

/// The samples of a recording its owner retired (`AudioCaptureEngine.retire(owner:)`), sealed on the engine's
/// control queue after the stop has returned. Every awaiter of `audio` gets the same value, one that arrives after
/// the seal at once.
final class CaptureSeal: Sendable {
    let owner: RecordingID
    private let state = OSAllocatedUnfairLock(initialState: State())

    private struct State: Sendable {
        var isSealed = false
        var audio: CapturedAudio?
        var waiters: [CheckedContinuation<CapturedAudio?, Never>] = []
    }

    fileprivate init(owner: RecordingID) {
        self.owner = owner
    }

    /// Whether the samples have been sealed.
    var isSealed: Bool {
        state.withLock { $0.isSealed }
    }

    /// What the recording captured, once sealed; nil when its samples had already been handed over. Sealing always
    /// finishes, so this is not cancellable.
    var audio: CapturedAudio? {
        get async {
            await withCheckedContinuation { (continuation: CheckedContinuation<CapturedAudio?, Never>) in
                let sealed = state.withLock { state -> CapturedAudio?? in
                    guard state.isSealed else {
                        state.waiters.append(continuation)
                        return .none
                    }
                    return .some(state.audio)
                }
                if case .some(let audio) = sealed {
                    continuation.resume(returning: audio)
                }
            }
        }
    }

    fileprivate func fulfill(_ audio: CapturedAudio?) {
        let waiters = state.withLock { state -> [CheckedContinuation<CapturedAudio?, Never>] in
            state.isSealed = true
            state.audio = audio
            let waiting = state.waiters
            state.waiters = []
            return waiting
        }
        for waiter in waiters {
            waiter.resume(returning: audio)
        }
    }
}

enum AudioCaptureEngineError: LocalizedError {
    case microphoneNotAuthorized(AVAuthorizationStatus)
    case missingInputNodeFormat
    case unsupportedInputFormat
    case converterInitializationFailed
    case engineStartFailed(underlying: any Error)
    case alreadyCapturing

    var errorDescription: String? {
        switch self {
        case .microphoneNotAuthorized:
            return "Microphone access is not authorized."
        case .missingInputNodeFormat:
            return "The input device did not report a usable audio format."
        case .unsupportedInputFormat:
            return "The input device delivers audio in a format Scribe cannot read."
        case .converterInitializationFailed:
            return "Could not create the audio converter for 16 kHz mono capture."
        case .engineStartFailed(let underlying):
            return "The audio engine could not start (error \((underlying as NSError).code))."
        case .alreadyCapturing:
            return "Another recording is still using the microphone."
        }
    }
}

/// The device side of one capture. The engine makes a new one for every capture and calls it only on its
/// control queue, never on the tap's thread or Apple's notification queue.
protocol CaptureDevice: AnyObject {
    /// Chooses the input device and reports the format its buffers will have. Nothing is delivered yet.
    func prepare() throws -> AVAudioFormat

    /// Starts delivering buffers to `deliver`, on a thread of the device's choosing, and calls
    /// `configurationChanged`, on another, when the input hardware's configuration changes.
    func start(
        deliver: @escaping @Sendable (AVAudioPCMBuffer) -> Void,
        configurationChanged: @escaping @Sendable () -> Void
    ) throws

    /// Whether the device is still running. A configuration change that stops it has ended the capture.
    var isRunning: Bool { get }

    /// Stops delivery and releases the device. Safe to call more than once, and after a failed start.
    func close()

    /// The device the input unit is pulling audio from, for `--verify-selected-microphone`.
    var currentInputDeviceID: AudioDeviceID? { get }
}

/// The microphone, owned one recording at a time.
///
/// A recording is admitted by `start(owner:policy:events:)`, which opens the device off the caller's thread,
/// and ends when its owner calls `stop(owner:)`, which returns everything the recording captured at once,
/// without waiting for the device, or `retire(owner:)`, which returns before even the samples are sealed and
/// seals them on the control queue. The samples live in the recording's `CaptureProcessor` (see there), and
/// every capture gets a generation of its own, so a tap buffer, a configuration change or a stall check that
/// arrives late can only reach the capture it was meant for, which by then has ended and ignores it.
///
/// Every call into the device (AVAudioEngine) happens on one serial control queue: opening it, closing it, and
/// reacting to a configuration change, which Apple delivers on an internal queue where tearing the engine down
/// synchronously can deadlock. Nothing on the tap's thread stops the engine; a recording that ends itself
/// (silence, its duration ceiling, a device fault) posts one stop request to its owner and hands the device
/// back to the control queue to close.
///
/// Start and stop behave like Windows' `AudioCaptureService` with an owner: a stop records that its recording
/// has stopped, so a start for that recording that has not opened the device yet opens nothing, and a stop
/// that names another recording touches nothing. However many callers stop the same recording, one of them
/// receives its audio.
final class AudioCaptureEngine: Sendable {
    static let targetSampleRate: Double = CaptureProcessor.targetSampleRate

    /// How long a device that is still running may go without delivering a buffer after a configuration
    /// change before the capture counts as dead.
    static let defaultConfigurationChangeStallLimit: Duration = .seconds(2)

    /// Runs `work` once `delay` has passed. Tests pass their own, to run a stall check at a point they choose.
    typealias Scheduler = @Sendable (_ delay: Duration, _ work: @escaping @Sendable () -> Void) -> Void

    static let dispatchAfter: Scheduler = { delay, work in
        let (seconds, attoseconds) = delay.components
        let interval = Double(seconds) + Double(attoseconds) / 1e18
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + interval) {
            work()
        }
    }

    private let makeDevice: @Sendable () -> any CaptureDevice
    private let configurationChangeStallLimit: Duration
    private let scheduleStallCheck: Scheduler
    private let resamplerTailFlush: CaptureProcessor.TailFlush
    private let controlQueue = DispatchQueue(label: "com.scribe.macos.capture.control", qos: .userInitiated)
    private let state = OSAllocatedUnfairLock(initialState: EngineState())
    private let slot = DeviceSlot()

    /// `resamplerTailFlush` is what each recording's processor uses to flush its resampler when the recording
    /// ends; tests pass their own to make it fail or to hold a stop inside it.
    init(
        configurationChangeStallLimit: Duration = AudioCaptureEngine.defaultConfigurationChangeStallLimit,
        scheduleStallCheck: @escaping Scheduler = AudioCaptureEngine.dispatchAfter,
        resamplerTailFlush: @escaping CaptureProcessor.TailFlush = CaptureProcessor.flushResamplerTail,
        makeDevice: @escaping @Sendable () -> any CaptureDevice = { AVAudioEngineCaptureDevice() }
    ) {
        self.configurationChangeStallLimit = configurationChangeStallLimit
        self.scheduleStallCheck = scheduleStallCheck
        self.resamplerTailFlush = resamplerTailFlush
        self.makeDevice = makeDevice
    }

    /// True from a recording's admission until its owner stops it, including while its device is opening and
    /// after it ended itself.
    var isCapturing: Bool {
        state.withLock { $0.current != nil }
    }

    /// The recording that owns the microphone, if any.
    var activeRecording: RecordingID? {
        state.withLock { $0.current?.owner }
    }

    /// Opens the microphone for `owner`. `events` is called on an audio thread, never with a lock held, with
    /// meter readings and at most one stop request; pass `CaptureEventRelay.sink` to receive them on the main
    /// actor. Every event names its recording, and one the tap computed just before a stop can arrive just
    /// after it, so an owner ignores events for a recording it has already stopped. Throws
    /// `AudioCaptureEngineError` when the device cannot be opened (nothing stays open), or `.alreadyCapturing`
    /// while another recording has not been stopped yet. Opening cannot be interrupted; to abandon a start,
    /// stop its recording, which is honored before the open or right after it.
    func start(
        owner: RecordingID,
        policy: CaptureStopPolicy,
        events: @escaping @Sendable (CaptureEvent) -> Void
    ) async throws -> CaptureOpenOutcome {
        let admission = state.withLock { state -> Admission in
            guard owner.rawValue > state.stoppedThrough else { return .stoppedBeforeOpen }
            guard state.current == nil else { return .busy }
            state.nextGeneration += 1
            let capture = ActiveCapture(
                owner: owner,
                generation: state.nextGeneration,
                processor: CaptureProcessor(owner: owner, policy: policy, tailFlush: resamplerTailFlush),
                events: events)
            state.current = capture
            return .admitted(capture)
        }

        switch admission {
        case .stoppedBeforeOpen:
            ScribeLog.info(
                .audio, "The recording was stopped before its microphone opened, so none was opened",
                .integer("recording", owner.rawValue))
            return .stoppedBeforeOpen
        case .busy:
            ScribeLog.warning(
                .audio, "A recording asked for the microphone while another still had it",
                .integer("recording", owner.rawValue))
            throw AudioCaptureEngineError.alreadyCapturing
        case .admitted(let capture):
            return try await withCheckedThrowingContinuation { continuation in
                controlQueue.async {
                    continuation.resume(with: Result { try self.open(capture) })
                }
            }
        }
    }

    /// Ends `owner`'s recording and returns everything it captured, or `nil` when `owner` does not hold the
    /// microphone (it was never started, a stop already took it, or a later recording has it). Returns without
    /// waiting for the device, which closes on the control queue, always before a later recording's device opens
    /// (see `open`); a tap buffer that arrives meanwhile is dropped. Seals the samples on the calling thread, which
    /// waits for a buffer the tap is converting and for the resampler's tail, so the dictation lifecycle, whose stop
    /// can come from an event tap's callback, calls `retire(owner:)` instead. Safe to call from any thread; never
    /// call it from `events`.
    @discardableResult
    func stop(owner: RecordingID) -> CapturedAudio? {
        guard let capture = take(owner) else { return nil }

        let captured = capture.processor.finish()
        let generation = capture.generation
        controlQueue.async {
            self.closeDevice(generation: generation)
        }
        if let captured {
            Self.logCompletion(captured)
        }
        return captured
    }

    /// Ends `owner`'s recording without waiting for anything: it gives up the microphone at once (a later start is
    /// admitted, and this recording's own pending start opens nothing), its processor drops every buffer the tap
    /// delivers from now on, and its samples are sealed afterwards on the control queue, ahead of any device work
    /// queued after this call, where its device then closes. Returns the seal to await, or `nil` when `owner` does
    /// not hold the microphone. Takes only the engine's own lock, never the recording's, so it returns while the tap
    /// converts a buffer or a resampler drains. Safe to call from any thread; never call it from `events`.
    func retire(owner: RecordingID) -> CaptureSeal? {
        guard let capture = take(owner) else { return nil }

        capture.processor.retire()
        let seal = CaptureSeal(owner: owner)
        let stoppedAt = Date()
        controlQueue.async {
            let captured = capture.processor.finish(at: stoppedAt)
            self.closeDevice(generation: capture.generation)
            if let captured {
                Self.logCompletion(captured)
            }
            seal.fulfill(captured)
        }
        return seal
    }

    /// Records that `owner` has stopped and takes its capture off the microphone, if it holds it.
    private func take(_ owner: RecordingID) -> ActiveCapture? {
        state.withLock { state -> ActiveCapture? in
            state.stoppedThrough = max(state.stoppedThrough, owner.rawValue)
            guard let current = state.current, current.owner == owner else { return nil }
            state.current = nil
            return current
        }
    }

    /// Returns once everything already queued for the device (opening or closing it) has run.
    func waitUntilIdle() async {
        await withCheckedContinuation { continuation in
            controlQueue.async {
                continuation.resume()
            }
        }
    }

    /// The device the open capture's input unit is pulling from. Waits on the control queue, so it is for the
    /// command-line verbs, not the main actor.
    func currentInputDeviceID() -> AudioDeviceID? {
        controlQueue.sync {
            slot.device?.currentInputDeviceID
        }
    }

    // MARK: - Control queue

    private func open(_ capture: ActiveCapture) throws -> CaptureOpenOutcome {
        dispatchPrecondition(condition: .onQueue(controlQueue))
        guard owns(capture) else { return .stoppedBeforeOpen }

        // A stop releases the microphone to the next recording before it has sealed its samples and queued its
        // device's close, so on another thread the next recording's open can get here first. The earlier device
        // is closed before a new one is made: never two engines at once, and never a device replaced unclosed.
        if slot.device != nil, slot.generation != capture.generation {
            ScribeLog.info(
                .audio, "The previous recording's microphone closes before the next one opens",
                .integer("recording", capture.owner.rawValue))
            closeDevice(generation: slot.generation)
        }

        let openStarted = ContinuousClock.now
        let device = makeDevice()
        let deviceRate: Double
        let channels: Int
        do {
            let format = try device.prepare()
            deviceRate = format.sampleRate
            channels = Int(format.channelCount)
            try capture.processor.configure(inputFormat: format)
            let generation = capture.generation
            try device.start(
                deliver: { [weak self] buffer in
                    self?.deliver(buffer, for: capture)
                },
                configurationChanged: { [weak self] in
                    self?.configurationChanged(generation: generation)
                })
        } catch {
            device.close()
            relinquish(capture)
            ScribeLog.error(
                .audio, "The microphone could not be opened", .integer("recording", capture.owner.rawValue),
                .failure(error))
            throw error
        }

        slot.generation = capture.generation
        slot.device = device
        capture.processor.markOpened(at: Date())
        let openTime = openStarted.duration(to: ContinuousClock.now)

        guard owns(capture) else {
            ScribeLog.info(
                .audio, "The recording was stopped while its microphone opened; it closes again",
                .integer("recording", capture.owner.rawValue), .duration("openTime", openTime))
            return .stoppedWhileOpening
        }

        ScribeLog.info(
            .audio, "Microphone opened",
            .integer("recording", capture.owner.rawValue),
            .decimal("deviceRate", deviceRate, precision: 0),
            .count("channels", channels),
            .flag("stopsOnSilence", capture.processor.policy.stopsOnSilence),
            .flag("durationCeiling", capture.processor.policy.maximumDuration != nil),
            .duration("openTime", openTime))
        return .live
    }

    /// Runs on the tap's thread.
    private func deliver(_ buffer: AVAudioPCMBuffer, for capture: ActiveCapture) {
        for kind in capture.processor.process(buffer) {
            if case .stopRequested(let reason) = kind {
                logEnding(of: capture.owner, reason)
                let generation = capture.generation
                controlQueue.async {
                    self.closeDevice(generation: generation)
                }
            }
            capture.events(CaptureEvent(owner: capture.owner, kind: kind))
        }
    }

    /// Runs on Apple's notification queue, which must not tear the engine down, so it only hands over.
    private func configurationChanged(generation: UInt64) {
        controlQueue.async {
            self.handleConfigurationChange(generation: generation)
        }
    }

    // Apple says the engine stops itself when the input's channel count or sample rate changes. A device that
    // is still running is given the stall limit to prove it is alive by delivering audio, so a notification
    // for a change that did not interrupt the stream never ends a good recording.
    private func handleConfigurationChange(generation: UInt64) {
        dispatchPrecondition(condition: .onQueue(controlQueue))
        guard slot.generation == generation, let device = slot.device,
            let capture = currentCapture(generation: generation)
        else {
            return
        }

        guard device.isRunning else {
            end(capture, .deviceChanged)
            return
        }

        let buffersAtChange = capture.processor.bufferCounts.received
        slot.stallCheckToken &+= 1
        let token = slot.stallCheckToken
        scheduleStallCheck(configurationChangeStallLimit) { [weak self] in
            guard let self else { return }
            controlQueue.async {
                self.checkForStall(generation: generation, token: token, buffersAtChange: buffersAtChange)
            }
        }
        ScribeLog.info(
            .audio, "The input configuration changed and the microphone is still running",
            .integer("recording", capture.owner.rawValue))
    }

    private func checkForStall(generation: UInt64, token: UInt64, buffersAtChange: Int) {
        dispatchPrecondition(condition: .onQueue(controlQueue))
        guard slot.stallCheckToken == token, slot.generation == generation, slot.device != nil,
            let capture = currentCapture(generation: generation),
            capture.processor.bufferCounts.received == buffersAtChange
        else {
            return
        }
        end(capture, .deviceChanged)
    }

    private func end(_ capture: ActiveCapture, _ reason: CaptureEndReason) {
        dispatchPrecondition(condition: .onQueue(controlQueue))
        guard capture.processor.end(reason) else { return }
        logEnding(of: capture.owner, reason)
        capture.events(CaptureEvent(owner: capture.owner, kind: .stopRequested(reason)))
        closeDevice(generation: capture.generation)
    }

    private func closeDevice(generation: UInt64) {
        dispatchPrecondition(condition: .onQueue(controlQueue))
        guard slot.generation == generation, let device = slot.device else { return }
        slot.stallCheckToken &+= 1
        slot.device = nil
        device.close()
    }

    /// A capture whose device failed to open has no owner to hand its audio to.
    private func relinquish(_ capture: ActiveCapture) {
        state.withLock { state in
            if state.current?.generation == capture.generation {
                state.current = nil
            }
        }
        _ = capture.processor.finish()
    }

    private func owns(_ capture: ActiveCapture) -> Bool {
        state.withLock { $0.current?.generation == capture.generation }
    }

    private func currentCapture(generation: UInt64) -> ActiveCapture? {
        state.withLock { state in
            state.current?.generation == generation ? state.current : nil
        }
    }

    // MARK: - Logging (shapes only)

    private func logEnding(of owner: RecordingID, _ reason: CaptureEndReason) {
        let recording = ScribeLog.Field.integer("recording", owner.rawValue)
        switch reason {
        case .silence(let detail) where detail.heardSpeech:
            ScribeLog.info(
                .audio, "Silence auto-stop: the speaker went quiet", recording,
                .decimal("peakLevel", Double(detail.peakLevel), precision: 4),
                .decimal("noiseFloor", Double(detail.noiseFloor), precision: 4),
                .decimal("voiceThreshold", Double(detail.voiceThreshold), precision: 4))
        case .silence(let detail):
            ScribeLog.warning(
                .audio, "Silence auto-stop: no speech was ever heard, so the recording ended on the lead-in limit",
                recording,
                .decimal("peakLevel", Double(detail.peakLevel), precision: 4),
                .decimal("noiseFloor", Double(detail.noiseFloor), precision: 4),
                .decimal("voiceThreshold", Double(detail.voiceThreshold), precision: 4))
        case .durationLimit:
            ScribeLog.warning(
                .audio, "The recording reached its duration ceiling and keeps what it captured", recording)
        case .deviceChanged:
            ScribeLog.warning(.audio, "The input device changed; the recording keeps what it captured", recording)
        case .formatChanged:
            ScribeLog.warning(.audio, "The input format changed; the recording keeps what it captured", recording)
        case .conversionFailed:
            ScribeLog.error(.audio, "Converting to 16 kHz failed; the recording keeps what was converted", recording)
        }
    }

    /// Logs the finished capture's shape, and warns about the ways a capture can look healthy and hold little.
    static func logCompletion(_ captured: CapturedAudio) {
        let summary = captured.summary
        let recording = ScribeLog.Field.integer("recording", captured.owner.rawValue)
        var fields: [ScribeLog.Field] = [
            recording,
            .decimal("seconds", summary.durationSeconds, precision: 2),
            .count("samples", summary.sampleCount),
            .name("ending", summary.ending),
            .count("buffers", summary.acceptedBufferCount),
            .count("droppedBuffers", summary.droppedBufferCount),
            .name("resamplerFlush", summary.resamplerFlush),
        ]
        if case .endedItself(let reason) = summary.ending {
            fields.append(.name("reason", reason))
        }
        if let signal = summary.signal {
            fields += signal.logFields
        }
        ScribeLog.log(.info, .audio, "Capture complete", fields)

        if summary.resamplerFlush == .failed {
            ScribeLog.error(
                .audio, "Converting the end of the recording to 16 kHz failed; it keeps what was converted before",
                recording)
        }

        // Each of these leaves a capture that looks healthy from outside: the meter moved and the duration is
        // right, and the recognizer then returns little or nothing.
        let openSeconds = summary.stoppedAt.timeIntervalSince(summary.startedAt)
        if summary.ending == .stoppedByOwner, openSeconds >= 1, summary.durationSeconds < openSeconds / 2 {
            ScribeLog.warning(
                .audio, "The capture holds much less audio than the time it was open; the input stopped delivering",
                recording, .decimal("openSeconds", openSeconds, precision: 2),
                .decimal("audioSeconds", summary.durationSeconds, precision: 2))
        }
        guard let signal = summary.signal, !signal.perChannel.isEmpty else { return }
        if signal.hasSilentChannel {
            ScribeLog.warning(
                .audio, "An input channel carried no audio, so averaging it in made the speech quieter", recording,
                .count("channels", signal.channels))
        } else if signal.channelsDiverge {
            ScribeLog.warning(
                .audio, "Input channels had very different levels, like a reference or echo-cancellation channel",
                recording, .count("channels", signal.channels))
        }
        if signal.peak > 0, signal.peakDbfs < -45 {
            ScribeLog.warning(
                .audio, "The capture peaked very low, where recognition is unreliable", recording,
                .decimal("peakDbfs", signal.peakDbfs))
        }
        if signal.clippedFraction > 0.01 {
            ScribeLog.warning(
                .audio, "The capture clipped", recording,
                .decimal("clippedPercent", signal.clippedFraction * 100, precision: 1))
        }
    }

    // MARK: - State

    private struct ActiveCapture: Sendable {
        let owner: RecordingID
        let generation: UInt64
        let processor: CaptureProcessor
        let events: @Sendable (CaptureEvent) -> Void
    }

    private enum Admission: Sendable {
        case stoppedBeforeOpen
        case busy
        case admitted(ActiveCapture)
    }

    private struct EngineState: Sendable {
        /// The highest recording whose stop has arrived.
        var stoppedThrough: UInt64 = 0
        var nextGeneration: UInt64 = 0
        var current: ActiveCapture?
    }

    /// The open device and its stall-check token. Only the control queue reads or writes these (every access
    /// is in a block run on `controlQueue`, asserted with `dispatchPrecondition` where it matters), which is
    /// why the unchecked conformance holds.
    private final class DeviceSlot: @unchecked Sendable {
        var generation: UInt64 = 0
        var device: (any CaptureDevice)?
        /// Bumped by every configuration change and every close, so only the latest stall check can act.
        var stallCheckToken: UInt64 = 0
    }
}

/// The real microphone. A fresh `AVAudioEngine` for every capture, so each one reads the current device and
/// formats instead of a graph a configuration change left behind (Apple: after a change "the nodes remain
/// attached and connected with previously set formats").
final class AVAudioEngineCaptureDevice: CaptureDevice {
    private let deviceStore: AudioDeviceStore
    private var engine: AVAudioEngine?
    private var format: AVAudioFormat?
    private var configurationObserver: (any NSObjectProtocol)?
    private var tapInstalled = false

    init(deviceStore: AudioDeviceStore = .live) {
        self.deviceStore = deviceStore
    }

    func prepare() throws -> AVAudioFormat {
        let status = AVCaptureDevice.authorizationStatus(for: .audio)
        guard status == .authorized else {
            ScribeLog.warning(.audio, "Microphone capture is not authorized", .integer("status", status.rawValue))
            throw AudioCaptureEngineError.microphoneNotAuthorized(status)
        }

        let engine = AVAudioEngine()
        self.engine = engine
        let inputNode = engine.inputNode
        selectSavedMicrophone(on: inputNode)

        let format = inputNode.inputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else {
            throw AudioCaptureEngineError.missingInputNodeFormat
        }
        self.format = format
        return format
    }

    func start(
        deliver: @escaping @Sendable (AVAudioPCMBuffer) -> Void,
        configurationChanged: @escaping @Sendable () -> Void
    ) throws {
        guard let engine, let format else {
            throw AudioCaptureEngineError.missingInputNodeFormat
        }

        configurationObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil
        ) { _ in
            configurationChanged()
        }
        engine.inputNode.installTap(onBus: 0, bufferSize: 2_048, format: format) { buffer, _ in
            deliver(buffer)
        }
        tapInstalled = true

        do {
            try engine.start()
        } catch {
            throw AudioCaptureEngineError.engineStartFailed(underlying: error)
        }
    }

    var isRunning: Bool {
        engine?.isRunning ?? false
    }

    func close() {
        if let configurationObserver {
            NotificationCenter.default.removeObserver(configurationObserver)
            self.configurationObserver = nil
        }
        guard let engine else { return }
        engine.stop()
        if tapInstalled {
            engine.inputNode.removeTap(onBus: 0)
            tapInstalled = false
        }
        self.engine = nil
        format = nil
    }

    var currentInputDeviceID: AudioDeviceID? {
        guard let audioUnit = engine?.inputNode.audioUnit else { return nil }
        var deviceID = AudioDeviceID(0)
        var dataSize = UInt32(MemoryLayout<AudioDeviceID>.size)
        let status = AudioUnitGetProperty(
            audioUnit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &deviceID, &dataSize)
        return status == noErr ? deviceID : nil
    }

    /// Points the input unit at the microphone chosen in Settings, so the choice holds even when the system
    /// default input changes later. With no choice saved the unit follows the system default by itself, which
    /// is also how a Bluetooth microphone works once macOS makes it the default input.
    private func selectSavedMicrophone(on inputNode: AVAudioInputNode) {
        guard var deviceID = deviceStore.resolveSelectedDeviceID() else { return }
        guard let audioUnit = inputNode.audioUnit else {
            ScribeLog.warning(.audio, "Could not select the saved microphone: the input node has no audio unit yet")
            return
        }

        // AVAudioEngine initialized the unit against the system default when `inputNode` was first touched, and
        // kAudioOutputUnitProperty_CurrentDevice only takes effect on an uninitialized unit, so the change is
        // bracketed by an uninitialize and an initialize; without them the unit silently keeps its device.
        let uninitializeStatus = AudioUnitUninitialize(audioUnit)
        let setStatus = AudioUnitSetProperty(
            audioUnit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &deviceID,
            UInt32(MemoryLayout<AudioDeviceID>.size))
        let initializeStatus = AudioUnitInitialize(audioUnit)

        if uninitializeStatus == noErr, setStatus == noErr, initializeStatus == noErr {
            ScribeLog.info(.audio, "Selected the saved microphone")
        } else {
            ScribeLog.warning(
                .audio, "Could not select the saved microphone; using the system default input",
                .integer("uninitializeStatus", uninitializeStatus), .integer("setStatus", setStatus),
                .integer("initializeStatus", initializeStatus))
        }
    }
}
