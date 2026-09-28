namespace Scribe.Core.Overlay;

/// <summary>
/// The recording indicator's five level bars: how tall each stands for a level from <see cref="PillLevelMeter"/>. A bar
/// is laid out 16 DIP tall and scaled vertically by a render transform, so a level update never runs a layout pass. In
/// silence every bar rests on its 4 DIP floor; above the floor each rises by its proportion of the other 12 DIP, in the
/// icon's proportions (0.26, 0.56, 1, 0.56, 0.26), so every bar moves and at full level the centre stands 16 DIP, its
/// neighbours 10.7 and the outer two 7.1. Pure arithmetic with no WinUI in it: the overlay, which has no reference to
/// Scribe.Core, compiles this file itself through a linked Compile item.
/// </summary>
public static class PillLevelBars
{
    /// <summary>How many bars the pill draws.</summary>
    public const int Count = 5;

    /// <summary>The scale a bar keeps in silence: its 4 DIP floor of the 16 DIP it is laid out at.</summary>
    public const double Floor = 4.0 / 16.0;

    private static readonly double[] Proportions = [0.26, 0.56, 1.0, 0.56, 0.26];

    /// <summary>The share of the travel above the floor that bar <paramref name="bar"/> (0 to 4, left to right) rises by.</summary>
    public static double Proportion(int bar) => Proportions[bar];

    /// <summary>The vertical scale of bar <paramref name="bar"/> at <paramref name="level"/> (0 to 1; outside that, and a
    /// level that is not a number, are held to the nearer end, with a level that is not a number resting on the floor).</summary>
    public static double ScaleOf(int bar, double level)
    {
        var held = level > 0 ? Math.Min(level, 1) : 0;
        return Floor + ((1 - Floor) * Proportions[bar] * held);
    }
}
