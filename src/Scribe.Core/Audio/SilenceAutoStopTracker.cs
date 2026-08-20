namespace Scribe.Core.Audio;

/// <summary>
/// Decides when a toggle-mode dictation should stop by itself because the speaker went quiet.
/// Fed with live input levels; reports "stop now" after sustained silence <b>following speech</b>,
/// or after a longer lead-in of pure silence when no speech ever arrived (muted or wrong mic), so
/// a forgotten toggle can't leave the microphone hot indefinitely. Pure function of the supplied
/// timestamps, so it is deterministic and unit-testable without real audio.
/// </summary>
public sealed class SilenceAutoStopTracker
{
    // Raw capture level (0..1) below which a sample counts as silence. Deliberately low: normal
    // room tone on a decent mic idles well under this, while quiet speech peaks above it.
    private const float DefaultSilenceThreshold = 0.02f;

    private readonly float _silenceThreshold;
    private readonly long _silenceHoldMs;
    private readonly long _leadInLimitMs;

    private long _startedMs;
    private long _lastVoiceMs;
    private bool _heardSpeech;

    public SilenceAutoStopTracker(
        long startedMs,
        float silenceThreshold = DefaultSilenceThreshold,
        long silenceHoldMs = 4_000,
        long leadInLimitMs = 10_000)
    {
        _silenceThreshold = silenceThreshold;
        _silenceHoldMs = silenceHoldMs;
        _leadInLimitMs = leadInLimitMs;
        _startedMs = startedMs;
        _lastVoiceMs = startedMs;
    }

    /// <summary>
    /// True once any sample has crossed the speech threshold. After a stop it distinguishes the two
    /// very different reasons this tracker fires: the speaker finished and went quiet, or the input
    /// never rose above the noise floor at all (a muted or wrong device, or a gain so low that real
    /// speech reads as silence). Those look identical to the user and need opposite fixes, so the
    /// log records which one happened.
    /// </summary>
    public bool HeardSpeech => _heardSpeech;

    /// <summary>Loudest level seen, for judging how far under the threshold a quiet mic sat.</summary>
    public float PeakLevel { get; private set; }

    /// <summary>
    /// Feeds one level sample. Returns true when the dictation should stop: the speaker has been
    /// silent for the hold window after speaking, or never spoke within the lead-in limit.
    /// </summary>
    public bool Update(float level, long timestampMs)
    {
        if (level > PeakLevel)
        {
            PeakLevel = level;
        }

        if (level >= _silenceThreshold)
        {
            _heardSpeech = true;
            _lastVoiceMs = timestampMs;
            return false;
        }

        return _heardSpeech
            ? timestampMs - _lastVoiceMs >= _silenceHoldMs
            : timestampMs - _startedMs >= _leadInLimitMs;
    }
}
