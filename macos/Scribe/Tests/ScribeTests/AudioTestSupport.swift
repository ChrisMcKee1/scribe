import AVFoundation
import Foundation
import XCTest
import os

@testable import Scribe

// Test doubles and signal generators for the capture, silence and transcription tests. Every generated signal
// is seeded, so each scenario is the same on every run and every machine.

/// SplitMix64: small, fast and fully determined by its seed.
struct AudioTestRandom {
    private var state: UInt64

    init(seed: UInt64) {
        state = seed
    }

    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var value = state
        value = (value ^ (value >> 30)) &* 0xBF58_476D_1CE4_E5B9
        value = (value ^ (value >> 27)) &* 0x94D0_49BB_1331_11EB
        return value ^ (value >> 31)
    }

    /// Uniform in [0, 1).
    mutating func unit() -> Double {
        Double(next() >> 11) / Double(1 << 53)
    }

    /// Standard normal, by Box-Muller.
    mutating func gaussian() -> Double {
        let radius = (-2 * log(1 - unit())).squareRoot()
        return radius * cos(2 * Double.pi * unit())
    }
}

/// Signals shaped like the ones the Windows tracker tests use, at 48 kHz unless told otherwise.
enum AudioTestSignal {
    static let sampleRate = 48_000

    /// Pink noise (Paul Kellet's filter over seeded Gaussian white noise) at an RMS level in dBFS.
    static func pink(seconds: Double, rmsDbfs: Double, seed: UInt64, sampleRate: Int = sampleRate) -> [Float] {
        var random = AudioTestRandom(seed: seed)
        var b0 = 0.0, b1 = 0.0, b2 = 0.0, b3 = 0.0, b4 = 0.0, b5 = 0.0, b6 = 0.0
        var samples = [Float](repeating: 0, count: Int(seconds * Double(sampleRate)))
        for index in samples.indices {
            let white = random.gaussian()
            b0 = 0.99886 * b0 + white * 0.0555179
            b1 = 0.99332 * b1 + white * 0.0750759
            b2 = 0.96900 * b2 + white * 0.1538520
            b3 = 0.86650 * b3 + white * 0.3104856
            b4 = 0.55000 * b4 + white * 0.5329522
            b5 = -0.7616 * b5 - white * 0.0168980
            samples[index] = Float(b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362)
            b6 = white * 0.115926
        }
        return scaled(samples, toRmsDbfs: rmsDbfs)
    }

    /// Speech-shaped signal: voiced syllables of 120 to 260 ms on a 110 to 180 Hz harmonic complex, each rising
    /// and decaying, 12 dB of spread between syllables, short gaps inside words and longer ones between them,
    /// scaled so its loudest sample sits at `peakDbfs`.
    static func syntheticSpeech(seconds: Double, peakDbfs: Double, seed: UInt64, sampleRate: Int = sampleRate)
        -> [Float]
    {
        var random = AudioTestRandom(seed: seed)
        let rate = Double(sampleRate)
        var samples = [Float](repeating: 0, count: Int(seconds * rate))
        var position = 0
        var syllable = 0
        while position < samples.count {
            let length = Int(rate * (0.12 + random.unit() * 0.14))
            let amplitude = pow(10, (random.unit() - 0.5) * 12 / 20)
            let pitch = 110 + random.unit() * 70
            var offset = 0
            while offset < length, position + offset < samples.count {
                let phase = 2 * Double.pi * pitch * Double(position + offset) / rate
                let voice = sin(phase) + 0.5 * sin(2 * phase) + 0.25 * sin(3 * phase)
                samples[position + offset] = Float(amplitude * sin(Double.pi * Double(offset) / Double(length)) * voice)
                offset += 1
            }
            position += length
            syllable += 1
            let wordEnds = syllable % 3 == 0
            position += Int(rate * (wordEnds ? 0.15 + random.unit() * 0.2 : 0.03 + random.unit() * 0.06))
        }
        let loudest = samples.map { abs($0) }.max() ?? 1
        let gain = Float(pow(10, peakDbfs / 20)) / max(loudest, .leastNonzeroMagnitude)
        return samples.map { $0 * gain }
    }

    /// A gain that moves linearly in dB from one level to another over `overSeconds`, then holds.
    static func ramp(
        _ samples: [Float], fromDbfs: Double, toDbfs: Double, overSeconds: Double, sampleRate: Int = sampleRate
    ) -> [Float] {
        samples.enumerated().map { index, sample in
            let progress = min(1, Double(index) / (overSeconds * Double(sampleRate)))
            return sample * Float(pow(10, (fromDbfs + (toDbfs - fromDbfs) * progress) / 20))
        }
    }

    /// `overlay` added onto `bed` from `startSeconds`.
    static func mix(_ bed: [Float], _ overlay: [Float], startSeconds: Double, sampleRate: Int = sampleRate) -> [Float] {
        var mixed = bed
        let offset = Int(startSeconds * Double(sampleRate))
        var index = 0
        while index < overlay.count, offset + index < mixed.count {
            mixed[offset + index] += overlay[index]
            index += 1
        }
        return mixed
    }

    static func tone(seconds: Double, frequency: Double, amplitude: Float, sampleRate: Int) -> [Float] {
        (0..<Int(seconds * Double(sampleRate))).map { index in
            amplitude * Float(sin(2 * Double.pi * frequency * Double(index) / Double(sampleRate)))
        }
    }

    static func rms(_ samples: ArraySlice<Float>) -> Float {
        guard !samples.isEmpty else { return 0 }
        let squares = samples.reduce(0.0) { $0 + Double($1) * Double($1) }
        return Float((squares / Double(samples.count)).squareRoot())
    }

    private static func scaled(_ samples: [Float], toRmsDbfs rmsDbfs: Double) -> [Float] {
        let rms = Double(self.rms(samples[...]))
        guard rms > 0 else { return samples }
        let gain = Float(pow(10, rmsDbfs / 20) / rms)
        return samples.map { $0 * gain }
    }
}

/// Builds `AVAudioPCMBuffer`s from per-channel samples, in any layout the capture has to read.
enum AudioTestBuffers {
    static func format(
        sampleRate: Double,
        channels: Int,
        commonFormat: AVAudioCommonFormat = .pcmFormatFloat32,
        interleaved: Bool = false
    ) -> AVAudioFormat {
        if channels <= 2,
            let format = AVAudioFormat(
                commonFormat: commonFormat, sampleRate: sampleRate, channels: AVAudioChannelCount(channels),
                interleaved: interleaved)
        {
            return format
        }
        let layout = AVAudioChannelLayout(layoutTag: kAudioChannelLayoutTag_DiscreteInOrder | UInt32(channels))!
        return AVAudioFormat(
            commonFormat: commonFormat, sampleRate: sampleRate, interleaved: interleaved, channelLayout: layout)
    }

    /// One buffer holding `channels[c][frame]` for every channel, all of the same length.
    static func make(
        sampleRate: Double,
        channels: [[Float]],
        commonFormat: AVAudioCommonFormat = .pcmFormatFloat32,
        interleaved: Bool = false
    ) -> AVAudioPCMBuffer {
        let frames = channels.first?.count ?? 0
        let format = format(
            sampleRate: sampleRate, channels: channels.count, commonFormat: commonFormat, interleaved: interleaved)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(max(1, frames)))!
        buffer.frameLength = AVAudioFrameCount(frames)
        let stride = channels.count
        for (channel, samples) in channels.enumerated() {
            for (frame, value) in samples.enumerated() {
                let row = interleaved ? 0 : channel
                let index = interleaved ? frame * stride + channel : frame
                switch commonFormat {
                case .pcmFormatInt16:
                    buffer.int16ChannelData![row][index] = Int16(clamping: Int((value * 32_767).rounded()))
                case .pcmFormatInt32:
                    let scaled = Int64((Double(value) * 2_147_483_647).rounded())
                    buffer.int32ChannelData![row][index] = Int32(clamping: scaled)
                default:
                    buffer.floatChannelData![row][index] = value
                }
            }
        }
        return buffer
    }

    /// A mono buffer of `samples`.
    static func mono(_ samples: [Float], sampleRate: Double) -> AVAudioPCMBuffer {
        make(sampleRate: sampleRate, channels: [samples])
    }
}

/// Feeds `samples` (mono) through `processor` in buffers of `framesPerBuffer`, returning every event and the
/// capture time in milliseconds at which the first stop request came.
func feedMono(
    _ samples: [Float],
    sampleRate: Double,
    framesPerBuffer: Int,
    to processor: CaptureProcessor
) -> (events: [CaptureEvent.Kind], stoppedAtMilliseconds: Int?) {
    var events: [CaptureEvent.Kind] = []
    var stoppedAt: Int?
    var start = 0
    while start < samples.count {
        let end = min(samples.count, start + framesPerBuffer)
        let kinds = processor.process(AudioTestBuffers.mono(Array(samples[start..<end]), sampleRate: sampleRate))
        events += kinds
        let requestedStop = kinds.contains { kind in
            if case .stopRequested = kind {
                return true
            }
            return false
        }
        if stoppedAt == nil, requestedStop {
            stoppedAt = Int(Double(end) * 1_000 / sampleRate)
        }
        start = end
    }
    return (events, stoppedAt)
}

/// Collects a recording's events on whatever thread posts them.
final class CaptureEventLog: Sendable {
    private let events = OSAllocatedUnfairLock<[CaptureEvent]>(initialState: [])

    var sink: @Sendable (CaptureEvent) -> Void {
        { [events] event in
            events.withLock { $0.append(event) }
        }
    }

    var all: [CaptureEvent] {
        events.withLock { $0 }
    }

    var stopRequests: [CaptureEndReason] {
        all.compactMap { event in
            if case .stopRequested(let reason) = event.kind {
                return reason
            }
            return nil
        }
    }

    var levels: [AudioLevelMeasurement] {
        all.compactMap { event in
            if case .level(let level) = event.kind {
                return level
            }
            return nil
        }
    }
}

/// Something a test waits for without sleeping to order anything: `signal()` from any thread wakes every
/// waiter, and `wait(timeout:)` returns false only when the timeout passes first.
final class AudioTestSignalLatch: Sendable {
    private struct State: Sendable {
        var signalled = false
        var nextID: UInt64 = 0
        var waiters: [UInt64: CheckedContinuation<Bool, Never>] = [:]
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    func signal() {
        let waiters = state.withLock { state -> [CheckedContinuation<Bool, Never>] in
            state.signalled = true
            let waiting = Array(state.waiters.values)
            state.waiters = [:]
            return waiting
        }
        for waiter in waiters {
            waiter.resume(returning: true)
        }
    }

    var isSignalled: Bool {
        state.withLock { $0.signalled }
    }

    func wait(timeout: Duration = .seconds(30)) async -> Bool {
        let state = state
        return await withCheckedContinuation { continuation in
            let id = state.withLock { state -> UInt64? in
                guard !state.signalled else { return nil }
                state.nextID += 1
                state.waiters[state.nextID] = continuation
                return state.nextID
            }
            guard let id else {
                continuation.resume(returning: true)
                return
            }
            Task.detached {
                try? await Task.sleep(for: timeout)
                let expired = state.withLock { $0.waiters.removeValue(forKey: id) }
                expired?.resume(returning: false)
            }
        }
    }
}

/// A capture device that delivers only what a test hands it, from whatever thread the test chooses.
final class CaptureTestDevice: CaptureDevice, Sendable {
    struct Configuration: Sendable {
        var sampleRate: Double = 16_000
        var channels = 1
        var commonFormat: AVAudioCommonFormat = .pcmFormatFloat32
        var interleaved = false
        var prepareError: (any Error)?
        var startError: (any Error)?
        /// When set, `prepare` announces itself on `prepareEntered` and waits for `releasePrepare()`, 30 seconds at
        /// most, so a test that fails before releasing it cannot hold the engine's control queue for good.
        var holdsPrepare = false
    }

    private struct State: Sendable {
        var deliver: (@Sendable (AVAudioPCMBuffer) -> Void)?
        var configurationChanged: (@Sendable () -> Void)?
        var prepared = 0
        var started = 0
        var closed = 0
        var running = false
    }

    let configuration: Configuration
    let prepareEntered = AudioTestSignalLatch()
    private let prepareRelease = DispatchSemaphore(value: 0)
    private let state = OSAllocatedUnfairLock(initialState: State())

    init(_ configuration: Configuration = Configuration()) {
        self.configuration = configuration
    }

    func prepare() throws -> AVAudioFormat {
        state.withLock { $0.prepared += 1 }
        if configuration.holdsPrepare {
            prepareEntered.signal()
            _ = prepareRelease.wait(timeout: .now() + .seconds(30))
        }
        if let error = configuration.prepareError {
            throw error
        }
        return AudioTestBuffers.format(
            sampleRate: configuration.sampleRate, channels: configuration.channels,
            commonFormat: configuration.commonFormat, interleaved: configuration.interleaved)
    }

    func releasePrepare() {
        prepareRelease.signal()
    }

    func start(
        deliver: @escaping @Sendable (AVAudioPCMBuffer) -> Void,
        configurationChanged: @escaping @Sendable () -> Void
    ) throws {
        if let error = configuration.startError {
            throw error
        }
        state.withLock { state in
            state.deliver = deliver
            state.configurationChanged = configurationChanged
            state.started += 1
            state.running = true
        }
    }

    var isRunning: Bool {
        state.withLock { $0.running }
    }

    /// Keeps the tap closure, like AVAudioEngine can for a callback already under way, so a test can deliver a
    /// buffer after the close.
    func close() {
        state.withLock { state in
            state.closed += 1
            state.running = false
        }
    }

    var currentInputDeviceID: AudioDeviceID? { 42 }

    var counts: (prepared: Int, started: Int, closed: Int) {
        state.withLock { (prepared: $0.prepared, started: $0.started, closed: $0.closed) }
    }

    /// Delivers `buffer` on the calling thread, as the tap would. False when the device was never started.
    @discardableResult
    func deliver(_ buffer: AVAudioPCMBuffer) -> Bool {
        guard let deliver = state.withLock({ $0.deliver }) else { return false }
        deliver(buffer)
        return true
    }

    /// Calls the configuration-change observer on the calling thread, as Apple's notification queue would,
    /// after stopping the device when `stopsDevice` says the change interrupted it.
    func changeConfiguration(stopsDevice: Bool) {
        let observer = state.withLock { state -> (@Sendable () -> Void)? in
            if stopsDevice {
                state.running = false
            }
            return state.configurationChanged
        }
        observer?()
    }
}

/// Collects the stall checks an engine schedules, so a test runs each one when it chooses.
final class CaptureTestScheduler: Sendable {
    private let pending = OSAllocatedUnfairLock<[@Sendable () -> Void]>(initialState: [])

    var schedule: AudioCaptureEngine.Scheduler {
        { [pending] _, work in
            pending.withLock { $0.append(work) }
        }
    }

    var count: Int {
        pending.withLock { $0.count }
    }

    /// Runs every scheduled check now; each hands itself to the engine's control queue.
    func runAll() {
        let work = pending.withLock { pending -> [@Sendable () -> Void] in
            defer { pending = [] }
            return pending
        }
        for item in work {
            item()
        }
    }
}
