import Foundation

/// Decides when a toggle-mode recording should stop by itself because the speaker went quiet: after
/// sustained silence following speech, or after a longer lead-in when no speech ever arrived (a muted or
/// wrong microphone, or a gain so low that speech reads as silence), so a forgotten toggle can never leave
/// the microphone on. It is a port of Windows' `SilenceAutoStopTracker` and is fed the same way: the peak
/// level of every 10 ms of capture, timed by the capture's own sample count rather than a wall clock.
///
/// What counts as speech adapts to the room. The tracker follows the noise floor, roughly the 9th
/// percentile of recent block peaks, and a block is voiced only when it peaks above both an absolute floor
/// and that noise floor plus a margin. A fixed threshold fails both ways: speech from a quiet microphone
/// never reaches it, and steady noise above it (a fan, an air conditioner) holds the capture open forever.
///
/// A plain value with no clock, no allocation and no audio, so it is deterministic and cheap enough to run
/// on the audio path for every block of a capture.
struct SilenceAutoStopTracker: Sendable {
    /// About -45 dBFS: the quietest peak that can count as voice however quiet the room is.
    static let defaultAbsoluteFloor: Float = 0.0056

    // How far above the noise floor a block must peak to count as voice. The 10 ms peaks of steady pink
    // noise stay within about 8 dB of the tracked floor, so 10 dB rejects it, while speech that peaks about
    // 15 dB above the noise still gets through.
    private static let noiseMarginDb: Float = 10

    // The floor rises slowly toward louder blocks and falls ten times faster toward quieter ones, so steady
    // noise is learned within seconds while the gaps between words keep pulling it back down during speech.
    private static let riseDbPerSecond: Float = 6
    private static let fallDbPerSecond: Float = 60

    // A toggle capture opens on the room, not on speech. The first 300 ms train the floor at much faster
    // rates and never count as voice, so noise present from the start is known before it can pass for
    // speech, and a noise-only capture ends on the lead-in with `heardSpeech` false.
    private static let calibrationMilliseconds: Int64 = 300
    private static let calibrationRiseDbPerSecond: Float = 200
    private static let calibrationFallDbPerSecond: Float = 2_000

    // Levels at or below 1e-5 (digital silence) all read as -100 dBFS.
    private static let silenceDb: Float = -100
    private static let silenceLevel: Float = 1e-5

    private let absoluteFloorDb: Float
    private let lowestFloorDb: Float
    private let silenceHoldMilliseconds: Int64
    private let leadInLimitMilliseconds: Int64
    private let startedMilliseconds: Int64

    private var floorDb: Float
    private var lastUpdateMilliseconds: Int64
    private var lastVoiceMilliseconds: Int64

    /// True once any block has counted as voice. After a stop it tells the two reasons apart: the speaker
    /// finished and went quiet, or the input never rose above the noise floor at all. They look the same to
    /// the user and need opposite fixes, so the log records which one happened.
    private(set) var heardSpeech = false

    /// The loudest level seen, for judging how far under the threshold a quiet microphone sat.
    private(set) var peakLevel: Float = 0

    init(configuration: SilenceAutoStopConfiguration = .standard, startedMilliseconds: Int64 = 0) {
        absoluteFloorDb = Self.decibels(configuration.absoluteFloor)
        // Below this the absolute floor decides alone, so the noise floor never needs to sink further, and it
        // never has far to climb once real noise arrives after digital silence.
        lowestFloorDb = absoluteFloorDb - Self.noiseMarginDb
        floorDb = lowestFloorDb
        silenceHoldMilliseconds = Self.milliseconds(configuration.silenceHold)
        leadInLimitMilliseconds = Self.milliseconds(configuration.leadInLimit)
        self.startedMilliseconds = startedMilliseconds
        lastUpdateMilliseconds = startedMilliseconds
        lastVoiceMilliseconds = startedMilliseconds
    }

    /// The current noise-floor estimate, as a peak level (0 to 1).
    var noiseFloor: Float { Self.level(floorDb) }

    /// The peak level a block must exceed right now to count as voice.
    var voiceThreshold: Float { Self.level(thresholdDb) }

    private var thresholdDb: Float { max(absoluteFloorDb, floorDb + Self.noiseMarginDb) }

    /// Feeds one block's peak level, measured at `timestamp` milliseconds of capture. Returns true when the
    /// recording should stop: the speaker has been silent for the hold window after speaking, or never spoke
    /// within the lead-in limit.
    mutating func update(level: Float, atMilliseconds timestamp: Int64) -> Bool {
        if level > peakLevel {
            peakLevel = level
        }

        let levelDb = Self.decibels(level)
        let calibrating = timestamp - startedMilliseconds < Self.calibrationMilliseconds

        // Judged against the floor as it stood before this block, so one loud block cannot raise the bar it
        // is measured against.
        let voiced = !calibrating && levelDb > thresholdDb
        adaptFloor(to: levelDb, at: timestamp, calibrating: calibrating)

        if voiced {
            heardSpeech = true
            lastVoiceMilliseconds = timestamp
            return false
        }

        return heardSpeech
            ? timestamp - lastVoiceMilliseconds >= silenceHoldMilliseconds
            : timestamp - startedMilliseconds >= leadInLimitMilliseconds
    }

    // Rates are per second of capture time rather than per block, so the estimate does not depend on how
    // large the blocks are or how often levels arrive.
    private mutating func adaptFloor(to levelDb: Float, at timestamp: Int64, calibrating: Bool) {
        let elapsedSeconds = Float(max(0, timestamp - lastUpdateMilliseconds)) / 1_000
        lastUpdateMilliseconds = max(lastUpdateMilliseconds, timestamp)

        if levelDb > floorDb {
            let rate = calibrating ? Self.calibrationRiseDbPerSecond : Self.riseDbPerSecond
            floorDb = min(levelDb, floorDb + rate * elapsedSeconds)
        } else {
            let rate = calibrating ? Self.calibrationFallDbPerSecond : Self.fallDbPerSecond
            floorDb = max(levelDb, floorDb - rate * elapsedSeconds)
        }
        floorDb = min(max(floorDb, lowestFloorDb), 0)
    }

    private static func decibels(_ level: Float) -> Float {
        level > silenceLevel ? 20 * log10f(level) : silenceDb
    }

    private static func level(_ decibels: Float) -> Float {
        powf(10, decibels / 20)
    }

    private static func milliseconds(_ duration: Duration) -> Int64 {
        let (seconds, attoseconds) = duration.components
        let (whole, overflowed) = seconds.multipliedReportingOverflow(by: 1_000)
        if overflowed {
            return seconds < 0 ? .min : .max
        }
        let (total, sumOverflowed) = whole.addingReportingOverflow(attoseconds / 1_000_000_000_000_000)
        return sumOverflowed ? .max : total
    }
}

/// How a toggle recording decides the speaker has finished.
struct SilenceAutoStopConfiguration: Sendable, Equatable {
    /// The quietest peak level (0 to 1) that can count as voice, however quiet the room is.
    let absoluteFloor: Float
    /// Silence after the last voiced block that ends the recording.
    let silenceHold: Duration
    /// Silence from the start, with no voiced block at all, that ends the recording. Longer than the hold,
    /// so a thinking pause before the first word is not cut off.
    let leadInLimit: Duration

    /// Windows' defaults: an absolute floor of about -45 dBFS, 4 s of silence after speech, 10 s without it.
    static let standard = SilenceAutoStopConfiguration(
        checkedAbsoluteFloor: SilenceAutoStopTracker.defaultAbsoluteFloor,
        silenceHold: .seconds(4),
        leadInLimit: .seconds(10))

    /// `nil` when `absoluteFloor` is not a level in (0, 1] or either duration is negative.
    init?(
        absoluteFloor: Float = SilenceAutoStopTracker.defaultAbsoluteFloor,
        silenceHold: Duration = .seconds(4),
        leadInLimit: Duration = .seconds(10)
    ) {
        guard absoluteFloor > 0, absoluteFloor <= 1, silenceHold >= .zero, leadInLimit >= .zero else {
            return nil
        }
        self.init(checkedAbsoluteFloor: absoluteFloor, silenceHold: silenceHold, leadInLimit: leadInLimit)
    }

    private init(checkedAbsoluteFloor: Float, silenceHold: Duration, leadInLimit: Duration) {
        self.absoluteFloor = checkedAbsoluteFloor
        self.silenceHold = silenceHold
        self.leadInLimit = leadInLimit
    }
}

/// What may end a recording other than its owner's stop. The lifecycle picks it per recording, from the
/// binding that fired, and hands it to `AudioCaptureEngine.start`, which enforces it on the audio path.
struct CaptureStopPolicy: Sendable, Equatable {
    /// Windows' `MaxDictationMinutes` default. A forgotten toggle or a stuck key otherwise records, and grows
    /// the capture, without bound; at the ceiling the recording ends and keeps everything it captured.
    static let defaultMaximumDuration: Duration = .seconds(10 * 60)

    /// Silence auto-stop, or `nil` when silence never ends the recording. A held key must never auto-stop:
    /// releasing it ends the recording, and a pause while it is held would lose everything said after it.
    let silence: SilenceAutoStopConfiguration?

    /// The longest the recording runs before it ends itself, or `nil` for no ceiling.
    let maximumDuration: Duration?

    /// A `maximumDuration` of zero or less means no ceiling, like Windows' `MaxDictationMinutes = 0`.
    init(
        silence: SilenceAutoStopConfiguration?,
        maximumDuration: Duration? = CaptureStopPolicy.defaultMaximumDuration
    ) {
        self.silence = silence
        if let maximumDuration, maximumDuration > .zero {
            self.maximumDuration = maximumDuration
        } else {
            self.maximumDuration = nil
        }
    }

    /// For a binding held while talking: never stops on silence; the ceiling still applies.
    static func hold(maximumDuration: Duration? = CaptureStopPolicy.defaultMaximumDuration) -> CaptureStopPolicy {
        CaptureStopPolicy(silence: nil, maximumDuration: maximumDuration)
    }

    /// For a binding tapped on and off: stops on silence, and at the ceiling.
    static func toggle(
        silence: SilenceAutoStopConfiguration = .standard,
        maximumDuration: Duration? = CaptureStopPolicy.defaultMaximumDuration
    ) -> CaptureStopPolicy {
        CaptureStopPolicy(silence: silence, maximumDuration: maximumDuration)
    }

    /// The policy for a recording that `gesture` started: a toggle stops on silence when
    /// `autoStopOnSilence` is on, a hold never does, and both stop at the ceiling.
    init(
        gesture: HotkeyGesture,
        autoStopOnSilence: Bool = true,
        maximumDuration: Duration? = CaptureStopPolicy.defaultMaximumDuration
    ) {
        let stopsOnSilence = gesture == .toggle && autoStopOnSilence
        self.init(silence: stopsOnSilence ? .standard : nil, maximumDuration: maximumDuration)
    }

    var stopsOnSilence: Bool { silence != nil }
}
