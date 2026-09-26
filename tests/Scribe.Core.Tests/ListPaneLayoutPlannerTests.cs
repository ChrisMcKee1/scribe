using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class ListPaneLayoutPlannerTests
{
    [Theory]
    [InlineData(900, 1.0, 270)]
    [InlineData(900, 2.25, 450)]
    [InlineData(1240, 2.25, 607.5)]
    public void List_width_scales_until_the_editor_would_get_less_than_half(
        double contentWidth,
        double scale,
        double expected)
    {
        var width = ListPaneLayoutPlanner.ListWidth(new ListPaneLayoutInput(contentWidth, scale));

        Assert.Equal(expected, width);
    }
}
