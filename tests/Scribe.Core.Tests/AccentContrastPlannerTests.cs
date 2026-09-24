using Scribe.Core.Appearance;
using Xunit;

namespace Scribe.Core.Tests;

public sealed class AccentContrastPlannerTests
{
    private static readonly AccentForegroundRole[] AccentRoles =
    [
        AccentForegroundRole.AccentButton,
        AccentForegroundRole.AccentFill,
        AccentForegroundRole.SelectedItem,
        AccentForegroundRole.CheckedToggleButton,
        AccentForegroundRole.CalendarToday,
        AccentForegroundRole.CheckGlyph,
        AccentForegroundRole.RadioGlyph,
        AccentForegroundRole.SwitchKnob,
    ];

    private static readonly AccentForegroundRole[] BadgeRoles =
    [
        AccentForegroundRole.CautionBadge,
        AccentForegroundRole.InfoBadge,
        AccentForegroundRole.DangerBadge,
        AccentForegroundRole.SuccessBadge,
    ];

    // What WPF-UI 4.3.0's accent manager writes for an accent (ApplicationAccentColorManager.Apply, as Scribe calls
    // it through ApplicationThemeManager.Apply): three shades, the theme's accent fill, and that fill at alpha 229
    // and 204 for hover and pressed. The offscreen renders read the same values back from the live resources.
    private static Dictionary<ThemeColor, SrgbColor> Theme(bool light, string primary, string secondary, string tertiary)
    {
        var fill = SrgbColor.Parse(light ? primary : secondary);
        return new Dictionary<ThemeColor, SrgbColor>
        {
            [ThemeColor.Surface] = SrgbColor.Parse(light ? "#F3F3F3" : "#202020"),
            [ThemeColor.AccentPrimary] = SrgbColor.Parse(primary),
            [ThemeColor.AccentSecondary] = SrgbColor.Parse(secondary),
            [ThemeColor.AccentTertiary] = SrgbColor.Parse(tertiary),
            [ThemeColor.AccentFill] = fill,
            [ThemeColor.AccentFillHover] = fill.WithAlpha(229),
            [ThemeColor.AccentFillPressed] = fill.WithAlpha(204),
            [ThemeColor.PaletteOrange] = SrgbColor.Parse("#FF9800"),
            [ThemeColor.PaletteLightBlue] = SrgbColor.Parse("#03A9F4"),
            [ThemeColor.PaletteRed] = SrgbColor.Parse("#F44336"),
            [ThemeColor.PaletteGreen] = SrgbColor.Parse("#4CAF50"),
        };
    }

    // The maintainer's accent, #0E0E70.
    private static Dictionary<ThemeColor, SrgbColor> NavyDark() => Theme(false, "#42429B", "#59599B", "#78789B");

    private static Dictionary<ThemeColor, SrgbColor> NavyLight() => Theme(true, "#0B0B57", "#060630", "#01010A");

    // A light accent: Windows' Gold, #FFB900.
    private static Dictionary<ThemeColor, SrgbColor> GoldDark() => Theme(false, "#FFCE4D", "#FFD973", "#FFE7A6");

    private static Dictionary<ThemeColor, SrgbColor> GoldLight() => Theme(true, "#E6A700", "#BF8B00", "#996F00");

    // Windows' default accent, #0078D4.
    private static Dictionary<ThemeColor, SrgbColor> BlueDark() => Theme(false, "#4DB2FF", "#73C2FF", "#A6D8FF");

    private static Dictionary<ThemeColor, SrgbColor> BlueLight() => Theme(true, "#006ABB", "#005494", "#003E6E");

    private static RoleForeground Role(AccentContrastPlan plan, AccentForegroundRole role) =>
        plan.For(role) ?? throw new Xunit.Sdk.XunitException($"no foreground planned for {role}");

    [Fact]
    public void A_dark_accent_in_the_dark_theme_gets_white_text_and_glyphs_on_every_accent_fill()
    {
        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, NavyDark());

        Assert.Equal(AccentContrastMode.Applied, plan.Mode);
        foreach (var role in AccentRoles)
        {
            var planned = Role(plan, role);
            Assert.Equal(SrgbColor.White, planned.Foreground);
            Assert.False(planned.Choice.IsThemeForeground);
            Assert.True(planned.Choice.MeetsInEveryState, $"{role} at {planned.Choice.AllStatesRatio:F2}:1");
        }

        // Save and the other accent buttons sit on the secondary shade, selected items and checked boxes on the primary.
        Assert.Equal(6.31, Math.Round(Role(plan, AccentForegroundRole.AccentButton).Choice.RestRatio, 2));
        Assert.Equal(8.45, Math.Round(Role(plan, AccentForegroundRole.SelectedItem).Choice.RestRatio, 2));
    }

    [Fact]
    public void A_dark_accent_in_the_light_theme_keeps_the_themes_white()
    {
        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Light, false, NavyLight());

        foreach (var role in AccentRoles)
        {
            var planned = Role(plan, role);
            Assert.Equal(SrgbColor.White, planned.Foreground);
            Assert.True(planned.Choice.IsThemeForeground);
            Assert.True(planned.Choice.MeetsInEveryState);
        }
    }

    [Fact]
    public void A_light_accent_in_the_light_theme_gets_black_on_every_accent_fill()
    {
        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Light, false, GoldLight());

        foreach (var role in AccentRoles)
        {
            var planned = Role(plan, role);
            Assert.Equal(SrgbColor.Black, planned.Foreground);
            Assert.False(planned.Choice.IsThemeForeground);
            Assert.True(planned.Choice.MeetsInEveryState, $"{role} at {planned.Choice.AllStatesRatio:F2}:1");
        }
    }

    [Fact]
    public void A_light_accent_in_the_dark_theme_keeps_the_themes_black()
    {
        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, GoldDark());

        foreach (var role in AccentRoles)
        {
            Assert.Equal(SrgbColor.Black, Role(plan, role).Foreground);
            Assert.True(Role(plan, role).Choice.IsThemeForeground);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_default_accent_keeps_the_theme_foreground_everywhere(bool light)
    {
        var plan = AccentContrastPlanner.Plan(light ? AppearanceTheme.Light : AppearanceTheme.Dark, false, light ? BlueLight() : BlueDark());

        foreach (var role in AccentRoles)
        {
            Assert.True(Role(plan, role).Choice.IsThemeForeground, role.ToString());
            Assert.True(Role(plan, role).Choice.MeetsAtRest, role.ToString());
        }
    }

    [Fact]
    public void Only_a_pressed_tone_the_theme_draws_itself_stays_below_the_minimum()
    {
        // WPF-UI's pressed accent button in the light theme with the default blue is 3.96:1 with its white text, and
        // black would fail at rest; the choice keeps the pairing that reads whenever the button is not being pressed.
        var button = Role(AccentContrastPlanner.Plan(AppearanceTheme.Light, false, BlueLight()), AccentForegroundRole.AccentButton);

        Assert.Equal(SrgbColor.White, button.Foreground);
        Assert.True(button.Choice.MeetsAtRest);
        Assert.False(button.Choice.MeetsInEveryState);
        Assert.Equal(3.96, Math.Round(button.Choice.AllStatesRatio, 2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Palette_badges_get_black_in_both_themes(bool light)
    {
        var plan = AccentContrastPlanner.Plan(light ? AppearanceTheme.Light : AppearanceTheme.Dark, false, light ? NavyLight() : NavyDark());

        foreach (var role in BadgeRoles)
        {
            var planned = Role(plan, role);
            Assert.Equal(SrgbColor.Black, planned.Foreground);
            Assert.True(planned.Choice.MeetsAtRest, $"{role} at {planned.Choice.RestRatio:F2}:1");

            // The light theme's white was the defect: 2.16:1 on the orange caution badge, 2.63:1 on the blue one.
            Assert.Equal(!light, planned.Choice.IsThemeForeground);
        }

        Assert.Equal(9.74, Math.Round(Role(plan, AccentForegroundRole.CautionBadge).Choice.RestRatio, 2));
        Assert.Equal(7.99, Math.Round(Role(plan, AccentForegroundRole.InfoBadge).Choice.RestRatio, 2));
    }

    [Fact]
    public void A_danger_button_in_the_dark_theme_gets_black_instead_of_white_on_the_red()
    {
        // White, the dark theme's button text, is 3.68:1 on the palette red; black is 5.70:1.
        var dark = Role(AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, NavyDark()), AccentForegroundRole.DangerButton);
        var light = Role(AccentContrastPlanner.Plan(AppearanceTheme.Light, false, NavyLight()), AccentForegroundRole.DangerButton);

        Assert.Equal(SrgbColor.Black, dark.Foreground);
        Assert.False(dark.Choice.IsThemeForeground);
        Assert.Equal(5.70, Math.Round(dark.Choice.RestRatio, 2));
        Assert.Equal(SrgbColor.Black, light.Foreground);
        Assert.True(light.Choice.IsThemeForeground);
    }

    [Fact]
    public void Hover_and_pressed_fills_are_measured_over_the_page_they_are_drawn_on()
    {
        // A checked box at rest is on the primary shade (8.45:1 with white); hovered it is the accent fill at alpha
        // 229 over the page, #53538E (7.01:1), not the opaque fill (6.31:1).
        var glyph = Role(AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, NavyDark()), AccentForegroundRole.CheckGlyph);

        Assert.Equal(8.45, Math.Round(glyph.Choice.RestRatio, 2));
        Assert.Equal(7.01, Math.Round(glyph.Choice.AllStatesRatio, 2));
    }

    [Fact]
    public void Without_a_readable_page_colour_the_themes_base_is_used()
    {
        var colours = NavyDark();
        var withPage = AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, colours);
        colours.Remove(ThemeColor.Surface);
        var withoutPage = AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, colours);

        Assert.Equal(withPage.Foregrounds, withoutPage.Foregrounds);
        Assert.Equal(withPage.SelectedItemFillRatio, withoutPage.SelectedItemFillRatio);
    }

    [Fact]
    public void Every_role_is_planned_once_when_every_colour_can_be_read()
    {
        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Light, false, GoldLight());

        Assert.Equal(AccentContrastPlanner.AllRoles.Count, plan.Foregrounds.Count);
        Assert.Equal(Enum.GetValues<AccentForegroundRole>().Order(), plan.Foregrounds.Select(f => f.Role).Order());
    }

    [Fact]
    public void A_role_whose_fill_cannot_be_read_is_left_to_the_theme()
    {
        var colours = NavyDark();
        colours.Remove(ThemeColor.PaletteOrange);
        colours.Remove(ThemeColor.AccentFillPressed);

        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, colours);

        Assert.Null(plan.For(AccentForegroundRole.CautionBadge));
        Assert.Null(plan.For(AccentForegroundRole.AccentButton));
        Assert.Null(plan.For(AccentForegroundRole.CheckGlyph));
        Assert.NotNull(plan.For(AccentForegroundRole.SelectedItem));
        Assert.NotNull(plan.For(AccentForegroundRole.InfoBadge));
    }

    [Theory]
    [InlineData(AppearanceTheme.HighContrast, false)]
    [InlineData(AppearanceTheme.Dark, true)]
    [InlineData(AppearanceTheme.Light, true)]
    public void A_contrast_theme_keeps_its_own_colours(AppearanceTheme theme, bool systemHighContrast)
    {
        // Windows can be in a contrast theme while WPF-UI still has the light or dark dictionary loaded.
        var plan = AccentContrastPlanner.Plan(theme, systemHighContrast, NavyDark());

        Assert.Equal(AccentContrastMode.ContrastTheme, plan.Mode);
        Assert.Empty(plan.Foregrounds);
        Assert.False(plan.SelectedRowCue);
        Assert.False(plan.SelectedItemWeightCue);
    }

    [Fact]
    public void An_unknown_theme_plans_nothing()
    {
        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Unknown, false, NavyDark());

        Assert.Equal(AccentContrastMode.UnknownTheme, plan.Mode);
        Assert.Empty(plan.Foregrounds);
        Assert.False(plan.SelectedRowCue);
    }

    [Fact]
    public void Selected_library_rows_are_always_marked_beyond_their_fill_in_light_and_dark()
    {
        Assert.True(AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, BlueDark()).SelectedRowCue);
        Assert.True(AccentContrastPlanner.Plan(AppearanceTheme.Light, false, BlueLight()).SelectedRowCue);
    }

    [Theory]
    // The maintainer's accent in the dark theme: the selected fill is 1.93:1 against the page.
    [InlineData("navy", false, true, 1.93)]
    [InlineData("navy", true, false, 15.77)]
    // Gold in the light theme: 1.91:1.
    [InlineData("gold", true, true, 1.91)]
    [InlineData("gold", false, false, 11.01)]
    [InlineData("blue", true, false, 5.01)]
    [InlineData("blue", false, false, 7.08)]
    public void Selected_list_items_get_a_weight_cue_only_where_their_accent_fill_is_under_3_to_1(
        string accent, bool light, bool expectedCue, double expectedRatio)
    {
        var colours = (accent, light) switch
        {
            ("navy", true) => NavyLight(),
            ("navy", false) => NavyDark(),
            ("gold", true) => GoldLight(),
            ("gold", false) => GoldDark(),
            ("blue", true) => BlueLight(),
            _ => BlueDark(),
        };

        var plan = AccentContrastPlanner.Plan(light ? AppearanceTheme.Light : AppearanceTheme.Dark, false, colours);

        Assert.Equal(expectedCue, plan.SelectedItemWeightCue);
        Assert.Equal(expectedRatio, Math.Round(plan.SelectedItemFillRatio!.Value, 2));
    }

    [Fact]
    public void An_unreadable_selection_fill_errs_toward_the_weight_cue()
    {
        var colours = NavyLight();
        colours.Remove(ThemeColor.AccentPrimary);

        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Light, false, colours);

        Assert.True(plan.SelectedItemWeightCue);
        Assert.Null(plan.SelectedItemFillRatio);
    }

    [Fact]
    public void The_secondary_tone_is_the_themes_own_tone_of_the_chosen_foreground()
    {
        var dark = AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, NavyDark());
        var light = AccentContrastPlanner.Plan(AppearanceTheme.Light, false, GoldLight());

        Assert.Equal(SrgbColor.Parse("#B3FFFFFF"), Role(dark, AccentForegroundRole.AccentButton).SecondaryForeground);
        Assert.Equal(SrgbColor.Parse("#80000000"), Role(light, AccentForegroundRole.AccentButton).SecondaryForeground);
    }

    [Fact]
    public void Glyphs_are_chosen_at_the_text_minimum_and_reported_against_the_non_text_one()
    {
        var plan = AccentContrastPlanner.Plan(AppearanceTheme.Dark, false, NavyDark());

        foreach (var role in new[] { AccentForegroundRole.CheckGlyph, AccentForegroundRole.RadioGlyph, AccentForegroundRole.SwitchKnob })
        {
            Assert.Equal(ForegroundKind.Glyph, Role(plan, role).Kind);
            Assert.Equal(WcagContrast.NonTextMinimum, Role(plan, role).ApplicableMinimum);
            Assert.Equal(WcagContrast.TextMinimum, Role(plan, role).Choice.Required);
        }

        Assert.Equal(WcagContrast.TextMinimum, Role(plan, AccentForegroundRole.AccentButton).ApplicableMinimum);
    }
}
