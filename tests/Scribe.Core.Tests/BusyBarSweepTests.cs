using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>
/// StopInactiveProgress keeps WPF's own sweep for the Dictionary busy bars: <see cref="BusyBarSweep"/> is
/// <c>ProgressBar.UpdateAnimation</c>'s arithmetic (dotnet/wpf release/10.0, MIT), which reads the indicator's and glow's
/// Width properties and the glow's current left margin. These are the values that code gives.
/// </summary>
public sealed class BusyBarSweepTests
{
    // The two bars are 72 DIP wide; WPF-UI's track and indicator have one DIP of margin each side, so ProgressBar sets the
    // indicator's Width to the track's 70 DIP, and the glow is 200 DIP wide with a base margin of 0.
    private const double Indicator = 70;
    private const double Glow = 200;

    [Fact]
    public void The_Dictionary_bar_on_its_first_show_travels_from_minus_200_to_270_in_2_35_s_then_pauses_1_s_starting_1_s_in()
    {
        var sweep = BusyBarSweep.For(Indicator, Glow, glowLeft: 0);

        Assert.NotNull(sweep);
        Assert.Equal(-200, sweep.Value.StartLeft);
        Assert.Equal(270, sweep.Value.EndLeft);
        Assert.Equal(TimeSpan.FromSeconds(2.35), sweep.Value.Travel);
        Assert.Equal(TimeSpan.FromSeconds(1), sweep.Value.Pause);
        Assert.Equal(TimeSpan.FromSeconds(3.35), sweep.Value.Duration);

        // The base margin, 0, is already 200 DIP into the travel: WPF begins the timeline 1 s back.
        Assert.Equal(TimeSpan.FromSeconds(-1), sweep.Value.BeginTime);
    }

    [Theory]
    [InlineData(-200, 0)] // hidden on the left: not under way
    [InlineData(-300, 0)] // further left than the travel starts
    [InlineData(100, -1.5)] // 300 DIP into the travel
    [InlineData(268.9, -2.3445)] // just inside the one DIP of fudge on the right
    [InlineData(269, 0)] // end minus one: WPF treats it as finished
    [InlineData(270, 0)] // hidden on the right, in the pause
    [InlineData(400, 0)]
    public void A_restart_continues_from_where_the_glow_already_is(double glowLeft, double expectedBeginSeconds)
    {
        var sweep = BusyBarSweep.For(Indicator, Glow, glowLeft);

        Assert.NotNull(sweep);
        Assert.Equal(expectedBeginSeconds, sweep.Value.BeginTime.TotalSeconds, precision: 9);
    }

    [Fact]
    public void Margins_within_WPF_s_rounding_of_either_end_count_as_that_end()
    {
        // DoubleUtil.GreaterThan and LessThan are strict and exclude values AreClose to the bound (within about 1e-13 here).
        Assert.Equal(TimeSpan.Zero, BusyBarSweep.For(Indicator, Glow, -200 + 5e-14)!.Value.BeginTime);
        Assert.Equal(TimeSpan.Zero, BusyBarSweep.For(Indicator, Glow, 269 - 1e-13)!.Value.BeginTime);
        Assert.True(BusyBarSweep.For(Indicator, Glow, -200 + 0.001)!.Value.BeginTime < TimeSpan.Zero);
    }

    [Fact]
    public void The_travel_time_uses_the_distance_truncated_to_whole_DIP()
    {
        // (int)(270.9 + 200) = 470, so 2.35 s, as ProgressBar computes it.
        var sweep = BusyBarSweep.For(70.9, Glow, glowLeft: -200);

        Assert.Equal(TimeSpan.FromSeconds(2.35), sweep!.Value.Travel);
        Assert.Equal(270.9, sweep.Value.EndLeft, precision: 12);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(-1, 200)]
    [InlineData(double.NaN, 200)]
    [InlineData(70, 0)]
    [InlineData(70, -5)]
    [InlineData(70, double.NaN)]
    public void No_sweep_where_WPF_would_not_animate(double indicatorWidth, double glowWidth)
    {
        // An indicator ProgressBar has not sized yet has a Width of NaN.
        Assert.Null(BusyBarSweep.For(indicatorWidth, glowWidth, glowLeft: 0));
    }

    [Theory]
    [InlineData(70, 200, 0)]
    [InlineData(220, 200, 17.5)]
    [InlineData(1, 1, 0.25)]
    [InlineData(500, 50, -10)]
    public void Every_sweep_matches_the_formula_ProgressBar_uses(double indicatorWidth, double glowWidth, double glowLeft)
    {
        var sweep = BusyBarSweep.For(indicatorWidth, glowWidth, glowLeft)!.Value;

        var endPos = indicatorWidth + glowWidth;
        var startPos = -glowWidth;
        Assert.Equal(startPos, sweep.StartLeft);
        Assert.Equal(endPos, sweep.EndLeft);
        Assert.Equal(TimeSpan.FromSeconds((int)(endPos - startPos) / 200.0), sweep.Travel);
        var underWay = glowLeft > startPos && glowLeft < endPos - 1;
        Assert.Equal(underWay ? TimeSpan.FromSeconds(-(glowLeft - startPos) / 200.0) : TimeSpan.Zero, sweep.BeginTime);
    }
}
