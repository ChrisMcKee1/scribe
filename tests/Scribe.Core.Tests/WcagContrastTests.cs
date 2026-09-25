using Scribe.Core.Appearance;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class WcagContrastTests
{
    private static SrgbColor C(string hex) => SrgbColor.Parse(hex);

    [Fact]
    public void Relative_luminance_runs_from_black_to_white()
    {
        Assert.Equal(0, WcagContrast.RelativeLuminance(SrgbColor.Black), 12);
        Assert.Equal(1, WcagContrast.RelativeLuminance(SrgbColor.White), 12);
    }

    [Fact]
    public void Black_and_white_are_21_to_1_either_way_round()
    {
        Assert.Equal(21, WcagContrast.Ratio(SrgbColor.Black, SrgbColor.White), 12);
        Assert.Equal(21, WcagContrast.Ratio(SrgbColor.White, SrgbColor.Black), 12);
        Assert.Equal(1, WcagContrast.Ratio(C("#42429B"), C("#42429B")), 12);
    }

    [Theory]
    // The grey W3C's Understanding pages use in their passing examples: the lightest that reaches 4.5:1 on white.
    [InlineData("#767676", "#FFFFFF", 4.54)]
    [InlineData("#777777", "#FFFFFF", 4.48)]
    // The pixels the offscreen renders measured before this change: the dark accent fill with its black text,
    // and the light theme's badges with their white text.
    [InlineData("#000000", "#42429B", 2.48)]
    [InlineData("#000000", "#59599B", 3.33)]
    [InlineData("#FFFFFF", "#FF9800", 2.16)]
    [InlineData("#FFFFFF", "#03A9F4", 2.63)]
    [InlineData("#000000", "#FF9800", 9.74)]
    [InlineData("#000000", "#03A9F4", 7.99)]
    // What the fix draws instead, and the library rows' selection stroke as it is drawn over the selected fill.
    [InlineData("#FFFFFF", "#42429B", 8.45)]
    [InlineData("#A0A0A0", "#2D2D2D", 5.27)]
    [InlineData("#828282", "#EAEAEA", 3.19)]
    public void Known_pairs_measure_as_published(string first, string second, double expected)
    {
        Assert.Equal(expected, Math.Round(WcagContrast.Ratio(C(first), C(second)), 2));
    }

    [Fact]
    public void The_transfer_function_switches_branch_at_the_threshold_WCAG_2_2_defines()
    {
        // 10/255 is at or below 0.04045 (the linear branch), 11/255 above it (the power curve).
        Assert.Equal(10 / 255.0 / 12.92, WcagContrast.RelativeLuminance(SrgbColor.FromRgb(10, 10, 10)), 15);
        Assert.Equal(Math.Pow(((11 / 255.0) + 0.055) / 1.055, 2.4),
            WcagContrast.RelativeLuminance(SrgbColor.FromRgb(11, 11, 11)), 15);
    }

    [Fact]
    public void Each_channel_is_weighted_as_WCAG_2_2_weights_it()
    {
        Assert.Equal(0.2126, WcagContrast.RelativeLuminance(SrgbColor.FromRgb(255, 0, 0)), 12);
        Assert.Equal(0.7152, WcagContrast.RelativeLuminance(SrgbColor.FromRgb(0, 255, 0)), 12);
        Assert.Equal(0.0722, WcagContrast.RelativeLuminance(SrgbColor.FromRgb(0, 0, 255)), 12);
    }

    [Fact]
    public void Alpha_is_ignored_so_a_translucent_colour_is_composited_first()
    {
        Assert.Equal(
            WcagContrast.Ratio(C("#42429B"), SrgbColor.White),
            WcagContrast.Ratio(C("#1042429B"), SrgbColor.White),
            12);
    }
}

public sealed class SrgbColorTests
{
    [Fact]
    public void A_translucent_colour_composites_over_an_opaque_one_as_WPF_blends_it()
    {
        // AccentFillColorSecondary (the dark accent fill at alpha 229) over the dark page: what a hovered button shows.
        Assert.Equal(SrgbColor.Parse("#53538E"), SrgbColor.Parse("#E559599B").Over(SrgbColor.Parse("#202020")));
        Assert.Equal(SrgbColor.Parse("#4D4D82"), SrgbColor.Parse("#CC59599B").Over(SrgbColor.Parse("#202020")));

        // The pressed accent fill of #0E0E70 in the light theme, and a danger button pressed in the dark one, as WPF's
        // own renders measure them.
        Assert.Equal(SrgbColor.Parse("#3A3A77"), SrgbColor.Parse("#CC0B0B57").Over(SrgbColor.Parse("#F3F3F3")));
        Assert.Equal(SrgbColor.Parse("#B53930"), SrgbColor.Parse("#F44336").WithOpacity(0.7).Over(SrgbColor.Parse("#202020")));
    }

    [Fact]
    public void An_opaque_colour_is_itself_over_anything()
    {
        var accent = SrgbColor.Parse("#42429B");

        Assert.Equal(accent, accent.Over(SrgbColor.White));
    }

    [Fact]
    public void A_translucent_background_is_refused()
    {
        Assert.Throws<ArgumentException>(() => SrgbColor.White.Over(SrgbColor.Parse("#80000000")));
    }

    [Fact]
    public void Opacity_scales_the_alpha_the_way_a_brush_does()
    {
        Assert.Equal(230, SrgbColor.Parse("#59599B").WithOpacity(0.9).A);
        Assert.Equal(0, SrgbColor.White.WithOpacity(-1).A);
        Assert.Equal(255, SrgbColor.White.WithOpacity(double.NaN).A);
        Assert.Equal(0x80, SrgbColor.Black.WithAlpha(0x80).A);
    }

    [Fact]
    public void Brush_opacity_0_9_and_alpha_229_are_different_fills()
    {
        // The About mark is AccentFillColorSecondaryBrush, the accent fill with brush opacity 0.9, which WPF draws as
        // #53538F over the dark page; a hovered accent button is AccentFillColorSecondary, alpha 229, #53538E.
        var page = SrgbColor.Parse("#202020");

        Assert.Equal(SrgbColor.Parse("#53538F"), SrgbColor.Parse("#59599B").WithOpacity(0.9).Over(page));
        Assert.Equal(SrgbColor.Parse("#53538E"), SrgbColor.Parse("#59599B").WithAlpha(229).Over(page));
    }

    [Theory]
    [InlineData("#FF0000", 0, 1, 0.5)]
    [InlineData("#0066CC", 210, 1, 0.4)]
    [InlineData("#808080", 0, 0, 0.50196)]
    [InlineData("#42429B", 240, 0.40271, 0.43333)]
    [InlineData("#E6A700", 43.565, 1, 0.45098)]
    public void Known_colours_have_the_HSL_CSS_gives_them(string hex, double hue, double saturation, double lightness)
    {
        var (h, s, l) = SrgbColor.Parse(hex).ToHsl();

        Assert.Equal(hue, h, 3);
        Assert.Equal(saturation, s, 4);
        Assert.Equal(lightness, l, 4);
    }

    [Fact]
    public void Hsl_round_trips_every_colour_on_a_grid()
    {
        for (var r = 0; r <= 255; r += 17)
        {
            for (var g = 0; g <= 255; g += 17)
            {
                for (var b = 0; b <= 255; b += 17)
                {
                    var color = SrgbColor.FromRgb((byte)r, (byte)g, (byte)b);
                    var (h, s, l) = color.ToHsl();
                    Assert.Equal(color, SrgbColor.FromHsl(h, s, l));
                }
            }
        }
    }

    [Fact]
    public void From_hsl_keeps_the_alpha_it_is_given_and_clamps_the_rest()
    {
        Assert.Equal(SrgbColor.Parse("#80FF0000"), SrgbColor.FromHsl(360, 1, 0.5, 0x80));
        Assert.Equal(SrgbColor.White, SrgbColor.FromHsl(120, 2, 1.5));
        Assert.Equal(SrgbColor.Black, SrgbColor.FromHsl(-30, -1, -0.5));
    }

    [Theory]
    [InlineData("#42429B", 255, 0x42, 0x42, 0x9B)]
    [InlineData("#B3FFFFFF", 0xB3, 255, 255, 255)]
    [InlineData("#80000000", 0x80, 0, 0, 0)]
    public void Hex_text_round_trips(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(SrgbColor.TryParse(text, out var color));
        Assert.Equal(new SrgbColor(a, r, g, b), color);
        Assert.Equal(text, color.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("42429B")]
    [InlineData("#42429")]
    [InlineData("#GG429B")]
    [InlineData("#FF42429B00")]
    public void Anything_else_is_not_a_colour(string? text)
    {
        Assert.False(SrgbColor.TryParse(text, out _));
    }
}
