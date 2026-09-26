using Scribe.Core.Settings;

namespace Scribe.Core.Tests.Libraries.Deciders;

/// <summary>
/// D-9: the layout planner at the minimum window (940 x 660), at the three work areas of plan 3.11 (1920 x 1080 at 175%,
/// 1366 x 768 at 125% and 1920 x 1080 at 200%) and at several text scales. Since round 7 the Word packs tab is the second
/// tab of the Dictionary page, so a tab strip of 40 DIPs times the text scale sits above the card at every one of them.
/// </summary>
public sealed class LibraryLayoutPlannerTests
{
    // Every window size the planner is checked at: the minimum window and the three short work areas.
    private static readonly (double Width, double Height)[] Targets = [(940, 660), (1097, 569), (1092, 566), (960, 492)];

    [Fact]
    public void D9_the_minimum_window_is_side_by_side_with_164_dips_per_text_column_and_short_under_the_tab_strip()
    {
        // Without the strip the normal composition had six rows here; the strip leaves it five, so the short one is used,
        // which shows seven.
        var layout = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 660));

        Assert.Equal(LibraryLayoutComposition.SideBySide | LibraryLayoutComposition.Short, layout.Composition);
        Assert.Equal(164, layout.SpokenColumnWidth);
        Assert.Equal(layout.SpokenColumnWidth, layout.WrittenColumnWidth);
        Assert.Equal(220, layout.ListWidth);
        Assert.Equal(7, layout.VisibleTermRows);
        Assert.False(layout.HorizontalOverflow);
        Assert.False(layout.VerticalOverflow);
    }

    [Theory]
    [InlineData(1097, 569, true, 4)]
    [InlineData(1092, 566, true, 4)]
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
        // 940 x 700 leaves the card what 940 x 660 left it before the tab strip: six rows, just not short.
        var plain = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 700));
        var notice = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 700, NoticeVisible: true));
        var details = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 700, TermDetailsOpen: true));
        var tall = LibraryLayoutPlanner.Plan(new LibraryLayoutInput(1240, 900, TermDetailsOpen: true));

        Assert.False(plain.Short);
        Assert.Equal(LibraryLayoutPlanner.MinimumNormalRows, plain.VisibleTermRows);
        Assert.True(notice.Short);
        Assert.True(notice.VisibleTermRows >= LibraryLayoutPlanner.MinimumRows);
        Assert.True(details.Short);
        Assert.False(tall.Short);
        Assert.True(tall.VisibleTermRows >= LibraryLayoutPlanner.MinimumNormalRows);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void D9_the_tab_strip_takes_exactly_its_height_from_the_card_at_every_target_size(double scale)
    {
        // The strip is 40 DIPs times the text scale, above the card, in both compositions, and nothing else moves: the card
        // gets exactly that much less than the same page without it.
        foreach (var (width, height) in Targets)
        {
            foreach (var isShort in new[] { false, true })
            {
                var input = new LibraryLayoutInput(width, height, scale);
                Assert.Equal(
                    40 * scale,
                    LibraryLayoutPlanner.CardHeight(input, isShort, tabStrip: 0) - LibraryLayoutPlanner.CardHeight(input, isShort));
            }
        }

        // Anchored to the window: at 940 x 660 the page above and below the card takes 232 DIPs at 100% text (192 in the
        // short composition, which has no subtitle) and 364 at 200%, and the strip 40 and 80 more.
        Assert.Equal(660 - 232 - 40, LibraryLayoutPlanner.CardHeight(new LibraryLayoutInput(940, 660), isShort: false));
        Assert.Equal(660 - 192 - 40, LibraryLayoutPlanner.CardHeight(new LibraryLayoutInput(940, 660), isShort: true));
        Assert.Equal(660 - 364 - 80, LibraryLayoutPlanner.CardHeight(new LibraryLayoutInput(940, 660, 2.0), isShort: false));
    }

    [Fact]
    public void D9_the_tab_strip_changes_a_fit_decision_only_where_its_height_makes_it()
    {
        // Every decision under the strip is the one the page without it makes in a window shorter by exactly the strip, and
        // nothing across the window moves: the rail stays 232 DIPs, the columns, the list and the side by side decision
        // are those of the same window without the strip. Checked at every target size, a range of other sizes, every text
        // scale the planner covers, and with a notice and Term details open or not.
        var sizes = Targets.Concat([(1280, 720), (1366, 768), (1600, 900), (1920, 1040), (1024, 600)]);
        var changed = 0;
        foreach (var (width, height) in sizes)
        {
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.25 })
            {
                foreach (var (notice, details) in new[] { (false, false), (true, false), (false, true), (true, true) })
                {
                    var input = new LibraryLayoutInput(width, height, scale, notice, details);
                    var layout = LibraryLayoutPlanner.Plan(input);
                    Assert.Equal(LibraryLayoutPlanner.Plan(input with { Height = height - 40 * scale }, tabStrip: 0), layout);

                    var without = LibraryLayoutPlanner.Plan(input, tabStrip: 0);
                    Assert.Equal(
                        (without.SideBySide, without.ListWidth, without.UseColumnWidth, without.SpokenColumnWidth,
                            without.WrittenColumnWidth, without.ActionColumnWidth, without.HorizontalOverflow),
                        (layout.SideBySide, layout.ListWidth, layout.UseColumnWidth, layout.SpokenColumnWidth,
                            layout.WrittenColumnWidth, layout.ActionColumnWidth, layout.HorizontalOverflow));
                    if (layout != without)
                    {
                        changed++;
                    }
                }
            }
        }

        // The strip does make decisions: at the target sizes it takes the minimum window into the short composition and a
        // row from each of the two larger work areas.
        Assert.True(changed > 0);
        Assert.Equal(LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 620), tabStrip: 0), LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 660)));
        Assert.False(LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 660), tabStrip: 0).Short);
        Assert.Equal(5, LibraryLayoutPlanner.Plan(new LibraryLayoutInput(1097, 569), tabStrip: 0).VisibleTermRows);
        Assert.Equal(940 - 232 - 36, LibraryLayoutPlanner.Plan(new LibraryLayoutInput(940, 660, 2.25)).ListWidth);
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
