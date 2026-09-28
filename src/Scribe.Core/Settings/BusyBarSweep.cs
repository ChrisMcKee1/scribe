namespace Scribe.Core.Settings;

/// <summary>
/// The sweep WPF's indeterminate ProgressBar animates on its glow, computed exactly as <c>ProgressBar.UpdateAnimation</c>
/// computes it (dotnet/wpf, MIT): the glow travels from hidden on the left (its own width to the left of the indicator) to
/// hidden on the right (the indicator's width plus its own) at 200 DIP per second, then pauses 1 s, and repeats; when the
/// glow's current left margin is already inside that travel, the timeline begins as if it had started that far back, so a
/// restart continues the sweep rather than jumping. Scribe's busy bar runs this same sweep on a clock it owns, only so that
/// hiding the bar can stop the clock, which WPF's own code leaves running.
/// </summary>
/// <param name="StartLeft">The glow's left margin at the start of each travel (hidden on the left).</param>
/// <param name="EndLeft">The glow's left margin at the end of each travel (hidden on the right).</param>
/// <param name="Travel">How long each travel takes.</param>
/// <param name="Pause">The pause after each travel.</param>
/// <param name="BeginTime">The animation's begin time: zero, or negative to continue a sweep already under way.</param>
public readonly record struct BusyBarSweep(double StartLeft, double EndLeft, TimeSpan Travel, TimeSpan Pause, TimeSpan BeginTime)
{
    /// <summary>The length of one cycle, travel and pause.</summary>
    public TimeSpan Duration => Travel + Pause;

    /// <summary>
    /// The sweep for an indicator and glow of these widths (their <c>Width</c> properties, as WPF reads them) with the glow's
    /// current left margin, or null where WPF would not animate (a width that is not positive, or not a number).
    /// </summary>
    public static BusyBarSweep? For(double indicatorWidth, double glowWidth, double glowLeft)
    {
        if (!(glowWidth > 0) || !(indicatorWidth > 0))
        {
            return null;
        }

        var endPos = indicatorWidth + glowWidth;
        var startPos = -1 * glowWidth;

        // Travel at 200 DIP per second (WPF truncates the distance to whole DIP first); pause 1 s between travels.
        var travel = TimeSpan.FromSeconds((int)(endPos - startPos) / 200.0);
        var pause = TimeSpan.FromSeconds(1.0);

        // Is the glow already under way (with one DIP of fudge on the right, as WPF has it)?
        var begin = GreaterThan(glowLeft, startPos) && LessThan(glowLeft, endPos - 1)
            ? TimeSpan.FromSeconds(-1 * (glowLeft - startPos) / 200.0)
            : TimeSpan.Zero;
        return new BusyBarSweep(startPos, endPos, travel, pause, begin);
    }

    // WPF's DoubleUtil comparisons (dotnet/wpf, MIT), which UpdateAnimation uses.
    private const double DoubleEpsilon = 2.2204460492503131e-016;

    private static bool AreClose(double value1, double value2)
    {
        if (value1 == value2)
        {
            return true;
        }

        var eps = (Math.Abs(value1) + Math.Abs(value2) + 10.0) * DoubleEpsilon;
        var delta = value1 - value2;
        return -eps < delta && eps > delta;
    }

    private static bool LessThan(double value1, double value2) => value1 < value2 && !AreClose(value1, value2);

    private static bool GreaterThan(double value1, double value2) => value1 > value2 && !AreClose(value1, value2);
}
