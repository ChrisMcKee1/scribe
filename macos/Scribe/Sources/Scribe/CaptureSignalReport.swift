import Foundation

/// Peak and RMS of one capture channel, before any downmix.
struct CaptureChannelLevel: Sendable, Equatable {
    let channel: Int
    let peak: Float
    let rms: Float

    var peakDbfs: Double { CaptureSignalReport.dbfs(peak) }
    var rmsDbfs: Double { CaptureSignalReport.dbfs(rms) }
}

/// The measurable shape of one capture: levels, clipping, DC offset and what each channel carried before
/// Scribe averaged them. It holds statistics only, never audio, so it can go in a log (PRIVACY.md) and still
/// separate the failures that make captures look alike: a stream that stopped delivering, a microphone that
/// was never live, a gain far too low for the recognizer, a clipping input, or a live channel averaged
/// against a dead one. A port of Windows' `CaptureSignalReport`.
struct CaptureSignalReport: Sendable, Equatable {
    /// A sample this close to full scale counts as clipped.
    static let clipThreshold: Float = 0.999
    /// Below this a sample counts toward the near-silent fraction, about -60 dBFS.
    static let nearSilenceThreshold: Float = 0.001
    /// A channel peaking below this carries nothing worth averaging in.
    static let silentChannelPeak: Float = 0.0005

    let channels: Int
    /// The device's rate, before resampling.
    let sampleRate: Double
    let peak: Float
    let rms: Float
    let clippedFraction: Double
    let nearSilentFraction: Double
    let dcOffset: Float
    /// Empty when nothing was measured.
    let perChannel: [CaptureChannelLevel]

    var peakDbfs: Double { Self.dbfs(peak) }
    var rmsDbfs: Double { Self.dbfs(rms) }

    /// A second channel carries essentially nothing. Scribe averages every channel, so this costs the speech
    /// 6 dB or more before the recognizer hears it.
    var hasSilentChannel: Bool {
        perChannel.count > 1 && perChannel.contains { $0.peak < Self.silentChannelPeak }
    }

    /// The channels differ enough that they are not the same microphone: a headset's reference channel, an
    /// echo-cancellation loopback, or a genuinely stereo pair.
    var channelsDiverge: Bool {
        guard perChannel.count > 1, let loudest = perChannel.map(\.rms).max(),
            let quietest = perChannel.map(\.rms).min(), loudest > 0
        else {
            return false
        }
        return quietest / loudest < 0.5
    }

    /// Converts a linear amplitude to dBFS, floored at -99 so silence reads as a number.
    static func dbfs(_ amplitude: Float) -> Double {
        amplitude <= 0 ? -99 : max(-99, 20 * log10(Double(amplitude)))
    }

    /// Measures interleaved samples in one pass, for callers that already hold a whole buffer.
    static func analyze(interleaved samples: [Float], channels: Int, sampleRate: Double) -> CaptureSignalReport {
        var accumulator = CaptureSignalAccumulator(channels: channels)
        guard channels > 0 else { return accumulator.report(sampleRate: sampleRate) }
        let frames = samples.count / channels
        for frame in 0..<frames {
            for channel in 0..<channels {
                accumulator.add(samples[frame * channels + channel], channel: channel)
            }
        }
        accumulator.countFrames(frames)
        return accumulator.report(sampleRate: sampleRate)
    }

    /// The report as log fields: numbers and flags only.
    var logFields: [ScribeLog.Field] {
        var fields: [ScribeLog.Field] = [
            .count("channels", channels),
            .decimal("deviceRate", sampleRate, precision: 0),
            .decimal("peakDbfs", peakDbfs),
            .decimal("rmsDbfs", rmsDbfs),
            .decimal("clippedPercent", clippedFraction * 100, precision: 2),
            .decimal("nearSilentPercent", nearSilentFraction * 100, precision: 0),
            .decimal("dcOffset", Double(dcOffset), precision: 4),
        ]
        if let quietest = perChannel.map(\.rms).min(), let loudest = perChannel.map(\.rms).max() {
            fields.append(.decimal("quietestChannelRmsDbfs", Self.dbfs(quietest)))
            fields.append(.decimal("loudestChannelRmsDbfs", Self.dbfs(loudest)))
        }
        fields.append(.flag("silentChannel", hasSilentChannel))
        fields.append(.flag("channelsDiverge", channelsDiverge))
        return fields
    }
}

/// Builds a `CaptureSignalReport` from samples as they arrive, without keeping any of them.
struct CaptureSignalAccumulator: Sendable {
    let channels: Int
    private var peaks: [Float]
    private var sumSquares: [Double]
    private var frames: Int64 = 0
    private var sum: Double = 0
    private var clipped: Int64 = 0
    private var nearSilent: Int64 = 0

    init(channels: Int) {
        self.channels = max(0, channels)
        peaks = Array(repeating: 0, count: max(0, channels))
        sumSquares = Array(repeating: 0, count: max(0, channels))
    }

    /// Adds one sample of `channel`, which must be below `channels`.
    mutating func add(_ value: Float, channel: Int) {
        let magnitude = abs(value)
        if magnitude > peaks[channel] {
            peaks[channel] = magnitude
        }
        sumSquares[channel] += Double(value) * Double(value)
        sum += Double(value)
        if magnitude >= CaptureSignalReport.clipThreshold {
            clipped += 1
        }
        if magnitude < CaptureSignalReport.nearSilenceThreshold {
            nearSilent += 1
        }
    }

    /// Records that `count` whole frames, one sample of every channel each, were added.
    mutating func countFrames(_ count: Int) {
        frames += Int64(max(0, count))
    }

    func report(sampleRate: Double) -> CaptureSignalReport {
        guard channels > 0, frames > 0 else {
            return CaptureSignalReport(
                channels: channels, sampleRate: sampleRate, peak: 0, rms: 0, clippedFraction: 0,
                nearSilentFraction: 0, dcOffset: 0, perChannel: [])
        }

        let totalSamples = Double(frames) * Double(channels)
        let perChannel = (0..<channels).map { channel in
            CaptureChannelLevel(
                channel: channel,
                peak: peaks[channel],
                rms: Float((sumSquares[channel] / Double(frames)).squareRoot()))
        }
        return CaptureSignalReport(
            channels: channels,
            sampleRate: sampleRate,
            peak: peaks.max() ?? 0,
            rms: Float((sumSquares.reduce(0, +) / totalSamples).squareRoot()),
            clippedFraction: Double(clipped) / totalSamples,
            nearSilentFraction: Double(nearSilent) / totalSamples,
            dcOffset: Float(sum / totalSamples),
            perChannel: perChannel)
    }
}
