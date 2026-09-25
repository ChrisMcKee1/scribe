using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class WindowFitTests
{
    [Theory]
    [InlineData(1280, 688, 1152, 660, 940, 660)]
    [InlineData(1366, 728, 1229.4, 669.76, 940, 660)]
    [InlineData(960, 540, 940, 540, 940, 540)]
    [InlineData(800, 480, 800, 480, 800, 480)]
    [InlineData(1920, 1032, 1240, 900, 940, 660)]
    [InlineData(3840, 2100, 1240, 900, 940, 660)]
    [InlineData(700, 400, 700, 400, 700, 400)]
    public void Compute_fits_the_plan_work_areas(
        double workWidth,
        double workHeight,
        double expectedWidth,
        double expectedHeight,
        double expectedMinWidth,
        double expectedMinHeight)
    {
        var fit = WindowFit.Compute(new WorkArea(0, 0, workWidth, workHeight));

        Assert.Equal(expectedWidth, fit.Width, precision: 2);
        Assert.Equal(expectedHeight, fit.Height, precision: 2);
        Assert.Equal(expectedMinWidth, fit.MinWidth, precision: 2);
        Assert.Equal(expectedMinHeight, fit.MinHeight, precision: 2);
        Assert.True(fit.Left >= 0);
        Assert.True(fit.Top >= 0);
        Assert.True(fit.Left + fit.Width <= workWidth + 0.01);
        Assert.True(fit.Top + fit.Height <= workHeight + 0.01);
    }

    [Fact]
    public void Compute_clamps_a_requested_position_inside_the_work_area()
    {
        var fit = WindowFit.Compute(
            WindowFit.DesiredWidth,
            WindowFit.DesiredHeight,
            WindowFit.MinimumWidth,
            WindowFit.MinimumHeight,
            new WorkArea(10, 20, 1280, 688),
            requestedLeft: -100,
            requestedTop: 9999);

        Assert.Equal(10, fit.Left);
        Assert.Equal(48, fit.Top, precision: 2);
    }

    [Theory]
    [InlineData(double.NaN, 600)]
    [InlineData(double.PositiveInfinity, 600)]
    [InlineData(800, double.NaN)]
    [InlineData(800, double.PositiveInfinity)]
    public void Compute_rejects_non_finite_work_area_dimensions(double width, double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowFit.Compute(new WorkArea(0, 0, width, height)));
    }
}
