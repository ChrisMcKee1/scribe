using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-9: the layout planner at the minimum window (940 x 660), at the three work areas of plan 3.11 (1920 x 1080 at 175%,
/// 1366 x 768 at 125% and 1920 x 1080 at 200%) and at several text scales.
/// </summary>
public sealed class LibraryLayoutPlannerTests
{
    [Fact]
    public void D9_the_minimum_window_is_side_by_side_not_short_with_164_dips_per_text_column()
    {
        var layout = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 660));

        Assert.Equal(LibraryLayoutComposition.SideBySide, layout.Composition);
        Assert.Equal(164, layout.SpokenColumnWidth);
        Assert.Equal(layout.SpokenColumnWidth, layout.WrittenColumnWidth);
        Assert.Equal(220, layout.ListWidth);
        Assert.Equal(6, layout.VisibleTermRows);
        Assert.False(layout.HorizontalOverflow);
        Assert.False(layout.VerticalOverflow);
    }

    [Theory]
    [InlineData(1097, 569, true, 5)]
    [InlineData(1092, 566, true, 5)]
    [InlineData(960, 492, true, 4)]
    public void D9_each_short_work_area_stays_side_by_side_in_the_short_composition_with_at_least_four_rows(
        double width, double height, bool sideBySide, int rows)
    {
        var layout = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(width, height));

        Assert.Equal(sideBySide, layout.SideBySide);
        Assert.True(layout.Short);
        Assert.Equal(rows, layout.VisibleTermRows);
        Assert.True(layout.SpokenColumnWidth >= LibraryLayoutPlanner.MinimumTextColumn);
        Assert.Equal(height == 492, layout.VerticalOverflow);
    }

    [Theory]
    [InlineData(940, 660, 1.0, true)]
    [InlineData(940, 660, 1.25, false)]
    [InlineData(940, 660, 1.5, false)]
    [InlineData(1097, 569, 1.25, true)]
    [InlineData(1097, 569, 1.5, false)]
    [InlineData(1920, 1040, 2.0, true)]
    [InlineData(1920, 1040, 2.25, true)]
    [InlineData(1280, 900, 2.25, false)]
    public void D9_larger_text_keeps_the_columns_their_minimum_and_stacks_when_it_cannot(
        double width, double height, double scale, bool sideBySide)
    {
        var layout = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(width, height, scale));

        Assert.Equal(sideBySide, layout.SideBySide);
        Assert.Equal(!sideBySide, layout.Composition.HasFlag(LibraryLayoutComposition.Stacked));
        if (!layout.HorizontalOverflow)
        {
            Assert.True(layout.SpokenColumnWidth >= LibraryLayoutPlanner.MinimumTextColumn * scale);
        }

        Assert.True(layout.VisibleTermRows >= LibraryLayoutPlanner.MinimumRows);
        Assert.Equal(40 * scale, layout.UseColumnWidth);
        Assert.Equal(40 * scale, layout.ActionColumnWidth);
    }

    [Fact]
    public void D9_the_widest_text_at_the_minimum_window_overflows_sideways_rather_than_squeezing_the_columns()
    {
        var layout = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 660, 2.25));

        Assert.Equal(LibraryLayoutComposition.Stacked | LibraryLayoutComposition.Short, layout.Composition);
        Assert.True(layout.HorizontalOverflow);
        Assert.Equal(LibraryLayoutPlanner.MinimumTextColumn * 2.25, layout.SpokenColumnWidth);
        Assert.Equal(940 - 232 - 36, layout.ListWidth);
    }

    [Fact]
    public void D9_a_notice_or_open_term_details_that_leave_too_few_rows_switch_to_the_short_composition()
    {
        var plain = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 660));
        var notice = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 660, NoticeVisible: true));
        var details = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 660, TermDetailsOpen: true));
        var tall = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(1240, 900, TermDetailsOpen: true));

        Assert.False(plain.Short);
        Assert.True(notice.Short);
        Assert.True(notice.VisibleTermRows >= LibraryLayoutPlanner.MinimumRows);
        Assert.True(details.Short);
        Assert.False(tall.Short);
        Assert.True(tall.VisibleTermRows >= LibraryLayoutPlanner.MinimumNormalRows);
    }

    [Fact]
    public void D9_the_planner_is_monotonic_more_room_never_gives_fewer_rows_or_narrower_columns()
    {
        for (var width = 900; width <= 1600; width += 50)
        {
            for (var height = 480; height <= 1000; height += 40)
            {
                var layout = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(width, height));
                var wider = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(width + 50, height));
                var taller = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(width, height + 40));
                Assert.True(taller.VisibleTermRows >= layout.VisibleTermRows || (layout.Short && !taller.Short));
                Assert.True(!layout.SideBySide || wider.SideBySide);
                Assert.True(wider.SpokenColumnWidth >= layout.SpokenColumnWidth || (layout.Stacked() && wider.SideBySide));
            }
        }
    }
}

internal static class LayoutAssertions
{
    public static bool Stacked(this LibraryLayout layout) => layout.Composition.HasFlag(LibraryLayoutComposition.Stacked);
}
