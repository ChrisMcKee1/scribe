using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Scribe.Core.Diagnostics;

/// <summary>
/// The stages of one timed span, startup or a Settings window opening (StartupStageTiming): each <see cref="Mark"/> records
/// when a stage ended, and <see cref="Describe"/> gives them as fixed ASCII codes with whole milliseconds from one anchor,
/// so the log can say where the time went while carrying nothing but shapes.
/// </summary>
/// <remarks>
/// Marks come from one thread (startup and the Settings window both run on the UI thread), cost one timestamp read each
/// and never throw: a code that is not 1 to 16 lowercase ASCII letters or digits, or a mark past the 32nd, is dropped.
/// </remarks>
public sealed class StageTimeline
{
    /// <summary>The most marks one timeline keeps.</summary>
    public const int MaxMarks = 32;

    /// <summary>The longest stage code.</summary>
    public const int MaxCodeLength = 16;

    private readonly Func<long> _clock;
    private readonly long _frequency;
    private readonly string[] _codes = new string[MaxMarks];
    private readonly long[] _stamps = new long[MaxMarks];
    private int _count;

    private StageTimeline(Func<long> clock, long frequency)
    {
        _clock = clock;
        _frequency = frequency;
        Anchor = clock();
    }

    /// <summary>The anchor, in the timeline's clock: when it was started.</summary>
    public long Anchor { get; }

    /// <summary>How many marks it holds.</summary>
    public int Count => _count;

    /// <summary>Starts a timeline anchored now, on <see cref="Stopwatch"/>'s clock.</summary>
    public static StageTimeline Start() => new(Stopwatch.GetTimestamp, Stopwatch.Frequency);

    /// <summary>Starts a timeline on the given clock (ticks at <paramref name="frequency"/> per second), for tests.</summary>
    public static StageTimeline Start(Func<long> clock, long frequency)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        return new StageTimeline(clock, frequency);
    }

    /// <summary>Whether a stage code is 1 to 16 lowercase ASCII letters or digits.</summary>
    public static bool IsCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength)
        {
            return false;
        }

        foreach (var c in code)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Records that the stage <paramref name="code"/> ended now.</summary>
    public void Mark(string code)
    {
        var index = _count;
        if (index >= MaxMarks || !IsCode(code))
        {
            return;
        }

        _stamps[index] = _clock();
        _codes[index] = code;
        _count = index + 1;
    }

    /// <summary>Whole milliseconds from the anchor to now.</summary>
    public long ElapsedMilliseconds => ToMilliseconds(_clock() - Anchor);

    /// <summary>
    /// "code=ms code=ms ...": each mark's whole milliseconds after the anchor, plus <paramref name="offsetMilliseconds"/>
    /// (how long the anchor came after an earlier moment, such as the process's creation).
    /// </summary>
    public string Describe(long offsetMilliseconds = 0)
    {
        var text = new StringBuilder(_count * 14);
        for (var i = 0; i < _count; i++)
        {
            if (i > 0)
            {
                text.Append(' ');
            }

            text.Append(_codes[i]).Append('=')
                .Append((ToMilliseconds(_stamps[i] - Anchor) + offsetMilliseconds).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// How many whole milliseconds the anchor came after the process was created, from Windows' creation time and the
    /// wall clock read together with this timeline's clock now; null when that is not a sensible figure (a creation time
    /// after the anchor, or more than a day before it, as a changed system clock could give).
    /// </summary>
    public long? AnchorAfter(DateTime processCreatedUtc, DateTime nowUtc)
    {
        var sinceCreation = nowUtc - processCreatedUtc;
        var sinceAnchor = TimeSpan.FromMilliseconds(ElapsedMilliseconds);
        var offset = (long)Math.Floor((sinceCreation - sinceAnchor).TotalMilliseconds);
        return offset is >= 0 and <= 86_400_000 ? offset : null;
    }

    private long ToMilliseconds(long ticks) => ticks * 1000 / _frequency;
}
