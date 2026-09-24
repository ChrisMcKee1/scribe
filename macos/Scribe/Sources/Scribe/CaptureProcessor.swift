import AVFoundation
import Foundation
import os

/// One meter reading: the peak and RMS of the audio since the previous reading, as linear levels (0 to 1),
/// taken across every channel before the downmix, the way Windows meters.
struct AudioLevelMeasurement: Sendable, Equatable {
    let peakAmplitude: Float
    let rmsAmplitude: Float

    var peakDbfs: Float { Self.toDbfs(peakAmplitude) }
    var rmsDbfs: Float { Self.toDbfs(rmsAmplitude) }

    static func toDbfs(_ amplitude: Float) -> Float {
        guard amplitude > 0 else { return -99 }
        return max(-99, 20 * log10f(amplitude))
    }
}

/// What silence auto-stop saw when it ended a recording: levels, never audio.
struct SilenceStopDetail: Sendable, Equatable {
    /// False when the recording ended on the lead-in without ever hearing speech: a muted or wrong
    /// microphone, a gain far too low, or only steady noise. That reads to the user as an unexplained cut-off
    /// and needs the opposite fix from a speaker who simply finished.
    let heardSpeech: Bool
    let peakLevel: Float
    let noiseFloor: Float
    let voiceThreshold: Float
}

/// Why a recording ended by itself rather than by its owner's stop.
enum CaptureEndReason: Sendable, Equatable {
    /// Toggle silence auto-stop: the speaker went quiet, or never spoke within the lead-in.
    case silence(SilenceStopDetail)
    /// The recording reached its policy's maximum duration.
    case durationLimit
    /// The input device was reconfigured or removed and stopped delivering audio.
    case deviceChanged
    /// A buffer arrived in a format other than the one the recording opened with.
    case formatChanged
    /// Converting to 16 kHz failed.
    case conversionFailed

    /// The input failed, as opposed to the recording being over.
    var isFault: Bool {
        switch self {
        case .silence, .durationLimit:
            return false
        case .deviceChanged, .formatChanged, .conversionFailed:
            return true
        }
    }
}

/// Something a recording tells its owner, tagged with the recording, so an owner that has moved on to a later
/// recording can tell a late event from a current one.
struct CaptureEvent: Sendable, Equatable {
    enum Kind: Sendable, Equatable {
        /// A meter reading, at most one per buffer and one per 50 ms of captured audio.
        case level(AudioLevelMeasurement)
        /// The recording has ended by itself and keeps everything it captured until its owner calls
        /// `AudioCaptureEngine.stop(owner:)`. Posted once per recording.
        case stopRequested(CaptureEndReason)
    }

    let owner: RecordingID
    let kind: Kind
}

/// The shape of one finished capture. Numbers only; the audio travels separately in `CapturedAudio`.
struct AudioCaptureSummary: Sendable, Equatable {
    enum Ending: Sendable, Equatable {
        /// The owner stopped it while it was recording.
        case stoppedByOwner
        /// It had already ended by itself when the owner stopped it.
        case endedItself(CaptureEndReason)
    }

    /// When the device opened, or when the recording was admitted if it never opened.
    let startedAt: Date
    let stoppedAt: Date
    /// Samples at `sampleRate` (16 kHz mono).
    let sampleCount: Int
    let sampleRate: Double
    let ending: Ending
    /// The raw input's shape before the downmix, or `nil` when the device never opened.
    let signal: CaptureSignalReport?
    /// Tap buffers whose audio is in the capture.
    let acceptedBufferCount: Int
    /// Tap buffers that arrived after the recording ended, or in a format it could not use.
    let droppedBufferCount: Int
    /// What became of the resampler's last few milliseconds when the recording ended.
    let resamplerFlush: ResamplerFlush

    /// The resampler holds back a few milliseconds of converted audio until the stream ends, and the recording
    /// flushes them once when it ends.
    enum ResamplerFlush: Sendable, Equatable {
        /// The device ran at 16 kHz or never opened, so there was nothing to flush.
        case notNeeded
        case flushed
        /// Converting them failed: the capture keeps everything converted before the failure.
        case failed
    }

    var durationSeconds: Double {
        sampleRate > 0 ? Double(sampleCount) / sampleRate : 0
    }
}

/// A finished recording: 16 kHz mono samples for the recognizer and the summary of how it went.
struct CapturedAudio: Sendable {
    let owner: RecordingID
    let samples: [Float]
    let summary: AudioCaptureSummary
}

/// Everything one recording does with its audio between the device's tap and its owner's stop: per-channel
/// statistics taken before the downmix, the average of every channel (so a microphone on any input is heard),
/// resampling to 16 kHz mono, the meter, silence auto-stop and the duration ceiling.
///
/// It is the only owner of the recording's samples, counters and converter, all behind one lock, so the tap
/// thread and whichever thread stops the recording never touch them at the same time. Once the recording has
/// ended, by its owner's `retire()` or `finish()` or by itself, a buffer the tap delivers late is counted and
/// dropped, so the samples `finish()` returns are exactly those of the buffers accepted before the end, and
/// `finish()` returns them once. Silence and the ceiling are judged on capture time counted in samples, never on a
/// clock.
final class CaptureProcessor: Sendable {
    static let targetSampleRate: Double = 16_000

    /// Silence auto-stop is fed one peak per 10 ms of capture, whatever size the device's buffers are, which
    /// is the block the Windows tracker's margins were measured on.
    static let silenceBlockSeconds: Double = 0.010

    /// The meter posts at most once per 50 ms of captured audio.
    static let meterIntervalSeconds: Double = 0.050

    let owner: RecordingID
    let policy: CaptureStopPolicy

    /// Converts what the resampler still holds when a recording ends, appending it to `samples`, and reports
    /// whether that worked; after a failure `samples` keeps whatever was appended before it. Tests pass their
    /// own to make the flush fail, or to hold a stop inside it.
    typealias TailFlush =
        @Sendable (_ converter: AVAudioConverter, _ target: AVAudioFormat, _ samples: inout [Float]) -> Bool

    static let flushResamplerTail: TailFlush = { converter, target, samples in
        CaptureProcessor.convert(
            ConverterFeed(nil), with: converter, target: target, capacity: 4_096, into: &samples)
    }

    private let tailFlush: TailFlush

    // The state holds AVFoundation objects and the sample array. Only this lock touches them, and nothing
    // leaves a `withLockUnchecked` body except finished Sendable values (event kinds, counts and the captured
    // audio), which is why the lock is created with unchecked state.
    private let state: OSAllocatedUnfairLock<State>

    /// Set by `retire()`. A lock of its own, so the owner's stop never waits for the recording's lock, which the
    /// tap holds while it converts a buffer and `finish()` holds while it drains the resampler. `process` reads it
    /// inside the recording's lock; `retire()` never takes that lock.
    private let retirement = OSAllocatedUnfairLock(initialState: false)

    init(
        owner: RecordingID,
        policy: CaptureStopPolicy,
        createdAt: Date = Date(),
        tailFlush: @escaping TailFlush = CaptureProcessor.flushResamplerTail
    ) {
        self.owner = owner
        self.policy = policy
        self.tailFlush = tailFlush
        let tracker = policy.silence.map { SilenceAutoStopTracker(configuration: $0) }
        state = OSAllocatedUnfairLock(uncheckedState: State(createdAt: createdAt, tracker: tracker))
    }

    /// Prepares for buffers in `format`, the format the device will deliver. Throws
    /// `AudioCaptureEngineError.unsupportedInputFormat` for a format it cannot read and
    /// `.converterInitializationFailed` when no 16 kHz converter can be made.
    func configure(inputFormat format: AVAudioFormat) throws {
        let sampleRate = format.sampleRate
        let channelCount = Int(format.channelCount)
        guard sampleRate > 0, sampleRate.isFinite, channelCount > 0,
            CaptureProcessor.isReadable(format.commonFormat)
        else {
            throw AudioCaptureEngineError.unsupportedInputFormat
        }
        guard
            let monoFormat = AVAudioFormat(
                commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: 1, interleaved: false),
            let targetFormat = AVAudioFormat(
                commonFormat: .pcmFormatFloat32, sampleRate: CaptureProcessor.targetSampleRate, channels: 1,
                interleaved: false)
        else {
            throw AudioCaptureEngineError.converterInitializationFailed
        }

        var converter: AVAudioConverter?
        if sampleRate != CaptureProcessor.targetSampleRate {
            guard let made = AVAudioConverter(from: monoFormat, to: targetFormat) else {
                throw AudioCaptureEngineError.converterInitializationFailed
            }
            converter = made
        }

        var maximumFrames: Int64?
        if let maximumDuration = policy.maximumDuration {
            let (seconds, attoseconds) = maximumDuration.components
            let frames = ((Double(seconds) + Double(attoseconds) / 1e18) * sampleRate).rounded(.down)
            maximumFrames = Int64(min(frames, Double(Int64.max / 2)))
        }

        let input = Input(
            sampleRate: sampleRate,
            channelCount: channelCount,
            monoFormat: monoFormat,
            targetFormat: targetFormat,
            converter: converter,
            framesPerBlock: max(1, Int((sampleRate * CaptureProcessor.silenceBlockSeconds).rounded())),
            framesPerReading: max(1, Int((sampleRate * CaptureProcessor.meterIntervalSeconds).rounded())),
            maximumFrames: maximumFrames)

        state.withLockUnchecked { state in
            state.input = input
            state.signal = CaptureSignalAccumulator(channels: channelCount)
            state.samples.reserveCapacity(Int(CaptureProcessor.targetSampleRate) * 10)
        }
    }

    /// Records when the device started delivering, for the summary.
    func markOpened(at date: Date) {
        state.withLockUnchecked { $0.openedAt = date }
    }

    /// Takes one buffer from the tap and returns what to tell the owner: at most one meter reading, then the
    /// stop request when this buffer ended the recording. Called on the tap's thread; never blocks on anything
    /// but this recording's own lock.
    func process(_ buffer: AVAudioPCMBuffer) -> [CaptureEvent.Kind] {
        let flush = tailFlush
        let retirement = retirement
        return state.withLockUnchecked { state -> [CaptureEvent.Kind] in
            state.received += 1
            guard case .recording = state.phase, !retirement.withLock({ $0 }), let input = state.input else {
                state.dropped += 1
                return []
            }
            guard Int(buffer.format.channelCount) == input.channelCount,
                buffer.format.sampleRate == input.sampleRate,
                let reader = SampleReader(buffer)
            else {
                state.dropped += 1
                return CaptureProcessor.end(&state, .formatChanged, flush: flush)
            }

            state.accepted += 1
            guard reader.frameCount > 0 else { return [] }

            var frameCount = reader.frameCount
            var reason: CaptureEndReason?
            if let maximumFrames = input.maximumFrames, maximumFrames - state.framesProcessed <= Int64(frameCount) {
                frameCount = Int(max(0, maximumFrames - state.framesProcessed))
                reason = .durationLimit
            }

            guard
                let mono = AVAudioPCMBuffer(
                    pcmFormat: input.monoFormat, frameCapacity: AVAudioFrameCount(max(1, frameCount))),
                let monoSamples = mono.floatChannelData?[0]
            else {
                return CaptureProcessor.end(&state, .conversionFailed, flush: flush)
            }

            let channelScale = 1 / Float(input.channelCount)
            var processed = 0
            framesLoop: for frame in 0..<frameCount {
                var sum: Float = 0
                var framePeak: Float = 0
                var frameSquares: Double = 0
                for channel in 0..<input.channelCount {
                    let value = reader.sample(channel: channel, frame: frame)
                    state.signal.add(value, channel: channel)
                    sum += value
                    framePeak = max(framePeak, abs(value))
                    frameSquares += Double(value) * Double(value)
                }

                monoSamples[frame] = sum * channelScale
                processed = frame + 1
                state.framesProcessed += 1
                state.readingPeak = max(state.readingPeak, framePeak)
                state.readingSquares += frameSquares
                state.readingSamples += input.channelCount

                guard state.tracker != nil else { continue }
                state.blockPeak = max(state.blockPeak, framePeak)
                state.blockFrames += 1
                guard state.blockFrames == input.framesPerBlock else { continue }

                let timestamp = Int64((Double(state.framesProcessed) * 1_000 / input.sampleRate).rounded(.down))
                let fired = state.tracker?.update(level: state.blockPeak, atMilliseconds: timestamp) ?? false
                state.blockPeak = 0
                state.blockFrames = 0
                if fired, let tracker = state.tracker {
                    reason = .silence(
                        SilenceStopDetail(
                            heardSpeech: tracker.heardSpeech,
                            peakLevel: tracker.peakLevel,
                            noiseFloor: tracker.noiseFloor,
                            voiceThreshold: tracker.voiceThreshold))
                    break framesLoop
                }
            }

            state.signal.countFrames(processed)
            mono.frameLength = AVAudioFrameCount(processed)

            var events: [CaptureEvent.Kind] = []
            state.readingFrames += processed
            if state.readingFrames >= input.framesPerReading {
                let rms =
                    state.readingSamples > 0
                    ? Float((state.readingSquares / Double(state.readingSamples)).squareRoot()) : 0
                events.append(.level(AudioLevelMeasurement(peakAmplitude: state.readingPeak, rmsAmplitude: rms)))
                state.readingPeak = 0
                state.readingSquares = 0
                state.readingSamples = 0
                state.readingFrames = 0
            }

            if !CaptureProcessor.append(mono, input: input, to: &state.samples) {
                return events + CaptureProcessor.end(&state, .conversionFailed, flush: flush)
            }
            if let reason {
                events += CaptureProcessor.end(&state, reason, flush: flush)
            }
            return events
        }
    }

    /// The owner's stop, taken at once: every buffer the tap delivers from now on is counted and dropped, and a
    /// buffer already being converted is kept. Seals nothing and waits for nothing; `finish()` seals afterwards,
    /// on another thread (`AudioCaptureEngine.retire(owner:)`).
    func retire() {
        retirement.withLock { $0 = true }
    }

    /// Ends a recording still in progress, keeping what it captured. True only for the call that ended it.
    func end(_ reason: CaptureEndReason) -> Bool {
        let flush = tailFlush
        return state.withLockUnchecked { state -> Bool in
            !CaptureProcessor.end(&state, reason, flush: flush).isEmpty
        }
    }

    /// Ends the recording, unless it already ended by itself, and hands over everything it captured. Every
    /// call after the first returns `nil`.
    func finish(at stoppedAt: Date = Date()) -> CapturedAudio? {
        let flush = tailFlush
        return state.withLockUnchecked { state -> CapturedAudio? in
            let ending: AudioCaptureSummary.Ending
            switch state.phase {
            case .finished:
                return nil
            case .ended(let reason):
                ending = .endedItself(reason)
            case .recording:
                CaptureProcessor.drainResampler(&state, flush: flush)
                ending = .stoppedByOwner
            }

            state.phase = .finished
            let samples = state.samples
            state.samples = []
            var signal: CaptureSignalReport?
            if let input = state.input {
                signal = state.signal.report(sampleRate: input.sampleRate)
            }
            let summary = AudioCaptureSummary(
                startedAt: state.openedAt ?? state.createdAt,
                stoppedAt: stoppedAt,
                sampleCount: samples.count,
                sampleRate: CaptureProcessor.targetSampleRate,
                ending: ending,
                signal: signal,
                acceptedBufferCount: state.accepted,
                droppedBufferCount: state.dropped,
                resamplerFlush: state.resamplerFlush ?? .notNeeded)
            return CapturedAudio(owner: owner, samples: samples, summary: summary)
        }
    }

    struct BufferCounts: Sendable, Equatable {
        let received: Int
        let accepted: Int
        let dropped: Int
    }

    var bufferCounts: BufferCounts {
        state.withLockUnchecked { BufferCounts(received: $0.received, accepted: $0.accepted, dropped: $0.dropped) }
    }

    /// True once the recording has ended, by itself or by `finish()`.
    var hasEnded: Bool {
        state.withLockUnchecked { state -> Bool in
            if case .recording = state.phase {
                return false
            }
            return true
        }
    }

    // MARK: - Inside the lock

    private enum Phase {
        case recording
        case ended(CaptureEndReason)
        case finished
    }

    private struct Input {
        let sampleRate: Double
        let channelCount: Int
        let monoFormat: AVAudioFormat
        let targetFormat: AVAudioFormat
        /// `nil` when the device already runs at 16 kHz.
        let converter: AVAudioConverter?
        let framesPerBlock: Int
        let framesPerReading: Int
        let maximumFrames: Int64?
    }

    private struct State {
        var phase = Phase.recording
        var input: Input?
        var samples: [Float] = []
        var signal = CaptureSignalAccumulator(channels: 0)
        var tracker: SilenceAutoStopTracker?
        var framesProcessed: Int64 = 0
        var blockPeak: Float = 0
        var blockFrames = 0
        var readingPeak: Float = 0
        var readingSquares: Double = 0
        var readingSamples = 0
        var readingFrames = 0
        var received = 0
        var accepted = 0
        var dropped = 0
        let createdAt: Date
        var openedAt: Date?
        /// Unset until the recording ends and the resampler is flushed.
        var resamplerFlush: AudioCaptureSummary.ResamplerFlush?

        init(createdAt: Date, tracker: SilenceAutoStopTracker?) {
            self.createdAt = createdAt
            self.tracker = tracker
        }
    }

    /// Ends the recording with its one stop request. A failed flush of the resampler's tail is recorded for the
    /// summary, never reported as a second reason.
    private static func end(
        _ state: inout State, _ reason: CaptureEndReason, flush: TailFlush
    ) -> [CaptureEvent.Kind] {
        guard case .recording = state.phase else { return [] }
        drainResampler(&state, flush: flush)
        state.phase = .ended(reason)
        return [.stopRequested(reason)]
    }

    /// Flushes the resampler's filter tail once, so the last few milliseconds before the end are kept.
    private static func drainResampler(_ state: inout State, flush: TailFlush) {
        guard state.resamplerFlush == nil else { return }
        guard let input = state.input, let converter = input.converter else {
            state.resamplerFlush = .notNeeded
            return
        }
        state.resamplerFlush = flush(converter, input.targetFormat, &state.samples) ? .flushed : .failed
    }

    private static func append(_ mono: AVAudioPCMBuffer, input: Input, to samples: inout [Float]) -> Bool {
        let frames = Int(mono.frameLength)
        guard frames > 0 else { return true }
        guard let converter = input.converter else {
            guard let data = mono.floatChannelData?[0] else { return false }
            samples.append(contentsOf: UnsafeBufferPointer(start: data, count: frames))
            return true
        }
        let capacity = AVAudioFrameCount(Double(frames) * CaptureProcessor.targetSampleRate / input.sampleRate) + 64
        return convert(
            ConverterFeed(mono), with: converter, target: input.targetFormat, capacity: capacity, into: &samples)
    }

    /// Runs `converter` until it has returned everything `feed` gives it. The block-based `convert` is the one
    /// Apple documents for streaming sample-rate conversion; the simple `convert(to:from:)` insists that the
    /// output hold as many frames as the input even when downsampling, and raises an Objective-C exception,
    /// which ends the process, when it does not.
    private static func convert(
        _ feed: ConverterFeed,
        with converter: AVAudioConverter,
        target: AVAudioFormat,
        capacity: AVAudioFrameCount,
        into samples: inout [Float]
    ) -> Bool {
        while true {
            guard let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else { return false }
            var conversionError: NSError?
            let status = converter.convert(to: output, error: &conversionError) { _, inputStatus in
                feed.next(inputStatus)
            }
            if status == .error || conversionError != nil {
                return false
            }
            let produced = Int(output.frameLength)
            if produced > 0, let data = output.floatChannelData?[0] {
                samples.append(contentsOf: UnsafeBufferPointer(start: data, count: produced))
            }
            // A full output buffer may leave converted audio behind; anything else means it gave all it had.
            if status != .haveData || produced == 0 {
                return true
            }
        }
    }

    private static func isReadable(_ format: AVAudioCommonFormat) -> Bool {
        format == .pcmFormatFloat32 || format == .pcmFormatInt16 || format == .pcmFormatInt32
    }

    /// Reads any channel and frame of a buffer as a float in -1...1, whatever its sample format and layout.
    /// Only valid while the buffer it was made from is alive.
    private struct SampleReader {
        private enum Storage {
            case float32(UnsafePointer<UnsafeMutablePointer<Float>>)
            case int16(UnsafePointer<UnsafeMutablePointer<Int16>>)
            case int32(UnsafePointer<UnsafeMutablePointer<Int32>>)
        }

        private let storage: Storage
        private let interleaved: Bool
        private let stride: Int
        let frameCount: Int

        init?(_ buffer: AVAudioPCMBuffer) {
            switch buffer.format.commonFormat {
            case .pcmFormatFloat32:
                guard let data = buffer.floatChannelData else { return nil }
                storage = .float32(data)
            case .pcmFormatInt16:
                guard let data = buffer.int16ChannelData else { return nil }
                storage = .int16(data)
            case .pcmFormatInt32:
                guard let data = buffer.int32ChannelData else { return nil }
                storage = .int32(data)
            default:
                return nil
            }
            interleaved = buffer.format.isInterleaved
            stride = max(1, buffer.stride)
            frameCount = Int(buffer.frameLength)
        }

        func sample(channel: Int, frame: Int) -> Float {
            let row = interleaved ? 0 : channel
            let index = interleaved ? frame * stride + channel : frame
            switch storage {
            case .float32(let data):
                return data[row][index]
            case .int16(let data):
                return Float(data[row][index]) / 32_768
            case .int32(let data):
                return Float(data[row][index]) / 2_147_483_648
            }
        }
    }
}

/// Carries one input buffer, or the end of the stream, into AVAudioConverter's input block. The block's type
/// is `@Sendable`, but `convert(to:error:withInputFrom:)` calls it synchronously on the calling thread before
/// it returns, and a feed is used only within the one conversion it was made for, so it is never shared
/// between threads.
private final class ConverterFeed: @unchecked Sendable {
    private let buffer: AVAudioPCMBuffer?
    private var delivered = false

    init(_ buffer: AVAudioPCMBuffer?) {
        self.buffer = buffer
    }

    func next(_ status: UnsafeMutablePointer<AVAudioConverterInputStatus>) -> AVAudioBuffer? {
        guard let buffer else {
            status.pointee = .endOfStream
            return nil
        }
        guard !delivered else {
            status.pointee = .noDataNow
            return nil
        }
        delivered = true
        status.pointee = .haveData
        return buffer
    }
}
