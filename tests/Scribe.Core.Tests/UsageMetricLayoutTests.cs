using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class UsageMetricLayoutTests
{
    [Theory]
    [InlineData(900, 1, 6)]
    [InlineData(760, 1, 3)]
    [InlineData(460, 1, 3)]
    [InlineData(320, 1, 2)]
    [InlineData(900, 2.25, 3)]
    [InlineData(620, 2.25, 2)]
    public void Columns_keeps_metric_tiles_readable_at_the_current_text_scale(
        double availableWidth,
        double scale,
        int expected)
    {
        Assert.Equal(expected, UsageMetricLayout.Columns(availableWidth, scale));
    }
}
