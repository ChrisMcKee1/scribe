using Scribe.Core.Appearance;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class ContrastShadeTests
{
    private static SrgbColor C(string hex) => SrgbColor.Parse(hex);

    // The surfaces accent text and switches are drawn on in WPF-UI 4.3.0: the page (Mica's fallback), a window without
    // Mica, a card and a filled row, the translucent ones over the page.
    private static readonly SrgbColor[] DarkSurfaces = [C("#202020"), C("#2B2B2B"), C("#2D2D2D")];
    private static readonly SrgbColor[] LightSurfaces = [C("#F3F3F3"), C("#FAFAFA"), C("#FBFBFB")];

    [Fact]
    public void A_colour_that_reads_on_every_surface_is_kept_exactly()
    {
        // Windows' default blue as WPF-UI's dark-theme accent text.
        var result = ContrastShade.Ensure(C("#73C2FF"), DarkSurfaces, WcagContrast.TextMinimum, lighter: true);

        Assert.False(result.Changed);
        Assert.Equal(C("#73C2FF"), result.Color);
        Assert.Equal(result.OriginalRatio, result.Ratio);
    }

    [Fact]
    public void Accent_text_too_dark_for_the_dark_theme_is_lightened_until_it_reads_on_every_surface()
    {
        // WPF-UI's accent text for the maintainer's #0E0E70: 2.58:1 on the page and 2.18:1 on a filled row.
        var result = ContrastShade.Ensure(C("#59599B"), DarkSurfaces, WcagContrast.TextMinimum, lighter: true);

        Assert.Equal(2.58, Math.Round(WcagContrast.Ratio(C("#59599B"), C("#202020")), 2));
        Assert.Equal(2.18, Math.Round(result.OriginalRatio, 2));
        Assert.True(result.Changed);
        Assert.Equal(C("#9090BF"), result.Color);
        Assert.All(DarkSurfaces, surface => Assert.True(WcagContrast.Ratio(result.Color, surface) >= WcagContrast.TextMinimum));
    }

    [Fact]
    public void Accent_text_too_light_for_the_light_theme_is_darkened_until_it_reads_on_every_surface()
    {
        // WPF-UI's light-theme accent text for Windows' Gold, #FFB900: 2.74:1 on the page.
        var result = ContrastShade.Ensure(C("#BF8B00"), LightSurfaces, WcagContrast.TextMinimum, lighter: false);

        Assert.Equal(2.74, Math.Round(result.OriginalRatio, 2));
        Assert.Equal(C("#8F6800"), result.Color);
        Assert.All(LightSurfaces, surface => Assert.True(WcagContrast.Ratio(result.Color, surface) >= WcagContrast.TextMinimum));
    }

    [Theory]
    [InlineData("#59599B", false, 4.5)]
    [InlineData("#42429B", false, 4.5)]
    [InlineData("#78789B", false, 4.5)]
    [InlineData("#0066CC", false, 4.5)]
    [InlineData("#FF0000", false, 4.5)]
    [InlineData("#42429B", false, 3.0)]
    [InlineData("#BF8B00", true, 4.5)]
    [InlineData("#E6A700", true, 4.5)]
    [InlineData("#FF0000", true, 4.5)]
    [InlineData("#E6A700", true, 3.0)]
    public void Only_the_lightness_changes_and_by_the_smallest_step_that_reads(string original, bool light, double required)
    {
        var surfaces = light ? LightSurfaces : DarkSurfaces;
        var result = ContrastShade.Ensure(C(original), surfaces, required, lighter: !light);
        var (hue, saturation, lightness) = C(original).ToHsl();
        var (newHue, newSaturation, newLightness) = result.Color.ToHsl();

        Assert.True(result.Changed);
        Assert.True(result.Meets(required), $"{result.Color} at {result.Ratio:F2}:1");
        Assert.True(Math.Abs(newHue - hue) < 3, $"hue {hue:F1} became {newHue:F1}");
        Assert.True(Math.Abs(newSaturation - saturation) < 0.05, $"saturation {saturation:F3} became {newSaturation:F3}");
        Assert.True(light ? newLightness < lightness : newLightness > lightness);

        // A hundredth of the lightness less far from the original no longer reads: nothing was changed more than needed.
        var nearer = SrgbColor.FromHsl(hue, saturation, light ? newLightness + 0.01 : newLightness - 0.01);
        Assert.True(ContrastShade.Lowest(nearer, surfaces) < required, $"{nearer} already read");
    }

    [Fact]
    public void A_translucent_colour_is_measured_as_it_shows_on_each_surface_and_stays_translucent_when_it_reads()
    {
        // The light theme's hovered switch track with Windows' default blue: the accent fill at alpha 229.
        var result = ContrastShade.Ensure(C("#E5006ABB"), LightSurfaces, WcagContrast.NonTextMinimum, lighter: false);

        Assert.False(result.Changed);
        Assert.Equal(C("#E5006ABB"), result.Color);
        Assert.Equal(
            LightSurfaces.Min(surface => WcagContrast.Ratio(C("#E5006ABB").Over(surface), surface)),
            result.Ratio);
    }

    [Fact]
    public void A_translucent_colour_that_does_not_read_is_replaced_by_an_opaque_shade()
    {
        // The dark theme's hovered switch track with #0E0E70, 2.03:1 as it shows on a filled row.
        var result = ContrastShade.Ensure(C("#E559599B"), DarkSurfaces, WcagContrast.NonTextMinimum, lighter: true);

        Assert.Equal(2.03, Math.Round(result.OriginalRatio, 2));
        Assert.True(result.Color.IsOpaque);
        Assert.True(result.Ratio >= WcagContrast.NonTextMinimum);
    }

    [Fact]
    public void Where_no_shade_can_read_the_extreme_is_the_best_there_is()
    {
        var result = ContrastShade.Ensure(C("#808080"), [SrgbColor.Black, SrgbColor.White], WcagContrast.TextMinimum, lighter: true);

        Assert.Equal(SrgbColor.White, result.Color);
        Assert.Equal(1, result.Ratio);
    }

    [Fact]
    public void Inputs_it_cannot_measure_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ContrastShade.Ensure(SrgbColor.White, null!, 4.5, true));
        Assert.Throws<ArgumentException>(() => ContrastShade.Ensure(SrgbColor.White, [], 4.5, true));
        Assert.Throws<ArgumentException>(() => ContrastShade.Ensure(SrgbColor.White, [C("#80202020")], 4.5, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContrastShade.Ensure(SrgbColor.White, DarkSurfaces, 1, true));
    }
}

internal static class ShadeResultExtensions
{
    public static bool Meets(this ShadeResult result, double required) => result.Ratio >= required;
}
