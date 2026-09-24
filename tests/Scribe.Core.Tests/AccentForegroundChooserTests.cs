using Scribe.Core.Appearance;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class AccentForegroundChooserTests
{
    private static SrgbColor C(string hex) => SrgbColor.Parse(hex);

    private static AccentForegroundChoice Choose(string fill, SrgbColor themeForeground) =>
        AccentForegroundChooser.Choose([C(fill)], [], themeForeground);

    [Fact]
    public void The_theme_foreground_stays_where_it_is_legible()
    {
        // WPF-UI's dark-theme fill for Windows' default blue, with the dark theme's black text on it.
        var choice = Choose("#4DB2FF", SrgbColor.Black);

        Assert.Equal(SrgbColor.Black, choice.Foreground);
        Assert.True(choice.IsThemeForeground);
        Assert.True(choice.MeetsInEveryState);
    }

    [Fact]
    public void A_dark_accent_in_the_dark_theme_gets_white_instead_of_the_themes_black()
    {
        // The dark-theme fill WPF-UI makes of the maintainer's accent #0E0E70: black was 2.48:1 on it.
        var choice = Choose("#42429B", SrgbColor.Black);

        Assert.Equal(SrgbColor.White, choice.Foreground);
        Assert.False(choice.IsThemeForeground);
        Assert.Equal(8.45, Math.Round(choice.RestRatio, 2));
        Assert.True(choice.MeetsAtRest);
    }

    [Fact]
    public void A_light_accent_in_the_light_theme_gets_black_instead_of_the_themes_white()
    {
        // The light-theme fill WPF-UI makes of Windows' Gold accent #FFB900: white would be 2.12:1 on it.
        var choice = Choose("#E6A700", SrgbColor.White);

        Assert.Equal(SrgbColor.Black, choice.Foreground);
        Assert.False(choice.IsThemeForeground);
        Assert.Equal(9.89, Math.Round(choice.RestRatio, 2));
    }

    [Fact]
    public void Where_both_are_legible_the_theme_foreground_wins()
    {
        // #767676 takes both black (4.62:1) and white (4.54:1), so neither theme gives up its own pairing.
        Assert.Equal(SrgbColor.White, Choose("#767676", SrgbColor.White).Foreground);
        Assert.Equal(SrgbColor.Black, Choose("#767676", SrgbColor.Black).Foreground);
        Assert.True(Choose("#767676", SrgbColor.White).IsThemeForeground);
    }

    [Fact]
    public void Legible_in_every_state_beats_the_theme_foreground()
    {
        // White reads at rest (4.54:1) but not on the lighter hover fill (3.19:1); black reads on both.
        var choice = AccentForegroundChooser.Choose([C("#767676")], [C("#909090")], SrgbColor.White);

        Assert.Equal(SrgbColor.Black, choice.Foreground);
        Assert.True(choice.MeetsInEveryState);
    }

    [Fact]
    public void Legible_at_rest_beats_legible_only_in_passing()
    {
        // WPF-UI's light-theme fills for Windows' default blue: white reads at rest and hovered but not on the
        // pressed tone (3.96:1), and black would fail at rest (3.78:1), so white stays.
        var choice = AccentForegroundChooser.Choose([C("#006ABB")], [C("#1978C1"), C("#3185C6")], SrgbColor.White);

        Assert.Equal(SrgbColor.White, choice.Foreground);
        Assert.True(choice.MeetsAtRest);
        Assert.False(choice.MeetsInEveryState);
        Assert.Equal(3.96, Math.Round(choice.AllStatesRatio, 2));
    }

    [Fact]
    public void When_neither_is_legible_the_better_one_at_rest_is_chosen()
    {
        // One foreground over a dark and a light fill at once cannot read on both.
        var fills = new[] { C("#202020"), C("#F0F0F0") };

        var fromWhite = AccentForegroundChooser.Choose(fills, [], SrgbColor.White);
        var fromBlack = AccentForegroundChooser.Choose(fills, [], SrgbColor.Black);

        Assert.Equal(SrgbColor.Black, fromWhite.Foreground);
        Assert.Equal(SrgbColor.Black, fromBlack.Foreground);
        Assert.False(fromWhite.MeetsAtRest);
    }

    [Fact]
    public void Every_opaque_fill_gets_a_foreground_that_reaches_the_text_minimum()
    {
        // Black and white tie at 4.58:1 where a fill's relative luminance is about 0.179 and one of them does better
        // everywhere else, so there is always a legible choice; the theme's own is kept when it also reaches 4.5:1.
        var lowest = double.MaxValue;
        for (var r = 0; r <= 255; r += 15)
        {
            for (var g = 0; g <= 255; g += 15)
            {
                for (var b = 0; b <= 255; b += 15)
                {
                    var fill = SrgbColor.FromRgb((byte)r, (byte)g, (byte)b);
                    var best = Math.Max(WcagContrast.Ratio(SrgbColor.Black, fill), WcagContrast.Ratio(SrgbColor.White, fill));
                    Assert.True(best >= 4.58, $"{fill}: {best:F3}:1 at best");

                    foreach (var theme in new[] { SrgbColor.Black, SrgbColor.White })
                    {
                        var choice = AccentForegroundChooser.Choose([fill], [], theme);
                        Assert.True(choice.MeetsAtRest, $"{fill} with {theme} chose {choice.Foreground} at {choice.RestRatio:F2}:1");
                        lowest = Math.Min(lowest, choice.RestRatio);
                    }
                }
            }
        }

        Assert.True(lowest >= WcagContrast.TextMinimum, $"lowest {lowest:F3}:1");
    }

    [Fact]
    public void Inputs_it_cannot_measure_are_refused()
    {
        Assert.Throws<ArgumentException>(() => AccentForegroundChooser.Choose([], [], SrgbColor.White));
        Assert.Throws<ArgumentException>(() => AccentForegroundChooser.Choose([C("#E559599B")], [], SrgbColor.White));
        Assert.Throws<ArgumentException>(() => AccentForegroundChooser.Choose([C("#42429B")], [C("#80000000")], SrgbColor.White));
        Assert.Throws<ArgumentException>(() => AccentForegroundChooser.Choose([C("#42429B")], [], C("#1A1A1A")));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccentForegroundChooser.Choose([C("#42429B")], [], SrgbColor.White, 1));
        Assert.Throws<ArgumentNullException>(() => AccentForegroundChooser.Choose(null!, [], SrgbColor.White));
    }
}
