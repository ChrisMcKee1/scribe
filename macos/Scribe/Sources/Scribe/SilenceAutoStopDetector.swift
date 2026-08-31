import Foundation

/// Real-time silence detector driving auto-stop for toggle-mode dictation (menu-triggered capture
/// that has no natural "release" gesture the way push-to-talk does). Windows Scribe uses a trained
/// Silero VAD model for this; a full ONNX port was assessed for macOS (would need a new
/// sherpa-onnx/ONNX Runtime dependency plus bundling the Silero model) and shelved as
/// disproportionate to the payoff, since a level-based detector plus adaptive tightening (below)
/// closes most of the practical gap. See PORTING-PLAN.md for that scope decision.
///
/// Beyond the original fixed `-45 dBFS` cutoff, this tracks a slow-decaying estimate of the
/// quietest levels actually observed and only ever **tightens** the effective threshold below the
/// fixed default, never loosens it above. That one-directional design is deliberate: it is safe to
/// reason about (the detector can never become more trigger-happy than its tested default, so
/// there is no regression risk) and it fixes a real failure mode of a fixed threshold: in an
/// unusually quiet recording setup (good mic, treated room), genuinely quiet speech sits well
/// above true silence but can still be well below a threshold tuned for a typical room, so the
/// original fixed detector would misread a quiet mid-sentence pause, or even quiet continuous
/// speech, as "the user stopped talking." Adapting the threshold down toward the observed floor
/// avoids that.
///
/// What this does **not** fix: a room whose *ambient* noise floor is louder than the default
/// threshold (e.g. a fan or AC hum above -45 dBFS). Raising the threshold to accommodate that
/// would require telling louder-than-default ambient noise apart from actual speech the first
/// time it's heard, which a pure signal-level detector cannot do (that's exactly the job a trained
/// VAD does by looking at the shape of the signal, not just its level). That case remains a
/// genuine, tracked gap toward Windows' Silero VAD.
///
/// The detector only ever answers "has this capture been silent long enough to auto-stop", it never
/// stops capture itself; the caller (AppDelegate) decides whether that answer matters, since
/// push-to-talk capture should never auto-stop on silence (release already ends it).
final class SilenceAutoStopDetector {
    /// The fixed cutoff used until enough quiet samples have been observed to adapt, and the
    /// ceiling the adaptive threshold can never exceed. -45 dBFS is comfortably below normal
    /// speech levels (typically -25 to -10 dBFS) but above typical room-noise floor on a laptop
    /// microphone, so a quiet room does not immediately look like silence.
    let baseSilenceThresholdDbfs: Float

    /// How long the signal must stay below the threshold before auto-stop fires.
    let requiredSilenceDuration: TimeInterval

    /// How far above the tracked quiet-level floor the adaptive threshold sits. Keeps the
    /// threshold from hugging the floor so closely that ordinary room-tone variance flickers
    /// across it.
    private let noiseFloorMarginDb: Float

    /// The adaptive threshold never drops below this, so an extraordinarily quiet capture (or a
    /// stray near-zero sample) can't tighten the detector to the point that it never fires.
    private let minEffectiveThresholdDbfs: Float

    /// How quickly the floor estimate chases a newly observed quiet level (exponential moving
    /// average weight per sample). Deliberately slow: this is meant to track the room, not react
    /// to one unusually quiet chunk.
    private let noiseFloorAdaptationRate: Float

    private var noiseFloorEstimateDbfs: Float
    private var lastAppliedThresholdDbfs: Float
    private var silenceStartedAt: Date?
    private var hasObservedSpeech = false
    private var hasFired = false

    init(
        silenceThresholdDbfs: Float = -45,
        requiredSilenceDuration: TimeInterval = 2.0,
        noiseFloorMarginDb: Float = 8,
        minEffectiveThresholdDbfs: Float = -60,
        noiseFloorAdaptationRate: Float = 0.15
    ) {
        self.baseSilenceThresholdDbfs = silenceThresholdDbfs
        self.requiredSilenceDuration = requiredSilenceDuration
        self.noiseFloorMarginDb = noiseFloorMarginDb
        self.minEffectiveThresholdDbfs = minEffectiveThresholdDbfs
        self.noiseFloorAdaptationRate = noiseFloorAdaptationRate
        self.noiseFloorEstimateDbfs = silenceThresholdDbfs - noiseFloorMarginDb
        self.lastAppliedThresholdDbfs = silenceThresholdDbfs
    }

    /// The threshold actually applied on the most recent `observe` call. Starts at
    /// `baseSilenceThresholdDbfs` and only ever moves at or below it as the floor estimate adapts;
    /// useful for logging what actually fired auto-stop rather than just the static default.
    var silenceThresholdDbfs: Float { lastAppliedThresholdDbfs }

    /// Feed one measured chunk. Returns `true` exactly once, the moment auto-stop should trigger;
    /// returns `false` on every other call, including calls after it already fired once (callers
    /// should stop capture and discard the detector instance on a `true` result).
    func observe(level: AudioLevelMeasurement, at now: Date = Date()) -> Bool {
        // Once fired, stay false permanently: callers are expected to discard the instance, but
        // guard here too so a stray extra `observe` call after firing can never fire a second time.
        guard !hasFired else { return false }

        let effectiveThreshold = min(baseSilenceThresholdDbfs, max(minEffectiveThresholdDbfs, noiseFloorEstimateDbfs + noiseFloorMarginDb))
        lastAppliedThresholdDbfs = effectiveThreshold

        let isSilent = level.rmsDbfs < effectiveThreshold

        if isSilent, level.rmsDbfs.isFinite {
            // Only quiet chunks ever move the floor, and only toward whatever was just observed:
            // sustained loud speech can never drag the floor (and therefore the threshold) upward,
            // which is what keeps this one-directional and safe.
            noiseFloorEstimateDbfs += (level.rmsDbfs - noiseFloorEstimateDbfs) * noiseFloorAdaptationRate
        }

        guard isSilent else {
            hasObservedSpeech = true
            silenceStartedAt = nil
            return false
        }

        // Do not auto-stop before any real speech was observed: otherwise a toggle-mode capture
        // started in a quiet room would immediately auto-stop before the user says anything.
        guard hasObservedSpeech else {
            return false
        }

        guard let silenceStartedAt else {
            self.silenceStartedAt = now
            return false
        }

        let shouldFire = now.timeIntervalSince(silenceStartedAt) >= requiredSilenceDuration
        if shouldFire {
            hasFired = true
        }
        return shouldFire
    }

    func reset() {
        noiseFloorEstimateDbfs = baseSilenceThresholdDbfs - noiseFloorMarginDb
        lastAppliedThresholdDbfs = baseSilenceThresholdDbfs
        silenceStartedAt = nil
        hasObservedSpeech = false
        hasFired = false
    }
}
