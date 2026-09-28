using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// The recording indicator's five level bars: a 4 DIP floor in silence, and above it the icon's proportions of the other
/// 12 DIP, so every bar moves with the voice.
/// </summary>
public sealed class PillLevelBarsTests
{
    private const double BarDip = 16;

    [Fact]
    public void In_silence_every_bar_rests_on_its_4_DIP_floor()
    {
        Assert.Equal(0.25, PillLevelBars.Floor);
        for (var bar = 0; bar < PillLevelBars.Count; bar++)
        {
            Assert.Equal(4, PillLevelBars.ScaleOf(bar, 0) * BarDip, 9);
        }
    }

    [Fact]
    public void At_full_level_the_bars_rise_above_the_floor_in_the_icon_s_proportions()
    {
        double[] expected = [7.12, 10.72, 16, 10.72, 7.12];
        for (var bar = 0; bar < PillLevelBars.Count; bar++)
        {
            Assert.Equal(expected[bar], PillLevelBars.ScaleOf(bar, 1) * BarDip, 9);
            var above = (PillLevelBars.ScaleOf(bar, 1) - PillLevelBars.Floor) / (1 - PillLevelBars.Floor);
            Assert.Equal(PillLevelBars.Proportion(bar), above, 12);
        }

        Assert.Equal([0.26, 0.56, 1.0, 0.56, 0.26], Enumerable.Range(0, PillLevelBars.Count).Select(PillLevelBars.Proportion));
    }

    [Fact]
    public void Every_bar_moves_at_least_3_DIP_between_silence_and_full_level()
    {
        // In 0.5.0 the outer bars could rise from 4 to 4.16 DIP, so they never moved; the neighbours needed half the level.
        for (var bar = 0; bar < PillLevelBars.Count; bar++)
        {
            var travel = (PillLevelBars.ScaleOf(bar, 1) - PillLevelBars.ScaleOf(bar, 0)) * BarDip;
            Assert.True(travel >= 3, $"bar {bar} travels {travel:F2} DIP");
        }
    }

    [Fact]
    public void The_bars_are_symmetric_and_rise_with_the_level()
    {
        for (var bar = 0; bar < PillLevelBars.Count; bar++)
        {
            var previous = double.MinValue;
            for (var level = 0.0; level <= 1.0; level += 0.05)
            {
                var scale = PillLevelBars.ScaleOf(bar, level);
                Assert.Equal(PillLevelBars.ScaleOf(PillLevelBars.Count - 1 - bar, level), scale, 12);
                Assert.True(scale >= previous);
                previous = scale;
            }
        }
    }

    [Theory]
    [InlineData(-3.0, 0.25)]
    [InlineData(double.NaN, 0.25)]
    [InlineData(double.NegativeInfinity, 0.25)]
    [InlineData(7.0, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void A_level_outside_0_to_1_is_held_to_the_nearer_end(double level, double centreScale)
    {
        Assert.Equal(centreScale, PillLevelBars.ScaleOf(2, level), 12);
    }
}
