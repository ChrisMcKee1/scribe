using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class ProfileLayoutPlannerTests
{
    [Theory]
    [InlineData(460, 1, false)]
    [InlineData(300, 1, true)]
    [InlineData(900, 2.25, false)]
    [InlineData(520, 2.25, true)]
    public void UseCompact_when_the_profile_list_would_show_fewer_than_four_rows(
        double availableHeight,
        double scale,
        bool expected)
    {
        var input = new ProfileLayoutInput(
            availableHeight,
            scale,
            NoticeHeight: 80 * scale,
            HintHeight: 28 * scale,
            ToolbarHeight: 48 * scale);

        Assert.Equal(expected, ProfileLayoutPlanner.UseCompact(input));
    }
}
