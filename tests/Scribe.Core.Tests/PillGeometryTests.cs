using Scribe.Core.Models;
using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// The recording pill's size and place as Windows text size scales it (WCAG 1.4.4): the whole pill scales as one unit by
/// the text scale, clamped to 1 to 2.25, and every anchor keeps it inside the monitor's work area. The overlay compiles
/// <see cref="PillGeometry"/> itself (a linked source file), so what these tests hold is what the pill does.
/// </summary>
public sealed class PillGeometryTests
{
    // A 1920 x 1080 monitor at 100% with a 48 pixel taskbar, and a 2560 x 1440 one at 125% with a 60 pixel taskbar.
    private static readonly PillRect FullHd = new(0, 0, 1920, 1032);
    private static readonly PillRect Qhd = new(0, 0, 2560, 1380);

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(2.25, 2.25)]
    [InlineData(1.25, 1.25)]
    [InlineData(0.5, 1.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(-3.0, 1.0)]
    [InlineData(2.5, 2.25)]
    [InlineData(10.0, 2.25)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 1.0)]
    public void The_text_scale_is_Windows_text_size_clamped_to_1_and_2_25_and_1_when_unreadable(double factor, double expected)
    {
        Assert.Equal(expected, PillGeometry.TextScale(factor));
    }

    [Theory]
    [InlineData(1.0, 264, 110)]
    [InlineData(1.5, 396, 165)]
    [InlineData(2.25, 594, 247.5)]
    public void The_window_is_the_100_percent_window_enlarged_by_the_text_scale(double textScale, double width, double height)
    {
        Assert.Equal((width, height), PillGeometry.SizeDip(textScale));
    }

    [Theory]
    [InlineData(1.0, 1.0, 264, 110)]
    [InlineData(1.0, 1.75, 462, 192)] // AGENTS.md's verification size: 100% text size at 175% DPI
    [InlineData(1.5, 1.0, 396, 165)]
    [InlineData(1.5, 1.25, 495, 206)]
    [InlineData(2.25, 1.0, 594, 248)] // the brief's 594 x 248 DIP window
    [InlineData(2.25, 1.25, 742, 309)]
    [InlineData(2.25, 1.5, 891, 371)]
    public void The_window_in_pixels_is_its_size_in_DIP_times_the_display_scale_rounded_as_before(
        double textScale, double dpiScale, int width, int height)
    {
        Assert.Equal((width, height), PillGeometry.SizePixels(textScale, dpiScale));
    }

    // The 100% place of every anchor, unchanged from before text scaling: 8 DIP from an edge, centred otherwise.
    public static TheoryData<PillAnchor, int, int> AnchorsAt100Percent => new()
    {
        { PillAnchor.TopLeft, 8, 8 },
        { PillAnchor.TopCenter, 828, 8 },
        { PillAnchor.TopRight, 1648, 8 },
        { PillAnchor.MiddleLeft, 8, 461 },
        { PillAnchor.Center, 828, 461 },
        { PillAnchor.MiddleRight, 1648, 461 },
        { PillAnchor.BottomLeft, 8, 914 },
        { PillAnchor.BottomCenter, 828, 914 },
        { PillAnchor.BottomRight, 1648, 914 },
    };

    [Theory]
    [MemberData(nameof(AnchorsAt100Percent))]
    public void Each_anchor_places_the_100_percent_pill_where_it_always_was(PillAnchor anchor, int x, int y)
    {
        Assert.Equal(new PillPlacement(x, y, 264, 110, Clamped: false), PillGeometry.Place(anchor, FullHd, 1.0, 1.0));
    }

    // At 150% text size and 125% DPI: a 495 x 206 pixel window, still 8 DIP (10 pixels) from an edge; the margin is not
    // text-scaled.
    public static TheoryData<PillAnchor, int, int> AnchorsAt150PercentText => new()
    {
        { PillAnchor.TopLeft, 10, 10 },
        { PillAnchor.TopCenter, 1032, 10 },
        { PillAnchor.TopRight, 2055, 10 },
        { PillAnchor.MiddleLeft, 10, 587 },
        { PillAnchor.Center, 1032, 587 },
        { PillAnchor.MiddleRight, 2055, 587 },
        { PillAnchor.BottomLeft, 10, 1164 },
        { PillAnchor.BottomCenter, 1032, 1164 },
        { PillAnchor.BottomRight, 2055, 1164 },
    };

    [Theory]
    [MemberData(nameof(AnchorsAt150PercentText))]
    public void Each_anchor_places_the_scaled_pill_by_its_scaled_size_and_the_unscaled_margin(PillAnchor anchor, int x, int y)
    {
        Assert.Equal(new PillPlacement(x, y, 495, 206, Clamped: false), PillGeometry.Place(anchor, Qhd, 1.5, 1.25));
    }

    [Theory]
    [InlineData(1.0, 720)] // a 48 pixel taskbar
    [InlineData(1.25, 708)] // a 60 pixel taskbar
    public void At_225_percent_text_size_every_anchor_keeps_the_pill_inside_a_1366_by_768_display(double dpiScale, int workHeight)
    {
        var work = new PillRect(0, 0, 1366, workHeight);
        foreach (var anchor in Enum.GetValues<PillAnchor>())
        {
            var place = PillGeometry.Place(anchor, work, 2.25, dpiScale);
            Assert.False(place.Clamped, $"{anchor} was clamped at {dpiScale:P0}.");
            AssertInside(place, work);
        }
    }

    [Fact]
    public void A_work_area_off_the_origin_places_the_pill_on_that_monitor()
    {
        // A 1366 x 768 monitor to the left of a 1080 pixel primary, bottom-aligned with it.
        var work = new PillRect(-1366, 312, 1366, 720);
        foreach (var anchor in Enum.GetValues<PillAnchor>())
        {
            AssertInside(PillGeometry.Place(anchor, work, 2.25, 1.0), work);
        }

        Assert.Equal(new PillPlacement(-1358, 320, 594, 248, false), PillGeometry.Place(PillAnchor.TopLeft, work, 2.25, 1.0));
        Assert.Equal(new PillPlacement(-980, 548, 594, 248, false), PillGeometry.Place(PillAnchor.Center, work, 2.25, 1.0));
        Assert.Equal(new PillPlacement(-602, 776, 594, 248, false), PillGeometry.Place(PillAnchor.BottomRight, work, 2.25, 1.0));
    }

    [Fact]
    public void A_pill_larger_than_the_work_area_keeps_its_top_left_corner_in_it_and_says_it_was_clamped()
    {
        var work = new PillRect(100, 50, 300, 200);
        foreach (var anchor in Enum.GetValues<PillAnchor>())
        {
            Assert.Equal(new PillPlacement(100, 50, 594, 248, Clamped: true), PillGeometry.Place(anchor, work, 2.25, 1.0));
        }
    }

    [Fact]
    public void A_pill_that_fits_only_without_its_margin_is_moved_inside_at_an_edge_and_left_alone_when_centred()
    {
        // 599 pixels wide: the 594 pixel window fits, its 8 pixel margin does not.
        var work = new PillRect(0, 0, 599, 720);
        foreach (var anchor in Enum.GetValues<PillAnchor>())
        {
            var place = PillGeometry.Place(anchor, work, 2.25, 1.0);
            AssertInside(place, work);
            var centred = anchor is PillAnchor.TopCenter or PillAnchor.Center or PillAnchor.BottomCenter;
            Assert.Equal(!centred, place.Clamped);
        }

        Assert.Equal(5, PillGeometry.Place(PillAnchor.TopLeft, work, 2.25, 1.0).X);
        Assert.Equal(0, PillGeometry.Place(PillAnchor.TopRight, work, 2.25, 1.0).X);
    }

    [Fact]
    public void A_pill_clamped_only_up_and_down_says_so_too()
    {
        // 250 pixels high: the 248 pixel window fits, its margin above or below does not; left and right are untouched.
        var work = new PillRect(0, 0, 1920, 250);
        Assert.Equal(new PillPlacement(663, 2, 594, 248, Clamped: true), PillGeometry.Place(PillAnchor.TopCenter, work, 2.25, 1.0));
        Assert.Equal(new PillPlacement(663, 1, 594, 248, Clamped: false), PillGeometry.Place(PillAnchor.Center, work, 2.25, 1.0));
        Assert.Equal(new PillPlacement(663, 0, 594, 248, Clamped: true), PillGeometry.Place(PillAnchor.BottomCenter, work, 2.25, 1.0));
    }

    [Fact]
    public void The_anchors_are_the_engine_s_positions_by_name_and_order()
    {
        // The overlay converts its OverlayAnchor by value; OverlayPipeProtocolTests holds that one to OverlayPosition too.
        Assert.Equal(Enum.GetNames<OverlayPosition>(), Enum.GetNames<PillAnchor>());
    }

    private static void AssertInside(PillPlacement place, PillRect work)
    {
        Assert.InRange(place.X, work.X, work.X + work.Width - place.Width);
        Assert.InRange(place.Y, work.Y, work.Y + work.Height - place.Height);
    }
}
