import Foundation

/// Real-time silence detector driving auto-stop for toggle-mode dictation (menu-triggered capture
/// that has no natural "release" gesture the way push-to-talk does). Windows Scribe uses a trained
/// Silero VAD model for this; the macOS port starts with an energy-threshold detector on the same
/// RMS levels `AudioCaptureEngine` already measures per chunk, because it requires no additional
/// model download and is enough to prove the auto-stop behavior end to end. A true Silero ONNX VAD
/// (matching Windows exactly, and also usable for future mid-dictation pause detection) is tracked
/// as a follow-up; see PORTING-PLAN.md.
///
/// The detector only ever answers "has this capture been silent long enough to auto-stop", it never
/// stops capture itself; the caller (AppDelegate) decides whether that answer matters, since
/// push-to-talk capture should never auto-stop on silence (release already ends it).
final class SilenceAutoStopDetector {
    /// Chunks quieter than this RMS dBFS level count as silence. -45 dBFS is comfortably below
    /// normal speech levels (typically -25 to -10 dBFS) but above typical room-noise floor on a
    /// laptop microphone, so a quiet room does not immediately look like silence.
    let silenceThresholdDbfs: Float

    /// How long the signal must stay below the threshold before auto-stop fires.
    let requiredSilenceDuration: TimeInterval

    private var silenceStartedAt: Date?
    private var hasObservedSpeech = false
    private var hasFired = false

    init(silenceThresholdDbfs: Float = -45, requiredSilenceDuration: TimeInterval = 2.0) {
        self.silenceThresholdDbfs = silenceThresholdDbfs
        self.requiredSilenceDuration = requiredSilenceDuration
    }

    /// Feed one measured chunk. Returns `true` exactly once, the moment auto-stop should trigger;
    /// returns `false` on every other call, including calls after it already fired once (callers
    /// should stop capture and discard the detector instance on a `true` result).
    func observe(level: AudioLevelMeasurement, at now: Date = Date()) -> Bool {
        // Once fired, stay false permanently: callers are expected to discard the instance, but
        // guard here too so a stray extra `observe` call after firing can never fire a second time.
        guard !hasFired else { return false }

        let isSilent = level.rmsDbfs < silenceThresholdDbfs

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
        silenceStartedAt = nil
        hasObservedSpeech = false
        hasFired = false
    }
}
