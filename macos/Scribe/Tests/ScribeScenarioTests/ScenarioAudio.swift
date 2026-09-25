import AVFoundation
import Accelerate
import Foundation

@testable import Scribe

/// SplitMix64, so every generated signal is the same on every run and every machine.
struct ScenarioRandom {
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
        Double(next() >> 11) / Double(UInt64(1) << 53)
    }

    /// Standard normal, by Box-Muller.
    mutating func gaussian() -> Double {
        let radius = (-2 * log(1 - unit())).squareRoot()
        return radius * cos(2 * Double.pi * unit())
    }

    mutating func integer(in range: ClosedRange<Int>) -> Int {
        range.lowerBound + Int(next() % UInt64(range.count))
    }
}

enum ScenarioAudioError: Error {
    case converterUnavailable
    case conversionFailed
}

/// Signal building and measuring for the scenarios. Every generator is seeded.
enum ScenarioAudio {
    static func amplitude(dbfs: Double) -> Float {
        Float(pow(10, dbfs / 20))
    }

    static func dbfs(_ amplitude: Double) -> Double {
        amplitude > 0 ? 20 * log10(amplitude) : -180
    }

    static func peak(_ samples: [Float]) -> Float {
        samples.reduce(0) { max($0, abs($1)) }
    }

    static func rms(_ samples: [Float]) -> Double {
        guard !samples.isEmpty else { return 0 }
        let squares = samples.reduce(0.0) { $0 + Double($1) * Double($1) }
        return (squares / Double(samples.count)).squareRoot()
    }

    /// Gaussian white noise at an RMS level, the analogue floor every live microphone has.
    static func white(count: Int, rmsDbfs: Double, seed: UInt64) -> [Float] {
        var random = ScenarioRandom(seed: seed)
        let sigma = Double(amplitude(dbfs: rmsDbfs))
        var samples = [Float](repeating: 0, count: count)
        for index in samples.indices {
            samples[index] = Float(random.gaussian() * sigma)
        }
        return samples
    }

    /// Pink noise, the spectrum of a fan or an air conditioner (Paul Kellet's filter over seeded white noise), scaled
    /// to an RMS level.
    static func pink(count: Int, rmsDbfs: Double, seed: UInt64) -> [Float] {
        var random = ScenarioRandom(seed: seed)
        var b0 = 0.0
        var b1 = 0.0
        var b2 = 0.0
        var b3 = 0.0
        var b4 = 0.0
        var b5 = 0.0
        var b6 = 0.0
        var samples = [Float](repeating: 0, count: count)
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
        let level = rms(samples)
        guard level > 0 else { return samples }
        let gain = Float(Double(amplitude(dbfs: rmsDbfs)) / level)
        return samples.map { $0 * gain }
    }

    static func scaled(_ samples: [Float], by gain: Float) -> [Float] {
        samples.map { $0 * gain }
    }

    /// `a` and `b` added sample by sample, over the longer of the two.
    static func added(_ a: [Float], _ b: [Float]) -> [Float] {
        var sum = [Float](repeating: 0, count: max(a.count, b.count))
        for index in a.indices {
            sum[index] += a[index]
        }
        for index in b.indices {
            sum[index] += b[index]
        }
        return sum
    }

    /// `samples` at `targetRate`, converted in one pass with AVAudioConverter at its highest quality: how the scenarios
    /// make a 44.1 or 48 kHz device out of a 16 kHz fixture. The capture converts back with its own converter.
    static func resampled(_ samples: [Float], from sourceRate: Double, to targetRate: Double) throws -> [Float] {
        guard sourceRate != targetRate else { return samples }
        guard
            let source = AVAudioFormat(
                commonFormat: .pcmFormatFloat32, sampleRate: sourceRate, channels: 1, interleaved: false),
            let target = AVAudioFormat(
                commonFormat: .pcmFormatFloat32, sampleRate: targetRate, channels: 1, interleaved: false),
            let converter = AVAudioConverter(from: source, to: target),
            let input = AVAudioPCMBuffer(pcmFormat: source, frameCapacity: AVAudioFrameCount(max(1, samples.count))),
            let inputData = input.floatChannelData
        else {
            throw ScenarioAudioError.converterUnavailable
        }
        converter.sampleRateConverterQuality = AVAudioQuality.max.rawValue
        input.frameLength = AVAudioFrameCount(samples.count)
        for (index, sample) in samples.enumerated() {
            inputData[0][index] = sample
        }

        let feed = ScenarioConverterFeed(input)
        var output: [Float] = []
        output.reserveCapacity(Int(Double(samples.count) * targetRate / sourceRate) + 1_024)
        while true {
            guard let chunk = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: 8_192) else {
                throw ScenarioAudioError.converterUnavailable
            }
            var failure: NSError?
            let status = converter.convert(to: chunk, error: &failure) { _, inputStatus in
                feed.next(inputStatus)
            }
            guard status != .error, failure == nil else {
                throw ScenarioAudioError.conversionFailed
            }
            if chunk.frameLength > 0, let data = chunk.floatChannelData {
                output.append(contentsOf: UnsafeBufferPointer(start: data[0], count: Int(chunk.frameLength)))
            }
            if status == .endOfStream {
                return output
            }
        }
    }

    /// Buffer sizes a device might deliver: seeded sizes within `range` that add up to exactly `total` frames.
    static func frameCounts(total: Int, seed: UInt64, within range: ClosedRange<Int>) -> [Int] {
        var random = ScenarioRandom(seed: seed)
        var counts: [Int] = []
        var remaining = total
        while remaining > 0 {
            let next = min(remaining, random.integer(in: range))
            counts.append(next)
            remaining -= next
        }
        return counts
    }

    /// How closely `candidate` follows `reference`: the best normalized cross-correlation over lags of up to `maxLag`
    /// samples, and the lag that gives it. 1 means the same waveform at some gain; the gain itself does not count.
    static func similarity(of candidate: [Float], to reference: [Float], maxLag: Int) -> (
        correlation: Double, lag: Int
    ) {
        var best: (correlation: Double, lag: Int) = (-1, 0)
        candidate.withUnsafeBufferPointer { candidateBuffer in
            reference.withUnsafeBufferPointer { referenceBuffer in
                guard let candidateBase = candidateBuffer.baseAddress, let referenceBase = referenceBuffer.baseAddress
                else {
                    return
                }
                for lag in -maxLag...maxLag {
                    // candidate[index + lag] is compared with reference[index].
                    let start = max(0, -lag)
                    let end = min(referenceBuffer.count, candidateBuffer.count - lag)
                    guard end > start else { continue }
                    let length = vDSP_Length(end - start)
                    var product: Float = 0
                    var referenceEnergy: Float = 0
                    var candidateEnergy: Float = 0
                    vDSP_dotpr(referenceBase + start, 1, candidateBase + start + lag, 1, &product, length)
                    vDSP_svesq(referenceBase + start, 1, &referenceEnergy, length)
                    vDSP_svesq(candidateBase + start + lag, 1, &candidateEnergy, length)
                    guard referenceEnergy > 0, candidateEnergy > 0 else { continue }
                    let correlation = Double(product) / (Double(referenceEnergy) * Double(candidateEnergy)).squareRoot()
                    if correlation > best.correlation {
                        best = (correlation, lag)
                    }
                }
            }
        }
        return best
    }

    /// The peak of every `framesPerBlock` frames across all channels: the level silence auto-stop is fed.
    static func blockPeaks(of channels: [[Float]], framesPerBlock: Int) -> [Float] {
        let frames = channels.first?.count ?? 0
        var peaks: [Float] = []
        peaks.reserveCapacity(frames / max(1, framesPerBlock))
        var block: Float = 0
        var framesInBlock = 0
        for frame in 0..<frames {
            for channel in channels.indices {
                block = max(block, abs(channels[channel][frame]))
            }
            framesInBlock += 1
            if framesInBlock == framesPerBlock {
                peaks.append(block)
                block = 0
                framesInBlock = 0
            }
        }
        return peaks
    }
}

/// Carries the one input buffer of `ScenarioAudio.resampled` into AVAudioConverter's input block, then the end of the
/// stream. The block's type is `@Sendable`, but `convert(to:error:withInputFrom:)` calls it synchronously on the
/// calling thread before it returns, and a feed serves only the one conversion it was made for, so it is never shared
/// between threads.
private final class ScenarioConverterFeed: @unchecked Sendable {
    private let buffer: AVAudioPCMBuffer
    private var delivered = false

    init(_ buffer: AVAudioPCMBuffer) {
        self.buffer = buffer
    }

    func next(_ status: UnsafeMutablePointer<AVAudioConverterInputStatus>) -> AVAudioBuffer? {
        guard !delivered else {
            status.pointee = .endOfStream
            return nil
        }
        delivered = true
        status.pointee = .haveData
        return buffer
    }
}

/// Which input channels carry the voice, and what the others carry.
enum ScenarioChannelLayout: Sendable, CustomStringConvertible {
    case mono
    /// Both channels carry the voice.
    case stereoBoth
    /// Only channel `voice` (0 is the left) carries it; the other holds exact digital silence, or a -60 dBFS floor.
    case stereoOne(voice: Int, otherHasFloor: Bool)

    var channelCount: Int {
        if case .mono = self {
            return 1
        }
        return 2
    }

    /// The voice's level once Scribe averages the channels.
    var downmixGain: Float {
        if case .stereoOne = self {
            return 0.5
        }
        return 1
    }

    var description: String {
        switch self {
        case .mono:
            return "mono"
        case .stereoBoth:
            return "stereo-both"
        case .stereoOne(let voice, let otherHasFloor):
            return "stereo-voice-\(voice == 0 ? "left" : "right")-other-\(otherHasFloor ? "floor" : "silent")"
        }
    }
}

/// A device's view of a scenario: what each input channel carries at the device's rate, and how the device encodes it.
struct ScenarioDeviceAudio: Sendable {
    enum Encoding: String, Sendable {
        case float32
        case int16Interleaved
    }

    let sampleRate: Double
    let encoding: Encoding
    /// One array per channel, all the same length, in -1...1.
    let channels: [[Float]]

    var frameCount: Int { channels.first?.count ?? 0 }

    /// The format the device reports; nil for a device with no channels or no rate, which cannot open.
    func format() -> AVAudioFormat? {
        guard sampleRate > 0, !channels.isEmpty else { return nil }
        switch encoding {
        case .float32:
            return AVAudioFormat(
                commonFormat: .pcmFormatFloat32, sampleRate: sampleRate,
                channels: AVAudioChannelCount(channels.count), interleaved: false)
        case .int16Interleaved:
            return AVAudioFormat(
                commonFormat: .pcmFormatInt16, sampleRate: sampleRate,
                channels: AVAudioChannelCount(channels.count), interleaved: true)
        }
    }

    /// One buffer holding `frames`, as the device's tap would hand it over.
    func buffer(frames: Range<Int>) -> AVAudioPCMBuffer? {
        guard let format = format(),
            let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(max(1, frames.count))),
            fill(buffer, with: frames)
        else {
            return nil
        }
        return buffer
    }

    /// Writes `frames` into `buffer`, which has this device's format, from its first frame on, and sets its length to
    /// them. False when the buffer cannot hold them.
    func fill(_ buffer: AVAudioPCMBuffer, with frames: Range<Int>) -> Bool {
        guard frames.count <= Int(buffer.frameCapacity) else {
            return false
        }
        buffer.frameLength = AVAudioFrameCount(frames.count)
        switch encoding {
        case .float32:
            guard let data = buffer.floatChannelData else { return false }
            for (channel, samples) in channels.enumerated() {
                for (offset, frame) in frames.enumerated() {
                    data[channel][offset] = samples[frame]
                }
            }
        case .int16Interleaved:
            guard let data = buffer.int16ChannelData else { return false }
            let stride = channels.count
            for (channel, samples) in channels.enumerated() {
                for (offset, frame) in frames.enumerated() {
                    data[0][offset * stride + channel] = Self.int16(samples[frame])
                }
            }
        }
        return true
    }

    /// The 16-bit value a device would store for `value`. Exact for a fixture's own samples, which are 16-bit values
    /// divided by 32,768.
    static func int16(_ value: Float) -> Int16 {
        Int16(clamping: Int((Double(value) * 32_768).rounded()))
    }

    /// `voice` (16 kHz mono) as a device at `sampleRate` delivers it on `layout`.
    static func device(
        playing voice: [Float], at sampleRate: Double, layout: ScenarioChannelLayout, encoding: Encoding, seed: UInt64
    ) throws -> ScenarioDeviceAudio {
        let played = try ScenarioAudio.resampled(voice, from: Double(ScenarioLibrary.fixtureRate), to: sampleRate)
        let channels: [[Float]]
        switch layout {
        case .mono:
            channels = [played]
        case .stereoBoth:
            channels = [played, played]
        case .stereoOne(let voiceChannel, let otherHasFloor):
            let other =
                otherHasFloor
                ? ScenarioAudio.white(count: played.count, rmsDbfs: -60, seed: seed)
                : [Float](repeating: 0, count: played.count)
            channels = voiceChannel == 0 ? [played, other] : [other, played]
        }
        return ScenarioDeviceAudio(sampleRate: sampleRate, encoding: encoding, channels: channels)
    }
}

/// What silence auto-stop decides for a signal, worked out without the capture: the production tracker fed the same
/// 10 ms block peaks, at the same capture timestamps, that `CaptureProcessor` feeds it. The capture must agree with it
/// exactly, whatever the size of the buffers it was given.
struct ScenarioSilenceOracle {
    /// The capture time at which the recording stops, or nil when it never does.
    let stopMilliseconds: Int64?
    let heardSpeech: Bool
    /// The last block that counted as voice, by the tracker's own rule, or nil when none did.
    let lastVoicedMilliseconds: Int64?
    let peakLevel: Float
    let noiseFloor: Float
    let voiceThreshold: Float

    /// The tracker's calibration window (`SilenceAutoStopTracker`), during which no block counts as voice.
    static let calibrationMilliseconds: Int64 = 300

    init(channels: [[Float]], sampleRate: Double, configuration: SilenceAutoStopConfiguration = .standard) {
        let framesPerBlock = max(1, Int((sampleRate * CaptureProcessor.silenceBlockSeconds).rounded()))
        var tracker = SilenceAutoStopTracker(configuration: configuration)
        var stop: Int64?
        var lastVoiced: Int64?
        for (index, level) in ScenarioAudio.blockPeaks(of: channels, framesPerBlock: framesPerBlock).enumerated() {
            let frames = Double((index + 1) * framesPerBlock)
            let timestamp = Int64((frames * 1_000 / sampleRate).rounded(.down))
            // Judged against the threshold as it stands before this block, as the tracker judges it.
            if timestamp >= Self.calibrationMilliseconds, level > tracker.voiceThreshold {
                lastVoiced = timestamp
            }
            if tracker.update(level: level, atMilliseconds: timestamp) {
                stop = timestamp
                break
            }
        }
        stopMilliseconds = stop
        heardSpeech = tracker.heardSpeech
        lastVoicedMilliseconds = lastVoiced
        peakLevel = tracker.peakLevel
        noiseFloor = tracker.noiseFloor
        voiceThreshold = tracker.voiceThreshold
    }
}
