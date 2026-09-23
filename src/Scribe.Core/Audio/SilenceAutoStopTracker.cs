namespace Scribe.Core.Audio;

/// <summary>
/// Decides when a toggle-mode dictation should stop by itself because the speaker went quiet.
/// Fed with the peak level of every capture buffer; reports "stop now" after sustained silence
/// <b>following speech</b>, or after a longer lead-in when no speech ever arrived (muted or wrong
/// mic), so a forgotten toggle can't leave the microphone hot indefinitely.
/// <para>
/// What counts as speech adapts to the room. The tracker follows the noise floor, a low percentile
/// of recent buffer peaks, and a buffer is voiced only when it peaks above both an absolute floor
/// and that noise floor plus a margin. A fixed threshold failed both ways: speech from a quiet
/// microphone never reached it, and steady noise above it held the capture open forever.
/// </para>
/// <para>
/// Pure function of the supplied levels and timestamps, so it is deterministic and unit-testable
/// without real audio, and allocation-free: it runs once per capture buffer on the audio thread.
/// </para>
/// </summary>
public sealed class SilenceAutoStopTracker
{
    /// <summary>
    /// About -45 dBFS: the quietest peak that can count as voice however quiet the room is. It
    /// hears speech peaking at -40 dBFS with 5 dB to spare, where the fixed 0.02 (-34 dBFS) this
    /// replaces heard none of it and ended such dictations on the lead-in.
    /// </summary>
    public const float DefaultAbsoluteFloor = 0.0056f;

    // How far above the noise floor a buffer must peak to count as voice. The 10 ms peaks of steady
    // pink noise stay within about 8 dB of the tracked floor, so 10 dB rejects it while speech that
    // peaks about 15 dB above the noise still gets through.
    private const float NoiseMarginDb = 10f;

    // The floor follows roughly the 9th percentile of recent buffer peaks: it rises slowly toward
    // louder buffers and falls ten times faster toward quieter ones. Steady noise is learned within
    // seconds, while the gaps between words keep pulling the floor back down during speech.
    private const float RiseDbPerSecond = 6f;
    private const float FallDbPerSecond = 60f;

    // A toggle-mode capture opens on the room, not on speech. The first 300 ms train the floor at
    // much faster rates and never count as voice, so noise present from the start is known before
    // it can pass for speech, and a noise-only capture ends on the lead-in with HeardSpeech false.
    private const long CalibrationMs = 300;
    private const float CalibrationRiseDbPerSecond = 200f;
    private const float CalibrationFallDbPerSecond = 2_000f;

    // Levels at or below 1e-5 (digital silence) all map to -100 dBFS.
    private const float SilenceDb = -100f;
    private const float SilenceLevel = 1e-5f;

    private readonly float _absoluteFloorDb;
    private readonly float _lowestFloorDb;
    private readonly long _silenceHoldMs;
    private readonly long _leadInLimitMs;
    private readonly long _startedMs;

    private float _floorDb;
    private long _lastUpdateMs;
    private long _lastVoiceMs;
    private bool _heardSpeech;

    public SilenceAutoStopTracker(
        long startedMs,
        float absoluteFloor = DefaultAbsoluteFloor,
        long silenceHoldMs = 4_000,
        long leadInLimitMs = 10_000)
    {
        if (!(absoluteFloor > 0f && absoluteFloor <= 1f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(absoluteFloor), absoluteFloor, "The absolute floor must be a level in (0, 1].");
        }

        _absoluteFloorDb = ToDb(absoluteFloor);

        // Below this the absolute floor decides alone, so the noise floor never needs to sink
        // further, and it never has far to climb once real noise arrives after digital silence.
        _lowestFloorDb = _absoluteFloorDb - NoiseMarginDb;
        _floorDb = _lowestFloorDb;
        _silenceHoldMs = silenceHoldMs;
        _leadInLimitMs = leadInLimitMs;
        _startedMs = startedMs;
        _lastUpdateMs = startedMs;
        _lastVoiceMs = startedMs;
    }

    /// <summary>
    /// True once any buffer has counted as voice. After a stop it distinguishes the two very
    /// different reasons this tracker fires: the speaker finished and went quiet, or the input never
    /// rose above the noise floor at all (a muted or wrong device, a gain so low that real speech
    /// reads as silence, or only steady noise). Those look identical to the user and need opposite
    /// fixes, so the log records which one happened.
    /// </summary>
    public bool HeardSpeech => _heardSpeech;

    /// <summary>Loudest level seen, for judging how far under the threshold a quiet mic sat.</summary>
    public float PeakLevel { get; private set; }

    /// <summary>The current noise-floor estimate, as a peak level (0..1).</summary>
    public float NoiseFloor => FromDb(_floorDb);

    /// <summary>The peak level a buffer must exceed right now to count as voice.</summary>
    public float VoiceThreshold => FromDb(ThresholdDb);

    private float ThresholdDb => MathF.Max(_absoluteFloorDb, _floorDb + NoiseMarginDb);

    /// <summary>
    /// Feeds one buffer's peak level. Returns true when the dictation should stop: the speaker has
    /// been silent for the hold window after speaking, or never spoke within the lead-in limit.
    /// </summary>
    public bool Update(float level, long timestampMs)
    {
        if (level > PeakLevel)
        {
            PeakLevel = level;
        }

        var levelDb = ToDb(level);
        var calibrating = timestampMs - _startedMs < CalibrationMs;

        // Judged against the floor as it stood before this buffer, so one loud buffer cannot raise
        // the bar it is measured against.
        var voiced = !calibrating && levelDb > ThresholdDb;
        AdaptFloor(levelDb, timestampMs, calibrating);

        if (voiced)
        {
            _heardSpeech = true;
            _lastVoiceMs = timestampMs;
            return false;
        }

        return _heardSpeech
            ? timestampMs - _lastVoiceMs >= _silenceHoldMs
            : timestampMs - _startedMs >= _leadInLimitMs;
    }

    // Rates are per second of capture time rather than per buffer, so the estimate does not depend
    // on how large the device's buffers are or how often the levels arrive.
    private void AdaptFloor(float levelDb, long timestampMs, bool calibrating)
    {
        var elapsedSeconds = Math.Max(0, timestampMs - _lastUpdateMs) / 1000f;
        _lastUpdateMs = Math.Max(_lastUpdateMs, timestampMs);

        _floorDb = levelDb > _floorDb
            ? MathF.Min(levelDb, _floorDb + ((calibrating ? CalibrationRiseDbPerSecond : RiseDbPerSecond) * elapsedSeconds))
            : MathF.Max(levelDb, _floorDb - ((calibrating ? CalibrationFallDbPerSecond : FallDbPerSecond) * elapsedSeconds));
        _floorDb = Math.Clamp(_floorDb, _lowestFloorDb, 0f);
    }

    private static float ToDb(float level) =>
        level > SilenceLevel ? 20f * MathF.Log10(level) : SilenceDb;

    private static float FromDb(float db) => MathF.Pow(10f, db / 20f);
}
