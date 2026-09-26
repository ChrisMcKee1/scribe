using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class SettingRowLayoutTests
{
    [Theory]
    [InlineData(600, 1, false)]
    [InlineData(575, 1, true)]
    [InlineData(820, 1.75, false)]
    [InlineData(800, 1.75, true)]
    public void StackControl_follows_the_measured_width_and_text_scale(double width, double scale, bool expected)
    {
        var input = new SettingRowLayoutInput(width, scale, ControlMinimumWidth: 240);

        Assert.Equal(expected, SettingRowLayout.StackControl(input));
    }
}
