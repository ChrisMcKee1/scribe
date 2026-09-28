namespace Scribe.Core.Overlay;

/// <summary>
/// The level the recording indicator's bars show, 0 to 1, from the peak of each piece of audio the microphone delivers.
/// The peak is read in dBFS over a window that ordinary speech fills: at <see cref="QuietDbfs"/> and below the bars rest
/// on their floor, and at <see cref="FullDbfs"/> and above they stand at full height. Speech at a typical microphone level
/// (10 ms peaks of about -17 dBFS at the median and -12 dBFS at the 90th percentile) therefore moves them most of their
/// height, and a louder voice still raises them. The level rises fast and falls more slowly
/// (<see cref="AttackMs"/>, <see cref="ReleaseMs"/>), each step timed by the time since the previous piece arrived, so
/// the attack and release times hold whatever interval a device delivers its audio at.
/// </summary>
/// <remarks>
/// Until 0.5.1 the level was the square root of the linear peak, and each bar was that times its proportion with the
/// floor as its minimum: ordinary speech moved the centre bar from 4 to about 6 DIP, its neighbours by half a DIP, and
/// never the outer two. Not thread-safe: the capture thread that raises the level updates it.
/// </remarks>
public sealed class PillLevelMeter
{
    /// <summary>The peak, in dBFS, at and below which the level is 0: below the noise of a quiet room.</summary>
    public const double QuietDbfs = -54;

    /// <summary>The peak, in dBFS, at and above which the level is 1: a raised voice, above ordinary speech's peaks.</summary>
    public const double FullDbfs = -6;

    /// <summary>The time constant, in ms, of a rising level: a syllable shows at once.</summary>
    public const double AttackMs = 10;

    /// <summary>The time constant, in ms, of a falling level: the bars drop between syllables and rest in a pause.</summary>
    public const double ReleaseMs = 120;

    // The step assumed when no earlier update times it (the first after Reset), and the longest step taken at once, so a
    // device that stalls and resumes decays no further than it would have in 250 ms.
    internal const double NominalStepMs = 10;
    internal const double MaxStepMs = 250;

    private double _level;
    private bool _started;

    /// <summary>The level last computed, 0 to 1.</summary>
    public double Level => _level;

    /// <summary>Starts again from silence, as a new recording does.</summary>
    public void Reset()
    {
        _level = 0;
        _started = false;
    }

    /// <summary>
    /// Takes one piece of audio's peak (linear, 1 at full scale) and the time since the previous piece arrived, and
    /// returns the new level. The first piece after <see cref="Reset"/> is timed as <c>10 ms</c>.
    /// </summary>
    public double Update(float peak, TimeSpan sincePrevious)
    {
        var stepMs = _started ? Math.Clamp(sincePrevious.TotalMilliseconds, 0, MaxStepMs) : NominalStepMs;
        _started = true;

        // Exponential approach to the peak's level: exact however the time is split between callbacks, because two steps
        // toward the same target compose into one step of their combined length.
        var target = TargetOf(peak);
        var timeConstant = target > _level ? AttackMs : ReleaseMs;
        _level = target + ((_level - target) * Math.Exp(-stepMs / timeConstant));
        return _level;
    }

    /// <summary>The level a peak held long enough leaves the bars at, 0 to 1. Silence and a peak that is not a number give 0.</summary>
    public static double TargetOf(float peak)
    {
        if (!(peak > 0))
        {
            return 0;
        }

        var dbfs = 20 * Math.Log10(peak);
        return Math.Clamp((dbfs - QuietDbfs) / (FullDbfs - QuietDbfs), 0, 1);
    }
}
