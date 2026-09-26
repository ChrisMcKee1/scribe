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

    [Theory]
    [InlineData(473.3, 1.5, 109.8, 28, 146.8, true)]
    [InlineData(760, 1.5, 109.8, 28, 146.8, false)]
    public void UseCompact_is_a_fixed_point_when_compact_moves_notice_and_hint_into_the_scroller(
        double availableHeight,
        double scale,
        double noticeHeight,
        double hintHeight,
        double toolbarHeight,
        bool expected)
    {
        var expanded = new ProfileLayoutInput(availableHeight, scale, noticeHeight, hintHeight, toolbarHeight);
        var compact = expanded with { NoticeHeight = noticeHeight, HintHeight = hintHeight };

        Assert.Equal(expected, ProfileLayoutPlanner.UseCompact(expanded));
        Assert.Equal(expected, ProfileLayoutPlanner.UseCompact(compact));
    }
}
